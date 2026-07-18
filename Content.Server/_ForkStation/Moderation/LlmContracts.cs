namespace Content.Server._ForkStation.Moderation;

/// <summary>
/// A provider-neutral request for one JSON object. Consumers must still validate the returned
/// object against their own allowlist and domain rules.
/// </summary>
public sealed record LlmStructuredRequest(
    string Purpose,
    string SystemPrompt,
    string UserPrompt,
    string SchemaName,
    string JsonSchema,
    int? MaxOutputTokens = null,
    float Temperature = 0f);

public enum LlmFailureKind : byte
{
    None,
    Disabled,
    NotConfigured,
    InvalidConfiguration,
    RateLimited,
    BudgetExceeded,
    Cancelled,
    Timeout,
    Transport,
    ProviderRejected,
    InvalidResponse,
}

/// <summary>
/// Sanitized gateway result. Provider response bodies are intentionally not exposed on errors.
/// </summary>
public sealed record LlmGatewayResult(
    bool Success,
    string? Content,
    string Provider,
    string Model,
    int InputTokens,
    int OutputTokens,
    LlmFailureKind FailureKind,
    string? SafeError)
{
    public static LlmGatewayResult Failed(
        LlmFailureKind kind,
        string error,
        string provider = "",
        string model = "")
    {
        return new LlmGatewayResult(false, null, provider, model, 0, 0, kind, error);
    }
}
