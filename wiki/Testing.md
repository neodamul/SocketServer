# Testing

## Test Project

테스트는 `SocketTests` 프로젝트에 통합되어 있습니다.

```bash
dotnet test SocketTests/SocketTests.csproj
```

검증 범위:

- frame encode/decode
- protobuf payload encode/decode
- healthcheck protocol
- HelloWorld protocol
- TLS secure connection, shared local Root CA, module certificate creation
- certificate password environment variable and optional mTLS handshake
- TLS disabled message encryption transport with AES-GCM/HMAC
- SocketAsyncEventArgs pool and mapped slab receive buffer
- TcpClient/TcpServer 기본 송수신
- SocketServer inactive client cleanup scheduler
- port range binding
- ControlServer route
- ControlServer peer registry sync
- ControlServer command/relay queue isolation
- ControlServer periodic peer snapshot sync recovery
- ControlServer dual endpoint direct report with stalled endpoint isolation
- ControlServer registry file persistence and stale heartbeat normalization
- ControlServer stale control connection cleanup scheduler
- client-to-client local delivery
- SocketServer-to-SocketServer client message relay
- SocketServer-to-SocketServer persistent relay channel sequential delivery
- SocketServer command/response/relay queue workers
- SocketServer relay server list refresh and broadcast fallback
- four SocketServer relay list refresh and broadcast fan-out
- SocketServer control channel disconnect detection
- active-active ControlServer, four SocketServers, platform sample clients concurrent messaging
- graceful shutdown for ControlServer, SocketServer, and SocketClient
- route reservation release
- heartbeat timeout route exclusion
- degraded resource route exclusion
- dashboard cluster snapshot
- dashboard, ControlServer, SocketServer, sample client registration and message send/receive integration
- dashboard liveness, readiness, metrics models
- sample client settings and client-to-client message flow
- native Android sample protocol validation script
- project log4net configuration and separate relay log appender

## Load Test

대량 접속 검증은 `SocketLoadTest`에서 수행합니다.

```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --profile soak-10k --use-control-server --report-file reports/soak-10k.json
```

기본 부하 테스트는 접속, 최초 healthcheck, healthcheck loop 유지 상태를 집계합니다. `--message-test`를 추가하면 연결된 클라이언트를 source/target 쌍으로 나누고 클라이언트 간 메시지 delivery와 source ack를 검증합니다.

```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --clients 10000 --batch-size 100 --hold-seconds 0 --use-control-server --message-test --message-rounds 1
```

UI 모드는 브라우저에서 부하 클라이언트를 시작/중지하고 상태를 조회합니다.

```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --ui --ui-port 10060 --clients 4 --batch-size 4 --host 127.0.0.1 --port 10000 --use-control-server
```

표시 항목:

- 현재 실행 상태
- 접속 대수와 healthcheck counters
- 타겟 서버별 연결 수
- 클라이언트별 타겟 서버와 연결 상태

주요 옵션:

- `--profile`: `smoke`, `soak-1k`, `soak-10k`, `soak-50k`, `message-1k`
- `--clients`: 생성할 클라이언트 수
- `--batch-size`: 한 번에 증가시킬 클라이언트 수
- `--hold-seconds`: 접속 유지 시간
- `--use-control-server`: ControlServer route를 통해 SocketServer에 접속
- `--message-test`: 클라이언트 간 메시지 delivery/ack 부하 검증
- `--message-rounds`: source/target 쌍별 메시지 반복 횟수
- `--ramp-delay-ms`: batch 사이 대기 시간
- `--expected-connected`: 최소 연결 성공 수
- `--healthcheck-timeout-seconds`: healthcheck 응답 timeout
- `--message-timeout-seconds`: client message delivery/ack timeout
- `--report-file`: 실행 옵션, counters, elapsed time을 JSON 파일로 저장

## Log Analysis

테스트 실행 후 `bin/Debug/net9.0/logs/` 또는 실행 프로젝트의 `logs/`에서 결과를 확인합니다.

- 일반 로그는 lifecycle, route, healthcheck, cleanup, register/ack 상태를 확인합니다.
- relay 로그는 client message token 기준으로 local delivery, broadcast relay, targeted relay, ControlServer peer sync 흐름을 추적합니다.
- 통합테스트 실패 시 source/target client id, message token, instance id, reservation id를 함께 검색하면 어느 서버 또는 ControlServer 단계에서 실패했는지 확인할 수 있습니다.

기본 batch size는 100개 단위 증가입니다. 10,000개 이상 검증은 OS와 장비 설정을 먼저 확인해야 합니다.

## Native Samples

Android 샘플은 Gradle Wrapper와 `validate.sh`를 포함합니다.

```bash
cd Samples/SocketSample.Android
./validate.sh --protocol-only
./validate.sh --apk
```

`--protocol-only`는 Android SDK 없이 프레임/프로토콜 Java 소스를 검증합니다. `--apk`는 Android SDK가 설정된 환경에서 debug APK 빌드를 강제합니다.

## Test Port

일부 기존 테스트는 `5001`을 사용합니다. 기본 런타임 포트는 nginx `10000`, ControlServer `10001`부터, SocketServer `10100`부터 사용합니다. 통합 테스트는 가능한 동적 포트 `0`을 사용해 충돌을 줄입니다.
