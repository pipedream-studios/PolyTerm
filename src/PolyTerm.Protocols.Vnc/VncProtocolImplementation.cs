using MarcusW.VncClient.Protocol;
using MarcusW.VncClient.Protocol.Implementation;
using MarcusW.VncClient.Protocol.Services;

namespace PolyTerm.Protocols.Vnc;

/// <summary>
/// The library's default RFB implementation with its two background threads replaced (issue #374).
/// Every connection must use this: with the library's own threads, closing it crashes or hangs.
/// </summary>
internal sealed class VncProtocolImplementation : DefaultImplementation
{
    public VncProtocolImplementation()
    {
    }

    /// <summary>For tests that need a message type of their own to reach the send or receive loop.</summary>
    internal VncProtocolImplementation(MessageTypesCollectionBuilderDelegate messageTypes)
        : base(GetDefaultSecurityTypes, messageTypes, GetDefaultEncodingTypes)
    {
    }

    public override IRfbMessageReceiver CreateMessageReceiver(RfbConnectionContext context) => new VncMessageReceiver(context);

    public override IRfbMessageSender CreateMessageSender(RfbConnectionContext context) => new VncMessageSender(context);
}
