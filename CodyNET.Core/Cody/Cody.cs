using System.Diagnostics;
using CodyNET.Assembler;
using CodyNET.Common;
using CodyNET.Common.Video;
using CodyNET.Common.Utils;
using CodyNET.Core.Devices;
using CodyNET.Core.Interfaces;
using CodyNET.Core.Roms;
using Debugger = CodyNET.Core.Devices.Debugger;
using System.Reflection.Metadata.Ecma335;

namespace CodyNET.Core.Cody;

// Options when creating a new cody, filled with default values for boot command
public sealed record CodySetupOptions
{
    public static CodySetupOptions Default => new();

    public long FrequencyHz { get; init; } = 1_000_000; // 1 MHz, -1 = as fast as possible
    public bool EnableDebugger { get; init; } = true;
    public bool PhysicalKeyboard { get; init; } = true; // Use physical keyboard input (instead of logical)
    public bool EnableScreen { get; init; } = true;
    public bool EnableProfiler { get; init; } = true;
    public bool StartPaused { get; init; } = false;
    /// <summary>
    /// When set, runs the profiler in test mode: waits for warmup, collects N samples, then exits.
    /// </summary>
    public int? PerfTestSamples { get; init; }
    /// <summary>
    /// Warmup duration before collecting performance samples (default 5s).
    /// </summary>
    public TimeSpan PerfWarmup { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed record CodyLoadOptions
{
    public FileInfo? File { get; init; }
    public bool AsCartridge { get; init; }
    public ushort LoadAddress { get; init; } = 0xE000;
    public bool AutoSetResetVector { get; set; } = false; // Legacy, kept for Boot() — prefer automatic detection
    public ushort? ResetVectorOverride { get; init; }
    public ushort? IrqVectorOverride { get; init; }
    public ushort? NmiVectorOverride { get; init; }
    public FileInfo? Uart1Source { get; init; }
    public FileInfo? Uart2Source { get; init; }
    public bool FixUartNewlines { get; set; }
}

public class Cody
{
    public Cpu Cpu;
    public Memory Memory => Cpu.Memory;
    public Debugger? Debugger { get; private set; }
    public IVideoDevice? VID { get; private set; }
    public IAudioDevice? SID { get; private set; }
    public VersatileInterfaceAdapter? VIA { get; init; }
    public Keyboard? Keyboard { get; private set; }
    public UartDevice Uart1 { get; private set; }
    public UartDevice Uart2 { get; private set; }
    public IScreenDevice? Screen { get; init; }
    public Profiler? Profiler;

    private int _allowedSteps = -1; // -1 = Normal running, 0 = Paused, 1 = Single Step Mode
    private readonly ManualResetEventSlim _resumeEvent = new(initialState: true); // true = open (running)
    private InputSequence? _inputSequence; // Scripted input, processed on the emulator thread

    // Measured: CodyBASIC enables its jiffy timer (and is ready for keyboard input) 434,401 cycles
    // after reset, on both cold and warm resets. Wait a bit longer to be safe.
    private const long BasicBootCycles = 700_000;
    // BASIC scans the keyboard once per jiffy (~18,250 cycles) and only accepts a key that is seen
    // on two consecutive scans and differs from the previous key. Hold and release for >3 jiffies each.
    private const long KeyHoldCycles = 60_000;
    private const long KeyReleaseCycles = 60_000;

    public long FrequencyHz
    {
        get => Cpu.CyclesPerSecond;
        set
        {
            Cpu.CyclesPerSecond = value;
            SID?.SetClockFrequency(value);
        }
    }

    public bool FastMode
    {
        get => Cpu.CyclesPerSecond == -1;
        set => FrequencyHz = value ? -1 : 1_000_000;
    }

    // TODO: Cody shouldn't need setup options, as Factory create devices based on options and passes them in. Refactor to remove this dependency.
    public Cody(CodySetupOptions options, IVideoDevice? videoDevice = null, IScreenDevice? screen = null, IAudioDevice? audioDevice = null)
    {
        if (options.StartPaused)
        {
            Pause();
        }
        VID = videoDevice;
        SID = audioDevice;
        Screen = screen;

        // 1. Set up CPU (and memory)
        // CPU also initializes memory
        Cpu = new Cpu
        {
            CyclesPerSecond = options.FrequencyHz
        };

        // 2. Set up devices
        if (options.EnableProfiler)
        {
            Profiler = new Profiler(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                options.PerfTestSamples,
                options.PerfTestSamples.HasValue ? options.PerfWarmup : null);
        }
        if (options.EnableDebugger)
        {
            // watch addresses 0xA000 - 0xA3E7 (text screen)
            /*var addresses = new List<ushort>();
            for (ushort addr = 0xA000; addr <= 0xA3E7; addr++)
                addresses.Add(addr);*/
            Debugger = new Debugger(Cpu);
            Memory.RegisterDevice(Debugger);
            Memory.RegisterTap(Debugger);
        }

        // Input Setup (VIA + Keyboard)
        KeyState keyState = new KeyState();
        VIA = new VersatileInterfaceAdapter(keyState);
        Memory.RegisterDevice(VIA);
        Keyboard = new Keyboard(keyState)
        {
            UsePhysicalKeyboard = options.PhysicalKeyboard
        };

        // Uart Setup: Uart1 is prop plug, Uart2 is extension port (cartridge)
        Uart1 = new UartDevice(0xD480);
        Memory.RegisterDevice(Uart1);
        Uart2 = new UartDevice(0xD4A0);
        Memory.RegisterDevice(Uart2);

        if (VID != null)
        {
            Memory.RegisterDevice(VID);
        }
        if (SID != null)
        {
            Memory.RegisterDevice(SID);
        }

        Log.Info("Cody Setup complete");
    }

    public void Reset()
    {
        Cpu.Reset();
    }

    /// <summary>
    /// Reproduces the manual "reset, type LOAD 1,1, send binary over UART" workflow:
    /// resets the CPU into CodyBASIC, types <paramref name="command"/> followed by RETURN on the
    /// emulated keyboard once BASIC is ready, then queues <paramref name="binaryFile"/> on UART1.
    /// BASIC loads the binary and jumps to its start address. Thread-safe: everything runs on the
    /// emulator thread, timed in CPU cycles. Calling it again cancels a sequence still in progress.
    /// </summary>
    public void RunViaBasicLoad(FileInfo binaryFile, string command = "LOAD 1,1")
    {
        var keyboard = Keyboard ?? throw new InvalidOperationException("No keyboard device available.");
        var unmapped = command.Where(c => !keyboard.CanType(c)).Distinct().ToArray();
        if (unmapped.Length > 0)
            throw new ArgumentException($"Cannot type character(s) '{new string(unmapped)}' on the Cody keyboard.", nameof(command));

        var sequence = new InputSequence().Then(0, () =>
        {
            keyboard.ReleaseAllKeys();
            Cpu.Reset();
            Log.Info("Scripted run: CPU reset, typing '{Command}' once BASIC is ready", command);
        });

        var delay = BasicBootCycles;
        foreach (var character in command + "\n")
        {
            sequence.Then(delay, () => keyboard.TypeCharacter(character, true));
            sequence.Then(KeyHoldCycles, () => keyboard.TypeCharacter(character, false));
            delay = KeyReleaseCycles;
        }

        sequence.Then(0, () =>
        {
            LoadUartSource(binaryFile, 1);
            Log.Info("Scripted run: sent {File} over UART1", binaryFile.FullName);
        });

        Volatile.Write(ref _inputSequence, sequence);
        Resume(); // The sequence only advances while the CPU runs
    }

    // First instructions of CodyBASIC's CARTCHECK routine (LDA #$0D / STA VIA_PCR / LDA VIA_IORA /
    // LDA #$0F / STA VIA_PCR). Located by content rather than a fixed address, so a ROM rebuild
    // that moves the routine still works.
    private static readonly byte[] CartCheckSignature =
        [0xA9, 0x0D, 0x8D, 0x0C, 0x9F, 0xAD, 0x01, 0x9F, 0xA9, 0x0F, 0x8D, 0x0C, 0x9F];
    // Measured: the ROM reaches CARTCHECK 206,607 cycles after reset (after INIT has run).
    private const long CartCheckTimeoutCycles = 1_000_000;

    /// <summary>
    /// Runs <paramref name="cartridgeFile"/> the way the Cody boots a cartridge: the CPU is reset
    /// into the ROM, which runs INIT (copies the character set, resets the video registers, clears
    /// the screen, sets up the VIA timer). When the ROM checks for a cartridge (CARTCHECK), the
    /// image (with its start/end header) is loaded and started instead of BASIC, just like the
    /// ROM's own cartridge path. Takes ~0.2 s of emulated time at 1 MHz.
    /// The reset vector is not modified, so CPU reset and <see cref="RunViaBasicLoad"/> still boot
    /// into BASIC. If the ROM has no recognizable CARTCHECK, the image is started directly (without
    /// INIT). Thread-safe like RunViaBasicLoad.
    /// </summary>
    /// <exception cref="InvalidDataException">The file has no valid cartridge header.</exception>
    public void RunAsCartridge(FileInfo cartridgeFile)
    {
        CheckFilePath(cartridgeFile);
        // Parse on the caller's thread so a bad header is reported to the caller
        var (image, start) = ParseImage(File.ReadAllBytes(cartridgeFile.FullName), new CodyLoadOptions { AsCartridge = true });
        var end = start + image.Length - 1;

        // A cartridge that supplies its own reset vector starts there, like ApplyVectors does
        ushort entry = start;
        if (start <= Cpu.RESET_VECTOR && end >= Cpu.RESET_VECTOR + 1)
        {
            var offset = Cpu.RESET_VECTOR - start;
            entry = (ushort)(image[offset] | (image[offset + 1] << 8));
        }

        if (end >= 0xE000)
            Log.Warn("Cartridge {File} overlaps the CodyBASIC ROM area ($E000-$FFFF); BASIC may not boot until the emulator is restarted", cartridgeFile.Name);

        var keyboard = Keyboard;
        void StartDirectly(string reason)
        {
            Log.Warn("Cartridge run: {Reason}; starting {File} directly without ROM initialization", reason, cartridgeFile.Name);
            LoadImage(image, start);
            Cpu.ResetToAddress(entry);
        }

        InputSequence sequence;
        if (!Memory.TryFindInRom(CartCheckSignature, out var cartCheck))
        {
            sequence = new InputSequence().Then(0, () =>
            {
                keyboard?.ReleaseAllKeys();
                StartDirectly("CodyBASIC's cartridge check was not found in ROM");
            });
        }
        else
        {
            sequence = new InputSequence()
                .Then(0, () =>
                {
                    keyboard?.ReleaseAllKeys();
                    Cpu.Reset(); // Boot the ROM: MAIN -> INIT -> CARTCHECK
                })
                .ThenWhen(() => Cpu.PC == cartCheck, CartCheckTimeoutCycles,
                    action: () =>
                    {
                        // Act as if a cartridge is plugged in: load it and jump to it, like the
                        // ROM's LOADBIN does. Drop the return address of MAIN's JSR CARTCHECK.
                        LoadImage(image, start);
                        Cpu.S = (byte)(Cpu.S + 2);
                        Cpu.PC = entry;
                        Log.Info("Cartridge run: ROM initialized, loaded {File} at ${Start:X4}-${End:X4}, starting at ${Entry:X4}",
                            cartridgeFile.Name, start, end, entry);
                    },
                    onTimeout: () => StartDirectly("the ROM did not reach its cartridge check"));
        }

        Volatile.Write(ref _inputSequence, sequence);
        Resume();
    }

    public StepResult Step()
    {
        return Cpu.Step();
    }

    public void Pause()
    {
        _allowedSteps = 0;
        _resumeEvent.Reset();
    }

    public void Resume()
    {
        _allowedSteps = -1;
        _resumeEvent.Set();
    }

    public void SetAllowedSteps(int steps)
    {
        _allowedSteps = steps;
        if (steps != 0)
            _resumeEvent.Set();
        else
            _resumeEvent.Reset();
    }

    private Stopwatch screenStopwatch = new();
    public void RunUntilFinish()
    {
        screenStopwatch.Start();
        StepResult result;
        //Log.Level = LogLevel.Trace;
        do
        {
            if (Debugger is not null && Debugger.IsAtBreakpoint())
            {
                Log.Info("Execution paused by debugger on PC={PC:X4}", Cpu.PC);
                Pause();
                //break;
            }
            _resumeEvent.Wait(); // Block here when paused - zero latency on resume

            var sequence = Volatile.Read(ref _inputSequence);
            if (sequence != null && sequence.Update(Cpu))
                Interlocked.CompareExchange(ref _inputSequence, null, sequence); // Keep a newer sequence

            result = Cpu.Step();
            if (_allowedSteps > 0) // After step, reduce allowed steps, if limited
            {
                _allowedSteps--;
                if (_allowedSteps == 0)
                    _resumeEvent.Reset(); // Single-step done -> pause again
            }
            Profiler?.SampleCpu(Cpu.TotalCyclesExecuted, Cpu.CyclesPerSecond);
            // TEMP

            // 60 times per second
            if (screenStopwatch.Elapsed >= TimeSpan.FromSeconds(1.0 / 60))
            {
                screenStopwatch.Restart();
                if (VID != null)
                {
                    var frame = VID.RenderTextFrame(Cpu.Memory);
                    Screen?.RenderFrame(frame);
                    Profiler?.FrameRendered();
                }
            }
        } while (result != StepResult.Stopped && Profiler?.TestComplete != true);
    }

    /// <summary>
    /// Performance testing helper. Returns cycles spent in one step.
    /// </summary>
    public long SingleStep()
    {
        Step();
        long cycles = Cpu.TotalCyclesExecuted;
        Cpu.TotalCyclesExecuted = 0;
        return cycles;
    }

    private void CheckFilePath(FileInfo? file)
    {
        if (file is null)
            throw new ArgumentNullException(nameof(file), "File path must be provided.");
        if (!file.Exists)
            throw new FileNotFoundException($"File not found: {file.FullName}");
    }

    /// <summary>
    /// Loads the given binary file as uart source for the specified UART (1 = prop plug, 2 = cartridge)
    /// </summary>
    /// <param name="file"></param>
    /// <param name="uartNumber"></param>
    /// <param name="fixNewlines"></param>
    public void LoadUartSource(FileInfo file, int uartNumber = 1, bool fixNewlines = false)
    {
        if (file.Extension == ".bas")
        {
            fixNewlines = true; // Automatically set to true when reading basic file. TODO: Find better solution
            Log.Info("BASIC file being set as UART source, enabling fix-newlines parameter");
        }
        var source = UartSource.FromFile(file, fixNewlines);

        if (uartNumber == 1)
            Uart1.Source = source;
        else if (uartNumber == 2)
            Uart2.Source = source;
        else
            throw new ArgumentException("Invalid UART number. Must be 1 or 2.", nameof(uartNumber));
    }

    public void LoadBinaryFile(CodyLoadOptions options)
    {
        CheckFilePath(options.File);

        var bytes = File.ReadAllBytes(options.File!.FullName);
        LoadBinary(bytes, options);
    }

    public void LoadBinary(byte[] bytes, CodyLoadOptions? options = null)
    {
        if (bytes is null) throw new ArgumentNullException(nameof(bytes));

        options ??= new CodyLoadOptions();

        if (options.Uart1Source != null) // TODO: Check if this is the right place to load UART source
            LoadUartSource(options.Uart1Source, 1, options.FixUartNewlines);
        if (options.Uart2Source != null)
            LoadUartSource(options.Uart2Source, 2, options.FixUartNewlines);
        // Uart2 load here
        var (image, loadAddress) = ParseImage(bytes, options);
        LoadImage(image, loadAddress);

        ushort lastWrittenAddress = (ushort)(loadAddress + image.Length - 1);
        ApplyVectors(loadAddress, lastWrittenAddress, options);
    }

    public void LoadAssemblyFile(CodyLoadOptions loadOptions)
    {
        CheckFilePath(loadOptions.File);

        var program = CodyAssembler.AssembleFileToBytes(loadOptions.File!);
        LoadBinary(program, loadOptions);
    }

    public void RunBinaryFile(CodyLoadOptions loadOptions)
    {
        CheckFilePath(loadOptions.File);
        Log.Debug("Running binary file: '{fileName}'", loadOptions.File!.FullName);

        var bytes = File.ReadAllBytes(loadOptions.File!.FullName);
        RunBinary(bytes, loadOptions);
    }

    public void RunBinary(byte[] bytes, CodyLoadOptions loadOptions)
    {
        LoadBinary(bytes, loadOptions);
        Reset();
        RunUntilFinish();
    }

    public void RunAssemblyFile(CodyLoadOptions loadOptions)
    {
        CheckFilePath(loadOptions.File);

        LoadAssemblyFile(loadOptions);
        Reset();
        RunUntilFinish();
    }

    /// <summary>
    /// Boots the machine by loading the built-in CodyBASIC ROM.
    /// </summary>
    public void Boot(CodyLoadOptions? options = null)
    {
        Log.Debug("Booting Cody with CodyBASIC ROM.");
        try
        {
            options ??= new CodyLoadOptions();
            options = options with
            {
                LoadAddress = 0xE000,
                AsCartridge = false,
                AutoSetResetVector = false,
            };
            var codyBasicRom = RomLoader.LoadEmbeddedCodyBasic();
            RunBinary(codyBasicRom, options);
        }
        catch (FileNotFoundException e)
        {
            Log.Error("Bundled CodyBASIC ROM not found as embedded resource. If this error keeps happening, please run and specify file manually instead. {message}", e.Message);
        }
    }

    public void LoadImage(byte[] data, ushort loadAddress)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));

        Memory.LoadBytes(data, loadAddress);
    }

    private static (byte[] image, ushort loadAddress) ParseImage(byte[] bytes, CodyLoadOptions options)
    {
        if (!options.AsCartridge)
            return (bytes, options.LoadAddress);

        if (bytes.Length < 4)
            throw new InvalidDataException("Cartridge header must be at least 4 bytes.");

        ushort start = (ushort)(bytes[0] | (bytes[1] << 8));
        ushort end = (ushort)(bytes[2] | (bytes[3] << 8));

        int len = end - start + 1;
        if (len <= 0)
            throw new InvalidDataException("Cartridge header invalid (end < start).");

        if (bytes.Length < 4 + len)
            throw new InvalidDataException("Cartridge image truncated (data shorter than header claims).");

        var image = new byte[len];
        Buffer.BlockCopy(bytes, 4, image, 0, len);
        return (image, start);
    }

    private void ApplyVectors(ushort loadAddress, ushort lastWrittenAddress, CodyLoadOptions options)
    {
        bool dataCoversResetVector = loadAddress <= Cpu.RESET_VECTOR && lastWrittenAddress >= Cpu.RESET_VECTOR + 1;

        if (options.ResetVectorOverride.HasValue)
        {
            Log.Info("Setting reset vector to 0x{resetVector:X4} (explicit override)", options.ResetVectorOverride.Value);
            SetResetVector(options.ResetVectorOverride.Value);
        }
        else if (!dataCoversResetVector)
        {
            // Loaded data does not cover the reset vector location — fall back to load address
            // so the CPU jumps directly to the loaded program on startup (matches original emulator behavior)
            Log.Info("Using load address 0x{loadAddress:X4} as reset vector, because the reset vector location was not written to", loadAddress);
            SetResetVector(loadAddress);
        }
        else
        {
            ushort existingVector = (ushort)(Memory.Read(Cpu.RESET_VECTOR) | (Memory.Read(Cpu.RESET_VECTOR + 1) << 8));
            Log.Info("Using reset vector 0x{resetVector:X4} from loaded data", existingVector);
        }

        if (options.IrqVectorOverride.HasValue)
            SetIrqVector(options.IrqVectorOverride.Value);
        if (options.NmiVectorOverride.HasValue)
            SetNmiVector(options.NmiVectorOverride.Value);
    }

    private void SetResetVector(ushort address)
    {
        Memory.ForceWrite(0xFFFC, (byte)(address & 0xFF));
        Memory.ForceWrite(0xFFFD, (byte)(address >> 8));
    }

    private void SetIrqVector(ushort address)
    {
        Memory.ForceWrite(0xFFFE, (byte)(address & 0xFF));
        Memory.ForceWrite(0xFFFF, (byte)(address >> 8));
    }

    private void SetNmiVector(ushort address)
    {
        Memory.ForceWrite(0xFFFA, (byte)(address & 0xFF));
        Memory.ForceWrite(0xFFFB, (byte)(address >> 8));
    }

    public CodyStatusSnapshot GetStatusSnapshot()
    {
        /*var runStatus = RunStatus.Running;
        if (!Cpu.Run)
            runStatus = RunStatus.Paused;*/
        var runStatus = RunStatus.Running;
        if (_allowedSteps == 0)
            runStatus = RunStatus.Paused;
        return new CodyStatusSnapshot
        {
            RunStatus = runStatus,
            ProfilerSnapshot = Profiler?.LastSnapshot
        };
    }
}
