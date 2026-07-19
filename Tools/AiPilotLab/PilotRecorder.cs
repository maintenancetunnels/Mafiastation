using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mafiastation.AiPilotLab;

public sealed class PilotRecorder : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly StreamWriter _writer;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();

    public string Path { get; }

    public PilotRecorder(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        _writer = new StreamWriter(
            new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read, 16_384, FileOptions.Asynchronous),
            new UTF8Encoding(false));
    }

    public Task RecordRequestAsync(string bot, PilotRequest request, CancellationToken cancellationToken = default) =>
        WriteAsync("request", bot, request, null, cancellationToken);

    public Task RecordResponseAsync(
        string bot,
        PilotResponse response,
        double latencyMilliseconds,
        CancellationToken cancellationToken = default) =>
        WriteAsync("response", bot, response, latencyMilliseconds, cancellationToken);

    public Task RecordModelAsync(string bot, object decision, CancellationToken cancellationToken = default) =>
        WriteAsync("model", bot, decision, null, cancellationToken);

    public Task RecordErrorAsync(string bot, object error, CancellationToken cancellationToken = default) =>
        WriteAsync("error", bot, error, null, cancellationToken);

    private async Task WriteAsync(
        string type,
        string bot,
        object payload,
        double? latencyMilliseconds,
        CancellationToken cancellationToken)
    {
        var entry = new PilotLogEntry(
            1,
            type,
            DateTimeOffset.UtcNow,
            _elapsed.Elapsed.TotalMilliseconds,
            bot,
            latencyMilliseconds,
            JsonSerializer.SerializeToElement(payload, JsonOptions));
        var json = JsonSerializer.Serialize(entry, JsonOptions);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            await _writer.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            await _writer.DisposeAsync();
        }
        finally
        {
            _writeLock.Release();
            _writeLock.Dispose();
        }
    }
}

public sealed record PilotLogEntry(
    int Version,
    string Type,
    DateTimeOffset TimestampUtc,
    double ElapsedMilliseconds,
    string Bot,
    double? LatencyMilliseconds,
    JsonElement Payload);
