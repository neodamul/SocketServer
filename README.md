# SocketServer

TCP 소켓 서버/클라이언트 구조를 실험하기 위한 C#/.NET 솔루션입니다. 현재 저장소는 서버와 클라이언트 모델 클래스, 콘솔 실행 프로젝트, MSTest 기반 테스트 프로젝트로 구성되어 있습니다.

## 프로젝트 구성

```text
SocketServer.sln
├── SocketCommon/
│   ├── Interface/
│   │   ├── IClient.cs
│   │   └── IServer.cs
│   └── SocketCommon.csproj
├── SocketServer/
│   ├── Program.cs
│   ├── Constants.cs
│   ├── Model/
│   │   ├── HealthCheckProtocol.cs
│   │   ├── TcpClient.cs
│   │   └── TcpServer.cs
│   └── SocketServer.csproj
└── SocketServerTest/
    ├── Model/
    │   ├── HealthCheckProtocolTests.cs
    │   ├── TcpClientTests.cs
    │   └── TcpServerTests.cs
    └── SocketServerTest.csproj
```

## 주요 클래스

### `TcpClient`

`SocketServer.Model.TcpClient`는 TCP 클라이언트의 기본 정보를 관리합니다.

- 클라이언트 식별자와 이름 저장
- IPv4 주소 체계(`AddressFamily.InterNetwork`) 사용
- 기본 IP 주소를 `127.0.0.1`로 설정
- 기본 포트를 `5000`으로 설정
- `Initialize()` 호출 시 `SocketType.Stream`, `ProtocolType.Tcp` 기반 소켓 생성
- `Connect()` 호출 시 설정된 IP/포트로 TCP 연결
- `Disconnect()` 호출 시 연결 종료 및 소켓 리소스 정리
- `IsConnected()`로 현재 소켓 연결 상태 확인
- `SendHealthCheck()`로 `PING` 전송
- `SendHealthCheckResponse()`로 `PONG OK` 응답 전송
- `TryReceiveHealthCheck()`로 healthcheck 메시지 수신
- `ToString()`으로 `Id:Name:Family:IpAddress:Port` 형식 출력

### `TcpServer`

`SocketServer.Model.TcpServer`는 `TcpClient`를 상속해 서버 동작을 표현하는 클래스입니다.

- `TcpClient`의 주소/포트 설정 로직 재사용
- 서버 생명주기 메서드 제공: `Start()`, `Bind()`, `Listen()`, `End()`
- `Bind()` 호출 시 설정된 IP/포트로 서버 소켓 바인딩
- 포트 `0`으로 바인딩하면 OS가 할당한 실제 포트를 `GetPort()`로 조회
- `Listen()` 호출 시 TCP 연결 대기 시작
- `End()` 호출 시 서버 소켓 종료

### `SocketCommon`

`SocketCommon` 프로젝트는 클라이언트/서버가 구현해야 할 공통 인터페이스를 제공합니다.

- `IClient`: 초기화, 연결, 연결 종료, 연결 상태, IP/포트 설정과 조회
- `IServer`: `IClient`를 확장하고 서버 시작, 종료, 바인드, 리슨 메서드 정의

### `HealthCheckProtocol`

`SocketServer.Model.HealthCheckProtocol`은 TCP 연결 상태 확인을 위한 최소 메시지 프로토콜입니다. UTF-8 라인 메시지로 인코딩하며, 현재는 요청/응답 두 가지 메시지만 정의합니다.

연결 유지 정책은 30초마다 `PING`을 보내고, 상대가 `PONG OK`를 반환하면 연결이 살아 있는 것으로 판단하는 방식입니다.

```text
HEALTHCHECK/1 PING
HEALTHCHECK/1 PONG OK
```

사용 예:

```csharp
byte[] ping = HealthCheckProtocol.Encode(HealthCheckProtocol.CreatePing());
bool decoded = HealthCheckProtocol.TryDecode(ping, out HealthCheckMessage message);
```

TCP 연결에서 사용할 때:

```csharp
client.SendHealthCheck();
client.TryReceiveHealthCheck(out HealthCheckMessage response);
```

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

- .NET SDK 6.0
- Visual Studio 2022, Rider, VS Code 또는 `dotnet` CLI

프로젝트의 대상 프레임워크는 `net6.0`입니다. .NET 7 이상 SDK만 설치된 환경에서는 .NET 6 런타임을 설치하거나 `DOTNET_ROLL_FORWARD=Major` 환경 변수를 사용해야 테스트 실행이 가능합니다.

## 빌드 및 테스트

```bash
dotnet restore SocketServer.sln
dotnet build SocketServer.sln
dotnet test SocketServer.sln
```

.NET 6 런타임이 없고 최신 런타임으로 실행하려면:

```bash
DOTNET_ROLL_FORWARD=Major dotnet test SocketServer.sln
```

실행:

```bash
dotnet run --project SocketServer/SocketServer.csproj
```

## 테스트

`SocketServerTest` 프로젝트는 MSTest를 사용합니다.

현재 테스트 범위:

- `TcpClient.Initialize()` 호출
- `TcpClient.Connect()`, `Disconnect()`, `IsConnected()` 호출
- IP 주소 설정/조회 검증
- 포트 설정/조회 검증
- `TcpServer.Start()`, `Bind()`, `Listen()`, `End()` 호출
- 로컬 서버 시작 후 클라이언트 TCP 연결 검증
- healthcheck `PING`, `PONG OK` 메시지 인코딩/파싱 검증
- healthcheck 메시지의 실제 소켓 송수신 검증
- 테스트 TCP 포트는 `5001` 사용

비동기 accept 루프와 주기 실행 스케줄러는 아직 구현되어 있지 않습니다.

## 향후 개선 방향

- `TcpServer`에 클라이언트 수락(`Accept`) 루프 구현
- healthcheck 30초 주기 실행 스케줄러 추가
- 다중 클라이언트 처리와 비동기 I/O 적용
- 송수신 프로토콜과 예외 처리 정책 정의
- 실제 메시지 송수신을 검증하는 통합 테스트 추가
