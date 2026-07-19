using System.Text.Json;
using NUnit.Framework;

namespace Mafiastation.AiPilotLab.Tests;

[TestFixture]
public sealed class PilotJsonTests
{
    [Test]
    public void ReadsNestedPathAndComparesEquivalentNumbers()
    {
        using var document = JsonDocument.Parse("""{"data":{"position":{"x":2}}}""");
        using var expected = JsonDocument.Parse("2.0");

        Assert.That(PilotJson.TryGetPath(document.RootElement, "data.position.x", out var actual), Is.True);
        Assert.That(PilotJson.Equivalent(actual, expected.RootElement), Is.True);
    }

    [Test]
    public void ExtractsOnlyIntegerEntityIds()
    {
        var response = Response(new
        {
            entities = new object[] { new { id = 4 }, new { id = 9 }, new { id = "bad" } },
        });

        Assert.That(PilotJson.ObservedEntityIds(response), Is.EquivalentTo(new[] { 4, 9 }));
    }

    internal static PilotResponse Response(object data, bool ok = true)
    {
        return new PilotResponse
        {
            Version = 1,
            Id = "test",
            Ok = ok,
            Data = JsonSerializer.SerializeToElement(data),
        };
    }
}
