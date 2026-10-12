// All COM calls to Visual Studio/TwinCAT run on ONE STA thread with a registered
// OLE message filter (otherwise RPC_E_CALL_REJECTED when the IDE is busy).
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
        if (Thread.CurrentThread == thread) return f();   // already on the STA thread (nested call) - run inline, never deadlock
        T result = default; Exception err = null;
        using var done = new ManualResetEventSlim();
        queue.Add(() => { try { result = f(); } catch (Exception ex) { err = ex; } finally { done.Set(); } });
        if (!done.Wait(timeoutMs)) throw new TimeoutException($"Timeout after {timeoutMs / 1000}s");
        if (err != null) throw err is COMException c ? new Exception($"COM error 0x{c.HResult:X8}: {c.Message}", c) : err;
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
    // SERVERCALL_RETRYLATER -> retry after 100 ms (max. ~5 min)
    int IOleMessageFilter.RetryRejectedCall(IntPtr a, int tick, int type) => type == 2 && tick < 300_000 ? 100 : -1;
    int IOleMessageFilter.MessagePending(IntPtr a, int b, int c) => 2;
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IOleMessageFilter newFilter, out IOleMessageFilter oldFilter);
}
