// Client fuer die VS-Erweiterung TcAgentBridge (Named Pipe "TcAgentBridge.<pid>" im Visual-Studio-Prozess).
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace TcAgent;

public static class BridgeClient
{
    const string Prefix = "TcAgentBridge.";

    /// <summary>Pipe-Name der Bruecke: zur verbundenen IDE (pid) oder, falls unbekannt, die einzige laufende Bruecke.</summary>
    public static string PipeFor(int pid)
    {
        var pipes = Directory.GetFiles(@"\\.\pipe\").Select(Path.GetFileName).Where(n => n != null && n.StartsWith(Prefix)).ToList();
        if (pid > 0)
        {
            var want = Prefix + pid;
            if (pipes.Contains(want)) return want;
            throw new Exception($"Die VS-Erweiterung TcAgentBridge laeuft nicht in der verbundenen IDE (pid {pid}). " +
                                (pipes.Count > 0 ? "Gefunden: " + string.Join(", ", pipes) : "Keine Bruecke aktiv - Erweiterung installiert und VS neu gestartet?"));
        }
        if (pipes.Count == 1) return pipes[0];
        throw new Exception(pipes.Count == 0 ? "Keine TcAgentBridge aktiv - VS-Erweiterung installiert und VS neu gestartet?"
                                             : "Mehrere Bruecken aktiv, bitte mit tc_attach verbinden: " + string.Join(", ", pipes));
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
        if (!read.Wait(timeoutMs)) throw new TimeoutException($"TcAgentBridge antwortet nicht innerhalb {timeoutMs / 1000} s ({command})");
        var resp = JsonNode.Parse(read.Result ?? "{}")!.AsObject();
        if (resp["ok"]?.GetValue<bool>() != true) throw new Exception("TcAgentBridge: " + (resp["error"]?.ToString() ?? "unbekannter Fehler"));
        return resp;
    }
}
