using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ValleyTalkMemory;

/// <summary>Which parts of ValleyTalk's history are worth remembering for one NPC.</summary>
internal sealed class HistoryScope
{
    /// <summary>Conversations the NPC merely overheard from other villagers.</summary>
    public bool IncludeOverheard { get; set; }

    /// <summary>The farmer's own lines in conversations with this NPC.</summary>
    public bool IncludeFarmerLines { get; set; } = true;

    /// <summary>Third-party event logs (festivals, cutscenes) where the NPC was only a listener.</summary>
    public bool IncludeThirdPartyEvents { get; set; }

    public static HistoryScope From(ModConfig config) => new HistoryScope
    {
        IncludeOverheard = config.IncludeOverheard,
        IncludeFarmerLines = config.IncludeFarmerLines,
        IncludeThirdPartyEvents = config.IncludeThirdPartyEvents
    };
}

/// <summary>
/// Tolerant reader for ValleyTalk's persisted <c>StardewEventHistory</c> JSON.
///
/// We parse by (case-insensitive) field name instead of binding to ValleyTalk's internal types,
/// so a ValleyTalk update can't break us at load time. Each individual spoken line becomes its own
/// <see cref="RawEvent"/>, which lets the same line logged by several history buckets collapse
/// into one memory entry.
///
/// By default only the NPC's own conversations are kept: an NPC has no way of knowing what other
/// villagers are planning, so overheard chatter and third-party event logs are excluded.
/// </summary>
internal static class VtHistoryParser
{
    public static List<RawEvent> Parse(string npcName, string json, HistoryScope scope)
    {
        var result = new List<RawEvent>();
        if (string.IsNullOrWhiteSpace(json))
            return result;

        scope ??= new HistoryScope();

        JObject root;
        try
        {
            root = JObject.Parse(json);
        }
        catch
        {
            return result;
        }

        ReadBucket(root, "DialogueHistory", "say", npcName, null, scope, result);

        if (scope.IncludeThirdPartyEvents)
            ReadBucket(root, "EventHistory", "event", npcName, null, scope, result);

        if (scope.IncludeOverheard)
            ReadBucket(root, "OverheardHistory", "overheard", npcName, "name", scope, result);

        ReadBucket(root, "ConversationHistory", "say", npcName, null, scope, result);
        return result;
    }

    private static void ReadBucket(JObject root, string bucket, string kind, string npcName, string speakerField, HistoryScope scope, List<RawEvent> into)
    {
        if (Prop(root, bucket) is not JArray list)
            return;

        foreach (JToken item in list)
        {
            if (item is not JObject entry)
                continue;
            if (Prop(entry, "Item1") is not JObject time || Prop(entry, "Item2") is not JObject payload)
                continue;

            long day = ReadDay(time);
            int clock = ReadInt(time, "timeOfDay") ?? 600;
            string speaker = speakerField != null ? (Prop(payload, speakerField)?.ToString() ?? "") : npcName;

            foreach (RawEvent e in Extract(payload, kind, speaker, npcName, day, clock, scope))
                into.Add(e);
        }
    }

    /// <summary>Expands one history payload into one event per spoken line.</summary>
    private static IEnumerable<RawEvent> Extract(JObject payload, string kind, string speaker, string npcName, long day, int clock, HistoryScope scope)
    {
        JToken dialogues = Prop(payload, "Dialogues") ?? Prop(payload, "dialogues");
        if (dialogues is JArray dlgArray)
        {
            foreach (JToken token in dlgArray)
            {
                string text = LineText(token);
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                yield return Make(day, clock, kind, speaker, text);
            }
            yield break;
        }

        if (Prop(payload, "ConversationElements") is JArray elements)
        {
            foreach (JToken token in elements)
            {
                string text = LineText(token);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                bool farmer = ReadBool(token as JObject, "IsPlayerLine") ?? false;
                if (farmer && !scope.IncludeFarmerLines)
                    continue;

                yield return Make(day, clock, farmer ? "farmer" : kind, farmer ? "" : npcName, text);
            }
            yield break;
        }

        if (Prop(payload, "chatHistory") is JArray chat)
        {
            foreach (JToken token in chat)
            {
                string text = token?.ToString();
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                yield return Make(day, clock, kind, speaker, text);
            }
        }
    }

    private static RawEvent Make(long day, int clock, string kind, string speaker, string text)
        => new RawEvent
        {
            Day = day,
            Time = clock,
            Kind = kind,
            Speaker = speaker ?? "",
            Text = DialogueTextCleaner.Clean(text)
        };

    private static string LineText(JToken token)
    {
        if (token is JValue value)
            return value.ToString();
        if (token is not JObject obj)
            return token?.ToString() ?? "";

        foreach (string name in new[] { "Text", "text", "Value", "value" })
        {
            JToken t = Prop(obj, name);
            if (t != null && t.Type != JTokenType.Null)
                return t.ToString();
        }
        return "";
    }

    private static long ReadDay(JObject time)
    {
        int? year = ReadInt(time, "year") ?? ReadInt(time, "Year");
        int? dayOfMonth = ReadInt(time, "dayOfMonth") ?? ReadInt(time, "DayOfMonth");
        JToken seasonToken = Prop(time, "season") ?? Prop(time, "Season");
        int seasonIndex = seasonToken == null
            ? 0
            : seasonToken.Type == JTokenType.Integer
                ? Math.Max(0, Math.Min(3, seasonToken.Value<int>()))
                : GameWeek.SeasonIndexFromName(seasonToken.ToString());

        return GameWeek.ToDay(year ?? 1, seasonIndex, dayOfMonth ?? 1);
    }

    private static int? ReadInt(JObject obj, string name)
    {
        JToken token = Prop(obj, name);
        if (token == null || token.Type == JTokenType.Null)
            return null;
        if (token.Type == JTokenType.Integer)
            return token.Value<int>();
        return int.TryParse(token.ToString(), out int parsed) ? parsed : (int?)null;
    }

    private static bool? ReadBool(JObject obj, string name)
    {
        if (obj == null)
            return null;
        JToken token = Prop(obj, name);
        if (token == null || token.Type == JTokenType.Null)
            return null;
        if (token.Type == JTokenType.Boolean)
            return token.Value<bool>();
        return bool.TryParse(token.ToString(), out bool parsed) ? parsed : (bool?)null;
    }

    /// <summary>Case-insensitive property lookup.</summary>
    private static JToken Prop(JObject obj, string name)
    {
        if (obj == null)
            return null;
        foreach (KeyValuePair<string, JToken> kvp in obj)
        {
            if (string.Equals(kvp.Key, name, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }
        return null;
    }
}
