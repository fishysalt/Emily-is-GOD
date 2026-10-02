using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewValley;

namespace ValleyTalkMemory;

/// <summary>
/// The list of places the model is allowed to send an NPC to.
///
/// Without this the model invents names like "Bar" or "Pub" that do not exist, and the promise dies
/// at the routing step. Built at runtime from the game's own locations, plus a Chinese alias table so
/// "酒吧" resolves to the internal name <c>Saloon</c>.
/// </summary>
internal static class LocationCatalog
{
    private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["酒吧"] = "Saloon", ["星之果实酒吧"] = "Saloon", ["沙龙"] = "Saloon",
        ["沙滩"] = "Beach", ["海滩"] = "Beach", ["海边"] = "Beach",
        ["镇上"] = "Town", ["镇子"] = "Town", ["广场"] = "Town",
        ["山区"] = "Mountain", ["山上"] = "Mountain", ["矿洞"] = "Mine", ["矿井"] = "Mine",
        ["森林"] = "Forest", ["秘密森林"] = "Woods", ["下水道"] = "Sewer",
        ["杂货店"] = "SeedShop", ["皮埃尔的店"] = "SeedShop", ["诊所"] = "Hospital",
        ["铁匠铺"] = "Blacksmith", ["木匠铺"] = "ScienceHouse", ["罗宾家"] = "ScienceHouse",
        ["牧场"] = "AnimalShop", ["玛妮家"] = "AnimalShop", ["鱼店"] = "FishShop",
        ["威利家"] = "FishShop", ["图书馆"] = "ArchaeologyHouse", ["博物馆"] = "ArchaeologyHouse",
        ["艾米丽家"] = "HaleyHouse", ["农场主屋"] = "FarmHouse", ["家里"] = "FarmHouse",
        ["公交站"] = "BusStop", ["火车站"] = "Railroad", ["温泉"] = "BathHouse_Entry",
        ["赌场"] = "Club", ["沙漠"] = "Desert", ["姜岛"] = "IslandSouth",
        ["社区中心"] = "CommunityCenter", ["电影院"] = "MovieTheater",
        ["苹果酒森林"] = "Forest", ["深山"] = "Mountain"
    };

    /// <summary>Internal names that actually exist right now, in a stable order.</summary>
    public static List<string> KnownLocations()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (GameLocation location in Game1.locations)
            {
                if (!string.IsNullOrWhiteSpace(location?.Name))
                    names.Add(location.Name);
            }
        }
        catch
        {
            // Best effort only.
        }

        foreach (string target in Aliases.Values)
            names.Add(target);

        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Turns whatever the model wrote into a real location name, or null.</summary>
    public static string Resolve(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string text = raw.Trim().Trim('"', '\'', '。', '，');

        if (Aliases.TryGetValue(text, out string mapped))
            text = mapped;

        // Exact internal name first, then a loose contains-match against known locations.
        List<string> known = KnownLocations();
        string exact = known.FirstOrDefault(n => n.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        string loose = known.FirstOrDefault(n => text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0
                                                 || n.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
        if (loose != null)
            return loose;

        // Last resort: a Chinese alias appearing inside the model's phrase.
        foreach (KeyValuePair<string, string> kvp in Aliases)
        {
            if (text.Contains(kvp.Key) && known.Contains(kvp.Value, StringComparer.OrdinalIgnoreCase))
                return kvp.Value;
        }
        return null;
    }

    /// <summary>The reference block handed to the model ("RAG" material for the tool call).</summary>
    public static string DescribeForPrompt()
        => string.Join(", ", KnownLocations());
}

internal sealed class PromiseExtraction
{
    public SchedulePromise Promise { get; set; }

    public string RawToolCall { get; set; }

    public string Error { get; set; }
}

/// <summary>
/// Turns a finished exchange into a tool call.
///
/// ValleyTalk owns its own output format (a "- line" plus "% responses"), so we cannot piggyback on
/// native function calling. Instead, after new dialogue shows up we make one extra LLM call whose
/// whole job is: "did she commit to going somewhere? emit one change_schedule tool call, or null."
/// That is the same shape as an agent deciding whether to use a tool while answering.
/// </summary>
internal sealed class PromiseExtractor
{
    private readonly LlmClient _llm;
    private readonly IMonitor _monitor;
    private readonly ModConfig _config;

    public PromiseExtractor(LlmClient llm, IMonitor monitor, ModConfig config)
    {
        _llm = llm;
        _monitor = monitor;
        _config = config;
    }

    /// <summary>Cheap gate so we only pay for an extraction call when it could possibly matter.</summary>
    private static readonly Regex PromiseCue = new Regex(
        @"点|今晚|今早|今天|明天|后天|下午|上午|早上|晚上|等我|一起|约|见面|来找我|去吧|会去|要去|答应|说好|等我一下",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool LooksLikePromise(string text)
        => !string.IsNullOrWhiteSpace(text) && PromiseCue.IsMatch(text);

    /// <summary>Runs one extraction. Returns null when there is nothing to do or a failure occurred.</summary>
    public async Task<PromiseExtraction> ExtractAsync(string npc, IReadOnlyList<RawEvent> lines)
    {
        if (lines == null || lines.Count == 0)
            return null;

        var transcript = new StringBuilder();
        foreach (RawEvent e in lines)
            transcript.AppendLine(MemoryTextBuilder.Format(e, npc, _config));

        string user =
            $"【当前游戏内时间】{GameWeek.Describe(GameWeek.Today, Game1.timeOfDay)}，"
            + $"现在时刻 {Game1.timeOfDay / 100:00}:{Game1.timeOfDay % 100:00}；"
            + $"今天是 {(IsFestival() ? "节日" : "普通日子")}\n"
            + $"【可用地点】（只能从这里选，必须原样使用英文内部名）\n{LocationCatalog.DescribeForPrompt()}\n\n"
            + $"【对话记录】\n{transcript}\n"
            + "请判断这个 NPC 是否承诺了一件她自己将来要做的、有明确时间的事。返回 JSON。";

        string raw = await _llm.CompleteAsync(SystemPrompt(), user, _config.QueryTimeoutSeconds, CancellationToken.None)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(raw))
            return new PromiseExtraction { Error = "no response" };

        return Parse(npc, raw);
    }

    private string SystemPrompt() =>
        "你是一个工具调用路由器。你只输出 JSON，不解释、不加代码块标记。\n"
        + "你可用的工具是：\n"
        + "{\"name\":\"change_schedule\",\"description\":\"这个角色在对话中明确答应、承诺、邀请或决定自己将在某个时刻去某个地点时调用。"
        + "只在角色本人会去做某事时调用；闲聊、玩笑、假设、拒绝、谈论过去、谈论别人，一律不要调用。\","
        + "\"parameters\":{"
        + "\"location\":\"必须原样使用【可用地点】里的英文内部名，禁止自创或翻译\","
        + "\"day\":\"today|tomorrow\","
        + "\"time\":\"HH:MM 24 小时制\","
        + "\"until\":\"可选，HH:MM，她打算待到几点\","
        + "\"commitment\":\"她承诺这件事的原话\"}}\n"
        + "换算规则：游戏一天从 06:00 开始，所以「1点」到「5点」按下午算；「两点」=14:00；"
        + "「今晚八点」=20:00；「明天早上十点」= day=tomorrow, time=10:00。"
        + "已经过去的时刻按今天算没意义，若明显指明天就用 tomorrow。\n"
        + "今天若为节日则不要调用工具，返回 null。\n"
        + "不需要调用时返回：{\"tool_call\": null}\n"
        + "需要调用时返回：{\"tool_call\": {\"location\":\"Saloon\",\"day\":\"today\",\"time\":\"14:00\",\"until\":\"\",\"commitment\":\"原话\"}}";

    private static bool IsFestival()
    {
        try { return Utility.isFestivalDay(); }
        catch { return false; }
    }

    private PromiseExtraction Parse(string npc, string raw)
    {
        string json = raw.Trim();
        int start = json.IndexOf('{');
        int end = json.LastIndexOf('}');
        if (start < 0 || end <= start)
            return new PromiseExtraction { Error = "no JSON in response" };
        json = json.Substring(start, end - start + 1);

        try
        {
            JObject root = JObject.Parse(json);
            JToken call = root["tool_call"];
            if (call == null || call.Type == JTokenType.Null)
                return null;

            string locationRaw = (string)call["location"];
            string location = LocationCatalog.Resolve(locationRaw);
            if (location == null)
                return new PromiseExtraction { RawToolCall = call.ToString(), Error = $"unknown location '{locationRaw}'" };

            string dayText = ((string)call["day"] ?? "today").Trim().ToLowerInvariant();
            long day = dayText.StartsWith("tom") ? GameWeek.Today + 1 : GameWeek.Today;

            if (!NpcScheduler.TryParseClock((string)call["time"], out int time))
                return new PromiseExtraction { RawToolCall = call.ToString(), Error = $"unreadable time '{(string)call["time"]}'" };

            int until = 0;
            NpcScheduler.TryParseClock((string)call["until"], out until);

            // A promise for an already-past moment today is stale unless it was meant for tomorrow.
            if (day == GameWeek.Today && time <= Game1.timeOfDay)
            {
                _monitor?.Log($"Promise time {time} already passed today; treating it as tomorrow.", LogLevel.Info);
                day = GameWeek.Today + 1;
            }

            int tileX = 0;
            int tileY = 0;
            NpcScheduler.TryGuessTile(location, out tileX, out tileY);

            return new PromiseExtraction
            {
                RawToolCall = call.ToString(),
                Promise = new SchedulePromise
                {
                    Npc = npc,
                    Location = location,
                    TileX = tileX,
                    TileY = tileY,
                    Facing = 2,
                    Day = day,
                    ArriveTime = time,
                    UntilTime = until,
                    SourceText = (string)call["commitment"] ?? "",
                    CreatedUtc = DateTime.UtcNow.ToString("o")
                }
            };
        }
        catch (Exception ex)
        {
            return new PromiseExtraction { Error = $"bad tool call JSON: {ex.Message}" };
        }
    }
}
