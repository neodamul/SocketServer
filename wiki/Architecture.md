# Architecture

## Overview
SocketServer scales a TCP backend across multiple instances. A ControlServer aggregates server state and tells clients which SocketServer to connect to. The ControlServer never proxies TCP payload — long-lived connections are held directly between client and SocketServer over an authenticated [secure connection](Protocols.md).

```text
Client -> nginx:10000     ROUTE_REQUEST
nginx  -> ControlServer    TCP stream proxy to 10001/10002
Client <- ControlServer    ROUTE_RESPONSE(host, port)
Client -> SocketServer     direct TCP connection
```

Client-to-client messaging also avoids payload proxying through the ControlServer: it only resolves the target client's location; payload moves via SocketServer-to-SocketServer relay.

```text
SourceClient -> SourceServer    CLIENT_MESSAGE_SEND
SourceServer -> ControlServer   CLIENT_LOCATION_REQUEST
SourceServer <- ControlServer   CLIENT_LOCATION_RESPONSE(target server)
SourceServer -> TargetServer    SERVER_RELAY_MESSAGE
TargetServer -> TargetClient    CLIENT_MESSAGE_DELIVER
SourceServer -> SourceClient    CLIENT_MESSAGE_ACK
```

## Projects
```text
SocketCommon      shared frame, protocols, config, diagnostics, logging
SocketClient      client library
SocketControl     broker and cluster registry
SocketServer      TCP backend server
SocketDashboard   status API and web dashboard
SocketLoadTest    load test tool
SocketTests       MSTest suite
Samples           native sample clients for .NET, iOS, macOS, Android
```

## ControlServer
Responsibilities:
- Receive SocketServer `SERVER_REGISTER` and `SERVER_HEARTBEAT`.
- Receive session opened/updated/closed events and client-location upsert/remove.
- Handle client-location requests and client `ROUTE_REQUEST`.
- Detect SocketServer heartbeat timeout; track per-server max/current/reserved/available connections and CPU/MEM/STORAGE usage.
- Sync registry/session/reservation to peer ControlServers.

Default routing policy `MostAvailableConnections`:
```text
availableConnections = maxConnections - currentConnections - reservedConnections
```
Candidate conditions: `Health == Healthy`, heartbeat not timed out, `availableConnections > 0`. Selection prefers highest `availableConnections`, then lowest `currentConnections`; ties are broken randomly within the tied group. Protocol codes: see [Protocols → Route](Protocols.md#route).

Client `ROUTE_REQUEST` connections are one-shot: ControlServer sends `ROUTE_RESPONSE` and closes the route lookup connection immediately. The client then opens its long-lived connection directly to the assigned SocketServer endpoint. This keeps the nginx broker stream short-lived during large client ramps.

## SocketServer
Binds an available port in its configured range, registers with the ControlServer, and sends periodic heartbeats. It reports register/heartbeat/session events directly to all configured ControlServers. In a 2-node active-active setup it sends to A/B in parallel and, once at least one report succeeds, continues after a short grace so a slow endpoint can't stall the call path; remaining sends record success/failure under their own operation timeout. The heartbeat loop retries on the next cycle after exceptions and periodically re-sends register metadata to recover from ControlServer restart or registry loss. A closed request channel alone does not mark a server `Unhealthy`; only heartbeat-timeout servers are excluded (by the cleanup scheduler) from new routes and client-location candidates. On reconnect, register/heartbeat/session-update restore state.

Heartbeat CPU/MEM/STORAGE are **machine-wide** usage of the host running the SocketServer (not the single process): Linux `/proc`, macOS Mach host statistics/sysctl, Windows system memory/time APIs.

On a client `CLIENT_REGISTER` (or any message), the SocketServer updates its `clientId -> session` index and propagates session/client location to the ControlServer. Connection close, read failure, idle timeout from stopped healthcheck, and delivery send failure all normalize to a session close → location-remove event.

Right after register, the SocketServer warms its relay-server list from the ControlServer registry snapshot and refreshes it on a throttled interval during heartbeats (no snapshot TLS request per heartbeat). When a client message has no local target, it uses an in-process client-location cache first, then ControlServer target-location lookup, then broadcast fallback across the cached healthy SocketServer list. Successful lookup/broadcast results populate the cache; stale targeted results invalidate it. Session events are sent by `ControlServerReporter` through a bounded queue to cap task/memory growth.

### Worker isolation
ControlServer and SocketServer separate request/response and relay handling into dedicated queues and long-running workers. Listener/receive loops focus on accept + frame read; command workers handle business commands; multiple response workers do the actual socket writes. ControlServer command/response worker counts scale from CPU count instead of staying fixed at four workers, so route bursts are not serialized through a tiny worker set. Response sending is parallel, but responses to the **same connection are serialized via a per-connection FIFO send queue** to preserve frame order, so one slow connection can't block the whole response queue. Server-to-server relay uses separate request/response queues so a slow peer / timeout / TLS handshake delay doesn't cascade into heartbeat or the next command. ControlServer peer relay/snapshot and SocketServer→ControlServer lookup keep per-endpoint persistent secure channels (no TCP/TLS handshake per request on the healthy path). ControlServer peer relay and SocketServer↔SocketServer relay coalesce repeated queue items over a short flush interval and send protobuf batch frames with per-item results, reducing repeated send/ack overhead during bursts. SocketServer↔SocketServer relay keeps a per-endpoint persistent channel pool with at least 2 channels, reducing the single-channel send lock bottleneck under cross-server message bursts while keeping the relay connection count bounded. Relay channels are accepted by the target SocketServer as normal server connections, so production capacity planning should reserve headroom for `sourceServerCount * targetEndpointCount * relayChannelCount` control-plane sessions in addition to client sessions. Only channels with a fault/timeout/send-receive failure are closed and reconnected. Dynamic relay-channel scaling is deferred until queue-depth, relay TPS, and p95/p99 latency metrics justify it; connected-client count alone does not determine relay channel count. The SocketClient healthcheck loop and SocketServer reporter loop run on shared dedicated workers, isolated from ThreadPool contention.

### Default server policy
- `maxConnections`: 10,000 · `pendingAcceptCount`: 100 · `idleTimeoutSeconds`: 90
- healthcheck interval: 30s · unhealthy-connection cleanup scan: 10s
- `SocketAsyncEventArgs` (SAEA) pool: initial 1,000, grows by 100
- SAEA receive buffer: fixed 8KB segments pre-allocated as slabs and retained on each SAEA instance
- SAEA pool counters increment/decrement on retained/in-use objects so discarded overflow objects don't linger in dashboard metrics

Console hosts catch Ctrl+C / termination to clean up reporter and server sockets via `ControlServer.StopAsync`, `TcpServer.EndAsync`, `TcpClient.DisconnectAsync` (listener, accept loop, active sockets, healthcheck loop, sessions in order).

SAEA buffers are managed as a memory-map-style slab pool suited to socket I/O (not a file-backed `MemoryMappedFile`). Each SAEA keeps a fixed byte-buffer segment across reuse. The receive path can reference the segment directly via `ReceiveMappedAsync`; legacy `ReceiveExactAsync` copies once into a result array for compatibility. Accept runs with the buffer cleared so initial TLS handshake data isn't consumed into the accept buffer, restoring the segment on return.

## ControlServer active-active
ControlServers share registry data via the `peers` setting. Server register/heartbeat, session open/update/close, and route-reservation changes are relayed to peers immediately, and a full snapshot is re-fetched every `peerSnapshotSyncIntervalSeconds` to recover missed events. In a 2-node setup the SocketServer reports to A/B directly, so peer relay is a secondary correction path. Server-snapshot merge uses `LastHeartbeatAt`/`UpdatedAt` (latest heartbeat), not a per-ControlServer local version. The registry can persist server snapshots, session summaries, and client locations to a file store, or use `InMemory` for tests/ephemeral runs. Route reservations are short-lived routing hints; they are kept in memory and peer-relayed, but excluded from file persistence so a client ramp does not amplify into one full registry write per route request or restore stale capacity holds after restart.

Session tombstones only suppress late events from a previous connection. A new session with the same SocketServer session id is accepted when its `ConnectedAt` is newer than the tombstone, so high-load reconnects or session-slot reuse cannot be permanently counted as stale.

ControlServer report channels are long-lived. The frame-header read timeout is the larger of the generic socket read timeout and `heartbeatTimeoutSeconds + 5s`, so a normal 30s SocketServer heartbeat is not closed as idle by a 30s read timeout race. The cleanup scheduler still owns unhealthy connection cleanup and heartbeat expiry.

Sync message codes: `SERVER_REGISTRY_UPSERT`, `SESSION_SUMMARY_UPSERT/REMOVE`, `ROUTE_RESERVATION_UPSERT/RELEASE`, `REGISTRY_SNAPSHOT_REQUEST/RESPONSE` (see [Protocols → Control plane](Protocols.md#control-plane)).

A ControlServer outage does not drop already-connected client↔SocketServer sessions; clients can re-request a route by iterating the configured ControlServer endpoints. On restart, a ControlServer loads the saved registry but normalizes heartbeat-timed-out servers to `Unhealthy` (excluded from routing), drops expired reservations, and recovers on the next register/heartbeat. If the same SocketServer instance re-registers with a newer `StartedAt`, leftover session summaries / client locations / reservations from the previous run are removed to avoid double-counting connections on the dashboard.

## Security profiles
See [Configuration → Security](Configuration.md#security) for keys.
- **`EndToEndTls`** (default): keeps TLS to the app process; enables mTLS when `requireClientCertificate=true`; verifies server cert SAN/name and serverAuth/clientAuth EKU. By default SocketClient can use one shared trusted client certificate and register a logical `clientId`; strict certificate-to-clientId binding is optional via `enforceClientCertificateId=true`. For very large fan-out (e.g. 300k), place SocketServer nodes behind an L4 pass-through / TCP stream proxy and size shard count from measured per-node SslStream memory (see [Operations → Scale notes](Operations.md#scale-notes)).
- **`EdgeTerminated`**: an L7 edge terminates TLS/client auth; the internal trusted network uses non-TLS transport (SAEA send/receive path). Requires `trustedNetwork=true` and a loopback/private `bindHost`. Identity propagation (PROXY protocol / edge token) is not yet implemented, so this profile does not currently bind the edge-authenticated client identity to the SocketServer session.
- **`AppTokenSession`**: reserved for a per-session-key secure channel (replay protection, key rotation, downgrade protection, frame binding). Rejected fail-fast at startup until implemented.

## Dashboard
The Dashboard queries the configured ControlServers and shows them as `ControlServer` rows in the Server Inventory. When multiple ControlServers respond, it merges the latest heartbeat snapshot per SocketServer `instanceId`. ControlServer snapshot responses also include the CPU/memory/storage usage of the host running that ControlServer; SocketServer resource usage comes from the heartbeat snapshot. The Dashboard's own server is always shown as a `Dashboard` row regardless of ControlServer connectivity; with no ControlServer it uses the local Dashboard server as a fallback cluster. On transient ControlServer timeout/unavailable, the last good cluster snapshot and per-endpoint counters are retained so the list doesn't vanish. Selecting a row updates the Server / Traffic / Socket Runtime / Details panels for that server. The browser UI refreshes the status API every 30s by default (selectable 5/10/30/60s).

The SocketServer heartbeat propagates accepted/closed/rejected/idle counters plus received/sent message counters and message-byte counters into the ControlServer registry snapshot, shown per server in the Dashboard's Selected Traffic. Message-byte counters exclude healthcheck Ping/Pong and measure the **application-level plaintext frame** (12-byte header + plaintext payload), independent of transport mode — see [Protocols → Common frame](Protocols.md#common-frame) for the exact basis.

Displayed: total capacity, current connections, available connections; ControlServer/SocketServer/Dashboard counts; per-type endpoints and status; per-server health and endpoint; machine CPU/MEM/STORAGE. Operational APIs: `GET /api/server/status`, `/health/live`, `/health/ready`, `/metrics` (see [Operations](Operations.md)).
