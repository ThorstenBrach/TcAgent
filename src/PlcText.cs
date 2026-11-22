// Writing a text part of a PLC object (declaration, implementation) and reading it back - used by tc_plc_write, tc_plc_create
// and the plain-text import (ToolRegistry.PlcWrite).
// SPDX-License-Identifier: MIT
using System;
using System.Linq;

namespace TcAgent;

public static class PlcText
{
    // Writes a text part and reads it back. The IDE sometimes drops a write - several methods of one POU written in quick
    // succession (they share one .TcPOU file that the IDE is still saving: "locked by another process") left a method empty
    // while the tool said "written". A write that did not arrive, or failed because the file was busy, is repeated once
    // after a short pause; if the text still differs the result says so instead of "written".
    public static string SetAndVerify(string part, Func<string> get, Action<string> set, string text)
    {
        static string Norm(string s) => string.Join("\n", (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd())).Trim();
        bool Arrived() { try { return Norm(get()) == Norm(text); } catch { return false; } }
        try { set(text); }
        catch (Exception ex) when (IsBusyFile(ex)) { System.Threading.Thread.Sleep(500); set(text); }
        if (Arrived()) return part + " written";
        System.Threading.Thread.Sleep(300);
        set(text);
        if (Arrived()) return part + " written (second attempt - the IDE had not kept the first one)";
        return part + " written, BUT reading it back shows a different text - check it with tc_plc_read (the IDE may have reformatted or dropped it)";
    }

    static bool IsBusyFile(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is System.IO.IOException || (e.Message ?? "").IndexOf("another process", StringComparison.OrdinalIgnoreCase) >= 0
                || (e.Message ?? "").IndexOf("anderen Prozess", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }
}
