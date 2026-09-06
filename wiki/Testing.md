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
LoadTest uses the same local mTLS client-certificate path as `SocketClient`; when SocketServer requires client certificates, load clients present the shared trusted `SocketClient` certificate and complete register before they are counted as active sessions. Strict per-client certificate binding is available only when `security.enforceClientCertificateId=true`. Certificate caching is per module name, so the default load-test path warms one SocketClient certificate instead of generating thousands of per-client certificates. CLI broker-routed runs use a bounded persistent ControlServer route channel pool by default, controlled by `--control-route-channels`; strict per-client certificate binding disables the pool automatically. CLI and UI modes warm only the certificate modules needed by the current batch before that batch starts connecting. A later batch's certificate warm-up never blocks admission of an earlier batch. Both modes expose progress as `certificateWarmupCompleted/Total`; UI mode does not wait for every requested client ID before the first batch connects. UI mode keeps the requested client IDs in reconnect-capable `SocketClientSession` instances, queues only one pending batch of connect workers at a time, limits active connect/retry work to `--batch-size`, creates each client session only after a connect slot is available, stores successful clients as soon as each one registers, and retries transient connect/TLS setup failures with 1/2/4/8/16/30-second backoff until the target client count is reached or the run is stopped. Transient connect/TLS failures are counted even when retry later succeeds, so ramp instability is visible instead of hidden behind the final connected count. Clicking **Start** while a run is active scales the live set **incrementally** — only the added client IDs (`[StartClientId .. StartClientId+Clients-1]` minus the current set) connect and only the removed surplus disconnect, leaving existing connections untouched; changing `Host`/`Port`/`UseControlServer` instead triggers a full restart (live connections cannot be migrated).

```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --clients 10000 --batch-size 100 --hold-seconds 0 --use-control-server --message-test --message-rounds 1
```

UI mode starts/stops load clients and shows state in the browser:
```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --ui --ui-port 10060 --clients 300 --start-client-id 301 --batch-size 100 --host 127.0.0.1 --port 10000 --use-control-server
```
Options: `--profile`, `--clients`, `--start-client-id`, `--batch-size`, `--hold-seconds`, `--use-control-server`, `--control-route-channels`, `--message-test`, `--message-rounds`, `--ramp-delay-ms`, `--expected-connected`, `--healthcheck-timeout-seconds`, `--message-timeout-seconds`, `--report-file`.

When `--report-file` is provided, the JSON report keeps the existing option and counter fields and adds the following admission fields:

- `ReadyClients`: clients that completed registration and the first healthcheck during admission. This is an admission result, not the number still connected after hold or teardown.
- `RampMilliseconds`: elapsed time through the connection, registration, and first-healthcheck ramp. It ends before message testing, hold, and teardown.
- `AllClientsReadyMilliseconds`: time to the last requested client becoming ready, or `null` when any requested client did not become ready. It is `0` for a zero-client run.
- `StageTimings`: fixed keys `route_lookup`, `tcp_connect`, `tls_authenticate`, `register`, `first_healthcheck`, and `client_ready`. Each stage contains `Succeeded` and `Failed` reports with `Count`, `MeanMilliseconds`, `MaxMilliseconds`, and bounded `P50UpperBoundMilliseconds`, `P95UpperBoundMilliseconds`, and `P99UpperBoundMilliseconds` values.

`route_lookup` is recorded once per route attempt, including persistent-pool queue wait. `client_ready` is recorded once per client attempt and stops when admission succeeds or fails, before client cleanup. Retry attempts remain separate observations. Hold and teardown time is represented only by `ElapsedMilliseconds`; it is not added to an admission stage.

Admission observations are buffered per client and delivered only by an explicit drain. This keeps user callbacks off the networking completion path; the load test drains each client before generating its report so every observation is counted exactly once. Callback exceptions remain isolated.

The first-batch admission guarantee is covered at the `AdmissionBatchRunner` boundary with a deterministic clock and controlled warm-up callbacks. A full `Program.RunAsync` multi-batch test would depend on real certificate-cache behavior and scheduler timing, and would require a production-only injection seam solely to control warm-up duration. The lower-level test therefore provides the reliable regression boundary: it verifies that a later batch warm-up cannot block the earlier batch and that warm-up time is excluded from both ramp measurements. The report-level tests separately verify the serialized timing contract, including exact zero values for a zero-client run.

For large local-only runs from a single host, add `--source-ips IP[,IP...]` so load clients bind outbound sockets across multiple loopback source addresses. This avoids exhausting the host ephemeral port range before the SocketServer connection limit is reached. Each source IP must be IPv4 and already bindable on the host; on macOS, add loopback aliases before using addresses such as `127.0.0.2`.

`SocketLoadTest` emits `[load-test-debug]` lines for certificate warm-up start/progress/complete, batch start/complete, ramp delay, hold stage, message-test stage, per-round timing, and message/healthcheck timeout points. These logs make certificate setup cost, connection ramp latency, and message relay stalls visible without waiting for the final summary.

## Log analysis
After a run, inspect `bin/Debug/net9.0/logs/` or the project's `logs/`. Default appender thresholds write INFO+; temporarily lower the relevant appender threshold to DEBUG only for focused trace runs because per-client DEBUG logging materially changes high-load ramp timing.
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
