// Client for the VS extension TcAgentBridge (named pipe "TcAgentBridge.<pid>" in the Visual Studio process).
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace TcAgent;

/// <summary>The pipe is not served by Visual Studio / the XAE Shell - its answers must never count as a confirmation.</summary>
public sealed class UntrustedBridgeException(string message) : Exception(message);

public static class BridgeClient
{
    const string Prefix = "TcAgentBridge.";

    /// <summary>Pipe name of the bridge: for the connected IDE (pid) or, if unknown, the only running bridge.</summary>
    public static string PipeFor(int pid)
    {
        var pipes = Directory.GetFiles(@"\\.\pipe\").Select(Path.GetFileName).Where(n => n != null && n.StartsWith(Prefix)).ToList();
        if (pid > 0)
        {
            var want = Prefix + pid;
            if (pipes.Contains(want)) return want;
            throw new Exception($"The VS extension TcAgentBridge is not running in the connected IDE (pid {pid}). " +
                                (pipes.Count > 0 ? "Found: " + string.Join(", ", pipes) : "No bridge active - extension installed and VS restarted?"));
        }
        if (pipes.Count == 1) return pipes[0];
        throw new Exception(pipes.Count == 0 ? "No TcAgentBridge active - VS extension installed and VS restarted?"
                                             : "Several bridges active, please connect with tc_attach: " + string.Join(", ", pipes));
    }

    public static bool Available(int pid) { try { PipeFor(pid); return true; } catch { return false; } }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

    /// <summary>Only talk to the bridge inside Visual Studio / XAE Shell: another program could create a pipe with the same
    /// name first and then answer confirmations with "yes" or read the editor context.</summary>
    static void VerifyServer(NamedPipeClientStream pipe, string name)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid))
            throw new UntrustedBridgeException($"TcAgentBridge: the process behind pipe {name} cannot be determined - refused");
        if (serverPid.ToString() != name[Prefix.Length..])
            throw new UntrustedBridgeException($"TcAgentBridge: pipe {name} is served by process {serverPid}, not by the IDE - refused");
        string proc;
        try { proc = Process.GetProcessById((int)serverPid).ProcessName; } catch { proc = "?"; }
        if (!proc.Equals("devenv", StringComparison.OrdinalIgnoreCase) && !proc.StartsWith("TcXaeShell", StringComparison.OrdinalIgnoreCase))
            throw new UntrustedBridgeException($"TcAgentBridge: pipe {name} is served by '{proc}' ({serverPid}), not by Visual Studio or the XAE Shell - refused");
    }

    public static JsonObject Call(int pid, string command, JsonObject args = null, int timeoutMs = 30000)
    {
        var req = args?.DeepClone().AsObject() ?? new JsonObject();
        req["command"] = command;
        var name = PipeFor(pid);
        // Identification: a foreign process serving this pipe name cannot act with the user's rights
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
        pipe.Connect(3000);
        VerifyServer(pipe, name);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        var reader = new StreamReader(pipe, new UTF8Encoding(false));
        writer.WriteLine(req.ToJsonString());
        var read = reader.ReadLineAsync();
        if (!read.Wait(timeoutMs)) throw new TimeoutException($"TcAgentBridge did not respond within {timeoutMs / 1000} s ({command})");
        var resp = JsonNode.Parse(read.Result ?? "{}")!.AsObject();
        if (resp["ok"]?.GetValue<bool>() != true) throw new Exception("TcAgentBridge: " + (resp["error"]?.ToString() ?? "unknown error"));
        return resp;
    }
}
