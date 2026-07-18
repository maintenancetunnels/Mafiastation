using System.IO;
using System.Linq;
using System.Text.Json;
using Content.Shared._ForkStation.PersistentPrisoner;
using Robust.Shared.Log;

namespace Content.Server._ForkStation.PersistentPrisoner;

/// <summary>
/// Simple JSON file-backed store for penalty records.
/// Stored in server data directory as penalties.json.
/// </summary>
public sealed class PenaltyDataStore
{
    private static readonly ISawmill Log = Logger.GetSawmill("penalty.store");

    private readonly string _filePath;
    private List<PenaltyRecord> _records = new();
    private int _nextId = 1;
    private readonly object _lock = new();

    public PenaltyDataStore(string dataDir)
    {
        _filePath = Path.Combine(dataDir, "penalties.json");
        Load();
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
            {
                _records = new List<PenaltyRecord>();
                _nextId = 1;
                return;
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                _records = JsonSerializer.Deserialize<List<PenaltyRecord>>(json) ?? new List<PenaltyRecord>();
                _nextId = _records.Count > 0 ? _records.Max(r => r.Id) + 1 : 1;
                Log.Info($"Loaded {_records.Count} penalty records.");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to load penalties: {ex.Message}");
                _records = new List<PenaltyRecord>();
                _nextId = 1;
            }
        }
    }

    private void Save()
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(_records, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to save penalties: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Add a new penalty record.
    /// </summary>
    public PenaltyRecord AddPenalty(string playerUserId, string issuedBy, string issuedByName,
        int rounds, string reason, int roundId, bool adminIssued)
    {
        lock (_lock)
        {
            var record = new PenaltyRecord
            {
                Id = _nextId++,
                PlayerUserId = playerUserId,
                IssuedBy = issuedBy,
                IssuedByName = issuedByName,
                RoundsAssigned = rounds,
                RoundsServed = 0,
                Reason = reason,
                IssuedAt = DateTime.UtcNow,
                IssuedRoundId = roundId,
                AdminIssued = adminIssued,
            };

            _records.Add(record);
            Save();
            return record;
        }
    }

    /// <summary>
    /// Get all active (unserved) penalties for a player.
    /// </summary>
    public List<PenaltyRecord> GetActivePenalties(string playerUserId)
    {
        lock (_lock)
        {
            return _records
                .Where(r => r.PlayerUserId == playerUserId && !r.IsServed)
                .ToList();
        }
    }

    /// <summary>
    /// Get total outstanding penalty rounds for a player.
    /// </summary>
    public int GetTotalPenaltyRounds(string playerUserId)
    {
        lock (_lock)
        {
            return _records
                .Where(r => r.PlayerUserId == playerUserId && !r.IsServed)
                .Sum(r => r.RoundsRemaining);
        }
    }

    /// <summary>
    /// Get all penalties (active and served) for a player.
    /// </summary>
    public List<PenaltyRecord> GetAllPenalties(string playerUserId)
    {
        lock (_lock)
        {
            return _records
                .Where(r => r.PlayerUserId == playerUserId)
                .ToList();
        }
    }

    /// <summary>
    /// Serve one penalty round for a player. Applies to the oldest unserved penalty first.
    /// </summary>
    public bool ServeRound(string playerUserId)
    {
        lock (_lock)
        {
            var oldest = _records
                .Where(r => r.PlayerUserId == playerUserId && !r.IsServed)
                .OrderBy(r => r.IssuedAt)
                .FirstOrDefault();

            if (oldest == null)
                return false;

            oldest.RoundsServed++;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Serve one round on a specific penalty by ID. Used for auto-decay.
    /// </summary>
    public bool ServeRoundOnPenalty(int penaltyId)
    {
        lock (_lock)
        {
            var record = _records.FirstOrDefault(r => r.Id == penaltyId && !r.IsServed);
            if (record == null)
                return false;

            record.RoundsServed++;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Remove a penalty by ID. Returns true if found and removed.
    /// </summary>
    public bool RemovePenalty(int penaltyId)
    {
        lock (_lock)
        {
            var record = _records.FirstOrDefault(r => r.Id == penaltyId);
            if (record == null)
                return false;

            _records.Remove(record);
            Save();
            return true;
        }
    }

    /// <summary>
    /// Wipe all penalties for a player. Admin-only operation.
    /// </summary>
    public int WipePenalties(string playerUserId)
    {
        lock (_lock)
        {
            var count = _records.RemoveAll(r => r.PlayerUserId == playerUserId);
            if (count > 0)
                Save();
            return count;
        }
    }

    /// <summary>
    /// Get a summary of all players with active penalties.
    /// </summary>
    public Dictionary<string, int> GetAllActivePenaltySummary()
    {
        lock (_lock)
        {
            return _records
                .Where(r => !r.IsServed)
                .GroupBy(r => r.PlayerUserId)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.RoundsRemaining));
        }
    }
}
