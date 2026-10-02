namespace ValleyTalkMemory;

/// <summary>User-editable config, written to config.json next to the mod DLL.</summary>
public sealed class ModConfig
{
    public bool EnableMod { get; set; } = true;

    public bool DebugLogging { get; set; } = true;

    /// <summary>Dump the exact injected memory block per NPC to the mod's debug folder.</summary>
    public bool DebugDumpMemoryFiles { get; set; } = true;

    /// <summary>Log a one-shot diagnostic at startup covering the paths that fail silently.</summary>
    public bool SelfTestOnStartup { get; set; } = true;

    /// <summary>
    /// Development only: save folder name to load automatically from the title screen (empty = off).
    /// Used to verify the injection chain end-to-end without a human clicking through the menus.
    /// </summary>
    public string DevAutoLoadSave { get; set; } = "";

    /// <summary>Development only: log a post-load verification report once the save is running.</summary>
    public bool DevVerifyAfterLoad { get; set; } = true;

    /// <summary>
    /// Development only: path to a file the mod polls for instruction lines
    /// ("schedule Emily", "goto Emily Beach 09:00 11:00", "status"). Empty = disabled.
    /// Lets the schedule feature be driven unattended while watching the game.
    /// </summary>
    public string DevCommandFile { get; set; } = "";

    // ---- LLM settings. Leave blank to reuse ValleyTalk's own config.json values. ----
    public string Provider { get; set; } = "";
    public string ServerAddress { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ModelName { get; set; } = "";
    public int QueryTimeoutSeconds { get; set; } = 90;

    /// <summary>In-game days per memory week. Stardew seasons are 28 days = 4 weeks.</summary>
    public int DaysPerWeek { get; set; } = 7;

    /// <summary>
    /// Which NPCs get memory at all, comma-separated (empty or "*" = every NPC ValleyTalk knows).
    /// Currently scoped to Emily on purpose: one NPC keeps prompt size, save size and LLM cost small.
    /// </summary>
    public string EnabledNpcs { get; set; } = "Emily";

    /// <summary>
    /// Remember conversations this NPC merely overheard from other villagers.
    /// Off by default: an NPC has no way of knowing other people's plans, so third-party chatter only
    /// pollutes the memory and inflates the prompt.
    /// </summary>
    public bool IncludeOverheard { get; set; } = false;

    /// <summary>Remember the farmer's own lines in conversations with this NPC (needed for context).</summary>
    public bool IncludeFarmerLines { get; set; } = true;

    /// <summary>
    /// Remember third-party "event" logs (festivals, cutscenes) where this NPC was only a listener.
    /// Off by default, for the same reason as <see cref="IncludeOverheard"/>.
    /// </summary>
    public bool IncludeThirdPartyEvents { get; set; } = false;

    /// <summary>Character budget for this week's raw, timestamped lines.</summary>
    public int ShortTermBudgetChars { get; set; } = 3000;

    /// <summary>Character budget for compressed weekly summaries.</summary>
    public int LongTermBudgetChars { get; set; } = 2600;

    /// <summary>How many compressed weeks to keep per NPC (oldest are merged away).</summary>
    public int MaxWeeklySummaries { get; set; } = 20;

    /// <summary>Language the compressor writes memory in.</summary>
    public string MemoryLanguage { get; set; } = "简体中文";

    /// <summary>Automatically compress the finished week when a new in-game week starts.</summary>
    public bool CompressOnWeekEnd { get; set; } = true;

    /// <summary>
    /// On the first load, also compress the weeks that already existed in ValleyTalk's history before
    /// this mod was installed. Costs one LLM call per NPC-week, once.
    /// </summary>
    public bool AutoBackfillExistingHistory { get; set; } = true;

    /// <summary>Safety cap on LLM compression calls per in-game day.</summary>
    public int MaxCompressionsPerDay { get; set; } = 30;

    /// <summary>Keep at most this many raw events per NPC (newest win).</summary>
    public int MaxRawEventsPerNpc { get; set; } = 800;

    // ---- Headings used in the injected prompt block ----
    public string HeaderLongTerm { get; set; } = "长期记忆（每周压缩，较早发生的事）";
    public string HeaderShortTerm { get; set; } = "短期记忆（本周内，带游戏内日期时间）";
    public string HeaderFacts { get; set; } = "关键事实";
    public string HeaderPromises { get; set; } = "承诺与约定";
    public string LabelFarmer { get; set; } = "农夫";
    public string LabelOverheard { get; set; } = "旁听到";
    public string LabelEvent { get; set; } = "事件";
    public string MemoryInstruction { get; set; } = "以上记忆按时间先后排列，是你亲身经历或亲耳听到的。请在对话中自然运用，不要逐条复述，也不要声称自己记得没发生过的细节。";

    // ---- "self knowledge": what she knows about her own day right now ----
    // ValleyTalk already tells the model which places she still plans to visit today, but not the
    // times, and it never mentions festivals. This block supplies both, so she can say "I'm busy at
    // 8" or decline an invitation because the town is holding a festival.
    public bool InjectSelfKnowledge { get; set; } = true;

    /// <summary>Let the LLM turn a dialogue commitment into a real schedule change (tool call).</summary>
    public bool EnablePromiseExtraction { get; set; } = true;

    public string HeaderSituation { get; set; } = "当前情况";

    public string SituationFestival { get; set; } = "今天是镇上的节日，全天都有庆典活动，她的时间是排满的，因此不会接受任何其他邀约；如果被约，她会说明自己要去参加节日。";

    public string SituationPlans { get; set; } = "她今天剩下的行程安排是";

    public string SituationNoPlan { get; set; } = "她今天没有既定的行程安排，时间是自由的。";

    public string SituationMustKeep { get; set; } = "以上是她已经定下的安排；如果被邀请做别的事，她应当先考虑会不会冲突，冲突时说明自己已有安排。";
}
