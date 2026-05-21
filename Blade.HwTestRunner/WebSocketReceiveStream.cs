using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Blade.HwTestRunner;

/// <summary>
/// Exposes websocket binary frames as a read-only byte stream for fixture parsing.
/// </summary>
internal sealed class WebSocketReceiveStream(WebSocket webSocket, string loaderName) : Stream
{
    private const int ReceiveBufferSize = 4096;

    private readonly WebSocket webSocket = webSocket ?? throw new ArgumentNullException(nameof(webSocket));
    private readonly string loaderName = loaderName ?? throw new ArgumentNullException(nameof(loaderName));
    private readonly byte[] receiveBuffer = new byte[ReceiveBufferSize];
    private int receiveBufferOffset;
    private int receiveBufferCount;
    private bool endOfStream;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer, nameof(buffer));
        return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
            return ValueTask.FromResult(0);

        int copied = CopyBufferedBytes(buffer);
        if (copied > 0)
            return ValueTask.FromResult(copied);

        if (this.endOfStream)
            return ValueTask.FromResult(0);

        return ReadCoreAsync(buffer, cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            await ReceiveNextChunkAsync(cancellationToken);

            int copied = CopyBufferedBytes(buffer);
            if (copied > 0)
                return copied;

            if (this.endOfStream)
                return 0;
        }
    }

    private int CopyBufferedBytes(Memory<byte> destination)
    {
        int copyLength = Math.Min(destination.Length, this.receiveBufferCount);
        if (copyLength == 0)
            return 0;

        this.receiveBuffer.AsMemory(this.receiveBufferOffset, copyLength).CopyTo(destination);
        this.receiveBufferOffset += copyLength;
        this.receiveBufferCount -= copyLength;
        if (this.receiveBufferCount == 0)
            this.receiveBufferOffset = 0;

        return copyLength;
    }

    private async ValueTask ReceiveNextChunkAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await this.webSocket.ReceiveAsync(this.receiveBuffer.AsMemory(), cancellationToken);
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                this.endOfStream = true;
                throw new FixtureException($"{this.loaderName} closed the websocket without a complete close handshake.", ex);
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                if (result.Count == 0)
                    continue;

                this.receiveBufferOffset = 0;
                this.receiveBufferCount = result.Count;
                return;
            }

            if (result.MessageType == WebSocketMessageType.Text)
                throw new FixtureException($"Unexpected text frame from {this.loaderName}.");

            this.endOfStream = true;
            WebSocketCloseStatus? closeStatus = this.webSocket.CloseStatus;
            string? closeStatusDescription = this.webSocket.CloseStatusDescription;

            if (closeStatus == WebSocketCloseStatus.PolicyViolation)
            {
                string description = string.IsNullOrWhiteSpace(closeStatusDescription)
                    ? "p2aas policy violation"
                    : closeStatusDescription;
                throw new TimeoutException($"p2aas timed out: {description}");
            }

            if (closeStatus is null or WebSocketCloseStatus.NormalClosure)
                return;

            string closeDescription = string.IsNullOrWhiteSpace(closeStatusDescription)
                ? "no description"
                : closeStatusDescription;
            throw new FixtureException($"p2aas closed the websocket with status {closeStatus}: {closeDescription}");
        }
    }
}