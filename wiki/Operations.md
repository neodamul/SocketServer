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
```

ControlServer가 실행 중이면 cluster registry snapshot을 표시합니다. ControlServer가 없으면 로컬 fallback SocketServer 상태를 표시합니다.

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
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --clients 10000 --batch-size 100 --hold-seconds 60 --host 127.0.0.1 --port 5000 --use-control-server
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
