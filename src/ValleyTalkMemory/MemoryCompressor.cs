using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;

namespace ValleyTalkMemory;

internal sealed class CompressionResult
{
    public string Npc { get; set; }

    public long WeekIndex { get; set; }

    public WeeklyMemory Memory { get; set; }

    public string Error { get; set; }
}

/// <summary>
/// Turns one finished in-game week of raw, timestamped lines into a durable long-term
/// memory entry.
///
/// Calls are processed strictly one at a time by a single background worker: a back-fill over a
/// large save can queue hundreds of weeks, and firing those concurrently would hammer the API
/// (and the player's quota). Results are drained on the game thread.
/// </summary>
internal sealed class MemoryCompressor
{
    private readonly LlmClient _llm;
    private readonly IMonitor _monitor;
    private readonly ModConfig _config;
    private readonly ConcurrentQueue<PendingJob> _pending = new ConcurrentQueue<PendingJob>();
    private readonly ConcurrentQueue<CompressionResult> _results = new ConcurrentQueue<CompressionResult>();
    private int _workerStarted;

    public MemoryCompressor(LlmClient llm, IMonitor monitor, ModConfig config)
    {
        _llm = llm;
        _monitor = monitor;
        _config = config;
    }

    /// <summary>Number of weeks waiting to be compressed.</summary>
    public int InFlight => _pending.Count;

    public bool TryDequeue(out CompressionResult result) => _results.TryDequeue(out result);

    public void Queue(NpcMemory memory, long weekIndex, List<RawEvent> events)
    {
        if (events == null || events.Count == 0)
            return;

        _pending.Enqueue(new PendingJob
        {
            Npc = memory.Npc,
            WeekIndex = weekIndex,
            Range = GameWeek.DescribeWeek(weekIndex, _config.DaysPerWeek),
            Transcript = BuildTranscript(memory.Npc, events, _config)
        });

        EnsureWorker();
    }

    private void EnsureWorker()
    {
        if (Interlocked.Exchange(ref _workerStarted, 1) == 1)
            return;
        _ = Task.Run(WorkerLoop);
    }

    private async Task WorkerLoop()
    {
        while (true)
        {
            if (!_pending.TryDequeue(out PendingJob job))
            {
                await Task.Delay(500).ConfigureAwait(false);
                continue;
            }

            var result = new CompressionResult { Npc = job.Npc, WeekIndex = job.WeekIndex };
            _monitor?.Log($"Compressing {job.Range} for {job.Npc}...", LogLevel.Info);

            try
            {
                string raw = await _llm
                    .CompleteAsync(BuildSystemPrompt(), BuildUserPrompt(job), _config.QueryTimeoutSeconds, CancellationToken.None)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(raw))
                {
                    result.Error = "no response";
                }
                else
                {
                    result.Memory = Parse(job, raw);
                    if (result.Memory == null)
                        result.Error = "unparseable response";
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            finally
            {
                _results.Enqueue(result);
            }

            // Small gap between calls so a large back-fill stays gentle.
            await Task.Delay(250).ConfigureAwait(false);
        }
    }

    private sealed class PendingJob
    {
        public string Npc;
        public long WeekIndex;
        public string Range;
        public string Transcript;
    }

    private string BuildSystemPrompt()
    {
        string language = string.IsNullOrWhiteSpace(_config.MemoryLanguage) ? "简体中文" : _config.MemoryLanguage;
        return
            "你是一个游戏角色记忆压缩引擎。你会收到某个星露谷 NPC 在游戏内一周里与玩家（农夫）的对话记录，"
            + "其中也包含该 NPC 只是旁听到的对话。请把它压缩成一条可以长期保留的记忆。"
            + $"全部输出必须使用{language}。"
            + "只记录对话中真实出现过的信息，不要编造。"
            + "严格返回 JSON，不要任何解释文字或代码块标记，格式为："
            + "{\"summary\":\"本周发生的事情的连贯叙述，2-4句\","
            + "\"facts\":[\"关于农夫或他人的稳定事实，如喜好、家庭、工作、性格\"],"
            + "\"promises\":[\"该NPC明确说过自己打算做、答应做或邀请农夫一起做的事，写清时间/地点/条件；没有就留空数组\"],"
            + "\"relationship\":\"本周两人关系的微妙变化，一句话\"}";
    }

    private static string BuildUserPrompt(PendingJob job)
        => $"NPC 名字：{job.Npc}\n游戏内时间范围：{job.Range}\n\n本周对话记录（按时间先后）：\n{job.Transcript}\n\n请返回 JSON。";

    private static string BuildTranscript(string npc, List<RawEvent> events, ModConfig config)
    {
        var sb = new StringBuilder();
        foreach (RawEvent e in MemoryMerge.WithoutCoveredLines(events))
            sb.AppendLine(MemoryTextBuilder.Format(e, npc, config));
        return sb.ToString();
    }

    /// <summary>Parses the model's JSON answer, tolerating code fences and stray prose.</summary>
    private WeeklyMemory Parse(PendingJob job, string raw)
    {
        string json = raw.Trim();
        int start = json.IndexOf('{');
        int end = json.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            _monitor?.Log($"Compression for {job.Npc} did not contain JSON: {raw.Substring(0, Math.Min(200, raw.Length))}", LogLevel.Warn);
            return null;
        }
        json = json.Substring(start, end - start + 1);

        try
        {
            JObject obj = JObject.Parse(json);
            return new WeeklyMemory
            {
                WeekIndex = job.WeekIndex,
                Range = job.Range,
                Summary = ((string)obj["summary"] ?? "").Trim(),
                Facts = ReadStringArray(obj["facts"]),
                Promises = ReadStringArray(obj["promises"]),
                Relationship = ((string)obj["relationship"] ?? "").Trim(),
                CreatedUtc = DateTime.UtcNow.ToString("o")
            };
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Could not parse compression JSON for {job.Npc}: {ex.Message}", LogLevel.Warn);
            return null;
        }
    }

    private static List<string> ReadStringArray(JToken token)
    {
        var list = new List<string>();
        if (token is JArray array)
        {
            foreach (JToken item in array)
            {
                string text = item?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    list.Add(text);
            }
        }
        else if (token != null && token.Type == JTokenType.String)
        {
            string text = token.ToString().Trim();
            if (text.Length > 0)
                list.Add(text);
        }
        return list;
    }
}
