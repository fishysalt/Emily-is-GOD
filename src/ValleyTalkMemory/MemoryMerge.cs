using System;
using System.Collections.Generic;
using System.Linq;

namespace ValleyTalkMemory;

/// <summary>
/// Pure merge/dedup logic, shared by the live memory store and the offline test harness.
/// ValleyTalk logs the same spoken line in more than one history bucket (dialogue log,
/// conversation log, overheard log), so deduplication is what keeps prompts small.
/// </summary>
internal static class MemoryMerge
{
    /// <summary>Adds events not already present. Returns how many were added.</summary>
    public static int Merge(List<RawEvent> target, IEnumerable<RawEvent> incoming)
    {
        var seen = new HashSet<string>(target.Select(e => e.DedupKey), StringComparer.Ordinal);
        int added = 0;
        foreach (RawEvent e in incoming)
        {
            if (string.IsNullOrWhiteSpace(e.Text) || !seen.Add(e.DedupKey))
                continue;
            target.Add(e);
            added++;
        }
        return added;
    }

    public static void SortByTime(List<RawEvent> events)
    {
        events.Sort((a, b) =>
        {
            int c = a.Day.CompareTo(b.Day);
            return c != 0 ? c : a.Time.CompareTo(b.Time);
        });
    }

    /// <summary>
    /// Removes lines that are fully contained in another line spoken at the same in-game minute.
    ///
    /// ValleyTalk logs the same exchange twice: once as separate lines and once as the whole
    /// generated reply concatenated. Exact-match dedup cannot see that, so without this pass the
    /// prompt budget gets spent repeating the same sentences. Only lines of the same kind are
    /// compared, so the farmer's lines are never swallowed by an NPC line.
    /// </summary>
    /// <summary>Non-mutating variant, used at render/compression time so storage stays complete.</summary>
    public static List<RawEvent> WithoutCoveredLines(IEnumerable<RawEvent> source)
    {
        List<RawEvent> copy = source.ToList();
        DropCoveredLines(copy);
        return copy;
    }

    /// <returns>How many lines were dropped.</returns>
    public static int DropCoveredLines(List<RawEvent> raw)
    {
        if (raw.Count < 2)
            return 0;

        var toDrop = new HashSet<RawEvent>();
        foreach (IGrouping<(long Day, int Time, string Kind), RawEvent> group in
                 raw.GroupBy(e => (e.Day, e.Time, e.Kind)))
        {
            List<RawEvent> lines = group.ToList();
            if (lines.Count < 2)
                continue;

            foreach (RawEvent candidate in lines)
            {
                if (candidate.Text.Length == 0)
                    continue;
                if (lines.Any(other => !ReferenceEquals(other, candidate)
                                       && other.Text.Length > candidate.Text.Length
                                       && other.Text.Contains(candidate.Text, StringComparison.Ordinal)))
                {
                    toDrop.Add(candidate);
                }
            }
        }

        if (toDrop.Count == 0)
            return 0;

        raw.RemoveAll(toDrop.Contains);
        return toDrop.Count;
    }
}
