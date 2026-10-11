// JUnit XML for CI systems (GitHub Actions, Azure DevOps, Jenkins, GitLab) and inline annotations for compiler messages.
//
// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Xml.Linq;

namespace TcAgent;

public static class JUnit
{
    static string F(double d) => d.ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>Compiler result as a test suite: one failing test case per error (and per warning with warningsAsErrors).</summary>
    public static XElement CompileSuite(string name, IReadOnlyList<TwinCatSession.ErrorEntry> compile, bool warningsAsErrors, double seconds)
    {
        var problems = compile.Where(e => e.Level == "ERROR" || (warningsAsErrors && e.Level == "WARN")).ToList();
        var suite = new XElement("testsuite",
            new XAttribute("name", name),
            new XAttribute("tests", Math.Max(1, problems.Count)),
            new XAttribute("failures", problems.Count),
            new XAttribute("errors", 0),
            new XAttribute("skipped", 0),
            new XAttribute("time", F(seconds)),
            new XAttribute("timestamp", DateTime.Now.ToString("s", CultureInfo.InvariantCulture)),
            new XAttribute("hostname", Environment.MachineName));
        if (problems.Count == 0)
            suite.Add(new XElement("testcase", new XAttribute("name", "compile"), new XAttribute("classname", name), new XAttribute("time", F(seconds))));
        foreach (var e in problems)
            suite.Add(new XElement("testcase",
                new XAttribute("name", $"{Path.GetFileName(e.File)}:{e.Line}"),
                new XAttribute("classname", string.IsNullOrEmpty(e.Project) ? name : e.Project),
                new XAttribute("file", e.File ?? ""),
                new XAttribute("line", e.Line),
                new XElement("failure", new XAttribute("message", e.Text), new XAttribute("type", e.Level == "ERROR" ? "CompileError" : "CompileWarning"),
                    $"{e.File}({e.Line},{e.Column}): {e.Level}: {e.Text}")));
        var warnings = compile.Where(e => e.Level == "WARN" && !warningsAsErrors).Select(e => e.ToString()).ToList();
        if (warnings.Count > 0) suite.Add(new XElement("system-out", "Warnings:\n" + string.Join("\n", warnings)));
        return suite;
    }

    /// <summary>TcUnit result as test suites.</summary>
    public static IEnumerable<XElement> TestSuites(TcUnitResults r, string fallbackName)
    {
        foreach (var s in r.Suites)
        {
            int failures = s.Cases.Count(c => c.Failed);
            int skipped = s.Cases.Count(c => c.Skipped && !c.Failed);
            var suite = new XElement("testsuite",
                new XAttribute("name", s.Name),
                new XAttribute("tests", Math.Max(s.Tests ?? 0, s.Cases.Count)),
                new XAttribute("failures", Math.Max(s.Failures ?? 0, failures)),
                new XAttribute("errors", 0),
                new XAttribute("skipped", skipped),
                new XAttribute("time", F(s.Duration ?? s.Cases.Sum(c => c.Duration ?? 0))),
                new XAttribute("timestamp", DateTime.Now.ToString("s", CultureInfo.InvariantCulture)),
                new XAttribute("hostname", Environment.MachineName));
            foreach (var c in s.Cases)
            {
                var tc = new XElement("testcase",
                    new XAttribute("name", c.Name),
                    new XAttribute("classname", s.Name),
                    new XAttribute("time", F(c.Duration ?? 0)));
                if (c.Failed)
                    tc.Add(new XElement("failure", new XAttribute("message", FirstLine(c.Failure) ?? "test failed"), new XAttribute("type", "AssertionFailed"),
                        c.Failure ?? $"status={c.Status}, asserts={c.Asserts}"));
                else if (c.Skipped) tc.Add(new XElement("skipped"));
                suite.Add(tc);
            }
            if (s.Cases.Count == 0 && (s.Tests ?? 0) > 0)
                suite.Add(new XElement("system-out", "Details per test are missing - set LogExtendedResults := TRUE in the TcUnit parameters."));
            yield return suite;
        }
        if (r.Suites.Count == 0)
        {
            // summary only (very old TcUnit or no extended results)
            var suite = new XElement("testsuite",
                new XAttribute("name", fallbackName),
                new XAttribute("tests", r.TotalCount),
                new XAttribute("failures", r.FailedCount),
                new XAttribute("errors", 0),
                new XAttribute("skipped", 0),
                new XAttribute("time", F(r.Duration ?? 0)),
                new XAttribute("timestamp", DateTime.Now.ToString("s", CultureInfo.InvariantCulture)),
                new XAttribute("hostname", Environment.MachineName),
                new XElement("system-out", string.Join("\n", r.Lines)));
            yield return suite;
        }
    }

    /// <summary>A suite with a single errored test case (infrastructure problem, timeout ...).</summary>
    public static XElement ErrorSuite(string name, string testName, string message, string details = null) =>
        new("testsuite",
            new XAttribute("name", name), new XAttribute("tests", 1), new XAttribute("failures", 0), new XAttribute("errors", 1),
            new XAttribute("skipped", 0), new XAttribute("time", "0"),
            new XAttribute("timestamp", DateTime.Now.ToString("s", CultureInfo.InvariantCulture)),
            new XAttribute("hostname", Environment.MachineName),
            new XElement("testcase", new XAttribute("name", testName), new XAttribute("classname", name),
                new XElement("error", new XAttribute("message", message), new XAttribute("type", "TcAgent"), details ?? message)));

    public static void Write(string path, string name, IEnumerable<XElement> suites)
    {
        var list = suites.ToList();
        int Sum(string attr) => list.Sum(s => int.TryParse((string)s.Attribute(attr), out var i) ? i : 0);
        double time = list.Sum(s => double.TryParse((string)s.Attribute("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0);
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("testsuites",
                new XAttribute("name", name),
                new XAttribute("tests", Sum("tests")), new XAttribute("failures", Sum("failures")),
                new XAttribute("errors", Sum("errors")), new XAttribute("skipped", Sum("skipped")),
                new XAttribute("time", F(time)),
                list));
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        doc.Save(path);
    }

    static string FirstLine(string s) => s?.Split('\n')[0].Trim();

    // ------------------------------------------------------------------ CI annotations
    /// <summary>Annotation in the CI log (GitHub Actions / Azure DevOps), otherwise a plain line in compiler format.</summary>
    public static string Annotation(TwinCatSession.ErrorEntry e, bool asError)
    {
        var level = asError ? "error" : "warning";
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            var file = Relative(e.File, Environment.GetEnvironmentVariable("GITHUB_WORKSPACE"));
            return $"::{level} file={Esc(file, true)},line={Math.Max(1, e.Line)},col={Math.Max(1, e.Column)}::{Esc(e.Text, false)}";
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD")))
        {
            var file = Relative(e.File, Environment.GetEnvironmentVariable("BUILD_SOURCESDIRECTORY"));
            return $"##vso[task.logissue type={level};sourcepath={file};linenumber={Math.Max(1, e.Line)};columnnumber={Math.Max(1, e.Column)}]{e.Text}";
        }
        return $"{e.File}({e.Line},{e.Column}): {level}: {e.Text}";
    }

    static string Relative(string file, string root)
    {
        if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(root)) return file ?? "";
        try { var rel = Path.GetRelativePath(root, file); return rel.StartsWith("..") ? file : rel.Replace('\\', '/'); }
        catch { return file; }
    }

    static string Esc(string s, bool property)
    {
        s = (s ?? "").Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");
        return property ? s.Replace(":", "%3A").Replace(",", "%2C") : s;
    }
}
