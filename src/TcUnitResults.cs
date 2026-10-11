// TcUnit results from the PLC runtime messages (error list of the IDE).
// TcUnit writes a summary after all tests have run and - with LogExtendedResults (default in TcUnit 1.2+) - one line per
// test suite and test. Lines are expected in chronological order (TwinCatSession.Chronological): TcUnit leaves
// "test class name" empty unless the test sets it, so a test belongs to the suite whose header came last.
//
//   | ==========TESTS FINISHED RUNNING==========
//   | Test suites: 2
//   | Tests: 5
//   | Successful tests: 4
//   | Failed tests: 1
//   | Duration: 0.012
//   | Test suite ID=0 'PRG_TEST.fbMotor_Test'
//   | ID=0 number of tests=3, number of failed tests=1, duration=0.004
//   | Test name=WhenStartedExpectRunning, test class name=PRG_TEST.fbMotor_Test, test status=FAIL, number of asserts=1, duration=0.001
//   FAILED TEST 'PRG_TEST.fbMotor_Test@WhenStartedExpectRunning', EXP: TRUE, ACT: FALSE, MSG: motor not running
//
// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text.RegularExpressions;

namespace TcAgent;

public sealed class TcUnitResults
{
    public sealed class Case
    {
        public string Suite, Name, Status = "SUCCESS", Failure;
        public int Asserts;
        public double? Duration;
        public bool Failed => Failure != null || Status.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase);
        public bool Skipped => Status.StartsWith("SKIP", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class Suite
    {
        public string Name;
        public int? Tests, Failures;
        public double? Duration;
        public readonly List<Case> Cases = new();
    }

    public bool Finished;
    public int? SuiteCount, TestCount, Successful, Failed;
    public double? Duration;
    public readonly List<Suite> Suites = new();
    /// <summary>All TcUnit lines of the run, for the log / system-out.</summary>
    public readonly List<string> Lines = new();

    static readonly Regex KeyValue = new(@"(?:^|,\s*)(?<k>[A-Za-z][A-Za-z ]*?)\s*=\s*(?<v>[^,]*)", RegexOptions.Compiled);
    static readonly Regex SuiteHeader = new(@"^Test suite ID\s*=\s*(?<id>\d+)\s+'(?<name>[^']*)'", RegexOptions.Compiled);
    static readonly Regex SuiteStats = new(@"^ID\s*=\s*(?<id>\d+)\s*,?\s*(?<rest>number of tests.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex FailedTest = new(@"FAILED TEST '(?<suite>[^'@]*)@(?<test>[^']*)'\s*,?\s*(?<rest>.*)$", RegexOptions.Compiled);

    public static TcUnitResults Parse(IEnumerable<string> runtimeTexts)
    {
        var r = new TcUnitResults();
        var suitesById = new Dictionary<string, Suite>();
        var statsById = new Dictionary<string, (int? tests, int? failed, double? dur)>();
        var cases = new Dictionary<string, Case>(StringComparer.Ordinal);   // "suite@test"
        var failures = new List<(string suite, string test, string text)>();
        Suite current = null;

        foreach (var raw in runtimeTexts)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var t = raw.Trim();
            bool isTcUnit = t.StartsWith("|") || t.Contains("FAILED TEST", StringComparison.Ordinal);
            if (!isTcUnit) continue;
            r.Lines.Add(t);
            var body = t.TrimStart('|').Trim();

            if (body.Contains("TESTS FINISHED RUNNING", StringComparison.OrdinalIgnoreCase)) { r.Finished = true; continue; }

            var f = FailedTest.Match(t);
            if (f.Success) { failures.Add((f.Groups["suite"].Value.Trim(), f.Groups["test"].Value.Trim(), f.Groups["rest"].Value.Trim())); continue; }

            if (TrySummary(body, "Test suites", out var n)) { r.SuiteCount = n; continue; }
            if (TrySummary(body, "Tests", out n)) { r.TestCount = n; continue; }
            if (TrySummary(body, "Successful tests", out n)) { r.Successful = n; continue; }
            if (TrySummary(body, "Failed tests", out n)) { r.Failed = n; continue; }
            if (body.StartsWith("Duration:", StringComparison.OrdinalIgnoreCase)) { r.Duration = Num(body.Substring(9)); continue; }

            var sh = SuiteHeader.Match(body);
            if (sh.Success)
            {
                var name = sh.Groups["name"].Value.Trim();
                current = r.GetSuite(name);
                suitesById[sh.Groups["id"].Value] = current;
                continue;
            }

            var ss = SuiteStats.Match(body);
            if (ss.Success)
            {
                var skv = KeyValues(ss.Groups["rest"].Value);
                statsById[ss.Groups["id"].Value] = (Int(skv, "number of tests"), Int(skv, "number of failed tests"), Dbl(skv, "duration"));
                continue;
            }

            var kv = KeyValues(body);
            if (kv.TryGetValue("Test name", out var testName))
            {
                kv.TryGetValue("test class name", out var cls);
                var suiteName = string.IsNullOrWhiteSpace(cls) ? current?.Name ?? "" : cls.Trim();
                var c = r.GetCase(cases, suiteName, testName.Trim());
                if (kv.TryGetValue("test status", out var st) && !string.IsNullOrWhiteSpace(st)) c.Status = st.Trim();
                c.Asserts = Int(kv, "number of asserts") ?? c.Asserts;
                c.Duration = Dbl(kv, "duration") ?? c.Duration;
            }
        }

        foreach (var (id, s) in suitesById)
            if (statsById.TryGetValue(id, out var st)) { s.Tests = st.tests; s.Failures = st.failed; s.Duration = st.dur; }

        foreach (var (suite, test, text) in failures)
        {
            var c = r.GetCase(cases, suite, test);
            c.Failure = c.Failure == null ? text : c.Failure + "\n" + text;
            if (!c.Failed) c.Status = "FAIL";
        }
        return r;
    }

    Suite GetSuite(string name)
    {
        var s = Suites.FirstOrDefault(x => x.Name == name);
        if (s == null) { s = new Suite { Name = name }; Suites.Add(s); }
        return s;
    }

    Case GetCase(Dictionary<string, Case> cases, string suite, string test)
    {
        var key = suite + "@" + test;
        if (cases.TryGetValue(key, out var c)) return c;
        c = new Case { Suite = suite, Name = test };
        cases[key] = c;
        GetSuite(suite).Cases.Add(c);
        return c;
    }

    /// <summary>Failed tests: from the summary, otherwise counted from the parsed cases.</summary>
    public int FailedCount => Failed ?? Suites.Sum(s => s.Cases.Count(c => c.Failed));
    public int TotalCount => TestCount ?? Suites.Sum(s => s.Cases.Count);

    static bool TrySummary(string body, string key, out int n)
    {
        n = 0;
        if (!body.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(body.Substring(key.Length + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
    }

    static Dictionary<string, string> KeyValues(string body)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in KeyValue.Matches(body)) d[m.Groups["k"].Value.Trim()] = m.Groups["v"].Value;
        return d;
    }

    static int? Int(Dictionary<string, string> kv, string k) =>
        kv.TryGetValue(k, out var v) && int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    static double? Dbl(Dictionary<string, string> kv, string k) => kv.TryGetValue(k, out var v) ? Num(v) : null;

    static double? Num(string v) =>
        double.TryParse(v.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
