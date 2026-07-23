namespace PolyTerm.App.McpBridge;

/// <summary>
/// Thread-safe monotonic version counter for a session's framebuffer — bumped once per
/// <c>IFramebufferChannel.RegionUpdated</c> (engine thread), read by <c>attach_framebuffer_read</c>
/// (pipe thread) to decide whether a poll needs to re-encode+re-base64 <c>CaptureSnapshot()</c> or can
/// cheaply answer <c>{ changed: false }</c>. Starts at 1 — a real, attachable "initial state" version,
/// not a sentinel — since a session can go from attach to steady-state (nothing changes) without a
/// single <see cref="Bump"/> ever happening, and that first version must still compare equal to itself
/// on the next poll. A client's <c>since_version</c> of 0 or less (never having called <c>attach_start</c>
/// at all) is what's treated as "I have nothing, give me whatever you've got" by the reader.
/// </summary>
internal sealed class FramebufferVersionTracker
{
    private long _value = 1;
    public long Value => Interlocked.Read(ref _value);
    public void Bump() => Interlocked.Increment(ref _value);
}
