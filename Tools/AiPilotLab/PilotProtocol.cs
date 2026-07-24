using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Mafiastation.AiPilotLab;

public sealed class PilotRequest
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("action")]
    public string Action { get; init; } = string.Empty;

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; init; } = JsonSerializer.SerializeToElement(new { });

    public static PilotRequest Create(string action, JsonElement? arguments = null)
    {
        return new PilotRequest
        {
            Action = action.Trim().ToLowerInvariant(),
            Arguments = arguments?.Clone() ?? JsonSerializer.SerializeToElement(new { }),
        };
    }
}

public sealed class PilotResponse
{
    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("data")]
    public JsonElement Data { get; init; }
}

public sealed record PilotExchange(PilotRequest Request, PilotResponse Response, JsonElement RawResponse);

public interface IPilotTransport
{
    string PipeName { get; }

    Task<PilotExchange> SendAsync(PilotRequest request, CancellationToken cancellationToken = default);
}

public sealed class PilotPipeClient : IPilotTransport
{
    private const int MaximumResponseCharacters = 65_536;
    private static readonly Regex PipeNamePattern = new("^[A-Za-z0-9._-]{1,96}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
    };

    public string PipeName { get; }
    public TimeSpan Timeout { get; }

    public PilotPipeClient(string pipeName, TimeSpan timeout)
    {
        if (!PipeNamePattern.IsMatch(pipeName))
            throw new ArgumentException("Pipe name must contain only letters, digits, dot, underscore, or dash and be at most 96 characters.", nameof(pipeName));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(timeout));

        PipeName = pipeName;
        Timeout = timeout;
    }

    public async Task<PilotExchange> SendAsync(PilotRequest request, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)Timeout.TotalMilliseconds, timeout.Token);

            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
            };
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var requestJson = JsonSerializer.Serialize(request, JsonOptions);
            await writer.WriteLineAsync(requestJson.AsMemory(), timeout.Token);
            var responseJson = await ReadBoundedLineAsync(reader, MaximumResponseCharacters, timeout.Token);
            if (string.IsNullOrWhiteSpace(responseJson))
                throw new IOException("Pilot bridge closed without returning a response.");

            using var document = JsonDocument.Parse(responseJson);
            var root = document.RootElement.Clone();
            var response = JsonSerializer.Deserialize<PilotResponse>(responseJson, JsonOptions)
                ?? throw new InvalidDataException("Pilot bridge returned an empty response object.");
            if (response.Version != 1 || !string.Equals(response.Id, request.Id, StringComparison.Ordinal))
                throw new InvalidDataException("Pilot bridge response version or request ID did not match.");

            return new PilotExchange(request, response, root);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Pilot pipe '{PipeName}' request timed out after {Timeout.TotalMilliseconds:0} ms.",
                exception);
        }
    }

    private static async Task<string?> ReadBoundedLineAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 4096));
        var buffer = new char[1024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0)
                return builder.Length == 0 ? null : builder.ToString();
            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                    return builder.ToString().TrimEnd('\r');
                if (builder.Length >= maximumCharacters)
                    throw new InvalidDataException($"Pilot bridge response exceeded {maximumCharacters} characters.");
                builder.Append(character);
            }
        }
    }
}

public static class PilotJson
{
    public static bool TryGetPath(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        if (string.IsNullOrWhiteSpace(path))
            return true;

        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
                return false;
        }
        return true;
    }

    public static bool Equivalent(JsonElement actual, JsonElement expected)
    {
        if (actual.ValueKind == JsonValueKind.Number && expected.ValueKind == JsonValueKind.Number &&
            actual.TryGetDouble(out var actualNumber) && expected.TryGetDouble(out var expectedNumber))
        {
            return Math.Abs(actualNumber - expectedNumber) < 0.000001;
        }
        return JsonElement.DeepEquals(actual, expected);
    }

    public static HashSet<int> ObservedEntityIds(PilotResponse response)
    {
        var ids = new HashSet<int>();
        if (response.Data.ValueKind != JsonValueKind.Object ||
            !response.Data.TryGetProperty("entities", out var entities) ||
            entities.ValueKind != JsonValueKind.Array)
        {
            return ids;
        }

        foreach (var entity in entities.EnumerateArray())
        {
            if (entity.ValueKind == JsonValueKind.Object &&
                entity.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.Number &&
                id.TryGetInt32(out var integer))
            {
                ids.Add(integer);
            }
        }
        return ids;
    }

    /// <summary>
    /// Enforces a bridge-reported dynamic capacity when present. Older recordings and isolated
    /// validator tests may not contain a capacity snapshot, so absence remains "unknown"; a
    /// present false value always fails closed.
    /// </summary>
    public static bool CapabilityAllows(PilotResponse? response, string capability)
    {
        if (response?.Data.ValueKind != JsonValueKind.Object ||
            !response.Data.TryGetProperty("capabilities", out var capabilities) ||
            capabilities.ValueKind != JsonValueKind.Object ||
            !capabilities.TryGetProperty(capability, out var value))
        {
            return true;
        }

        return value.ValueKind == JsonValueKind.True;
    }

    public static string? GoalState(PilotResponse? response)
    {
        if (response?.Data.ValueKind != JsonValueKind.Object)
            return null;

        if (response.Data.TryGetProperty("state", out var direct) &&
            direct.ValueKind == JsonValueKind.String)
        {
            return direct.GetString();
        }

        if (response.Data.TryGetProperty("goal", out var goal) &&
            goal.ValueKind == JsonValueKind.Object &&
            goal.TryGetProperty("state", out var nested) &&
            nested.ValueKind == JsonValueKind.String)
        {
            return nested.GetString();
        }

        return null;
    }
}
