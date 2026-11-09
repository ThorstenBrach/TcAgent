// Rules of the ST formatter: defaults, profiles and formatting.json on the knowledge levels (docs/FORMATTER.md).
//
//   formatting.json on a level (built-in, company, user, team, platform, project - later wins):
//     { "profile": "twincat", "rules": { "indent.size": 4, "align.decl": false }, "locked": ["indent.style"] }
//   "locked": true locks every rule the file sets. A rule set to "unset" goes back to the default.
//   Nothing is a fixed rule: every key can be changed, most also know "preserve" (leave it as it is).
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace TcAgent;

public sealed class StFormatConfig
{
    /// <summary>Every rule with its default (profile twincat) and its allowed values - the reference for docs and the window.</summary>
    public static readonly (string Key, string Default, string Values, string Description)[] Rules =
    {
        ("indent.style", "spaces", "spaces | tabs", "indent with spaces or tabs"),
        ("indent.size", "2", "number", "spaces per level (with tabs: one tab per level)"),
        ("indent.tab-width", "4", "number", "width of a tab, for columns and line length"),
        ("indent.case-labels", "true", "true | false", "CASE labels one level under CASE, their statements one more"),
        ("indent.continuation", "preserve", "preserve | indent", "continuation lines: keep their offset to the first line, or one level"),
        ("indent.underscore", "off", "off | declarations | everywhere", "a name that starts with _ one column further left, so its letters line up with the other names"),
        ("indent.from-ide", "false", "true | false", "take indent style and size from the TwinCAT editor settings (in the IDE)"),
        ("case.keywords", "upper", "upper | lower | preserve", "IF, THEN, END_IF, VAR ..."),
        ("case.types", "upper", "upper | lower | preserve", "BOOL, INT, TIME, STRING ..."),
        ("case.literals", "upper", "upper | lower | preserve", "TRUE, FALSE and literal prefixes T#, INT#"),
        ("space.operators", "one", "one | preserve", "one space around :=, =>, S=, R=, REF=, + - * /, comparisons, AND/OR"),
        ("space.comma", "one", "one | preserve", "no space before a comma, one after"),
        ("space.decl-colon", "one", "one | preserve", "one space around the : of a declaration"),
        ("space.tabs", "replace", "replace | preserve", "a tab inside a line becomes spaces up to the next tab stop (indent.tab-width), the text keeps its column"),
        ("space.parens", "preserve", "none | one | preserve", "spaces inside ( ) and [ ]"),
        ("space.keyword-paren", "one", "one | preserve", "IF (, AND (, NOT ("),
        ("space.call-paren", "none", "none | preserve", "no space between a name and its ("),
        ("comment.decl-position", "preserve", "above | behind | preserve", "the comment of a declaration on its own line above it, or at the end of its line"),
        ("comment.case-labels", "off", "off | enum", "a comment above each CASE label that has none: the comment of the enum value, otherwise its name"),
        ("comment.eol-spaces", "preserve", "preserve | number", "spaces before a comment at the end of a code line (implementation)"),
        ("layout.blocks", "preserve", "expand | preserve", "one statement per line; the bodies of IF, CASE, FOR, WHILE and REPEAT on their own lines"),
        ("layout.short-if", "preserve", "preserve | number", "IF ... THEN with one statement and END_IF on one line when it stays within this many characters"),
        ("layout.long-if", "preserve", "expand | preserve", "an IF ... END_IF on one line that is longer than layout.short-if (or wrap.max-length): one statement per line"),
        ("layout.then", "preserve", "same-line | own-line | preserve", "THEN, DO and OF at the end of the condition or on their own line"),
        ("align.decl", "true", "true | false", "the : of declarations in a column"),
        ("align.decl-init", "true", "true | false", "the := of initial values in a column"),
        ("align.eol-comments", "true", "true | false", "comments at the end of declarations in a column"),
        ("align.assignments", "false", "true | false", "the := of consecutive assignments in a column"),
        ("align.named-args", "true", "true | false", "the := and => of named parameters in a column when a call has one per line"),
        ("align.max-pad", "40", "number", "no alignment of a group when a line would need more filling"),
        ("wrap.max-length", "120", "number | 0", "longest line; 0 = never wrap"),
        ("wrap.call-args", "when-long", "when-long | never", "a call too long: one parameter per line, ) on its own line"),
        ("wrap.conditions", "when-long", "when-long | never", "a condition or boolean assignment too long: one term per line at the weakest AND/OR"),
        ("wrap.operator", "first", "first | last", "where AND/OR stand when a condition is wrapped"),
        ("blank.max", "1", "number | preserve", "most blank lines in a row"),
        ("blank.body-edges", "remove", "remove | preserve", "blank lines at the start and end of a part"),
        ("blank.case-branches", "preserve", "between | after-label | both | preserve", "a blank line between the CASE branches, after the label line, or both"),
        ("blank.around-blocks", "preserve", "add | preserve", "a blank line before and after IF, CASE, FOR, WHILE and REPEAT blocks"),
        ("eol.trim-trailing", "true", "true | false", "remove spaces at the end of lines"),
        ("eol.style", "preserve", "preserve | crlf | lf", "line endings"),
        ("paren.precedence", "true", "true | false", "brackets around AND groups inside OR, so the order is visible (the meaning stays)"),
        ("cond.lines", "preserve", "per-term | when-long | preserve", "conditions after IF, ELSIF, WHILE, UNTIL: one sub-condition per line - always, only when too long, or as written (then the other cond rules do nothing)"),
        ("cond.paren-terms", "preserve", "always | when-several | preserve", "every sub-condition in its own brackets - always (also a single one), only when there are several, or as written"),
        ("cond.paren-groups", "preserve", "outer | or-groups | preserve", "brackets around groups: also one around the whole condition, only around AND groups inside OR, or as written"),
        ("cond.group-space", "spaced", "tight | spaced", "brackets of a group next to the brackets inside it: (( and )) or ( ( and ) )"),
        ("cond.not", "preserve", "inside | outside | preserve", "NOT of a sub-condition inside its brackets ( NOT a ) or in front of them NOT ( a )"),
        ("cond.not-align", "false", "true | false", "in a condition with NOT, the other sub-conditions one NOT further in, so all names start in one column"),
        ("cond.operator", "last", "last | first", "AND / OR at the end of the line or at the start of the next one"),
        ("cond.continuation", "paren", "paren | indent", "the next lines under the first bracket after IF, or the condition one level in below the IF"),
        ("cond.nest-indent", "indent", "indent | flat", "an inner group one bracket further right, so you see what the AND and OR act on - or all sub-conditions in one column"),
        ("cond.align-close", "false", "true | false", "the closing brackets of the sub-conditions in one column"),
        ("cond.align-compare", "false", "true | false", "the comparison operators (= <> < >) of the sub-conditions in one column, right-aligned"),
        ("cond.keyword-paren", "preserve", "none | one | preserve", "IF( or IF ( - for every bracket right after IF, ELSIF, WHILE, UNTIL (preserve: as space.keyword-paren)"),
        ("cond.comments", "preserve", "align | preserve", "comments at the end of the lines of a condition in one column"),
        ("cond.assignments", "false", "true | false", "the cond rules also for boolean assignments: bRelease := a AND b;"),
    };

    /// <summary>Short names in plain words for the settings window (the key stays in the details).</summary>
    public static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["indent.style"] = "Indent with", ["indent.size"] = "Spaces per level", ["indent.tab-width"] = "Width of a tab",
        ["indent.case-labels"] = "CASE labels one level in", ["indent.continuation"] = "Continuation lines", ["indent.underscore"] = "Names with _ one column left", ["indent.from-ide"] = "Take indentation from the IDE",
        ["case.keywords"] = "Keywords", ["case.types"] = "Data types", ["case.literals"] = "TRUE, FALSE, T#",
        ["space.operators"] = "Around operators", ["space.comma"] = "After commas", ["space.decl-colon"] = "Around the : of declarations",
        ["space.parens"] = "Inside brackets", ["space.tabs"] = "Tabs inside a line", ["space.keyword-paren"] = "Keyword and (", ["space.call-paren"] = "Name and (",
        ["comment.eol-spaces"] = "Before end-of-line comments", ["comment.case-labels"] = "Comment above CASE labels", ["comment.decl-position"] = "Comments of declarations",
        ["blank.case-branches"] = "Blank lines in CASE",
        ["layout.blocks"] = "One statement per line", ["layout.then"] = "THEN, DO, OF", ["layout.short-if"] = "Short IF on one line up to", ["layout.long-if"] = "Longer IF on one line",
        ["align.decl"] = "Declarations: the :", ["align.decl-init"] = "Declarations: the :=", ["align.eol-comments"] = "Declarations: comments",
        ["align.assignments"] = "Assignments: the :=", ["align.named-args"] = "Named parameters", ["align.max-pad"] = "Most filling for a column",
        ["wrap.max-length"] = "Longest line", ["wrap.call-args"] = "Long calls", ["wrap.conditions"] = "Long conditions", ["wrap.operator"] = "AND / OR stand",
        ["blank.max"] = "Blank lines in a row", ["blank.body-edges"] = "Blank lines at the edges", ["blank.around-blocks"] = "Blank lines around blocks",
        ["eol.trim-trailing"] = "Spaces at line ends", ["eol.style"] = "Line endings", ["paren.precedence"] = "Brackets for AND in OR",
        ["cond.lines"] = "One sub-condition per line", ["cond.paren-terms"] = "Brackets around sub-conditions", ["cond.paren-groups"] = "Brackets around groups",
        ["cond.group-space"] = "Group brackets", ["cond.not"] = "NOT and its brackets", ["cond.not-align"] = "Make room for NOT",
        ["cond.operator"] = "AND / OR in conditions", ["cond.continuation"] = "Next lines of a condition", ["cond.nest-indent"] = "Inner groups",
        ["cond.align-close"] = "Closing brackets in a column", ["cond.align-compare"] = "Comparisons in a column", ["cond.keyword-paren"] = "IF and (",
        ["cond.comments"] = "Comments in conditions", ["cond.assignments"] = "Also boolean assignments",
    };

    /// <summary>The names in German, for the settings window when the chat answers in German (English for every other language).</summary>
    public static readonly Dictionary<string, (string Label, string Description)> German = new(StringComparer.OrdinalIgnoreCase)
    {
        ["indent.style"] = ("Einrücken mit", "mit Leerzeichen oder Tabs einrücken"),
        ["indent.size"] = ("Leerzeichen pro Ebene", "Leerzeichen pro Einrückungsebene (mit Tabs: ein Tab pro Ebene)"),
        ["indent.tab-width"] = ("Breite eines Tabs", "Breite eines Tabs, für Spalten und Zeilenlänge"),
        ["indent.case-labels"] = ("CASE-Labels eine Ebene tiefer", "CASE-Labels eine Ebene unter CASE, ihre Anweisungen eine weitere"),
        ["indent.underscore"] = ("Namen mit _ eine Spalte links", "ein Name mit _ am Anfang eine Spalte weiter links, damit seine Buchstaben unter denen der anderen Namen stehen"),
        ["indent.continuation"] = ("Fortsetzungszeilen", "Fortsetzungszeilen behalten ihren Versatz zur ersten Zeile, oder eine Ebene"),
        ["indent.from-ide"] = ("Einrückung aus der IDE", "Einrückungsart und -breite aus den Einstellungen des TwinCAT-Editors übernehmen (in der IDE)"),
        ["case.keywords"] = ("Schlüsselwörter", "IF, THEN, END_IF, VAR …"),
        ["case.types"] = ("Datentypen", "BOOL, INT, TIME, STRING …"),
        ["case.literals"] = ("TRUE, FALSE, T#", "TRUE, FALSE und die Präfixe von Literalen wie T#, INT#"),
        ["space.operators"] = ("Um Operatoren", "ein Leerzeichen um :=, =>, S=, R=, REF=, + - * /, Vergleiche, AND/OR"),
        ["space.comma"] = ("Nach Kommas", "kein Leerzeichen vor einem Komma, eins danach"),
        ["space.decl-colon"] = ("Um den : von Deklarationen", "ein Leerzeichen um den : einer Deklaration"),
        ["space.tabs"] = ("Tabs innerhalb der Zeile", "ein Tab in der Zeile wird bis zur nächsten Tab-Position mit Leerzeichen aufgefüllt, der Text bleibt in seiner Spalte"),
        ["space.parens"] = ("In Klammern", "Leerzeichen innerhalb von ( ) und [ ]"),
        ["space.keyword-paren"] = ("Schlüsselwort und (", "IF (, AND (, NOT ("),
        ["space.call-paren"] = ("Name und (", "kein Leerzeichen zwischen einem Namen und seiner ("),
        ["comment.decl-position"] = ("Kommentare von Deklarationen", "der Kommentar einer Deklaration in einer eigenen Zeile darüber oder am Ende ihrer Zeile"),
        ["comment.case-labels"] = ("Kommentar über CASE-Labels", "ein Kommentar über jedem CASE-Label ohne Kommentar: der Kommentar des Enum-Werts, sonst sein Name"),
        ["comment.eol-spaces"] = ("Vor Kommentaren am Zeilenende", "Leerzeichen vor einem Kommentar am Ende einer Codezeile (Implementierung)"),
        ["layout.blocks"] = ("Eine Anweisung pro Zeile", "eine Anweisung pro Zeile; die Rümpfe von IF, CASE, FOR, WHILE und REPEAT auf eigenen Zeilen"),
        ["layout.short-if"] = ("Kurzes IF in einer Zeile bis", "IF … THEN mit einer Anweisung und END_IF in einer Zeile, wenn es höchstens so viele Zeichen lang ist"),
        ["layout.long-if"] = ("Längeres IF in einer Zeile", "ein einzeiliges IF … END_IF, das länger als die Grenze ist, bekommt eine Anweisung pro Zeile"),
        ["layout.then"] = ("THEN, DO, OF", "THEN, DO und OF am Ende der Bedingung oder in einer eigenen Zeile"),
        ["align.decl"] = ("Deklarationen: der :", "der : von Deklarationen in einer Spalte"),
        ["align.decl-init"] = ("Deklarationen: das :=", "das := der Anfangswerte in einer Spalte"),
        ["align.eol-comments"] = ("Deklarationen: Kommentare", "Kommentare am Ende von Deklarationen in einer Spalte"),
        ["align.assignments"] = ("Zuweisungen: das :=", "das := aufeinander folgender Zuweisungen in einer Spalte"),
        ["align.named-args"] = ("Benannte Parameter", "das := und => benannter Parameter in einer Spalte, wenn ein Aufruf einen pro Zeile hat"),
        ["align.max-pad"] = ("Höchste Auffüllung einer Spalte", "keine Ausrichtung einer Gruppe, wenn eine Zeile mehr Auffüllung bräuchte"),
        ["wrap.max-length"] = ("Längste Zeile", "längste Zeile; 0 = nie umbrechen"),
        ["wrap.call-args"] = ("Lange Aufrufe", "ein zu langer Aufruf: ein Parameter pro Zeile, ) in eigener Zeile"),
        ["wrap.conditions"] = ("Lange Bedingungen", "eine zu lange Bedingung oder boolesche Zuweisung: ein Term pro Zeile am schwächsten AND/OR"),
        ["wrap.operator"] = ("AND / OR stehen", "wo AND/OR stehen, wenn eine Bedingung umbrochen wird"),
        ["blank.max"] = ("Leerzeilen hintereinander", "höchstens so viele Leerzeilen hintereinander"),
        ["blank.body-edges"] = ("Leerzeilen an den Rändern", "Leerzeilen am Anfang und Ende eines Teils"),
        ["blank.case-branches"] = ("Leerzeilen in CASE", "eine Leerzeile zwischen den CASE-Zweigen, nach der Label-Zeile, oder beides"),
        ["blank.around-blocks"] = ("Leerzeilen um Blöcke", "eine Leerzeile vor und nach IF-, CASE-, FOR-, WHILE- und REPEAT-Blöcken"),
        ["eol.trim-trailing"] = ("Leerzeichen am Zeilenende", "Leerzeichen am Ende von Zeilen entfernen"),
        ["eol.style"] = ("Zeilenenden", "Zeilenenden"),
        ["paren.precedence"] = ("Klammern für AND in OR", "Klammern um AND-Gruppen in einem OR, damit die Reihenfolge sichtbar ist (die Bedeutung bleibt)"),
        ["cond.lines"] = ("Eine Teilbedingung pro Zeile", "Bedingungen nach IF, ELSIF, WHILE, UNTIL: eine Teilbedingung pro Zeile - immer, nur wenn zu lang, oder wie geschrieben (dann wirken die anderen Bedingungsregeln nicht)"),
        ["cond.paren-terms"] = ("Klammern um Teilbedingungen", "jede Teilbedingung in eigene Klammern - immer (auch eine einzelne), nur wenn es mehrere gibt, oder wie geschrieben"),
        ["cond.paren-groups"] = ("Klammern um Gruppen", "Klammern um Gruppen: auch eine um die ganze Bedingung, nur um UND-Gruppen in einem OR, oder wie geschrieben"),
        ["cond.group-space"] = ("Gruppenklammern", "die Klammern einer Gruppe direkt an den Klammern darin: (( und )) oder ( ( und ) )"),
        ["cond.not"] = ("NOT und seine Klammern", "NOT einer Teilbedingung in ihren Klammern ( NOT a ) oder davor NOT ( a )"),
        ["cond.not-align"] = ("Platz für NOT", "in einer Bedingung mit NOT die anderen Teilbedingungen um ein NOT eingerückt, damit alle Namen in einer Spalte beginnen"),
        ["cond.operator"] = ("AND / OR in Bedingungen", "AND / OR am Ende der Zeile oder am Anfang der nächsten"),
        ["cond.continuation"] = ("Folgezeilen einer Bedingung", "die nächsten Zeilen unter der ersten Klammer nach dem IF, oder die Bedingung eine Ebene eingerückt unter dem IF"),
        ["cond.nest-indent"] = ("Innere Gruppen", "eine innere Gruppe eine Klammer weiter rechts, damit man sieht, worauf AND und OR wirken - oder alle Teilbedingungen in einer Spalte"),
        ["cond.align-close"] = ("Schließende Klammern in einer Spalte", "die schließenden Klammern der Teilbedingungen in einer Spalte"),
        ["cond.align-compare"] = ("Vergleiche in einer Spalte", "die Vergleichsoperatoren (= <> < >) der Teilbedingungen in einer Spalte, rechtsbündig"),
        ["cond.keyword-paren"] = ("IF und (", "IF( oder IF ( - für jede Klammer direkt nach IF, ELSIF, WHILE, UNTIL (so lassen: wie „Schlüsselwort und (“)"),
        ["cond.comments"] = ("Kommentare in Bedingungen", "Kommentare am Ende der Zeilen einer Bedingung in einer Spalte"),
        ["cond.assignments"] = ("Auch boolesche Zuweisungen", "die Bedingungsregeln auch für boolesche Zuweisungen: bRelease := a AND b;"),
    };

    /// <summary>The values in plain words: value → (English, German). A number stays a number.</summary>
    public static readonly Dictionary<string, (string En, string De)> ValueLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["preserve"] = ("keep as written", "so lassen, wie geschrieben"), ["true"] = ("yes", "ja"), ["false"] = ("no", "nein"),
        ["one"] = ("one space", "ein Leerzeichen"), ["none"] = ("no space", "kein Leerzeichen"),
        ["upper"] = ("UPPER CASE", "GROSS"), ["lower"] = ("lower case", "klein"),
        ["spaces"] = ("spaces", "Leerzeichen"), ["tabs"] = ("tabs", "Tabs"),
        ["expand"] = ("split into lines", "auf Zeilen aufteilen"), ["when-long"] = ("when too long", "wenn zu lang"), ["never"] = ("never", "nie"),
        ["first"] = ("at the start of the line", "am Zeilenanfang"), ["last"] = ("at the end of the line", "am Zeilenende"),
        ["add"] = ("add", "einfügen"), ["remove"] = ("remove", "entfernen"), ["replace"] = ("replace with spaces", "durch Leerzeichen ersetzen"),
        ["off"] = ("off", "aus"), ["enum"] = ("from the enum, else the name", "aus der Enum, sonst der Name"),
        ["between"] = ("between the branches", "zwischen den Zweigen"), ["after-label"] = ("after the label", "nach dem Label"), ["both"] = ("both", "beides"),
        ["same-line"] = ("at the end of the condition", "am Ende der Bedingung"), ["own-line"] = ("on its own line", "in eigener Zeile"),
        ["declarations"] = ("in declarations", "in Deklarationen"),
        ["above"] = ("above the declaration", "über der Deklaration"), ["behind"] = ("at the end of the line", "am Ende der Zeile"), ["everywhere"] = ("everywhere", "überall"),
        ["indent"] = ("one level in", "eine Ebene eingerückt"), ["crlf"] = ("CRLF (Windows)", "CRLF (Windows)"), ["lf"] = ("LF (Unix)", "LF (Unix)"),
        ["per-term"] = ("always, one per line", "immer, eine pro Zeile"), ["always"] = ("always", "immer"), ["when-several"] = ("when there are several", "wenn es mehrere gibt"),
        ["outer"] = ("also around the whole condition", "auch um die ganze Bedingung"), ["or-groups"] = ("only AND groups inside OR", "nur UND-Gruppen in einem OR"),
        ["tight"] = ("tight: ((", "eng: (("), ["spaced"] = ("with a space: ( (", "mit Leerzeichen: ( ("),
        ["inside"] = ("inside: ( NOT a )", "innen: ( NOT a )"), ["outside"] = ("in front: NOT ( a )", "davor: NOT ( a )"),
        ["paren"] = ("under the first bracket", "unter der ersten Klammer"), ["flat"] = ("all in one column", "alle in einer Spalte"),
        ["align"] = ("in a column", "in einer Spalte"),
    };

    public static readonly Dictionary<string, Dictionary<string, string>> Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["twincat"] = new(),
        ["compact"] = new() { ["align.decl"] = "false", ["align.decl-init"] = "false", ["align.eol-comments"] = "false", ["align.named-args"] = "false" },
        ["structured"] = new() { ["layout.blocks"] = "expand", ["layout.then"] = "own-line", ["blank.around-blocks"] = "add", ["align.assignments"] = "true" },
        // every sub-condition in brackets, one per line, columns - the style of many machine projects (interview 09.11.)
        ["conditions-aligned"] = new()
        {
            ["layout.then"] = "own-line", ["cond.lines"] = "per-term", ["cond.paren-terms"] = "always", ["cond.paren-groups"] = "outer",
            ["cond.group-space"] = "tight", ["cond.not"] = "inside", ["cond.not-align"] = "true", ["cond.operator"] = "last",
            ["cond.continuation"] = "paren", ["cond.nest-indent"] = "indent", ["cond.align-close"] = "true", ["cond.align-compare"] = "true",
            ["cond.keyword-paren"] = "one", ["cond.comments"] = "align",
        },
        ["preserve"] = Rules.Where(r => r.Values.Contains("preserve")).ToDictionary(r => r.Key, _ => "preserve")
            .Concat(new Dictionary<string, string> { ["align.decl"] = "false", ["align.decl-init"] = "false", ["align.eol-comments"] = "false", ["align.named-args"] = "false",
                ["wrap.max-length"] = "0", ["paren.precedence"] = "false", ["eol.trim-trailing"] = "false" }).ToDictionary(k => k.Key, k => k.Value),
    };

    public readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Where each value comes from: "default", "profile compact", "user", "project" ...</summary>
    public readonly Dictionary<string, string> Source = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Notes = new();
    /// <summary>"E_Type.Value" → the comment of the enumeration value (StEnums.Read of the solution folder) for comment.case-labels.</summary>
    public Dictionary<string, string> EnumComments = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Rules a level locked: key → the level.</summary>
    public readonly Dictionary<string, string> Locked = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>A formatting.json was found on some level (the agent formats its own code only then).</summary>
    public bool FromFiles;

    public StFormatConfig()
    {
        foreach (var r in Rules) { Values[r.Key] = r.Default; Source[r.Key] = "default"; }
    }

    public string this[string key] => Values.TryGetValue(key, out var v) ? v : "";
    public bool Flag(string key) => this[key].Equals("true", StringComparison.OrdinalIgnoreCase);
    public int Int(string key, int fallback) => int.TryParse(this[key], out var v) ? v : fallback;
    public bool Preserve(string key) => this[key].Equals("preserve", StringComparison.OrdinalIgnoreCase);

    public string IndentUnit => this["indent.style"].Equals("tabs", StringComparison.OrdinalIgnoreCase) ? "\t" : new string(' ', Math.Max(0, Int("indent.size", 2)));
    public int TabWidth => Math.Max(1, Int("indent.tab-width", 4));

    public void Set(string key, string value, string source)
    {
        if (!Rules.Any(r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) { Notes.Add($"unknown rule '{key}' ({source}) - ignored"); return; }
        if (value.Equals("unset", StringComparison.OrdinalIgnoreCase))
        { Values[key] = Rules.First(r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Default; Source[key] = "default (unset by " + source + ")"; return; }
        Values[key] = value; Source[key] = source;
    }

    public void ApplyProfile(string profile, string source)
    {
        if (!Profiles.TryGetValue(profile ?? "", out var p)) { Notes.Add($"unknown profile '{profile}' ({source})"); return; }
        foreach (var kv in p) Set(kv.Key, kv.Value, $"profile {profile} ({source})");
    }

    /// <summary>Applies one formatting.json. locked: rules a level before locked; they stay.</summary>
    public void Apply(JsonObject j, string source, Dictionary<string, string> locked)
    {
        var lockAll = j["locked"] is JsonValue lv && lv.TryGetValue<bool>(out var b) && b;
        var lockKeys = (j["locked"] as JsonArray)?.Select(x => x?.ToString()).Where(x => x != null).ToList() ?? new List<string>();
        void SetUnlessLocked(string key, string value, string src)
        {
            if (locked.TryGetValue(key, out var by)) { Notes.Add($"{key} from {source} ignored: locked by {by}"); return; }
            Set(key, value, src);
        }
        if (j["profile"]?.ToString() is { Length: > 0 } prof)
        {
            if (!Profiles.TryGetValue(prof, out var p)) Notes.Add($"unknown profile '{prof}' ({source})");
            else foreach (var kv in p) SetUnlessLocked(kv.Key, kv.Value, $"profile {prof} ({source})");
        }
        if (j["rules"] is JsonObject rules)
            foreach (var kv in rules)
            {
                SetUnlessLocked(kv.Key, kv.Value?.ToString() ?? "unset", source);
                if (lockAll && !locked.ContainsKey(kv.Key)) locked[kv.Key] = source;
            }
        foreach (var k in lockKeys) if (!locked.ContainsKey(k)) locked[k] = source;
    }

    /// <summary>The effective rules for a solution: the formatting.json of every knowledge level, later wins, locks hold.</summary>
    public static StFormatConfig Load(string solutionDir)
    {
        var c = new StFormatConfig();
        var locked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> platforms = Enumerable.Empty<string>();
        try { if (solutionDir != null) platforms = KnowledgeLayers.Platforms(solutionDir)?.Active ?? platforms; } catch { }
        foreach (var layer in KnowledgeLayers.Layers(solutionDir, platforms).OrderBy(l => l.Rank))
        {
            var f = Path.Combine(layer.Root, "formatting.json");
            if (!File.Exists(f)) continue;
            try
            {
                var j = JsonNode.Parse(File.ReadAllText(f)) as JsonObject ?? throw new Exception("not a JSON object");
                c.Apply(j, layer.Label, locked);
                c.FromFiles = true;
            }
            catch (Exception ex) { c.Notes.Add($"{f}: {ex.Message} - ignored"); }
        }
        foreach (var kv in locked) c.Locked[kv.Key] = kv.Value;
        return c;
    }

    public static StFormatConfig FromFile(string file)
    {
        var c = new StFormatConfig();
        var j = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new Exception(file + ": not a JSON object");
        c.Apply(j, Path.GetFileName(file), new Dictionary<string, string>());
        c.FromFiles = true;
        return c;
    }

    public JsonObject ToJson() => new()
    {
        ["rules"] = new JsonObject(Rules.Select(r => KeyValuePair.Create(r.Key, (JsonNode)new JsonObject
        {
            ["value"] = this[r.Key], ["from"] = Source[r.Key], ["default"] = r.Default, ["values"] = r.Values, ["description"] = r.Description,
            ["label"] = Labels.TryGetValue(r.Key, out var lab) ? lab : r.Key,
            ["labelDe"] = German.TryGetValue(r.Key, out var de) ? de.Label : null,
            ["descriptionDe"] = German.TryGetValue(r.Key, out var de2) ? de2.Description : null,
            ["locked"] = Locked.TryGetValue(r.Key, out var by) ? by : null
        }))),
        ["valueLabels"] = new JsonObject(ValueLabels.Select(v => KeyValuePair.Create(v.Key, (JsonNode)new JsonObject { ["en"] = v.Value.En, ["de"] = v.Value.De }))),
        ["profiles"] = new JsonObject(Profiles.Select(p => KeyValuePair.Create(p.Key,
            (JsonNode)new JsonObject(Rules.Select(r => KeyValuePair.Create(r.Key, (JsonNode)(p.Value.TryGetValue(r.Key, out var v) ? v : r.Default))))))),
        ["fromFiles"] = FromFiles,
        ["notes"] = new JsonArray(Notes.Select(n => (JsonNode)n).ToArray())
    };
}
