# Protocols

## Common Frame

모든 프로토콜 메시지는 TLS로 보호되는 소켓 연결 위에서 12바이트 고정 헤더를 사용합니다. 정수는 big-endian(network byte order)입니다.

```text
0..3   clientId      uint32
4..7   messageId     uint32
8..11  payloadLength uint32
12..   payload       byte[payloadLength]
```

payload 최대 길이는 4KB입니다.

`SocketClient`, `SocketServer`, `SocketControl`, `SocketDashboard` 간 연결은 `SecureSocketConnection`을 통해 인증된 뒤 common frame을 송수신합니다. 로컬 Root CA가 모듈별 leaf 인증서를 서명하고, 클라이언트는 해당 Root CA 체인을 검증합니다. 기본 모드는 런타임/OS가 지원하는 가장 적절한 TLS 버전을 협상하고, `SOCKET_REQUIRE_TLS13=true` 환경 변수를 설정하면 TLS 1.3이 아닌 협상 결과를 연결 실패로 처리합니다.

## HealthCheck

```text
1 PING
2 PONG
```

`PING`은 payload가 없습니다. `PONG` payload는 `OK`입니다.

클라이언트는 `StartHealthCheckLoop()`로 기본 30초 간격 keepalive를 실행할 수 있습니다.

## HelloWorld

```text
100 HelloWorldRequest
101 HelloWorldResponse
```

`HelloWorldResponse` payload는 기본 `Hello, World!`입니다.

## Client Message

클라이언트 간 메시지는 SocketServer를 통해 전달됩니다. 같은 서버에 target client가 있으면 로컬 delivery로 처리하고, 다른 서버에 있으면 서버 간 relay를 사용합니다.

```text
2000 CLIENT_REGISTER
2001 CLIENT_REGISTER_ACK
2002 CLIENT_MESSAGE_SEND
2003 CLIENT_MESSAGE_DELIVER
2004 CLIENT_MESSAGE_ACK
2005 CLIENT_MESSAGE_ERROR
```

`CLIENT_MESSAGE_SEND`:

```json
{
  "messageToken": "f1f0c3...",
  "sourceClientId": 10001,
  "targetClientId": 10002,
  "content": "hello",
  "ttlSeconds": 10,
  "createdAt": "2026-06-03T00:00:00Z"
}
```

`CLIENT_MESSAGE_DELIVER`는 source/target/client content를 target client에 전달합니다. Source client는 delivery 결과를 `CLIENT_MESSAGE_ACK` 또는 `CLIENT_MESSAGE_ERROR`로 받습니다.

## Server Relay

SocketServer 간 메시지 전달은 서버 listen endpoint로 직접 TLS 소켓 연결해 처리합니다.

```text
2100 SERVER_RELAY_MESSAGE
2101 SERVER_RELAY_ACK
2102 SERVER_RELAY_ERROR
```

Source 서버는 ControlServer에서 target client 위치를 조회한 뒤 target 서버로 `SERVER_RELAY_MESSAGE`를 보냅니다. Target 서버는 target client가 로컬에 연결되어 있으면 `CLIENT_MESSAGE_DELIVER`를 전송하고 relay ack를 반환합니다.

## Control Plane

ControlServer, SocketServer, SocketClient는 common frame 위에 JSON payload를 사용합니다.

Server-Control:

```text
1000 SERVER_REGISTER
1001 SERVER_REGISTER_ACK
1002 SERVER_HEARTBEAT
1003 SERVER_UNREGISTER
1004 SERVER_HEARTBEAT_ACK
1100 SESSION_OPENED
1101 SESSION_UPDATED
1102 SESSION_CLOSED
```

Client-Control:

```text
1200 ROUTE_REQUEST
1201 ROUTE_RESPONSE
1202 ROUTE_RESOLVE_FAILED
1210 CLIENT_LOCATION_REQUEST
1211 CLIENT_LOCATION_RESPONSE
1212 CLIENT_LOCATION_NOT_FOUND
```

Control-Control:

```text
1400 CONTROL_REGISTER
1401 CONTROL_REGISTER_ACK
1402 CONTROL_HEARTBEAT
1410 SERVER_REGISTRY_UPSERT
1411 SERVER_REGISTRY_REMOVE
1420 SESSION_SUMMARY_UPSERT
1421 SESSION_SUMMARY_REMOVE
1422 CLIENT_LOCATION_UPSERT
1423 CLIENT_LOCATION_REMOVE
1430 ROUTE_RESERVATION_UPSERT
1431 ROUTE_RESERVATION_RELEASE
1440 REGISTRY_SNAPSHOT_REQUEST
1441 REGISTRY_SNAPSHOT_RESPONSE
```

## Server Heartbeat

`SERVER_HEARTBEAT`는 서버별 capacity와 리소스 사용률을 포함합니다.

```json
{
  "clusterId": "socket-cluster-1",
  "serverId": 1,
  "instanceId": "server-1-a",
  "host": "127.0.0.1",
  "port": 5100,
  "health": "Healthy",
  "maxConnections": 10000,
  "currentConnections": 5231,
  "reservedConnections": 37,
  "availableConnections": 4728,
  "resourceUsage": {
    "cpuUsagePercent": 42.8,
    "memoryUsagePercent": 67.1,
    "storageUsagePercent": 51.4
  }
}
```

## Route

`ROUTE_REQUEST`:

```json
{
  "clientId": 10001,
  "preferredServerId": null,
  "routingPolicy": "MostAvailableConnections"
}
```

`ROUTE_RESPONSE`:

```json
{
  "success": true,
  "reservationId": "control-1-100",
  "serverId": 1,
  "instanceId": "server-1-a",
  "host": "127.0.0.1",
  "port": 5100,
  "expiresAt": "2026-06-03T00:00:10Z"
}
```

클라이언트는 응답받은 SocketServer endpoint로 직접 TCP 연결을 생성합니다.
