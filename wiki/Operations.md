# Operations

## Build

```bash
dotnet restore SocketServer.sln
dotnet build SocketServer.sln
```

MSBuild 병렬 노드에서 문제가 있으면 다음처럼 단일 노드 빌드를 사용할 수 있습니다.

```bash
dotnet build SocketServer.sln --no-restore --disable-build-servers -p:UseSharedCompilation=false -m:1
```

## Run ControlServer

```bash
dotnet run --project SocketControl/SocketControl.csproj
```

기본 endpoint는 `127.0.0.1:5000`입니다.

## Run SocketServer

설정 파일의 모든 서버 인스턴스 실행:

```bash
dotnet run --project SocketServer/SocketServer.csproj -- --all
```

특정 server id 실행:

```bash
dotnet run --project SocketServer/SocketServer.csproj -- --server-id 1
```

## Run Dashboard

```bash
dotnet run --project SocketDashboard/SocketDashboard.csproj
```

대시보드 기본 URL:

```text
http://127.0.0.1:5080
```

API:

```text
GET /api/server/status
GET /health/live
GET /health/ready
GET /metrics
```

ControlServer가 실행 중이면 cluster registry snapshot을 표시합니다. ControlServer가 없으면 로컬 fallback SocketServer 상태를 표시합니다. 설정된 ControlServer endpoint는 별도 목록에 항상 표시되며, endpoint별 healthy/unavailable 상태와 server/session counters를 확인할 수 있습니다.
웹 대시보드는 기본 30초 간격으로 `/api/server/status`를 갱신하며, 화면 상단 콤보에서 5초, 10초, 30초, 60초를 선택할 수 있습니다.
`/health/live`는 Dashboard 프로세스 생존 여부, `/health/ready`는 Dashboard 내부 TCP 서버 준비 상태, `/metrics`는 cluster 연결 수와 로컬 socket/pool counters를 반환합니다.

## Certificates

로컬 인증서는 기본적으로 솔루션 루트의 `Certificates/`에 생성됩니다. 운영이나 장비별 격리가 필요하면 각 프로젝트의 `config.json`에서 `security.certificateDirectory`를 지정하거나 `SOCKET_CERTIFICATE_DIR` 환경 변수를 설정합니다.

PFX 비밀번호는 코드에 고정하지 않고 `security.certificatePasswordEnvironmentVariable`에 지정한 환경 변수에서 읽습니다. 기본 변수명은 `SOCKET_CERTIFICATE_PASSWORD`입니다.

```bash
export SOCKET_CERTIFICATE_PASSWORD='change-this-local-secret'
```

`certificateRenewBeforeDays` 이내로 만료가 가까운 Root CA 또는 모듈 인증서는 다음 시작/연결 시 재생성됩니다. `requireClientCertificate=true`를 사용하면 mTLS 모드로 동작하므로 모든 모듈이 같은 Root CA 체인을 사용해야 합니다.

## Load Test

직접 서버 접속:

```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --clients 10000 --batch-size 100 --hold-seconds 60 --port 5000
```

외부 서버 접속:

```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --clients 10000 --batch-size 100 --hold-seconds 60 --host 127.0.0.1 --port 5000 --external-server
```

ControlServer route 사용:

```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --profile soak-10k --host 127.0.0.1 --port 5000 --use-control-server --report-file reports/soak-10k.json
```

부하 테스트 preset:

```text
smoke       100 clients, 10s hold
soak-1k     1,000 clients, 300s hold
soak-10k    10,000 clients, 600s hold
soak-50k    50,000 clients, 900s hold
message-1k  1,000 clients, client message delivery/ack
```

## Scale Notes

30만 동접은 애플리케이션 구조만으로 보장되지 않습니다. 다음 조건을 함께 검증해야 합니다.

- OS file descriptor limit
- ephemeral port range
- TCP backlog
- 메모리/CPU/GC 상태
- 장비 수와 서버 인스턴스 수
- ControlServer 이중화 구성

현재 기본 권장값은 SocketServer 인스턴스당 10,000 연결입니다.
