using System.Collections.Generic;
using Newtonsoft.Json;

namespace ValleyTalkMemory;

/// <summary>One remembered line/exchange, anchored to an in-game date+time.</summary>
internal sealed class RawEvent
{
    /// <summary>Absolute in-game day (0 = Spring 1, Year 1).</summary>
    public long Day { get; set; }

    /// <summary>Stardew clock value, e.g. 630 = 06:30.</summary>
    public int Time { get; set; }

    /// <summary>say (this NPC spoke) | farmer (the player spoke) | overheard | event</summary>
    public string Kind { get; set; } = "say";

    /// <summary>Who spoke; only meaningful for overheard history.</summary>
    public string Speaker { get; set; } = "";

    public string Text { get; set; } = "";

    /// <summary>
    /// Deliberately ignores <see cref="Kind"/>/<see cref="Speaker"/>: ValleyTalk records the same
    /// spoken line in several buckets (dialogue log + conversation log + overheard log), and we only
    /// want to remember it once.
    /// </summary>
    [JsonIgnore]
    public string DedupKey => $"{Day}|{Time}|{Text.Trim()}";
}

/// <summary>A compressed summary of one finished in-game week.</summary>
internal sealed class WeeklyMemory
{
    public long WeekIndex { get; set; }

    public string Range { get; set; } = "";

    public string Summary { get; set; } = "";

    public List<string> Facts { get; set; } = new List<string>();

    /// <summary>Things this NPC said they would do — the seed for the future schedule feature.</summary>
    public List<string> Promises { get; set; } = new List<string>();

    public string Relationship { get; set; } = "";

    public string CreatedUtc { get; set; } = "";
}

/// <summary>Everything we remember about one NPC.</summary>
internal sealed class NpcMemory
{
    public string Npc { get; set; } = "";

    public List<RawEvent> Raw { get; set; } = new List<RawEvent>();

    public List<WeeklyMemory> Weeks { get; set; } = new List<WeeklyMemory>();

    /// <summary>Highest week index already folded into <see cref="Weeks"/>.</summary>
    public long CompressedThroughWeek { get; set; } = -1;

    /// <summary>Set once the existing ValleyTalk backlog has been back-filled (or skipped).</summary>
    public bool Backfilled { get; set; }

    /// <summary>Commitments not yet acted on. Persisted so "tomorrow" survives the overnight rebuild.</summary>
    public List<SchedulePromise> Promises { get; set; } = new List<SchedulePromise>();
}

/// <summary>
/// Disk mirror of the <em>derived</em> memory only (compressed weeks), written next to the mod.
///
/// Raw events can always be re-read from ValleyTalk's own history, but weekly summaries are LLM
/// output and cannot be recreated. Mirroring them means a lost or overwritten save-data entry costs
/// nothing, and it also makes the compression result inspectable outside the game.
/// </summary>
internal sealed class MemoryBackup
{
    public string SaveFolder { get; set; } = "";

    public string SavedUtc { get; set; } = "";

    public List<NpcMemoryBackup> Npcs { get; set; } = new List<NpcMemoryBackup>();
}

internal sealed class NpcMemoryBackup
{
    public string Npc { get; set; } = "";

    public List<WeeklyMemory> Weeks { get; set; } = new List<WeeklyMemory>();

    public long CompressedThroughWeek { get; set; } = -1;

    public bool Backfilled { get; set; }

    public List<SchedulePromise> Promises { get; set; } = new List<SchedulePromise>();
}
