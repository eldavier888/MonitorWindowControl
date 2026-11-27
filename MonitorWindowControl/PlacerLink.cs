using System;
using System.Threading;
using System.Threading.Tasks;

namespace MonitorWindowControl;

/// <summary>
/// Runs "Move open windows to their monitors", "Bring windows here", Undo and the Recent activity status where they
/// belong: in the background helper when it's the one placing windows (it knows what happened in the background and
/// which windows you moved yourself), otherwise in this app's own engine.
/// </summary>
internal sealed class PlacerLink
{
    private const int TimeoutMs = 3000; // the helper normally answers within a few milliseconds
    private const int PollMs = 50;      // how often to look for its answer while waiting (only while waiting)
    private readonly Engine _engine;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _nextId = Environment.TickCount & 0x3FFFFFFF;

    public PlacerLink(Engine engine) => _engine = engine;

    /// <summary>True when the background helper places windows (helper mode, and it's running).</summary>
    public bool ViaHelper => _engine.Settings.RunMode == RunModes.Helper && !_engine.IsActive && HelperManager.IsRunning();

    /// <summary>
    /// Does what <paramref name="req"/> asks, in the helper or here. If the helper doesn't answer, the reply's
    /// <see cref="HelperReply.Error"/> says so.
    /// </summary>
    public async Task<HelperReply> RunAsync(HelperRequest req)
    {
        if (!ViaHelper) return _engine.Answer(req);
        await _gate.WaitAsync();
        try
        {
            req.Id = Interlocked.Increment(ref _nextId);
            if (HelperChannel.WriteRequest(req) && Signal())
            {
                long start = Environment.TickCount64;
                while (Environment.TickCount64 - start < TimeoutMs)
                {
                    await Task.Delay(PollMs);
                    if (HelperChannel.ReadReply() is { } reply && reply.Id == req.Id) return reply;
                }
            }
        }
        finally { _gate.Release(); }
        return new HelperReply { Id = req.Id, Error = "The background helper didn't answer. Try again in a moment." };
    }

    /// <summary>Wakes the helper up to read the request.</summary>
    private static bool Signal()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(HelperChannel.EventName);
            return ev.Set();
        }
        catch (Exception) { return false; } // the helper isn't running (any more)
    }
}
