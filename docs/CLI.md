# CLI Reference

CodyNET provides a command-line interface for running, assembling, and disassembling programs for the Cody Computer.

When launched without arguments (or with `-i` / `--interactive`), an interactive shell starts. Enter `help` to list commands, `exit` or `quit` to leave.

## Global Options

| Option | Description |
|---|---|
| `--log-level <level>` | Console log level: `Error`, `Warn`, `Info`, `Debug`, `Verbose` (default: `Info`) |
| `--verbose`, `-v` | Shorthand for `--log-level Verbose` |

## Commands

### boot

Boot the emulator with the built-in CodyBASIC.

```
boot [OPTIONS]
```

| Option | Description |
|---|---|
| `--debug`, `-d` | Enable the integrated debugger |
| `--uart1-source <path>` | File to fill UART1 receive buffer |
| `--uart2-source <path>` | File to fill UART2 receive buffer |
| `--fix-newlines` | Normalize newlines in UART text input (CRLF to LF) |
| `--physical-keyboard` | Use physical Cody keyboard mapping (ignores host layout) |
| `--clock <frequency>` | Target CPU clock rate (default: `1MHz`) |
| `--fast` | Run as fast as possible (ignores `--clock`) |
| `--headless` | Run without video output |
| `--start-paused` | Start the emulator in paused state |

**Examples:**

```
boot --debug --clock 2MHz
boot --uart1-source program.bas
```

### ide

Boot the emulator with the debugger and code editor enabled. Shortcut for `boot --debug`.

```
ide [OPTIONS]
```

Accepts the same options as `boot` (except `--debug`, which is implied).

**Edit-and-run workflow:** load your `.asm` once via **Load** in the Assembly Editor. The editor
reloads it automatically whenever you save it in an external editor (e.g. Sublime Text), keeping
breakpoints, caret and scroll position. Press **F5** (or the **▶ Run** button) to assemble and start
the program. The arrow next to the Run button selects how it is started:

| Run mode | What happens | Time to start (1 MHz) |
|---|---|---|
| **BASIC** (default) | Reset the CPU, type `LOAD 1,1` into CodyBASIC, send the binary over UART1, the same steps as doing it by hand | ~1.8 s |
| **Cartridge** | Load the binary (start/end header) straight into memory and start it, skipping BASIC, like `run --as-cartridge` | immediate |

Both modes expect the binary to begin with the 4-byte start/end address header. In cartridge mode
BASIC never runs, so nothing it normally sets up (screen, interrupt handler, keyboard scanning) is
initialized, and RAM keeps whatever the previous run left there. Unlike `run --as-cartridge`, the
reset vector is left untouched, so **CPU Reset** and BASIC mode still boot into CodyBASIC.

If assembling fails, nothing is reset and the problems are listed in the **Errors** tab.

**Examples:**

```
ide
ide --clock 2MHz --start-paused
```

### run

Run a binary file with the emulator.

```
run <file> [OPTIONS]
```

| Argument | Description |
|---|---|
| `file` | Binary file path (required) |

| Option | Description |
|---|---|
| `--as-cartridge` | Treat file as cartridge image (expects cartridge header) |
| `--load-address`, `-l <address>` | Load address for raw binaries (default: `0xE000`) |
| `--reset-vector <address>` | Override Reset Vector (`0xFFFC`) |
| `--irq-vector <address>` | Override IRQ Vector (`0xFFFE`) |
| `--nmi-vector <address>` | Override NMI Vector (`0xFFFA`) |
| `--debug`, `-d` | Enable the integrated debugger |
| `--uart1-source <path>` | File to fill UART1 receive buffer |
| `--uart2-source <path>` | File to fill UART2 receive buffer |
| `--fix-newlines` | Normalize newlines in UART text input |
| `--physical-keyboard` | Use physical Cody keyboard mapping |
| `--clock <frequency>` | Target CPU clock rate (default: `1MHz`) |
| `--fast` | Run as fast as possible |
| `--headless` | Run without video output |
| `--start-paused` | Start the emulator in paused state |

**Examples:**

```
run program.bin --load-address 0x8000 --debug
run cartridge.bin --as-cartridge --fast
```

### assemble

Assemble a source file into a binary.

```
assemble <file> [OPTIONS]
```

| Argument | Description |
|---|---|
| `file` | Assembly source file (required) |

| Option | Description |
|---|---|
| `--output`, `-o <path>` | Output binary path (default: `<input>.bin`) |

**Examples:**

```
assemble program.asm
assemble program.asm --output compiled.bin
```

### disassemble

Disassemble a binary into assembly.

```
disassemble <file> [OPTIONS]
```

| Argument | Description |
|---|---|
| `file` | Binary file (required) |

| Option | Description |
|---|---|
| `--as-cartridge` | Treat file as cartridge image |
| `--load-address`, `-l <address>` | Base address for raw binaries (default: `0xE000`) |
| `--output`, `-o <path>` | Output assembly path (default: `<input>.asm`) |

**Examples:**

```
disassemble binary.bin --load-address 0x8000
disassemble cartridge.bin --as-cartridge --output program.asm
```

### list

List `.asm` and `.bin` files in the current directory.

**Aliases:** `ls`, `dir`

```
list [OPTIONS]
```

| Option | Description |
|---|---|
| `--recursive`, `-r` | Include file counts from subdirectories |

## Address Formats

All address parameters accept the following formats:

| Format | Example |
|---|---|
| Decimal | `32768` |
| Hex with `0x` prefix | `0x8000` |
| Hex with `$` prefix | `$8000` |
| Bare hex | `E000` |

## Clock Formats

The `--clock` parameter accepts:

| Format | Example |
|---|---|
| Raw Hz | `1000000` |
| MHz | `1MHz`, `2MHz` |
| kHz | `500kHz` |
| Hz | `1000Hz` |

Default: `1MHz`
