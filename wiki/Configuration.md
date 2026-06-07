# Configuration

Each executable project uses a JSON config file:
```text
SocketControl/config.json
SocketServer/config.json
SocketClient/config.json
```
Each project keeps its own `log4net.config`. Executable and test projects copy their `log4net.config` to build output; library projects keep only their config file to avoid conflicts across referencing projects.

`host`, `bindHost`, `controlEndpoints[].host`, `controlServers[].host`, `peers[].host` accept both IP literals (`127.0.0.1`) and DNS names (`localhost`). Connect paths use async DNS resolution and select only an address matching the socket address family.

## Logging
Per-project `log4net.config` files exist for SocketCommon/Client/Control/Server/Dashboard/LoadTest/Tests. `SocketLoadTest` prefers `log4net.load-test.config`, falling back to `log4net.config`. Logs are split per project under `logs/`:
- general: `logs/<project>.log`
- relay: `logs/<project>.relay.log`
General logs cover lifecycle, connection, healthcheck, route, cleanup, test progress. The relay log (`SocketRelay` logger) covers ControlServer peer relay/snapshot sync, SocketServer relay refresh, client-to-client relay, target-location lookups, and broadcast/targeted relay results. Files log down to DEBUG; the console shows INFO+.

## Security
Executable projects apply transport security via `security` (default TLS). Profiles are described in [Architecture → Security profiles](Architecture.md#security-profiles).
```json
{
  "security": {
    "profile": "EndToEndTls",
    "transportMode": "Tls",
    "tlsProtocol": "Tls13",
    "requireTls13": true,
    "requireClientCertificate": true,
    "certificateDirectory": "",
    "certificatePasswordEnvironmentVariable": "SOCKET_CERTIFICATE_PASSWORD",
    "certificateRenewBeforeDays": 30,
    "rootCertificateLifetimeYears": 10,
    "moduleCertificateLifetimeYears": 2,
    "authenticationTimeoutMilliseconds": 30000,
    "messageEncryptionSecretEnvironmentVariable": "SOCKET_MESSAGE_SECRET",
    "trustedNetwork": false
  }
}
```
- `profile`: `EndToEndTls` (default, TLS to the app) · `EdgeTerminated` (edge terminates TLS; requires `trustedNetwork=true` and a loopback/private SocketServer `bindHost`; rejected with `transportMode=Tls`; edge identity propagation not yet implemented) · `AppTokenSession` (reserved, rejected at startup).
- `transportMode`: `Tls` (`SslStream`) or `MessageEncryption` (per-frame AES-GCM-256 + HMAC-SHA256, no TLS handshake). `EdgeTerminated` currently uses the `MessageEncryption` path for non-TLS app transport. See [Protocols → Transport security](Protocols.md#transport-security).
- `MessageEncryption` requires every node and client to share the same secret via env var (default `SOCKET_MESSAGE_SECRET`; override with `messageEncryptionSecretEnvironmentVariable`). The secret may be base64 or plain text; AES-GCM and HMAC keys are derived separately.
- `tlsProtocol`: default `Tls13`; `Auto` uses OS/.NET negotiation. Production forces TLS 1.3. `EndToEndTls` forces mTLS — even if `requireClientCertificate=false`, it is normalized to true. Cert validation checks Root CA signature, SAN/name match, serverAuth/clientAuth EKU; the SocketClient cert SAN includes `socket-client-{clientId}` and the SocketServer refuses a connection whose client-facing frame header clientId differs.

> Note: on macOS, `SslStream` cannot explicitly request `Tls13`; use `Auto` (OS negotiation) for non-server components.

## Socket options
`socketOptions` sets network operation timeouts; missing or ≤0 falls back to 30s.
```json
{ "socketOptions": { "connectTimeoutSeconds": 30, "readTimeoutSeconds": 30, "writeTimeoutSeconds": 30 } }
```
- `connectTimeoutSeconds`: TCP connect limit between ControlServer/SocketServer/client
- `readTimeoutSeconds`: frame header/payload read limit
- `writeTimeoutSeconds`: frame write/flush limit

## Dashboard
`SocketDashboard/appsettings.json` sets ControlServer endpoints to query. `dashboard.controlServers[]` shows multiple ControlServers as `ControlServer` rows with per-endpoint query status; the legacy single `dashboard.controlServer` remains a fallback. A `dashboard.security` section may set the dashboard's transport security (e.g. `tlsProtocol: Auto` on macOS).
```json
{ "dashboard": { "controlServers": [ { "host": "127.0.0.1", "port": 10001 }, { "host": "127.0.0.1", "port": 10002 } ] } }
```

## Certificates
Each module auto-generates local certs on first TLS connection. One Root CA signs per-module leaf certs.
```text
Certificates/SocketServerLocalRootCA.pfx
Certificates/SocketClient.pfx
Certificates/SocketServer.pfx
Certificates/SocketControl.pfx
Certificates/SocketDashboard.pfx
```
Default location is the solution-root `Certificates/`; override with `security.certificateDirectory` or `SOCKET_CERTIFICATE_DIR`. PFX password from the env var named by `security.certificatePasswordEnvironmentVariable` (default `SOCKET_CERTIFICATE_PASSWORD`). Certs are for local dev/test: leaf subject `CN=SocketServerLocal`, SAN `SocketServerLocal`, `localhost`. Force TLS 1.3 with `security.requireTls13=true` or `SOCKET_REQUIRE_TLS13=true` (connection fails if the platform can't negotiate it). Root CA / module certs are deleted and regenerated when near expiry (`certificateRenewBeforeDays`) or unreadable with the current PFX password. The TLS handshake path caches Root CA and module certs in-process to avoid repeated file loads and chain validation; the cache is replaced when `security` is re-applied or the cert file/password/mtime changes; a shared cert instance that an in-progress handshake may reference is not disposed immediately.

## ControlServer
```json
{
  "controlServer": {
    "clusterId": "socket-cluster-1", "nodeId": "control-1",
    "host": "127.0.0.1", "port": 10001, "peerSyncPort": 10021,
    "heartbeatTimeoutSeconds": 90, "peerSnapshotSyncIntervalSeconds": 30,
    "routeReservationSeconds": 10, "routingPolicy": "MostAvailableConnections",
    "degradedCpuPercent": 85, "degradedMemoryPercent": 85, "degradedStoragePercent": 90
  },
  "peers": [],
  "registry": { "provider": "File", "syncMode": "ActiveActive", "connectionString": "control-registry.json" }
}
```
- `port`: client route requests + SocketServer register/heartbeat
- `peerSyncPort`: ControlServer-to-ControlServer sync
- `heartbeatTimeoutSeconds`: timed-out servers excluded from routing
- `peerSnapshotSyncIntervalSeconds`: interval to re-fetch peer full snapshot (recover missed events)
- `routeReservationSeconds`: short reservation TTL between route response and actual connect
- `degraded*Percent`: marks `Degraded` above resource thresholds — evaluated on **machine-wide** usage (not the process)
- `registry.provider`: `InMemory` or `File`; `registry.connectionString`: file path (empty → `{nodeId}-registry.json` in the run dir)

The default config uses `File`; tests/ephemeral runs use `InMemory`. The registry file stores server snapshots, route reservations, session summaries, client locations (payload protocol stays protobuf).

## SocketServer
```json
{
  "socketAsyncEventArgsPool": { "initialSize": 1000, "growthSize": 100, "maxRetained": 20000 },
  "controlServers": [ { "host": "127.0.0.1", "port": 10000 } ],
  "servers": [
    {
      "serverId": 1, "instanceId": "server-1-a", "name": "socket-server-1",
      "bindHost": "127.0.0.1", "portRangeStart": 10100, "portRangeEnd": 10199,
      "maxConnections": 10000, "pendingAcceptCount": 100,
      "idleTimeoutSeconds": 90, "heartbeatIntervalSeconds": 30
    }
  ]
}
```
`portRangeStart=0`/`portRangeEnd=0` uses OS dynamic port binding; production should use an explicit range. `socketAsyncEventArgsPool` tunes the SAEA pool used for accept/send/receive (`initialSize`, `growthSize`, `maxRetained`); each SAEA holds a fixed 8KB receive segment pre-allocated as slabs. Non-TLS data planes (`MessageEncryption`) use SAEA transport for protected frame I/O; `EndToEndTls` keeps TLS record I/O on the .NET TLS stack because `SslStream` owns socket I/O. SAEA returned beyond `maxRetained` is discarded and removed from pool counters, so `/metrics` and the dashboard reflect actual retained/in-use state.

## SocketClient
```json
{
  "client": {
    "clientId": 1, "name": "socket-client-1",
    "controlEndpoints": [ { "host": "127.0.0.1", "port": 10000 } ],
    "healthCheckIntervalSeconds": 30
  }
}
```
`controlEndpoints` may list multiple entries. Route resolution retries configured ControlServer endpoints when TCP connect, TLS authentication, timeout, stream failure, or an unusable route response occurs, so a client can still connect when one ControlServer is down or has no current server snapshot.

## Sample clients
```text
Samples/SocketSample.Net/appsettings.json
Samples/SocketSample.Android/app/src/main/res/raw/config.json
```
Common keys: `clientId`, `clientName`, `host`/`port` (direct SocketServer or ControlServer endpoint), `useControlServer` (route via ControlServer then connect), `receiveTimeoutSeconds`, `healthCheckIntervalSeconds`, `reconnectRetrySeconds` (default 30), `duplicateRejectBackoffSeconds` (default 90), `security`. iOS/macOS samples set `Client ID`, `Host`, `Port`, and self-signed cert acceptance on screen; Swift sample projects are generated by XcodeGen from `Samples/SocketSample.iOS/project.yml` and `Samples/SocketSample.macOS/project.yml`.
