# Operations

## Build
```bash
dotnet restore SocketServer.sln
dotnet build SocketServer.sln
```
If MSBuild parallel nodes cause issues, use a single-node build:
```bash
dotnet build SocketServer.sln --no-restore --disable-build-servers -p:UseSharedCompilation=false -m:1
```

## Local port map
```text
nginx client-facing endpoint  10000
ControlServer                 10001, 10002
SocketServer                  10100-10199
Dashboard                     10050
SocketLoadTest UI             10060
```
The default client-facing endpoint is the nginx TCP stream proxy at `127.0.0.1:10000`; ControlServer instances run on `10001`/`10002`.

## Run
ControlServer:
```bash
dotnet run --project SocketControl/SocketControl.csproj
```
SocketServer — all configured instances, or a specific id:
```bash
dotnet run --project SocketServer/SocketServer.csproj -- --all
dotnet run --project SocketServer/SocketServer.csproj -- --server-id 1
```
Dashboard (`http://127.0.0.1:10050`):
```bash
dotnet run --project SocketDashboard/SocketDashboard.csproj
```
APIs: `GET /api/server/status`, `/health/live`, `/health/ready`, `/metrics`. With a ControlServer running, the dashboard shows the cluster registry snapshot; otherwise the local fallback SocketServer state. `/health/live` = dashboard process liveness, `/health/ready` = dashboard TCP server readiness, `/metrics` = cluster connection counters + local socket/pool counters. See [Architecture → Dashboard](Architecture.md#dashboard).
Dashboard fixes its content root to the application output directory, so `appsettings.json` is read from the deployed dashboard directory even when the process is started from another working directory. Startup logs include the effective ControlServer endpoints; the local default must show `127.0.0.1:10001` and `127.0.0.1:10002`, not the nginx broker endpoint `127.0.0.1:10000`.

## Certificates
Local certs are generated under the solution-root `Certificates/` by default. For per-host isolation, set `security.certificateDirectory` (per project `config.json`) or `SOCKET_CERTIFICATE_DIR`. The PFX password is read from the env var named by `security.certificatePasswordEnvironmentVariable` (default `SOCKET_CERTIFICATE_PASSWORD`), never hardcoded:
```bash
export SOCKET_CERTIFICATE_PASSWORD='change-this-local-secret'
```
Root CA / module certs nearing expiry within `certificateRenewBeforeDays` are regenerated on the next start/connect. With mTLS (`requireClientCertificate=true` / `EndToEndTls`), all modules must share the same Root CA chain. See [Configuration → Certificates](Configuration.md#certificates).

## Load test
```bash
# direct
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --clients 10000 --batch-size 100 --hold-seconds 60 --port 10000
# external server
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --clients 10000 --batch-size 100 --hold-seconds 60 --host 127.0.0.1 --port 10000 --external-server
# via ControlServer route
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --profile soak-10k --host 127.0.0.1 --port 10000 --use-control-server --report-file reports/soak-10k.json
```
Presets: `smoke` (100/10s), `soak-1k` (1k/300s), `soak-10k` (10k/600s), `soak-50k` (50k/900s), `message-1k` (1k, client message delivery/ack). See [Testing → Load test](Testing.md#load-test) for options and UI mode.

## Scale notes
300k concurrency is not guaranteed by app structure alone; also validate: OS file-descriptor limit, ephemeral port range, TCP backlog, memory/CPU/GC, host count and server instance count, ControlServer redundancy. Current default recommendation is 10,000 connections per SocketServer instance.

`EndToEndTls` keeps TLS to the app process and enables mTLS when `requireClientCertificate=true`. For large fan-out, distribute connections across SocketServer nodes via L4 TCP pass-through / TCP stream proxy and size shard count from measured per-node SslStream memory.

Sizing procedure:
```text
1. Hold long-lived connections at 1k, 10k, 50k stages.
2. Record RSS / managed heap / GC count / socket count / p95-p99 latency at each stage.
3. Per-connection memory = (stage RSS - baseline RSS) / connection count.
4. Budget only 60-70% of node memory for connections.
5. Node count = target concurrency / safe connections per node.
```
Example: a validated 10,000 safe connections/node implies ≥30 SocketServer instances for 300,000. TLS handshake CPU is a transient spike for long-lived connections — measure restart/reconnect-storm scenarios separately.

For control-plane / server-to-server relay message-size estimates and broadcast amplification at scale, see [Relay Traffic Sizing](RelayTrafficSizing.md).
