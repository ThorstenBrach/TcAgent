// Modal dialogs of the IDE (e.g. "Target system reports a fatal error", license or save prompts).
// While such a dialog is open, every Automation Interface call waits - TcAgent would hang without knowing why.
// This class finds them (title + text), so TcAgent can report them to the agent or - in the pipeline - close them.
// Closing always means "cancel / X" (WM_CLOSE), never "Yes" or "OK, overwrite".
//
// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TcAgent;

public static class IdeDialogs
{
    public sealed record Dialog(IntPtr Handle, string Title, string Text, string[] Buttons)
    {
        public override string ToString() =>
            $"'{Title}'" + (Text.Length > 0 ? ": " + Text : "") + (Buttons.Length > 0 ? $" [{string.Join(" | ", Buttons)}]" : "");
    }

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, StringBuilder l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    const uint GW_OWNER = 4, WM_GETTEXT = 0x000D, WM_GETTEXTLENGTH = 0x000E, WM_CLOSE = 0x0010, SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>Modal dialogs of the IDE process: visible owned top-level windows while the main window is disabled.</summary>
    public static List<Dialog> Find(int pid)
    {
        var result = new List<Dialog>();
        if (pid <= 0) return result;
        IntPtr main;
        try { using var p = Process.GetProcessById(pid); main = p.MainWindowHandle; }
        catch { return result; }
        bool modal = main != IntPtr.Zero && !IsWindowEnabled(main);

        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var wpid);
            if (wpid != pid || h == main || !IsWindowVisible(h)) return true;
            var cls = ClassOf(h);
            bool isMessageBox = cls == "#32770";
            // classic message boxes always count; other owned windows only while the main window is blocked
            if (!isMessageBox && !(modal && GetWindow(h, GW_OWNER) != IntPtr.Zero && IsWindowEnabled(h))) return true;
            var title = TextOf(h);
            var texts = new List<string>(); var buttons = new List<string>();
            EnumChildWindows(h, (c, __) =>
            {
                if (!IsWindowVisible(c)) return true;
                var t = TextOf(c).Replace("&", "").Trim();
                if (t.Length == 0) return true;
                if (ClassOf(c).Contains("BUTTON", StringComparison.OrdinalIgnoreCase)) buttons.Add(t); else texts.Add(t);
                return true;
            }, IntPtr.Zero);
            if (title.Length == 0 && texts.Count == 0) return true;   // e.g. invisible helper or tooltip windows
            result.Add(new Dialog(h, title, string.Join(" ", texts.Distinct()).Replace("\r", " ").Replace("\n", " ").Trim(), buttons.Distinct().ToArray()));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Closes a dialog like the X button / Esc (cancel semantics).</summary>
    public static void Close(Dialog d) => PostMessage(d.Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    static string TextOf(IntPtr h)
    {
        // WM_GETTEXT with timeout - GetWindowText could block on a hung window
        if (SendMessageTimeout(h, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 500, out var len) == IntPtr.Zero) return "";
        int n = (int)len;
        if (n <= 0) return "";
        var sb = new StringBuilder(Math.Min(n, 4000) + 1);
        SendMessageTimeout(h, WM_GETTEXT, (IntPtr)sb.Capacity, sb, SMTO_ABORTIFHUNG, 500, out _);
        return sb.ToString();
    }
}
