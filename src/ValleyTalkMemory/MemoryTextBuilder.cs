using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ValleyTalkMemory;

/// <summary>
/// Pure text composition for the injected memory block (no SMAPI/game dependencies),
/// so it can be unit-tested offline against real save data.
/// </summary>
internal static class MemoryTextBuilder
{
    public static string Build(
        string npc,
        NpcMemory memory,
        Func<RawEvent, long> weekOf,
        IDictionary<string, string> valleyTalkStrings,
        ModConfig config,
        string situation = null)
    {
        bool hasMemory = memory != null
                         && (memory.Weeks.Count > 0
                             || memory.Raw.Any(e => weekOf(e) > memory.CompressedThroughWeek));

        if (!hasMemory && string.IsNullOrWhiteSpace(situation))
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("## " + Str(valleyTalkStrings, "eventHistoryHeading", "Event history:"));
        sb.AppendLine(Substitute(Str(valleyTalkStrings, "eventHistoryIntro", "{{Name}} is aware of the following recent events and conversations with the farmer."), npc));
        sb.AppendLine(Str(valleyTalkStrings, "eventHistorySubheading", "History:"));

        if (memory != null)
        {
            List<RawEvent> shortTerm = memory.Raw
                .Where(e => weekOf(e) > memory.CompressedThroughWeek)
                .OrderBy(e => e.Day)
                .ThenBy(e => e.Time)
                .ToList();

            AppendLongTerm(sb, memory, config);
            AppendShortTerm(sb, shortTerm, npc, config);
        }

        if (!string.IsNullOrWhiteSpace(situation))
            sb.Append(situation);

        if (!string.IsNullOrWhiteSpace(config.MemoryInstruction))
            sb.AppendLine(config.MemoryInstruction);

        return sb.ToString();
    }

    private static void AppendLongTerm(StringBuilder sb, NpcMemory memory, ModConfig config)
    {
        if (memory.Weeks.Count == 0)
            return;

        var blocks = new List<string>();
        int budget = Math.Max(200, config.LongTermBudgetChars);

        foreach (WeeklyMemory week in memory.Weeks.OrderByDescending(w => w.WeekIndex))
        {
            var one = new StringBuilder();
            string range = string.IsNullOrWhiteSpace(week.Range)
                ? GameWeek.DescribeWeek(week.WeekIndex, config.DaysPerWeek)
                : week.Range;
            one.Append("- [").Append(range).Append("] ").Append(week.Summary);

            if (!string.IsNullOrWhiteSpace(week.Relationship))
                one.Append("（关系：").Append(week.Relationship).Append('）');

            if (week.Facts.Count > 0)
                one.Append("\n  - ").Append(config.HeaderFacts).Append('：').Append(string.Join("；", week.Facts));

            if (week.Promises.Count > 0)
                one.Append("\n  - ").Append(config.HeaderPromises).Append('：').Append(string.Join("；", week.Promises));

            string block = one.ToString();
            if (block.Length > budget && blocks.Count > 0)
                break;

            blocks.Add(block);
            budget -= block.Length;
            if (budget <= 0)
                break;
        }

        if (blocks.Count == 0)
            return;

        sb.AppendLine("### " + config.HeaderLongTerm);
        for (int i = blocks.Count - 1; i >= 0; i--)
            sb.AppendLine(blocks[i]);
    }

    private static void AppendShortTerm(StringBuilder sb, List<RawEvent> shortTerm, string npc, ModConfig config)
    {
        if (shortTerm.Count == 0)
            return;

        int budget = Math.Max(200, config.ShortTermBudgetChars);
        var lines = new List<string>();
        for (int i = shortTerm.Count - 1; i >= 0; i--)
        {
            string line = Format(shortTerm[i], npc, config);
            if (line.Length > budget && lines.Count > 0)
                break;
            lines.Add(line);
            budget -= line.Length;
            if (budget <= 0)
                break;
        }

        if (lines.Count == 0)
            return;

        sb.AppendLine("### " + config.HeaderShortTerm);
        for (int i = lines.Count - 1; i >= 0; i--)
            sb.AppendLine(lines[i]);
    }

    /// <summary>Renders one remembered line, e.g. "- [Y1 Fall D19 06:00] Emily: 早上好".</summary>
    public static string Format(RawEvent e, string npc, ModConfig config)
    {
        string when = GameWeek.Describe(e.Day, e.Time);
        string body = e.Kind switch
        {
            "farmer" => $"{config.LabelFarmer}: {e.Text}",
            "overheard" => $"{config.LabelOverheard} {e.Speaker} 说：{e.Text}",
            "event" => $"{config.LabelEvent}：{e.Text}",
            "sys" => e.Text,
            _ => $"{npc}: {e.Text}"
        };
        return $"- [{when}] {body}";
    }

    private static string Str(IDictionary<string, string> strings, string key, string fallback)
        => strings != null && strings.TryGetValue(key, out string value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    private static string Substitute(string text, string npcName)
        => string.IsNullOrEmpty(text) ? text : text.Replace("{{Name}}", npcName);
}
