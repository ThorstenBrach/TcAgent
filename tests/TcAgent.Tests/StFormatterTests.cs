using TcAgent;
using Xunit;

public class StFormatterTests
{
    static string N(string s) => s.Replace("\r\n", "\n");
    static StFormatResult F(string s, StPart part = StPart.Auto, StFormatConfig cfg = null) => StFormatter.Format(s, cfg ?? new StFormatConfig(), part);

    [Fact]
    public void LexerIsLossless()
    {
        var src = "IF a(* x (* nested *) *)THEN b := 'it$'s {x} $$'; // c\r\n  c := T#1s500ms + INT#5 + 16#FF + 1.5E3; {attribute 'x' := '}'}\r\nEND_IF\n%IX0.1 x S= y; p REF= q;";
        Assert.Equal(src, StLexer.Join(StLexer.Lex(src)));
    }

    [Fact]
    public void UnclosedCommentIsNotFormatted()
    {
        var r = F("IF a THEN (* open\nb := 1;\nEND_IF");
        Assert.NotNull(r.Error);
        Assert.False(r.Changed);
    }

    [Fact]
    public void IndentsBlocksWithTwoSpacesAndUppercasesKeywords()
    {
        var r = F("if a then\nb:=1;\nelsif c then\nfor i:=1 to 10 do\nx[i]:=-i;\nend_for\nelse\nwhile not d do\nd:=true;\nend_while\nend_if\n", StPart.Implementation);
        Assert.Null(r.Error);
        Assert.Equal("IF a THEN\n  b := 1;\nELSIF c THEN\n  FOR i := 1 TO 10 DO\n    x[i] := -i;\n  END_FOR\nELSE\n  WHILE NOT d DO\n    d := TRUE;\n  END_WHILE\nEND_IF\n", N(r.Text));
    }

    [Fact]
    public void ThenOnItsOwnLineStaysAtTheLevelOfIf()
    {
        var r = F("IF NOT bEnable\nTHEN\nbPhaseOff := FALSE;\nEND_IF", StPart.Implementation);
        Assert.Equal("IF NOT bEnable\nTHEN\n  bPhaseOff := FALSE;\nEND_IF", N(r.Text));
    }

    [Fact]
    public void CaseLabelsAndStatements()
    {
        var r = F("CASE eState OF\nE_State.Idle:\nx := 1;\nE_State.Run, E_State.Wait:\ny := 2;\nELSE\nz := 3;\nEND_CASE", StPart.Implementation);
        Assert.Equal("CASE eState OF\n  E_State.Idle:\n    x := 1;\n  E_State.Run, E_State.Wait:\n    y := 2;\n  ELSE\n    z := 3;\nEND_CASE", N(r.Text));
    }

    [Fact]
    public void DeclarationsAreAlignedInColumns()
    {
        var r = F("FUNCTION_BLOCK FB_X\nVAR\nbStart:BOOL; // start\nnCount : INT:=0; // count\nfbTimer   :   TON;\nEND_VAR", StPart.Declaration);
        Assert.Null(r.Error);
        Assert.Equal("FUNCTION_BLOCK FB_X\nVAR\n  bStart  : BOOL;     // start\n  nCount  : INT := 0; // count\n  fbTimer : TON;\nEND_VAR", N(r.Text));
    }

    [Fact]
    public void CommentLinesBreakAlignmentGroupsAndTakeTheIndentOfTheNextLine()
    {
        var r = F("VAR_INPUT\n/// lamp\nbEnable : BOOL;\n/// on time\ntOn : TIME := T#500MS;\nEND_VAR", StPart.Declaration);
        Assert.Equal("VAR_INPUT\n  /// lamp\n  bEnable : BOOL;\n  /// on time\n  tOn : TIME := T#500MS;\nEND_VAR", N(r.Text));
    }

    [Fact]
    public void BracketsMakeThePrecedenceVisible()
    {
        var r = F("bRelease := NOT bRunning AND bDoorOk OR bSetup AND bKeySwitch;", StPart.Implementation);
        Assert.Null(r.Error);
        Assert.Equal("bRelease := (NOT bRunning AND bDoorOk) OR (bSetup AND bKeySwitch);", r.Text);
    }

    [Fact]
    public void BracketsAlsoInConditionsAndCallArguments()
    {
        var r = F("IF a AND b OR c THEN\n  fb(IN := x AND y OR z);\nEND_IF", StPart.Implementation);
        Assert.Equal("IF (a AND b) OR c THEN\n  fb(IN := (x AND y) OR z);\nEND_IF", N(r.Text));
    }

    [Fact]
    public void ExistingBracketsAndPureChainsStay()
    {
        var src = "x := (a AND b) OR c;\ny := a AND b AND c;\nz := a OR b OR c;";
        Assert.Equal(src, F(src, StPart.Implementation).Text);
    }

    [Fact]
    public void SwitchedOffSectionsStayAsTheyAre()
    {
        var src = "// tcagent-format: off\nif   a then x:=1; end_if\n// tcagent-format: on\nif b then\ny:=2;\nend_if";
        var r = F(src, StPart.Implementation);
        Assert.Equal("// tcagent-format: off\nif   a then x:=1; end_if\n// tcagent-format: on\nIF b THEN\n  y := 2;\nEND_IF", N(r.Text));
        var pragma = "{attribute 'tcagent.format' := 'off'}\nx:=1;\n{attribute 'tcagent.format' := 'on'}\ny:=2;";
        Assert.Equal("{attribute 'tcagent.format' := 'off'}\nx:=1;\n{attribute 'tcagent.format' := 'on'}\ny := 2;", N(F(pragma, StPart.Implementation).Text));
        var whole = "// tcagent-format: file\nx:=1;";
        Assert.Equal(whole, F(whole, StPart.Implementation).Text);
    }

    [Fact]
    public void LongCallsGetOneParameterPerLine()
    {
        var cfg = new StFormatConfig();
        cfg.Set("wrap.max-length", "40", "test");
        var r = F("fbTimer(IN := bEnable AND NOT bPhaseOff, PT := tOnTime);", StPart.Implementation, cfg);
        Assert.Equal("fbTimer(\n  IN := bEnable AND NOT bPhaseOff,\n  PT := tOnTime\n);", N(r.Text));
        Assert.Equal(r.Text, F(r.Text, StPart.Implementation, cfg).Text);
    }

    [Fact]
    public void BlankLinesAreLimited()
    {
        var r = F("\n\nx := 1;\n\n\n\ny := 2;\n\n", StPart.Implementation);
        Assert.Equal("x := 1;\n\ny := 2;\n", N(r.Text));
    }

    [Fact]
    public void LineEndingsStay()
    {
        var r = F("IF a THEN\r\nb := 1;\r\nEND_IF\r\n", StPart.Implementation);
        Assert.Equal("IF a THEN\r\n  b := 1;\r\nEND_IF\r\n", r.Text);
    }

    [Fact]
    public void ContinuationLinesKeepTheirOffset()
    {
        var src = "IF a\n   AND b\nTHEN\nx := 1;\nEND_IF";
        Assert.Equal("IF a\n   AND b\nTHEN\n  x := 1;\nEND_IF", N(F(src, StPart.Implementation).Text));
    }

    [Fact]
    public void TabsAndPreserveCanBeChosen()
    {
        var cfg = new StFormatConfig();
        cfg.Set("indent.style", "tabs", "test");
        cfg.Set("case.keywords", "preserve", "test");
        var r = F("if a then\nb := 1;\nend_if", StPart.Implementation, cfg);
        Assert.Equal("if a then\n\tb := 1;\nend_if", N(r.Text));
    }

    [Fact]
    public void MembersNamedLikeKeywordsAndIdentifiersStay()
    {
        var r = F("x := fb.Done AND st.Time > T#1s;\nfbTimer(IN := bIn, PT := t);", StPart.Implementation);
        Assert.Equal("x := fb.Done AND st.Time > T#1s;\nfbTimer(IN := bIn, PT := t);", N(r.Text));
    }

    [Fact]
    public void IsIdempotentOnAProgram()
    {
        var src = @"// Disabled: start the next blink period with the on phase
IF NOT bEnable
THEN
	bPhaseOff := FALSE;
END_IF

// Timers are called every cycle, the phase is in IN
tOnTimer(IN := (bEnable AND NOT bPhaseOff), PT := tOn);
tOffTimer(IN := (bEnable AND bPhaseOff), PT := tOff);

IF tOnTimer.Q
THEN
	bPhaseOff := TRUE;
ELSIF tOffTimer.Q
THEN
	bPhaseOff := FALSE;
END_IF

bLamp := (bEnable AND NOT bPhaseOff);";
        var once = F(src, StPart.Implementation);
        Assert.Null(once.Error);
        Assert.Equal(once.Text, F(once.Text, StPart.Implementation).Text);
        Assert.Contains("\n  bPhaseOff := FALSE;", N(once.Text));
    }

    static StFormatConfig With(params string[] kv)
    {
        var c = new StFormatConfig();
        for (int i = 0; i < kv.Length; i += 2) c.Set(kv[i], kv[i + 1], "test");
        return c;
    }

    static void Stable(string text, StFormatConfig cfg) => Assert.Equal(text, F(text, StPart.Implementation, cfg).Text);

    [Fact]
    public void BlocksExpandToOneStatementPerLine()
    {
        var cfg = With("layout.blocks", "expand");
        var r = F("IF a THEN b := 1; c := 2; ELSE d := 3; END_IF", StPart.Implementation, cfg);
        Assert.Null(r.Error);
        Assert.Equal("IF a THEN\n  b := 1;\n  c := 2;\nELSE\n  d := 3;\nEND_IF", N(r.Text));
        Stable(r.Text, cfg);
    }

    [Fact]
    public void ThenOnItsOwnLineOrAtTheEnd()
    {
        var own = With("layout.then", "own-line");
        var r = F("IF a THEN\nb := 1;\nEND_IF\nWHILE x DO\ny := 1;\nEND_WHILE", StPart.Implementation, own);
        Assert.Equal("IF a\nTHEN\n  b := 1;\nEND_IF\nWHILE x\nDO\n  y := 1;\nEND_WHILE", N(r.Text));
        Stable(r.Text, own);
        var same = With("layout.then", "same-line");
        var s = F("IF a\nTHEN\nb := 1;\nEND_IF", StPart.Implementation, same);
        Assert.Equal("IF a THEN\n  b := 1;\nEND_IF", N(s.Text));
        Stable(s.Text, same);
    }

    [Fact]
    public void LongConditionsGetOneTermPerLine()
    {
        var cfg = With("wrap.max-length", "40");
        var r = F("IF bStartButton AND bDoorClosed AND NOT bEmergencyStop THEN\nx := 1;\nEND_IF", StPart.Implementation, cfg);
        Assert.Null(r.Error);
        Assert.Equal("IF bStartButton\n  AND bDoorClosed\n  AND NOT bEmergencyStop THEN\n  x := 1;\nEND_IF", N(r.Text));
        Stable(r.Text, cfg);
        var last = With("wrap.max-length", "40", "wrap.operator", "last");
        var l = F("bRelease := bStartButton AND bDoorClosed OR bSetupMode AND bKey;", StPart.Implementation, last);
        Assert.Equal("bRelease := (bStartButton AND bDoorClosed) OR\n            (bSetupMode AND bKey);", N(l.Text));
        Stable(l.Text, last);
    }

    [Fact]
    public void NamedArgumentsAndAssignmentsInColumns()
    {
        var cfg = With("wrap.max-length", "40", "align.assignments", "true");
        var r = F("fbTimer(IN := bEnable AND NOT bPhase, PT := tOn);\nnA := 1;\nnLonger := 2;", StPart.Implementation, cfg);
        Assert.Equal("fbTimer(\n  IN := bEnable AND NOT bPhase,\n  PT := tOn\n);\nnA      := 1;\nnLonger := 2;", N(r.Text));
        Stable(r.Text, cfg);
    }

    [Fact]
    public void BlankLinesAroundBlocks()
    {
        var cfg = With("blank.around-blocks", "add");
        var r = F("x := 1;\n// check\nIF a THEN\nIF b THEN\ny := 2;\nEND_IF\nEND_IF\nz := 3;", StPart.Implementation, cfg);
        Assert.Equal("x := 1;\n\n// check\nIF a THEN\n  IF b THEN\n    y := 2;\n  END_IF\nEND_IF\n\nz := 3;", N(r.Text));
        Stable(r.Text, cfg);
    }

    [Fact]
    public void NamesWithUnderscoreStandOneColumnLeft()
    {
        var cfg = With("indent.underscore", "declarations");
        var r = F("VAR\nbStart : BOOL;\n_localCount : INT;\nEND_VAR", StPart.Declaration, cfg);
        Assert.Equal("VAR\n  bStart     : BOOL;\n _localCount : INT;\nEND_VAR", N(r.Text));
        Assert.Equal(r.Text, F(r.Text, StPart.Declaration, cfg).Text);
        var code = With("indent.underscore", "everywhere");
        Assert.Equal("IF a THEN\n _count := 1;\n  b := 2;\nEND_IF", N(F("IF a THEN\n_count := 1;\nb := 2;\nEND_IF", StPart.Implementation, code).Text));
    }

    [Fact]
    public void TabsInsideALineBecomeSpacesUpToTheNextTabStop()
    {
        var r = F("nA := 1;\t// first\nnB := 22;\t// second", StPart.Implementation);
        Assert.Equal("nA := 1;    // first\nnB := 22;   // second", N(r.Text));
        var keep = F("nA := 1;\t// first", StPart.Implementation, With("space.tabs", "preserve"));
        Assert.Equal("nA := 1;\t// first", keep.Text);
    }

    [Fact]
    public void ShortIfsOnOneLine()
    {
        var cfg = With("layout.short-if", "40");
        var src = "IF bReset THEN\nnCount := 0;\nEND_IF\nIF bStartConditionsComplete THEN\neState := E_State.Run;\nEND_IF\nIF a THEN\nb := 1;\nELSE\nb := 2;\nEND_IF";
        var r = F(src, StPart.Implementation, cfg);
        Assert.Equal("IF bReset THEN nCount := 0; END_IF\nIF bStartConditionsComplete THEN\n  eState := E_State.Run;\nEND_IF\nIF a THEN\n  b := 1;\nELSE\n  b := 2;\nEND_IF", N(r.Text));
        Stable(r.Text, cfg);
        var both = With("layout.short-if", "40", "layout.blocks", "expand", "blank.around-blocks", "add");
        var x = F("x := 1;\nIF bReset THEN nCount := 0; END_IF\ny := 2;", StPart.Implementation, both);
        Assert.Null(x.Error);
        Stable(x.Text, both);
    }

    [Fact]
    public void LongerIfsOnOneLineAreExpanded()
    {
        var cfg = With("layout.short-if", "40", "layout.long-if", "expand");
        var r = F("IF bReset THEN nCount := 0; END_IF\nIF bStartConditionsComplete THEN eState := E_State.Run; nCount := 0; ELSE eState := E_State.Idle; END_IF;", StPart.Implementation, cfg);
        Assert.Null(r.Error);
        Assert.Equal("IF bReset THEN nCount := 0; END_IF\nIF bStartConditionsComplete THEN\n  eState := E_State.Run;\n  nCount := 0;\nELSE\n  eState := E_State.Idle;\nEND_IF;", N(r.Text));
        Stable(r.Text, cfg);
    }

    [Fact]
    public void EnumCommentsFromTheDut()
    {
        var d = StEnums.FromText("TYPE E_State :\n(\n  Idle := 0,  // waiting for start\n  /// the cycle runs\n  Run,\n  (* fault *)\n  Error\n);\nEND_TYPE");
        Assert.Equal("waiting for start", d["E_State.Idle"]);
        Assert.Equal("the cycle runs", d["E_State.Run"]);
        Assert.Equal("fault", d["E_State.Error"]);
    }

    [Fact]
    public void CaseLabelsGetTheEnumCommentAndBlankLines()
    {
        var cfg = With("comment.case-labels", "enum", "blank.case-branches", "between");
        cfg.EnumComments["E_State.Idle"] = "waiting for start";
        var src = "CASE eState OF\nE_State.Idle:\nx := 1;\nE_State.Run:\nx := 2;\n// my own\nE_State.Error, E_State.Idle:\nx := 3;\n10:\nx := 4;\nELSE\nx := 5;\nEND_CASE";
        var r = F(src, StPart.Implementation, cfg);
        Assert.Null(r.Error);
        Assert.Equal("CASE eState OF\n  // waiting for start\n  E_State.Idle:\n    x := 1;\n\n  // Run\n  E_State.Run:\n    x := 2;\n\n  // my own\n  E_State.Error, E_State.Idle:\n    x := 3;\n\n  10:\n    x := 4;\n\n  ELSE\n    x := 5;\nEND_CASE", N(r.Text));
        Stable(r.Text, cfg);
        var after = With("blank.case-branches", "after-label");
        var a = F("CASE n OF\n1:\nx := 1;\n2: x := 2;\nEND_CASE", StPart.Implementation, after);
        Assert.Equal("CASE n OF\n  1:\n\n    x := 1;\n  2: x := 2;\nEND_CASE", N(a.Text));
        Stable(a.Text, after);
    }

    [Fact]
    public void CommentsOfDeclarationsAboveOrBehind()
    {
        var src = "VAR_INPUT\n/// start the cycle\nbStart : BOOL;\nnParts : INT; // parts so far\n// two lines\n// stay above\nbReset : BOOL;\nEND_VAR";
        var above = With("comment.decl-position", "above");
        var a = F(src, StPart.Declaration, above);
        Assert.Null(a.Error);
        Assert.Equal("VAR_INPUT\n  /// start the cycle\n  bStart : BOOL;\n  // parts so far\n  nParts : INT;\n  // two lines\n  // stay above\n  bReset : BOOL;\nEND_VAR", N(a.Text));
        Stable(a.Text, above);
        var behind = With("comment.decl-position", "behind");
        var b = F(src, StPart.Declaration, behind);
        Assert.Null(b.Error);
        Assert.Equal("VAR_INPUT\n  bStart : BOOL; /// start the cycle\n  nParts : INT;  // parts so far\n  // two lines\n  // stay above\n  bReset : BOOL;\nEND_VAR", N(b.Text));
        Assert.Equal(b.Text, F(b.Text, StPart.Declaration, behind).Text);
    }

    [Fact]
    public void ConfigFromLevelsLocksAndUnset()
    {
        var c = new StFormatConfig();
        var locked = new Dictionary<string, string>();
        c.Apply(System.Text.Json.Nodes.JsonNode.Parse("{\"rules\":{\"indent.size\":4},\"locked\":[\"indent.size\"]}")!.AsObject(), "company", locked);
        c.Apply(System.Text.Json.Nodes.JsonNode.Parse("{\"profile\":\"compact\",\"rules\":{\"indent.size\":8,\"case.keywords\":\"lower\"}}")!.AsObject(), "project", locked);
        Assert.Equal("4", c["indent.size"]);
        Assert.Equal("lower", c["case.keywords"]);
        Assert.Equal("false", c["align.decl"]);
        Assert.Contains(c.Notes, n => n.Contains("locked by company"));
        c.Set("case.keywords", "unset", "user");
        Assert.Equal("upper", c["case.keywords"]);
    }
}
