using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.GameTicking.Events;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.Network;

namespace Content.Server._ForkStation.Moderation;

/// <summary>
/// Provider-neutral, budgeted LLM gateway shared by moderation and future bounded NPC/event
/// directors. It implements wire protocols only; consumers own prompts, schemas, validation, and
/// authorization of any resulting game action.
/// </summary>
public sealed class MafiaLlmGatewaySystem : EntitySystem
{
    private const string ApiKeyEnvironmentVariable = "MAFIA_LLM_KEY";
    private const int MaximumResponseCharacters = 1_000_000;

    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IHttpClientHolder _http = default!;

    private readonly object _budgetLock = new();
    private readonly Queue<DateTimeOffset> _requestTimes = new();
    private readonly CancellationTokenSource _shutdown = new();
    private int _reservedTokensThisRound;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundStartingEvent>(OnRoundStarting);
    }

    public override void Shutdown()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        base.Shutdown();
    }

    private void OnRoundStarting(RoundStartingEvent ev)
    {
        lock (_budgetLock)
        {
            _reservedTokensThisRound = 0;
            _requestTimes.Clear();
        }
    }

    /// <summary>
    /// Returns false without logging when the gateway is intentionally disabled or lacks
    /// credentials. This lets optional consumers remain completely quiet on ordinary servers.
    /// </summary>
    public bool IsAvailable()
    {
        if (!_cfg.GetCVar(CCVars.MafiaLlmEnabled))
            return false;

        var endpoint = _cfg.GetCVar(CCVars.MafiaLlmEndpoint);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        return _cfg.GetCVar(CCVars.MafiaLlmAllowUnauthenticated) ||
               !string.IsNullOrWhiteSpace(GetApiKey());
    }

    public async Task<LlmGatewayResult> CompleteStructuredAsync(
        LlmStructuredRequest request,
        CancellationToken cancellationToken = default)
    {
        var provider = _cfg.GetCVar(CCVars.MafiaLlmProvider).Trim().ToLowerInvariant();
        var model = _cfg.GetCVar(CCVars.MafiaLlmModel).Trim();

        if (!_cfg.GetCVar(CCVars.MafiaLlmEnabled))
            return LlmGatewayResult.Failed(LlmFailureKind.Disabled, "LLM gateway is disabled.", provider, model);

        var endpointText = _cfg.GetCVar(CCVars.MafiaLlmEndpoint).Trim();
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            return LlmGatewayResult.Failed(
                LlmFailureKind.InvalidConfiguration,
                "LLM endpoint must be an absolute HTTP or HTTPS URL.",
                provider,
                model);
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return LlmGatewayResult.Failed(
                LlmFailureKind.InvalidConfiguration,
                "LLM model is empty.",
                provider,
                model);
        }

        if (provider is not ("anthropic" or "openai-compatible"))
        {
            return LlmGatewayResult.Failed(
                LlmFailureKind.InvalidConfiguration,
                "Unsupported LLM provider protocol.",
                provider,
                model);
        }

        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey) &&
            !_cfg.GetCVar(CCVars.MafiaLlmAllowUnauthenticated))
        {
            return LlmGatewayResult.Failed(
                LlmFailureKind.NotConfigured,
                "No LLM API key is configured.",
                provider,
                model);
        }

        var configuredMaximum = Math.Clamp(_cfg.GetCVar(CCVars.MafiaLlmMaxOutputTokens), 1, 8192);
        var outputTokens = Math.Clamp(request.MaxOutputTokens ?? configuredMaximum, 1, configuredMaximum);
        var reservedTokens = EstimateTokens(request.SystemPrompt, request.UserPrompt, outputTokens);

        if (!TryReserveRequest(reservedTokens, out var budgetFailure))
            return LlmGatewayResult.Failed(budgetFailure, BudgetFailureMessage(budgetFailure), provider, model);

        var timeout = TimeSpan.FromSeconds(Math.Clamp(
            _cfg.GetCVar(CCVars.MafiaLlmTimeoutSeconds),
            1f,
            120f));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        linked.CancelAfter(timeout);

        try
        {
            using var httpRequest = BuildHttpRequest(
                provider,
                endpoint,
                model,
                apiKey,
                request,
                outputTokens);
            using var response = await _http.Client.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token);

            if (!response.IsSuccessStatusCode)
            {
                return LlmGatewayResult.Failed(
                    LlmFailureKind.ProviderRejected,
                    $"LLM provider returned HTTP {(int) response.StatusCode}.",
                    provider,
                    model);
            }

            var body = await response.Content.ReadAsStringAsync(linked.Token);
            if (body.Length > MaximumResponseCharacters)
            {
                return LlmGatewayResult.Failed(
                    LlmFailureKind.InvalidResponse,
                    "LLM provider response exceeded the size limit.",
                    provider,
                    model);
            }

            return provider == "anthropic"
                ? ParseAnthropicResponse(body, provider, model)
                : ParseOpenAiResponse(body, provider, model);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested || _shutdown.IsCancellationRequested)
                return LlmGatewayResult.Failed(LlmFailureKind.Cancelled, "LLM request was cancelled.", provider, model);

            return LlmGatewayResult.Failed(LlmFailureKind.Timeout, "LLM request timed out.", provider, model);
        }
        catch (HttpRequestException)
        {
            return LlmGatewayResult.Failed(
                LlmFailureKind.Transport,
                "LLM provider could not be reached.",
                provider,
                model);
        }
        catch (JsonException)
        {
            return LlmGatewayResult.Failed(
                LlmFailureKind.InvalidResponse,
                "LLM provider returned invalid JSON.",
                provider,
                model);
        }
    }

    private bool TryReserveRequest(int tokens, out LlmFailureKind failure)
    {
        lock (_budgetLock)
        {
            var now = DateTimeOffset.UtcNow;
            while (_requestTimes.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromMinutes(1))
                _requestTimes.Dequeue();

            var requestsPerMinute = Math.Clamp(_cfg.GetCVar(CCVars.MafiaLlmRequestsPerMinute), 1, 120);
            if (_requestTimes.Count >= requestsPerMinute)
            {
                failure = LlmFailureKind.RateLimited;
                return false;
            }

            var roundBudget = Math.Max(1, _cfg.GetCVar(CCVars.MafiaLlmRoundTokenBudget));
            if (tokens > roundBudget - _reservedTokensThisRound)
            {
                failure = LlmFailureKind.BudgetExceeded;
                return false;
            }

            _requestTimes.Enqueue(now);
            _reservedTokensThisRound += tokens;
            failure = LlmFailureKind.None;
            return true;
        }
    }

    private HttpRequestMessage BuildHttpRequest(
        string provider,
        Uri endpoint,
        string model,
        string apiKey,
        LlmStructuredRequest request,
        int outputTokens)
    {
        object payload = provider == "anthropic"
            ? BuildAnthropicPayload(model, request, outputTokens)
            : BuildOpenAiPayload(model, request, outputTokens);

        var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };

        message.Headers.UserAgent.ParseAdd("Mafiastation-LLM/1.0");
        if (provider == "anthropic")
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
                message.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else if (!string.IsNullOrWhiteSpace(apiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return message;
    }

    private object BuildOpenAiPayload(
        string model,
        LlmStructuredRequest request,
        int outputTokens)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt },
            },
            ["temperature"] = Math.Clamp(request.Temperature, 0f, 2f),
            ["max_tokens"] = outputTokens,
        };

        switch (_cfg.GetCVar(CCVars.MafiaLlmResponseFormat).Trim().ToLowerInvariant())
        {
            case "json_object":
                payload["response_format"] = new { type = "json_object" };
                break;
            case "json_schema":
                using (var schema = JsonDocument.Parse(request.JsonSchema))
                {
                    payload["response_format"] = new
                    {
                        type = "json_schema",
                        json_schema = new
                        {
                            name = request.SchemaName,
                            strict = true,
                            schema = schema.RootElement.Clone(),
                        },
                    };
                }
                break;
        }

        return payload;
    }

    private static object BuildAnthropicPayload(
        string model,
        LlmStructuredRequest request,
        int outputTokens)
    {
        return new
        {
            model,
            system = request.SystemPrompt,
            messages = new object[]
            {
                new { role = "user", content = request.UserPrompt },
            },
            temperature = Math.Clamp(request.Temperature, 0f, 1f),
            max_tokens = outputTokens,
        };
    }

    private static LlmGatewayResult ParseOpenAiResponse(string body, string provider, string model)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String)
        {
            return LlmGatewayResult.Failed(
                LlmFailureKind.InvalidResponse,
                "OpenAI-compatible response did not contain message content.",
                provider,
                model);
        }

        var inputTokens = 0;
        var outputTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            TryReadInt(usage, "prompt_tokens", out inputTokens);
            TryReadInt(usage, "completion_tokens", out outputTokens);
        }

        return new LlmGatewayResult(
            true,
            content.GetString(),
            provider,
            model,
            inputTokens,
            outputTokens,
            LlmFailureKind.None,
            null);
    }

    private static LlmGatewayResult ParseAnthropicResponse(string body, string provider, string model)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (!root.TryGetProperty("content", out var blocks) ||
            blocks.ValueKind != JsonValueKind.Array)
        {
            return LlmGatewayResult.Failed(
                LlmFailureKind.InvalidResponse,
                "Anthropic response did not contain content blocks.",
                provider,
                model);
        }

        var text = new StringBuilder();
        foreach (var block in blocks.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type) &&
                type.GetString() == "text" &&
                block.TryGetProperty("text", out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                text.Append(value.GetString());
            }
        }

        if (text.Length == 0)
        {
            return LlmGatewayResult.Failed(
                LlmFailureKind.InvalidResponse,
                "Anthropic response did not contain a text block.",
                provider,
                model);
        }

        var inputTokens = 0;
        var outputTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            TryReadInt(usage, "input_tokens", out inputTokens);
            TryReadInt(usage, "output_tokens", out outputTokens);
        }

        return new LlmGatewayResult(
            true,
            text.ToString(),
            provider,
            model,
            inputTokens,
            outputTokens,
            LlmFailureKind.None,
            null);
    }

    private static bool TryReadInt(JsonElement parent, string property, out int value)
    {
        value = 0;
        return parent.TryGetProperty(property, out var element) &&
               element.TryGetInt32(out value);
    }

    private string GetApiKey()
    {
        var environmentKey = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        return !string.IsNullOrWhiteSpace(environmentKey)
            ? environmentKey.Trim()
            : _cfg.GetCVar(CCVars.MafiaLlmApiKey).Trim();
    }

    private static int EstimateTokens(string systemPrompt, string userPrompt, int outputTokens)
    {
        // Conservative, provider-independent estimate suitable for a hard cost guard.
        var inputCharacters = (long) systemPrompt.Length + userPrompt.Length;
        return (int) Math.Min(int.MaxValue, (inputCharacters + 2L) / 3L + outputTokens);
    }

    private static string BudgetFailureMessage(LlmFailureKind failure)
    {
        return failure == LlmFailureKind.RateLimited
            ? "LLM request rate limit reached."
            : "LLM round token budget reached.";
    }
}
