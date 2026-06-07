# SocketServer

SocketServer is a C#/.NET TCP socket server/client solution with TLS or message encryption.
ControlServer acts as the broker for server registration, healthcheck, routing, and session location sharing across multiple SocketServer instances and clients.
The project includes SAEA-based async I/O, fixed slab buffers, protobuf protocols, a dashboard, load testing, and native sample clients.

## Project Layout

```text
SocketServer.sln
├── SocketCommon      shared frames, protocols, config, logging
├── SocketClient      TCP client library
├── SocketControl     registration, status, and routing broker
├── SocketServer      TCP server executable
├── SocketDashboard   status API and web dashboard
├── SocketLoadTest    load and message test tool
├── SocketTests       MSTest suite
└── Samples           .NET, iOS, macOS, Android sample clients
```

## Key Features

- TLS socket connections, optional mTLS, and AES-GCM/HMAC message protection when TLS is disabled
- Healthcheck, HelloWorld, ControlServer route, client message, and server relay protocols
- Existing-connection-first policy for duplicate ClientId registration and client reconnect/backoff
- `SocketAsyncEventArgs` async send/receive, object pools, and fixed slab buffers
- SocketServer port range binding and per-server max/current/available connection tracking
- ControlServer broker registration, heartbeat, route response, registry persistence, and peer sync
- Client location lookup, SocketServer-to-SocketServer message relay, and heartbeat timeout cleanup
- Dedicated command, response, relay, and healthcheck workers for ControlServer, SocketServer, and SocketClient
- Dashboard with ControlServer/SocketServer inventory, selected runtime/details, metrics, and 30-second default refresh
- Separate general/relay log4net files with INFO/DEBUG trace coverage

## Requirements

- .NET SDK 9.0 or later
- Visual Studio 2022, Rider, VS Code, or the `dotnet` CLI

## Configuration

```text
SocketControl/config.json
SocketServer/config.json
SocketClient/config.json
```

SocketServer binds to an available port in its configured port range, registers with ControlServer, and reports heartbeat/session status. Clients request a route from ControlServer and then connect directly to the routed SocketServer endpoint.

Each module uses `socketOptions` for connection/read/write timeouts; the default is 30 seconds. Client reconnect delay defaults to 30 seconds. Duplicate ClientId rejection uses the server-provided retry-after value or the default 90-second backoff.

Default local runtime ports:

```text
nginx client-facing endpoint  10000
ControlServer                 10001, 10002
SocketServer                  10100-10199
Dashboard                     10050
SocketLoadTest UI             10060
```

## Build And Test

```bash
dotnet restore SocketServer.sln
dotnet build SocketServer.sln
dotnet test SocketTests/SocketTests.csproj
```

Use targeted tests for change-scoped development. The full test suite is the merge gate for broad changes.

## Run

ControlServer:

```bash
dotnet run --project SocketControl/SocketControl.csproj
```

SocketServer:

```bash
dotnet run --project SocketServer/SocketServer.csproj -- --all
```

Dashboard:

```bash
dotnet run --project SocketDashboard/SocketDashboard.csproj
```

Default dashboard URL:

```text
http://127.0.0.1:10050
```

Sample Client:

```bash
dotnet run --project Samples/SocketSample.Net/SocketSample.Net.csproj
```

The .NET sample client UI binds to a dynamic local port by default. Check the `Now listening on` log line for the URL. See [Samples](Samples/README.md) for iOS, macOS, and Android native clients.

## Load Test

Broker-routed 10k connection test:

```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --clients 10000 --batch-size 100 --hold-seconds 60 --host 127.0.0.1 --port 10000 --use-control-server
```

ControlServer route profile:

```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --profile soak-10k --host 127.0.0.1 --port 10000 --use-control-server --report-file reports/soak-10k.json
```

Broker-routed client-to-client message load:

```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --clients 1000 --batch-size 100 --hold-seconds 0 --host 127.0.0.1 --port 10000 --use-control-server --message-test --message-rounds 1
```

UI mode:

```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --ui --ui-port 10060 --clients 300 --start-client-id 301 --batch-size 100 --host 127.0.0.1 --port 10000 --use-control-server
```

The UI shows connected clients, target server distribution, per-client connection state, counters, and metrics.

## Detailed Documentation

- [Architecture](wiki/Architecture.md)
- [Configuration](wiki/Configuration.md)
- [Protocols](wiki/Protocols.md)
- [Operations](wiki/Operations.md)
- [Testing](wiki/Testing.md)
- [Open Source Libraries](wiki/OpenSourceLibraries.md)
