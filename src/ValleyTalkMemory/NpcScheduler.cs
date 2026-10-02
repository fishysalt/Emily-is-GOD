using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;

namespace ValleyTalkMemory;

/// <summary>One thing an NPC promised to do, turned into something the engine can execute.</summary>
internal sealed class SchedulePromise
{
    public string Npc { get; set; } = "";

    public string Location { get; set; } = "";

    public int TileX { get; set; }

    public int TileY { get; set; }

    /// <summary>0=up 1=right 2=down 3=left</summary>
    public int Facing { get; set; } = 2;

    /// <summary>Absolute in-game day (0 = Spring 1, Year 1).</summary>
    public long Day { get; set; } = -1;

    /// <summary>Departure time in military time; must be a multiple of 10.</summary>
    public int ArriveTime { get; set; }

    /// <summary>When she should head home. 0 = leave it to her own remaining schedule.</summary>
    public int UntilTime { get; set; }

    /// <summary>End-of-route behaviour: "" = just stand, "square_5_5" = pace around, or an animation key.</summary>
    public string Activity { get; set; } = "";

    /// <summary>Optional line she says on arrival.</summary>
    public string Message { get; set; } = "";

    /// <summary>The dialogue this came from, for debugging.</summary>
    public string SourceText { get; set; } = "";

    public string CreatedUtc { get; set; } = "";
}

/// <summary>
/// Turns a promise into a real in-game itinerary.
///
/// How the engine actually works (read out of the decompiled <c>NPC</c>):
///  * <c>Schedule</c> is a live dictionary of departure-time -> route, rebuilt from the
///    <c>Characters/schedules/&lt;name&gt;</c> asset once per day, and polled every 10 in-game minutes.
///  * Each entry's <c>route</c> is precomputed from the <em>previous entry's destination</em>, so
///    simply inserting one entry leaves every later entry walking from the wrong place. That is why
///    this class rebuilds the whole remaining chain instead of patching one key.
///  * On arrival the NPC only does what <c>endOfRouteBehavior</c> says (nothing, pace in a square,
///    or a looping animation). Between two entries the engine does not drive her at all, so
///    "stay until 9pm then go home" has to exist as its own entry.
///  * Overnight she is teleported home by <c>dayUpdate</c>, so anything not anchored to an entry
///    would leave her standing outside until the warp.
/// </summary>
internal static class NpcScheduler
{
    private const string FallbackKey = "spring";

    /// <summary>Where vanilla sends a married NPC before walking her anywhere (see NPC.parseMasterSchedule).</summary>
    private const string BusStopMap = "BusStop";
    private const int BusStopX = 10;
    private const int BusStopY = 23;

    private static bool IsFarmMap(string name)
        => !string.IsNullOrEmpty(name)
           && (name.Equals("Farm", StringComparison.OrdinalIgnoreCase)
               || name.Equals("FarmHouse", StringComparison.OrdinalIgnoreCase));

    /// <summary>Applies a promise. Returns null on success, otherwise a human-readable reason.</summary>
    public static string Apply(SchedulePromise promise, IMonitor monitor)
    {
        if (promise == null)
            return "no promise";
        if (!Context.IsMainPlayer)
            return "only the host can drive NPC pathfinding";

        try
        {
            if (Game1.eventUp || Game1.CurrentEvent != null)
                return "a cutscene/event is playing";
            if (Utility.isFestivalDay())
            {
                // On festival days the game clears every NPC's schedule and places them in the
                // festival map, so an injected plan would be meaningless. Refuse instead, and let the
                // prompt-level "situation" note make her decline the invitation in dialogue.
                return "today is a festival day (the town is holding a festival)";
            }
        }
        catch
        {
            // These checks are best-effort; never block the feature because one of them throws.
        }

        NPC npc = Game1.getCharacterFromName(promise.Npc);
        if (npc == null)
            return $"NPC '{promise.Npc}' not found";
        if (npc.currentLocation == null)
            return "NPC has no current location";

        try
        {
            if (npc.Schedule == null)
                npc.TryLoadSchedule();
        }
        catch (Exception ex)
        {
            monitor?.Log($"TryLoadSchedule failed: {ex.Message}", LogLevel.Warn);
        }

        // A spouse whose schedule was cleared (ClearSchedule() leaves Schedule=null,
        // dayScheduleName="" and followSchedule=false — e.g. a festival day, a wedding, or another
        // mod taking over spouse behaviour) simply has no plan today. We can still install one.
        var remaining = npc.Schedule ?? new Dictionary<int, SchedulePathDescription>();

        int now = Game1.timeOfDay;
        int arrive = Math.Max(RoundToTen(promise.ArriveTime), now);

        // --- 1. collect what is left of her own day, after our arrival ---
        var later = remaining
            .Where(kv => kv.Key > arrive)
            .OrderBy(kv => kv.Key)
            .ToList();

        // --- 2. rebuild the whole remaining chain so every route starts where she actually will be ---
        var rebuilt = new SortedDictionary<int, SchedulePathDescription>();
        string fromLocation = npc.currentLocation.Name;
        int fromX = npc.TilePoint.X;
        int fromY = npc.TilePoint.Y;

        SchedulePathDescription first = BuildRoute(npc, promise, arrive, fromLocation, fromX, fromY, monitor);

        // Confirmed empirically: NPC pathfinding has no route *out of* the Farm, which is exactly why
        // vanilla married schedules hard-code their start at "BusStop 10 23" — the game warps the
        // spouse to the bus stop and walks from there. Mirror that instead of failing.
        if (first == null && npc.isMarried() && IsFarmMap(fromLocation))
        {
            monitor?.Log($"No route out of {fromLocation}; warping {npc.Name} to BusStop (10,23) like a vanilla spouse schedule.", LogLevel.Info);
            try
            {
                Game1.warpCharacter(npc, BusStopMap, new Microsoft.Xna.Framework.Vector2(BusStopX, BusStopY));
            }
            catch (Exception ex)
            {
                monitor?.Log($"Warp to BusStop failed: {ex.Message}", LogLevel.Warn);
            }

            fromLocation = BusStopMap;
            fromX = BusStopX;
            fromY = BusStopY;
            first = BuildRoute(npc, promise, arrive, fromLocation, fromX, fromY, monitor);
        }

        if (first == null)
            return $"could not find a walkable route to {promise.Location} ({promise.TileX},{promise.TileY}) from {fromLocation}";

        rebuilt[arrive] = first;
        fromLocation = promise.Location;
        fromX = promise.TileX;
        fromY = promise.TileY;

        foreach (KeyValuePair<int, SchedulePathDescription> entry in later)
        {
            SchedulePathDescription step = RebuildRoute(npc, entry.Value, entry.Key, fromLocation, fromX, fromY, monitor);
            if (step == null)
            {
                monitor?.Log($"Dropped original waypoint {entry.Key} ({entry.Value.targetLocationName}) — no route from {fromLocation}.", LogLevel.Warn);
                continue;
            }
            rebuilt[entry.Key] = step;
            fromLocation = entry.Value.targetLocationName;
            fromX = entry.Value.targetTile.X;
            fromY = entry.Value.targetTile.Y;
        }

        // --- 3. if her day would otherwise end wherever we sent her, send her home ---
        int until = promise.UntilTime > arrive ? RoundToTen(promise.UntilTime) : 0;
        if (until > 0)
        {
            SchedulePathDescription home = BuildHomeRoute(npc, until, fromLocation, fromX, fromY, monitor);
            if (home != null)
            {
                rebuilt[until] = home;
                monitor?.Log($"{npc.Name} will head home at {until}.", LogLevel.Trace);
            }
            else
            {
                monitor?.Log($"Could not build a route home for {npc.Name} at {until}; she may stay out until the overnight warp.", LogLevel.Warn);
            }
        }
        if (npc.Schedule == null && later.Count == 0 && until == 0)
        {
            // Nothing else to anchor to, so she would stand at the destination all day and be warped
            // home overnight. Add a plausible return leg so the day is self-consistent.
            int fallback = RoundToTen(arrive + 200);
            SchedulePathDescription homeOnly = BuildHomeRoute(npc, fallback, fromLocation, fromX, fromY, monitor);
            if (homeOnly != null)
            {
                rebuilt[fallback] = homeOnly;
                monitor?.Log($"{npc.Name} had no plan today; added a return-home step at {fallback}.", LogLevel.Info);
            }
        }
        else if (until == 0 && later.Count == 0)
        {
            monitor?.Log($"{npc.Name} has no later waypoint today, so she will remain at {promise.Location} until the overnight warp home.", LogLevel.Warn);
        }

        // --- 4. install ---
        try
        {
            string key = string.IsNullOrWhiteSpace(npc.ScheduleKey) ? FallbackKey : npc.ScheduleKey;
            npc.ignoreScheduleToday = false;
            npc.currentScheduleDelay = 0f;
            npc.TryLoadSchedule(key, new Dictionary<int, SchedulePathDescription>(rebuilt));
            npc.queuedSchedulePaths.Clear();
            npc.lastAttemptedSchedule = 0;
            npc.checkSchedule(Game1.timeOfDay);
        }
        catch (Exception ex)
        {
            return $"installing the schedule failed: {ex.Message}";
        }

        monitor?.Log($"Schedule rewritten for {npc.Name}: {string.Join(" -> ", rebuilt.Select(kv => $"{kv.Key}:{kv.Value.targetLocationName}"))}", LogLevel.Info);
        return null;
    }

    private static SchedulePathDescription BuildRoute(NPC npc, SchedulePromise promise, int time, string fromLocation, int fromX, int fromY, IMonitor monitor)
    {
        try
        {
            SchedulePathDescription route = npc.pathfindToNextScheduleLocation(
                string.IsNullOrWhiteSpace(npc.ScheduleKey) ? FallbackKey : npc.ScheduleKey,
                fromLocation, fromX, fromY,
                promise.Location, promise.TileX, promise.TileY,
                promise.Facing, promise.Activity ?? "", promise.Message ?? "");

            if (route?.route == null || route.route.Count == 0)
                return null;
            route.time = time;
            return route;
        }
        catch (Exception ex)
        {
            monitor?.Log($"pathfind to {promise.Location} ({promise.TileX},{promise.TileY}) failed: {ex.Message}", LogLevel.Warn);
            return null;
        }
    }

    private static SchedulePathDescription RebuildRoute(NPC npc, SchedulePathDescription original, int time, string fromLocation, int fromX, int fromY, IMonitor monitor)
    {
        try
        {
            SchedulePathDescription route = npc.pathfindToNextScheduleLocation(
                string.IsNullOrWhiteSpace(npc.ScheduleKey) ? FallbackKey : npc.ScheduleKey,
                fromLocation, fromX, fromY,
                original.targetLocationName, original.targetTile.X, original.targetTile.Y,
                original.facingDirection, original.endOfRouteBehavior ?? "", original.endOfRouteMessage ?? "");
            if (route?.route == null || route.route.Count == 0)
                return null;
            route.time = time;
            return route;
        }
        catch (Exception ex)
        {
            monitor?.Log($"repath to {original.targetLocationName} failed: {ex.Message}", LogLevel.Warn);
            return null;
        }
    }

    private static SchedulePathDescription BuildHomeRoute(NPC npc, int time, string fromLocation, int fromX, int fromY, IMonitor monitor)
    {
        bool married = npc.isMarried();

        // NPC pathfinding cannot enter the Farm either, so a married NPC's "go home" leg has to end
        // at the bus stop — the same place vanilla starts their work-day schedule. The engine (and
        // spouse handling) takes her indoors from there, and the overnight dayUpdate warps her home
        // as a backstop.
        string home = married ? BusStopMap : npc.DefaultMap;
        if (string.IsNullOrWhiteSpace(home))
            return null;

        int hx;
        int hy;
        if (married)
        {
            hx = BusStopX;
            hy = BusStopY;
            monitor?.Log($"{npc.Name} is married, so the trip home ends at {BusStopMap} ({hx},{hy}) — the game cannot path onto the Farm.", LogLevel.Trace);
        }
        else
        {
            var homePosition = npc.DefaultPosition;
            hx = (int)(homePosition.X / 64f);
            hy = (int)(homePosition.Y / 64f);
        }

        try
        {
            SchedulePathDescription route = npc.pathfindToNextScheduleLocation(
                string.IsNullOrWhiteSpace(npc.ScheduleKey) ? FallbackKey : npc.ScheduleKey,
                fromLocation, fromX, fromY, home, hx, hy, 2, npc.isMarried() ? "sleep" : "", "");

            if (route?.route == null || route.route.Count == 0)
                return null;
            route.time = time;
            return route;
        }
        catch (Exception ex)
        {
            monitor?.Log($"pathfind home ({home} {hx},{hy}) failed: {ex.Message}", LogLevel.Warn);
            return null;
        }
    }

    /// <summary>
    /// Schedule lookups are an exact match against the 10-minute clock, so a time like 2005 would
    /// never fire. Times are also clamped into the valid in-game range.
    /// </summary>
    public static int RoundToTen(int time)
    {
        if (time <= 0)
            return 0;
        int rounded = time / 10 * 10;
        int minute = rounded % 100;
        if (minute >= 60)
            rounded = rounded - minute + 100;   // e.g. 1265 -> 1300
        return Math.Max(600, Math.Min(2600, rounded));
    }

    /// <summary>Parses "20:00", "2000" or "8pm" into military time.</summary>
    public static bool TryParseClock(string text, out int time)
    {
        time = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string s = text.Trim().ToLowerInvariant().Replace("：", ":");
        bool pm = s.EndsWith("pm");
        bool am = s.EndsWith("am");
        if (pm || am)
            s = s.Substring(0, s.Length - 2).Trim();

        int hour;
        int minute = 0;
        if (s.Contains(':'))
        {
            string[] parts = s.Split(':');
            if (!int.TryParse(parts[0], out hour))
                return false;
            if (parts.Length > 1 && parts[1].Length > 0 && !int.TryParse(parts[1], out minute))
                return false;
        }
        else
        {
            if (!int.TryParse(s, out int raw))
                return false;
            if (raw >= 100)
            {
                hour = raw / 100;
                minute = raw % 100;
            }
            else
            {
                hour = raw;
            }
        }

        if (pm && hour < 12)
            hour += 12;
        if (am && hour == 12)
            hour = 0;
        if (hour < 6)
            hour += 12;          // Stardew days start at 06:00, so "1" means 13:00

        time = hour * 100 + minute;
        return time >= 600 && time <= 2600;
    }

    /// <summary>
    /// Guesses a sensible standing tile for a location by reusing a tile some NPC's own schedule
    /// already walks to. Avoids guessing at tile-passability APIs.
    /// </summary>
    public static bool TryGuessTile(string locationName, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (string.IsNullOrWhiteSpace(locationName))
            return false;

        foreach (NPC npc in Utility.getAllCharacters())
        {
            if (npc?.Schedule == null)
                continue;

            foreach (SchedulePathDescription entry in npc.Schedule.Values)
            {
                if (string.Equals(entry.targetLocationName, locationName, StringComparison.OrdinalIgnoreCase)
                    && entry.targetTile.X > 0 && entry.targetTile.Y > 0)
                {
                    x = entry.targetTile.X;
                    y = entry.targetTile.Y;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Human-readable dump of an NPC's remaining day, for the debug command.</summary>
    public static IEnumerable<string> DescribeRemaining(NPC npc)
    {
        if (npc?.Schedule == null)
            yield break;

        foreach (KeyValuePair<int, SchedulePathDescription> entry in npc.Schedule.OrderBy(kv => kv.Key))
        {
            if (entry.Key < Game1.timeOfDay)
                continue;
            SchedulePathDescription d = entry.Value;
            yield return $"  {entry.Key:0000}  {d.targetLocationName} ({d.targetTile.X},{d.targetTile.Y}) facing={d.facingDirection}"
                         + $" behaviour='{d.endOfRouteBehavior}' message='{d.endOfRouteMessage}' steps={d.route?.Count ?? 0}";
        }
    }
}
