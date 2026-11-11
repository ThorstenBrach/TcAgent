// The conversations of the TcAgent Chat, kept per TwinCAT solution - LOCAL only, never in Git (docs/USER_GUIDE.md, "Chat
// history"):
//
//   %LOCALAPPDATA%\TcAgent\chats\<solution>-<hash>\solution.json                  the solution folder of the conversations
//   %LOCALAPPDATA%\TcAgent\chats\<solution>-<hash>\<yyyyMMdd-HHmmss>-<id>.jsonl   one conversation, one event per line
//
// <hash>: 8 hex digits of the full solution folder, so two checkouts with the same solution name do not mix.
// An event is one JSON object: {"t":"2026-11-11T17:42:03","type":"question","text":"..."}. Types:
//   start {backend}   question {text, where, attachments[]}   answer {text, seconds, tools}   tool {name, ok, result}
//   plan {items[{text, status}]}   card {question, answer}   stopped {}   session {id}
// Only names and first lines of tool results are kept, no attachment contents.
//
// The chat window writes (vsbridge links this file, .NET Framework), tc_chat_history reads (TcAgent server, .NET 8) - so this
// file has no dependencies besides the JSON of each framework. Pure file logic: the unit tests link it.
//
// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TcAgent
{
    public static class ChatHistory
    {
        public const int KeepDays = 90, KeepPerProject = 50;
        /// <summary>A conversation without an event for this long is finished; the next question starts a new one.</summary>
        public static readonly TimeSpan IdleLimit = TimeSpan.FromHours(8);
        public const string NoSolution = "_no-solution";

        public static string RootOverride;      // tests
        public static string Root => RootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "chats");

        static readonly object writeLock = new object();

        // ------------------------------------------------------------------ the folder of a solution
        /// <summary>The name of a project: the solution file if there is exactly one, otherwise the folder.</summary>
        public static string ProjectName(string solutionDir)
        {
            if (string.IsNullOrEmpty(solutionDir)) return null;
            try { var s = Directory.GetFiles(solutionDir, "*.sln"); if (s.Length == 1) return Path.GetFileNameWithoutExtension(s[0]); } catch { }
            return Path.GetFileName(solutionDir.TrimEnd('\\', '/'));
        }

        public static string Key(string solutionDir)
        {
            if (string.IsNullOrWhiteSpace(solutionDir)) return null;
            string full;
            try { full = Path.GetFullPath(solutionDir.Trim()); } catch { full = solutionDir.Trim(); }
            return full.TrimEnd('\\', '/').ToLowerInvariant();
        }

        public static string FolderFor(string solutionDir)
        {
            var key = Key(solutionDir);
            if (key == null) return Path.Combine(Root, NoSolution);
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
            var name = Regex.Replace(ProjectName(solutionDir) ?? "solution", @"[^\w.-]+", "_");
            if (name.Length > 40) name = name.Substring(0, 40);
            return Path.Combine(Root, name + "-" + string.Concat(hash.Take(4).Select(b => b.ToString("x2"))));
        }

        // ------------------------------------------------------------------ writing
        /// <summary>A new conversation file (created with its first event).</summary>
        public static string NewFile(string solutionDir, DateTime now)
        {
            var folder = FolderFor(solutionDir);
            Directory.CreateDirectory(folder);
            var info = Path.Combine(folder, "solution.json");
            if (!string.IsNullOrEmpty(solutionDir) && !File.Exists(info))
                try { File.WriteAllText(info, ToJson(new Dictionary<string, object> { ["solutionDir"] = solutionDir, ["name"] = ProjectName(solutionDir) }), new UTF8Encoding(false)); } catch { }
            var id = Guid.NewGuid().ToString("N").Substring(0, 4);
            return Path.Combine(folder, now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + id + ".jsonl");
        }

        /// <summary>One event at the end of the conversation file. Never throws: the chat goes on without its history.</summary>
        public static bool Append(string file, string type, Dictionary<string, object> data = null, DateTime? time = null)
        {
            if (string.IsNullOrEmpty(file)) return false;
            var e = new Dictionary<string, object> { ["t"] = (time ?? DateTime.Now).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), ["type"] = type };
            if (data != null) foreach (var kv in data) if (kv.Value != null) e[kv.Key] = kv.Value;
            try
            {
                var line = ToJson(e).Replace("\r", "").Replace("\n", "\\n") + "\n";
                lock (writeLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.AppendAllText(file, line, new UTF8Encoding(false));
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Removes conversations older than <paramref name="days"/> and all but the newest <paramref name="max"/> of
        /// the project; the conversation in use stays. Returns the number removed.</summary>
        public static int Prune(string solutionDir, int days = KeepDays, int max = KeepPerProject, string keep = null, DateTime? now = null)
        {
            var n = 0;
            var files = Files(solutionDir);
            var limit = (now ?? DateTime.Now) - TimeSpan.FromDays(Math.Max(1, days));
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                if (keep != null && string.Equals(Path.GetFullPath(f), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
                if (i < max && LastWrite(f) >= limit) continue;
                try { File.Delete(f); n++; } catch { }
            }
            return n;
        }

        /// <summary>Deletes every conversation of the project. Returns the number deleted.</summary>
        public static int DeleteProject(string solutionDir)
        {
            var folder = FolderFor(solutionDir);
            if (!Directory.Exists(folder)) return 0;
            var n = Directory.GetFiles(folder, "*.jsonl").Length;
            try { Directory.Delete(folder, true); } catch { }
            return n;
        }

        // ------------------------------------------------------------------ reading
        public sealed class Entry
        {
            public DateTime Time;
            public string Type;
            public Dictionary<string, object> Data;
            public string S(string key) => Data != null && Data.TryGetValue(key, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        }

        public sealed class PlanItem
        {
            public string Text, Status;
            public bool Open => !string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase);
        }

        public sealed class Conversation
        {
            public string File;
            public List<Entry> Entries = new List<Entry>();
            public DateTime Started => Entries.Count > 0 ? Entries[0].Time : DateTime.MinValue;
            public DateTime Last => Entries.Count > 0 ? Entries[Entries.Count - 1].Time : DateTime.MinValue;
            public string Backend => Entries.LastOrDefault(e => e.Type == "start")?.S("backend");
            public List<Entry> Questions => Entries.Where(e => e.Type == "question").ToList();
            public string Title => Short(Questions.FirstOrDefault()?.S("text"), 80) ?? "(no question)";
            public string SessionId => Entries.LastOrDefault(e => e.Type == "session")?.S("id");

            /// <summary>Question and answer pairs, in order (a question without an answer has null).</summary>
            public List<KeyValuePair<string, string>> Pairs()
            {
                var list = new List<KeyValuePair<string, string>>();
                foreach (var e in Entries)
                {
                    if (e.Type == "question") list.Add(new KeyValuePair<string, string>(e.S("text") ?? "", null));
                    else if (e.Type == "answer" && list.Count > 0 && list[list.Count - 1].Value == null)
                        list[list.Count - 1] = new KeyValuePair<string, string>(list[list.Count - 1].Key, e.S("text") ?? "");
                }
                return list;
            }

            /// <summary>The newest plan (task list) of the agent.</summary>
            public List<PlanItem> Plan()
            {
                var p = Entries.LastOrDefault(e => e.Type == "plan");
                if (p == null || !(p.Data.TryGetValue("items", out var items) && items is IEnumerable list) || items is string) return new List<PlanItem>();
                return list.OfType<object>().Select(o => o as IDictionary<string, object>).Where(d => d != null)
                           .Select(d => new PlanItem { Text = d.TryGetValue("text", out var t) ? Convert.ToString(t) : "", Status = d.TryGetValue("status", out var s) ? Convert.ToString(s) : "" })
                           .Where(i => !string.IsNullOrWhiteSpace(i.Text)).ToList();
            }

            /// <summary>The last question was stopped by the user or got no answer.</summary>
            public bool Unfinished
            {
                get
                {
                    var last = Entries.LastOrDefault(e => e.Type == "question" || e.Type == "answer" || e.Type == "stopped");
                    return last != null && last.Type != "answer";
                }
            }
        }

        /// <summary>The conversation files of a project, newest first.</summary>
        public static List<string> Files(string solutionDir)
        {
            var folder = FolderFor(solutionDir);
            if (!Directory.Exists(folder)) return new List<string>();
            return Directory.GetFiles(folder, "*.jsonl").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).ToList();
        }

        static DateTime LastWrite(string f) { try { return File.GetLastWriteTime(f); } catch { return DateTime.MinValue; } }

        public static Conversation Load(string file)
        {
            var c = new Conversation { File = file };
            string[] lines;
            try { lines = File.ReadAllLines(file, Encoding.UTF8); } catch { return c; }
            foreach (var line in lines)
            {
                if (line.Trim().Length == 0) continue;
                Dictionary<string, object> d;
                try { d = FromJson(line); } catch { continue; }      // a line cut off by a crash
                if (d == null) continue;
                var e = new Entry { Data = d, Type = d.TryGetValue("type", out var ty) ? Convert.ToString(ty) : "" };
                DateTime.TryParse(d.TryGetValue("t", out var t) ? Convert.ToString(t) : "", CultureInfo.InvariantCulture, DateTimeStyles.None, out e.Time);
                c.Entries.Add(e);
            }
            return c;
        }

        /// <summary>The newest conversation of the project that has a question, or null.</summary>
        public static Conversation Latest(string solutionDir)
        {
            foreach (var f in Files(solutionDir))
            {
                var c = Load(f);
                if (c.Questions.Count > 0) return c;
            }
            return null;
        }

        // ------------------------------------------------------------------ text for the agent
        /// <summary>The short handover for the first question after the project was opened again: when, what was asked,
        /// the open tasks of the agent's plan, whether it was stopped. About 2000 characters at most.</summary>
        public static string Handover(Conversation c, int maxChars = 2000)
        {
            if (c == null || c.Questions.Count == 0) return null;
            var sb = new StringBuilder();
            var qs = c.Questions;
            sb.Append($"{c.Started:yyyy-MM-dd HH:mm} to {c.Last:HH:mm}").Append(c.Backend != null ? " with " + c.Backend : "")
              .Append($", {qs.Count} question{(qs.Count == 1 ? "" : "s")}.\n");
            sb.Append("Questions (oldest first):\n");
            foreach (var q in qs.Skip(Math.Max(0, qs.Count - 8))) sb.Append("- ").Append(Short(q.S("text"), 160)).Append('\n');
            var answer = c.Entries.LastOrDefault(e => e.Type == "answer")?.S("text");
            if (!string.IsNullOrWhiteSpace(answer)) sb.Append("Last answer (shortened): ").Append(Short(answer, 500)).Append('\n');
            var open = c.Plan().Where(i => i.Open).ToList();
            if (open.Count > 0)
            {
                sb.Append("Open tasks of your plan then:\n");
                foreach (var i in open.Take(10)) sb.Append("- [").Append(i.Status).Append("] ").Append(Short(i.Text, 140)).Append('\n');
            }
            if (c.Unfinished) sb.Append("The last question was stopped or got no answer.\n");
            var text = sb.ToString();
            if (text.Length > maxChars) text = text.Substring(0, maxChars) + "...\n";
            return text + "The project may have changed since then (other people, Git) - check open tasks against the current state before you continue. " +
                          "Details: tcagent tool tc_chat_history (action=read; list, search).";
        }

        /// <summary>The conversations of the project, newest first, one line each.</summary>
        public static string ListText(string solutionDir, int max = 20)
        {
            var files = Files(solutionDir);
            if (files.Count == 0) return "No saved chat conversations for this project (folder " + FolderFor(solutionDir) + ").";
            var sb = new StringBuilder($"{files.Count} conversation(s), newest first:\n");
            foreach (var f in files.Take(max))
            {
                var c = Load(f);
                var open = c.Plan().Count(i => i.Open);
                sb.Append($"  {Path.GetFileNameWithoutExtension(f)}  {c.Started:yyyy-MM-dd HH:mm}  {c.Questions.Count} question(s)")
                  .Append(open > 0 ? $", {open} open task(s)" : "").Append(c.Unfinished ? ", stopped" : "").Append("  ").Append(c.Title).Append('\n');
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>A conversation as text: questions, answers (shortened unless full), tool calls in one line, the plan.</summary>
        public static string Format(Conversation c, bool full = false)
        {
            if (c == null || c.Entries.Count == 0) return "Conversation not found or empty.";
            var sb = new StringBuilder();
            sb.Append($"Conversation {Path.GetFileNameWithoutExtension(c.File)}, {c.Started:yyyy-MM-dd HH:mm} to {c.Last:yyyy-MM-dd HH:mm}")
              .Append(c.Backend != null ? ", " + c.Backend : "").Append("\n\n");
            var tools = new List<string>();
            void FlushTools() { if (tools.Count > 0) { sb.Append("  (tools: ").Append(string.Join(", ", tools)).Append(")\n"); tools.Clear(); } }
            foreach (var e in c.Entries)
                switch (e.Type)
                {
                    case "question":
                        FlushTools();
                        sb.Append($"[{e.Time:HH:mm}] User: ").Append(e.S("text")).Append(e.S("where") != null ? "  (" + e.S("where") + ")" : "").Append('\n');
                        break;
                    case "answer":
                        FlushTools();
                        var a = e.S("text") ?? "";
                        sb.Append($"[{e.Time:HH:mm}] Agent: ").Append(full ? a : Short(a, 1200)).Append("\n\n");
                        break;
                    case "tool": tools.Add((e.S("ok") == "False" ? "✗ " : "") + e.S("name")); break;
                    case "card": FlushTools(); sb.Append("  Asked: ").Append(Short(e.S("question"), 200)).Append(" -> ").Append(Short(e.S("answer") ?? "(no answer)", 200)).Append('\n'); break;
                    case "stopped": FlushTools(); sb.Append("  (stopped by the user)\n"); break;
                }
            FlushTools();
            var plan = c.Plan();
            if (plan.Count > 0)
            {
                sb.Append("\nPlan of the agent at the end:\n");
                foreach (var i in plan) sb.Append("- [").Append(i.Status).Append("] ").Append(i.Text).Append('\n');
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Questions and answers of the project's conversations that contain all words of the query.</summary>
        public static string Search(string solutionDir, string query, int max = 10)
        {
            var words = (query ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToArray();
            if (words.Length == 0) return "query: one or more words.";
            var hits = new List<string>();
            foreach (var f in Files(solutionDir))
            {
                var c = Load(f);
                foreach (var e in c.Entries.Where(x => x.Type == "question" || x.Type == "answer"))
                {
                    var text = e.S("text") ?? "";
                    var low = text.ToLowerInvariant();
                    if (!words.All(low.Contains)) continue;
                    var at = Math.Max(0, low.IndexOf(words[0], StringComparison.Ordinal) - 80);
                    hits.Add($"{Path.GetFileNameWithoutExtension(f)}  {e.Time:yyyy-MM-dd HH:mm}  {(e.Type == "question" ? "User" : "Agent")}: " +
                             (at > 0 ? "..." : "") + Short(text.Substring(at), 240));
                    if (hits.Count >= max) break;
                }
                if (hits.Count >= max) break;
            }
            return hits.Count == 0 ? "No match in the saved conversations of this project." : string.Join("\n", hits);
        }

        /// <summary>The conversation file of an id ("20261111-174203-a1b2", its start, or "latest").</summary>
        public static string Find(string solutionDir, string id)
        {
            var files = Files(solutionDir);
            if (string.IsNullOrWhiteSpace(id) || id.Trim().Equals("latest", StringComparison.OrdinalIgnoreCase))
                return files.FirstOrDefault(f => Load(f).Questions.Count > 0) ?? files.FirstOrDefault();
            id = id.Trim();
            return files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(id, StringComparison.OrdinalIgnoreCase))
                ?? files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).StartsWith(id, StringComparison.OrdinalIgnoreCase));
        }

        public static string Short(string s, int n)
        {
            if (s == null) return null;
            s = Regex.Replace(s.Replace("\r", ""), @"\s*\n\s*", " ").Trim();
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        // ------------------------------------------------------------------ JSON of each framework
        // Values come back as string, bool, double/int/decimal, Dictionary<string, object> and a list (IEnumerable).
#if !NET5_0_OR_GREATER     // .NET Framework: the extension (also built with csc by vsbridge\build.ps1, without symbols)
        static System.Web.Script.Serialization.JavaScriptSerializer Js() => new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        public static string ToJson(object o) => Js().Serialize(o);
        public static Dictionary<string, object> FromJson(string s) => Js().Deserialize<Dictionary<string, object>>(s);
#else
        static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        public static string ToJson(object o) => System.Text.Json.JsonSerializer.Serialize(o, JsonOptions);
        public static Dictionary<string, object> FromJson(string s)
        {
            using (var doc = System.Text.Json.JsonDocument.Parse(s)) return Value(doc.RootElement) as Dictionary<string, object>;
        }

        static object Value(System.Text.Json.JsonElement e)
        {
            switch (e.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object: return e.EnumerateObject().ToDictionary(p => p.Name, p => Value(p.Value));
                case System.Text.Json.JsonValueKind.Array: return e.EnumerateArray().Select(Value).ToList();
                case System.Text.Json.JsonValueKind.String: return e.GetString();
                case System.Text.Json.JsonValueKind.Number: return e.TryGetInt64(out var l) ? l : (object)e.GetDouble();
                case System.Text.Json.JsonValueKind.True: return true;
                case System.Text.Json.JsonValueKind.False: return false;
                default: return null;
            }
        }
#endif
    }
}
