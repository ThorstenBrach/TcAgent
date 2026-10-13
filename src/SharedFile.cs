// Appending lines to files that several TcAgent processes write at the same time (audit log, daily log, feedback):
// File.AppendAllText opens the file without write sharing - a second process then gets "file in use" and the line was lost.
// Here: shared append (FileShare.ReadWrite) with a few retries.
//
// SPDX-License-Identifier: MIT
using System.Text;

namespace TcAgent;

public static class SharedFile
{
    static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Appends text (one or more complete lines) - retries for about a second if another process holds the file.</summary>
    public static void Append(string path, string text, int attempts = 10)
    {
        var bytes = Utf8.GetBytes(text);
        for (int i = 1; ; i++)
        {
            try
            {
                using var f = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                f.Write(bytes, 0, bytes.Length);
                return;
            }
            catch (IOException) when (i < attempts) { Thread.Sleep(20 * i); }
            catch (UnauthorizedAccessException) when (i < attempts) { Thread.Sleep(20 * i); }
        }
    }

    /// <summary>Reads all lines while other processes may still be appending.</summary>
    public static List<string> ReadLines(string path)
    {
        var list = new List<string>();
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(f, Utf8, true);
        string l;
        while ((l = r.ReadLine()) != null) list.Add(l);
        return list;
    }
}
