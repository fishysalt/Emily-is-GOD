using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using StardewModdingAPI;

namespace ValleyTalkMemory;

/// <summary>
/// Per-NPC memory, persisted in the save file under this mod's own keys.
/// We never write into ValleyTalk's keys.
/// </summary>
internal sealed class MemoryStore
{
    // SMAPI save-data keys may only contain letters, digits, '_', '.' and '-' — a '/' makes
    // WriteSaveData/ReadSaveData throw, which silently cost us all persisted long-term memory.
    private const string KeyRoot = "vtmemory_";
    private const string IndexKey = KeyRoot + "index";

    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly ModConfig _config;
    private readonly Dictionary<string, NpcMemory> _byNpc = new Dictionary<string, NpcMemory>(StringComparer.OrdinalIgnoreCase);

    public MemoryStore(IModHelper helper, IMonitor monitor, ModConfig config)
    {
        _helper = helper;
        _monitor = monitor;
        _config = config;
    }

    public IEnumerable<string> KnownNpcs => _byNpc.Keys;

    public IEnumerable<NpcMemory> All => _byNpc.Values;

    public NpcMemory GetOrCreate(string npc)
    {
        if (!_byNpc.TryGetValue(npc, out NpcMemory memory))
        {
            memory = new NpcMemory { Npc = npc };
            _byNpc[npc] = memory;
        }
        return memory;
    }

    public bool TryGet(string npc, out NpcMemory memory) => _byNpc.TryGetValue(npc, out memory);

    public long WeekIndexOf(RawEvent e) => GameWeek.WeekOf(e.Day, _config.DaysPerWeek);

    /// <summary>Merges freshly read ValleyTalk events. Returns true when something new arrived.</summary>
    /// <param name="canonicalName">
    /// True when <paramref name="npc"/> is a real NPC name rather than a lower-cased save key.
    /// The dictionary is case-insensitive, so "emily" and "Emily" share one slot but the stored key
    /// string keeps whichever was inserted first — which would make us register the override under
    /// the wrong casing. Recording the canonical name here is what prevents that.
    /// </param>
    public bool Merge(string npc, IEnumerable<RawEvent> events, bool canonicalName = true)
    {
        NpcMemory memory = GetOrCreate(npc);
        if (canonicalName && !string.IsNullOrWhiteSpace(npc) && !string.Equals(memory.Npc, npc, StringComparison.Ordinal))
            memory.Npc = npc;

        int added = MemoryMerge.Merge(memory.Raw, events);
        if (added == 0)
            return false;

        MemoryMerge.SortByTime(memory.Raw);
        Trim(memory);
        return true;
    }

    private void Trim(NpcMemory memory)
    {
        int dropped = MemoryMerge.DropCoveredLines(memory.Raw);
        if (dropped > 0)
            _monitor?.Log($"Folded {dropped} line(s) for {memory.Npc} that were already contained in a longer line at the same minute.", LogLevel.Trace);

        int max = Math.Max(50, _config.MaxRawEventsPerNpc);
        if (memory.Raw.Count <= max)
            return;

        int drop = memory.Raw.Count - max;
        // Only ever drop events that are already folded into long-term memory.
        List<RawEvent> droppable = memory.Raw
            .Take(drop)
            .Where(e => WeekIndexOf(e) <= memory.CompressedThroughWeek)
            .ToList();
        if (droppable.Count > 0)
        {
            memory.Raw.RemoveAll(e => droppable.Contains(e));
            _monitor?.Log($"Trimmed {droppable.Count} already-compressed raw events for {memory.Npc}.", LogLevel.Trace);
        }
    }

    /// <summary>Uncompressed events belonging to weeks that have already finished.</summary>
    public List<RawEvent> FinishedUncompressedEvents(NpcMemory memory, long currentWeek)
        => memory.Raw
            .Where(e => WeekIndexOf(e) < currentWeek && WeekIndexOf(e) > memory.CompressedThroughWeek)
            .OrderBy(e => e.Day)
            .ThenBy(e => e.Time)
            .ToList();

    /// <summary>This week's raw, timestamped lines (short-term memory).</summary>
    public List<RawEvent> ShortTermEvents(NpcMemory memory)
        => memory.Raw
            .Where(e => WeekIndexOf(e) > memory.CompressedThroughWeek)
            .OrderBy(e => e.Day)
            .ThenBy(e => e.Time)
            .ToList();

    public void LoadFromSave()
    {
        _byNpc.Clear();
        List<string> index = null;
        try
        {
            index = _helper.Data.ReadSaveData<List<string>>(IndexKey);
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Failed reading memory index: {ex.Message}", LogLevel.Warn);
        }

        if (index == null)
            return;

        foreach (string npc in index)
        {
            try
            {
                NpcMemory memory = _helper.Data.ReadSaveData<NpcMemory>(KeyFor(npc));
                if (memory == null)
                    continue;
                if (string.IsNullOrWhiteSpace(memory.Npc))
                    memory.Npc = npc;
                _byNpc[memory.Npc] = memory;
            }
            catch (Exception ex)
            {
                _monitor?.Log($"Failed reading memory for {npc}: {ex.Message}", LogLevel.Warn);
            }
        }

        _monitor?.Log($"Loaded memory for {_byNpc.Count} NPCs from save.", LogLevel.Trace);
    }

    public void SaveToSave()
    {
        try
        {
            // Persist under the canonical NPC name, not the case-insensitive dictionary key.
            foreach (NpcMemory memory in _byNpc.Values)
                _helper.Data.WriteSaveData(KeyFor(memory.Npc), memory);
            _helper.Data.WriteSaveData(
                IndexKey,
                _byNpc.Values.Select(m => m.Npc).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Failed writing memory to save: {ex.Message}", LogLevel.Error);
        }
    }

    public void Reset(string npc)
    {
        if (npc == null)
            _byNpc.Clear();
        else
            _byNpc.Remove(npc);
    }

    // ---- disk mirror of the compressed weeks ----

    private string BackupPath
    {
        get
        {
            string folder = Constants.SaveFolderName ?? "unknown";
            var chars = folder.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray();
            string safe = new string(chars);
            return Path.Combine(_helper.DirectoryPath, $"memory-{(safe.Length > 0 ? safe : "unknown")}.json");
        }
    }

    /// <summary>Mirrors the derived memory (weekly summaries) to a JSON file next to the mod.</summary>
    public void SaveBackup()
    {
        try
        {
            var payload = new MemoryBackup
            {
                SaveFolder = Constants.SaveFolderName ?? "",
                SavedUtc = DateTime.UtcNow.ToString("o"),
                Npcs = _byNpc.Values
                    .Where(m => m.Weeks.Count > 0 || m.Backfilled)
                    .Select(m => new NpcMemoryBackup
                    {
                        Npc = m.Npc,
                        Weeks = m.Weeks,
                        CompressedThroughWeek = m.CompressedThroughWeek,
                        Backfilled = m.Backfilled
                    })
                    .ToList()
            };

            File.WriteAllText(BackupPath, JsonConvert.SerializeObject(payload, Formatting.Indented), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Could not write memory mirror: {ex.Message}", LogLevel.Trace);
        }
    }

    /// <summary>
    /// Restores weekly summaries from the mirror when the save file does not have them yet.
    /// Raw events are deliberately not mirrored — they are re-read from ValleyTalk's own history.
    /// </summary>
    public int LoadBackup()
    {
        try
        {
            if (!File.Exists(BackupPath))
                return 0;

            var payload = JsonConvert.DeserializeObject<MemoryBackup>(File.ReadAllText(BackupPath));
            if (payload?.Npcs == null)
                return 0;
            if (!string.Equals(payload.SaveFolder, Constants.SaveFolderName, StringComparison.Ordinal))
            {
                _monitor?.Log($"Memory mirror belongs to save '{payload.SaveFolder}', not the loaded one; ignoring.", LogLevel.Trace);
                return 0;
            }

            int restored = 0;
            foreach (NpcMemoryBackup saved in payload.Npcs)
            {
                if (string.IsNullOrWhiteSpace(saved.Npc))
                    continue;

                NpcMemory memory = GetOrCreate(saved.Npc);
                if (string.IsNullOrWhiteSpace(memory.Npc))
                    memory.Npc = saved.Npc;

                int before = memory.Weeks.Count;
                foreach (WeeklyMemory week in saved.Weeks)
                {
                    if (!memory.Weeks.Any(w => w.WeekIndex == week.WeekIndex))
                        memory.Weeks.Add(week);
                }
                memory.Weeks = memory.Weeks.OrderBy(w => w.WeekIndex).ToList();
                memory.CompressedThroughWeek = Math.Max(memory.CompressedThroughWeek, saved.CompressedThroughWeek);
                memory.Backfilled |= saved.Backfilled;

                if (memory.Weeks.Count > before)
                    restored += memory.Weeks.Count - before;
            }

            if (restored > 0)
                _monitor?.Log($"Restored {restored} compressed week(s) from the memory mirror.", LogLevel.Info);
            return restored;
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Could not read memory mirror: {ex.Message}", LogLevel.Warn);
            return 0;
        }
    }

    private static string KeyFor(string npc)
    {
        var chars = npc.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.').ToArray();
        string safe = new string(chars);
        if (safe.Length > 40)
            safe = safe.Substring(0, 40);
        return KeyRoot + (safe.Length > 0 ? safe : "unnamed");
    }
}
