# SocketServer

TCP 소켓 서버/클라이언트, ControlServer 브로커, 대시보드, 부하 테스트를 포함한 C#/.NET 솔루션입니다.

## 구성

```text
SocketServer.sln
├── SocketCommon      공통 프레임, 프로토콜, 설정, 로깅
├── SocketClient      TCP 클라이언트 라이브러리
├── SocketControl     서버 등록/상태 취합/라우팅 브로커
├── SocketServer      TCP 서버 실행 프로젝트
├── SocketDashboard   상태 API와 웹 대시보드
├── SocketLoadTest    대량 접속 검증 도구
└── SocketTests       MSTest 통합 테스트
```

## 핵심 기능

- 12바이트 헤더, protobuf payload, 4KB payload 제한
- TLS 기반 소켓 연결, optional mTLS, 공유 Root CA 기반 모듈별 로컬 인증서
- healthcheck, HelloWorld, ControlServer route 프로토콜
- 클라이언트 간 메시지 전송과 SocketServer 간 relay
- `SocketAsyncEventArgs` 기반 비동기 송수신과 객체 풀
- SocketServer port range 바인딩과 서버별 최대/현재/접속가능 수 관리
- ControlServer 브로커 기반 서버 등록, heartbeat, route 응답, registry 파일 저장
- ControlServer client location 조회와 서버-control 연결 단절 감지
- 서버별 CPU/MEM/STORAGE 사용률 수집, 대시보드 표시, health/metrics API
- 프로젝트별 log4net 로깅 설정

## 요구 사항

- .NET SDK 9.0 이상
- Visual Studio 2022, Rider, VS Code 또는 `dotnet` CLI

## 설정 파일

```text
SocketControl/config.json
SocketServer/config.json
SocketClient/config.json
```

SocketServer는 설정된 port range 안에서 사용 가능한 포트를 찾아 바인딩하고, ControlServer에 등록합니다. 클라이언트는 ControlServer에 route를 요청한 뒤 응답받은 SocketServer endpoint로 직접 접속합니다.

## 빌드 및 테스트

```bash
dotnet restore SocketServer.sln
dotnet build SocketServer.sln
dotnet test SocketServer.sln
```

## 실행

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

기본 대시보드 주소는 `http://127.0.0.1:5080`입니다.

## 부하 테스트

직접 서버 접속:

```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --clients 10000 --batch-size 100 --hold-seconds 60 --port 5000
```

ControlServer route 사용:

```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --clients 10000 --batch-size 100 --hold-seconds 60 --host 127.0.0.1 --port 5000 --use-control-server
```

클라이언트 간 메시지 전송 부하:

```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --clients 1000 --batch-size 100 --hold-seconds 0 --port 5000 --message-test --message-rounds 1
```

## 상세 문서

- [Architecture](wiki/Architecture.md)
- [Configuration](wiki/Configuration.md)
- [Protocols](wiki/Protocols.md)
- [Operations](wiki/Operations.md)
- [Testing](wiki/Testing.md)
- [Open Source Libraries](wiki/OpenSourceLibraries.md)
