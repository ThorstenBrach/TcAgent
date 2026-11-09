// Where the variables of a condition come from (SymbolResolve.cs): inherited, reference, pointer, interface, THIS^/SUPER^,
// method locals, GVLs, properties, array indexes.
//
// SPDX-License-Identifier: MIT
using TcAgent;
using Xunit;

public class SymbolResolveTests
{
    static string Pou(string name, string decl, string extra = "") =>
        $"<TcPlcObject><POU Name=\"{name}\"><Declaration><![CDATA[{decl}]]></Declaration>{extra}</POU></TcPlcObject>";

    static ProjectIndex Project()
    {
        var idx = new ProjectIndex();
        SymbolResolve.AddFile(idx, Pou("FB_Base", "FUNCTION_BLOCK FB_Base\nVAR\n  bAuto : BOOL;\nEND_VAR"));
        SymbolResolve.AddFile(idx, Pou("FB_Station", "FUNCTION_BLOCK FB_Station EXTENDS FB_Base\nVAR_INPUT\n  bSetup : BOOL;\nEND_VAR\nVAR\n" +
            "  bError : BOOL;\n  pSensor : POINTER TO ST_Sensor;\n  refParams : REFERENCE TO ST_Params;\n  itfKey : I_Key;\n  aNest : ARRAY[1..4] OF ST_Nest;\n  nIdx : INT;\n  fbTimer : TON;\n  fbAxis : FB_Axis;\nEND_VAR\nVAR_TEMP\n  bTmp : BOOL;\nEND_VAR",
            "<Method Name=\"Cycle\"><Declaration><![CDATA[METHOD Cycle : BOOL\nVAR_INPUT\n  bStart : BOOL;\nEND_VAR\nVAR\n  nLocal : INT;\nEND_VAR]]></Declaration></Method>" +
            "<Property Name=\"Ready\"><Declaration><![CDATA[{attribute 'monitoring' := 'call'}\nPROPERTY Ready : BOOL]]></Declaration></Property>"));
        SymbolResolve.AddFile(idx, Pou("FB_Axis", "FUNCTION_BLOCK FB_Axis\nVAR_OUTPUT\n  bDone : BOOL;\nEND_VAR",
            "<Property Name=\"Busy\"><Declaration><![CDATA[PROPERTY Busy : BOOL]]></Declaration></Property>"));
        SymbolResolve.AddFile(idx, "<TcPlcObject><DUT Name=\"ST_Sensor\"><Declaration><![CDATA[TYPE ST_Sensor :\nSTRUCT\n  rValue : REAL;\nEND_STRUCT\nEND_TYPE]]></Declaration></DUT></TcPlcObject>");
        SymbolResolve.AddFile(idx, "<TcPlcObject><DUT Name=\"ST_Params\"><Declaration><![CDATA[TYPE ST_Params :\nSTRUCT\n  rMin : REAL;\nEND_STRUCT\nEND_TYPE]]></Declaration></DUT></TcPlcObject>");
        SymbolResolve.AddFile(idx, "<TcPlcObject><DUT Name=\"ST_Nest\"><Declaration><![CDATA[TYPE ST_Nest :\nSTRUCT\n  bFree : BOOL;\nEND_STRUCT\nEND_TYPE]]></Declaration></DUT></TcPlcObject>");
        SymbolResolve.AddFile(idx, "<TcPlcObject><DUT Name=\"E_State\"><Declaration><![CDATA[TYPE E_State :\n(\n  Idle := 0,\n  Run\n);\nEND_TYPE]]></Declaration></DUT></TcPlcObject>");
        SymbolResolve.AddFile(idx, "<TcPlcObject><Itf Name=\"I_Key\"><Declaration><![CDATA[INTERFACE I_Key]]></Declaration></Itf></TcPlcObject>");
        SymbolResolve.AddFile(idx, "<TcPlcObject><GVL Name=\"GVL_IO\"><Declaration><![CDATA[{attribute 'qualified_only'}\nVAR_GLOBAL\n  bEmergency : BOOL;\nEND_VAR]]></Declaration></GVL></TcPlcObject>");
        return idx;
    }

    static ResolvedOperand One(string atom, string method = null) =>
        Assert.Single(SymbolResolve.Operands(Project(), "FB_Station", method, "MAIN.fbStation1", atom));

    [Fact]
    public void InstancesAreFoundInProgramsAndGvls()
    {
        var idx = Project();
        SymbolResolve.AddFile(idx, Pou("MAIN", "PROGRAM MAIN\nVAR\n  fbStation1 : FB_Station;\n  aStations : ARRAY[1..3] OF FB_Station;\n  stLine : ST_Line;\nEND_VAR"));
        SymbolResolve.AddFile(idx, "<TcPlcObject><DUT Name=\"ST_Line\"><Declaration><![CDATA[TYPE ST_Line :\nSTRUCT\n  fbLast : FB_Station;\nEND_STRUCT\nEND_TYPE]]></Declaration></DUT></TcPlcObject>");
        Assert.Equal(new[] { "MAIN.fbStation1", "MAIN.aStations[1]", "MAIN.stLine.fbLast" }, SymbolResolve.Instances(idx, "FB_Station"));
        Assert.Contains("MAIN.fbStation1", SymbolResolve.Instances(idx, "FB_Base"));      // an instance of a derived FB counts
        Assert.Equal(new[] { "MAIN" }, SymbolResolve.Instances(idx, "MAIN"));
    }

    [Fact]
    public void InheritedVariableIsReadThroughTheInstance()
    {
        var o = One("bAuto");
        Assert.Equal("MAIN.fbStation1.bAuto", o.Path);
        Assert.Contains("FB_Base", o.From);
    }

    [Fact]
    public void EnumIndexAndMembersOfALibraryBase()
    {
        var idx = Project();
        SymbolResolve.AddFile(idx, "<TcPlcObject><DUT Name=\"E_Band\"><Declaration><![CDATA[TYPE E_Band :\n(\n  OK := 1,\n  NOK,\n  REWORK := 16#10\n);\nEND_TYPE]]></Declaration></DUT></TcPlcObject>");
        SymbolResolve.AddFile(idx, Pou("FB_Handling", "FUNCTION_BLOCK FB_Handling EXTENDS LibUnitBase\nVAR\n  bOwn : BOOL;\nEND_VAR"));
        SymbolResolve.AddFile(idx, "<TcPlcObject><GVL Name=\"GVL_Line\"><Declaration><![CDATA[VAR_GLOBAL\n  aBands : ARRAY[1..20] OF FB_Handling;\n  fbHandling : FB_Handling;\nEND_VAR]]></Declaration></GVL></TcPlcObject>");
        var o = Assert.Single(SymbolResolve.Operands(idx, "FB_Station", null, "MAIN.fbStation1", "aBands[E_Band.NOK].IsEmpty"));
        Assert.Equal("GVL_Line.aBands[2].IsEmpty", o.Path);
        Assert.Contains("LibUnitBase", o.From);
        Assert.Equal("GVL_Line.aBands[16].bOwn", SymbolResolve.Operands(idx, "FB_Station", null, "MAIN.fbStation1", "aBands[E_Band.REWORK].bOwn")[0].Path);
        Assert.Equal("GVL_Line.fbHandling.IsEmpty", SymbolResolve.Operands(idx, "FB_Station", null, "MAIN.fbStation1", "fbHandling.IsEmpty")[0].Path);
    }

    [Fact]
    public void CommentsOfDeclarationsComeAlong()
    {
        var d = SymbolResolve.ParseDecl("FUNCTION_BLOCK FB_X\nVAR\n  bError : BOOL; // collective fault\n  // door locked\n  bLocked : BOOL;\n  nCount : INT := 0; (* parts *)\n  bNone : BOOL;\nEND_VAR");
        Assert.Equal("collective fault", d.Vars["bError"].Comment);
        Assert.Equal("door locked", d.Vars["bLocked"].Comment);
        Assert.Equal("parts", d.Vars["nCount"].Comment);
        Assert.Null(d.Vars["bNone"].Comment);
    }

    [Fact]
    public void ThisAndSuper()
    {
        Assert.Equal("MAIN.fbStation1.bError", One("THIS^.bError").Path);
        var s = One("SUPER^.bAuto");
        Assert.Equal("MAIN.fbStation1.bAuto", s.Path);
        Assert.Contains("SUPER", s.From);
    }

    [Fact]
    public void PointerIsCheckedThenDereferenced()
    {
        var o = One("pSensor^.rValue");
        Assert.Equal("MAIN.fbStation1.pSensor^.rValue", o.Path);
        Assert.Equal(new[] { "MAIN.fbStation1.pSensor" }, o.PointerChecks);
        Assert.Contains("pointer", o.From);
        Assert.Null(One("pSensor.rValue").Path);       // a pointer without ^
    }

    [Fact]
    public void ReferenceIsReadThroughIt()
    {
        var o = One("refParams.rMin");
        Assert.Equal("MAIN.fbStation1.refParams.rMin", o.Path);
        Assert.Contains("reference", o.From);
    }

    [Fact]
    public void InterfacePropertyAndMethodLocalsAreNotRead()
    {
        Assert.Contains("interface", One("itfKey.Enabled").Reason);
        Assert.Contains("stack", One("nLocal", "Cycle").Reason);
        Assert.Contains("stack", One("bStart", "Cycle").Reason);
        Assert.Contains("VAR_TEMP", One("bTmp").Reason);
        Assert.Contains("monitoring", One("fbAxis.Busy").Reason);
        Assert.Equal("MAIN.fbStation1.Ready", One("Ready").Path);
        Assert.Contains("call", One("F_Check(1)").Reason);
    }

    [Fact]
    public void ArrayIndexIsReadFirst()
    {
        var o = One("aNest[nIdx].bFree");
        Assert.Equal("MAIN.fbStation1.aNest[{0}].bFree", o.Path);
        Assert.Equal(("nIdx", "MAIN.fbStation1.nIdx"), o.Indexes[0]);
        Assert.Equal("MAIN.fbStation1.aNest[2].bFree", One("aNest[2].bFree").Path);
    }

    [Fact]
    public void GlobalsLibraryMembersAndEnums()
    {
        Assert.Equal("GVL_IO.bEmergency", One("GVL_IO.bEmergency").Path);
        Assert.Equal("MAIN.fbStation1.fbTimer.Q", One("fbTimer.Q").Path);
        Assert.Equal("MAIN.fbStation1.fbAxis.bDone", One("fbAxis.bDone").Path);
        var ops = SymbolResolve.Operands(Project(), "FB_Station", null, "MAIN.fbStation1", "eState = E_State.Run");
        Assert.Single(ops);                     // E_State.Run is no operand
    }
}
