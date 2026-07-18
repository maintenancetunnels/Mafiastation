using System.Reflection;
using Content.Server._ForkStation.Moderation;
using NUnit.Framework;

namespace Content.Tests.Server._ForkStation.Moderation;

[TestFixture, TestOf(typeof(MafiaLlmGatewaySystem))]
[Parallelizable(ParallelScope.All)]
public static class LlmProviderResponseParserTest
{
    [Test]
    public static void ParsesOpenAiCompatibleContentAndUsage()
    {
        const string body =
            """
            {
              "choices": [{"message": {"content": "{\"verdicts\":[]}"}}],
              "usage": {"prompt_tokens": 12, "completion_tokens": 3}
            }
            """;

        var result = Invoke("ParseOpenAiResponse", body);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.Content, Is.EqualTo("{\"verdicts\":[]}"));
            Assert.That(result.InputTokens, Is.EqualTo(12));
            Assert.That(result.OutputTokens, Is.EqualTo(3));
        });
    }

    [Test]
    public static void ConcatenatesAnthropicTextBlocksAndUsage()
    {
        const string body =
            """
            {
              "content": [
                {"type": "text", "text": "{\"verdicts\":"},
                {"type": "tool_use", "name": "ignored"},
                {"type": "text", "text": "[]}"}
              ],
              "usage": {"input_tokens": 15, "output_tokens": 4}
            }
            """;

        var result = Invoke("ParseAnthropicResponse", body);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.Content, Is.EqualTo("{\"verdicts\":[]}"));
            Assert.That(result.InputTokens, Is.EqualTo(15));
            Assert.That(result.OutputTokens, Is.EqualTo(4));
        });
    }

    [Test]
    public static void RejectsProviderResponseWithoutText()
    {
        const string body =
            """
            {"content": [{"type": "tool_use", "name": "not-authorized"}]}
            """;

        var result = Invoke("ParseAnthropicResponse", body);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.FailureKind, Is.EqualTo(LlmFailureKind.InvalidResponse));
            Assert.That(result.Content, Is.Null);
        });
    }

    private static LlmGatewayResult Invoke(string methodName, string body)
    {
        var method = typeof(MafiaLlmGatewaySystem).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(method, Is.Not.Null);

        return (LlmGatewayResult) method!.Invoke(
            null,
            new object[] { body, "test-provider", "test-model" })!;
    }
}
