using CodyNET.Core;
using CodyNET.Host;
using NUnit.Framework;

namespace CodyNET.Tests.Program;

public class CliTests
{
    [Explicit]
    [Test]
    public void TestCli()
    {
        string input = "assemble test.asm";
        var result = Cli.BuildRootCommand().Parse(input);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public void TestIdeCommandParses()
    {
        var result = Cli.BuildRootCommand().Parse("ide --clock 2MHz --start-paused");
        Assert.That(result.Errors, Is.Empty);
        Assert.That(result.CommandResult.Command.Name, Is.EqualTo("ide"));
    }

    public void TestBootDefaults()
    {
        string input = "boot";
        // TODO: How to test executed logic?
         var result = Cli.BuildRootCommand().Parse(input);
         Assert.That(result.Errors, Is.Empty);
    }
}