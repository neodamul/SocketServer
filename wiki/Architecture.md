# Architecture

## Overview

SocketServer는 TCP 서버를 여러 인스턴스로 확장하고, ControlServer가 서버 상태를 취합해 클라이언트에게 접속할 SocketServer 정보를 내려주는 구조입니다.

```text
Client -> ControlServer:5000  ROUTE_REQUEST
Client <- ControlServer       ROUTE_RESPONSE(host, port)
Client -> SocketServer        direct TCP connection
```

ControlServer는 TCP payload를 프록시하지 않습니다. 실제 장기 연결은 클라이언트와 SocketServer가 직접 유지합니다.

## Projects

```text
SocketCommon      shared frame, protocols, config, diagnostics, logging
SocketClient      client library
SocketControl     broker and cluster registry
SocketServer      TCP backend server
SocketDashboard   status API and web dashboard
SocketLoadTest    load test tool
SocketTests       MSTest suite
```

## ControlServer

ControlServer 역할:

- SocketServer `SERVER_REGISTER` 수신
- SocketServer `SERVER_HEARTBEAT` 수신
- session opened/updated/closed event 수신
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

서버 기본 정책:

- `maxConnections`: 기본 10,000
- `pendingAcceptCount`: 기본 100
- `idleTimeoutSeconds`: 기본 90
- healthcheck interval: 기본 30초

## ControlServer Active-Active

ControlServer는 `peers` 설정을 통해 서로 registry 정보를 공유합니다. 현재 구현은 in-memory registry 기반이며 다음 메시지를 peer로 전파합니다.

- `SERVER_REGISTRY_UPSERT`
- `SESSION_SUMMARY_UPSERT`
- `SESSION_SUMMARY_REMOVE`
- `ROUTE_RESERVATION_UPSERT`
- `ROUTE_RESERVATION_RELEASE`
- `REGISTRY_SNAPSHOT_REQUEST`
- `REGISTRY_SNAPSHOT_RESPONSE`

ControlServer 장애가 발생해도 이미 연결된 Client-SocketServer 세션은 유지됩니다. 클라이언트는 설정된 ControlServer endpoint 목록을 순회해 route를 다시 요청할 수 있습니다.

## Dashboard

Dashboard는 ControlServer registry snapshot을 조회해 전체 서버 정보를 표시합니다. ControlServer가 없으면 로컬 fallback server 상태를 표시합니다.

표시 항목:

- 전체 수용 가능 클라이언트 수
- 현재 접속 수
- 접속 가능 수
- 서버별 health
- 서버별 endpoint
- CPU/MEM/STORAGE 사용률
