// The read loop in this file is adapted from RfbMessageReceiver in MarcusW.VncClient 1.0.0-alpha4,
// which is distributed under the following licence:
//
// MIT License
//
// Copyright (c) 2020 Marcus Wichelmann
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Collections.Immutable;
using MarcusW.VncClient.Protocol;
using MarcusW.VncClient.Protocol.Implementation;
using MarcusW.VncClient.Protocol.Implementation.Services.Communication;
using MarcusW.VncClient.Protocol.MessageTypes;
using MarcusW.VncClient.Protocol.Services;
using MarcusW.VncClient.Utils;
using Microsoft.Extensions.Logging;

namespace PolyTerm.Protocols.Vnc;

/// <summary>
/// Replaces the library's message receiver, which is sealed, so that closing a connection works
/// (issue #374). The original fails in two ways:
/// <list type="bullet">
/// <item>It waits for the next message in a blocking read that cancellation can't interrupt, so on an
/// idle desktop <c>RfbConnection.CloseAsync</c> never completes.</item>
/// <item>A message arriving after cancellation makes it throw <see cref="OperationCanceledException"/>
/// on its own thread, which terminates the app. See <see cref="VncBackgroundLoop"/>.</item>
/// </list>
/// </summary>
internal sealed class VncMessageReceiver : BackgroundThread, IRfbMessageReceiver
{
    private readonly RfbConnectionContext _context;
    private readonly ProtocolState _state;

    // Deliberately the library receiver's category: the app's VNC log filters (issue #343) match on it.
    private readonly ILogger<RfbMessageReceiver> _logger;

    public VncMessageReceiver(RfbConnectionContext context) : base("RFB Message Receiver")
    {
        _context = context;
        _state = context.GetState<ProtocolState>();
        _logger = context.Connection.LoggerFactory.CreateLogger<RfbMessageReceiver>();

        // The connection subscribes too, and treats this as an interruption.
        Failed += (_, args) => _logger.LogWarning(args.Exception, "Receive loop failed.");
    }

    public void StartReceiveLoop()
    {
        _logger.LogDebug("Starting receive loop...");
        Start();
    }

    public Task StopReceiveLoopAsync()
    {
        _logger.LogDebug("Stopping receive loop...");

        // Cancel first, so the loop treats the read error that follows as a normal exit.
        var stopped = StopAndWaitAsync();

        // Only closing the transport wakes a read that's already waiting for the server. The
        // connection is being closed anyway: the library disposes the transport straight after this.
        _context.Transport?.Dispose();
        return stopped;
    }

    protected override void ThreadWorker(CancellationToken cancellationToken) =>
        VncBackgroundLoop.Run(() => ReceiveMessages(cancellationToken), cancellationToken);

    private void ReceiveMessages(CancellationToken cancellationToken)
    {
        var transport = _context.Transport ?? throw new InvalidOperationException("No transport to receive from.");
        var transportStream = transport.Stream;

        var incomingMessageTypes = _context.SupportedMessageTypes!.OfType<IIncomingMessageType>()
            .ToImmutableDictionary(type => type.Id);

        Span<byte> messageTypeBuffer = stackalloc byte[1];

        while (!cancellationToken.IsCancellationRequested)
        {
            if (transportStream.Read(messageTypeBuffer) == 0)
                throw new UnexpectedEndOfStreamException("Stream reached its end while reading next message type.");
            var messageTypeId = messageTypeBuffer[0];

            if (!incomingMessageTypes.TryGetValue(messageTypeId, out var messageType))
                throw new UnexpectedDataException($"Server sent a message of type {messageTypeId} that is not supported by this protocol implementation. "
                    + "Servers should always check for client support before using protocol extensions.");

            _logger.LogDebug("Received message: {Name}({Id})", messageType.Name, messageTypeId);

            if (!messageType.IsStandardMessageType)
                _state.EnsureMessageTypeIsMarkedAsUsed(messageType);

            messageType.ReadMessage(transport, cancellationToken);
        }
    }
}
