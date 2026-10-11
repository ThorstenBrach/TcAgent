// Werkzeuge, die der KI ueber MCP angeboten werden.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using TCatSysManagerLib;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    sealed record Tool(string Name, string Description, JsonObject Schema, Func<JsonObject, string> Handler);

    readonly List<Tool> tools = new();
    readonly TwinCatSession tc;
    readonly Policy policy = new();

    // TwinCAT-Subtypen fuer CreateChild (PLC)
    static readonly Dictionary<string, int> PlcKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["folder"] = 601, ["program"] = 602, ["function"] = 603, ["function_block"] = 604,
        ["dut_enum"] = 605, ["dut_struct"] = 606, ["dut_union"] = 607, ["action"] = 608,
        ["method"] = 609, ["interface_method"] = 610, ["property"] = 611, ["interface_property"] = 612,
        ["property_get"] = 613, ["property_set"] = 614,
        ["gvl"] = 615, ["transition"] = 616, ["interface"] = 618, ["dut_alias"] = 623,
    };
    static readonly Dictionary<string, int> Languages = new(StringComparer.OrdinalIgnoreCase)
    { ["ST"] = 1, ["IL"] = 2, ["SFC"] = 3, ["FBD"] = 4, ["CFC"] = 5, ["LD"] = 6 };

    public ToolRegistry(TwinCatSession session)
    {
        tc = session;

        // ---------------------------------------------------------- Verbindung
        Add("tc_instances", "Listet laufende Visual-Studio-2022-/TcXaeShell-Instanzen mit geoeffneter Solution.",
            Schema(), a => tc.Sta.Run(() => string.Join("\n",
                tc.ListInstances().Select(i => $"pid={i.Pid}\t{i.Moniker}\t{i.Solution}")) is var s && s.Length > 0 ? s : "keine Instanz"));

        Add("tc_attach", "Verbindet mit einer laufenden IDE-Instanz (geschieht beim ersten Werkzeugaufruf auch automatisch, wenn die Auswahl eindeutig ist). Auswahl per pid oder Teil des Solution-Pfads. Ohne Angabe: die IDE, aus der TcAgent gestartet wurde (TcAgent-Chat), sonst TCAGENT_SOLUTION, sonst die einzige Instanz mit Solution. Sind mehrere offen, zeigt der Client eine Auswahlliste (falls unterstuetzt); sonst kommt eine Liste zurueck - dann den Benutzer fragen und mit pid aufrufen.",
            Schema(P("solution", "string", "Teil des Solution-Pfads zur Auswahl"), P("pid", "integer", "Prozess-ID der IDE")),
            a => tc.Sta.Run(() => tc.Attach(Str(a, "solution"), Int(a, "pid", 0))));

        Add("tc_open", "Startet Visual Studio 2022 (bzw. TcXaeShell) und oeffnet eine Solution.",
            Schema(Req(P("solutionPath", "string", "Vollstaendiger Pfad zur .sln")),
                   P("visible", "boolean", "IDE sichtbar starten (Standard true)"),
                   P("progId", "string", "z. B. VisualStudio.DTE.17.0 oder TcXaeShell.DTE.17.0")),
            a => tc.Sta.Run(() => tc.Open(Str(a, "solutionPath"), Bool(a, "visible", true), Str(a, "progId"))));

        Add("tc_status", "Zeigt die aktuelle Verbindung, Solution und Ziel-NetId.",
            Schema(), a => tc.Sta.Run(() =>
            {
                var s = tc.InstanceInfo + $"\nModus: {policy.Mode}";
                if (tc.SysMan != null) try { s += "\nZiel-NetId: " + (string)((dynamic)tc.SysMan).GetTargetNetId(); } catch { }
                return s;
            }));

        Add("tc_coding_rules", "Liefert die Codierrichtlinien dieses Projekts (Namenskonventionen, Kommentare, Aufbau ...). VOR dem ersten " +
                               "Schreiben oder Anlegen von Code aufrufen und alle Aenderungen daran ausrichten.",
            Schema(), a =>
            {
                var dir = SolutionDir();
                rulesSeen = true;
                return CodingRules.Text(dir) is { } t
                    ? "Codierrichtlinien (spaetere Abschnitte gehen bei Widerspruechen vor):\n\n" + t
                    : "Keine Codierrichtlinien hinterlegt - allgemein uebliche TwinCAT-Konventionen und den Stil des vorhandenen Codes verwenden.\n" + CodingRules.Where(dir);
            });

        Add("tc_coding_rules_add", "Nimmt eine Regel in die Codierrichtlinien auf - wenn der Nutzer sagt 'merke dir ...', 'ab jetzt immer ...', " +
                                   "'so will ich das haben'. Die Regel selbst knapp, eindeutig und allgemein formulieren (ein Satz, mit Beispiel " +
                                   "wenn noetig), dem Nutzer nennen und eintragen. Gilt ab sofort und fuer alle kuenftigen Sitzungen/Agenten.",
            Schema(Req(P("rule", "string", "die Regel als ein Satz, z. B. 'Interne Variablen beginnen mit _ (z. B. _nState).'")),
                   P("scope", "string", "project (Standard: Solution\\.tcagent\\coding-rules.md, in Git fuers Team) oder personal (nur dieser Nutzer)"),
                   P("section", "string", "Ueberschrift, unter der die Regel steht, z. B. Namen, Aufbau, Formatierung (Standard: Ergaenzungen)"),
                   P("dryRun", "boolean", "Nur zeigen, was eingetragen wuerde")),
            a =>
            {
                var (file, res) = CodingRules.Add(SolutionDir(), Str(a, "scope"), Str(a, "rule"), Str(a, "section"), DryRun(a));
                rulesSeen = true;
                return $"{res}\nDatei: {file}";
            });

        Add("tc_docs", "Dokumentation nachschlagen - nur lesend: Beckhoff InfoSys, weitere freigegebene URLs, Dokumente im Projektordner " +
                       ".tcagent\\docs bzw. %LOCALAPPDATA%\\TcAgent\\docs und eingetragene Ordner/Netzlaufwerke (PDF, Word, HTML, Markdown, Text). " +
                       "action=search (query) durchsucht lokale Dokumente und bereits gelesene Seiten; action=read (url oder Pfad) liefert den Text " +
                       "(lange Texte seitenweise per offset); action=sources zeigt die Quellen. Bei Fragen zu Klemmen, Parametern, Fehlercodes, " +
                       "TwinCAT-Funktionen erst hier nachschlagen statt zu raten, und die Quelle nennen.",
            Schema(P("action", "string", "search (Standard), read oder sources"),
                   P("query", "string", "search: Suchbegriffe, z. B. 'EL6001 Baudrate'"),
                   P("url", "string", "read: URL (InfoSys oder freigegebene Domain) oder Dateipfad in einer freigegebenen Quelle"),
                   P("offset", "integer", "read: ab Zeichen (fuer lange Dokumente)"), P("max", "integer", "read: max. Zeichen (Standard 12000)")),
            a =>
            {
                var dir = SolutionDir();
                return (Str(a, "action") ?? (Str(a, "url") != null ? "read" : "search")).ToLowerInvariant() switch
                {
                    "sources" => Knowledge.Describe(dir),
                    "read" => Knowledge.Read(dir, Str(a, "url") ?? Str(a, "path"), Int(a, "offset", 0), Math.Clamp(Int(a, "max", 12000), 1000, 60000)),
                    _ => Knowledge.Search(dir, Str(a, "query"))
                };
            });

        Add("tc_docs_add", "Traegt eine Wissensquelle ein (URL bzw. Domain, Ordner oder Netzlaufwerk) - nur nach Wunsch des Nutzers. " +
                           "TcAgent liest Quellen ausschliesslich, schreibt dort nie.",
            Schema(Req(P("source", "string", "z. B. https://infosys.beckhoff.com/content/1031/tf6340_tc3_serial_communication/ oder \\\\server\\Handbuecher")),
                   P("scope", "string", "project (Standard, .tcagent\\knowledge.md in der Solution) oder personal"),
                   P("note", "string", "kurzer Kommentar, wofuer die Quelle gut ist")),
            a =>
            {
                var (file, res) = Knowledge.Add(SolutionDir(), Str(a, "scope"), Str(a, "source"), Str(a, "note"));
                return $"{res}\nDatei: {file}";
            });

        Add("tc_policy", "Zeigt die Leitplanken: freigegebene Zielsysteme fuer Laufzeit-Eingriffe, Nur-Lese-Modus, Pfad des Audit-Logs.",
            Schema(), a => policy.Describe());

        Add("tc_feedback", "Meldet ein Problem, eine fehlende Funktion oder einen Verbesserungsvorschlag an die TcAgent-Entwicklung. " +
                           "Bitte immer nutzen, wenn ein Werkzeug fehlschlaegt, unerwartet reagiert, eine Aufgabe nur per Umweg loesbar war oder ein Werkzeug fehlt.",
            Schema(Req(P("type", "string", "bug | missing_feature | usability | docs")),
                   Req(P("summary", "string", "Ein Satz: was ist das Problem / was fehlt")),
                   P("tool", "string", "Betroffenes Werkzeug, z. B. tc_plc_create"),
                   P("details", "string", "Aufruf/Argumente, Fehlermeldung, Kontext (TwinCAT-Version, Projektart)"),
                   P("expected", "string", "Was haette passieren sollen"),
                   P("workaround", "string", "Wie die Aufgabe trotzdem geloest wurde (falls ueberhaupt)"),
                   P("severity", "string", "blocker | major | minor")),
            a =>
            {
                var path = Environment.GetEnvironmentVariable("TCAGENT_FEEDBACK") ??
                           Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "feedback", "feedback.jsonl"));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var entry = new JsonObject
                {
                    ["time"] = DateTime.Now.ToString("o"), ["serverVersion"] = Program.ServerVersion, ["client"] = Program.ClientName,
                    ["solution"] = tc.InstanceInfo, ["status"] = "open"
                };
                foreach (var k in new[] { "type", "severity", "tool", "summary", "details", "expected", "workaround" })
                    if (Str(a, k) != null) entry[k] = Str(a, k);
                File.AppendAllText(path, entry.ToJsonString(new System.Text.Json.JsonSerializerOptions
                    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine);
                return "Danke - Rueckmeldung gespeichert in " + path;
            });

        Add("tc_version", "Zeigt die Version des TcAgent-Servers.", Schema(), a => $"TcAgent {Program.ServerVersion}");

        // ---------------------------------------------------------- Baum / XML
        Add("tc_tree", "Zeigt den TwinCAT-Projektbaum (Name, Typ, Pfad). Ohne path: alle Wurzeln (TIPC=PLC, TIID=I/O, TIRT=Tasks ...).",
            Schema(P("path", "string", "Startpfad, z. B. TIPC oder TIID^Device 1 (EtherCAT)"), P("depth", "integer", "Tiefe (Standard 2)")),
            a => tc.Sta.Run(() => tc.Tree(Str(a, "path"), Int(a, "depth", 2))));

        Add("tc_xml_get", "Liest die XML-Beschreibung eines Baumelements (ProduceXml): Parameter, I/O-Konfiguration, Task-Einstellungen, Online-Settings ...",
            Schema(Req(P("path", "string", "Baumpfad")), P("recursive", "boolean", "Kinder einschliessen")),
            a => tc.Sta.Run(() => (string)((dynamic)tc.Item(Str(a, "path"))).ProduceXml(Bool(a, "recursive", false))));

        Add("tc_xml_set", "Schreibt XML in ein Baumelement (ConsumeXml) - aendert Konfiguration. Vorher mit tc_xml_get das Format lesen.",
            Schema(Req(P("path", "string", "Baumpfad")), Req(P("xml", "string", "XML im Format von ProduceXml (nur zu aendernde Teile)"))),
            a => tc.Sta.Run(() => { ((dynamic)tc.Item(Str(a, "path"))).ConsumeXml(Str(a, "xml")); return "OK"; }));

        Add("tc_delete", "Loescht ein Kind-Element im Baum.",
            Schema(Req(P("parentPath", "string", "Pfad des Elternelements")), Req(P("name", "string", "Name des Kindes"))),
            a => tc.Sta.Run(() => { ((dynamic)tc.Item(Str(a, "parentPath"))).DeleteChild(Str(a, "name")); return "geloescht"; }));

        // ---------------------------------------------------------- I/O-Geraete anlegen
        Add("tc_io_add", "Legt I/O-Konfiguration an: Geraete unter TIID (z. B. EtherCAT-Master), darunter Boxen/Koppler/Klemmen per " +
                         "Produktbezeichnung (EK1100, EL6001, EL1008 ...). Mit 'children' entsteht eine ganze Station in einem Aufruf, " +
                         "z. B. kind=box product=EK1100 children=EL1008,EL2008,EL6001. Danach Parameter mit tc_xml_get/tc_xml_set, " +
                         "Prozessdaten mit tc_link. Ohne angeschlossene Hardware wird offline konfiguriert (spaeter per Scan pruefen).",
            Schema(Req(P("kind", "string", "ethercat_master (Geraet unter TIID), box (EtherCAT-Koppler/-Klemme/-Box, braucht product) oder raw (subType angeben)")),
                   P("parentPath", "string", "Elternelement: TIID fuer Geraete (Standard), z. B. 'TIID^Device 1 (EtherCAT)' fuer Boxen, '...^Term 1 (EK1100)' fuer Klemmen"),
                   P("product", "string", "box: Produktbezeichnung, z. B. EK1100 oder mit Revision EL6001-0000-0020"),
                   P("children", "string", "box: weitere Klemmen unter dem neuen Element, komma-getrennt in Steckreihenfolge, z. B. EL1008,EL2008,EL6001"),
                   P("name", "string", "Name im Baum (leer = TwinCAT-uebliche Benennung, z. B. 'Term 1 (EK1100)')"),
                   P("subType", "integer", "raw: TwinCAT-Subtyp fuer CreateChild"),
                   P("dryRun", "boolean", "Nur zeigen, was angelegt wuerde")),
            a => tc.Sta.Run(() => IoAdd(a)));

        RegisterIoScan();

        // ---------------------------------------------------------- I/O-Verknuepfungen
        Add("tc_link", "Verknuepft Variablen (PLC-Variable mit I/O-Kanal). Einzeln mit source/target oder viele auf einmal mit pairs. " +
                       "Pfade wie im Baum, z. B. 'TIPC^MyPlc^MyPlc Instance^PlcTask Inputs^MAIN.bIn' und " +
                       "'TIID^Device 1 (EtherCAT)^Term 2 (EL1008)^Channel 1^Input'. Pfade vorher mit tc_io_vars ermitteln. " +
                       "PLC-Variablen erscheinen erst nach einem echten Build (tc_build, nicht tc_check), wenn sie mit AT %I*/%Q* deklariert sind.",
            Schema(P("source", "string", "Erster Variablenpfad"), P("target", "string", "Zweiter Variablenpfad"),
                   P("pairs", "string", "Mehrere Verknuepfungen, eine je Zeile: '<pfad> => <pfad>'"),
                   P("dryRun", "boolean", "Nur pruefen, ob alle Pfade existieren")),
            a => tc.Sta.Run(() => Link(a)));

        Add("tc_io_vars", "Listet verknuepfbare Variablen unter einem Baumelement mit Typ - z. B. alle Kanaele einer Klemme " +
                          "('TIID^Device 1 (EtherCAT)^Term 2 (EL1008)') oder das Prozessabbild der PLC ('TIPC^MyPlc^MyPlc Instance'). " +
                          "Liefert die exakten Pfade fuer tc_link.",
            Schema(Req(P("path", "string", "Baumelement, z. B. eine Klemme, ein Geraet oder die PLC-Instanz")),
                   P("filter", "string", "nur Pfade, die diesen Text enthalten"), P("max", "integer", "max. Eintraege (Standard 300)")),
            a => tc.Sta.Run(() => IoVars(Str(a, "path"), Str(a, "filter"), Int(a, "max", 300))));

        Add("tc_unlink", "Loest Verknuepfungen einer Variablen (optional nur zu target).",
            Schema(Req(P("source", "string", "Variablenpfad")), P("target", "string", "Gegenstelle (leer = alle)")),
            a => tc.Sta.Run(() => { ((dynamic)tc.RequireSysMan()).UnlinkVariables(Str(a, "source"), Str(a, "target") ?? ""); return "geloest"; }));

        // ---------------------------------------------------------- PLC-Code
        Add("tc_plc_read", "Liest Deklaration und Implementierung eines PLC-Objekts (POU, Methode, Property, DUT, GVL) samt Unterobjekten.",
            Schema(Req(P("path", "string", "z. B. TIPC^MyPlc^MyPlc Project^POUs^FB_Axis"))),
            a => tc.Sta.Run(() => PlcRead(Str(a, "path"))));

        Add("tc_plc_write", "Ueberschreibt Deklaration und/oder Implementierung eines PLC-Objekts (ST-Text). Mit dryRun=true (im Modus 'propose' immer) wird nichts geschrieben, sondern ein Diff geliefert.",
            Schema(Req(P("path", "string", "Baumpfad des Objekts")), P("declaration", "string", "kompletter Deklarationsteil"),
                   P("implementation", "string", "kompletter Implementierungsteil"),
                   P("dryRun", "boolean", "Nur Diff zeigen, nichts aendern")),
            a => tc.Sta.Run(() =>
            {
                var item = tc.Item(Str(a, "path"));
                return DryRun(a) ? PlcDiff(item, Str(a, "declaration"), Str(a, "implementation"))
                                 : PlcWrite(item, Str(a, "declaration"), Str(a, "implementation"));
            }));

        Add("tc_plc_create", "Legt ein PLC-Objekt an und setzt optional Deklaration/Implementierung. kind: " + string.Join(", ", PlcKinds.Keys),
            Schema(Req(P("parentPath", "string", "Elternordner/-POU, z. B. ...^POUs oder ...^POUs^FB_Axis fuer Methoden")),
                   Req(P("name", "string", "Objektname")), Req(P("kind", "string", "Objekttyp")),
                   P("language", "string", "ST (Standard), IL, SFC, FBD, CFC, LD"),
                   P("returnType", "string", "Rueckgabetyp bei function/method/property"),
                   P("declaration", "string", "kompletter Deklarationsteil"), P("implementation", "string", "Implementierungsteil (bei property: der Get-Accessor)"),
                   P("implements", "string", "nur function_block: Interface(s), komma-getrennt - legt IMPLEMENTS und alle Methoden/Properties " +
                                              "des Interfaces (inkl. EXTENDS) als Stubs an, wie der TwinCAT-Dialog. Projekt- und Bibliotheks-Interfaces."),
                   P("getImplementation", "string", "property: Code des Get-Accessors"), P("setImplementation", "string", "property: Code des Set-Accessors"),
                   P("dryRun", "boolean", "Nur zeigen, was angelegt wuerde")),
            a => tc.Sta.Run(() => PlcCreate(a)));

        Add("tc_plc_search", "Durchsucht Deklarationen und Implementierungen aller PLC-Objekte (Volltext oder Regex). Treffer mit Baumpfad, Teil und Zeile - z. B. um Verwendungen eines FBs, einer Variablen oder eines Aufrufs zu finden.",
            Schema(Req(P("pattern", "string", "Suchtext oder regulaerer Ausdruck")),
                   P("regex", "boolean", "pattern als Regex (Standard false)"),
                   P("caseSensitive", "boolean", "Gross-/Kleinschreibung beachten (Standard false)"),
                   P("part", "string", "decl, impl oder all (Standard)"),
                   P("path", "string", "Nur unterhalb dieses Baumpfads suchen (Standard: alle PLC-Projekte)"),
                   P("max", "integer", "Max. Treffer (Standard 200)")),
            a => tc.Sta.Run(() => PlcSearch(a)));

        Add("tc_plc_references", "Listet die Bibliotheksreferenzen der PLC-Projekte mit aufgeloester Version und Hersteller (auch Platzhalter). Mit installed=true zusaetzlich die im Repository installierten Bibliotheken.",
            Schema(P("plcProjectPath", "string", "Pfad des PLC-Projekts (Standard: alle)"),
                   P("installed", "boolean", "Installierte Bibliotheken mit auflisten")),
            a => tc.Sta.Run(() => PlcReferences(Str(a, "plcProjectPath"), Bool(a, "installed", false))));

        Add("tc_editor_context", "Zeigt, woran der Nutzer gerade in der IDE arbeitet: aktives Fenster/Dokument, markierte Elemente, daraus ermittelte Baumpfade (direkt fuer tc_plc_read nutzbar) und - mit der VS-Erweiterung TcAgentBridge - Cursor und Selektion. Nutzen, wenn der Nutzer von 'hier', 'diesem FB' oder 'der Methode' spricht.",
            Schema(), a =>
            {
                var (text, pid) = tc.Sta.Run(() => (tc.EditorContext(), tc.AttachedPid));
                if (!BridgeClient.Available(pid)) return text + "(VS-Erweiterung TcAgentBridge nicht aktiv - Cursor/Selektion nicht verfuegbar)\n";
                // Cursor/Selektion kommen aus der Bruecke -> den DTE-Hinweis "nicht verfuegbar" weglassen (widerspruechlich)
                try { return string.Join("\n", text.Split('\n').Where(l => !l.StartsWith("Cursor/Selektion: vom TwinCAT-Editor"))) + BridgeContext(BridgeClient.Call(pid, "context"), text); }
                catch (Exception ex) { return text + "\nTcAgentBridge: " + ex.Message + "\n"; }
            });

        Add("tc_goto_definition", "Gehe zu Definition: loest einen Namen so auf wie TwinCAT selbst - im Gueltigkeitsbereich des im Editor offenen Objekts. Ohne symbol wird das Wort am Cursor (bzw. die Markierung) genommen. Liefert bei Variablen Typ, Deklarationsort und den aufgeloesten Typ; bei Typen/POUs den Ort im Projekt (Baumpfad) oder die Bibliothek. Braucht die VS-Erweiterung TcAgentBridge.",
            Schema(P("symbol", "string", "Name, z. B. _startTime, MC_MoveLinearAbsoluteFB, GVL_Progress.Progress (leer = Wort am Cursor)")),
            a => GotoDefinition(a));

        Add("tc_find_references", "Findet alle Verwendungen eines Symbols: Querverweise wie in TwinCATs Kreuzreferenzliste plus Textsuche nach dem ganzen Wort als Gegenprobe.",
            Schema(Req(P("symbol", "string", "Name des Symbols (Variable, FB, Methode, Typ)")), P("max", "integer", "Max. Treffer je Quelle (Standard 300)")),
            a => FindReferences(a));

        Add("tc_ide", "Experimentell: direkte Abfrage der VS-Erweiterung TcAgentBridge im IDE-Prozess. command: ping (laeuft sie?), context (Editor-Kontext roh), libs (installierte Bibliotheken im Repository), probe (Diagnose: was ist im IDE-Prozess erreichbar; schreibt probe.txt).",
            Schema(Req(P("command", "string", "ping | context | libs | probe | lm_call | chat_show (Chat-Fenster in TwinCAT oeffnen)")),
                   P("target", "string", "lm_call (Diagnose): Abfrageziel lm (Standard), om, engine oder view"),
                   P("method", "string", "lm_call: nur lesende Methode (Get*/Find*/All*/Is*)"),
                   P("args", "string", "lm_call: Argumente durch | getrennt; $ph = Projekt-Handle, $obj = Objekt im Editor")),
            a =>
            {
                var cmd = Str(a, "command");
                if (cmd is not ("ping" or "context" or "libs" or "probe" or "lm_call" or "chat_show")) throw new Exception("command muss ping, context, libs, probe, lm_call oder chat_show sein");
                var pid = tc.Sta.Run(() => { try { tc.RequireDte(); } catch { } return tc.AttachedPid; });
                JsonObject args = null;
                if (cmd == "lm_call")
                    args = new JsonObject { ["target"] = Str(a, "target") ?? "lm", ["method"] = Str(a, "method"),
                        ["args"] = new JsonArray((Str(a, "args") ?? "").Split('|', StringSplitOptions.None).Where((x, i) => !(i == 0 && x.Length == 0 && (Str(a, "args") ?? "").Length == 0)).Select(x => (JsonNode)JsonValue.Create(x)).ToArray()) };
                var res = Pretty(BridgeClient.Call(pid, cmd, args, cmd == "probe" || cmd == "lm_call" ? 120000 : 30000));
                return res.Length > 60000 ? res[..60000] + "\n... (abgeschnitten)" : res;
            });

        Add("tc_lib_signatures", "Signaturen einer Bibliothek - auch aus compiled libraries von Fremdherstellern - (VS-Erweiterung TcAgentBridge noetig). Ohne symbol: Gliederung (eine Zeile je FB/Funktion/Typ, mit EXTENDS und Anzahl Ein-/Ausgaenge). Mit symbol: Schnittstelle als ST-Deklaration inkl. geerbter Ein-/Ausgaenge, Methoden und Doku-Kommentaren. Ergebnis wird je Bibliotheksversion zwischengespeichert.",
            Schema(Req(P("library", "string", "Bibliothekstitel, z. B. RobotLibrary oder Tc2_Standard")),
                   P("version", "string", "Version (Standard: die im Projekt aufgeloeste, sonst die neueste installierte)"),
                   P("symbol", "string", "Name eines FB, einer Funktion, eines Typs; Methode als FB.Methode"),
                   P("filter", "string", "Gliederung nur fuer Namen, die diesen Text enthalten (z. B. MC_ oder FB)"),
                   P("source", "string", "lm (Standard: mit EXTENDS, Methoden, Originalschreibweise) oder doc (alternativer Export, Rueckfall)"),
                   P("refresh", "boolean", "Zwischenspeicher neu erzeugen")),
            a => LibSignatures(a));

        Add("tc_plc_library_add", "Fuegt einer PLC eine Bibliotheksreferenz hinzu.",
            Schema(Req(P("referencesPath", "string", "Pfad des References-Knotens, z. B. TIPC^MyPlc^MyPlc Project^References")),
                   Req(P("library", "string", "Bibliotheksname")), P("version", "string", "Version oder * (Standard)"),
                   P("vendor", "string", "Hersteller, z. B. Beckhoff Automation GmbH")),
            a => tc.Sta.Run(() =>
            {
                var lm = (ITcPlcLibraryManager)tc.Item(Str(a, "referencesPath"));
                lm.AddLibrary(Str(a, "library"), Str(a, "version") ?? "*", Str(a, "vendor") ?? "");
                return "Bibliothek hinzugefuegt";
            }));

        Add("tc_dialogs", "Zeigt offene modale Dialoge der IDE (Titel, Text, Knoepfe) - solange einer offen ist, warten alle TwinCAT-Aufrufe. " +
                          "action=close schliesst sie wie das X bzw. Abbrechen (nie Ja/OK/Ueberschreiben). Normalerweise den Benutzer schliessen lassen.",
            Schema(P("action", "string", "list (Standard) oder close")),
            a =>
            {
                var d = IdeDialogs.Find(tc.AttachedPid);
                if (d.Count == 0) return tc.AttachedPid > 0 ? "Kein Dialog offen." : "Nicht mit einer IDE verbunden.";
                if ((Str(a, "action") ?? "list") != "close") return "Offene Dialoge:\n  " + string.Join("\n  ", d);
                foreach (var x in d) IdeDialogs.Close(x);
                return "Geschlossen (Abbrechen/X):\n  " + string.Join("\n  ", d);
            });

        Add("tc_save", "Speichert alle geaenderten Dateien der Solution auf die Platte (wie Strg+Shift+S). Aenderungen per Werkzeug sind sonst nur in der IDE.",
            Schema(), a => tc.Sta.Run(() => { tc.RequireDte().ExecuteCommand("File.SaveAll"); return "alles gespeichert"; }));

        // ---------------------------------------------------------- Build / Aktivieren / Online
        Add("tc_check", "Prueft alle PLC-Objekte auf Compilerfehler (\"Alle Objekte pruefen\", auch unbenutzte POUs) - ohne Build, " +
                        "erzeugt also keinen ausfuehrbaren Code und keine Dateien. Der Standard nach Code-Aenderungen. Liefert Fehler/Warnungen mit Datei und Zeile.",
            Schema(), a => tc.Sta.Run(() => tc.Check()));

        Add("tc_build", "Baut die Solution (erzeugt ausfuehrbaren Code und Dateien) und liefert Compilerfehler/Warnungen. Nur noetig vor dem " +
                        "Aktivieren/Laden oder wenn der Nutzer es verlangt - zum Pruefen auf Compilerfehler tc_check nehmen.",
            Schema(P("rebuild", "boolean", "Vorher bereinigen"), P("checkAll", "boolean", "Zusaetzlich alle Objekte pruefen (Standard false)")),
            a => tc.Sta.Run(() => tc.Build(null, Bool(a, "rebuild", false), Bool(a, "checkAll", false))));

        Add("tc_errors", "Liest die Fehlerliste der IDE, getrennt nach Compiler-/Projektmeldungen und Laufzeit-/TcUnit-Meldungen der PLC.",
            Schema(P("kind", "string", "compile, runtime oder all (Standard)"), P("max", "integer", "Max. Eintraege (Standard 500)")),
            a => tc.Sta.Run(() => tc.ErrorList(Int(a, "max", 500), Str(a, "kind") ?? "all")));

        Add("tc_test_results", "Liefert das Ergebnis des letzten TcUnit-Laufs (Suites, Tests, Erfolge, Fehlschlaege mit Testnamen und Meldung) aus den PLC-Laufzeitmeldungen.",
            Schema(), a => tc.Sta.Run(() => tc.TestResults()));

        // ---------------------------------------------------------- Tasks, Bootprojekt, Startmodus, Lizenzen
        Add("tc_task", "Echtzeit-Tasks: list, create (name, cycleUs, priority, withImage) oder set (path, cycleUs, priority, autoStart). " +
                       "Wirkt erst nach tc_activate. Die PLC-Task-Zuordnung steht in der PLC-Instanz (tc_tree/tc_xml_get).",
            Schema(Req(P("action", "string", "list, create oder set")),
                   P("name", "string", "create: Taskname"), P("path", "string", "set: Baumpfad der Task, z. B. TIRT^PlcTask"),
                   P("cycleUs", "integer", "Zykluszeit in Mikrosekunden, z. B. 1000 = 1 ms"),
                   P("priority", "integer", "Prioritaet (kleiner = hoeher)"), P("autoStart", "boolean", "Task automatisch starten"),
                   P("withImage", "boolean", "create: Task mit Prozessabbild (Standard false)"),
                   P("dryRun", "boolean", "Nur zeigen, was geaendert wuerde")),
            a => tc.Sta.Run(() => Task(a)));

        Add("tc_boot_project", "PLC-Bootprojekt: Autostart ein/aus (autostart) und/oder Bootprojekt erzeugen (generate). " +
                               "Ohne Parameter: aktuellen Stand zeigen.",
            Schema(P("plcPath", "string", "PLC-Projekt, z. B. TIPC^MyPlc (leer = das einzige/erste)"),
                   P("autostart", "boolean", "Bootprojekt beim Start der Runtime automatisch starten"),
                   P("generate", "boolean", "Bootprojekt jetzt erzeugen (auf dem aktuellen Ziel)"),
                   P("dryRun", "boolean", "Nur zeigen, was geaendert wuerde")),
            a => tc.Sta.Run(() => BootProject(a)));

        Add("tc_runtime_startup", "Startmodus der TwinCAT-Runtime nach dem Hochfahren des Rechners: run oder config. " +
                                  "Ohne mode: aktuellen Stand zeigen. Bisher nur fuer das lokale Zielsystem (Registry, braucht Administratorrechte).",
            Schema(P("mode", "string", "run oder config (leer = nur anzeigen)")),
            a => RuntimeStartup(Str(a, "mode")));

        Add("tc_licenses", "Zeigt den Lizenz-Knoten der Solution (benoetigte/ausgewaehlte Lizenzen) als XML. Aendern mit tc_xml_set " +
                           "im gezeigten Format. 7-Tage-Testlizenzen kann nur der Nutzer selbst aktivieren (TwinCAT verlangt dafuer " +
                           "die Eingabe eines Sicherheitscodes) - dann den Nutzer darum bitten.",
            Schema(), a => tc.Sta.Run(() => Licenses()));

        Add("tc_activate", "Setzt optional die Ziel-NetId, aktiviert die Konfiguration und startet TwinCAT im Run-Modus neu.",
            Schema(P("netId", "string", "AmsNetId des Ziels, z. B. 5.12.34.56.1.1 (leer = aktuelles Ziel)"),
                   P("restart", "boolean", "TwinCAT neu starten (Standard true)")),
            a => tc.Sta.Run(() =>
            {
                dynamic sm = tc.RequireSysMan();
                var net = Str(a, "netId");
                if (!string.IsNullOrEmpty(net)) sm.SetTargetNetId(net);
                sm.ActivateConfiguration();
                if (Bool(a, "restart", true)) sm.StartRestartTwinCAT();
                return "aktiviert auf " + (string)sm.GetTargetNetId();
            }));

        Add("tc_plc_online", "PLC-Online-Befehle ueber das PLC-Projekt: login, download/Online Change, start, stop, logout.",
            Schema(Req(P("plcProjectPath", "string", "Pfad des PLC-Projekts, z. B. TIPC^MyPlc^MyPlc Project")),
                   P("login", "boolean", ""), P("start", "boolean", ""), P("stop", "boolean", ""), P("logout", "boolean", "")),
            a =>
            {
                // Befehle einzeln und nacheinander senden - Login und Start in einem XML wird von TwinCAT nicht zuverlaessig ausgefuehrt
                var done = new List<string>();
                foreach (var (k, tag) in new[] { ("login", "LoginCmd"), ("stop", "StopCmd"), ("start", "StartCmd"), ("logout", "LogoutCmd") })
                {
                    if (!Bool(a, k, false)) continue;
                    var xml = $"<TreeItem><IECProjectDef><OnlineSettings><Commands><{tag}>true</{tag}></Commands></OnlineSettings></IECProjectDef></TreeItem>";
                    tc.Sta.Run(() => { ((dynamic)tc.Item(Str(a, "plcProjectPath"))).ConsumeXml(xml); return 0; });
                    done.Add(k);
                    Thread.Sleep(k == "login" ? 3000 : 1000);
                }
                string state;
                try { using var c = Connect(a); state = c.ReadState().AdsState.ToString(); } catch (Exception ex) { state = "unbekannt (" + ex.Message + ")"; }
                return $"ausgefuehrt: {string.Join(", ", done)}; PLC-Zustand (ADS): {state}";
            });

        Add("tc_plc_state", "Liest den Laufzeitzustand der PLC per ADS (Run, Stop, Config ...).",
            Schema(P("netId", "string", "AmsNetId (leer = lokal)"), P("port", "integer", "ADS-Port (Standard 851)")),
            a => { using var c = Connect(a); var s = c.ReadState(); return $"AdsState={s.AdsState} DeviceState={s.DeviceState}"; });

        // ---------------------------------------------------------- ADS (Laufzeit)
        Add("ads_read", "Liest eine Variable zur Laufzeit per ADS (z. B. MAIN.nCounter).",
            Schema(Req(P("symbol", "string", "Symbolpfad")), P("netId", "string", "AmsNetId (leer = lokal)"), P("port", "integer", "ADS-Port (Standard 851)")),
            a => Ads(a, (sym) => Format(sym.ReadValue())));

        Add("ads_write", "Schreibt eine Variable zur Laufzeit per ADS.",
            Schema(Req(P("symbol", "string", "Symbolpfad")), Req(P("value", "string", "Neuer Wert (Text, wird in den Variablentyp konvertiert)")),
                   P("netId", "string", "AmsNetId (leer = lokal)"), P("port", "integer", "ADS-Port (Standard 851)")),
            a => Ads(a, (sym) =>
            {
                var current = sym.ReadValue();
                var text = Str(a, "value");
                object v = current is bool ? (object)(text.Trim().ToUpperInvariant() is "TRUE" or "1")
                         : Convert.ChangeType(text, current.GetType(), CultureInfo.InvariantCulture);
                sym.WriteValue(v);
                return $"{Format(current)} -> {Format(sym.ReadValue())}";
            }));

        Add("ads_symbols", "Listet Laufzeitsymbole per ADS (optional gefiltert).",
            Schema(P("filter", "string", "Teilstring, z. B. MAIN."), P("netId", "string", "AmsNetId"), P("port", "integer", "ADS-Port (851)")),
            a =>
            {
                using var c = Connect(a);
                var loader = SymbolLoaderFactory.Create(c, new SymbolLoaderSettings(SymbolsLoadMode.VirtualTree));
                var f = Str(a, "filter") ?? "";
                var sb = new StringBuilder();
                void Walk(ISymbol s, int lvl)
                {
                    if (lvl > 3) return;
                    if (s.InstancePath.Contains(f, StringComparison.OrdinalIgnoreCase))
                        sb.AppendLine($"{s.InstancePath}\t{s.TypeName}");
                    foreach (ISymbol sub in s.SubSymbols) Walk(sub, lvl + 1);
                }
                foreach (ISymbol s in loader.Symbols) Walk(s, 0);
                return sb.Length > 0 ? sb.ToString() : "keine Symbole";
            });
    }

    // ------------------------------------------------------------------ PLC-Helfer
    string PlcRead(string path)
    {
        var item = tc.Item(path);
        var sb = new StringBuilder($"# {item.Name}  [sub={item.ItemSubType}]\n");
        if (item is ITcPlcDeclaration d) sb.Append("## Deklaration\n").Append(d.DeclarationText).Append('\n');
        if (item is ITcPlcImplementation i) sb.Append("## Implementierung\n").Append(i.ImplementationText).Append('\n');
        foreach (var c in TwinCatSession.Children(item))
        {
            sb.Append($"\n### {c.Name}  [sub={c.ItemSubType}]  {c.PathName}\n");
            if (c is ITcPlcDeclaration cd) sb.Append(cd.DeclarationText).Append('\n');
            if (c is ITcPlcImplementation ci) sb.Append("--- Implementierung ---\n").Append(ci.ImplementationText).Append('\n');
        }
        return sb.ToString();
    }

    static string PlcWrite(ITcSmTreeItem item, string decl, string impl)
    {
        var done = new List<string>();
        if (decl != null)
        {
            if (item is not ITcPlcDeclaration d) throw new Exception("Objekt hat keinen Deklarationsteil");
            d.DeclarationText = decl; done.Add("Deklaration");
        }
        if (impl != null)
        {
            if (item is not ITcPlcImplementation i) throw new Exception("Objekt hat keinen Implementierungsteil");
            i.ImplementationText = impl; done.Add("Implementierung");
        }
        return done.Count == 0 ? "nichts geaendert" : string.Join(" + ", done) + " geschrieben";
    }

    static string Pretty(JsonObject o) => o.ToJsonString(new System.Text.Json.JsonSerializerOptions
        { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    string LibSignatures(JsonObject a)
    {
        var lib = Str(a, "library");
        var (pid, version) = tc.Sta.Run(() =>
        {
            string v = Str(a, "version");
            if (string.IsNullOrEmpty(v))
                try { v = ReferencedVersion(lib); } catch { }
            return (tc.AttachedPid, v);
        });
        var source = Str(a, "source") ?? "lm";
        if (source == "lm")
        {
            // Projektmodell: kompakt je Bibliotheksversion zwischengespeichert
            var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "libcache");
            Directory.CreateDirectory(cacheDir);
            var apiFile = Path.Combine(cacheDir, $"{lib}_{(string.IsNullOrEmpty(version) ? "aktuell" : version)}.api.json");
            LibApi api = null; string origin;
            if (File.Exists(apiFile) && !Bool(a, "refresh", false)) { api = LibApi.Load(apiFile); origin = "aus Zwischenspeicher"; }
            else
            {
                var lr = BridgeClient.Call(pid, "lm_export", new JsonObject { ["library"] = lib }, 300000);
                if (lr["found"]?.GetValue<bool>() != true || lr["file"] == null)
                    return $"Kein Language-Model-Kontext fuer '{lib}' gefunden (Bibliothek im Projekt referenziert?). Kontexte:\n" +
                           string.Join("\n", (lr["contexts"] as JsonArray ?? new JsonArray()).Select(x => "  " + x));
                var raw = lr["file"]!.ToString();
                api = LibApi.FromLm(File.ReadAllText(raw), lib);
                var libIds = api.Symbols.Select(x => x.LibraryId).Where(x => x != null).Distinct().ToList();
                if (libIds.Count > 0 && !libIds.Any(x => x.StartsWith(lib + ",", StringComparison.OrdinalIgnoreCase)))
                    return $"Falscher Kontext erwischt ({string.Join("; ", libIds)}) - bitte mit source=doc abfragen und tc_feedback melden.";
                api.Save(apiFile);
                try { File.Delete(raw); } catch { }
                origin = $"neu aus der IDE gelesen ({libIds.FirstOrDefault()})";
            }
            var s0 = Str(a, "symbol");
            return $"{lib} {version} - {origin}\n" + (string.IsNullOrEmpty(s0) ? api.Outline(Str(a, "filter")) : api.Describe(s0));
        }
        var args = new JsonObject { ["library"] = lib, ["version"] = version ?? "*", ["refresh"] = Bool(a, "refresh", false) };
        var r = BridgeClient.Call(pid, "lib_export", args, 300000);
        var file = r["file"]!.ToString();
        var json = File.ReadAllText(file);
        var head = $"{r["title"]} {r["version"]} ({r["company"]}) - {(r["cached"]?.GetValue<bool>() == true ? "aus Zwischenspeicher" : "neu exportiert")}, {json.Length} Zeichen, {file}\n";
        var sym = Str(a, "symbol");
        return head + (string.IsNullOrEmpty(sym) ? LibJson.Outline(json, Str(a, "filter")) : LibJson.Find(json, sym));
    }

    // Im Projekt aufgeloeste Version einer referenzierten Bibliothek (Platzhalter beruecksichtigt)
    string ReferencedVersion(string library)
    {
        foreach (var plc in tc.PlcProjects())
            foreach (var c in TwinCatSession.Children(plc))
            {
                if (c is not ITcPlcLibraryManager lm) continue;
                foreach (object r in (System.Collections.IEnumerable)((dynamic)lm).References)
                {
                    var d = DescribeLib(r);   // "Platzhalter X -> Name, Version (Hersteller)" bzw. "Name, Version (Hersteller)"
                    var lib = d.Contains(" -> ") ? d[(d.IndexOf(" -> ") + 4)..] : d;
                    var parts = lib.Split(',', 2);
                    if (parts.Length == 2 && parts[0].Trim().Equals(library, StringComparison.OrdinalIgnoreCase))
                        return parts[1].Split('(')[0].Trim();
                }
            }
        return null;
    }

    // ------------------------------------------------------------------ Editor-Cursor (aus der Bruecke) auswerten
    sealed record Cursor(string Path, string Part, int Line, int Col, string[] Code, string Selected, string SelText);

    // TwinCAT zaehlt die Zeilen ueber Deklaration + Implementierung durch -> auf den Teil umrechnen
    Cursor AnalyzeCursor(JsonObject b, string dteText)
    {
        string V(string k) => b[k]?.ToString();
        var path = dteText.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("TIPC^"));
        string decl = null, impl = null;
        if (path != null)
            try
            {
                (decl, impl) = tc.Sta.Run(() =>
                {
                    var it = tc.Item(path);
                    return ((it as ITcPlcDeclaration)?.DeclarationText, (it as ITcPlcImplementation)?.ImplementationText);
                });
            }
            catch { }
        string[] Lines(string t) { if (t == null) return Array.Empty<string>(); t = t.Replace("\r\n", "\n"); if (t.EndsWith("\n")) t = t[..^1]; return t.Split('\n'); }
        var dl = Lines(decl); var il = Lines(impl);
        // Bevorzugt: Cursor direkt aus dem Editor-Control der aktiven Haelfte (unabhaengig vom VS-Fokus)
        if (b["active"] is JsonObject act && int.TryParse(act["line"]?.ToString(), out var al))
        {
            var ap = act["part"]?.ToString();
            int.TryParse(act["column"]?.ToString(), out var acol);
            var st = act["selectedText"]?.ToString();
            return new Cursor(path, ap, al, Math.Max(1, acol), ap == "Implementierung" ? il : dl,
                              string.IsNullOrEmpty(st) ? null : $"{ap}, Zeile {al}", string.IsNullOrEmpty(st) ? null : st);
        }
        int offset = dl.Length;
        var selFirst = (V("selectedText") ?? "").Replace("\r\n", "\n").Split('\n')[0].Trim();
        if (selFirst.Length > 0 && int.TryParse(V("selStartLine"), out var sl))
            foreach (var k in new[] { dl.Length, dl.Length + 1, dl.Length - 1 })   // Plausibilitaet: markierter Text muss in der Zeile stehen
            {
                var src = sl > k ? il : dl; var idx = sl > k ? sl - k : sl;
                if (idx >= 1 && idx <= src.Length && src[idx - 1].Contains(selFirst)) { offset = k; break; }
            }
        if (!int.TryParse(V("caretLine"), out var cl)) return new Cursor(path, null, 0, 0, null, null, V("selectedText"));
        var part = cl > offset ? "Implementierung" : "Deklaration";
        var line = cl > offset ? cl - offset : cl;
        string sel = null;
        if (int.TryParse(V("selStartLine"), out var s1) && int.TryParse(V("selEndLine"), out var s2) && (s1 != s2 || V("selStartColumn") != V("selEndColumn")))
            sel = $"{(s1 > offset ? "Implementierung" : "Deklaration")}, Zeile {(s1 > offset ? s1 - offset : s1)}:{V("selStartColumn")} bis {(s2 > offset ? s2 - offset : s2)}:{V("selEndColumn")}";
        return new Cursor(path, part, line, int.TryParse(V("caretColumn"), out var cc) ? cc : 1, part == "Implementierung" ? il : dl, sel, V("selectedText"));
    }

    string BridgeContext(JsonObject b, string dteText)
    {
        var o = new StringBuilder("\n--- aus der IDE (TcAgentBridge) ---\n");
        var c = AnalyzeCursor(b, dteText);
        if (c.Part == null) return o.AppendLine("Cursor nicht verfuegbar. Roh: " + Pretty(b)).ToString();
        o.AppendLine($"Cursor: {c.Part}, Zeile {c.Line}, Spalte {c.Col}");
        var w = WordAt(c);
        if (w != null) o.AppendLine("Wort am Cursor: " + w);
        if (c.Selected != null) o.AppendLine("Selektion: " + c.Selected);
        if (c.SelText != null) o.AppendLine("Markierter Text:\n" + c.SelText);
        if (c.Path != null && c.Code.Length > 0)
        {
            o.AppendLine($"Code um den Cursor ({c.Path}, {c.Part}):");
            for (int i = Math.Max(1, c.Line - 3); i <= Math.Min(c.Code.Length, c.Line + 3); i++)
                o.AppendLine($"{(i == c.Line ? ">" : " ")}{i,5}: {c.Code[i - 1]}");
        }
        return o.ToString();
    }

    // Bezeichner (mit Punkt-Pfad, z. B. GVL_Progress.Progress) an der Cursorposition
    static string WordAt(Cursor c)
    {
        if (c.Code == null || c.Line < 1 || c.Line > c.Code.Length) return null;
        var t = c.Code[c.Line - 1];
        int i = Math.Clamp(c.Col - 1, 0, t.Length);
        bool W(char ch) => char.IsLetterOrDigit(ch) || ch == '_' || ch == '.';
        if (i == t.Length || !W(t[i])) { if (i > 0 && W(t[i - 1])) i--; else return null; }
        int a = i, e = i;
        while (a > 0 && W(t[a - 1])) a--;
        while (e < t.Length - 1 && W(t[e + 1])) e++;
        var word = t[a..(e + 1)].Trim('.');
        return word.Length == 0 || char.IsDigit(word[0]) ? null : word;
    }

    // Gehe zu Definition: Name (oder Wort am Cursor) wie TwinCAT im Gueltigkeitsbereich des offenen Objekts aufloesen
    string GotoDefinition(JsonObject a)
    {
        var (text, pid) = tc.Sta.Run(() => (tc.EditorContext(), tc.AttachedPid));
        var name = Str(a, "symbol");
        if (string.IsNullOrWhiteSpace(name))
        {
            var c = AnalyzeCursor(BridgeClient.Call(pid, "context"), text);
            // Markierung: erster Bezeichner darin (z. B. "_startTime := TIME();" -> _startTime), sonst Wort am Cursor
            var m = Regex.Match(c.SelText ?? "", @"[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*");
            name = m.Success ? m.Value : WordAt(c);
            if (string.IsNullOrEmpty(name)) return "Kein Bezeichner am Cursor - symbol angeben oder im Editor auf einen Namen klicken.";
        }
        var r = BridgeClient.Call(pid, "goto", new JsonObject { ["name"] = name });
        var o = new StringBuilder($"'{name}' (aufgeloest im Kontext {r["context"]}):\n");
        string Where(JsonNode s)
        {
            if (s == null) return "?";
            if (s["isLibrary"]?.ToString() == "true" || s["isLibrary"]?.ToString() == "True")
                return $"Bibliothek {s["library"]} -> tc_lib_signatures library={s["library"]?.ToString().Split(',')[0]} symbol={s["name"]}";
            var op = s["objectPath"]?.ToString();
            var paths = string.IsNullOrEmpty(op) ? new List<string>() : tc.Sta.Run(() => tc.ResolvePlcPathsPublic(op));
            return paths.Count > 0 ? "Projekt: " + string.Join(" | ", paths) + "  (tc_plc_read)" : "Projekt: " + op;
        }
        if (r["variable"] is JsonObject v)
        {
            o.AppendLine($"Variable {v["name"]} : {v["type"]}  [{v["flags"]}]" + (string.IsNullOrWhiteSpace(v["doc"]?.ToString()) ? "" : $"  // {v["doc"]?.ToString().Trim()}"));
            o.AppendLine($"  deklariert in {v["declaredIn"]?["kind"]} {v["declaredIn"]?["name"]}: {Where(v["declaredIn"])}");
            if (r["type"] is JsonObject t)
                o.AppendLine($"  Typ {t["name"]} ({t["kind"]}{(string.IsNullOrEmpty(t["extends"]?.ToString()) ? "" : " EXTENDS " + t["extends"])}): {Where(t)}");
        }
        else if (r["signatures"] is JsonArray sigs && sigs.Count > 0)
            foreach (var s in sigs)
                o.AppendLine($"{s?["kind"]} {s?["name"]}{(string.IsNullOrEmpty(s?["extends"]?.ToString()) ? "" : " EXTENDS " + s?["extends"])}: {Where(s)}");
        else o.AppendLine("Nicht gefunden (weder Variable im aktuellen Objekt noch Typ/POU). Fuer Textsuche: tc_plc_search.");
        if (r["members"] is JsonArray mem)
            foreach (var mm in mem)
            {
                if (mm?["notFoundIn"] != null) { o.AppendLine($"  .{mm["name"]}: nicht gefunden in {mm["notFoundIn"]?["name"]}"); continue; }
                o.AppendLine($"  .{mm?["name"]} : {mm?["type"]}  [{mm?["flags"]}]" + (string.IsNullOrWhiteSpace(mm?["doc"]?.ToString()) ? "" : $"  // {mm?["doc"]?.ToString().Trim()}"));
                o.AppendLine($"      deklariert in {mm?["declaredIn"]?["kind"]} {mm?["declaredIn"]?["name"]}: {Where(mm?["declaredIn"])}");
            }
        if (r["memberType"] is JsonObject mt)
            o.AppendLine($"  Typ {mt["name"]} ({mt["kind"]}{(string.IsNullOrEmpty(mt["extends"]?.ToString()) ? "" : " EXTENDS " + mt["extends"])}): {Where(mt)}");
        return o.ToString();
    }

    // Querverweise: semantisch aus dem Projektmodell (mit Zugriffsart), Zeile/Code ueber die Textsuche im selben Objekt
    sealed record Hit(string Path, string Part, int Line, string Text);

    List<Hit> WordHits(string word, int max) => tc.Sta.Run(() =>
    {
        var rx = new Regex($@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase);
        var hits = new List<Hit>();
        foreach (var it in tc.PlcObjects())
        {
            if (hits.Count >= max) break;
            string path = null;
            try { path = it.PathName; } catch { continue; }
            void Scan(string part, string text)
            {
                if (string.IsNullOrEmpty(text)) return;
                var lines = text.Replace("\r\n", "\n").Split('\n');
                for (int n = 0; n < lines.Length && hits.Count < max; n++)
                    if (rx.IsMatch(lines[n])) hits.Add(new Hit(path, part, n + 1, lines[n]));
            }
            try { if (it is ITcPlcDeclaration d) Scan("Deklaration", d.DeclarationText); } catch { }
            try { if (it is ITcPlcImplementation i) Scan("Implementierung", i.ImplementationText); } catch { }
        }
        return hits;
    });

    string FindReferences(JsonObject a)
    {
        var name = Str(a, "symbol");
        int max = Int(a, "max", 300);
        var plain = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
        var hits = WordHits(plain, Math.Max(2000, max * 10));   // Textsuche breiter als die Anzeige, damit jede Fundstelle ihre Zeile bekommt
        var used = new HashSet<Hit>();
        var pid = tc.Sta.Run(() => { try { tc.RequireDte(); } catch { } return tc.AttachedPid; });
        var o = new StringBuilder();
        static string Access(string acc) => acc switch { "Declarative" => "Deklaration", "Read" => "Lesen", "Write" => "Schreiben", "ReadWrite" => "Lesen+Schreiben", "Call" => "Aufruf", _ => acc };
        try
        {
            var r = BridgeClient.Call(pid, "xref", new JsonObject { ["name"] = name, ["max"] = max }, 120000);
            var entries = r["entries"] as JsonArray ?? new JsonArray();
            int total = entries.Sum(e => (e?["refs"] as JsonArray)?.Count ?? 0);
            o.AppendLine($"Querverweise auf '{name}' (wie TwinCATs Kreuzreferenzliste): {total}");
            foreach (var e in entries)
                foreach (var x in (e?["refs"] as JsonArray ?? new JsonArray()).Take(max))
                {
                    var loc = x?["location"]?.ToString();
                    var paths = string.IsNullOrEmpty(loc) ? new List<string>() : tc.Sta.Run(() => tc.ResolvePlcPathsPublic(loc));
                    var path = paths.FirstOrDefault();
                    var part = x?["part"]?.ToString();
                    int.TryParse(x?["column"]?.ToString(), out var col);
                    // passende Textzeile im selben Objekt/Teil, deren Fundstelle am naechsten an der Spalte liegt
                    var cand = hits.Where(h => !used.Contains(h) && h.Path == path && (part == null || h.Part == part))
                                   .OrderBy(h => Math.Abs(h.Text.IndexOf(plain, StringComparison.OrdinalIgnoreCase) + 1 - col)).FirstOrDefault();
                    if (cand != null) used.Add(cand);
                    o.AppendLine($"  {Access(x?["Access"]?.ToString()),-15} {path ?? loc ?? "(Ort unbekannt)"}" +
                                 (part != null ? $"  [{part}{(cand != null ? $" Zeile {cand.Line}" : "")}]" : "") +
                                 (cand != null ? "  " + cand.Text.Trim() : ""));
                }
        }
        catch (Exception ex) { o.AppendLine("Querverweise nicht verfuegbar: " + ex.Message + " - nur Textsuche:"); }
        var rest = hits.Where(h => !used.Contains(h)).Take(max).ToList();
        if (rest.Count > 0)
        {
            o.AppendLine($"\nNur in der Textsuche ({rest.Count}, z. B. Kommentare, gleichnamige andere Symbole oder nicht kompilierter Code):");
            foreach (var h in rest) o.AppendLine($"  {h.Path}  [{h.Part} Zeile {h.Line}]  {h.Text.Trim()}");
        }
        return o.ToString();
    }

    bool DryRun(JsonObject a) => policy.ForceDryRun || Bool(a, "dryRun", false);

    static string PlcDiff(ITcSmTreeItem item, string decl, string impl)
    {
        var o = new StringBuilder($"VORSCHLAG fuer {item.PathName} (nichts geschrieben):\n");
        if (decl != null)
        {
            if (item is not ITcPlcDeclaration d) throw new Exception("Objekt hat keinen Deklarationsteil");
            o.Append(LineDiff.Unified(d.DeclarationText, decl, "Deklaration"));
        }
        if (impl != null)
        {
            if (item is not ITcPlcImplementation i) throw new Exception("Objekt hat keinen Implementierungsteil");
            o.Append(LineDiff.Unified(i.ImplementationText, impl, "Implementierung"));
        }
        return o.ToString();
    }

    string PlcSearch(JsonObject a)
    {
        var pat = Str(a, "pattern");
        var opts = RegexOptions.CultureInvariant | (Bool(a, "caseSensitive", false) ? RegexOptions.None : RegexOptions.IgnoreCase);
        var rx = new Regex(Bool(a, "regex", false) ? pat : Regex.Escape(pat), opts, TimeSpan.FromSeconds(2));
        var part = (Str(a, "part") ?? "all").ToLowerInvariant();
        int max = Int(a, "max", 200), hits = 0, objects = 0;
        var root = Str(a, "path") is { Length: > 0 } p ? tc.Item(p) : null;
        var o = new StringBuilder();
        void Scan(string path, string label, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int n = 0; n < lines.Length && hits < max; n++)
                if (rx.IsMatch(lines[n])) { hits++; o.AppendLine($"{path}  [{label}:{n + 1}]  {lines[n].Trim()}"); }
        }
        foreach (var it in tc.PlcObjects(root))
        {
            if (hits >= max) break;
            string path = null;
            try { path = it.PathName; } catch { continue; }
            bool any = false;
            if (part != "impl" && it is ITcPlcDeclaration d) { any = true; try { Scan(path, "Dekl", d.DeclarationText); } catch { } }
            if (part != "decl" && it is ITcPlcImplementation i) { any = true; try { Scan(path, "Impl", i.ImplementationText); } catch { } }
            if (any) objects++;
        }
        var head = $"{hits} Treffer in {objects} durchsuchten Objekten" + (hits >= max ? $" (abgeschnitten bei max={max})" : "") + "\n";
        return head + o;
    }

    string PlcReferences(string plcPath, bool installed)
    {
        var plcs = string.IsNullOrEmpty(plcPath) ? tc.PlcProjects() : new List<ITcSmTreeItem> { tc.Item(plcPath) };
        var o = new StringBuilder();
        foreach (var plc in plcs)
            foreach (var c in TwinCatSession.Children(plc))
            {
                if (c is not ITcPlcLibraryManager lm) continue;
                o.AppendLine($"# {plc.Name}  ({c.PathName})");
                foreach (object r in (System.Collections.IEnumerable)((dynamic)lm).References) o.AppendLine("  " + DescribeLib(r));
                if (installed)
                {
                    o.AppendLine("  -- installiert im Repository --");
                    foreach (object r in (System.Collections.IEnumerable)((dynamic)lm).ScanLibraries()) o.AppendLine("  " + DescribeLib(r));
                }
            }
        return o.Length > 0 ? o.ToString() : "keine Bibliotheksreferenzen gefunden";
    }

    static string DescribeLib(object r)
    {
        dynamic d = r;
        static string T(Func<object> f) { try { return f()?.ToString() ?? ""; } catch { return null; } }
        string Lib(object lo)
        {
            if (lo == null) return "-";
            dynamic l = lo;
            return $"{T(() => l.Name)}, {T(() => l.Version)} ({T(() => l.Distributor)})";
        }
        var ph = T(() => d.PlaceholderName);
        if (!string.IsNullOrEmpty(ph))
        {
            dynamic eff = null, def = null;
            try { eff = d.EffectiveResolution; } catch { }
            try { def = d.DefaultResolution; } catch { }
            object res = eff ?? def;
            return $"Platzhalter {ph} -> {Lib(res)}";
        }
        return Lib(r);
    }

    string PlcCreate(JsonObject a)
    {
        var kind = Str(a, "kind");
        if (!PlcKinds.TryGetValue(kind, out var sub)) throw new Exception("Unbekannter kind: " + kind);
        var impls = (Str(a, "implements") ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (impls.Count > 0 && sub != 604) throw new Exception("implements gibt es nur bei kind=function_block");
        var name = Str(a, "name");
        var decl = Str(a, "declaration");
        if (impls.Count > 0 && decl == null)
            decl = $"FUNCTION_BLOCK {name} IMPLEMENTS {string.Join(", ", impls)}\nVAR_INPUT\nEND_VAR\nVAR_OUTPUT\nEND_VAR\nVAR\nEND_VAR\n";
        // Interface-Mitglieder vorab ermitteln (auch fuer dryRun)
        var members = new List<(string Iface, LibApi.Member M)>();
        var missing = new List<string>();
        foreach (var i in impls)
        {
            var ms = InterfaceMembersFor(i);
            if (ms == null) missing.Add(i); else members.AddRange(ms.Select(m => (i, m)));
        }
        if (missing.Count > 0)
            throw new Exception($"Interface nicht gefunden: {string.Join(", ", missing)}. Projekt-Interfaces werden direkt gelesen; bei Bibliotheks-" +
                                "Interfaces einmal tc_lib_signatures library=<Bibliothek> aufrufen (fuellt den Zwischenspeicher), dann erneut.");
        if (DryRun(a))
            return $"VORSCHLAG (nichts angelegt): {kind} '{name}' unter {tc.Item(Str(a, "parentPath")).PathName}" +
                   (decl is { } dd ? "\n--- Deklaration ---\n" + dd : "") +
                   (Str(a, "implementation") is { } ii ? "\n--- Implementierung ---\n" + ii : "") +
                   (members.Count > 0 ? "\n--- Stubs aus " + string.Join(", ", impls) + " ---\n" +
                                        string.Join("\n", members.Select(m => m.M.IsProperty ? $"PROPERTY {m.M.Name} : {m.M.ReturnType} (Get/Set)" : m.M.Declaration.Split('\n').First(ln => !ln.StartsWith("///")))) : "");
        var lang = Languages.TryGetValue(Str(a, "language") ?? "ST", out var l) ? l : 1;
        var ret = Str(a, "returnType");
        object vInfo = sub switch
        {
            602 or 604 or 608 or 613 or 614 => lang,
            603 or 609 or 610 => new object[] { lang, ret ?? "BOOL" },
            611 or 612 => ret ?? "BOOL",
            _ => null
        };
        var parent = tc.Item(Str(a, "parentPath"));
        var item = (ITcSmTreeItem)((dynamic)parent).CreateChild(name, sub, "", vInfo);
        string w;
        if (sub is 611 or 612)
        {
            // Properties brauchen Get/Set-Accessoren - CreateChild legt sie nicht von selbst an
            w = PlcWrite(item, decl, null);
            var acc = EnsureAccessors(item, lang, Str(a, "getImplementation") ?? Str(a, "implementation"), Str(a, "setImplementation"));
            w += "; " + acc;
        }
        else w = PlcWrite(item, decl, Str(a, "implementation"));
        var o = new StringBuilder($"angelegt: {item.PathName}; {w}");
        // Stubs fuer die Interface-Mitglieder
        var existing = TwinCatSession.Children(item).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (iface, m) in members)
        {
            if (existing.Contains(m.Name)) { o.Append($"\n  {m.Name}: schon vorhanden"); continue; }
            try
            {
                if (m.IsProperty)
                {
                    var pi = (ITcSmTreeItem)((dynamic)item).CreateChild(m.Name, 611, "", m.ReturnType ?? "BOOL");
                    o.Append($"\n  PROPERTY {m.Name} : {m.ReturnType} ({iface}) - {EnsureAccessors(pi, lang, null, null)}");
                }
                else
                {
                    var mi = (ITcSmTreeItem)((dynamic)item).CreateChild(m.Name, 609, "", new object[] { lang, m.ReturnType ?? "BOOL" });
                    PlcWrite(mi, m.Declaration + (m.Declaration.Contains("\nVAR\n") ? "" : "\nVAR\nEND_VAR"), "");
                    o.Append($"\n  METHOD {m.Name}{(m.ReturnType != null ? " : " + m.ReturnType : "")} ({iface})");
                }
            }
            catch (Exception ex) { o.Append($"\n  {m.Name}: FEHLER {ex.Message}"); }
        }
        if (members.Count > 0) o.Append("\nStubs sind leer - Implementierung mit tc_plc_write ergaenzen, dann tc_check.");
        return o.ToString();
    }

    // Get/Set unter einer Property sicherstellen (TwinCAT-Subtypen 613/614)
    static string EnsureAccessors(ITcSmTreeItem prop, int lang, string getImpl, string setImpl)
    {
        var notes = new List<string>();
        foreach (var (nm, st, impl) in new[] { ("Get", 613, getImpl), ("Set", 614, setImpl) })
        {
            var child = TwinCatSession.Children(prop).FirstOrDefault(c => c.Name.Equals(nm, StringComparison.OrdinalIgnoreCase));
            if (child == null)
            {
                Exception last = null;
                foreach (var vi in new object[] { lang, null })
                {
                    try { child = (ITcSmTreeItem)((dynamic)prop).CreateChild(nm, st, "", vi); break; }
                    catch (Exception ex) { last = ex; }
                }
                if (child == null) { notes.Add($"{nm} NICHT anlegbar ({last?.Message})"); continue; }
                notes.Add(nm + " angelegt");
            }
            if (impl != null)
            {
                if (child is ITcPlcImplementation ci) { ci.ImplementationText = impl; notes.Add(nm + "-Code geschrieben"); }
                else notes.Add(nm + " hat keinen Implementierungsteil");
            }
        }
        return string.Join(", ", notes);
    }

    // Mitglieder eines Interfaces: zuerst im Projekt, dann in den zwischengespeicherten Bibliotheksschnittstellen
    List<LibApi.Member> InterfaceMembersFor(string iface)
    {
        var list = new List<LibApi.Member>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (CollectProjectInterface(iface, list, done)) return list;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "libcache");
        if (!Directory.Exists(dir)) return null;
        foreach (var f in Directory.GetFiles(dir, "*.api.json").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try { if (LibApi.Load(f).InterfaceMembers(iface) is { } ms) return ms; } catch { }
        }
        return null;
    }

    bool CollectProjectInterface(string iface, List<LibApi.Member> list, HashSet<string> done)
    {
        if (!done.Add(iface)) return true;
        ITcSmTreeItem it = null;
        foreach (var (nm, pth) in tc.PlcIndex())
        {
            if (!nm.Equals(iface, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var x = tc.Item(pth);
                if (x is ITcPlcDeclaration xd &&
                    Regex.IsMatch(xd.DeclarationText ?? "", @"^\s*(//[^\n]*\n|\(\*[\s\S]*?\*\)\s*)*\s*INTERFACE\b", RegexOptions.IgnoreCase)) { it = x; break; }
            }
            catch { }
        }
        if (it == null) return false;
        var d = ((ITcPlcDeclaration)it).DeclarationText ?? "";
        foreach (var c in TwinCatSession.Children(it))
        {
            var cd = (c as ITcPlcDeclaration)?.DeclarationText ?? "";
            if (Regex.IsMatch(cd, @"\bPROPERTY\b", RegexOptions.IgnoreCase))
            {
                var m = Regex.Match(cd, @"PROPERTY\s+(?:PUBLIC\s+|PRIVATE\s+|PROTECTED\s+|INTERNAL\s+)?\w+\s*:\s*([^\s;/(]+)", RegexOptions.IgnoreCase);
                if (!list.Any(x => x.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new LibApi.Member(c.Name, true, m.Success ? m.Groups[1].Value : "BOOL", cd));
            }
            else if (Regex.IsMatch(cd, @"\bMETHOD\b", RegexOptions.IgnoreCase) && !list.Any(x => x.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
            {
                var m = Regex.Match(cd, @"METHOD\s+(?:PUBLIC\s+|PRIVATE\s+|PROTECTED\s+|INTERNAL\s+)?\w+\s*:\s*([^\s;/(]+)", RegexOptions.IgnoreCase);
                list.Add(new LibApi.Member(c.Name, false, m.Success ? m.Groups[1].Value : null, cd.TrimEnd()));
            }
        }
        // EXTENDS weiterer Interfaces (Projekt oder Bibliothek)
        var ext = Regex.Match(d, @"INTERFACE\s+\w+\s+EXTENDS\s+([\w\s,.]+)", RegexOptions.IgnoreCase);
        if (ext.Success)
            foreach (var e in ext.Groups[1].Value.Split(',').Select(x => x.Trim().Split('.').Last()).Where(x => x.Length > 0))
                if (!CollectProjectInterface(e, list, done) && InterfaceMembersFor(e) is { } lm)
                    foreach (var x in lm) if (!list.Any(y => y.Name.Equals(x.Name, StringComparison.OrdinalIgnoreCase))) list.Add(x);
        return true;
    }

    // ------------------------------------------------------------------ I/O anlegen
    const int IoEtherCatMaster = 111, IoBox = 9099;

    string IoAdd(JsonObject a)
    {
        var kind = (Str(a, "kind") ?? "").ToLowerInvariant();
        var parentPath = Str(a, "parentPath") ?? "TIID";
        var product = Str(a, "product");
        var kids = (Str(a, "children") ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        int sub = kind switch
        {
            "ethercat_master" => IoEtherCatMaster,
            "box" or "terminal" or "coupler" => IoBox,
            "raw" => Int(a, "subType", -1),
            _ => throw new Exception("kind: ethercat_master, box oder raw")
        };
        if (sub < 0) throw new Exception("raw braucht subType");
        if (sub == IoBox && string.IsNullOrEmpty(product)) throw new Exception("box braucht product, z. B. EK1100");
        var parent = tc.Item(parentPath);
        if (DryRun(a))
            return $"VORSCHLAG (nichts angelegt): unter {parent.PathName}: {(sub == IoBox ? product : kind)}" +
                   (kids.Count > 0 ? "\n  darunter: " + string.Join(", ", kids) : "");
        var o = new StringBuilder();
        var item = CreateIo(parent, sub, product, Str(a, "name"));
        o.Append("angelegt: ").Append(item.PathName);
        foreach (var k in kids)
        {
            try { o.Append("\n  + ").Append(CreateIo(item, IoBox, k, null).PathName); }
            catch (Exception ex) { o.Append($"\n  {k}: FEHLER {ex.InnerException?.Message ?? ex.Message}"); }
        }
        o.Append("\nUnterelemente/Prozessdaten mit tc_tree pruefen; Parameter mit tc_xml_get/tc_xml_set.");
        if (sub == IoEtherCatMaster) o.Append(" Netzwerkadapter des Masters ist noch nicht zugewiesen (in der IDE: Adapter > Search, oder per tc_xml_set).");
        return o.ToString();
    }

    // Name nach TwinCAT-Art: Geraete "Device n (EtherCAT)", Boxen "Term n (EL1008)" mit laufender Nummer im ganzen Geraet
    ITcSmTreeItem CreateIo(ITcSmTreeItem parent, int sub, string product, string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            if (sub == IoEtherCatMaster)
                name = $"Device {TwinCatSession.Children(parent).Count() + 1} (EtherCAT)";
            else
            {
                // zum Geraet (TIID^Device n ...) gehen und alle Boxen darunter zaehlen
                var segs = parent.PathName.Split('^');
                var dev = segs.Length >= 2 ? tc.Item(segs[0] + "^" + segs[1]) : parent;
                int n = Descendants(dev).Count(x => Regex.IsMatch(x.Name, @"^(Term|Box) \d+ \(")) + 1;
                var shortName = product.Split('-')[0].ToUpperInvariant();
                name = $"{(shortName.StartsWith("EP") || shortName.StartsWith("EQ") ? "Box" : "Term")} {n} ({shortName})";
            }
        }
        object vInfo = sub == IoBox ? product : null;
        try { return (ITcSmTreeItem)((dynamic)parent).CreateChild(name, sub, "", vInfo); }
        catch (Exception ex) when (sub == IoBox && !product.Contains('-'))
        {
            throw new Exception($"{product} nicht anlegbar ({ex.InnerException?.Message ?? ex.Message}). Ggf. mit Revision angeben, " +
                                $"z. B. {product}-0000-0017, und pruefen, ob die ESI-Beschreibung in TwinCAT installiert ist.");
        }
    }

    static IEnumerable<ITcSmTreeItem> Descendants(ITcSmTreeItem it)
    {
        foreach (var c in TwinCatSession.Children(it)) { yield return c; foreach (var d in Descendants(c)) yield return d; }
    }

    // ------------------------------------------------------------------ Verknuepfen
    string Link(JsonObject a)
    {
        var list = new List<(string S, string T)>();
        if (!string.IsNullOrEmpty(Str(a, "source")) && !string.IsNullOrEmpty(Str(a, "target"))) list.Add((Str(a, "source").Trim(), Str(a, "target").Trim()));
        foreach (var line in (Str(a, "pairs") ?? "").Split('\n'))
        {
            var l = line.Trim();
            if (l.Length == 0) continue;
            var parts = l.Split(new[] { "=>", "<=>", "->" }, StringSplitOptions.None);
            if (parts.Length != 2) throw new Exception("Zeile nicht im Format '<pfad> => <pfad>': " + l);
            list.Add((parts[0].Trim(), parts[1].Trim()));
        }
        if (list.Count == 0) throw new Exception("source+target oder pairs angeben");
        var sm = (dynamic)tc.RequireSysMan();
        var o = new StringBuilder();
        int ok = 0;
        foreach (var (src, dst) in list)
        {
            try
            {
                // Existenz pruefen - gibt klare Fehlermeldung statt eines COM-Fehlers
                foreach (var p in new[] { src, dst })
                    try { tc.Item(p); } catch { throw new Exception("Pfad nicht gefunden: " + p); }
                if (!DryRun(a)) sm.LinkVariables(src, dst);
                ok++;
                o.AppendLine($"{(DryRun(a) ? "OK (dryRun)" : "verknuepft")}: {src} <=> {dst}");
            }
            catch (Exception ex) { o.AppendLine($"FEHLER: {src} <=> {dst}: {ex.InnerException?.Message ?? ex.Message}"); }
        }
        return $"{ok}/{list.Count} {(DryRun(a) ? "pruefbar" : "verknuepft")}\n" + o;
    }

    // Blaetter unter einem Element = Variablen/Kanaele; Typ und Groesse aus ProduceXml, soweit vorhanden
    string IoVars(string path, string filter, int max)
    {
        var root = tc.Item(path);
        var o = new StringBuilder();
        int n = 0;
        foreach (var it in Descendants(root).Prepend(root))
        {
            if (TwinCatSession.Children(it).Any()) continue;   // nur Blaetter
            var p = it.PathName;
            if (!string.IsNullOrEmpty(filter) && p.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (++n > max) { o.AppendLine($"... (abgeschnitten nach {max} - filter nutzen)"); break; }
            string type = "";
            try
            {
                var x = (string)((dynamic)it).ProduceXml(false);
                var m = Regex.Match(x, @"<VarType[^>]*>([^<]+)</VarType>");
                if (m.Success) type = m.Groups[1].Value;
                if (Regex.IsMatch(x, @"<Link[s ]|<LinkInfo", RegexOptions.IgnoreCase)) type += " [verknuepft?]";
            }
            catch { }
            o.Append(p);
            if (type.Length > 0) o.Append("  : ").Append(type);
            o.AppendLine();
        }
        return n == 0 ? $"Keine Variablen unter {root.PathName} gefunden (PLC-Prozessabbild erscheint erst nach tc_build)." : o.ToString();
    }

    // ------------------------------------------------------------------ Tasks
    string Task(JsonObject a)
    {
        var action = (Str(a, "action") ?? "list").ToLowerInvariant();
        var rt = tc.Item("TIRT");
        if (action == "list")
        {
            var o = new StringBuilder();
            foreach (var t in TwinCatSession.Children(rt))
            {
                string x = ""; try { x = (string)((dynamic)t).ProduceXml(false); } catch { }
                string V(string tag) { var m = Regex.Match(x, $@"<{tag}[^>]*>([^<]*)</{tag}>", RegexOptions.IgnoreCase); return m.Success ? m.Groups[1].Value : "?"; }
                var ct = V("CycleTime");
                var us = long.TryParse(ct, out var ticks) ? $"{ticks / 10.0:0.###} us" : ct;
                o.AppendLine($"{t.PathName}  Zyklus {us}, Prioritaet {V("Priority")}, AutoStart {V("AutoStart")}");
            }
            return o.Length == 0 ? "Keine Tasks unter TIRT." : o.ToString();
        }
        ITcSmTreeItem task;
        if (action == "create")
        {
            var name = Str(a, "name") ?? throw new Exception("name fehlt");
            if (DryRun(a)) return $"VORSCHLAG: Task '{name}' unter TIRT ({(Bool(a, "withImage", false) ? "mit" : "ohne")} Prozessabbild)";
            task = (ITcSmTreeItem)((dynamic)rt).CreateChild(name, Bool(a, "withImage", false) ? 0 : 1, null, null);
        }
        else if (action == "set") task = tc.Item(Str(a, "path") ?? throw new Exception("path fehlt"));
        else throw new Exception("action: list, create oder set");
        var res = $"{(action == "create" ? "angelegt" : "Task")}: {task.PathName}";
        // Werte im eigenen XML der Task ersetzen - so stimmen die Tag-Namen garantiert
        var changes = new List<(string Tag, string Val)>();
        if (a["cycleUs"] != null) changes.Add(("CycleTime", (Int(a, "cycleUs", 1000) * 10L).ToString()));
        if (a["priority"] != null) changes.Add(("Priority", Int(a, "priority", 20).ToString()));
        if (a["autoStart"] != null) changes.Add(("AutoStart", Bool(a, "autoStart", true) ? "true" : "false"));
        if (changes.Count == 0) return res;
        var xml = (string)((dynamic)task).ProduceXml(false);
        var notes = new List<string>();
        foreach (var (tag, val) in changes)
        {
            var re = new Regex($@"(<{tag}(\s[^>]*)?>)([^<]*)(</{tag}>)", RegexOptions.IgnoreCase);
            if (!re.IsMatch(xml)) { notes.Add($"{tag}: nicht im Task-XML gefunden"); continue; }
            xml = re.Replace(xml, m => m.Groups[1].Value + val + m.Groups[4].Value, 1);
            notes.Add($"{tag}={val}");
        }
        if (DryRun(a)) return res + " - VORSCHLAG: " + string.Join(", ", notes);
        ((dynamic)task).ConsumeXml(xml);
        return res + " - gesetzt: " + string.Join(", ", notes) + " (CycleTime in 100-ns-Schritten). Wirkt nach tc_activate.";
    }

    // ------------------------------------------------------------------ Bootprojekt
    string BootProject(JsonObject a)
    {
        ITcSmTreeItem plc = !string.IsNullOrEmpty(Str(a, "plcPath")) ? tc.Item(Str(a, "plcPath"))
                          : TwinCatSession.Children(tc.Item("TIPC")).FirstOrDefault() ?? throw new Exception("Kein PLC-Projekt unter TIPC");
        dynamic d = plc;
        string Now() { try { return ((bool)d.BootProjectAutostart) ? "an" : "aus"; } catch (Exception ex) { return "nicht lesbar (" + ex.Message + ")"; } }
        var o = new StringBuilder($"{plc.PathName}: Autostart Bootprojekt {Now()}");
        if (DryRun(a)) return o + (a["autostart"] != null ? $"\nVORSCHLAG: Autostart {(Bool(a, "autostart", true) ? "an" : "aus")}" : "") +
                              (Bool(a, "generate", false) ? "\nVORSCHLAG: Bootprojekt erzeugen" : "");
        if (a["autostart"] != null)
        {
            try { d.BootProjectAutostart = Bool(a, "autostart", true); o.Append($" -> jetzt {Now()}"); }
            catch (Exception ex) { o.Append($"\nAutostart nicht setzbar: {ex.InnerException?.Message ?? ex.Message}"); }
        }
        if (Bool(a, "generate", false))
        {
            try { d.GenerateBootProject(true); o.Append("\nBootprojekt erzeugt."); }
            catch (Exception ex) { o.Append($"\nBootprojekt nicht erzeugt: {ex.InnerException?.Message ?? ex.Message}"); }
        }
        return o.ToString();
    }

    // ------------------------------------------------------------------ Startmodus der Runtime (lokal, Registry)
    static string RuntimeStartup(string mode)
    {
        const string key = @"SOFTWARE\WOW6432Node\Beckhoff\TwinCAT3\System";
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key, writable: !string.IsNullOrEmpty(mode));
        if (k == null) return "TwinCAT-Registry nicht gefunden (HKLM\\" + key + ").";
        string Show(object v) => v is int i ? (i == 5 ? "run" : i == 15 ? "config" : $"Wert {i}") : "nicht gesetzt";
        var cur = Show(k.GetValue("SysStartupState"));
        if (string.IsNullOrEmpty(mode)) return $"Startmodus lokal: {cur}";
        int val = mode.Equals("run", StringComparison.OrdinalIgnoreCase) ? 5 : mode.Equals("config", StringComparison.OrdinalIgnoreCase) ? 15
                : throw new Exception("mode: run oder config");
        k.SetValue("SysStartupState", val, Microsoft.Win32.RegistryValueKind.DWord);
        return $"Startmodus lokal: {cur} -> {Show(val)} (gilt ab dem naechsten Hochfahren)";
    }

    // ------------------------------------------------------------------ Lizenzen
    string Licenses()
    {
        // Der Lizenz-Knoten haengt unter SYSTEM; Namen je nach Sprache/Version
        foreach (var p in new[] { "TIRC^License", "TIRC^Lizenz", "TIRC^Licence" })
            try { var it = tc.Item(p); return it.PathName + "\n" + (string)((dynamic)it).ProduceXml(false); } catch { }
        foreach (var root in new[] { "TIRC", "TIRS" })
            try
            {
                foreach (var c in TwinCatSession.Children(tc.Item(root)))
                    if (Regex.IsMatch(c.Name, "Lizen|Licen", RegexOptions.IgnoreCase))
                        return c.PathName + "\n" + (string)((dynamic)c).ProduceXml(false);
            }
            catch { }
        return "Lizenz-Knoten nicht gefunden - mit tc_tree path=TIRC suchen und tc_feedback melden.";
    }

    // ------------------------------------------------------------------ ADS-Helfer
    static AdsClient Connect(JsonObject a)
    {
        var c = new AdsClient();
        var net = Str(a, "netId");
        c.Connect(string.IsNullOrEmpty(net) ? AmsNetId.Local : AmsNetId.Parse(net), Int(a, "port", 851));
        return c;
    }

    static string Ads(JsonObject a, Func<IValueSymbol, string> f)
    {
        using var c = Connect(a);
        var loader = SymbolLoaderFactory.Create(c, new SymbolLoaderSettings(SymbolsLoadMode.VirtualTree));
        if (!loader.Symbols.TryGetInstance(Str(a, "symbol"), out var s) || s is not IValueSymbol vs)
            throw new Exception("Symbol nicht gefunden: " + Str(a, "symbol"));
        return f(vs);
    }

    static string Format(object v) => v switch
    {
        null => "null",
        System.Collections.IEnumerable e and not string => "[" + string.Join(", ", e.Cast<object>().Select(Format)) + "]",
        IFormattable fm => fm.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString()
    };

    // ------------------------------------------------------------------ Registry / Schema
    void Add(string name, string desc, JsonObject schema, Func<JsonObject, string> h) => tools.Add(new Tool(name, desc, schema, h));

    // Zielsystem eines Laufzeit-Eingriffs: explizite netId oder aktuelles Ziel der Solution
    string TargetOf(string tool, JsonObject a) => tool switch
    {
        "ads_write" => Str(a, "netId"),
        "tc_activate" when !string.IsNullOrEmpty(Str(a, "netId")) => Str(a, "netId"),
        "tc_runtime_mode" when !string.IsNullOrEmpty(Str(a, "netId")) => Str(a, "netId"),
        "tc_activate" or "tc_plc_online" or "tc_runtime_mode" => tc.Sta.Run(() => (string)((dynamic)tc.RequireSysMan()).GetTargetNetId()),
        _ => null
    };

    public JsonArray List() => new(tools.Select(t => (JsonNode)new JsonObject
    {
        ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = t.Schema.DeepClone()
    }).ToArray());

    bool rulesSeen;

    string SolutionDir()
    {
        try { return tc.Sta.Run(() => Path.GetDirectoryName((string)tc.RequireDte().Solution.FullName)); } catch { return null; }
    }

    public JsonObject Call(string name, JsonObject args)
    {
        var t = tools.FirstOrDefault(x => x.Name == name) ?? throw new Exception("Unbekanntes Tool: " + name);
        // Sicherheitsnetz: gibt es Codierrichtlinien und hat der Agent sie noch nicht gelesen, wird der erste schreibende
        // Code-Aufruf nicht ausgefuehrt, sondern die Richtlinien zurueckgegeben (einmal je Sitzung).
        if (!rulesSeen && (name is "tc_plc_write" or "tc_plc_create") && !DryRun(args)
            && Environment.GetEnvironmentVariable("TCAGENT_RULES_GATE") != "0")
        {
            rulesSeen = true;
            if (CodingRules.Text(SolutionDir()) is { } rt)
                return new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] =
                        "NICHT ausgefuehrt: Fuer dieses Projekt gelten Codierrichtlinien, die noch nicht gelesen wurden. " +
                        "Code daran ausrichten und den Aufruf dann wiederholen (naechstes Mal vorher tc_coding_rules aufrufen).\n\n" + rt }),
                    ["isError"] = true
                };
        }
        string text; bool isError = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Fuer die Haenger-Diagnose der Bruecke: welches Werkzeug laeuft gerade (eine Datei je TcAgent-Prozess)
        var busy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", $"busy.{Environment.ProcessId}.txt");
        try { File.WriteAllText(busy, name); } catch { }
        try
        {
            policy.Check(name, () => TargetOf(name, args));
            if (Confirm.Needed(name, args))
            {
                string target = null;
                try { target = TargetOf(name, args); } catch { }
                var what = Confirm.Describe(name, args, target);
                var ok = Confirm.Ask(name, what, tc.AttachedPid);
                if (ok == false) throw new OperationCanceledException("Vom Benutzer abgelehnt: " + what + " - nicht erneut versuchen, ohne nachzufragen.");
                if (ok == null) Log.Info("Keine Rueckfrage moeglich (kein Kanal) - " + what);
            }
            text = RunWatchingDialogs(name, () => t.Handler(args));
        }
        catch (Exception ex) { text = "Fehler: " + (ex.InnerException?.Message ?? ex.Message); isError = true; Log.Error($"{name}: {ex}"); }
        finally { try { File.Delete(busy); } catch { } }
        if (Policy.ClassOf(name) != ToolClass.Read || isError) policy.Audit(name, args, !isError, text, sw.ElapsedMilliseconds);
        if (Policy.ClassOf(name) != ToolClass.Read && !isError) tc.InvalidatePlcIndex();
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError
        };
    }

    /// <summary>Fuehrt ein Werkzeug aus; oeffnet die IDE dabei einen modalen Dialog, wartet jeder Automation-Interface-Aufruf
    /// bis zu dessen Schliessen. Dann sofort mit dem Dialogtext zurueckkehren, statt minutenlang zu haengen
    /// (der Aufruf selbst laeuft weiter und wird fertig, sobald der Dialog zu ist).</summary>
    string RunWatchingDialogs(string name, Func<string> handler)
    {
        if (name == "tc_dialogs") return handler();
        var task = System.Threading.Tasks.Task.Run(handler);
        int seen = 0;
        while (!task.Wait(1000))
        {
            List<IdeDialogs.Dialog> dialogs;
            try { dialogs = IdeDialogs.Find(tc.AttachedPid); } catch { dialogs = new(); }
            seen = dialogs.Count > 0 ? seen + 1 : 0;
            if (seen < 3) continue;
            task.ContinueWith(x => Log.Info($"{name} nach Dialog beendet: " + (x.IsFaulted ? x.Exception?.InnerException?.Message : "ok")));
            throw new Exception("Die IDE zeigt einen Dialog und wartet auf eine Eingabe - " + name + " wird erst danach fertig:\n  " +
                                string.Join("\n  ", dialogs) +
                                "\nDen Benutzer bitten, den Dialog zu lesen und zu schliessen (oder tc_dialogs action=close, das entspricht Abbrechen/X). " +
                                "Weitere TwinCAT-Aufrufe warten bis dahin.");
        }
        return task.GetAwaiter().GetResult();
    }

    static (string name, JsonObject def, bool req) P(string name, string type, string desc) =>
        (name, new JsonObject { ["type"] = type, ["description"] = desc }, false);
    static (string, JsonObject, bool) Req((string n, JsonObject d, bool r) p) => (p.n, p.d, true);

    static JsonObject Schema(params (string name, JsonObject def, bool req)[] props)
    {
        var p = new JsonObject();
        var r = new JsonArray();
        foreach (var x in props) { p[x.name] = x.def; if (x.req) r.Add(x.name); }
        return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = r };
    }

    static string Str(JsonObject a, string k) => a[k] is JsonValue v ? v.ToString() : null;
    static int Int(JsonObject a, string k, int d) => a[k] is JsonValue v && int.TryParse(v.ToString(), out var i) ? i : d;
    static bool Bool(JsonObject a, string k, bool d) => a[k] is JsonValue v && bool.TryParse(v.ToString(), out var b) ? b : d;
}
