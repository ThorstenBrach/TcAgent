// Advisor (docs/ADVISOR.md): an agent that looks, advises and hands over a ready order (a prompt) - it changes nothing itself.
// Advisor Shadow runs in the background (at night, after a commit, after a failed pipeline), Advisor Live helps a person at
// the running plant (later). This file is the record of the advice: one JSONL file per month in the project, in Git.
//
//   <solution dir>\.tcagent\advisor\2026-11.jsonl
//     {"id":"AD-1a2b3c4d","time":"…","trigger":"nightly","kind":"review","finding":"…","evidence":["…"],"proposal":"…",
//      "diff":"…","order":"…","workflow":"bug-fix","confidence":"high","why":"…"}
//     {"rate":"AD-1a2b3c4d","rating":"right","by":"name","time":"…","comment":"…"}      a rating, appended later
//
// Append only: a rating is a line of its own, so two people never edit the same line and Git merges cleanly.
// Pure logic, so the unit tests link this file.
//
// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TcAgent;

public static class Advisor
{
    public static readonly string[] Ratings = { "right", "partly", "wrong", "n/a" };
    public static readonly string[] Confidences = { "high", "medium", "low" };
    public const int Window = 20, MinRated = 10, StaleDays = 14;

    public sealed class Item
    {
        public string Id, Trigger, Kind, Finding, Proposal, Diff, Order, Workflow, Confidence, Why, Run;
        public DateTime Time;
        public List<string> Evidence = new();
        public string Rating, RatedBy, Comment;
        public DateTime? RatedAt;
    }

    public static string Folder(string solutionDir) =>
        string.IsNullOrEmpty(solutionDir) ? throw new Exception("No solution - the advice is kept in the project (.tcagent\\advisor)")
                                          : Path.Combine(solutionDir, ".tcagent", "advisor");

    static string FileOf(string solutionDir, DateTime t) => Path.Combine(Folder(solutionDir), t.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl");

    static readonly JsonSerializerOptions Line = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static void Append(string solutionDir, DateTime t, JsonObject o)
    {
        Directory.CreateDirectory(Folder(solutionDir));
        File.AppendAllText(FileOf(solutionDir, t), o.ToJsonString(Line) + "\n", new UTF8Encoding(false));
    }

    /// <summary>Checks and stores one piece of advice. Throws ArgumentException with a message for the agent.</summary>
    public static Item Record(string solutionDir, Item it)
    {
        if (string.IsNullOrWhiteSpace(it.Finding)) throw new ArgumentException("finding: one sentence - what you found");
        if (it.Evidence.Count == 0) throw new ArgumentException("evidence: at least one quote from a tool output, with its place (object, line, variable, value)");
        if (string.IsNullOrWhiteSpace(it.Order)) throw new ArgumentException("order: the prompt a person sends to an agent to act on the finding");
        it.Confidence = (it.Confidence ?? "medium").Trim().ToLowerInvariant();
        if (!Confidences.Contains(it.Confidence)) throw new ArgumentException("confidence: high, medium or low");
        it.Kind = string.IsNullOrWhiteSpace(it.Kind) ? "review" : it.Kind.Trim().ToLowerInvariant();
        it.Trigger = string.IsNullOrWhiteSpace(it.Trigger) ? "chat" : it.Trigger.Trim().ToLowerInvariant();
        it.Time = DateTime.Now;
        it.Id = "AD-" + Guid.NewGuid().ToString("N")[..8];
        var o = new JsonObject
        {
            ["id"] = it.Id, ["time"] = it.Time.ToString("o", CultureInfo.InvariantCulture), ["trigger"] = it.Trigger, ["kind"] = it.Kind,
            ["finding"] = it.Finding.Trim(), ["evidence"] = new JsonArray(it.Evidence.Select(e => (JsonNode)e).ToArray()),
            ["proposal"] = it.Proposal, ["diff"] = it.Diff, ["order"] = it.Order.Trim(), ["workflow"] = it.Workflow,
            ["confidence"] = it.Confidence, ["why"] = it.Why, ["run"] = it.Run,
        };
        foreach (var k in o.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) o.Remove(k);
        Append(solutionDir, it.Time, o);
        return it;
    }

    public static void Rate(string solutionDir, string id, string rating, string by, string comment)
    {
        rating = (rating ?? "").Trim().ToLowerInvariant();
        if (!Ratings.Contains(rating)) throw new ArgumentException("rating: right, partly, wrong or n/a");
        if (Load(solutionDir).All(x => x.Id != id)) throw new ArgumentException($"no advice {id}");
        var now = DateTime.Now;
        var o = new JsonObject { ["rate"] = id, ["rating"] = rating, ["by"] = by, ["time"] = now.ToString("o", CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(comment)) o["comment"] = comment.Trim();
        Append(solutionDir, now, o);
    }

    /// <summary>All advice of the project, oldest first, with the latest rating of each.</summary>
    public static List<Item> Load(string solutionDir)
    {
        var dir = Folder(solutionDir);
        if (!Directory.Exists(dir)) return new List<Item>();
        return Parse(Directory.GetFiles(dir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal).SelectMany(File.ReadAllLines));
    }

    public static List<Item> Parse(IEnumerable<string> lines)
    {
        var items = new List<Item>();
        var byId = new Dictionary<string, Item>();
        foreach (var l in lines)
        {
            if (string.IsNullOrWhiteSpace(l)) continue;
            JsonObject o;
            try { o = JsonNode.Parse(l) as JsonObject; } catch { continue; }      // a broken line (merge) must not hide the rest
            if (o == null) continue;
            string S(string k) => o[k] is JsonValue v ? v.ToString() : null;
            DateTime? T(string k) => DateTime.TryParse(S(k), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;
            if (S("rate") is { } rid)
            {
                if (byId.TryGetValue(rid, out var r)) { r.Rating = S("rating"); r.RatedBy = S("by"); r.RatedAt = T("time"); r.Comment = S("comment"); }
                continue;
            }
            var it = new Item
            {
                Id = S("id"), Time = T("time") ?? DateTime.MinValue, Trigger = S("trigger"), Kind = S("kind"), Finding = S("finding"),
                Proposal = S("proposal"), Diff = S("diff"), Order = S("order"), Workflow = S("workflow"), Confidence = S("confidence"), Why = S("why"), Run = S("run"),
                Evidence = (o["evidence"] as JsonArray)?.Select(x => x?.ToString()).Where(x => x != null).ToList() ?? new List<string>(),
            };
            if (it.Id == null || byId.ContainsKey(it.Id)) continue;
            byId[it.Id] = it; items.Add(it);
        }
        return items;
    }

    public sealed record Quota(string Kind, int Rated, int Right, int Open, double? Rate);

    /// <summary>The hit rate per kind of task: "right" among the last 20 rated advice; under 10 ratings no rate. Advice that
    /// stayed unrated for 14 days is no longer counted as open.</summary>
    public static List<Quota> Quotas(IEnumerable<Item> items, DateTime now) =>
        items.GroupBy(i => i.Kind ?? "review").OrderBy(g => g.Key).Select(g =>
        {
            var rated = g.Where(i => i.Rating is "right" or "partly" or "wrong").OrderByDescending(i => i.RatedAt ?? i.Time).Take(Window).ToList();
            int right = rated.Count(i => i.Rating == "right");
            int open = g.Count(i => i.Rating == null && (now - i.Time).TotalDays <= StaleDays);
            return new Quota(g.Key, rated.Count, right, open, rated.Count >= MinRated ? (double)right / rated.Count : null);
        }).ToList();

    public static string FormatQuota(Quota q) =>
        $"{q.Kind}: " + (q.Rate is { } r ? $"{r * 100:0} % right ({q.Right} of {q.Rated})" : $"too few ratings ({q.Rated} of {MinRated})") + $", {q.Open} open";

    public static string List(List<Item> items, bool openOnly, int max)
    {
        var shown = items.Where(i => !openOnly || i.Rating == null).OrderByDescending(i => i.Time).Take(max).ToList();
        if (shown.Count == 0) return openOnly ? "No open advice." : "No advice yet.";
        var sb = new StringBuilder();
        foreach (var i in shown)
            sb.AppendLine($"{i.Id}  {i.Time:yyyy-MM-dd HH:mm}  {i.Kind}/{i.Trigger}  {i.Confidence}  {(i.Rating ?? "not rated")}\n  {i.Finding}");
        return sb.ToString();
    }

    public static JsonObject ToJson(Item i) => new()
    {
        ["id"] = i.Id, ["time"] = i.Time.ToString("o", CultureInfo.InvariantCulture), ["trigger"] = i.Trigger, ["kind"] = i.Kind, ["finding"] = i.Finding,
        ["evidence"] = new JsonArray(i.Evidence.Select(e => (JsonNode)e).ToArray()), ["proposal"] = i.Proposal, ["diff"] = i.Diff, ["order"] = i.Order,
        ["workflow"] = i.Workflow, ["confidence"] = i.Confidence, ["why"] = i.Why, ["rating"] = i.Rating, ["ratedBy"] = i.RatedBy,
        ["ratedAt"] = i.RatedAt?.ToString("o", CultureInfo.InvariantCulture), ["comment"] = i.Comment,
    };
}
