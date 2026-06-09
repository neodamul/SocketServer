# Testing

## Test project
Tests live in `SocketTests`:
```bash
dotnet test SocketTests/SocketTests.csproj
```
Coverage includes:
- frame encode/decode; protobuf payload encode/decode
- healthcheck and HelloWorld protocols
- TLS secure connection, shared local Root CA, module cert creation; cert password env var and optional mTLS handshake
- TLS-disabled MessageEncryption transport (AES-GCM/HMAC)
- SAEA pool and mapped slab receive buffer
- TcpClient/TcpServer basic send/receive; inactive-client cleanup scheduler; port range binding
- ControlServer route; peer registry sync; command/relay queue isolation; periodic peer snapshot sync recovery; dual-endpoint direct report with stalled-endpoint isolation; registry file persistence and stale-heartbeat normalization; stale control-connection cleanup scheduler
- client-to-client local delivery; SocketServer-to-SocketServer relay; persistent relay channel sequential delivery; command/response/relay queue workers; relay list refresh and broadcast fallback; four-server relay fan-out; control-channel disconnect detection
- active-active ControlServer + four SocketServers + platform sample clients concurrent messaging
- graceful shutdown (ControlServer/SocketServer/SocketClient); route reservation release; heartbeat-timeout and degraded-resource route exclusion
- dashboard cluster snapshot; full registration + message send/receive integration; Selected Traffic message-byte counters with healthcheck exclusion; liveness/readiness/metrics models
- sample client settings and client-to-client flow; native Android sample protocol validation script; per-project log4net config and separate relay appender

Integration tests run real TCP/TLS servers/clients: a test class that changes global socket/security settings must restore defaults in class init. The test assembly is marked `DoNotParallelize`; do not run multiple `dotnet test` processes against `SocketTests` at the same time because they share CPU-intensive TLS handshakes, global socket/security settings, and local TCP ports. Sample-client tests assume auto-register and a background receive loop after `Connect`, and assert on sample state (`LastReceivedMessage`/`Status`) rather than a manual receive return value.

Long-running integration tests emit `[test-progress]` lines for stage start/completion, wait conditions, elapsed time since the previous step, and timeout context. When a test stalls, inspect the last `[test-progress]` line first; it names the waiting condition such as cluster convergence, client location propagation, message delivery, or sample state update.

## Load test
Bulk-connection validation uses `SocketLoadTest` (see [Operations → Load test](Operations.md#load-test)).
```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --profile soak-10k --use-control-server --report-file reports/soak-10k.json
```
The base run aggregates connect, register, first healthcheck, and healthcheck-loop retention. `--message-test` splits connected clients into source/target pairs and verifies client-to-client delivery and source ack.
LoadTest uses the same local mTLS client-certificate path as `SocketClient`; when SocketServer requires client certificates, load clients must present per-client certificates and complete register before they are counted as active sessions. UI mode keeps the requested client IDs in reconnect-capable `SocketClientSession` instances, so failed or disconnected clients keep retrying instead of requiring a fresh load-test start.

```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --clients 10000 --batch-size 100 --hold-seconds 0 --use-control-server --message-test --message-rounds 1
```

UI mode starts/stops load clients and shows state in the browser:
```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --ui --ui-port 10060 --clients 300 --start-client-id 301 --batch-size 100 --host 127.0.0.1 --port 10000 --use-control-server
```
Options: `--profile`, `--clients`, `--start-client-id`, `--batch-size`, `--hold-seconds`, `--use-control-server`, `--message-test`, `--message-rounds`, `--ramp-delay-ms`, `--expected-connected`, `--healthcheck-timeout-seconds`, `--message-timeout-seconds`, `--report-file` (JSON of options, counters, elapsed time).

`SocketLoadTest` emits `[load-test-debug]` lines for batch start/complete, ramp delay, hold stage, message-test stage, per-round timing, and message/healthcheck timeout points. These logs make connection ramp latency and message relay stalls visible without waiting for the final summary.

## Log analysis
After a run, inspect `bin/Debug/net9.0/logs/` or the project's `logs/`:
- general log: lifecycle, route, healthcheck, cleanup, register/ack
- relay log: per client message token — local delivery, broadcast/targeted relay, ControlServer peer sync
- on integration failure, search source/target client id + message token + instance id + reservation id to locate the failing server/ControlServer stage

Default batch increment is 100; validate ≥10,000 only after checking OS/host settings.

## Native samples
The Android sample includes a Gradle Wrapper and `validate.sh`:
```bash
cd Samples/SocketSample.Android
./validate.sh --protocol-only   # validate frame/protocol Java sources without Android SDK
./validate.sh --apk             # force a debug APK build where the Android SDK is configured
```

## Test ports
Fixed-port tests use `25001`, outside the well-known port range and away from runtime defaults. Default runtime ports are nginx `10000`, ControlServer from `10001`, SocketServer from `10100`. Integration tests prefer dynamic port `0` to reduce conflicts.
