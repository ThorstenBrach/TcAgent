// Alle COM-Aufrufe an Visual Studio/TwinCAT laufen auf EINEM STA-Thread mit registriertem
// OLE-Message-Filter (sonst RPC_E_CALL_REJECTED, wenn die IDE beschaeftigt ist).
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace TcAgent;

public sealed class StaWorker : IDisposable
{
    readonly BlockingCollection<Action> queue = new();
    readonly Thread thread;

    public StaWorker()
    {
        thread = new Thread(Loop) { IsBackground = true, Name = "TcAgent-STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    void Loop()
    {
        MessageFilter.Register();
        foreach (var a in queue.GetConsumingEnumerable()) a();
        MessageFilter.Revoke();
    }

    public T Run<T>(Func<T> f, int timeoutMs = 600_000)
    {
        T result = default; Exception err = null;
        using var done = new ManualResetEventSlim();
        queue.Add(() => { try { result = f(); } catch (Exception ex) { err = ex; } finally { done.Set(); } });
        if (!done.Wait(timeoutMs)) throw new TimeoutException($"Zeitueberschreitung nach {timeoutMs / 1000}s");
        if (err != null) throw err is COMException c ? new Exception($"COM-Fehler 0x{c.HResult:X8}: {c.Message}", c) : err;
        return result;
    }

    public void Dispose() => queue.CompleteAdding();
}

[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IOleMessageFilter
{
    [PreserveSig] int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);
    [PreserveSig] int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);
    [PreserveSig] int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
}

sealed class MessageFilter : IOleMessageFilter
{
    public static void Register() => CoRegisterMessageFilter(new MessageFilter(), out _);
    public static void Revoke() => CoRegisterMessageFilter(null, out _);
    int IOleMessageFilter.HandleInComingCall(int a, IntPtr b, int c, IntPtr d) => 0;
    // SERVERCALL_RETRYLATER -> nach 100 ms erneut versuchen (max. ~5 min)
    int IOleMessageFilter.RetryRejectedCall(IntPtr a, int tick, int type) => type == 2 && tick < 300_000 ? 100 : -1;
    int IOleMessageFilter.MessagePending(IntPtr a, int b, int c) => 2;
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IOleMessageFilter newFilter, out IOleMessageFilter oldFilter);
}
