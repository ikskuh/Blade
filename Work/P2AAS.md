
## WebSocket protocol

### Endpoint

Connect to:

```text
ws://p2aas.fritz.box:12880/
```

The implementation accepts query parameters on the same URL:

- `baudrate`: serial baud rate to use after the upload succeeds. Default: `115200`.
- `timeout_ms`: how long the post-upload serial bridge is allowed to run. Default: `2500`. Allowed range: `100` to `10000`.

Examples:

```text
ws://127.0.0.1:12880/
ws://127.0.0.1:12880/?baudrate=230400
ws://127.0.0.1:12880/?baudrate=2000000&timeout_ms=5000
```

If either query parameter is invalid, the server responds with HTTP `400` and a plain-text error message instead of upgrading to WebSocket.

### Upload phase

The client first uploads the Propeller image. The simplest and intended form is a single binary WebSocket message with this layout:

```text
+----------------------+-------------------+
| 4-byte little-endian | payload bytes     |
| payload length       |                   |
+----------------------+-------------------+
```

Rules enforced by the server:

- The first message must be binary.
- The 4-byte prefix is an unsigned little-endian payload length.
- The payload length must be greater than zero.
- The payload length must not exceed `524288` bytes.
- The actual payload length must exactly match the length prefix.
- The payload length must be divisible by 4.

The current implementation also accepts a two-message variant:

1. First binary message: only the 4-byte little-endian payload length.
2. Second binary message: exactly `payload_length` bytes.

In practice, the one-message form is the easiest client protocol and is what the example client uses.

The payload itself is the raw Propeller image. The client does not need to send Propeller boot-loader commands, Base64, or the final checksum word. The server handles that part internally.

### Loader phase

After the payload is received, the server performs roughly this boot-loader sequence on the serial port:

```text
reset board via DTR
send "> "
send "Prop_Txt 0 0 0 0 " + base64(payload + checksum) + " ?\r"
expect "." on success
```

If the loader returns `!`, the server treats that as a checksum failure. Any other unexpected response is treated as a protocol error.

### Runtime bridge phase

Once the upload succeeds, the WebSocket stops being a request-response protocol and becomes a byte pipe:

- Client to server: bytes received from the WebSocket are written to the serial port.
- Server to client: bytes read from the serial port are sent back as binary WebSocket messages.

Message boundaries are not meaningful after upload. Clients should treat the connection as a continuous byte stream carried over WebSocket frames.

### Closure and errors

At a high level, the connection ends like this:

- Invalid HTTP request or invalid query parameters: HTTP `400`, no WebSocket upgrade.
- Malformed upload or wrong WebSocket message type during upload: WebSocket `ProtocolError`.
- User-code runtime exceeded `timeout_ms`: WebSocket `PolicyViolation`.
- Unexpected server-side failure: WebSocket `InternalServerError`.
- Clean end of session: WebSocket `NormalClosure`.

## Minimal client flow

From a client's point of view, the session looks like this:

1. Open `ws://HOST:12880/` with optional `baudrate` and `timeout_ms` query parameters.
2. Send one binary message containing `uint32_le(payload_length) + payload_bytes`.
3. Wait for the upload to complete.
4. Exchange raw bytes with the uploaded program until the socket closes.

The example client in [example/example.py](example/example.py) follows exactly that pattern.
