using System.Reflection;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Messages;

namespace PolyTerm.Protocols.Ssh.X11;

/// <summary>
/// Reflection access to SSH.NET internals with no public extension point: X11 forwarding needs the
/// "x11-req" channel request sent <b>before</b> "shell" (so the remote shell gets <c>DISPLAY</c>
/// auto-exported, matching real <c>ssh -X</c>), but <see cref="ShellStream"/>'s constructor sends
/// "pty-req" then "shell" atomically with no hook in between. <see cref="Channels.IChannelSession"/>'s
/// <c>SendX11ForwardingRequest</c> exists and works (confirmed live against a real ad-hoc
/// <c>sshd.exe</c> — see docs/todos.md's SSH X11 section), but the interface and every channel type
/// in <c>Renci.SshNet.Channels</c> are internal, so driving them requires reflection end to end.
/// </summary>
/// <remarks>
/// <b>Fragility, acknowledged upfront:</b> every member here is an SSH.NET implementation detail with
/// no versioning contract — a future SSH.NET release could rename or restructure any of it without
/// it counting as a breaking change from their side. Confirmed against SSH.NET 2025.1.0 specifically
/// (live, against a real ad-hoc Windows <c>sshd.exe</c> — see the same section of docs/todos.md for
/// exactly what was and wasn't verified). If this throws after an SSH.NET upgrade, re-verify each
/// member here via reflection over the new version before assuming the shape is unchanged.
/// </remarks>
internal static class SshNetInternals
{
    private static readonly PropertyInfo BaseClientSessionProperty =
        typeof(BaseClient).GetProperty("Session", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMemberException("BaseClient.Session");

    private static readonly MethodInfo SessionSendMessageMethod =
        typeof(Session).GetMethod("SendMessage", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(Message)], null)
        ?? throw new MissingMemberException("Session.SendMessage");

    private static readonly PropertyInfo SessionNextChannelNumberProperty =
        typeof(Session).GetProperty("NextChannelNumber", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMemberException("Session.NextChannelNumber");

    private static readonly MethodInfo CreateChannelSessionMethod =
        typeof(SshClient).Assembly.GetType("Renci.SshNet.ISession")?.GetMethod("CreateChannelSession")
        ?? throw new MissingMemberException("ISession.CreateChannelSession");

    private static readonly Type ChannelSessionInterfaceType =
        typeof(SshClient).Assembly.GetType("Renci.SshNet.Channels.IChannelSession")
        ?? throw new MissingMemberException("Renci.SshNet.Channels.IChannelSession");

    private static readonly MethodInfo ChannelOpenMethod = ChannelSessionInterfaceType.GetMethod("Open")!;
    private static readonly MethodInfo SendPseudoTerminalRequestMethod = ChannelSessionInterfaceType.GetMethod("SendPseudoTerminalRequest")!;
    private static readonly MethodInfo SendX11ForwardingRequestMethod = ChannelSessionInterfaceType.GetMethod("SendX11ForwardingRequest")!;
    private static readonly MethodInfo SendShellRequestMethod = ChannelSessionInterfaceType.GetMethod("SendShellRequest")!;
    private static readonly MethodInfo SendWindowChangeRequestMethod = ChannelSessionInterfaceType.GetMethod("SendWindowChangeRequest")!;

    // Channel.DataReceived/Closed/SendData/Close are declared on the internal Channel base class,
    // shared by every channel type (confirmed live: this is the same reflection path a "session"
    // channel's shell traffic already uses successfully).
    private static readonly EventInfo ChannelDataReceivedEvent = FindEventOnBaseTypes(typeof(SshClient).Assembly
        .GetType("Renci.SshNet.Channels.ChannelSession")!, "DataReceived")!;
    private static readonly EventInfo ChannelClosedEvent = FindEventOnBaseTypes(typeof(SshClient).Assembly
        .GetType("Renci.SshNet.Channels.ChannelSession")!, "Closed")!;
    private static readonly MethodInfo ChannelSendDataMethod = FindMethodOnBaseTypes(typeof(SshClient).Assembly
        .GetType("Renci.SshNet.Channels.ChannelSession")!, "SendData", typeof(byte[]))!;
    private static readonly MethodInfo ChannelCloseMethod = FindMethodOnBaseTypes(typeof(SshClient).Assembly
        .GetType("Renci.SshNet.Channels.ChannelSession")!, "Close")!;

    /// <summary>The live SSH session backing <paramref name="client"/> — null before <c>Connect()</c>.</summary>
    public static Session? GetSession(BaseClient client) => (Session?)BaseClientSessionProperty.GetValue(client);

    /// <summary>Allocates the next free channel number. Confirmed live: each read auto-increments (not idempotent).</summary>
    public static uint NextChannelNumber(Session session) => (uint)SessionNextChannelNumberProperty.GetValue(session)!;

    public static void SendMessage(Session session, Message message) =>
        SessionSendMessageMethod.Invoke(session, [message]);

    /// <summary>
    /// Creates and opens a new "session"-type channel. Split from
    /// <see cref="SendShellRequests"/> specifically so a caller can subscribe to
    /// <see cref="SubscribeDataReceived"/> immediately after this returns — before any channel
    /// request is sent — so no early channel data (in practice: shell output arriving right after
    /// "shell-req" succeeds) can be lost in the gap between that request returning and the caller
    /// wiring up its own subscription.
    /// </summary>
    public static object CreateAndOpenChannel(Session session)
    {
        var channel = CreateChannelSessionMethod.Invoke(session, null)!;
        ChannelOpenMethod.Invoke(channel, null);
        return channel;
    }

    /// <summary>
    /// Sends "pty-req", optionally "x11-req" (before "shell" so the remote shell inherits
    /// <c>DISPLAY</c>), then "shell" — the same request order <see cref="ShellStream"/> uses
    /// internally for pty-req/shell-req, with x11-req spliced in between when requested. Throws if
    /// any <c>want_reply</c> request the server was asked to acknowledge came back with
    /// <c>CHANNEL_FAILURE</c>.
    /// </summary>
    public static void SendShellRequests(object channel, string terminalName, uint columns, uint rows, X11ForwardingParameters? x11)
    {
        var ptyOk = (bool)SendPseudoTerminalRequestMethod.Invoke(channel,
            [terminalName, columns, rows, 0u, 0u, null])!;
        if (!ptyOk)
            throw new SshException("The server rejected the pseudo-terminal request.");

        if (x11 is not null)
        {
            var x11Ok = (bool)SendX11ForwardingRequestMethod.Invoke(channel,
                [x11.SingleConnection, x11.AuthenticationProtocol, x11.AuthenticationCookieHex, x11.ScreenNumber])!;
            if (!x11Ok)
                throw new SshException("The server rejected the X11 forwarding request " +
                    "(X11Forwarding may be disabled in its sshd_config).");
        }

        var shellOk = (bool)SendShellRequestMethod.Invoke(channel, null)!;
        if (!shellOk)
            throw new SshException("The server rejected the shell request.");
    }

    public static void SendWindowChangeRequest(object channel, uint columns, uint rows, uint width, uint height) =>
        SendWindowChangeRequestMethod.Invoke(channel, [columns, rows, width, height]);

    public static void SendChannelData(object channel, byte[] data) =>
        ChannelSendDataMethod.Invoke(channel, [data]);

    public static void CloseChannel(object channel) => ChannelCloseMethod.Invoke(channel, null);

    /// <summary>
    /// Subscribes to the channel's <c>DataReceived</c> event. The event-arg type
    /// (<c>ChannelDataEventArgs</c>) is internal, so the handler receives it as <see langword="object"/>
    /// and reads its (public) <c>Data</c> property (an <c>ArraySegment&lt;byte&gt;</c>) via reflection —
    /// confirmed live this delivers real terminal output. Returns an unsubscribe action.
    /// </summary>
    public static Action SubscribeDataReceived(object channel, Action<ArraySegment<byte>> onData)
    {
        PropertyInfo? dataProp = null;
        void Handler(object? sender, object e)
        {
            dataProp ??= e.GetType().GetProperty("Data")!;
            onData((ArraySegment<byte>)dataProp.GetValue(e)!);
        }

        Action<object?, object> typedHandler = Handler;
        var del = Delegate.CreateDelegate(ChannelDataReceivedEvent.EventHandlerType!, typedHandler.Target, typedHandler.Method);
        ChannelDataReceivedEvent.AddEventHandler(channel, del);
        return () => ChannelDataReceivedEvent.RemoveEventHandler(channel, del);
    }

    public static Action SubscribeClosed(object channel, Action onClosed)
    {
        void Handler(object? sender, object e) => onClosed();
        Action<object?, object> typedHandler = Handler;
        var del = Delegate.CreateDelegate(ChannelClosedEvent.EventHandlerType!, typedHandler.Target, typedHandler.Method);
        ChannelClosedEvent.AddEventHandler(channel, del);
        return () => ChannelClosedEvent.RemoveEventHandler(channel, del);
    }

    private static EventInfo? FindEventOnBaseTypes(Type t, string name)
    {
        for (var cur = t; cur is not null; cur = cur.BaseType)
        {
            var e = cur.GetEvent(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (e is not null) return e;
        }
        return null;
    }

    private static MethodInfo? FindMethodOnBaseTypes(Type t, string name, params Type[] paramTypes)
    {
        for (var cur = t; cur is not null; cur = cur.BaseType)
        {
            var m = cur.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, paramTypes, null);
            if (m is not null) return m;
        }
        return null;
    }
}

/// <summary>Parameters for the "x11-req" channel request (RFC 4254 §6.3.1).</summary>
/// <param name="SingleConnection">If true, the server only opens one X11 channel back for this request rather than one per remote X client connection.</param>
/// <param name="AuthenticationProtocol">Almost always "MIT-MAGIC-COOKIE-1".</param>
/// <param name="AuthenticationCookieHex">
/// The ASCII hex text of the cookie (e.g. 32 hex chars for a 16-byte cookie) — confirmed live this is
/// what the wire format expects (a real sshd rejected a raw-byte cookie with "Invalid X11 forwarding
/// data"; the hex-encoded form was accepted). <b>Known simplification, not real xauth substitution:</b>
/// PolyTerm does not read the local machine's real xauth cookie or rewrite it into inbound X11
/// connections the way <c>ssh -X</c> does — see <see cref="X11Forwarder"/>'s doc comment.
/// </param>
/// <param name="ScreenNumber">Almost always 0.</param>
internal sealed record X11ForwardingParameters(bool SingleConnection, string AuthenticationProtocol, byte[] AuthenticationCookieHex, uint ScreenNumber);
