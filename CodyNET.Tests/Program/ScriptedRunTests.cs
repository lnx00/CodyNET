using CodyNET.Assembler;
using CodyNET.Core.Cody;
using CodyNET.Core.Roms;
using NUnit.Framework;

namespace CodyNET.Tests.Program;

/// <summary>
/// End-to-end tests for the IDE's Run [F5] modes against the real CodyBASIC ROM:
/// via BASIC (type LOAD 1,1, send the binary over UART1) and as a cartridge (load directly).
/// </summary>
public class ScriptedRunTests
{
    private const ushort MarkerAddress = 0x0300;

    private DirectoryInfo _dir = null!;

    [SetUp]
    public void SetUp() => _dir = Directory.CreateTempSubdirectory("cody_scripted_run_");

    [TearDown]
    public void TearDown() => _dir.Delete(recursive: true);

    /// <summary>Assembles a program (with LOAD/cartridge header) that stores <paramref name="marker"/> and stops.</summary>
    private FileInfo AssembleMarkerProgram(string name, byte marker)
    {
        var source = new FileInfo(Path.Combine(_dir.FullName, name + ".asm"));
        File.WriteAllText(source.FullName, $"""
                    * = $2FFC
                    .word start, end - 1    ; header: first and last address
            start   lda #${marker:X2}
                    sta ${MarkerAddress:X4}
                    stp                     ; stop the emulator so RunUntilFinish returns
            end
            """);
        return CodyAssembler.AssembleFile(source);
    }

    private static Cody CreateBootedCody()
    {
        var cody = new Cody(new CodySetupOptions { EnableScreen = false, EnableDebugger = false, FrequencyHz = -1 });
        cody.LoadImage(RomLoader.LoadEmbeddedCodyBasic(), 0xE000);
        return cody;
    }

    private static void RunUntilStp(Cody cody, string failureMessage)
    {
        var runner = Task.Run(cody.RunUntilFinish);
        var finished = runner.Wait(TimeSpan.FromSeconds(30));
        if (!finished)
            cody.Cpu.Run = false; // stop the background loop before failing
        Assert.That(finished, Is.True, failureMessage);
    }

    private static ushort ReadResetVector(Cody cody) =>
        (ushort)(cody.Memory.Read(Cpu.RESET_VECTOR) | (cody.Memory.Read(Cpu.RESET_VECTOR + 1) << 8));

    [Test]
    public void RunViaBasicLoad_LoadsAndStartsProgram()
    {
        var binary = AssembleMarkerProgram("marker", 0x42);
        var cody = CreateBootedCody();

        cody.RunViaBasicLoad(binary);
        RunUntilStp(cody, "Program never reached STP - LOAD 1,1 was not typed or the UART transfer failed.");

        Assert.That(cody.Memory.Read(MarkerAddress), Is.EqualTo(0x42));
    }

    [Test]
    public void RunViaBasicLoad_RejectsUntypeableCommand()
    {
        var cody = new Cody(new CodySetupOptions { EnableScreen = false, EnableDebugger = false });
        Assert.Throws<ArgumentException>(() => cody.RunViaBasicLoad(new FileInfo("x.bin"), "LOAD ~"));
    }

    [Test]
    public void RunAsCartridge_StartsProgramImmediatelyAndKeepsResetVector()
    {
        var binary = AssembleMarkerProgram("marker", 0x42);
        var cody = CreateBootedCody();
        var romResetVector = ReadResetVector(cody);

        cody.RunAsCartridge(binary);
        RunUntilStp(cody, "Cartridge program never reached STP.");

        Assert.That(cody.Memory.Read(MarkerAddress), Is.EqualTo(0x42));
        // The CPU reset zeroes the cycle counter, so this counts cycles since the run started:
        // lda + sta + stp, i.e. it started right at the program without booting BASIC first
        Assert.That(cody.Cpu.TotalCyclesExecuted, Is.LessThan(100));
        Assert.That(ReadResetVector(cody), Is.EqualTo(romResetVector), "Reset vector must still point into BASIC");
    }

    [Test]
    public void RunViaBasicLoad_StillWorksAfterCartridgeRun()
    {
        var cartridge = AssembleMarkerProgram("cart", 0x11);
        var viaBasic = AssembleMarkerProgram("basic", 0x22);
        var cody = CreateBootedCody();

        cody.RunAsCartridge(cartridge);
        RunUntilStp(cody, "Cartridge program never reached STP.");
        Assert.That(cody.Memory.Read(MarkerAddress), Is.EqualTo(0x11));

        cody.RunViaBasicLoad(viaBasic);
        RunUntilStp(cody, "BASIC run after a cartridge run never reached STP - BASIC did not boot.");
        Assert.That(cody.Memory.Read(MarkerAddress), Is.EqualTo(0x22));
    }

    [Test]
    public void RunAsCartridge_RejectsFileWithoutValidHeader()
    {
        var file = new FileInfo(Path.Combine(_dir.FullName, "broken.bin"));
        File.WriteAllBytes(file.FullName, [0x00, 0x30]); // header needs 4 bytes
        var cody = new Cody(new CodySetupOptions { EnableScreen = false, EnableDebugger = false });

        Assert.Throws<InvalidDataException>(() => cody.RunAsCartridge(file));
    }
}
