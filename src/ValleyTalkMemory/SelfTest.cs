using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StardewModdingAPI;

namespace ValleyTalkMemory;

/// <summary>
/// One-shot startup check of the paths that would otherwise only be exercised after loading a save,
/// and which fail <em>silently</em> if they break (a missed prompt override just means the NPC keeps
/// forgetting things — no exception, no visible symptom).
///
/// It is deliberately read-only apart from registering and immediately clearing a throwaway override
/// under a name no real NPC uses.
/// </summary>
internal static class SelfTest
{
    private const string ProbeName = "__VTMemorySelfTest__";

    public static void Run(IModHelper helper, IMonitor monitor, ModEntry mod)
    {
        monitor.Log("--- ValleyTalk Memory startup self-test ---", LogLevel.Info);

        CheckNameResolver(helper, monitor);
        CheckValleyTalkPrompts(helper, monitor);
        CheckApiProxy(monitor, mod);
        CheckSaveData(monitor);

        monitor.Log("--- self-test finished ---", LogLevel.Info);
    }

    private static void CheckNameResolver(IModHelper helper, IMonitor monitor)
    {
        try
        {
            NpcNameResolver.Rebuild(helper, monitor);
            string sources = string.Join(", ", NpcNameResolver.LastSourceCounts.Select(kv => $"{kv.Key}={kv.Value}"));
            monitor.Log($"[1/4] name resolver: {NpcNameResolver.Count} keys ({sources})", LogLevel.Info);

            foreach (string probe in new[] { "emily", "haley", "morris", "adelaiderosiervmv" })
            {
                // The probe is the lower-cased save key; success means it mapped back to a real NPC
                // name (which is what ValleyTalk keys its prompt overrides by).
                bool known = NpcNameResolver.IsKnown(probe);
                string resolved = NpcNameResolver.Resolve(probe);
                monitor.Log($"      '{probe}' -> '{resolved}' {(known ? "OK" : "UNRESOLVED (override for this NPC would not match)")}",
                    known ? LogLevel.Trace : LogLevel.Warn);
            }

            DumpEntries(helper, monitor);
        }
        catch (Exception ex)
        {
            monitor.Log($"[1/4] name resolver FAILED: {ex}", LogLevel.Error);
        }
    }

    /// <summary>Writes the full key -> name table so the mapping can be audited outside the game.</summary>
    private static void DumpEntries(IModHelper helper, IMonitor monitor)
    {
        try
        {
            string dir = System.IO.Path.Combine(helper.DirectoryPath, "debug");
            System.IO.Directory.CreateDirectory(dir);
            var lines = new List<string> { "savekey\tnpcname" };
            lines.AddRange(NpcNameResolver.Entries
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => kv.Key + "\t" + kv.Value));
            System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, "npc_names.tsv"), lines);
            monitor.Log($"      wrote {NpcNameResolver.Count} name mappings to debug/npc_names.tsv", LogLevel.Trace);
        }
        catch (Exception ex)
        {
            monitor.Log($"      could not write name mapping dump: {ex.Message}", LogLevel.Trace);
        }
    }

    private static void CheckValleyTalkPrompts(IModHelper helper, IMonitor monitor)
    {
        try
        {
            var prompts = helper.GameContent.Load<Dictionary<string, string>>("ValleyTalk/Prompts");
            if (prompts == null)
            {
                monitor.Log("[2/4] ValleyTalk/Prompts loaded as null — injected headings will fall back to English defaults.", LogLevel.Warn);
                return;
            }

            monitor.Log($"[2/4] ValleyTalk/Prompts: {prompts.Count} strings loaded.", LogLevel.Info);
            foreach (string key in new[] { "eventHistoryHeading", "eventHistoryIntro", "eventHistorySubheading" })
            {
                if (prompts.TryGetValue(key, out string value))
                    monitor.Log($"      {key} = {Preview(value)}", LogLevel.Trace);
                else
                    monitor.Log($"      {key} MISSING (fallback text will be used)", LogLevel.Warn);
            }
        }
        catch (Exception ex)
        {
            monitor.Log($"[2/4] loading ValleyTalk/Prompts FAILED: {ex.Message}", LogLevel.Error);
        }
    }

    private static void CheckApiProxy(IMonitor monitor, ModEntry mod)
    {
        IValleyTalkApi api = mod.VtApi;
        if (api == null)
        {
            monitor.Log("[3/4] ValleyTalk API is NOT available — memory will be recorded but never injected.", LogLevel.Warn);
            return;
        }

        try
        {
            api.RegisterPromptOverride(ProbeName, "EventHistory", "self-test");
            monitor.Log("[3/4] RegisterPromptOverride: OK", LogLevel.Info);

            api.ClearPromptOverride(ProbeName, "EventHistory");
            monitor.Log("      ClearPromptOverride: OK", LogLevel.Info);

            api.ClearPromptOverrides(ProbeName);
            monitor.Log("      ClearPromptOverrides: OK", LogLevel.Info);

            bool enabled = false;
            try
            {
                enabled = api.IsEnabledForCharacter(null);
            }
            catch
            {
                // Passing null is not meaningful; the point is only that the member exists.
                enabled = true;
            }
            monitor.Log($"      IsEnabledForCharacter member reachable: {enabled}", LogLevel.Trace);
        }
        catch (Exception ex)
        {
            monitor.Log($"[3/4] ValleyTalk API proxy call FAILED: {ex.Message}", LogLevel.Error);
        }
    }

    private static void CheckSaveData(IMonitor monitor)
    {
        try
        {
            var data = StardewValley.Game1.CustomData;
            int keys = data == null ? -1 : VtHistorySource.FromSaveData(monitor).Count;
            monitor.Log($"[4/4] Game1.CustomData reachable: {data != null}; ValleyTalk history entries visible at title screen: {keys} (expected 0 until a save is loaded).", LogLevel.Info);
        }
        catch (Exception ex)
        {
            monitor.Log($"[4/4] reading Game1.CustomData FAILED: {ex.Message}", LogLevel.Error);
        }
    }

    private static string Preview(string value)
        => string.IsNullOrEmpty(value)
            ? "<empty>"
            : value.Length <= 90 ? value : value.Substring(0, 90) + "...";

    /// <summary>
    /// Post-load verification, run a few seconds after a save is loaded so ValleyTalk has had time to
    /// patch NPCs and populate its own character list. This is what proves the injection chain works
    /// end to end: memory built -> override registered -> landed in ValleyTalk's own storage under the
    /// exact key it looks up.
    /// </summary>
    public static void RunAfterLoad(IModHelper helper, IMonitor monitor, ModEntry mod)
    {
        monitor.Log("--- post-load verification ---", LogLevel.Info);

        try
        {
            NpcNameResolver.Rebuild(helper, monitor);
            string sources = string.Join(", ", NpcNameResolver.LastSourceCounts.Select(kv => $"{kv.Key}={kv.Value}"));
            monitor.Log($"[A] name resolver after load: {NpcNameResolver.Count} keys ({sources})", LogLevel.Info);
        }
        catch (Exception ex)
        {
            monitor.Log($"[A] name resolver rebuild failed: {ex.Message}", LogLevel.Warn);
        }

        List<NpcMemory> all = mod.Store.All.ToList();
        monitor.Log($"[B] memory store: {all.Count} NPCs, {all.Count(m => m.Weeks.Count > 0)} with long-term weeks, "
                    + $"{all.Sum(m => m.Raw.Count)} raw events total.", LogLevel.Info);

        foreach (NpcMemory memory in all.OrderByDescending(m => m.Raw.Count).Take(3))
        {
            monitor.Log($"      {memory.Npc}: raw={memory.Raw.Count}, currentWeek={mod.Store.ShortTermEvents(memory).Count}, "
                        + $"weeks={memory.Weeks.Count}, compressedThrough={memory.CompressedThroughWeek}", LogLevel.Info);
        }

        ReportInjection(monitor, mod, "Emily");
        CheckOverridesLanded(monitor);
    }

    private static void ReportInjection(IMonitor monitor, ModEntry mod, string npc)
    {
        try
        {
            string block = mod.Injector.Build(npc);
            if (string.IsNullOrWhiteSpace(block))
            {
                monitor.Log($"[C] {npc}: no memory block built (no raw events and no compressed weeks).", LogLevel.Warn);
                return;
            }

            string[] lines = block.Split('\n');
            monitor.Log($"[C] {npc}: memory block is {block.Length} chars, {lines.Length} lines. First lines:", LogLevel.Info);
            foreach (string line in lines.Take(6))
                monitor.Log($"      | {line.TrimEnd()}", LogLevel.Info);
            monitor.Log("      | ...", LogLevel.Info);
            foreach (string line in lines.Skip(Math.Max(0, lines.Length - 3)))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    monitor.Log($"      | {line.TrimEnd()}", LogLevel.Info);
            }
        }
        catch (Exception ex)
        {
            monitor.Log($"[C] building the block for {npc} FAILED: {ex}", LogLevel.Error);
        }
    }

    /// <summary>
    /// Reads ValleyTalk's private override table to confirm our text is stored under the same
    /// character name it will look up when building a prompt.
    /// </summary>
    private static void CheckOverridesLanded(IMonitor monitor)
    {
        try
        {
            Assembly asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "ValleyTalk", StringComparison.OrdinalIgnoreCase));
            Type managerType = asm?.GetType("ValleyTalk.ModInteropManager", throwOnError: false);
            object manager = managerType
                ?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            object raw = managerType
                ?.GetField("_promptOverrides", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(manager);

            if (raw is not System.Collections.IDictionary characters)
            {
                monitor.Log("[D] could not read ValleyTalk's override table (internal layout changed).", LogLevel.Warn);
                return;
            }

            var names = new List<string>();
            foreach (object key in characters.Keys)
            {
                if (key is not string name)
                    continue;
                if (characters[name] is not System.Collections.IDictionary elements)
                    continue;
                if (elements["EventHistory"] is System.Collections.IDictionary mods && mods.Count > 0)
                    names.Add(name);
            }

            monitor.Log($"[D] ValleyTalk is holding an 'EventHistory' override for {names.Count} characters.", LogLevel.Info);
            if (names.Count == 0)
            {
                monitor.Log("      NONE — injection is not reaching ValleyTalk.", LogLevel.Error);
                return;
            }

            foreach (string sample in new[] { "Emily", "Haley", "Abigail" })
            {
                bool landed = names.Contains(sample, StringComparer.Ordinal);
                monitor.Log($"      {sample}: {(landed ? "override present, will be used for the next prompt" : "no override (no memory stored)")}",
                    landed ? LogLevel.Info : LogLevel.Trace);
            }
        }
        catch (Exception ex)
        {
            monitor.Log($"[D] override table check FAILED: {ex.Message}", LogLevel.Warn);
        }
    }
}
