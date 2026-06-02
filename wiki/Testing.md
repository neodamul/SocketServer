# Testing

## Test Project

테스트는 `SocketTests` 프로젝트에 통합되어 있습니다.

```bash
dotnet test SocketTests/SocketTests.csproj
```

검증 범위:

- frame encode/decode
- healthcheck protocol
- HelloWorld protocol
- SocketAsyncEventArgs pool
- TcpClient/TcpServer 기본 송수신
- port range binding
- ControlServer route
- ControlServer peer registry sync
- route reservation release
- heartbeat timeout route exclusion
- degraded resource route exclusion
- dashboard cluster snapshot

## Load Test

대량 접속 검증은 `SocketLoadTest`에서 수행합니다.

```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --clients 10000 --batch-size 100 --hold-seconds 60 --use-control-server
```

기본 batch size는 100개 단위 증가입니다. 10,000개 이상 검증은 OS와 장비 설정을 먼저 확인해야 합니다.

## Test Port

일부 기존 테스트는 `5001`을 사용합니다. 통합 테스트는 가능한 동적 포트 `0`을 사용해 충돌을 줄입니다.
