// Client for the VS extension TcAgentBridge (named pipe "TcAgentBridge.<pid>" in the Visual Studio process).
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace TcAgent;

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

    public static JsonObject Call(int pid, string command, JsonObject args = null, int timeoutMs = 30000)
    {
        var req = args?.DeepClone().AsObject() ?? new JsonObject();
        req["command"] = command;
        using var pipe = new NamedPipeClientStream(".", PipeFor(pid), PipeDirection.InOut);
        pipe.Connect(3000);
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
