using System.IO;
using System.Text.Json;
using Robust.Shared.Log;

namespace Content.Server._ForkStation.Moderation;

/// <summary>
/// JSON incident audit store rooted beneath server user data. Only messages cited as evidence are
/// persisted; ordinary unflagged chat batches are not retained here.
/// </summary>
internal sealed class ModerationIncidentStore
{
    private static readonly ISawmill Log = Logger.GetSawmill("mafia.moderation.incidents");

    private readonly object _lock = new();
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
    };
    private ModerationIncidentDocument _document;

    public ModerationIncidentStore(string userDataRoot, string relativePath)
    {
        _filePath = ResolvePath(userDataRoot, relativePath);
        _document = Load();
    }

    public bool Append(ModerationIncident incident)
    {
        lock (_lock)
        {
            _document.Incidents.Add(incident);
            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var temporaryPath = _filePath + ".tmp";
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(_document, _jsonOptions));
                File.Move(temporaryPath, _filePath, true);
                return true;
            }
            catch (Exception exception)
            {
                _document.Incidents.Remove(incident);
                Log.Error($"Unable to persist moderation incident: {exception.GetType().Name}");
                return false;
            }
        }
    }

    private ModerationIncidentDocument Load()
    {
        if (!File.Exists(_filePath))
            return new ModerationIncidentDocument();

        try
        {
            var document = JsonSerializer.Deserialize<ModerationIncidentDocument>(
                File.ReadAllText(_filePath));
            return document ?? new ModerationIncidentDocument();
        }
        catch (Exception exception)
        {
            Log.Error($"Unable to read moderation incident store: {exception.GetType().Name}");
            return new ModerationIncidentDocument();
        }
    }

    private static string ResolvePath(string userDataRoot, string relativePath)
    {
        var root = Path.GetFullPath(userDataRoot);
        var candidate = string.IsNullOrWhiteSpace(relativePath)
            ? "moderation_incidents.json"
            : relativePath.Trim();

        if (Path.IsPathRooted(candidate))
            candidate = "moderation_incidents.json";

        var fullPath = Path.GetFullPath(Path.Combine(root, candidate));
        var rootedPrefix = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase))
            return Path.Combine(root, "moderation_incidents.json");

        return fullPath;
    }
}
