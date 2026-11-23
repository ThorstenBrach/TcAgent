// Tests for the vInfo of CreateChild per PLC subtype and the default declarations of tc_plc_create (PlcCreateInfo.cs).
//
// SPDX-License-Identifier: MIT
using Xunit;

namespace TcAgent.Tests;

public sealed class PlcCreateInfoTests
{
    [Fact]
    public void AnInterfaceMethodGetsTheReturnTypeAsFirstElement()
    {
        var c = PlcCreateInfo.Candidates(610, 1, "DINT", null);
        Assert.Equal(new object[] { "DINT" }, (object[])c[0]);
        Assert.Equal("DINT", c[1]);
        Assert.Null(c[2]);
    }

    [Fact]
    public void AnAliasGetsItsDeclarationAsFirstElement()
    {
        const string decl = "TYPE T_Name : STRING(80);\nEND_TYPE\n";
        var c = PlcCreateInfo.Candidates(623, 1, null, decl);
        Assert.Equal(new object[] { decl }, (object[])c[0]);
        Assert.Equal(decl, c[1]);
        Assert.Null(c[2]);
        Assert.Equal(new object[] { null }, PlcCreateInfo.Candidates(623, 1, null, null));
    }

    [Fact]
    public void OtherSubtypesKeepTheirSingleVInfo()
    {
        Assert.Equal(new object[] { 1 }, PlcCreateInfo.Candidates(604, 1, null, null));
        Assert.Equal(new object[] { 1, "BOOL" }, (object[])Assert.Single(PlcCreateInfo.Candidates(609, 1, null, null)));
        Assert.Equal(new object[] { "REAL" }, PlcCreateInfo.Candidates(611, 1, "REAL", null));
        Assert.Equal(new object[] { null }, PlcCreateInfo.Candidates(606, 1, null, null));
    }

    [Theory]
    [InlineData("TYPE T_Speed : REAL;\nEND_TYPE", "REAL")]
    [InlineData("// speed in mm/s\nTYPE T_Name :  STRING(80) ;\nEND_TYPE", "STRING(80)")]
    [InlineData("TYPE T_Buf : ARRAY[1..10] OF BYTE;\nEND_TYPE", "ARRAY[1..10] OF BYTE")]
    [InlineData("TYPE E_Mode : (Off, On) INT;\nEND_TYPE", null)]
    [InlineData("TYPE ST_Data :\nSTRUCT\n  a : INT;\nEND_STRUCT\nEND_TYPE", null)]
    [InlineData(null, null)]
    public void TheBaseTypeComesFromAnAliasDeclaration(string decl, string expected) =>
        Assert.Equal(expected, PlcCreateInfo.AliasBaseType(decl));

    [Fact]
    public void DefaultDeclarations()
    {
        Assert.Equal("TYPE T_Speed : REAL;\nEND_TYPE\n", PlcCreateInfo.AliasDeclaration("T_Speed", "REAL"));
        Assert.Equal("METHOD Start : BOOL\nVAR_INPUT\nEND_VAR\n", PlcCreateInfo.InterfaceMethodDeclaration("Start", "BOOL"));
        Assert.Equal("METHOD Reset\nVAR_INPUT\nEND_VAR\n", PlcCreateInfo.InterfaceMethodDeclaration("Reset", null));
    }
}
