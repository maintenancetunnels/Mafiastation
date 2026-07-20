using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Robust.Client.Mafiastation.AiPilot;

/// <summary>
/// Primitive-only view over JSON arguments. Sandboxed content can read bounded values without
/// receiving a System.Text.Json type or a general operating-system I/O surface.
/// </summary>
public sealed class AiPilotPipeArguments
{
    private readonly JsonElement _element;

    internal AiPilotPipeArguments(JsonElement element)
    {
        _element = element;
    }

    public bool Contains(string property)
    {
        return _element.TryGetProperty(property, out _);
    }

    public bool TryGetString(string property, out string value)
    {
        value = string.Empty;
        if (!_element.TryGetProperty(property, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    public bool TryGetInt32(string property, out int value)
    {
        value = default;
        return _element.TryGetProperty(property, out var element) &&
               element.TryGetInt32(out value);
    }

    public bool TryGetSingle(string property, out float value)
    {
        value = default;
        return _element.TryGetProperty(property, out var element) &&
               element.TryGetSingle(out value);
    }

    public bool TryGetBoolean(string property, out bool value)
    {
        value = default;
        if (!_element.TryGetProperty(property, out var element) ||
            element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }
}

public sealed class AiPilotPipeRequest
{
    public int Version { get; }
    public string Id { get; }
    public string Action { get; }
    public AiPilotPipeArguments Arguments { get; }

    internal AiPilotPipeRequest(
        int version,
        string id,
        string action,
        AiPilotPipeArguments arguments)
    {
        Version = version;
        Id = id;
        Action = action;
        Arguments = arguments;
    }
}

public sealed class AiPilotPipeResponse
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    [JsonPropertyName("data")]
    public object Data { get; init; } = new { };

    public static AiPilotPipeResponse Success(string id, object data)
    {
        return new AiPilotPipeResponse
        {
            Id = id,
            Ok = true,
            Data = data,
        };
    }

    public static AiPilotPipeResponse Failure(string id, string error, object? data = null)
    {
        return new AiPilotPipeResponse
        {
            Id = id,
            Ok = false,
            Error = error,
            Data = data ?? new { },
        };
    }
}

/// <summary>
/// Narrow trusted boundary for the local pilot experiment. Normal content remains sandboxed.
/// This helper owns only one current-user named pipe and will not start unless the immutable
/// process command line proves that this is an explicitly enabled headless loopback pilot.
/// </summary>
public static class AiPilotPipeBridge
{
    public const string ProcessFlag = "--ai-pilot-local-trusted-bridge";
    public const int MaximumRequestCharacters = 16_384;
    public const int MaximumResponseCharacters = 65_536;
    public const int MaximumRequestIdCharacters = 128;
    public const int MaximumActionCharacters = 32;

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
    };
    private static readonly string? AuthorizedPipe = ResolveAuthorizedPipe();

    private static CancellationTokenSource? _cancellation;
    private static Task? _runTask;
    private static Func<AiPilotPipeRequest, Task<AiPilotPipeResponse>>? _handler;
    private static volatile bool _running;
    private static string _pipeName = string.Empty;
    private static string _lastError = string.Empty;

    public static bool IsProcessAuthorized => AuthorizedPipe != null;
    public static bool IsRunning => _running;

    public static string PipeName
    {
        get
        {
            lock (Gate)
                return _pipeName;
        }
    }

    public static string LastError
    {
        get
        {
            lock (Gate)
                return _lastError;
        }
    }

    /// <summary>
    /// Forces this trusted dependency to load before Robust begins loading sandboxed modules.
    /// </summary>
    public static void Preload()
    {
    }

    public static bool Start(
        string pipeName,
        Func<AiPilotPipeRequest, Task<AiPilotPipeResponse>> handler,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(handler);
        pipeName = pipeName.Trim();
        lock (Gate)
        {
            if (AuthorizedPipe == null)
            {
                error =
                    "Trusted pilot IPC is unavailable: the process was not launched as an " +
                    "explicit headless loopback pilot.";
                return false;
            }

            if (!string.Equals(pipeName, AuthorizedPipe, StringComparison.Ordinal))
            {
                error = "Pilot pipe does not match the immutable launch authorization.";
                return false;
            }

            if (_cancellation != null)
            {
                error = _running
                    ? "Pilot pipe is already running."
                    : "Pilot pipe is still stopping.";
                return false;
            }

            _pipeName = pipeName;
            _lastError = string.Empty;
            _handler = handler;
            _cancellation = new CancellationTokenSource();
            var cancellation = _cancellation;
            _runTask = Task.Run(() => RunAsync(pipeName, cancellation));
            error = string.Empty;
            return true;
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _handler = null;
            _cancellation?.Cancel();
        }
    }

    private static async Task RunAsync(string pipeName, CancellationTokenSource cancellation)
    {
        _running = true;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(cancellation.Token).ConfigureAwait(false);
                    await HandleConnectionAsync(pipe, cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    SetLastError(
                        $"Pilot pipe rejected a connection after " +
                        $"{exception.GetType().Name}: {exception.Message}");
                    await Task.Delay(250, cancellation.Token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _running = false;
            lock (Gate)
            {
                if (ReferenceEquals(_cancellation, cancellation))
                {
                    _cancellation = null;
                    _runTask = null;
                    _handler = null;
                    _pipeName = string.Empty;
                }
            }
            cancellation.Dispose();
        }
    }

    private static async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            pipe,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        await using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(false),
            leaveOpen: true)
        {
            AutoFlush = true,
        };

        var requestJson = await ReadBoundedLineAsync(
                reader,
                MaximumRequestCharacters,
                cancellationToken)
            .ConfigureAwait(false);
        if (requestJson == null)
            return;

        AiPilotPipeResponse response;
        if (!TryParseRequest(requestJson, out var request, out var requestId, out var error) ||
            request == null)
        {
            response = AiPilotPipeResponse.Failure(requestId, error);
        }
        else
        {
            Func<AiPilotPipeRequest, Task<AiPilotPipeResponse>>? handler;
            lock (Gate)
                handler = _handler;

            if (handler == null)
            {
                response = AiPilotPipeResponse.Failure(
                    request.Id,
                    "Pilot bridge stopped before the request could run.");
            }
            else
            {
                try
                {
                    response = await handler(request).ConfigureAwait(false);
                    if (!string.Equals(response.Id, request.Id, StringComparison.Ordinal))
                    {
                        response = AiPilotPipeResponse.Failure(
                            request.Id,
                            "Pilot content returned a mismatched response ID.");
                    }
                }
                catch (Exception exception)
                {
                    response = AiPilotPipeResponse.Failure(
                        request.Id,
                        $"Pilot content failed safely ({exception.GetType().Name}).");
                }
            }
        }

        var responseJson = JsonSerializer.Serialize(response, JsonOptions);
        if (responseJson.Length > MaximumResponseCharacters)
        {
            responseJson = JsonSerializer.Serialize(
                AiPilotPipeResponse.Failure(
                    response.Id,
                    "Pilot response exceeded the bridge size limit."),
                JsonOptions);
        }

        await writer.WriteLineAsync(responseJson.AsMemory(), cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool TryParseRequest(
        string json,
        out AiPilotPipeRequest? request,
        out string requestId,
        out string error)
    {
        request = null;
        requestId = string.Empty;
        error = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Pilot request must be a JSON object.";
                return false;
            }

            var properties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!properties.Add(property.Name))
                {
                    error = $"Pilot request contains duplicate property '{property.Name}'.";
                    return false;
                }

                if (property.Name is not ("version" or "id" or "action" or "arguments"))
                {
                    error = $"Pilot request contains unsupported property '{property.Name}'.";
                    return false;
                }
            }

            var dto = JsonSerializer.Deserialize<AiPilotRequestDto>(json, JsonOptions);
            if (dto == null)
            {
                error = "Pilot request was empty.";
                return false;
            }

            var id = dto.Id ?? string.Empty;
            var action = dto.Action ?? string.Empty;
            requestId = id;
            if (dto.Version != 1)
            {
                error = "Pilot protocol version must be 1.";
                return false;
            }

            if (id.Length is < 1 or > MaximumRequestIdCharacters ||
                id.Any(char.IsControl))
            {
                error =
                    $"Pilot request id must contain 1-{MaximumRequestIdCharacters} " +
                    "non-control characters.";
                return false;
            }

            if (action.Length is < 1 or > MaximumActionCharacters ||
                action.Any(character =>
                    !(char.IsAsciiLetterLower(character) || character == '_')))
            {
                error = "Pilot action must be lowercase ASCII letters and underscores.";
                return false;
            }

            JsonElement arguments;
            if (dto.Arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                arguments = JsonSerializer.SerializeToElement(new { });
            }
            else if (dto.Arguments.ValueKind != JsonValueKind.Object)
            {
                error = "Pilot arguments must be a JSON object.";
                return false;
            }
            else
            {
                arguments = dto.Arguments.Clone();
            }

            request = new AiPilotPipeRequest(
                dto.Version,
                id,
                action,
                new AiPilotPipeArguments(arguments));
            return true;
        }
        catch (JsonException)
        {
            error = "Pilot request was not valid bounded JSON.";
            return false;
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
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
                return builder.Length == 0 ? null : builder.ToString();

            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                    return builder.ToString().TrimEnd('\r');
                if (builder.Length >= maximumCharacters)
                {
                    throw new InvalidDataException(
                        $"Pilot request exceeded {maximumCharacters} characters.");
                }
                builder.Append(character);
            }
        }
    }

    private static string? ResolveAuthorizedPipe()
    {
#if FULL_RELEASE
        return null;
#else
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains(ProcessFlag, StringComparer.Ordinal) ||
            !args.Contains("--headless", StringComparer.Ordinal) ||
            !args.Contains("--connect", StringComparer.Ordinal) ||
            !TryGetLastArgument(args, "--connect-address", out var address) ||
            !IsLoopbackAddress(address) ||
            !TryGetLastCvar(args, "mafia.ai_pilot.client_enabled", out var enabled) ||
            !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase) ||
            !TryGetLastCvar(args, "mafia.ai_pilot.pipe_name", out var pipeName) ||
            !IsValidPipeName(pipeName))
        {
            return null;
        }

        return pipeName.Trim();
#endif
    }

    internal static bool TryGetLastArgument(
        IReadOnlyList<string> args,
        string name,
        out string value)
    {
        value = string.Empty;
        var found = false;
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.Ordinal))
                continue;
            value = args[index + 1];
            found = true;
        }
        return found;
    }

    internal static bool TryGetLastCvar(
        IReadOnlyList<string> args,
        string name,
        out string value)
    {
        value = string.Empty;
        var found = false;
        var prefix = name + "=";
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (!string.Equals(args[index], "--cvar", StringComparison.Ordinal) ||
                !args[index + 1].StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            value = args[index + 1][prefix.Length..];
            found = true;
        }
        return found;
    }

    internal static bool IsLoopbackAddress(string address)
    {
        var normalized = address.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal))
            normalized = "udp://" + normalized;

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "udp", StringComparison.OrdinalIgnoreCase) ||
            uri.Port is < 1 or > 65_535 ||
            uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/")
        {
            return false;
        }

        var host = uri.Host.Trim('[', ']');
        return IPAddress.TryParse(host, out var ipAddress)
            ? IPAddress.IsLoopback(ipAddress)
            : string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsValidPipeName(string value)
    {
        value = value.Trim();
        return value.Length is >= 1 and <= 96 &&
               value.All(character =>
                   char.IsAsciiLetterOrDigit(character) ||
                   character is '.' or '_' or '-');
    }

    private static void SetLastError(string error)
    {
        lock (Gate)
            _lastError = error;
    }

    private sealed class AiPilotRequestDto
    {
        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("action")]
        public string? Action { get; init; }

        [JsonPropertyName("arguments")]
        public JsonElement Arguments { get; init; }
    }
}
