// Unit tests: a PLC text part is read back after writing - a write the IDE dropped is repeated once, a text that still differs
// is reported instead of "written".
// SPDX-License-Identifier: MIT
using TcAgent;
using Xunit;

public class PlcWriteVerifyTests
{
    [Fact]
    public void ArrivedWriteSaysWritten_LineEndingsDoNotMatter()
    {
        string stored = null;
        var r = PlcText.SetAndVerify("implementation", () => stored, t => stored = t.Replace("\n", "\r\n") + "\r\n", "a := 1;\nb := 2;");
        Assert.Equal("implementation written", r);
    }

    [Fact]
    public void DroppedFirstWriteIsRepeated()
    {
        string stored = ""; int calls = 0;
        var r = PlcText.SetAndVerify("implementation", () => stored, t => { if (++calls > 1) stored = t; }, "x := TRUE;");
        Assert.Equal(2, calls);
        Assert.Contains("second attempt", r);
    }

    [Fact]
    public void BusyFileIsRetried()
    {
        string stored = ""; int calls = 0;
        var r = PlcText.SetAndVerify("declaration", () => stored,
            t => { if (++calls == 1) throw new System.IO.FileLoadException("The file is used by another process"); stored = t; }, "VAR END_VAR");
        Assert.Equal("declaration written", r);
    }

    [Fact]
    public void TextThatNeverArrivesIsReported()
    {
        var r = PlcText.SetAndVerify("implementation", () => "", t => { }, "y := 3;");
        Assert.Contains("BUT reading it back", r);
    }
}
