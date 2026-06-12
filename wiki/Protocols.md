# Protocols

## Common frame
All protocol messages ride a secure transport connection and use a fixed 12-byte header. Integers are big-endian (network byte order).

```text
0..3   clientId      uint32
4..7   messageId     uint32
8..11  payloadLength uint32
12..   payload       byte[payloadLength]
```

Max application payload is 4KB. Payload is serialized as protobuf (`SocketCommon/Proto/SocketMessages.proto`), not JSON.

Connections between `SocketClient`, `SocketServer`, `SocketControl`, `SocketDashboard` are protected by `SecureSocketConnection` before exchanging common frames. See [Configuration → Security](Configuration.md#security) and [Architecture → Security profiles](Architecture.md#security-profiles) for profiles.

### Transport security
- **`Tls` (default)**: `SslStream`. The `EndToEndTls` profile supports mTLS when `requireClientCertificate=true`: a local Root CA signs per-module leaf certs and each side validates the peer against the same Root CA. With `SOCKET_REQUIRE_TLS13=true`, a non-TLS-1.3 negotiation result fails the connection. Certificate validation checks Root CA signature, server cert SAN/name match, and serverAuth/clientAuth EKU. By default, the SocketClient certificate authenticates the client app/device group while `CLIENT_REGISTER` assigns the logical `clientId`; set `enforceClientCertificateId=true` only for deployments that issue one certificate per client ID.
- **`MessageEncryption`**: no TLS handshake; each frame payload is protected. Wire payload envelope:
  ```text
  0      version     byte
  1..12  nonce       byte[12]
  13..28 aesGcmTag   byte[16]
  29..N  cipherText  byte[payloadLength]
  N..    hmacSha256  byte[32]
  ```
  AES-GCM uses `clientId + messageId` as associated data; HMAC-SHA256 covers the wire header + envelope. The receiver verifies/decrypts and restores the original 12-byte frame, so the upper protobuf protocol is unchanged. In this mode, header and payload reads use the same caller-provided read timeout.

**Message-byte counting basis**: `total_received_message_bytes` / `total_sent_message_bytes` (heartbeat → snapshot → dashboard) count healthcheck-excluded message frame wire bytes (`12-byte header + payload`). Payload is **plaintext** (receive measured after unprotect, send measured before Protect); the `MessageEncryption` envelope (version·nonce·tag·HMAC) and TLS record overhead are not included — i.e., the same logical message size regardless of transport mode.

## HealthCheck
```text
1 PING
2 PONG
```
`PING`/`PONG` use `ProtoHealthCheckMessage`; `PONG.status` is `OK`. The client runs a keepalive via `StartHealthCheckLoop()` (default 30s); it waits ≥250ms for a pong and, after 3 consecutive failures, closes the connection as unhealthy. The SocketServer refreshes session activity on any valid frame (including `PING`); if healthcheck stops and `idleTimeoutSeconds` is exceeded, the cleanup scheduler closes the client session and propagates session close to the ControlServer.

## Client Register

```text
2000 CLIENT_REGISTER
2001 CLIENT_REGISTER_ACK
```

When an existing connection for the same `clientId` is still alive, SocketServer keeps the existing connection and rejects the new register request. It returns `CLIENT_REGISTER_ACK.success=false` with `retryAfterSeconds=<server idle timeout>`, then closes only the new connection. After the existing connection is removed by healthcheck/idle cleanup, a new register request for the same `clientId` is accepted.

## HelloWorld
```text
100 HelloWorldRequest
101 HelloWorldResponse
```
Protobuf payloads; `HelloWorldResponse.message` defaults to `Hello, World!`.

## Client message
Client-to-client messages go through the SocketServer: local delivery if the target is on the same server, else server-to-server relay.
```text
2000 CLIENT_REGISTER
2001 CLIENT_REGISTER_ACK
2002 CLIENT_MESSAGE_SEND
2003 CLIENT_MESSAGE_DELIVER
2004 CLIENT_MESSAGE_ACK
2005 CLIENT_MESSAGE_ERROR
```
`CLIENT_MESSAGE_SEND` payload (`ProtoClientMessageSendRequest`): `message_token`, `source_client_id`, `target_client_id`, `content`, `ttl_seconds`, `created_at_unix_ms`. `CLIENT_MESSAGE_DELIVER` carries source/target/content to the target; the source receives `CLIENT_MESSAGE_ACK` or `CLIENT_MESSAGE_ERROR`.

## Server relay
SocketServer-to-SocketServer delivery connects directly to the target server's listen endpoint over TLS. Each source server keeps a persistent per-endpoint relay channel pool with at least 2 channels, so cross-server messages do not serialize behind a single send lock during bursts.
```text
2100 SERVER_RELAY_MESSAGE
2101 SERVER_RELAY_ACK
2102 SERVER_RELAY_ERROR
2103 SERVER_RELAY_BATCH
2104 SERVER_RELAY_BATCH_RESULT
```
The source server first tries local delivery. If the target is remote, it checks the in-process client-location cache and sends a targeted relay on a cache hit. Cache misses go to ControlServer client-location lookup; successful lookups populate the cache. If targeted relay fails because the cached or resolved target no longer has the client, the cache entry is invalidated and the server falls back to lookup/broadcast. Broadcast remains the final fallback across the healthy relay-server snapshot, and a successful broadcast ACK can also populate the cache.

Repeated SocketServer-to-SocketServer relay messages are coalesced for a short flush interval and sent as `SERVER_RELAY_BATCH`; the receiver processes each item and returns one `SERVER_RELAY_BATCH_RESULT` containing per-message success/error entries. Single-message relay remains supported for compatibility.

## Control plane
ControlServer/SocketServer/SocketClient use protobuf payloads over the common frame.

Server-Control:
```text
1000 SERVER_REGISTER       1004 SERVER_HEARTBEAT_ACK
1001 SERVER_REGISTER_ACK   1100 SESSION_OPENED
1002 SERVER_HEARTBEAT      1101 SESSION_UPDATED
1003 SERVER_UNREGISTER     1102 SESSION_CLOSED
```
`SERVER_HEARTBEAT` carries connection counters, resource usage, and traffic counters; the ControlServer stores them in `BackendServerSnapshot` and propagates via peer snapshot and the Dashboard status API. `REGISTRY_SNAPSHOT_RESPONSE` also includes the responding ControlServer's own `control_server_resource_usage` (shown as the ControlServer inventory row's CPU/memory/storage).

Client-Control:
```text
1200 ROUTE_REQUEST            1210 CLIENT_LOCATION_REQUEST
1201 ROUTE_RESPONSE           1211 CLIENT_LOCATION_RESPONSE
1202 ROUTE_RESOLVE_FAILED     1212 CLIENT_LOCATION_NOT_FOUND
```

Control-Control:
```text
1400 CONTROL_REGISTER      1420 SESSION_SUMMARY_UPSERT    1430 ROUTE_RESERVATION_UPSERT
1401 CONTROL_REGISTER_ACK  1421 SESSION_SUMMARY_REMOVE    1431 ROUTE_RESERVATION_RELEASE
1402 CONTROL_HEARTBEAT     1422 CLIENT_LOCATION_UPSERT     1440 REGISTRY_SNAPSHOT_REQUEST
1410 SERVER_REGISTRY_UPSERT 1423 CLIENT_LOCATION_REMOVE    1441 REGISTRY_SNAPSHOT_RESPONSE
1411 SERVER_REGISTRY_REMOVE
```

### Server heartbeat
A closed SocketServer request channel is not treated as an immediate fault; only heartbeat-timeout snapshots are normalized to `Unhealthy` by the cleanup scheduler and excluded from routing. `ProtoServerHeartbeatRequest` fields: `cluster_id`, `server_id`, `instance_id`, `host`, `port`, `health`, `max_connections`, `current_connections`, `reserved_connections`, `available_connections`, `resource_usage`, `total_accepted_clients`, `total_closed_clients`, `total_rejected_clients`, `total_idle_timeout_clients`, traffic/byte counters, `sent_at_unix_ms`.

### Route
`ProtoRouteRequest`: `client_id`, `preferred_server_id?`, `routing_policy`.
`ProtoRouteResponse`: `success`, `reservation_id`, `server_id`, `instance_id`, `host`, `port`, `expires_at_unix_ms`, `error_message`. The client opens a direct TCP connection to the returned endpoint. Routing policy: see [Architecture → ControlServer](Architecture.md#controlserver).
