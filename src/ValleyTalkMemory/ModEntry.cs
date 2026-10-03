using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleyTalkMemory;

public sealed class ModEntry : Mod
{
    private const int PollIntervalTicks = 300;
    private const int PromiseExtractionCooldownTicks = 1200;

    private readonly Dictionary<string, int> _liveFingerprint = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentQueue<PromiseExtraction> _extractionResults =
        new System.Collections.Concurrent.ConcurrentQueue<PromiseExtraction>();
    private PromiseExtractor _extractor;
    private int _lastExtractionTick = -100000;

    private IModHelper _helper;
    private long _lastSeenWeek = -1;
    private long _compressionDay = -1;
    private int _compressionsToday;
    private bool _vtStringsLoaded;
    private bool _selfTestPending;
    private int _devLoadCountdown = -1;
    private bool _devLoadDone;
    private int _verifyCountdown = -1;

    internal static ModEntry Instance { get; private set; }

    internal ModConfig Config { get; private set; }

    internal MemoryStore Store { get; private set; }

    internal PromptInjector Injector { get; private set; }

    internal MemoryCompressor Compressor { get; private set; }

    internal LlmClient Llm { get; private set; }

    internal IValleyTalkApi VtApi { get; private set; }

    public override void Entry(IModHelper helper)
    {
        Instance = this;
        _helper = helper;

        Config = helper.ReadConfig<ModConfig>();
        helper.WriteConfig(Config);

        Store = new MemoryStore(helper, Monitor, Config);
        Llm = new LlmClient(Monitor);
        Compressor = new MemoryCompressor(Llm, Monitor, Config);
        Injector = new PromptInjector(helper, Monitor, Config, Store);
        _extractor = new PromiseExtractor(Llm, Monitor, Config);

        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.Saving += OnSaving;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;

        Commands.Register(helper, this, Monitor);

        Monitor.Log("ValleyTalk Memory loaded. Short-term = this in-game week, long-term = weekly LLM compression.", LogLevel.Info);
    }

    private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
    {
        VtApi = _helper.ModRegistry.GetApi<IValleyTalkApi>("dandm1.ValleyTalk");
        if (VtApi == null)
        {
            Monitor.Log("ValleyTalk's mod API was not found. Memory will still be recorded, but cannot be injected into prompts.", LogLevel.Warn);
        }
        else
        {
            VtApi.SetModName(ModManifest.UniqueID);
            Monitor.Log("Connected to ValleyTalk's prompt override API.", LogLevel.Info);
        }

        Injector.Api = VtApi;
        Llm.Configure(Config, _helper);

        if (Config.SelfTestOnStartup)
            _selfTestPending = true;

        if (!string.IsNullOrWhiteSpace(Config.DevAutoLoadSave))
        {
            _devLoadCountdown = 180;
            Monitor.Log($"[dev] will auto-load save '{Config.DevAutoLoadSave}' in ~3 seconds (read-only; nothing is saved).", LogLevel.Warn);
        }
    }

    /// <summary>
    /// Development helper: loads a save straight from the title screen, exactly like clicking it in
    /// the load menu (<c>SaveGame.Load(slotName)</c> + <c>Game1.exitActiveMenu()</c>). Loading never
    /// writes to the save, so this is safe to run unattended; we always kill the process before any
    /// in-game day can end.
    /// </summary>
    private void RunDevAutoLoad()
    {
        _devLoadDone = true;
        string folder = Config.DevAutoLoadSave;
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "StardewValley", "Saves", folder);
            if (!Directory.Exists(path))
            {
                Monitor.Log($"[dev] save folder not found: {path}", LogLevel.Error);
                return;
            }

            Monitor.Log($"[dev] loading save '{folder}'...", LogLevel.Warn);
            SaveGame.Load(folder);
            Game1.exitActiveMenu();
        }
        catch (Exception ex)
        {
            Monitor.Log($"[dev] auto-load FAILED: {ex}", LogLevel.Error);
        }
    }

    /// <summary>
    /// Re-checks stored NPC names against the (now larger) runtime name table and re-registers the
    /// prompt override under the canonical name.
    ///
    /// ValleyTalk's own character list only fills up once it starts patching NPCs, and CustomCompanions
    /// pets never appear in Data/Characters at all — so a couple of keys can only be resolved a few
    /// seconds after the save is loaded. Without this pass those NPCs would keep a lower-cased name
    /// that ValleyTalk never looks up, i.e. they would silently get no memory.
    /// </summary>
    internal void ReconcileNames()
    {
        int rekeyed = 0;
        foreach (NpcMemory memory in Store.All.ToList())
        {
            string resolved = NpcNameResolver.Resolve(memory.Npc);
            if (!string.Equals(resolved, memory.Npc, StringComparison.Ordinal))
            {
                Monitor.Log($"Re-keyed memory '{memory.Npc}' -> '{resolved}'.", LogLevel.Info);
                memory.Npc = resolved;
                rekeyed++;
            }
            Injector.Refresh(memory.Npc);
        }
        Monitor.Log($"Name reconcile pass done ({rekeyed} re-keyed).", rekeyed > 0 ? LogLevel.Info : LogLevel.Trace);
    }

    /// <summary>Runs the startup self-test on the first tick after the mod API is wired up.</summary>
    private void RunPendingSelfTest()
    {
        _selfTestPending = false;
        try
        {
            SelfTest.Run(_helper, Monitor, this);
        }
        catch (Exception ex)
        {
            Monitor.Log($"Self-test crashed: {ex}", LogLevel.Error);
        }
    }

    private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
    {
        try
        {
            Store.LoadFromSave();
            Store.LoadBackup();
            NpcNameResolver.Rebuild(_helper, Monitor);

            HashSet<string> changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unresolved = new List<string>();
            var scope = HistoryScope.From(Config);
            int total = 0;
            int skipped = 0;
            foreach (KeyValuePair<string, string> kvp in VtHistorySource.FromSaveData(Monitor))
            {
                total++;
                string npc = NpcNameResolver.Resolve(kvp.Key);
                bool known = NpcNameResolver.IsKnown(kvp.Key);

                if (!IsNpcEnabled(npc))
                {
                    skipped++;
                    continue;
                }

                if (!known)
                    unresolved.Add(kvp.Key);

                List<RawEvent> events = VtHistoryParser.Parse(npc, kvp.Value, scope);
                if (Store.Merge(npc, events, known))
                    changed.Add(npc);
            }

            Monitor.Log($"History keys: {total} found, {skipped} skipped (not in EnabledNpcs), "
                        + $"{total - skipped - unresolved.Count}/{total - skipped} of the rest resolved to real NPC names.", LogLevel.Info);
            if (unresolved.Count > 0)
                Monitor.Log($"Unresolved keys (these NPCs are not currently defined or placed, so they cannot talk anyway): {string.Join(", ", unresolved)}. Run 'vtmemory_names' later to re-check.", LogLevel.Info);

            if (!_vtStringsLoaded)
            {
                Injector.LoadValleyTalkStrings();
                _vtStringsLoaded = true;
            }

            Injector.RefreshAll();
            _lastSeenWeek = GameWeek.CurrentWeek(Config.DaysPerWeek);

            Monitor.Log($"Save loaded: memory for {Store.KnownNpcs.Count()} NPCs, {changed.Count} refreshed from saved ValleyTalk history.", LogLevel.Info);

            ApplyTodayPromises();

            if (Config.AutoBackfillExistingHistory)
                BackfillExistingHistory();

            if (Config.DevVerifyAfterLoad)
                _verifyCountdown = 300;
        }
        catch (Exception ex)
        {
            Monitor.Log($"Failed to initialise memory on save load: {ex}", LogLevel.Error);
        }
    }

    private void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        try
        {
            NpcNameResolver.Rebuild(_helper, Monitor);
            ReconcileNames();
            ApplyTodayPromises();
            PollLiveHistory();

            long currentWeek = GameWeek.CurrentWeek(Config.DaysPerWeek);
            long today = GameWeek.Today;
            if (_compressionDay != today)
            {
                _compressionDay = today;
                _compressionsToday = 0;
            }

            if (_lastSeenWeek >= 0 && currentWeek > _lastSeenWeek)
            {
                Monitor.Log($"New in-game week ({GameWeek.DescribeWeek(currentWeek, Config.DaysPerWeek)}); compressing finished weeks.", LogLevel.Info);
                QueueWeeklyCompressions(currentWeek);
            }
            _lastSeenWeek = currentWeek;
        }
        catch (Exception ex)
        {
            Monitor.Log($"DayStarted handling failed: {ex}", LogLevel.Error);
        }
    }

    private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        try
        {
            if (_selfTestPending)
                RunPendingSelfTest();

            if (!_devLoadDone && _devLoadCountdown > 0 && --_devLoadCountdown == 0)
                RunDevAutoLoad();

            if (_verifyCountdown > 0 && --_verifyCountdown == 0)
            {
                try
                {
                    ReconcileNames();
                    SelfTest.RunAfterLoad(_helper, Monitor, this);
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Post-load verification crashed: {ex}", LogLevel.Error);
                }
            }

            if (e.IsMultipleOf(PollIntervalTicks))
            {
                PollLiveHistory();
                PollDevCommandFile();

                // The "current situation" block tracks her live schedule and the clock, so keep the
                // registered override in step. Text that has not changed is skipped internally.
                if (Config.InjectSelfKnowledge)
                    Injector.RefreshAll();
            }

            DrainExtractions();

            while (Compressor.TryDequeue(out CompressionResult result))
                ApplyCompression(result);
        }
        catch (Exception ex)
        {
            Monitor.Log($"UpdateTicked handling failed: {ex}", LogLevel.Error);
        }
    }

    private void OnSaving(object sender, SavingEventArgs e)
    {
        Store.SaveToSave();
        Store.SaveBackup();
    }

    private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
    {
        Injector.ClearAll();
        Store.Reset(null);
        // Everything below is per-save state. Leaving it behind would leak one save's world into the
        // next one: fingerprints would suppress re-reading history, and name-mapping retries would be
        // permanently marked as already attempted.
        _liveFingerprint.Clear();
        _lastSeenWeek = -1;
        _compressionDay = -1;
        _compressionsToday = 0;
    }

    /// <summary>Reads ValleyTalk's live in-memory history and merges anything new.</summary>
    internal int PollLiveHistory()
    {
        int changedCount = 0;
        var scope = HistoryScope.From(Config);
        foreach (KeyValuePair<string, string> kvp in VtHistorySource.FromLiveCache(Monitor))
        {
            // ValleyTalk's cache holds the whole history per NPC and only changes when they speak,
            // so skip re-parsing anything whose JSON is byte-identical to last time.
            int fingerprint = Fingerprint(kvp.Value);
            if (_liveFingerprint.TryGetValue(kvp.Key, out int previous) && previous == fingerprint)
                continue;
            _liveFingerprint[kvp.Key] = fingerprint;

            string npc = NpcNameResolver.Resolve(kvp.Key);
            if (!IsNpcEnabled(npc))
                continue;

            bool known = NpcNameResolver.IsKnown(kvp.Key);
            NpcMemory memory = Store.GetOrCreate(npc);
            HashSet<string> before = memory.Raw.Select(x => x.DedupKey).ToHashSet(StringComparer.Ordinal);

            List<RawEvent> events = VtHistoryParser.Parse(npc, kvp.Value, scope);
            if (Store.Merge(npc, events, known))
            {
                Injector.Refresh(npc);
                changedCount++;

                List<RawEvent> added = memory.Raw.Where(x => !before.Contains(x.DedupKey)).ToList();
                QueuePromiseExtraction(npc, added);
            }
        }
        return changedCount;
    }

    /// <summary>
    /// Kicks off the "did she commit to something?" tool call for freshly captured lines.
    /// Gated three ways so normal chit-chat costs nothing: a keyword prefilter, a per-tick cooldown,
    /// and the extractor returning null when the model says there is no tool call.
    /// </summary>
    private void QueuePromiseExtraction(string npc, List<RawEvent> added)
    {
        if (!Config.EnablePromiseExtraction || added == null || added.Count == 0)
            return;
        if (Game1.ticks - _lastExtractionTick < PromiseExtractionCooldownTicks)
            return;

        string text = string.Join(" ", added.Select(e => e.Text));
        if (!PromiseExtractor.LooksLikePromise(text))
            return;

        _lastExtractionTick = Game1.ticks;
        List<RawEvent> lines = added.ToList();
        Monitor.Log($"[promise] checking {added.Count} new line(s) from {npc} for a commitment...", LogLevel.Trace);

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            PromiseExtraction result = await _extractor.ExtractAsync(npc, lines).ConfigureAwait(false);
            if (result != null)
                _extractionResults.Enqueue(result);
        });
    }

    private void DrainExtractions()
    {
        while (_extractionResults.TryDequeue(out PromiseExtraction result))
        {
            if (result.Error != null)
            {
                Monitor.Log($"[promise] extraction failed: {result.Error}", LogLevel.Warn);
                RecordUnfulfilledPromise(result.Promise?.Npc, "（抽取失败，无法安排行程）");
                continue;
            }
            if (result.Promise == null)
            {
                Monitor.Log("[promise] no tool call — the model judged this was not a commitment to act.", LogLevel.Info);
                continue;
            }

            Monitor.Log($"[promise] tool call: {result.RawToolCall}", LogLevel.Info);

            if (result.Promise.Day != GameWeek.Today)
            {
                NpcMemory owner = Store.GetOrCreate(result.Promise.Npc);
                owner.Promises.RemoveAll(p => p.Day == result.Promise.Day && p.ArriveTime == result.Promise.ArriveTime && p.Location == result.Promise.Location);
                owner.Promises.Add(result.Promise);
                Store.SaveBackup();
                Monitor.Log($"[promise] stored for {GameWeek.Describe(result.Promise.Day)}: "
                            + $"{result.Promise.Npc} -> {result.Promise.Location} at {result.Promise.ArriveTime}", LogLevel.Info);
                continue;
            }

            string error = NpcScheduler.Apply(result.Promise, Monitor);
            if (error != null)
            {
                Monitor.Log($"[promise] could not be honoured: {error}", LogLevel.Warn);
                RecordUnfulfilledPromise(result.Promise.Npc, $"（答应了「{result.Promise.SourceText}」但没能成行：{error}）");
                continue;
            }
            Monitor.Log($"[promise] honoured: {result.Promise.Npc} -> {result.Promise.Location} at {result.Promise.ArriveTime}", LogLevel.Info);
        }
    }

    /// <summary>
    /// C: an NPC who agreed to something and then silently failed to do it must not be surprised next
    /// time. Writing the failure into her own short-term memory is what keeps her consistent — she
    /// will bring it up instead of contradicting herself.
    /// </summary>
    private void RecordUnfulfilledPromise(string npc, string note)
    {
        if (string.IsNullOrWhiteSpace(npc))
            return;

        var memory = new RawEvent
        {
            Day = GameWeek.Today,
            Time = Game1.timeOfDay,
            Kind = "sys",
            Speaker = npc,
            Text = note
        };

        if (Store.Merge(npc, new[] { memory }, canonicalName: false))
            Injector.Refresh(npc);
    }

    /// <summary>
    /// Development helper: executes any lines waiting in <see cref="ModConfig.DevCommandFile"/> and
    /// clears the file. SMAPI's public command API has no "trigger" method, so this is how the
    /// schedule feature gets driven without a human at the keyboard.
    /// </summary>
    private void PollDevCommandFile()
    {
        string path = Config.DevCommandFile;
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (!File.Exists(path))
                return;

            string[] lines = File.ReadAllLines(path);
            File.WriteAllText(path, string.Empty);
            foreach (string line in lines)
                DevActions.RunDevLine(line, this, Monitor);
        }
        catch (Exception ex)
        {
            Monitor.Log($"[devcmd] failed: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>
    /// Whether this NPC should have memory at all. Scoping this to one NPC keeps the prompt small,
    /// the save small and the LLM bill tiny; set <c>EnabledNpcs</c> to "" or "*" for everyone.
    /// </summary>
    internal bool IsNpcEnabled(string npc)
    {
        string raw = Config.EnabledNpcs;
        if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "*")
            return true;

        foreach (string part in raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(part.Trim(), npc, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static int Fingerprint(string value)
    {
        unchecked
        {
            int hash = 17;
            for (int i = 0; i < value.Length; i++)
                hash = hash * 31 + value[i];
            return hash;
        }
    }

    /// <summary>Queues compression for every NPC that has events in weeks older than the current one.</summary>
    internal void QueueWeeklyCompressions(long currentWeek)
    {
        if (!Config.CompressOnWeekEnd)
            return;

        foreach (NpcMemory memory in Store.All.ToList())
        {
            if (_compressionsToday >= Config.MaxCompressionsPerDay)
            {
                Monitor.Log($"Compression budget for today reached ({Config.MaxCompressionsPerDay}); remaining weeks will be compressed tomorrow.", LogLevel.Warn);
                break;
            }

            List<RawEvent> pending = Store.FinishedUncompressedEvents(memory, currentWeek);
            if (pending.Count == 0)
                continue;

            long week = Store.WeekIndexOf(pending[0]);
            List<RawEvent> slice = pending.Where(x => Store.WeekIndexOf(x) == week).ToList();
            Compressor.Queue(memory, week, slice);
            _compressionsToday++;
        }
    }

    /// <summary>
    /// Compresses the weeks that were already in ValleyTalk's history before this mod existed.
    /// Runs once per NPC (guarded by <see cref="NpcMemory.Backfilled"/>), bypassing the daily
    /// compression budget because it is a one-off catch-up rather than routine work.
    /// </summary>
    internal void BackfillExistingHistory()
    {
        long currentWeek = GameWeek.CurrentWeek(Config.DaysPerWeek);
        int queued = 0;

        foreach (NpcMemory memory in Store.All.ToList())
        {
            if (memory.Backfilled)
                continue;

            List<RawEvent> pending = Store.FinishedUncompressedEvents(memory, currentWeek);
            if (pending.Count == 0)
            {
                memory.Backfilled = true;
                continue;
            }

            var byWeek = pending.GroupBy(Store.WeekIndexOf).OrderBy(g => g.Key).ToList();
            Monitor.Log($"Back-filling {memory.Npc}: {pending.Count} lines across {byWeek.Count} finished week(s) "
                        + $"({string.Join(", ", byWeek.Select(g => GameWeek.DescribeWeek(g.Key, Config.DaysPerWeek)))}) — this costs {byWeek.Count} LLM call(s).",
                        LogLevel.Info);

            foreach (IGrouping<long, RawEvent> week in byWeek)
            {
                Compressor.Queue(memory, week.Key, week.OrderBy(e => e.Day).ThenBy(e => e.Time).ToList());
                queued++;
            }
        }

        if (queued > 0)
        {
            Monitor.Log($"Queued {queued} historical week(s); they are compressed one at a time in the background. "
                        + "Results are mirrored to disk as soon as each one finishes.", LogLevel.Info);
            Store.SaveBackup();
        }
    }

    /// <summary>
    /// Re-applies the commitments that fall on today. Necessary because NPC.Schedule is not
    /// serialised — the game rebuilds it from the asset every morning, so anything we injected
    /// yesterday is gone by the time the promise is due.
    /// </summary>
    internal int ApplyTodayPromises()
    {
        long today = GameWeek.Today;
        int applied = 0;

        foreach (NpcMemory memory in Store.All.ToList())
        {
            if (memory.Promises == null || memory.Promises.Count == 0)
                continue;

            foreach (SchedulePromise promise in memory.Promises.Where(p => p.Day == today && p.ArriveTime > Game1.timeOfDay).ToList())
            {
                string error = NpcScheduler.Apply(promise, Monitor);
                if (error == null)
                {
                    Monitor.Log($"[promise] replayed today's commitment: {promise.Npc} -> {promise.Location} at {promise.ArriveTime}", LogLevel.Info);
                    applied++;
                }
                else
                {
                    Monitor.Log($"[promise] could not replay today's commitment: {error}", LogLevel.Warn);
                    RecordUnfulfilledPromise(promise.Npc, $"\uFF08\u7b54\u5e94\u8fc7\u300c{promise.SourceText}\u300d\u4f46\u6ca1\u80fd\u6210\u884c\uff1a{error}\uFF09");
                }
                memory.Promises.Remove(promise);
            }
        }

        if (applied > 0)
        {
            Store.SaveBackup();
            Injector.RefreshAll();
        }
        return applied;
    }

    private void ApplyCompression(CompressionResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            Monitor.Log($"Compression for {result.Npc} (week {result.WeekIndex}) failed: {result.Error}", LogLevel.Warn);
            return;
        }
        if (result.Memory == null)
            return;

        NpcMemory memory = Store.GetOrCreate(result.Npc);
        memory.Weeks.RemoveAll(w => w.WeekIndex == result.WeekIndex);
        memory.Weeks.Add(result.Memory);
        memory.Weeks = memory.Weeks.OrderBy(w => w.WeekIndex).ToList();
        memory.CompressedThroughWeek = Math.Max(memory.CompressedThroughWeek, result.WeekIndex);
        Compact(memory);

        if (Store.FinishedUncompressedEvents(memory, GameWeek.CurrentWeek(Config.DaysPerWeek)).Count == 0)
            memory.Backfilled = true;

        Store.SaveBackup();
        Injector.Refresh(result.Npc);
        Monitor.Log($"Long-term memory updated for {result.Npc}: {result.Memory.Range} — {Trim(result.Memory.Summary, 90)}", LogLevel.Info);
    }

    /// <summary>Folds the oldest weeks together once the configured cap is exceeded.</summary>
    private void Compact(NpcMemory memory)
    {
        int max = Math.Max(1, Config.MaxWeeklySummaries);
        if (memory.Weeks.Count <= max)
            return;

        int excess = memory.Weeks.Count - max;
        List<WeeklyMemory> oldest = memory.Weeks.Take(excess).ToList();
        List<WeeklyMemory> keep = memory.Weeks.Skip(excess).ToList();

        var merged = new WeeklyMemory
        {
            WeekIndex = oldest[0].WeekIndex,
            Range = $"{oldest[0].Range} 起（已合并 {oldest.Count} 周）",
            Summary = string.Join(" ", oldest.Select(w => w.Summary).Where(s => !string.IsNullOrWhiteSpace(s))),
            Relationship = oldest.Last().Relationship,
            CreatedUtc = DateTime.UtcNow.ToString("o")
        };
        foreach (WeeklyMemory w in oldest)
        {
            merged.Facts.AddRange(w.Facts);
            merged.Promises.AddRange(w.Promises);
        }
        merged.Facts = merged.Facts.Distinct().Take(20).ToList();
        merged.Promises = merged.Promises.Distinct().Take(20).ToList();

        keep.Insert(0, merged);
        memory.Weeks = keep;
    }

    private static string Trim(string value, int max)
        => string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max) + "...";
}
