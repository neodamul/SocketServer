# SocketServer

TCP 소켓 서버/클라이언트 구조를 실험하기 위한 C#/.NET 솔루션입니다. 현재 저장소는 서버와 클라이언트 모델 클래스, 콘솔 실행 프로젝트, MSTest 기반 테스트 프로젝트로 구성되어 있습니다.

## 프로젝트 구성

```text
SocketServer.sln
├── SocketCommon      공통 프로토콜, 프레임, 소켓 전송 유틸리티
├── SocketClient      TCP 클라이언트 라이브러리
├── SocketServer      TCP 서버 콘솔 프로젝트
├── SocketDashboard   서버 상태 API와 웹 대시보드
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

### 소켓 옵션과 객체 풀

소켓 생성은 `SocketFactory`를 통해 처리합니다.

- TCP listen backlog는 `100`입니다.
- `ReuseAddress`를 활성화합니다.
- `NoDelay=true`를 기본 적용해 Nagle 알고리즘을 비활성화합니다.

`SocketAsyncEventArgsFactory`는 반복적으로 필요한 `SocketAsyncEventArgs` 객체를 풀로 관리합니다.

- 프로세스 로드 시 초기 `1000`개를 생성합니다.
- 풀이 부족하면 `100`개 단위로 추가 생성합니다.
- accept, send, receive 작업은 풀에서 객체를 빌려 쓰고 완료 후 반환합니다.

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

TCP 연결에서 사용할 때:

```csharp
await client.SendHealthCheckAsync();
(bool ok, HealthCheckMessage response) = await client.TryReceiveHealthCheckAsync();
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

## 테스트

테스트는 단일 `SocketTests` 프로젝트에 통합되어 있으며 MSTest를 사용합니다. `SocketCommon`, `SocketClient`, `SocketServer`, `SocketDashboard` 프로젝트를 참조해 공통 프로토콜, 클라이언트/서버 송수신, 대시보드 상태 서비스를 함께 검증합니다.

테스트 TCP 포트는 `5001`을 사용합니다.

healthcheck를 30초마다 자동 전송하는 주기 실행 스케줄러는 아직 구현되어 있지 않습니다.

## 향후 개선 방향

- healthcheck 30초 주기 실행 스케줄러 추가
- 송수신 프로토콜과 예외 처리 정책 정의
- HelloWorld 외 추가 요청/응답 프로토콜 확장
