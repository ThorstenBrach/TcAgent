// The settings of TcAgent in one place: every TCAGENT_ variable a person may set, with its page in the settings window, type,
// values, default and a text in English and German. The settings window (vsbridge/SettingsWindow.cs) is built from it, the
// command line (TcAgent.exe settings) reads and writes with it, and a test checks that every variable the code reads is here -
// so a new setting cannot be forgotten in the window or the documentation.
//
// Where a value comes from: default · settings.json "env" (%LOCALAPPDATA%\TcAgent, written by the window) · the environment of
// Windows or an MCP configuration (wins over settings.json; the window shows it and cannot change it).
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace TcAgent;

public sealed record Setting(string Key, string Page, string Type, string Default, string[] Values,
                             string Label, string LabelDe, string Description, string DescriptionDe, bool Restart = false)
{
    /// <summary>Plain words for the values (en, de), same order as Values.</summary>
    public (string En, string De)[] ValueLabels { get; init; }
}

public static class SettingsSchema
{
    /// <summary>Pages of the window, in order: id, English, German.</summary>
    public static readonly (string Id, string En, string De)[] Pages =
    {
        ("general", "General and mode", "Allgemein und Modus"),
        ("targets", "Target systems", "Zielsysteme"),
        ("gates", "Gates and confirmations", "Gates und Bestätigungen"),
        ("knowledge", "Knowledge and levels", "Wissen und Ebenen"),
        ("advanced", "Advanced", "Erweitert"),
    };

    // on/off switches: "0" switches off, nothing (or anything else) is on
    static readonly string[] OnOff = { "1", "0" };
    static readonly (string, string)[] OnOffLabels = { ("on", "an"), ("off", "aus") };

    public static readonly Setting[] All =
    {
        // ---------------------------------------------------------------- general
        new("TCAGENT_MODE", "general", "choice", "agent", new[] { "agent", "propose", "ask" },
            "What the agent may do", "Was der Agent darf",
            "agent: change the project and, on allowed targets, the runtime · propose: code changes only as a diff, nothing is changed · ask: read only",
            "agent: Projekt ändern und auf freigegebenen Zielen die Runtime · propose: Code-Änderungen nur als Diff, nichts wird geändert · ask: nur lesen")
        { ValueLabels = new[] { ("change (agent)", "ändern (Agent)"), ("propose only", "nur vorschlagen"), ("read only", "nur lesen") } },
        new("TCAGENT_READONLY", "general", "choice", "", new[] { "", "1" },
            "Read only (older spelling)", "Nur lesen (alte Schreibweise)",
            "1 = the same as mode 'read only'. Kept for older configurations - use the mode above.",
            "1 = wie Modus „nur lesen“. Für ältere Konfigurationen - besser den Modus oben verwenden.")
        { ValueLabels = new[] { ("not set", "nicht gesetzt"), ("read only", "nur lesen") } },
        new("TCAGENT_AUDIT", "general", "path", @"%LOCALAPPDATA%\TcAgent\audit.jsonl", null,
            "Audit log", "Audit-Log",
            "File of the audit log: one line per changing call.", "Datei des Audit-Logs: eine Zeile pro ändernden Aufruf.", Restart: true),
        new("TCAGENT_JOB_WAIT", "general", "number", "45", null,
            "Wait for decisions (s)", "Auf Entscheidungen warten (s)",
            "Seconds tc_refactor apply/result waits for your decisions before it returns the progress.",
            "Sekunden, die tc_refactor apply/result auf deine Entscheidungen wartet, bevor es den Fortschritt meldet."),

        // ---------------------------------------------------------------- targets
        new("TCAGENT_ALLOW_TARGETS", "targets", "text", "local", null,
            "Allowed by configuration", "Freigabe per Konfiguration",
            "AMS Net IDs the agent may always write to (comma-separated; local = this PC). For pipelines and MCP configurations - in the window, release targets in the list above instead.",
            "AMS Net IDs, auf die der Agent immer schreiben darf (mit Komma getrennt; local = dieser PC). Für Pipelines und MCP-Konfigurationen - im Fenster besser oben in der Liste freigeben."),

        // ---------------------------------------------------------------- gates
        new("TCAGENT_CONFIRM", "gates", "choice", "runtime", new[] { "runtime", "all", "off" },
            "Ask before", "Nachfragen vor",
            "runtime: before runtime actions (activate, PLC start/stop, ADS write) · all: also before project changes · off: never (pipelines, unattended agents only)",
            "runtime: vor Laufzeit-Aktionen (aktivieren, PLC starten/stoppen, ADS schreiben) · all: auch vor Projektänderungen · off: nie (nur Pipelines, unbeaufsichtigte Agenten)")
        { ValueLabels = new[] { ("runtime actions", "Laufzeit-Aktionen"), ("every change", "jeder Änderung"), ("never", "nie") } },
        new("TCAGENT_ACTIVATE_GATE", "gates", "onoff", "1", OnOff,
            "Activate only after a clean build", "Aktivieren nur nach fehlerfreiem Build",
            "Before activating, TcAgent builds; with errors it refuses to activate.",
            "Vor dem Aktivieren baut TcAgent; bei Fehlern wird nicht aktiviert.") { ValueLabels = OnOffLabels },
        new("TCAGENT_SPEC_GATE", "gates", "onoff", "1", OnOff,
            "Ask before new code without a specification", "Vor neuem Code ohne Spezifikation fragen",
            "Before the agent creates a new FB, program, function or method outside a workflow, you are asked once per session.",
            "Bevor der Agent außerhalb eines Workflows einen neuen FB, ein Programm, eine Funktion oder Methode anlegt, wirst du einmal pro Sitzung gefragt.") { ValueLabels = OnOffLabels },
        new("TCAGENT_RULES_GATE", "gates", "onoff", "1", OnOff,
            "Read the coding rules before writing", "Codierregeln vor dem Schreiben lesen",
            "The first write is refused once until the agent read the coding rules.",
            "Der erste Schreibversuch wird einmal abgelehnt, bis der Agent die Codierregeln gelesen hat.") { ValueLabels = OnOffLabels },
        new("TCAGENT_LESSONS_RECALL", "gates", "onoff", "1", OnOff,
            "Recall lessons on errors", "Bei Fehlern an Lessons erinnern",
            "On an error, TcAgent adds matching lessons to the answer.", "Bei einem Fehler hängt TcAgent passende Lessons an die Antwort.") { ValueLabels = OnOffLabels },

        // ---------------------------------------------------------------- knowledge
        new("TCAGENT_COMPANY", "knowledge", "path", "", null,
            "Company level", "Firmen-Ebene",
            "Folder or Git repository of the company level (coding rules, risk rules, platforms ...).",
            "Ordner oder Git-Repository der Firmen-Ebene (Codierregeln, Risikoregeln, Plattformen …)."),
        new("TCAGENT_TEAM", "knowledge", "paths", "", null,
            "Team levels", "Team-Ebenen",
            "Shared folder or Git repository per team, ';'-separated - between your user level and the platform.",
            "Gemeinsamer Ordner oder Git-Repository je Team, mit ';' getrennt - zwischen deiner Benutzer-Ebene und der Plattform."),
        new("TCAGENT_PLATFORMS", "knowledge", "paths", "", null,
            "Platform repositories", "Plattform-Repositories",
            "Further roots of platforms (an automation framework in a repository of its own), ';'-separated.",
            "Weitere Ablagen von Plattformen (ein Automatisierungs-Framework in einem eigenen Repository), mit ';' getrennt."),
        new("TCAGENT_RULES", "knowledge", "paths", "", null,
            "Additional coding rules", "Zusätzliche Codierregeln",
            "Files or folders with coding rules, ';'-separated (count as company level).",
            "Dateien oder Ordner mit Codierregeln, mit ';' getrennt (zählen als Firmen-Ebene)."),
        new("TCAGENT_RISK_RULES", "knowledge", "paths", "", null,
            "Additional risk rules", "Zusätzliche Risikoregeln",
            "Files or folders with risk rules, ';'-separated.", "Dateien oder Ordner mit Risikoregeln, mit ';' getrennt."),
        new("TCAGENT_KNOWLEDGE", "knowledge", "paths", "", null,
            "Additional knowledge sources", "Zusätzliche Wissensquellen",
            "URLs or folders the agent may look things up in, ';'-separated.", "URLs oder Ordner, in denen der Agent nachschlagen darf, mit ';' getrennt."),
        new("TCAGENT_LESSONS", "knowledge", "path", "", null,
            "Team lessons (older)", "Team-Lessons (alt)",
            "A team file or folder for lessons. Newer: the lessons folder of the team level.",
            "Eine Team-Datei oder ein Ordner für Lessons. Neuer: der Ordner lessons der Team-Ebene."),
        new("TCAGENT_DOC_TEMPLATES", "knowledge", "paths", "", null,
            "Manual templates", "Vorlagen für Anleitungen",
            "Example manuals for tc_userdoc, ';'-separated files or folders.", "Beispiel-Anleitungen für tc_userdoc, mit ';' getrennte Dateien oder Ordner."),
        new("TCAGENT_SEARCH", "knowledge", "text", "", null,
            "Web search service", "Websuche",
            "Search service for tc_docs: searxng:<url> or google:<API key>:<search engine ID>.",
            "Suchdienst für tc_docs: searxng:<url> oder google:<API-Schlüssel>:<Suchmaschinen-ID>."),
        new("TCAGENT_FEEDBACK", "knowledge", "path", "", null,
            "Feedback store", "Feedback-Ablage",
            "Where tc_feedback stores reports: a .jsonl file or a (shared) folder. Empty: the TcAgent folder.",
            "Wo tc_feedback Berichte ablegt: eine .jsonl-Datei oder ein (gemeinsamer) Ordner. Leer: der TcAgent-Ordner."),

        // ---------------------------------------------------------------- advanced
        new("TCAGENT_PLUGINS", "advanced", "onoff", "1", OnOff,
            "Load plugins", "Plugins laden",
            "Off: TcAgent loads no plugins (to find a problem).", "Aus: TcAgent lädt keine Plugins (zur Fehlersuche).") { ValueLabels = OnOffLabels },
        new("TCAGENT_SKILL_MIRROR", "advanced", "onoff", "1", OnOff,
            "Copy skills to the agent CLIs", "Skills in die Agenten-CLIs kopieren",
            "Keeps the skills that apply everywhere current in the skill folders of Claude Code, Copilot and others.",
            "Hält die überall geltenden Skills in den Skill-Ordnern von Claude Code, Copilot und anderen aktuell.") { ValueLabels = OnOffLabels },
        new("TCAGENT_SOLUTION", "advanced", "text", "", null,
            "Pin a solution", "Solution festlegen",
            "Part of the solution path that picks one IDE instance. Normally not needed.",
            "Teil des Solution-Pfads, der eine IDE-Instanz auswählt. Normalerweise nicht nötig.", Restart: true),
        new("TCAGENT_IDE_ASSEMBLIES", "advanced", "paths", "", null,
            "IDE assemblies", "IDE-Assemblies",
            "Folders searched first for EnvDTE and the TwinCAT interop, ';'-separated.",
            "Ordner, in denen zuerst nach EnvDTE und dem TwinCAT-Interop gesucht wird, mit ';' getrennt.", Restart: true),
        new("TCAGENT_FORMAT_DEBUG", "advanced", "path", "", null,
            "Formatter debug folder", "Formatter-Debug-Ordner",
            "A folder: the formatter writes the first and second run of every format there (to find a problem).",
            "Ein Ordner: der Formatter legt dort den ersten und zweiten Durchlauf jeder Formatierung ab (zur Fehlersuche)."),
    };

    /// <summary>Set by TcAgent itself for the processes it starts - not shown, not to be set by hand.</summary>
    public static readonly string[] Internal = { "TCAGENT_CLIENT", "TCAGENT_VS_PID", "TCAGENT_ADVISOR" };

    public static Setting Find(string key) => All.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>A value is fine for the setting? null = yes, otherwise the reason.</summary>
    public static string Check(Setting s, string value)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0) return null;                               // empty = back to the default
        switch (s.Type)
        {
            case "choice":
            case "onoff":
                if (s.Key == "TCAGENT_MODE" && value is "vorschlag" or "fragen" or "read") return null;
                return s.Values.Contains(value, StringComparer.OrdinalIgnoreCase) ? null : $"one of: {string.Join(", ", s.Values.Where(v => v.Length > 0))}";
            case "number":
                return int.TryParse(value, out var n) && n >= 0 ? null : "a whole number, 0 or more";
            default:
                return value.Contains('"') ? "no quotes" : null;
        }
    }

    /// <summary>The schema and the effective values as JSON for the settings window.</summary>
    public static JsonObject Show(string settingsFile, IEnumerable<string> mcpConfigs)
    {
        JsonObject env = null;
        try { if (File.Exists(settingsFile)) env = JsonNode.Parse(File.ReadAllText(settingsFile))?["env"] as JsonObject; } catch { }
        env ??= new JsonObject();
        // what an MCP configuration of TcAgent sets (the chat's own configurations)
        var fromMcp = new Dictionary<string, (string Value, string File)>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in mcpConfigs ?? Enumerable.Empty<string>())
            try
            {
                if (File.Exists(f) && JsonNode.Parse(File.ReadAllText(f))?["mcpServers"]?["tcagent"]?["env"] is JsonObject e)
                    foreach (var (k, v) in e) if (v != null && v.ToString().Length > 0) fromMcp[k] = (v.ToString(), Path.GetFileName(f));
            }
            catch { }
        var arr = new JsonArray();
        foreach (var s in All)
        {
            var o = new JsonObject
            {
                ["key"] = s.Key, ["page"] = s.Page, ["type"] = s.Type, ["default"] = s.Default,
                ["label"] = s.Label, ["labelDe"] = s.LabelDe, ["description"] = s.Description, ["descriptionDe"] = s.DescriptionDe,
                ["restart"] = s.Restart
            };
            if (s.Values != null) o["values"] = new JsonArray(s.Values.Select(v => (JsonNode)v).ToArray());
            if (s.ValueLabels != null) o["valueLabels"] = new JsonArray(s.ValueLabels.Select(v => (JsonNode)new JsonObject { ["en"] = v.En, ["de"] = v.De }).ToArray());
            var saved = env[s.Key]?.ToString();
            if (saved != null) o["saved"] = saved;
            // outside: Windows (user or machine variable) or an MCP configuration - wins over settings.json
            var win = Environment.GetEnvironmentVariable(s.Key, EnvironmentVariableTarget.User) ?? Environment.GetEnvironmentVariable(s.Key, EnvironmentVariableTarget.Machine);
            if (fromMcp.TryGetValue(s.Key, out var m)) { o["outside"] = m.Value; o["outsideFrom"] = "MCP configuration " + m.File; }
            else if (!string.IsNullOrEmpty(win)) { o["outside"] = win; o["outsideFrom"] = "Windows environment variable"; }
            o["effective"] = o["outside"]?.ToString() ?? saved ?? s.Default;
            o["from"] = o["outside"] != null ? "outside" : saved != null ? "saved" : "default";
            arr.Add(o);
        }
        return new JsonObject
        {
            ["pages"] = new JsonArray(Pages.Select(p => (JsonNode)new JsonObject { ["id"] = p.Id, ["en"] = p.En, ["de"] = p.De }).ToArray()),
            ["settings"] = arr,
            ["file"] = settingsFile
        };
    }
}
