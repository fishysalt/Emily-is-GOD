using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;

namespace ValleyTalkMemory;

/// <summary>
/// Pushes the memory block into ValleyTalk through its public <c>RegisterPromptOverride</c> API,
/// replacing the built-in "EventHistory" prompt element.
///
/// ValleyTalk itself only ever feeds the model the newest ~20 history entries (with an additional
/// 4000 character cap), so anything older is invisible to it. Our block puts compressed weekly
/// summaries in front of the still-raw current week, which is exactly the gap this mod fills.
/// </summary>
internal sealed class PromptInjector
{
    private const string Element = "EventHistory";

    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly ModConfig _config;
    private readonly MemoryStore _store;
    private readonly Dictionary<string, string> _vtStrings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastText = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public PromptInjector(IModHelper helper, IMonitor monitor, ModConfig config, MemoryStore store)
    {
        _helper = helper;
        _monitor = monitor;
        _config = config;
        _store = store;
    }

    public IValleyTalkApi Api { get; set; }

    /// <summary>Pulls ValleyTalk's own localized prompt strings so our block matches its wording.</summary>
    public void LoadValleyTalkStrings()
    {
        _vtStrings.Clear();
        try
        {
            var prompts = _helper.GameContent.Load<Dictionary<string, string>>("ValleyTalk/Prompts");
            if (prompts != null)
            {
                foreach (KeyValuePair<string, string> kvp in prompts)
                    _vtStrings[kvp.Key] = kvp.Value;
            }
            _monitor?.Log($"Loaded {_vtStrings.Count} ValleyTalk prompt strings.", LogLevel.Trace);
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Could not load ValleyTalk/Prompts asset: {ex.Message}", LogLevel.Trace);
        }
    }

    public void RefreshAll()
    {
        // Use each memory's canonical NPC name: the store's dictionary is case-insensitive, so its
        // keys may still be lower-cased save keys, and ValleyTalk matches overrides exactly.
        foreach (NpcMemory memory in _store.All.ToList())
        {
            if (!string.IsNullOrWhiteSpace(memory.Npc))
                Refresh(memory.Npc);
        }
    }

    public void Refresh(string npc)
    {
        if (Api == null || !_config.EnableMod)
            return;

        string text = Build(npc);
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                if (_registered.Remove(npc))
                    Api.ClearPromptOverride(npc, Element);
                _lastText.Remove(npc);
                return;
            }

            // The situation block changes as her day advances, so RefreshAll() runs periodically.
            // Re-registering identical text every few seconds would be wasted work and would spam the
            // debug dump, so skip when nothing actually changed.
            if (_lastText.TryGetValue(npc, out string previous) && previous == text)
                return;

            Api.RegisterPromptOverride(npc, Element, text);
            _registered.Add(npc);
            _lastText[npc] = text;

            if (_config.DebugDumpMemoryFiles)
                Dump(npc, text);
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Could not register memory override for {npc}: {ex.Message}", LogLevel.Warn);
        }
    }

    public void ClearAll()
    {
        if (Api == null)
            return;
        foreach (string npc in _registered.ToList())
        {
            try
            {
                Api.ClearPromptOverride(npc, Element);
            }
            catch
            {
                // ValleyTalk may already be gone (e.g. returning to title); nothing to do.
            }
        }
        _registered.Clear();
        // Must also drop the cached texts: the dictionary is keyed by NPC name only, so a different
        // save whose Emily produced identical text would otherwise be skipped as "unchanged" and end
        // up with no override at all.
        _lastText.Clear();
    }

    /// <summary>Builds the exact text handed to ValleyTalk for one NPC. Null when there is nothing to say.</summary>
    public string Build(string npc)
    {
        _store.TryGet(npc, out NpcMemory memory);
        return MemoryTextBuilder.Build(npc, memory, _store.WeekIndexOf, _vtStrings, _config, BuildSituation(npc));
    }

    /// <summary>
    /// What she knows about her own day right now.
    ///
    /// ValleyTalk already feeds the model the <em>places</em> she still plans to visit
    /// ("Later today she plans to go to: ..."), which is why she automatically knows about a schedule
    /// we inject. What it does not provide is the <em>times</em>, or any hint that today is a
    /// festival — so we supply both, and that is what lets her decline an invitation convincingly.
    /// </summary>
    private string BuildSituation(string npc)
    {
        if (!_config.InjectSelfKnowledge)
            return null;

        NPC character = Game1.getCharacterFromName(npc);
        if (character == null)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("### " + _config.HeaderSituation);

        bool festival = false;
        try
        {
            festival = Utility.isFestivalDay();
        }
        catch
        {
            // Best-effort only.
        }

        if (festival)
            sb.AppendLine("- " + _config.SituationFestival);

        List<string> plans = new List<string>();
        if (character.Schedule != null)
        {
            foreach (KeyValuePair<int, SchedulePathDescription> entry in character.Schedule.OrderBy(kv => kv.Key))
            {
                if (entry.Key < Game1.timeOfDay)
                    continue;
                int time = entry.Key;
                plans.Add($"{time / 100:00}:{time % 100:00} 去 {entry.Value.targetLocationName}");
            }
        }

        if (plans.Count > 0)
        {
            sb.AppendLine($"- {_config.SituationPlans}：{string.Join("；", plans)}");
            sb.AppendLine("- " + _config.SituationMustKeep);
        }
        else if (!festival)
        {
            sb.AppendLine("- " + _config.SituationNoPlan);
        }

        return sb.ToString();
    }

    private void Dump(string npc, string text)
    {
        try
        {
            string dir = Path.Combine(_helper.DirectoryPath, "debug");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"memory_{npc}.txt"), text, Encoding.UTF8);
        }
        catch
        {
            // Debug dumps are best-effort only.
        }
    }
}
