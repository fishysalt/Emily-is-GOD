using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace ValleyTalkMemory;

/// <summary>
/// The actual work behind the debug commands, extracted so it can be driven either from the SMAPI
/// console or from a watched command file (<see cref="ModConfig.DevCommandFile"/>).
///
/// The command file exists so the whole schedule feature can be exercised unattended: write a line,
/// the mod runs it on its next tick, and the result lands in the log.
/// </summary>
internal static class DevActions
{
    public static void Status(ModEntry mod, IMonitor monitor, string filter)
    {
        monitor.Log($"current in-game week: {GameWeek.DescribeWeek(GameWeek.CurrentWeek(mod.Config.DaysPerWeek), mod.Config.DaysPerWeek)}", LogLevel.Info);
        monitor.Log($"NPCs with memory: {mod.Store.KnownNpcs.Count()}", LogLevel.Info);
        foreach (NpcMemory memory in mod.Store.All
                     .Where(m => filter == null || m.Npc.Equals(filter, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(m => m.Raw.Count))
        {
            List<RawEvent> shortTerm = mod.Store.ShortTermEvents(memory);
            monitor.Log($"  {memory.Npc,-16} raw={memory.Raw.Count,4}  shortTerm={shortTerm.Count,3}  weeks={memory.Weeks.Count,3}  compressedThrough={memory.CompressedThroughWeek}", LogLevel.Info);
        }
    }

    public static void ShowSchedule(string npcName, IMonitor monitor)
    {
        NPC npc = Game1.getCharacterFromName(npcName);
        if (npc == null)
        {
            monitor.Log($"NPC '{npcName}' not found.", LogLevel.Warn);
            return;
        }

        monitor.Log($"{npc.Name}: now={Game1.timeOfDay}, scheduleKey='{npc.ScheduleKey}', "
                    + $"at {npc.currentLocation?.Name} ({npc.TilePoint.X},{npc.TilePoint.Y}), "
                    + $"ignoreScheduleToday={npc.ignoreScheduleToday}, followSchedule={npc.followSchedule}", LogLevel.Info);

        List<string> lines = NpcScheduler.DescribeRemaining(npc).ToList();
        monitor.Log(lines.Count == 0 ? "  (nothing left today)" : string.Join(Environment.NewLine, lines), LogLevel.Info);

        // What ValleyTalk will tell the model about her own plans — this is how "does she know?"
        // reaches the prompt.
        if (npc.Schedule != null)
        {
            List<string> future = npc.Schedule
                .Where(kv => kv.Key >= Game1.timeOfDay)
                .OrderBy(kv => kv.Key)
                .Select(kv => kv.Value.targetLocationName)
                .ToList();
            monitor.Log($"  ValleyTalk will prompt her with: Later today she plans to go to: {(future.Count > 0 ? string.Join(", ", future) : "(nothing)")}", LogLevel.Info);
        }
    }

    /// <summary>Explains why an NPC does or does not have a schedule right now.</summary>
    public static void Diagnose(string npcName, IMonitor monitor)
    {
        NPC npc = Game1.getCharacterFromName(npcName);
        if (npc == null)
        {
            monitor.Log($"NPC '{npcName}' not found.", LogLevel.Warn);
            return;
        }

        bool festival = false;
        try { festival = Utility.isFestivalDay(); } catch { }

        monitor.Log($"[diag] {npc.Name}: date=Y{Game1.Date.Year} {Game1.Date.Season} D{Game1.Date.DayOfMonth}, "
                    + $"time={Game1.timeOfDay}, festivalDay={festival}, eventUp={Game1.eventUp}, currentEvent={Game1.CurrentEvent != null}", LogLevel.Info);
        monitor.Log($"[diag] married={npc.isMarried()}, playerSpouse='{Game1.player.spouse}', "
                    + $"Schedule={(npc.Schedule == null ? "null" : npc.Schedule.Count + " entries")}, "
                    + $"scheduleKey='{npc.ScheduleKey}', followSchedule={npc.followSchedule}, ignoreScheduleToday={npc.ignoreScheduleToday}, "
                    + $"scheduleDelaySeconds={npc.scheduleDelaySeconds}", LogLevel.Info);
        monitor.Log($"[diag] DefaultMap='{npc.DefaultMap}', DefaultPosition=({npc.DefaultPosition.X},{npc.DefaultPosition.Y}) "
                    + $"-> tile ({(int)(npc.DefaultPosition.X / 64f)},{(int)(npc.DefaultPosition.Y / 64f)})", LogLevel.Info);

        try
        {
            Dictionary<string, string> raw = npc.getMasterScheduleRawData();
            if (raw == null)
            {
                monitor.Log("[diag] schedule asset for this NPC is EMPTY/null.", LogLevel.Warn);
            }
            else
            {
                List<string> keys = raw.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                monitor.Log($"[diag] '{npc.Name}' schedule asset has {keys.Count} keys: {string.Join(", ", keys.Take(40))}", LogLevel.Info);
                foreach (string probe in new[] { $"marriage_{npc.Name}", $"{Game1.currentSeason}_{Game1.Date.DayOfWeek}", "spring", "rain" })
                {
                    if (raw.TryGetValue(probe, out string script))
                        monitor.Log($"[diag]   key '{probe}' = {script.Substring(0, Math.Min(180, script.Length))}", LogLevel.Info);
                }
            }
        }
        catch (Exception ex)
        {
            monitor.Log($"[diag] could not read the schedule asset: {ex.Message}", LogLevel.Warn);
        }

        try
        {
            bool reloaded = npc.TryLoadSchedule();
            monitor.Log($"[diag] TryLoadSchedule() returned {reloaded}; Schedule is now "
                        + $"{(npc.Schedule == null ? "still null" : npc.Schedule.Count + " entries, key='" + npc.ScheduleKey + "'")}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            monitor.Log($"[diag] TryLoadSchedule() threw: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>Dumps the animation keys a schedule entry may use as its end-of-route behaviour.</summary>
    public static void DumpAnimations(IMonitor monitor)
    {
        try
        {
            var anims = DataLoader.AnimationDescriptions(Game1.content);
            if (anims == null) { monitor.Log("[diag] AnimationDescriptions is null", LogLevel.Warn); return; }
            monitor.Log($"[diag] Data/AnimationDescriptions has {anims.Count} keys: {string.Join(", ", anims.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))}", LogLevel.Info);
        }
        catch (Exception ex) { monitor.Log($"[diag] could not read AnimationDescriptions: {ex.Message}", LogLevel.Warn); }
    }

    /// <summary>Manual promise injection. <paramref name="args"/> = [npc, location, time, until?, x?, y?]</summary>
    public static void Goto(IReadOnlyList<string> args, IMonitor monitor)
    {
        if (args.Count < 3)
        {
            monitor.Log("usage: goto <npc> <location> <time> [untilTime] [tileX tileY]", LogLevel.Info);
            return;
        }

        NPC npc = Game1.getCharacterFromName(args[0]);
        if (npc == null)
        {
            monitor.Log($"NPC '{args[0]}' not found.", LogLevel.Warn);
            return;
        }

        if (!NpcScheduler.TryParseClock(args[2], out int arrive))
        {
            monitor.Log($"Could not read time '{args[2]}'. Use 20:00 or 2000.", LogLevel.Warn);
            return;
        }

        int until = 0;
        int tileX = 0;
        int tileY = 0;

        List<string> rest = args.Skip(3).ToList();
        if (rest.Count > 0 && rest.Count != 2 && NpcScheduler.TryParseClock(rest[0], out int parsedUntil))
        {
            until = parsedUntil;
            rest.RemoveAt(0);
        }
        if (rest.Count >= 2 && int.TryParse(rest[0], out int px) && int.TryParse(rest[1], out int py))
        {
            tileX = px;
            tileY = py;
        }

        if (tileX == 0 && tileY == 0)
        {
            if (!NpcScheduler.TryGuessTile(args[1], out tileX, out tileY))
            {
                monitor.Log($"Could not guess a tile for '{args[1]}'; pass one explicitly.", LogLevel.Warn);
                return;
            }
            monitor.Log($"Guessed tile for {args[1]}: ({tileX},{tileY})", LogLevel.Info);
        }

        var promise = new SchedulePromise
        {
            Npc = npc.Name,
            Location = args[1],
            TileX = tileX,
            TileY = tileY,
            Facing = 2,
            Day = GameWeek.Today,
            ArriveTime = arrive,
            UntilTime = until,
            SourceText = "manual goto",
            CreatedUtc = DateTime.UtcNow.ToString("o")
        };

        monitor.Log($"Applying: {npc.Name} -> {args[1]} ({tileX},{tileY}) at {arrive}{(until > 0 ? $", home at {until}" : "")}", LogLevel.Info);
        string error = NpcScheduler.Apply(promise, monitor);
        if (error != null)
        {
            monitor.Log($"Could not apply: {error}", LogLevel.Warn);
            return;
        }

        monitor.Log("Applied. Rewritten day:", LogLevel.Info);
        ShowSchedule(npc.Name, monitor);
    }

    /// <summary>
    /// Parses one line from the dev command file. Deliberately tiny: "status", "schedule &lt;npc&gt;",
    /// "goto &lt;npc&gt; &lt;loc&gt; &lt;time&gt; [until] [x y]".
    /// </summary>
    public static void RunDevLine(string line, ModEntry mod, IMonitor monitor)
    {
        string trimmed = (line ?? "").Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith("#"))
            return;

        List<string> parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        string verb = parts[0].ToLowerInvariant();
        parts.RemoveAt(0);

        monitor.Log($"[devcmd] {trimmed}", LogLevel.Info);
        switch (verb)
        {
            case "status":
                Status(mod, monitor, parts.FirstOrDefault());
                break;
            case "schedule":
                if (parts.Count == 0) monitor.Log("usage: schedule <npc>", LogLevel.Info);
                else ShowSchedule(parts[0], monitor);
                break;
            case "anims":
                DumpAnimations(monitor);
                break;
            case "diag":
                if (parts.Count == 0) monitor.Log("usage: diag <npc>", LogLevel.Info);
                else Diagnose(parts[0], monitor);
                break;
            case "goto":
                Goto(parts, monitor);
                break;
            case "reload":
                mod.PollLiveHistory();
                mod.Injector.RefreshAll();
                monitor.Log("Reloaded and refreshed.", LogLevel.Info);
                break;
            default:
                monitor.Log($"Unknown dev command '{verb}'.", LogLevel.Warn);
                break;
        }
    }
}
