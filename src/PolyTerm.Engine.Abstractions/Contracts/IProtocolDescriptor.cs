namespace PolyTerm.Engine;

/// <summary>
/// Describes a protocol module (SSH, local shell, RDP, ...) and creates sessions for it. One
/// descriptor is registered per protocol via <see cref="IProtocolRegistry"/>; adding protocol N+1
/// means adding one project that implements this interface — no changes to the App, session
/// manager, or storage layer.
/// </summary>
public interface IProtocolDescriptor
{
    /// <summary>Stable identifier stored in <see cref="ConnectionProfile.ProtocolId"/>, e.g. "ssh".</summary>
    string Id { get; }

    /// <summary>Human-readable name shown in the UI, e.g. "SSH".</summary>
    string DisplayName { get; }

    /// <summary>Default port to prefill in the connection editor. 0 when not applicable (serial, local shell).</summary>
    int DefaultPort { get; }

    /// <summary>Flags describing what this protocol can do — drives which UI affordances are shown.</summary>
    ProtocolCapabilities Capabilities { get; }

    /// <summary>What kind of surface the App must provide to render a session of this protocol.</summary>
    SurfaceKind Surface { get; }

    /// <summary>Schema describing this protocol's configurable settings, for driving an auto-generated settings UI.</summary>
    SettingsSchema Settings { get; }

    /// <summary>Creates a new, not-yet-connected session for this protocol from a resolved context.</summary>
    IProtocolSession CreateSession(SessionContext ctx);

    /// <summary>
    /// Whether this protocol can actually be used in the current environment — false when a required
    /// native component isn't present on this OS (e.g. RDP's native control is Windows-only until a
    /// cross-platform shim exists). The App uses this to grey out the connect affordance and show
    /// <paramref name="reason"/>, rather than letting <see cref="CreateSession"/> fail at connect time.
    /// Protocols with no environmental dependency never need to override this.
    /// </summary>
    bool IsAvailable(out string? reason)
    {
        reason = null;
        return true;
    }
}
