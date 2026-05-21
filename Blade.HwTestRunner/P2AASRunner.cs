using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Threading;

namespace Blade.HwTestRunner;

internal sealed class P2AASRunner : Runner
{
    internal P2AASRunner(RunnerConfiguration configuration)
        : base(configuration)
    {
    }

    protected override P2Transport CreateTransport(byte[] testBinary)
    {
        ArgumentNullException.ThrowIfNull(testBinary);

        byte[] loadableBinary = PadToLongBoundary(testBinary);
        P2AasSessionSettings session = ResolveP2AasSession();
        return new P2AASTransport(session, loadableBinary);
    }

    private P2AasSessionSettings ResolveP2AasSession()
    {
        Uri endpoint = ParseP2AasEndpoint();
        bool hasTimeoutQuery = TryGetQueryParameter(endpoint, "timeout_ms", out string? queryTimeoutText);
        int endpointTimeoutMs = this.TimeoutMs;
        if (hasTimeoutQuery
            && int.TryParse(queryTimeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedTimeoutMs)
            && parsedTimeoutMs > 0)
        {
            endpointTimeoutMs = parsedTimeoutMs;
        }

        Uri effectiveEndpoint = hasTimeoutQuery
            ? endpoint
            : AppendQueryParameter(endpoint, "timeout_ms", endpointTimeoutMs.ToString(CultureInfo.InvariantCulture));
        int effectiveTimeoutMs = Math.Max(this.TimeoutMs, endpointTimeoutMs);
        return new P2AasSessionSettings(effectiveEndpoint, effectiveTimeoutMs);
    }

    private Uri ParseP2AasEndpoint()
    {
        if (!Uri.TryCreate(this.PortName, UriKind.Absolute, out Uri? endpoint) || !IsWebSocketScheme(endpoint.Scheme))
            throw new FixtureException($"p2aas requires a WebSocket endpoint URL, but got '{this.PortName}'.");

        return endpoint;
    }

    private static void ConnectToP2Aas(ClientWebSocket webSocket, Uri endpoint)
    {
        try
        {
            webSocket.ConnectAsync(endpoint, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new FixtureException($"Failed to connect to p2aas endpoint '{endpoint}': {ex.Message}", ex);
        }
    }

    private static void SendP2AasUpload(ClientWebSocket webSocket, byte[] loadableBinary)
    {
        byte[] payload = new byte[loadableBinary.Length + sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, checked((uint)loadableBinary.Length));
        Buffer.BlockCopy(loadableBinary, 0, payload, sizeof(uint), loadableBinary.Length);

        try
        {
            webSocket.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new FixtureException($"Failed to upload fixture to p2aas: {ex.Message}", ex);
        }
    }

    private static void CloseWebSocket(WebSocket webSocket)
    {
        try
        {
            if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                webSocket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "fixture complete",
                    CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        catch
        {
        }
    }

    private static void AbortWebSocket(WebSocket webSocket)
    {
        try
        {
            webSocket.Abort();
        }
        catch
        {
        }
    }

    private static bool TryGetQueryParameter(Uri endpoint, string key, out string? value)
    {
        ArgumentNullException.ThrowIfNull(endpoint, nameof(endpoint));
        ArgumentNullException.ThrowIfNull(key, nameof(key));

        string query = endpoint.Query;
        if (query.Length > 0 && query[0] == '?')
            query = query[1..];

        foreach (string entry in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = entry.Split('=', 2);
            string entryKey = Uri.UnescapeDataString(parts[0]);
            if (!string.Equals(entryKey, key, StringComparison.OrdinalIgnoreCase))
                continue;

            value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            return true;
        }

        value = null;
        return false;
    }

    private static Uri AppendQueryParameter(Uri endpoint, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(endpoint, nameof(endpoint));
        ArgumentNullException.ThrowIfNull(key, nameof(key));
        ArgumentNullException.ThrowIfNull(value, nameof(value));

        UriBuilder builder = new(endpoint);
        string existingQuery = builder.Query;
        if (existingQuery.Length > 0 && existingQuery[0] == '?')
            existingQuery = existingQuery[1..];

        string escapedEntry = $"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
        builder.Query = string.IsNullOrEmpty(existingQuery)
            ? escapedEntry
            : $"{existingQuery}&{escapedEntry}";
        return builder.Uri;
    }

    private sealed class P2AASTransport : P2Transport
    {
        private readonly ClientWebSocket webSocket;
        private readonly WebSocketReceiveStream outputStream;

        internal P2AASTransport(P2AasSessionSettings session, byte[] loadableBinary)
            : this(CreateResources(session, loadableBinary))
        {
        }

        private P2AASTransport(P2AasTransportResources resources)
            : base("p2aas", Stream.Null, resources.OutputStream, Stream.Null, resources.TimeoutMs, false)
        {
            this.webSocket = resources.WebSocket;
            this.outputStream = resources.OutputStream;
        }

        internal override void AfterProtocol()
        {
            CloseWebSocket(this.webSocket);
        }

        internal override void Cleanup(bool completedSuccessfully)
        {
            if (!completedSuccessfully)
                AbortWebSocket(this.webSocket);
        }

        public override void Dispose()
        {
            this.outputStream.Dispose();
            this.webSocket.Dispose();
        }

        private static P2AasTransportResources CreateResources(P2AasSessionSettings session, byte[] loadableBinary)
        {
            ClientWebSocket webSocket = new();
            try
            {
                ConnectToP2Aas(webSocket, session.Endpoint);
                SendP2AasUpload(webSocket, loadableBinary);
                WebSocketReceiveStream outputStream = new(webSocket, "p2aas");
                return new P2AasTransportResources(webSocket, outputStream, session.TimeoutMs);
            }
            catch
            {
                webSocket.Dispose();
                throw;
            }
        }
    }

    private sealed record class P2AasSessionSettings(Uri Endpoint, int TimeoutMs);

    private sealed record class P2AasTransportResources(ClientWebSocket WebSocket, WebSocketReceiveStream OutputStream, int TimeoutMs);
}
