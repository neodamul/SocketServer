# Protocols

## Common Frame

모든 프로토콜 메시지는 12바이트 고정 헤더를 사용합니다. 정수는 big-endian(network byte order)입니다.

```text
0..3   clientId      uint32
4..7   messageId     uint32
8..11  payloadLength uint32
12..   payload       byte[payloadLength]
```

payload 최대 길이는 4KB입니다.

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
