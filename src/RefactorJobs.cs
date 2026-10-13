// tc_refactor action=apply as a background job.
// apply waits for the user's clicks (Apply / Apply all / Reject / Cancel) - possibly minutes. A client that reaches this PC
// through a remote bridge gives up after ~60 s and the report is lost while the work goes on. So apply starts a job and waits
// at most waitSeconds (default 45, TCAGENT_JOB_WAIT): finished -> the report as before; otherwise "still waiting - k of n done,
// call tc_refactor action=result job=<id>", and the job keeps running. action=result returns the final report (or the
// progress again, after waiting up to waitSeconds). Only the last few jobs are kept in memory.
//
// Threads: the job runs on its own background thread (never on the STA - it blocks while the user decides); the
// confirmations go through the bridge/elicitation from there, every COM access (planning, edits) through tc.Sta.Run.
// The MCP main loop stays free, so other calls (and action=result) are answered meanwhile.
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TcAgent;

/// <summary>Progress of one tc_refactor apply job (thread-safe).</summary>
sealed class RefactorJob
{
    public readonly string Id;
    public readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    public System.Threading.Tasks.Task<string> Work;
    readonly object gate = new();
    int total, done;
    string report = "", waitingFor;
    bool returnedPending;

    public RefactorJob(string id, int total) { Id = id; this.total = total; }

    public bool Finished => Work?.IsCompleted == true;

    public void Begin(int n) { lock (gate) total = n; }

    public void Progress(int processed, string reportSoFar, string current)
    {
        lock (gate) { done = processed; report = reportSoFar ?? ""; waitingFor = current; }
    }

    public (int Done, int Total, string Report, string WaitingFor) Snapshot() { lock (gate) return (done, total, report, waitingFor); }

    /// <summary>The apply call returns without the final report: true if the job is still running (then its end is audited
    /// separately); false if it finished just now (the caller returns the report itself).</summary>
    public bool MarkPendingIfRunning() { lock (gate) { if (Finished) return false; returnedPending = true; return true; } }

    public bool ReturnedPending { get { lock (gate) return returnedPending; } }
}

public sealed partial class ToolRegistry
{
    readonly List<RefactorJob> refactorJobs = new();      // lock(refactorJobs)
    const int KeepJobs = 5;
    int refactorJobCounter;

    /// <summary>Calls that wait for the user themselves (with their own IDE dialog watch) - not through RunWatchingDialogs.</summary>
    static bool WatchesItself(string name, JsonObject args) =>
        name == "tc_refactor" && (Str(args, "action") ?? "").ToLowerInvariant() is "apply" or "result";

    /// <summary>Action/dryRun/job of a call for the log line "&lt;tool&gt; started [...]" - tc_policy action=audit_check uses it to tell
    /// read-only actions from changes.</summary>
    static string LogAttrs(JsonObject a)
    {
        var parts = new List<string>();
        if (Str(a, "action") is { Length: > 0 } act) parts.Add("action=" + Regex.Replace(act.Length > 40 ? act[..40] : act, @"[\s\[\]]", "_"));
        if (Bool(a, "dryRun", false)) parts.Add("dryRun");
        if (Str(a, "job") is { Length: > 0 } j) parts.Add("job=" + Regex.Replace(j.Length > 40 ? j[..40] : j, @"[\s\[\]]", "_"));
        return parts.Count == 0 ? "" : " [" + string.Join(" ", parts) + "]";
    }

    static int JobWait(JsonObject a)
    {
        int def = int.TryParse(Environment.GetEnvironmentVariable("TCAGENT_JOB_WAIT"), out var e) && e > 0 ? e : 45;
        return Math.Clamp(Int(a, "waitSeconds", def), 1, 3600);
    }

    string RefactorStartJob(JsonObject a)
    {
        if (a["changes"] is not JsonArray changes) throw new Exception("changes is required: [{object, kind, target, name, newName, comment, reason}]");
        var args = a.DeepClone().AsObject();          // the job thread gets its own copy (JsonObject is not thread-safe)
        RefactorJob job;
        lock (refactorJobs)
        {
            var busy = refactorJobs.FirstOrDefault(j => !j.Finished);
            if (busy != null)
            {
                var (d, t, _, w) = busy.Snapshot();
                throw new Exception($"refactoring job {busy.Id} is still waiting for the user's decisions ({d} of {t} done" +
                                    (w != null ? $", the dialog asks about {w}" : "") + $") - call tc_refactor action=result job={busy.Id}; " +
                                    "start further changes only after it has finished.");
            }
            job = new RefactorJob($"rf{++refactorJobCounter}-{DateTime.Now:HHmmss}", changes.Count);
            refactorJobs.Add(job);
            while (refactorJobs.Count > KeepJobs)
            {
                var old = refactorJobs.FirstOrDefault(j => j.Finished && j != job);
                if (old == null) break;
                refactorJobs.Remove(old);
            }
        }
        Log.Info($"tc_refactor job {job.Id} started ({changes.Count} changes)");
        job.Work = System.Threading.Tasks.Task.Factory.StartNew(() => RefactorApply(args, job), CancellationToken.None,
            System.Threading.Tasks.TaskCreationOptions.LongRunning, System.Threading.Tasks.TaskScheduler.Default);
        job.Work.ContinueWith(x =>
        {
            Log.Info($"tc_refactor job {job.Id} finished after {job.Clock.ElapsedMilliseconds} ms: " +
                     (x.IsFaulted ? "error " + ErrorText(x.Exception!) : x.Result.Split('\n')[0]));
            // the apply call already returned "still waiting" (and was audited with that) - audit the real outcome now
            if (job.ReturnedPending)
                policy.Audit("tc_refactor", args, !x.IsFaulted, x.IsFaulted ? "Error: " + ErrorText(x.Exception!) : x.Result,
                             job.Clock.ElapsedMilliseconds, $"job {job.Id} finished");
            if (!x.IsFaulted) tc.InvalidatePlcIndex();
        });
        return WaitJob(job, JobWait(a), fromApply: true);
    }

    string RefactorJobResult(JsonObject a)
    {
        var id = Str(a, "job");
        RefactorJob job;
        lock (refactorJobs)
            job = string.IsNullOrEmpty(id) ? refactorJobs.LastOrDefault()
                                           : refactorJobs.FirstOrDefault(j => j.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
        if (job == null)
            return string.IsNullOrEmpty(id)
                ? "No refactoring job in this TcAgent process."
                : $"Job {id} is unknown - only the last {KeepJobs} jobs of this TcAgent process are kept (a restarted TcAgent has none). " +
                  "Do not apply the changes again blindly: check the code (tc_refactor action=analyze / tc_plc_read) or ask the user.";
        return WaitJob(job, JobWait(a), fromApply: false);
    }

    /// <summary>Waits up to 'seconds' for the job; meanwhile watches for modal IDE dialogs (TcAgent's own confirmation
    /// dialogs are not counted - see IdeDialogs.Find). Returns the final report or the progress.</summary>
    string WaitJob(RefactorJob job, int seconds, bool fromApply)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int seen = 0;
        List<IdeDialogs.Dialog> dialogs = new();
        while (System.Threading.Tasks.Task.WaitAny(new System.Threading.Tasks.Task[] { job.Work }, 1000) < 0)
        {
            try { dialogs = IdeDialogs.Find(tc.AttachedPid); } catch { dialogs = new(); }
            seen = dialogs.Count > 0 ? seen + 1 : 0;
            if (seen < 3 && sw.Elapsed.TotalSeconds < seconds) continue;
            if (!job.MarkPendingIfRunning()) break;          // finished just now - return the report below
            return Pending(job, seen >= 3 ? dialogs : null, seconds);
        }
        var result = job.Work.GetAwaiter().GetResult();     // rethrows the job's own exception
        return fromApply ? result : $"Job {job.Id} finished:\n{result}";
    }

    static string Pending(RefactorJob job, List<IdeDialogs.Dialog> dialogs, int seconds)
    {
        var (done, total, report, waitingFor) = job.Snapshot();
        var o = new StringBuilder();
        if (dialogs != null)
            o.AppendLine("The IDE is showing a dialog and waiting for input - the refactoring job continues only after it is closed:\n  " +
                         string.Join("\n  ", dialogs) + "\nAsk the user to read and close it (or tc_dialogs action=close, which is Cancel/X).\n");
        o.AppendLine($"Still waiting for the user's decisions - {done} of {total} done" +
                     (waitingFor != null ? $"; the dialog now asks about {waitingFor}" : "") + ".");
        o.AppendLine($"The job keeps running in TcAgent. Call tc_refactor action=result job={job.Id} to get the final report " +
                     $"(it waits up to {seconds} s again). Do NOT call apply again for these changes. Tell the user that the " +
                     "confirmation dialog is waiting for them (Visual Studio / XAE Shell or the client's question).");
        if (report.Trim().Length > 0) o.AppendLine("\nSo far:\n" + report.TrimEnd());
        return o.ToString();
    }
}
