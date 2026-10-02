using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewValley;

namespace ValleyTalkMemory;

/// <summary>
/// Two sources of ValleyTalk history:
///  1. the saved copy in <see cref="Game1"/>'s CustomData (valid right after loading a save);
///  2. ValleyTalk's live in-memory cache (private), which is what actually updates during play.
///     ValleyTalk only flushes to the save file when the game saves, so reading only (1) would
///     give us stale data for the whole session.
/// </summary>
internal static class VtHistorySource
{
    private const string SaveDataPrefix = "smapi/mod-data/dandm1.valleytalk/eventhistory_";
    private const string LiveKeyPrefix = "EventHistory_";

    /// <summary>Raw (npc name, json) pairs from the save file.</summary>
    public static Dictionary<string, string> FromSaveData(IMonitor monitor)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IDictionary<string, string> data;
        try
        {
            data = Game1.CustomData;
        }
        catch (Exception ex)
        {
            monitor?.Log($"Could not read Game1.CustomData: {ex.Message}", StardewModdingAPI.LogLevel.Warn);
            return result;
        }

        if (data == null)
            return result;

        foreach (KeyValuePair<string, string> kvp in data)
        {
            if (kvp.Key == null || !kvp.Key.StartsWith(SaveDataPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            string npc = kvp.Key.Substring(SaveDataPrefix.Length);
            if (npc.Length > 0 && !string.IsNullOrWhiteSpace(kvp.Value))
                result[npc] = kvp.Value;
        }
        return result;
    }

    /// <summary>Raw (npc name, json) pairs from ValleyTalk's live cache; empty if unavailable.</summary>
    public static Dictionary<string, string> FromLiveCache(IMonitor monitor)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Assembly asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "ValleyTalk", StringComparison.OrdinalIgnoreCase));
            Type readerType = asm?.GetType("ValleyTalk.EventHistoryReader", throwOnError: false);
            if (readerType == null)
                return result;

            object instance = readerType
                .GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (instance == null)
                return result;

            FieldInfo cacheField = readerType.GetField("_saveCache", BindingFlags.NonPublic | BindingFlags.Instance);
            if (cacheField?.GetValue(instance) is not IDictionary cache)
                return result;

            foreach (DictionaryEntry entry in cache)
            {
                if (entry.Key is not string key || entry.Value == null)
                    continue;
                string npc = key.StartsWith(LiveKeyPrefix, StringComparison.OrdinalIgnoreCase)
                    ? key.Substring(LiveKeyPrefix.Length)
                    : key;
                if (npc.Length == 0)
                    continue;
                result[npc] = JsonConvert.SerializeObject(entry.Value);
            }
        }
        catch (Exception ex)
        {
            monitor?.Log($"Could not read ValleyTalk live history cache: {ex.Message}", StardewModdingAPI.LogLevel.Trace);
        }
        return result;
    }
}
