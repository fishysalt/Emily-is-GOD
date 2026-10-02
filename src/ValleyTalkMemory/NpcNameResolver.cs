using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using StardewModdingAPI;
using StardewValley;

namespace ValleyTalkMemory;

/// <summary>
/// Maps the sanitized, lower-cased keys ValleyTalk's history is stored under back to real NPC names.
///
/// ValleyTalk persists history via <c>Helper.Data.WriteSaveData("EventHistory_" + GetSaveName(name))</c>,
/// and SMAPI lower-cases save-data keys. So the save file contains <c>eventhistory_emily</c> while
/// ValleyTalk's prompt overrides are keyed by <c>NPC.Name</c> = <c>"Emily"</c>. Without this mapping
/// our overrides would silently never match.
/// </summary>
internal static class NpcNameResolver
{
    private static Dictionary<string, string> _byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _alreadyRetried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> _sourceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
    private static IModHelper _helper;
    private static IMonitor _monitor;

    /// <summary>How many names each source contributed on the last rebuild (diagnostics).</summary>
    public static IReadOnlyDictionary<string, int> LastSourceCounts => _sourceCounts;

    public static int Count => _byKey.Count;

    /// <summary>Snapshot of the whole lookup, for diagnostics.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Entries => _byKey;

    /// <summary>Rebuilds the lookup from every NPC the game currently knows about.</summary>
    public static void Rebuild(IModHelper helper, IMonitor monitor)
    {
        _helper = helper ?? _helper;
        _monitor = monitor ?? _monitor;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;
            string key = SaveKey(name);
            if (key.Length == 0)
                return;
            if (!map.TryGetValue(key, out string existing))
                map[key] = name;
            else if (!string.Equals(existing, name, StringComparison.Ordinal))
                _monitor?.Log($"NPC name collision on history key '{key}': {existing} vs {name}.", LogLevel.Warn);
        }

        _sourceCounts.Clear();

        // Best source: the exact dictionary ValleyTalk keys its prompt overrides by
        // (DialogueBuilder._characters). Every NPC that has any history is guaranteed to be in it,
        // including non-standard actors such as CustomCompanions pets.
        try
        {
            Assembly asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "ValleyTalk", StringComparison.OrdinalIgnoreCase));
            Type builderType = asm?.GetType("ValleyTalk.DialogueBuilder", throwOnError: false);
            object builder = builderType
                ?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            object raw = builderType?
                .GetField("_characters", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(builder);

            if (raw is System.Collections.IDictionary characters)
            {
                foreach (object key in characters.Keys)
                    Add(key as string);
                _sourceCounts["ValleyTalk._characters"] = characters.Count;
            }
            else
            {
                _sourceCounts["ValleyTalk._characters"] = -1;
            }
        }
        catch (Exception ex)
        {
            _sourceCounts["ValleyTalk._characters"] = -2;
            _monitor?.Log($"Could not read ValleyTalk's character list: {ex.Message}", LogLevel.Trace);
        }

        // Primary source: the exact set ValleyTalk itself iterates (DialogueBuilder.PopulateCharacters).
        // Covers content-pack NPCs that are defined but not currently placed in the world.
        try
        {
            if (Game1.characterData is System.Collections.IDictionary characterData)
            {
                foreach (object key in characterData.Keys)
                    Add(key as string);
                _sourceCounts["Game1.characterData"] = characterData.Count;
            }
            else
            {
                _sourceCounts["Game1.characterData"] = -1;
            }
        }
        catch (Exception ex)
        {
            _sourceCounts["Game1.characterData"] = -2;
            _monitor?.Log($"Could not read Game1.characterData for name resolution: {ex.Message}", LogLevel.Trace);
        }

        // NPCs currently instantiated in any loaded location.
        try
        {
            int live = 0;
            foreach (GameLocation location in Game1.locations)
            {
                if (location?.characters == null)
                    continue;
                foreach (NPC npc in location.characters)
                {
                    Add(npc?.Name);
                    live++;
                }
            }
            _sourceCounts["liveLocations"] = live;
        }
        catch (Exception ex)
        {
            _sourceCounts["liveLocations"] = -2;
            _monitor?.Log($"NPC name scan of locations failed: {ex.Message}", LogLevel.Trace);
        }

        // Last resort: every character defined by the game/content packs.
        // Note: Data/Characters is a Dictionary<string, CharacterData>, not <string, string>.
        try
        {
            var data = _helper?.GameContent?.Load<Dictionary<string, StardewValley.GameData.Characters.CharacterData>>("Data/Characters");
            if (data != null)
            {
                foreach (string name in data.Keys)
                    Add(name);
                _sourceCounts["Data/Characters"] = data.Count;
            }
            else
            {
                _sourceCounts["Data/Characters"] = -1;
            }
        }
        catch (Exception ex)
        {
            _sourceCounts["Data/Characters"] = -2;
            _monitor?.Log($"Could not read Data/Characters for name resolution: {ex.Message}", LogLevel.Trace);
        }

        _byKey = map;
        _monitor?.Log($"NPC name resolver built with {map.Count} entries.", LogLevel.Trace);
    }

    /// <summary>Returns the real NPC name for a history key, or the key itself when unknown.</summary>
    public static string Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return key;

        if (_byKey.TryGetValue(key, out string name))
            return name;

        // Unknown key: maybe a new NPC appeared, so refresh once and retry. Each distinct key only
        // triggers one extra rebuild, otherwise polling would rescan every tick.
        if (_alreadyRetried.Add(key))
        {
            Rebuild(_helper, _monitor);
            if (_byKey.TryGetValue(key, out name))
                return name;
        }
        return key;
    }

    /// <summary>True when the key resolved to a real, known NPC.</summary>
    public static bool IsKnown(string key)
        => !string.IsNullOrWhiteSpace(key) && _byKey.ContainsKey(key);

    /// <summary>Faithful copy of ValleyTalk's EventHistoryReader.GetSaveName.</summary>
    public static string SaveKey(string name)
    {
        const string allowed = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-.";
        var value = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (allowed.IndexOf(c) >= 0)
                value.Append(c);
        }

        if (value.Length == 0)
            value.Append(BitConverter.ToString(name.Select(ch => (byte)ch).ToArray()).Replace("-", ""));
        if (value.Length > 50)
            value.Length = 50;
        return value.ToString();
    }
}
