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

    /// <summary>Test helper: construct against an explicit path without loading disk.</summary>
    public PenaltyDataStore(string filePath, bool forTests)
    {
        _filePath = filePath;
        _records = new List<PenaltyRecord>();
        _nextId = 1;
        if (!forTests)
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
                // Preserve the unreadable file instead of letting the next Save() overwrite it
                // with an empty array. These records are the only record of why players are
                // serving time; silently discarding them is worse than failing loudly.
                Log.Error($"Failed to load penalties: {ex.Message}");

                try
                {
                    var quarantine = _filePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                    File.Copy(_filePath, quarantine, overwrite: true);
                    Log.Error($"Preserved the unreadable penalty file at {quarantine}.");
                }
                catch (Exception copyEx)
                {
                    Log.Error($"Could not preserve the unreadable penalty file: {copyEx.Message}");
                }

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

                // Write-then-replace so a crash mid-write cannot truncate the live file.
                var temp = _filePath + ".tmp";
                File.WriteAllText(temp, json);

                if (File.Exists(_filePath))
                    File.Replace(temp, _filePath, destinationBackupFileName: null);
                else
                    File.Move(temp, _filePath);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to save penalties: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Add a new penalty record. Security issues set <paramref name="pendingCustody"/> so the
    /// sentence only becomes outstanding force-spawn balance after end-round custody.
    /// </summary>
    public PenaltyRecord AddPenalty(string playerUserId, string issuedBy, string issuedByName,
        int rounds, string reason, int roundId, bool adminIssued, bool pendingCustody = false)
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
                PendingCustody = pendingCustody,
            };

            _records.Add(record);
            Save();
            return record;
        }
    }

    /// <summary>
    /// Get all active (unserved, confirmed) penalties for a player — force-spawn balance.
    /// </summary>
    public List<PenaltyRecord> GetActivePenalties(string playerUserId)
    {
        lock (_lock)
        {
            return _records
                .Where(r => r.PlayerUserId == playerUserId && r.CountsTowardBalance)
                .ToList();
        }
    }

    /// <summary>
    /// Outstanding force-spawn balance only (confirmed + unserved).
    /// </summary>
    public int GetTotalPenaltyRounds(string playerUserId)
    {
        lock (_lock)
        {
            return _records
                .Where(r => r.PlayerUserId == playerUserId && r.CountsTowardBalance)
                .Sum(r => r.RoundsRemaining);
        }
    }

    /// <summary>
    /// All unserved rounds for a player including still-pending custody sentences (for UI).
    /// </summary>
    public int GetPendingPlusOutstandingRounds(string playerUserId)
    {
        lock (_lock)
        {
            return _records
                .Where(r => r.PlayerUserId == playerUserId && !r.IsServed)
                .Sum(r => r.RoundsRemaining);
        }
    }

    public List<PenaltyRecord> GetPendingPenalties(string playerUserId)
    {
        lock (_lock)
        {
            return _records
                .Where(r => r.PlayerUserId == playerUserId && r.PendingCustody && !r.IsServed)
                .ToList();
        }
    }

    public List<PenaltyRecord> GetAllPenalties(string playerUserId)
    {
        lock (_lock)
        {
            return _records
                .Where(r => r.PlayerUserId == playerUserId)
                .ToList();
        }
    }

    public bool ServeRound(string playerUserId)
    {
        lock (_lock)
        {
            var oldest = _records
                .Where(r => r.PlayerUserId == playerUserId && r.CountsTowardBalance)
                .OrderBy(r => r.IssuedAt)
                .FirstOrDefault();

            if (oldest == null)
                return false;

            oldest.RoundsServed++;
            Save();
            return true;
        }
    }

    public bool ServeRoundOnPenalty(int penaltyId)
    {
        lock (_lock)
        {
            var record = _records.FirstOrDefault(r => r.Id == penaltyId && r.CountsTowardBalance);
            if (record == null)
                return false;

            record.RoundsServed++;
            Save();
            return true;
        }
    }

    public PenaltyRecord? GetPenaltyById(int penaltyId)
    {
        lock (_lock)
        {
            return _records.FirstOrDefault(r => r.Id == penaltyId);
        }
    }

    /// <summary>
    /// Confirm a pending security sentence after custody at round end.
    /// Truncates against the lifetime cap if concurrent grants would otherwise exceed it.
    /// </summary>
    public bool ConfirmPending(int penaltyId)
    {
        lock (_lock)
        {
            var record = _records.FirstOrDefault(r => r.Id == penaltyId && r.PendingCustody);
            if (record == null)
                return false;

            // Defensive: never let confirmation push confirmed balance past the lifetime cap.
            var otherOutstanding = _records
                .Where(r => r.PlayerUserId == record.PlayerUserId && r.Id != record.Id && r.CountsTowardBalance)
                .Sum(r => r.RoundsRemaining);
            var room = PrisonerDesignRules.MaxPenaltyRounds - otherOutstanding;
            if (room <= 0)
            {
                _records.Remove(record);
                Save();
                return false;
            }

            if (record.RoundsRemaining > room)
                record.RoundsAssigned = record.RoundsServed + room;

            record.PendingCustody = false;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Drop unconfirmed security sentences that did not stick (escape / not in custody).
    /// </summary>
    public int DiscardPendingForPlayer(string playerUserId)
    {
        lock (_lock)
        {
            var count = _records.RemoveAll(r =>
                r.PlayerUserId == playerUserId && r.PendingCustody && !r.IsServed);
            if (count > 0)
                Save();
            return count;
        }
    }

    /// <summary>
    /// Discard every still-pending sentence for the current round that was not confirmed.
    /// Call after the custody-confirmation pass.
    /// </summary>
    public int DiscardAllRemainingPending()
    {
        lock (_lock)
        {
            var count = _records.RemoveAll(r => r.PendingCustody && !r.IsServed);
            if (count > 0)
                Save();
            return count;
        }
    }

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

    public Dictionary<string, int> GetAllActivePenaltySummary()
    {
        lock (_lock)
        {
            return _records
                .Where(r => r.CountsTowardBalance)
                .GroupBy(r => r.PlayerUserId)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.RoundsRemaining));
        }
    }

    /// <summary>
    /// Rounds applied this shift that still count against the per-round cap: pending + confirmed
    /// unserved issued this round, plus served amount from this round's grants is not tracked
    /// separately — callers track applied-this-round in memory.
    /// </summary>
    public IReadOnlyList<PenaltyRecord> Snapshot()
    {
        lock (_lock)
        {
            return _records.ToList();
        }
    }
}
