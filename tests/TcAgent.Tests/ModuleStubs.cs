// The few server types Modules.cs and Plugins.cs use, for the tests (the server project needs the TwinCAT interop).
//
// SPDX-License-Identifier: MIT
namespace TcAgent;

public enum ToolClass { Read, ProjectWrite, RuntimeWrite }

public static class Log
{
    public static readonly List<string> Lines = new();
    public static void Info(string m) { lock (Lines) Lines.Add(m); }
    public static void Error(string m) { lock (Lines) Lines.Add("ERROR " + m); }
}

public static partial class Cli { public const int Ok = 0, Failed = 1, Usage = 2, Infra = 3; }

public static class Program { public const string ServerVersion = "test"; }
