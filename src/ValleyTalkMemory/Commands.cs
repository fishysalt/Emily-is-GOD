using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace ValleyTalkMemory;

/// <summary>In-game console commands, so the memory layer can be verified without playing for weeks.</summary>
internal static class Commands
{
    public static void Register(IModHelper helper, ModEntry mod, IMonitor monitor)
    {
        helper.ConsoleCommands.Add("vtmemory_names", "Show how stored history keys map to real NPC names (diagnostic).", (cmd, args) =>
        {
            NpcNameResolver.Rebuild(helper, monitor);
            var keys = new List<string>();
            foreach (System.Collections.Generic.KeyValuePair<string, string> kvp in VtHistorySource.FromSaveData(monitor))
                keys.Add(kvp.Key);
            foreach (string key in VtHistorySource.FromLiveCache(monitor).Keys)
            {
                if (!keys.Contains(key))
                    keys.Add(key);
            }

            monitor.Log($"{keys.Count} history keys found; {keys.Count(NpcNameResolver.IsKnown)} resolved to known NPCs.", LogLevel.Info);
            foreach (string key in keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                string resolved = NpcNameResolver.Resolve(key);
                bool known = NpcNameResolver.IsKnown(key);
                monitor.Log($"  {key,-28} -> {resolved}{(known ? "" : "   <-- UNRESOLVED, override will not match")}", known ? LogLevel.Info : LogLevel.Warn);
            }
        });

        helper.ConsoleCommands.Add("vtmemory_schedule", "Print an NPC's remaining schedule for today. Usage: vtmemory_schedule <npc>", (cmd, args) =>
        {
            if (args.Length == 0)
            {
                monitor.Log("usage: vtmemory_schedule <npc>", LogLevel.Info);
                return;
            }

            NPC npc = StardewValley.Game1.getCharacterFromName(args[0]);
            if (npc == null)
            {
                monitor.Log($"NPC '{args[0]}' not found.", LogLevel.Warn);
                return;
            }

            monitor.Log($"{npc.Name}: now={StardewValley.Game1.timeOfDay}, scheduleKey='{npc.ScheduleKey}', "
                        + $"at {npc.currentLocation?.Name} ({npc.TilePoint.X},{npc.TilePoint.Y}), "
                        + $"ignoreScheduleToday={npc.ignoreScheduleToday}, followSchedule={npc.followSchedule}", LogLevel.Info);

            var lines = NpcScheduler.DescribeRemaining(npc).ToList();
            monitor.Log(lines.Count == 0 ? "  (nothing left today)" : string.Join(Environment.NewLine, lines), LogLevel.Info);
        });

        helper.ConsoleCommands.Add("vtmemory_goto",
            "Manually send an NPC somewhere. Usage: vtmemory_goto <npc> <location> <time> [untilTime] [tileX tileY]  (time like 20:00 or 2000)",
            (cmd, args) =>
            {
                if (args.Length < 3)
                {
                    monitor.Log("usage: vtmemory_goto <npc> <location> <time> [untilTime] [tileX tileY]", LogLevel.Info);
                    return;
                }

                NPC npc = StardewValley.Game1.getCharacterFromName(args[0]);
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

                // remaining args: [untilTime] [x y]
                var rest = args.Skip(3).ToList();
                if (rest.Count > 0 && NpcScheduler.TryParseClock(rest[0], out int parsedUntil) && rest.Count != 2)
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
                        monitor.Log($"Could not guess a tile for '{args[1]}'; pass one explicitly: vtmemory_goto {args[0]} {args[1]} {args[2]} <x> <y>", LogLevel.Warn);
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
                    SourceText = "manual vtmemory_goto",
                    CreatedUtc = DateTime.UtcNow.ToString("o")
                };

                string error = NpcScheduler.Apply(promise, monitor);
                monitor.Log(error == null
                    ? $"Applied. Run 'vtmemory_schedule {npc.Name}' to see the rewritten day."
                    : $"Could not apply: {error}", error == null ? LogLevel.Info : LogLevel.Warn);
            });

        helper.ConsoleCommands.Add("vtmemory_promises", "List stored schedule promises. Usage: vtmemory_promises [npc]", (cmd, args) =>
        {
            string filter = args.Length > 0 ? args[0] : null;
            int total = 0;
            foreach (NpcMemory memory in mod.Store.All)
            {
                if (filter != null && !memory.Npc.Equals(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (SchedulePromise p in memory.Promises ?? new List<SchedulePromise>())
                {
                    total++;
                    monitor.Log($"  [{GameWeek.Describe(p.Day)}] {p.ArriveTime / 100:00}:{p.ArriveTime % 100:00} -> {p.Location} ({p.TileX},{p.TileY})"
                                + (p.UntilTime > 0 ? $" until {p.UntilTime}" : "") + $"  \u300c{p.SourceText}\u300d", LogLevel.Info);
                }
            }
            monitor.Log(total == 0 ? "No stored promises." : $"{total} promise(s).", LogLevel.Info);
        });

        helper.ConsoleCommands.Add("vtmemory_status", "Show memory store status. Optional: NPC name.", (cmd, args) =>
        {
            string filter = args.Length > 0 ? args[0] : null;
            monitor.Log($"current in-game week: {GameWeek.DescribeWeek(GameWeek.CurrentWeek(mod.Config.DaysPerWeek), mod.Config.DaysPerWeek)}", LogLevel.Info);
            monitor.Log($"NPCs with memory: {mod.Store.KnownNpcs.Count()}", LogLevel.Info);
            foreach (NpcMemory memory in mod.Store.All
                         .Where(m => filter == null || m.Npc.Equals(filter, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(m => m.Raw.Count))
            {
                List<RawEvent> shortTerm = mod.Store.ShortTermEvents(memory);
                monitor.Log($"  {memory.Npc,-16} raw={memory.Raw.Count,4}  shortTerm={shortTerm.Count,3}  weeks={memory.Weeks.Count,3}  compressedThrough={memory.CompressedThroughWeek}", LogLevel.Info);
            }
        });

        helper.ConsoleCommands.Add("vtmemory_dump", "Print stored raw memory for an NPC (optionally only that NPC's short-term lines).", (cmd, args) =>
        {
            if (args.Length == 0)
            {
                monitor.Log("usage: vtmemory_dump <npc> [short]", LogLevel.Info);
                return;
            }
            if (!mod.Store.TryGet(args[0], out NpcMemory memory))
            {
                monitor.Log($"No memory stored for '{args[0]}'.", LogLevel.Warn);
                return;
            }

            bool shortOnly = args.Length > 1 && args[1].Equals("short", StringComparison.OrdinalIgnoreCase);
            List<RawEvent> events = shortOnly ? mod.Store.ShortTermEvents(memory) : memory.Raw;
            monitor.Log($"{memory.Npc}: {events.Count} raw events", LogLevel.Info);
            foreach (RawEvent e in events)
                monitor.Log($"  [{GameWeek.Describe(e.Day, e.Time)}] ({e.Kind}/{e.Speaker}) {e.Text}", LogLevel.Info);

            foreach (WeeklyMemory week in memory.Weeks)
                monitor.Log($"  <long-term {week.Range}> {week.Summary} facts=[{string.Join(" | ", week.Facts)}] promises=[{string.Join(" | ", week.Promises)}]", LogLevel.Info);
        });

        helper.ConsoleCommands.Add("vtmemory_show", "Print the exact memory block injected into ValleyTalk's prompt for an NPC.", (cmd, args) =>
        {
            if (args.Length == 0)
            {
                monitor.Log("usage: vtmemory_show <npc>", LogLevel.Info);
                return;
            }
            string text = mod.Injector.Build(args[0]);
            monitor.Log(text == null ? $"(no memory block for {args[0]})" : text, LogLevel.Info);
        });

        helper.ConsoleCommands.Add("vtmemory_reload", "Re-read ValleyTalk history (saved + live) and refresh all overrides.", (cmd, args) =>
        {
            int live = mod.PollLiveHistory();
            mod.Injector.LoadValleyTalkStrings();
            mod.Injector.RefreshAll();
            monitor.Log($"Reloaded. {live} NPCs had new live history; overrides refreshed for {mod.Store.KnownNpcs.Count()} NPCs.", LogLevel.Info);
        });

        helper.ConsoleCommands.Add("vtmemory_compress", "Force long-term compression of finished weeks. Usage: vtmemory_compress <npc|all>", (cmd, args) =>
        {
            if (args.Length == 0)
            {
                monitor.Log("usage: vtmemory_compress <npc|all>", LogLevel.Info);
                return;
            }
            long currentWeek = GameWeek.CurrentWeek(mod.Config.DaysPerWeek);
            int queued = 0;
            foreach (NpcMemory memory in mod.Store.All.ToList())
            {
                if (!args[0].Equals("all", StringComparison.OrdinalIgnoreCase)
                    && !memory.Npc.Equals(args[0], StringComparison.OrdinalIgnoreCase))
                    continue;

                List<RawEvent> pending = mod.Store.FinishedUncompressedEvents(memory, currentWeek);
                foreach (IGrouping<long, RawEvent> group in pending.GroupBy(mod.Store.WeekIndexOf))
                {
                    mod.Compressor.Queue(memory, group.Key, group.ToList());
                    queued++;
                }
            }
            monitor.Log(queued == 0 ? "Nothing to compress (no finished, uncompressed weeks for that NPC)." : $"Queued {queued} week(s) for compression.", LogLevel.Info);
        });

        helper.ConsoleCommands.Add("vtmemory_backfill", "Compress the entire existing ValleyTalk backlog. Usage: vtmemory_backfill <npc|all>", (cmd, args) =>
        {
            if (args.Length == 0)
            {
                monitor.Log("usage: vtmemory_backfill <npc|all>  (costs one LLM call per NPC-week)", LogLevel.Info);
                return;
            }
            long currentWeek = GameWeek.CurrentWeek(mod.Config.DaysPerWeek);
            int queued = 0;
            foreach (NpcMemory memory in mod.Store.All.ToList())
            {
                if (!args[0].Equals("all", StringComparison.OrdinalIgnoreCase)
                    && !memory.Npc.Equals(args[0], StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (IGrouping<long, RawEvent> group in memory.Raw
                             .Where(e => mod.Store.WeekIndexOf(e) < currentWeek)
                             .GroupBy(mod.Store.WeekIndexOf)
                             .OrderBy(g => g.Key))
                {
                    mod.Compressor.Queue(memory, group.Key, group.OrderBy(x => x.Day).ThenBy(x => x.Time).ToList());
                    queued++;
                }
            }
            monitor.Log(queued == 0
                ? "Nothing to back-fill."
                : $"Queued {queued} historical week(s). They are compressed one at a time in the background "
                  + $"(roughly {queued * 5 / 60.0:0.#} minutes of API calls); watch the log for results.",
                LogLevel.Info);
        });

        helper.ConsoleCommands.Add("vtmemory_reset", "Delete stored memory. Usage: vtmemory_reset <npc|all>", (cmd, args) =>
        {
            if (args.Length == 0)
            {
                monitor.Log("usage: vtmemory_reset <npc|all>", LogLevel.Info);
                return;
            }
            if (args[0].Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                mod.Injector.ClearAll();
                mod.Store.Reset(null);
                monitor.Log("All memory cleared.", LogLevel.Info);
                return;
            }
            mod.Store.Reset(args[0]);
            mod.Injector.Refresh(args[0]);
            monitor.Log($"Memory cleared for {args[0]}.", LogLevel.Info);
        });
    }
}
