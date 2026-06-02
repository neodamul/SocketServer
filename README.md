# SocketServer

TCP 소켓 서버/클라이언트 구조를 실험하기 위한 C#/.NET 솔루션입니다. 현재 저장소는 서버와 클라이언트 모델 클래스, 콘솔 실행 프로젝트, MSTest 기반 테스트 프로젝트로 구성되어 있습니다.

## 프로젝트 구성

```text
SocketServer.sln
├── SocketCommon      공통 프로토콜, 프레임, 소켓 전송 유틸리티
├── SocketClient      TCP 클라이언트 라이브러리
├── SocketServer      TCP 서버 콘솔 프로젝트
├── SocketDashboard   서버 상태 API와 웹 대시보드
├── SocketLoadTest    대량 접속 검증용 콘솔 도구
└── SocketTests       통합 MSTest 테스트 프로젝트
```

## 주요 기능

- TCP 클라이언트와 서버 기본 연결, 연결 종료, 상태 확인
- 12바이트 헤더 기반 메시지 프레임과 4KB payload 제한
- healthcheck `PING`/`PONG OK` 프로토콜
- HelloWorld 요청/응답 프로토콜
- `SocketAsyncEventArgs` 기반 비동기 송수신과 객체 풀 재사용
- 다중 클라이언트 accept loop와 연결별 메시지 처리
- `/api/server/status` 기반 서버 상태 조회와 웹 대시보드 실시간 갱신
- 최대 10,000개 연결 목표를 위한 연결 제한, idle timeout, 부하 테스트 도구
- log4net 기반 공통 로깅

### 소켓 옵션과 객체 풀

소켓 생성은 `SocketFactory`를 통해 처리합니다.

- TCP listen backlog는 `100`입니다.
- `ReuseAddress`를 활성화합니다.
- `NoDelay=true`를 기본 적용해 Nagle 알고리즘을 비활성화합니다.

`SocketAsyncEventArgsFactory`는 반복적으로 필요한 `SocketAsyncEventArgs` 객체를 풀로 관리합니다.

- 프로세스 로드 시 초기 `1000`개를 생성합니다.
- 풀이 부족하면 `100`개 단위로 추가 생성합니다.
- accept, send, receive 작업은 풀에서 객체를 빌려 쓰고 완료 후 반환합니다.
- 생성 수, 사용 중 수, 최고 사용량, 성장 횟수를 상태 API와 대시보드에서 확인할 수 있습니다.

### 연결 처리 구조

서버의 기본 연결 정책은 다음과 같습니다.

- 최대 연결 수는 `10,000`입니다.
- accept 작업은 기본 `100`개 worker로 동시에 대기합니다.
- 연결별 상태는 `ConnectionSession`으로 관리하며, 연결 ID, 원격 주소, 접속 시각, 마지막 수신 시각을 추적합니다.
- 제한을 초과한 연결은 즉시 닫고 rejected counter를 증가시킵니다.
- 기본 idle timeout은 `90`초입니다. healthcheck 등 수신이 없는 연결은 idle sweep에서 닫습니다.
- 클라이언트는 `StartHealthCheckLoop()`로 30초마다 `PING`을 보내고 `PONG OK` 응답을 받아 연결을 유지할 수 있습니다.

### 로깅

모든 프로젝트는 `SocketCommon.Logging`의 공통 log4net 래퍼를 사용합니다.

- 설정 파일은 루트 `log4net.config`입니다.
- 콘솔과 `logs/socketserver.log`에 로그를 남깁니다.
- 서버 시작/종료, 바인드/리스닝, 클라이언트 연결/종료, 주요 송수신 실패 지점을 기록합니다.

### 공통 프레임

모든 프로토콜 메시지는 `SocketMessageFrame` 형식으로 인코딩합니다. 헤더는 12바이트 고정 길이이며 모든 정수는 big-endian(network byte order)입니다.

```text
0..3   clientId      uint32
4..7   messageId     uint32
8..11  payloadLength uint32
12..   payload       byte[payloadLength]
```

### 대용량 송수신

프로토콜의 실제 소켓 송수신은 `SocketAsyncEventArgs`를 사용하는 `SocketAsyncEventArgsTransport`를 통해 처리합니다.

- 송신은 8KB 버퍼 단위로 나누어 모든 바이트가 전송될 때까지 반복합니다.
- 수신은 먼저 12바이트 헤더를 정확히 읽고, 헤더의 `payloadLength`만큼 payload를 이어서 읽습니다.
- payload 최대 길이는 4KB로 제한합니다.
- 기존 동기 API는 유지하고, `SendAsync`, `TryReceive...Async` 계열 API로 비동기 경로를 직접 사용할 수 있습니다.

### `HealthCheckProtocol`

`SocketCommon.Model.HealthCheckProtocol`은 TCP 연결 상태 확인을 위한 최소 메시지 프로토콜입니다.

연결 유지 정책은 30초마다 `PING`을 보내고, 상대가 `PONG OK`를 반환하면 연결이 살아 있는 것으로 판단하는 방식입니다.

```text
messageId 1: PING, payloadLength 0
messageId 2: PONG, payload "OK"
```

사용 예:

```csharp
byte[] ping = HealthCheckProtocol.Encode(HealthCheckProtocol.CreatePing());
bool decoded = HealthCheckProtocol.TryDecode(ping, out HealthCheckMessage message);
```

TCP 연결에서 한 번 보낼 때:

```csharp
await client.SendHealthCheckAsync();
(bool ok, HealthCheckMessage response) = await client.TryReceiveHealthCheckAsync();
```

30초 주기로 연결을 유지할 때:

```csharp
client.StartHealthCheckLoop();
```

### `HelloWorldProtocol`

`SocketCommon.Model.HelloWorldProtocol`은 클라이언트가 서버에 기본 HelloWorld 요청을 보내고 서버가 응답하는 프레임 기반 프로토콜입니다.

```text
messageId 100: HelloWorldRequest, payloadLength 0
messageId 101: HelloWorldResponse, payload "Hello, World!"
```

클라이언트에서 사용할 때:

```csharp
await client.SendHelloWorldRequestAsync();
(bool ok, HelloWorldResponse response) = await client.TryReceiveHelloWorldResponseAsync();
```

서버에서 사용할 때:

```csharp
await server.AcceptHelloWorldRequestAndRespondAsync();
```

다중 클라이언트 서버 루프로 사용할 때:

```csharp
TcpServer server = new(1, "server", "127.0.0.1", 5000);
server.Start();
server.StartClientAcceptLoop();
```

`StartClientAcceptLoop()`는 서버 소켓에서 연결을 계속 수락하고, 연결별 비동기 처리 루프를 실행합니다. 각 클라이언트는 동일한 서버에 동시에 접속해 healthcheck와 HelloWorld 요청/응답을 독립적으로 주고받을 수 있습니다.

## 실행 예시

`Program.cs`는 클라이언트와 서버 인스턴스를 생성하고 각 객체의 문자열 표현을 출력합니다.

```csharp
TcpClient tcpClient = new(1, "testClient");
Console.WriteLine(tcpClient);

TcpServer tcpServer = new(1, "testServer");
Console.WriteLine(tcpServer);
```

예상 출력 형식:

```text
1:testClient:InterNetwork:127.0.0.1:5000
1:testServer:InterNetwork:127.0.0.1:5000
```

## 요구 사항

- .NET SDK 9.0 이상
- Visual Studio 2022, Rider, VS Code 또는 `dotnet` CLI

프로젝트는 SDK 스타일의 .NET Core 계열 프로젝트이며 대상 프레임워크는 `net9.0`입니다.

## 빌드 및 테스트

```bash
dotnet restore SocketServer.sln
dotnet build SocketServer.sln
dotnet test SocketServer.sln
```

실행:

```bash
dotnet run --project SocketServer/SocketServer.csproj
```

대시보드 실행:

```bash
dotnet run --project SocketDashboard/SocketDashboard.csproj
```

기본 대시보드 웹 주소는 `http://127.0.0.1:5080`입니다. 대시보드가 모니터링하는 내부 SocketServer는 기본 TCP 포트 `5000`을 사용합니다.

대시보드 API:

```text
GET /api/server/status
```

상태 API와 대시보드는 연결 수, 최대 연결 수, 누적 accept/close/reject 수, idle timeout 종료 수, backlog, pending accept 수, 최대 payload, `SocketAsyncEventArgs` 풀 상태를 표시합니다.

부하 테스트:

```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --clients 10000 --batch-size 100 --hold-seconds 60 --port 5000
```

외부 서버를 대상으로 검증할 때:

```bash
dotnet run --project SocketLoadTest/SocketLoadTest.csproj -- --clients 10000 --batch-size 100 --hold-seconds 60 --host 127.0.0.1 --port 5000 --external-server
```

10,000개 접속 테스트는 운영체제의 파일 디스크립터 제한, ephemeral port 범위, TCP backlog, 머신 메모리/CPU 상태의 영향을 받습니다. 기본 부하 테스트는 한 번에 10,000개를 만들지 않고 `100`개 단위 배치로 늘려갑니다.

## 테스트

테스트는 단일 `SocketTests` 프로젝트에 통합되어 있으며 MSTest를 사용합니다. `SocketCommon`, `SocketClient`, `SocketServer`, `SocketDashboard` 프로젝트를 참조해 공통 프로토콜, 클라이언트/서버 송수신, 대시보드 상태 서비스를 함께 검증합니다. 대량 접속 검증은 별도 `SocketLoadTest` 프로젝트로 실행합니다.

테스트 TCP 포트는 `5001`을 사용합니다.

## 향후 개선 방향

- 송수신 프로토콜과 예외 처리 정책 정의
- HelloWorld 외 추가 요청/응답 프로토콜 확장

## 오픈소스 라이브러리

이 프로젝트는 다음 NuGet 오픈소스 라이브러리를 사용합니다.

- `log4net` `3.3.1`: 공통 로깅
- `Google.Protobuf` `3.20.1`: Protocol Buffers 지원
- `Grpc.Net.Client` `2.46.0`: gRPC 클라이언트 지원
- `Grpc.Tools` `2.46.1`: gRPC/Protobuf 빌드 도구
- `Microsoft.NET.Test.Sdk` `17.2.0`: .NET 테스트 실행
- `MSTest.TestAdapter` `2.2.10`: MSTest 어댑터
- `MSTest.TestFramework` `2.2.10`: MSTest 프레임워크
- `coverlet.collector` `3.1.2`: 테스트 커버리지 수집
