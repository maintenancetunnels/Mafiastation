using NUnit.Framework;

namespace Mafiastation.AiPilotLab.Tests;

[TestFixture]
public sealed class CommandLineArgumentsTests
{
    [Test]
    public void EmptyArgumentsSelectHelp()
    {
        Assert.That(CommandLineArguments.Parse(Array.Empty<string>()).Command, Is.EqualTo("help"));
    }

    [Test]
    public void RepeatedOptionsArePreservedAndLastValueWins()
    {
        var parsed = CommandLineArguments.Parse(new[]
        {
            "replay",
            "--map", "one=pipe-1",
            "--map", "two=pipe-2",
            "--allow-speech",
        });

        Assert.That(parsed.GetMany("map"), Is.EqualTo(new[] { "one=pipe-1", "two=pipe-2" }));
        Assert.That(parsed.Get("map"), Is.EqualTo("two=pipe-2"));
        Assert.That(parsed.GetFlag("allow-speech"), Is.True);
    }

    [Test]
    public void RejectsPositionalArgumentsAfterCommand()
    {
        Assert.That(
            () => CommandLineArguments.Parse(new[] { "send", "unexpected" }),
            Throws.ArgumentException.With.Message.Contains("Unexpected argument"));
    }
}
