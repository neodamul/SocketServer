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
- SocketAsyncEventArgs pool
- TcpClient/TcpServer 기본 송수신
- port range binding
- ControlServer route
- ControlServer peer registry sync
- client-to-client local delivery
- SocketServer-to-SocketServer client message relay
- SocketServer control channel disconnect detection
- route reservation release
- heartbeat timeout route exclusion
- degraded resource route exclusion
- dashboard cluster snapshot

## Load Test

대량 접속 검증은 `SocketLoadTest`에서 수행합니다.

```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --clients 10000 --batch-size 100 --hold-seconds 60 --use-control-server
```

기본 부하 테스트는 접속, 최초 healthcheck, healthcheck loop 유지 상태를 집계합니다. `--message-test`를 추가하면 연결된 클라이언트를 source/target 쌍으로 나누고 클라이언트 간 메시지 delivery와 source ack를 검증합니다.

```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --clients 10000 --batch-size 100 --hold-seconds 0 --use-control-server --message-test --message-rounds 1
```

주요 옵션:

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

기본 batch size는 100개 단위 증가입니다. 10,000개 이상 검증은 OS와 장비 설정을 먼저 확인해야 합니다.

## Test Port

일부 기존 테스트는 `5001`을 사용합니다. 통합 테스트는 가능한 동적 포트 `0`을 사용해 충돌을 줄입니다.
