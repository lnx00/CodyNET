using CodyNET.Assembler;
using CodyNET.Disassembler;
using NUnit.Framework;

namespace CodyNET.Tests.AssemblerTests;

/// <summary>
/// These tests require 64tass assembler to be installed and accessible via the path specified in TassAssembler.cs
/// </summary>
public class AssemblerTests
{
    [Test]
    public void TestAssembler()
    {
        var bytes = CodyAssembler.AssembleFileToBytes(FileUtils.GetTestDataFile("minimal.asm"));
        var expectedBytes = File.ReadAllBytes(FileUtils.GetTestDataPath("minimal.bin"));
        Assert.AreEqual(bytes, expectedBytes);
    }
    
    [Test]
    public void TestFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => CodyAssembler.AssembleFileToBytes(FileUtils.GetTestDataFile("not-existing.asm")));
    }

    [Test]
    public void TestInvalidOpcode()
    {
        var ex = Assert.Throws<AssemblerException>(() => CodyAssembler.AssembleFileToBytes(FileUtils.GetTestDataFile("invalidOpcode.asm")));
        Assert.That(ex!.Diagnostics, Has.Count.GreaterThan(0));
        Assert.That(ex.Diagnostics[0].Line, Is.EqualTo(1));
        Assert.That(ex.Diagnostics[0].Severity, Is.EqualTo(AssemblerDiagnosticSeverity.Error));
        Assert.That(ex.Diagnostics[0].File, Does.EndWith("invalidOpcode._pre.asm"));
    }

    [Test]
    public void TestParseTassOutput()
    {
        const string output =
            "X:\\repos\\proj\\editor._pre.asm:12:5: error: general syntax\r\n" +
            " lda ##1\r\n" +
            "     ^\r\n" +
            "/home/user/inc/macros.asm:3:1: warning: unused symbol 'foo'\n" +
            "/home/user/inc/macros.asm:1:1: note: original definition\n" +
            "editor._pre.asm:7:2: fatal error: can't open file\n" +
            "error: some unlocated message\n";

        var diags = AssemblerDiagnostic.ParseTassOutput(output);

        Assert.That(diags, Is.EqualTo(new[]
        {
            new AssemblerDiagnostic("X:\\repos\\proj\\editor._pre.asm", 12, 5, AssemblerDiagnosticSeverity.Error, "general syntax"),
            new AssemblerDiagnostic("/home/user/inc/macros.asm", 3, 1, AssemblerDiagnosticSeverity.Warning, "unused symbol 'foo'"),
            new AssemblerDiagnostic("/home/user/inc/macros.asm", 1, 1, AssemblerDiagnosticSeverity.Note, "original definition"),
            new AssemblerDiagnostic("editor._pre.asm", 7, 2, AssemblerDiagnosticSeverity.Error, "can't open file"),
        }));
    }

    [Test]
    public void TestParseTassOutputEmpty()
    {
        Assert.That(AssemblerDiagnostic.ParseTassOutput(null), Is.Empty);
        Assert.That(AssemblerDiagnostic.ParseTassOutput(""), Is.Empty);
    }

    [Test]
    public async Task TestAssembleWithMapAsync()
    {
        var input = FileUtils.GetTestDataFile("programs/assembly/TicTacToe.asm");
        var output = new FileInfo(Path.Combine(Path.GetTempPath(), $"cody_{Guid.NewGuid():N}.bin"));
        try
        {
            var (result, addressToPreLine) = await CodyAssembler.AssembleFileWithMapAsync(input, input, output);
            Assert.That(result.Length, Is.GreaterThan(0));
            // TicTacToe sets an origin, so the listing yields address -> line entries.
            Assert.That(addressToPreLine, Is.Not.Empty);
        }
        finally
        {
            output.Delete();
        }
    }

    [Test]
    public async Task TestInvalidOpcodeAsync()
    {
        var input = FileUtils.GetTestDataFile("invalidOpcode.asm");
        var ex = await Assert.ThrowsAsync<AssemblerException>(() => CodyAssembler.AssembleFileWithMapAsync(input, input));
        Assert.That(ex!.Diagnostics.Select(d => d.Line), Does.Contain(1));
    }

    [Test]
    public void TestUnmappedArea()
    {
        var bytes = CodyAssembler.AssembleFileToBytes(FileUtils.GetTestDataFile("debugTesting/unmappedArea.asm"));
        Assert.True(true);
    }

    [Test]
    public void TestDisassemblerSingle()
    {
        var result = CodyDisassembler.Disassemble([0x69, 0x53, 0x00]);
        result = result.Replace("\r\n", "\n").TrimEnd();
        var expected = "ADC #$53\nBRK";
        Assert.AreEqual(expected, result);
    }
    
    [Test]
    public void TestDisassemblerMinimal()
    {
        var result = CodyDisassembler.Disassemble([0xA9, 0x01 , 0x8D , 0x00 , 0x02 , 0xA9 , 0x05 , 0x8D , 0x01 , 0x02 , 0xA9 , 0x08 , 0x8D , 0x02 , 0x02]);
        result = result.Replace("\r\n", "\n").TrimEnd();
        var expected = "LDA #$01\nSTA $0200\nLDA #$05\nSTA $0201\nLDA #$08\nSTA $0202";
        Assert.AreEqual(expected, result);
    }

    [Test]
    public void TestDisassemblerRelativeBranchRoundtrip()
    {
        byte[] original = [0xD0, 0xFE];
        string disassembly = CodyDisassembler.Disassemble(original, 0xE000);
        var reassembled = CodyAssembler.AssembleTextToBytes(disassembly);

        Assert.That(reassembled, Is.EqualTo(original), disassembly);
    }

    [Test]
    [Explicit("Dev Test")]
    public void TestAssembleDisassembleRoundtripCodybasic()
    {
        var folderPath = "C:\\Users\\Konsi\\Documents\\CodyNETSecond\\CodyNET.Tests\\testdata\\programs\\assembly";
        var name = "TicTacToe";
        var asm = File.ReadAllText(Path.Combine(folderPath, $"{name}.asm"));
        var assembled = CodyAssembler.AssembleTextToBytes(asm);
        // Write to file
        var assembledPath = FileUtils.GetTestDataPath(Path.Combine(folderPath, $"{name}_assembled.bin"));
        File.WriteAllBytes(assembledPath, assembled);
        var disassembled  = CodyDisassembler.Disassemble(assembled);
        var disassembledPath = Path.Combine(folderPath, $"{name}_disassembled.asm");
        File.WriteAllText(disassembledPath, disassembled);
        Console.WriteLine($"Files written to {assembledPath} and {disassembledPath}");
    }

    [Test]
    [Explicit("Linear disassembly of real binaries with data sections is inherently unreliable for round-tripping")]
    public void TestDisassemblerRoundtrip()
    {
        var original = File.ReadAllBytes(FileUtils.GetTestDataPath("programs/codybasic.bin"));
        string disassembly = CodyDisassembler.Disassemble(original, 0xE000);
        var reassembled = CodyAssembler.AssembleTextToBytes(disassembly);

        Assert.That(reassembled, Is.EqualTo(original));
    }

    [Explicit("Dev Test")]
    [Test]
    public void TestPreprocessor()
    {
        var code = "LDA #1\nSTA #1\n\n";
        var x = CodyPreprocessor.Preprocess(code);
        
        List<string> lines = code.Split('\n').ToList();
        var y = CodyPreprocessor.Preprocess(lines);
        
        Assert.That(x , Is.EqualTo(y));
    }
    
    [Explicit("Dev Test")]
    [Test]
    public void TestDisassembler()
    {
        var file1 = "C:\\Users\\Konsi\\Documents\\CodyNET\\CodyNET.Tests\\testdata\\programs\\assembly\\TicTacToe_assembled.bin";
        var file2 = "C:\\Users\\Konsi\\Documents\\CodyNET\\CodyNET.Tests\\testdata\\programs\\assembly\\TicTacToe_breakpoint.bin";
        CodyDisassembler.DisassembleFile(new FileInfo(file1), new FileInfo(file1.Replace(".bin", ".asm")));
        CodyDisassembler.DisassembleFile(new FileInfo(file2), new FileInfo(file2.Replace(".bin", ".asm")));
    }
}
