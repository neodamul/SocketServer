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
- SocketServer heartbeat timeout 감지
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

SocketServer는 설정된 port range에서 사용 가능한 포트를 찾아 바인딩합니다. TCP listener는 같은 포트에 중복 바인딩되지 않도록 `ReuseAddress`를 기본 적용하지 않으며, 바인딩 후 ControlServer에 등록하고 주기적으로 heartbeat를 전송합니다.

SocketServer는 configured ControlServer 전체에 register, heartbeat, session open/update/close를 직접 보고합니다. 2대 active-active 구성에서는 A/B 양쪽에 병렬 전송하고, 하나 이상의 ControlServer 보고가 성공하면 느린 endpoint 때문에 호출 흐름이 장시간 막히지 않도록 짧은 grace 이후 계속 진행합니다. 남은 endpoint 전송은 자체 operation timeout으로 성공/실패를 기록합니다. Heartbeat loop는 예외가 발생해도 다음 주기에 재시도하며, ControlServer 재시작이나 registry 유실을 보정하기 위해 register metadata를 주기적으로 다시 전송합니다. ControlServer는 개별 요청 채널이 닫혔다는 이유만으로 SocketServer를 `Unhealthy`로 바꾸지 않고, heartbeat timeout을 초과한 서버만 cleanup scheduler에서 신규 route 및 client message location 후보에서 제외합니다. SocketServer가 재연결하면 register, heartbeat, session update로 상태를 다시 보정합니다.

클라이언트가 `CLIENT_REGISTER` 또는 다른 메시지를 보내면 SocketServer는 `clientId -> session` 인덱스를 갱신하고 ControlServer에 session/client location을 전파합니다. 연결 종료, read 실패, healthcheck 중단으로 인한 idle timeout, delivery send 실패는 session close로 정규화되어 location 제거 이벤트로 이어집니다.

SocketServer는 register 직후 ControlServer registry snapshot에서 healthy SocketServer 목록을 가져와 relay server list를 warm-up하고, 주기적 heartbeat 중 제한된 간격으로 갱신합니다. Heartbeat마다 snapshot TLS 요청이 발생하지 않도록 refresh는 throttle 처리합니다. 클라이언트 메시지가 로컬 target을 찾지 못하면 먼저 cached relay server list에 병렬 broadcast하고, cache가 비어 있을 때만 snapshot refresh를 기다립니다. Broadcast가 실패하면 기존 ControlServer target location 조회를 fallback으로 사용합니다.

SocketServer의 session event는 bounded queue를 통해 ControlServerReporter가 전송합니다. 이벤트 폭주 시 무제한 task/memory 증가를 막고, reporter worker에서 endpoint별 전송 실패와 timeout을 기록합니다.

ControlServer와 SocketServer는 request/response와 relay 처리를 전담 큐와 long-running worker로 분리합니다. Listener/receive 루프는 socket accept와 frame read에 집중하고, command worker가 business command를 처리하며, response worker가 실제 socket write를 담당합니다. 서버 간 relay도 request/response queue를 따로 사용해 느린 peer, timeout, TLS handshake 지연이 heartbeat나 다음 command 처리까지 연쇄 지연되지 않도록 합니다. ControlServer peer relay/snapshot, SocketServer의 ControlServer lookup, SocketServer 간 relay는 endpoint별 persistent secure channel을 유지하며 정상 경로에서 매 요청마다 TCP/TLS handshake를 반복하지 않습니다. 장애, timeout, send/receive 실패가 발생한 channel만 닫고 다음 요청에서 재연결합니다. SocketClient의 healthcheck loop와 SocketServer reporter loop도 공통 전담 워커에서 실행되어 ThreadPool 혼잡과 분리됩니다.

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

ControlServer는 `peers` 설정을 통해 서로 registry 정보를 공유합니다. 서버 register/heartbeat, session open/update/close, route reservation 변경 이벤트가 발생하면 peer로 즉시 relay하고, `peerSnapshotSyncIntervalSeconds` 간격으로 full snapshot을 다시 가져와 누락된 이벤트를 보정합니다. 2대 구성에서는 SocketServer가 A/B에 직접 보고하므로 peer relay는 보조 보정 경로로 사용합니다. Server snapshot merge는 ControlServer별 local version이 아니라 `LastHeartbeatAt`/`UpdatedAt` 기준으로 최신 heartbeat를 반영합니다. Registry는 기본적으로 파일 저장소에 서버 snapshot, route reservation, session summary, client location을 저장할 수 있으며, 테스트나 임시 실행에는 `InMemory` 저장소를 사용할 수 있습니다.

- `SERVER_REGISTRY_UPSERT`
- `SESSION_SUMMARY_UPSERT`
- `SESSION_SUMMARY_REMOVE`
- `ROUTE_RESERVATION_UPSERT`
- `ROUTE_RESERVATION_RELEASE`
- `REGISTRY_SNAPSHOT_REQUEST`
- `REGISTRY_SNAPSHOT_RESPONSE`

ControlServer 장애가 발생해도 이미 연결된 Client-SocketServer 세션은 유지됩니다. 클라이언트는 설정된 ControlServer endpoint 목록을 순회해 route를 다시 요청할 수 있습니다.

ControlServer가 재시작되면 저장된 registry를 로드하되, heartbeat timeout이 지난 서버는 `Unhealthy`로 정규화하고 route 후보에서 제외합니다. 만료된 reservation은 제거하며, 서버가 다시 register/heartbeat를 보내면 최신 상태로 복구됩니다. 같은 SocketServer instance가 더 새로운 `StartedAt`으로 재등록되면 이전 실행에서 남은 session summary, client location, reservation을 제거해 대시보드 접속 수 중복 집계를 방지합니다.

## Dashboard

Dashboard는 설정된 ControlServer 목록을 조회하고 Server Inventory에 `ControlServer` type으로 표시합니다. 여러 ControlServer가 응답하면 SocketServer `instanceId`별 최신 heartbeat snapshot을 병합해 전체 SocketServer 정보를 표시합니다. Dashboard 내부 서버 상태는 ControlServer 연결 여부와 무관하게 `Dashboard` type으로 항상 함께 표시됩니다. ControlServer가 없으면 로컬 Dashboard server 상태를 fallback cluster로 사용합니다. 일시적인 ControlServer timeout이나 unavailable 응답이 발생하면 마지막 정상 cluster snapshot과 endpoint별 counters를 유지해 서버 목록이 갑자기 사라지지 않도록 합니다. Server Inventory에서 서버 row를 선택하면 하단의 Server, Traffic, Socket Runtime, Details 패널이 선택된 서버 기준으로 갱신됩니다.
브라우저 UI는 기본 30초 간격으로 상태 API를 갱신하고, 사용자가 refresh interval 콤보에서 5초, 10초, 30초, 60초 중 하나를 선택할 수 있습니다.

표시 항목:

- 전체 수용 가능 클라이언트 수
- 현재 접속 수
- 접속 가능 수
- ControlServer, SocketServer, Dashboard 수 분리 표시
- ControlServer/SocketServer/Dashboard type별 endpoint와 상태
- 서버별 health
- 서버별 endpoint
- CPU/MEM/STORAGE 사용률

운영 API:

- `GET /api/server/status`: dashboard와 cluster snapshot
- `GET /health/live`: dashboard process liveness
- `GET /health/ready`: dashboard TCP server readiness
- `GET /metrics`: cluster connection counters, local socket counters, socket async args pool counters
