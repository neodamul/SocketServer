# Architecture

## Overview

SocketServer는 TCP 서버를 여러 인스턴스로 확장하고, ControlServer가 서버 상태를 취합해 클라이언트에게 접속할 SocketServer 정보를 내려주는 구조입니다.

```text
Client -> ControlServer:5000  ROUTE_REQUEST
Client <- ControlServer       ROUTE_RESPONSE(host, port)
Client -> SocketServer        direct TCP connection
```

ControlServer는 TCP payload를 프록시하지 않습니다. 실제 장기 연결은 클라이언트와 SocketServer가 직접 유지하며, 각 연결은 TLS 소켓 연결로 인증된 뒤 메시지 프레임을 송수신합니다.

클라이언트 간 메시지 전송도 ControlServer가 payload를 프록시하지 않습니다. ControlServer는 target client 위치만 알려주고, 실제 payload는 SocketServer 간 relay로 이동합니다.

```text
SourceClient -> SourceServer       CLIENT_MESSAGE_SEND
SourceServer -> ControlServer      CLIENT_LOCATION_REQUEST
SourceServer <- ControlServer      CLIENT_LOCATION_RESPONSE(target server)
SourceServer -> TargetServer       SERVER_RELAY_MESSAGE
TargetServer -> TargetClient       CLIENT_MESSAGE_DELIVER
SourceServer -> SourceClient       CLIENT_MESSAGE_ACK
```

## Projects

```text
SocketCommon      shared frame, protocols, config, diagnostics, logging
SocketClient      client library
SocketControl     broker and cluster registry
SocketServer      TCP backend server
SocketDashboard   status API and web dashboard
SocketLoadTest    load test tool
SocketTests       MSTest suite
Samples           native sample clients for .NET, iOS, macOS, Android
```

## ControlServer

ControlServer 역할:

- SocketServer `SERVER_REGISTER` 수신
- SocketServer `SERVER_HEARTBEAT` 수신
- session opened/updated/closed event 수신
- client location upsert/remove 수신
- client location request 처리
- SocketServer control channel 단절 감지
- 서버별 max/current/reserved/available 연결 수 관리
- 서버별 CPU/MEM/STORAGE 사용률 저장
- 클라이언트 `ROUTE_REQUEST` 처리
- peer ControlServer에 registry/session/reservation 정보 동기화

기본 라우팅 정책은 `MostAvailableConnections`입니다.

```text
availableConnections = maxConnections - currentConnections - reservedConnections
```

라우팅 후보 조건:

- `Health == Healthy`
- heartbeat timeout이 지나지 않음
- `availableConnections > 0`

## SocketServer

SocketServer는 설정된 port range에서 사용 가능한 포트를 찾아 바인딩합니다. 바인딩 후 ControlServer에 등록하고, 주기적으로 heartbeat를 전송합니다.

SocketServer는 ControlServer와 persistent control channel을 유지합니다. 이 연결이 끊기면 ControlServer는 해당 서버를 즉시 `Unhealthy`로 표시하고 신규 route 및 client message location 후보에서 제외합니다. heartbeat timeout을 초과한 stale control channel은 ControlServer cleanup scheduler가 주기적으로 닫고 registry를 정규화합니다. SocketServer가 재연결하면 register, heartbeat, session update로 상태를 다시 보정합니다.

클라이언트가 `CLIENT_REGISTER` 또는 다른 메시지를 보내면 SocketServer는 `clientId -> session` 인덱스를 갱신하고 ControlServer에 session/client location을 전파합니다. 연결 종료, read 실패, healthcheck 중단으로 인한 idle timeout, delivery send 실패는 session close로 정규화되어 location 제거 이벤트로 이어집니다.

SocketServer의 session event는 bounded queue를 통해 ControlServerReporter가 순차 전송합니다. 이벤트 폭주 시 무제한 task/memory 증가를 막고, reporter worker에서 전송 실패를 기록합니다.

서버 기본 정책:

- `maxConnections`: 기본 10,000
- `pendingAcceptCount`: 기본 100
- `idleTimeoutSeconds`: 기본 90
- healthcheck interval: 기본 30초
- unhealthy connection cleanup scan: 기본 10초
- `SocketAsyncEventArgs` pool: 초기 1,000개, 100개 단위 증가
- SAEA receive buffer: 8KB 고정 세그먼트를 슬랩 단위로 선할당하고 SAEA 인스턴스에 유지

콘솔 실행 프로젝트는 Ctrl+C/프로세스 종료 신호를 받아 reporter와 서버 소켓을 정리합니다. 코드 경로는 `ControlServer.StopAsync`, `TcpServer.EndAsync`, `TcpClient.DisconnectAsync`로 listener, accept loop, active socket, healthcheck loop, client session을 순서대로 닫습니다.

SAEA 버퍼는 파일 기반 `MemoryMappedFile`이 아니라, 소켓 I/O에 적합한 메모리맵형 슬랩 풀로 관리합니다. 각 SAEA는 생성 시 고정 byte buffer segment를 갖고 반환 후에도 같은 segment reference를 유지합니다. 수신 경로는 `ReceiveMappedAsync`로 이 segment를 직접 참조할 수 있으며, 기존 `ReceiveExactAsync`는 호환성을 위해 최종 결과 배열로 한 번만 복사합니다. Accept 작업은 초기 TLS handshake 데이터가 accept buffer에 소비되지 않도록 buffer를 비운 상태로 실행하고, 반환 시 segment를 복원합니다.

## ControlServer Active-Active

ControlServer는 `peers` 설정을 통해 서로 registry 정보를 공유합니다. 노드 시작 시 peer registry snapshot을 먼저 요청해 이미 등록된 서버 정보를 보정하고, 이후 변경 이벤트를 peer로 전파합니다. Registry는 기본적으로 파일 저장소에 서버 snapshot, route reservation, session summary, client location을 저장할 수 있으며, 테스트나 임시 실행에는 `InMemory` 저장소를 사용할 수 있습니다.

- `SERVER_REGISTRY_UPSERT`
- `SESSION_SUMMARY_UPSERT`
- `SESSION_SUMMARY_REMOVE`
- `ROUTE_RESERVATION_UPSERT`
- `ROUTE_RESERVATION_RELEASE`
- `REGISTRY_SNAPSHOT_REQUEST`
- `REGISTRY_SNAPSHOT_RESPONSE`

ControlServer 장애가 발생해도 이미 연결된 Client-SocketServer 세션은 유지됩니다. 클라이언트는 설정된 ControlServer endpoint 목록을 순회해 route를 다시 요청할 수 있습니다.

ControlServer가 재시작되면 저장된 registry를 로드하되, heartbeat timeout이 지난 서버는 `Unhealthy`로 정규화하고 route 후보에서 제외합니다. 만료된 reservation은 제거하며, 서버가 다시 register/heartbeat를 보내면 최신 상태로 복구됩니다.

## Dashboard

Dashboard는 ControlServer registry snapshot을 조회해 전체 서버 정보를 표시합니다. ControlServer가 없으면 로컬 fallback server 상태를 표시합니다.

표시 항목:

- 전체 수용 가능 클라이언트 수
- 현재 접속 수
- 접속 가능 수
- 서버별 health
- 서버별 endpoint
- CPU/MEM/STORAGE 사용률

운영 API:

- `GET /api/server/status`: dashboard와 cluster snapshot
- `GET /health/live`: dashboard process liveness
- `GET /health/ready`: dashboard TCP server readiness
- `GET /metrics`: cluster connection counters, local socket counters, socket async args pool counters
