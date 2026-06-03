# Protocols

## Common Frame

모든 프로토콜 메시지는 보안 전송 연결 위에서 12바이트 고정 헤더를 사용합니다. 정수는 big-endian(network byte order)입니다.

```text
0..3   clientId      uint32
4..7   messageId     uint32
8..11  payloadLength uint32
12..   payload       byte[payloadLength]
```

애플리케이션 payload 최대 길이는 4KB입니다. payload는 JSON 문자열이 아니라 `SocketCommon/Proto/SocketMessages.proto`에 정의된 protobuf 모델로 직렬화합니다.

`SocketClient`, `SocketServer`, `SocketControl`, `SocketDashboard` 간 연결은 `SecureSocketConnection`을 통해 보호된 뒤 common frame을 송수신합니다. 기본 `Tls` 모드는 `SslStream`을 사용합니다. 로컬 Root CA가 모듈별 leaf 인증서를 서명하고, 클라이언트는 해당 Root CA 체인을 검증합니다. `security.requireClientCertificate=true`이면 서버도 클라이언트 인증서를 같은 Root CA 기준으로 검증합니다. `SOCKET_REQUIRE_TLS13=true` 환경 변수를 설정하면 TLS 1.3이 아닌 협상 결과를 연결 실패로 처리합니다.

`MessageEncryption` 모드는 TLS handshake를 사용하지 않고 각 frame payload를 보호합니다. wire payload는 다음 envelope입니다.

```text
0      version       byte
1..12  nonce         byte[12]
13..28 aesGcmTag     byte[16]
29..N  cipherText    byte[payloadLength]
N..    hmacSha256    byte[32]
```

AES-GCM은 `clientId + messageId`를 associated data로 사용하고, HMAC-SHA256은 wire header와 envelope를 검증합니다. 수신 측은 envelope를 검증/복호화한 뒤 기존 12바이트 frame으로 복원하므로 상위 protobuf 프로토콜은 동일합니다.

## HealthCheck

```text
1 PING
2 PONG
```

`PING`, `PONG`은 `ProtoHealthCheckMessage` payload를 사용합니다. `PONG`의 `status`는 `OK`입니다.

클라이언트는 `StartHealthCheckLoop()`로 기본 30초 간격 keepalive를 실행할 수 있습니다.
SocketServer는 `PING`을 포함한 정상 frame 수신 시 session activity를 갱신합니다. healthcheck가 중단되어 `idleTimeoutSeconds`를 초과하면 cleanup scheduler가 해당 client session을 닫고 ControlServer에 session close를 전파합니다.

## HelloWorld

```text
100 HelloWorldRequest
101 HelloWorldResponse
```

`HelloWorldRequest`, `HelloWorldResponse`는 protobuf payload를 사용합니다. `HelloWorldResponse.message` 기본값은 `Hello, World!`입니다.

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

`CLIENT_MESSAGE_SEND` payload:

```text
ProtoClientMessageSendRequest
message_token     string
source_client_id  uint32
target_client_id  uint32
content           string
ttl_seconds       int32
created_at_unix_ms int64
```

`CLIENT_MESSAGE_DELIVER`는 source/target/client content를 target client에 전달합니다. Source client는 delivery 결과를 `CLIENT_MESSAGE_ACK` 또는 `CLIENT_MESSAGE_ERROR`로 받습니다.

## Server Relay

SocketServer 간 메시지 전달은 서버 listen endpoint로 직접 TLS 소켓 연결해 처리합니다.

```text
2100 SERVER_RELAY_MESSAGE
2101 SERVER_RELAY_ACK
2102 SERVER_RELAY_ERROR
```

Source 서버는 ControlServer registry snapshot으로 healthy SocketServer 목록을 갱신하고, target client가 로컬에 없으면 known SocketServer 전체에 `SERVER_RELAY_MESSAGE`를 병렬 broadcast합니다. target client를 보유한 서버만 `CLIENT_MESSAGE_DELIVER` 전송 후 ACK를 반환하고, 나머지 서버는 `TargetNotConnected` 오류를 반환합니다. Broadcast가 실패하면 기존 ControlServer client location 조회와 targeted relay를 fallback으로 사용합니다.

## Control Plane

ControlServer, SocketServer, SocketClient는 common frame 위에 protobuf payload를 사용합니다.

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
ControlServer는 SocketServer 요청 채널이 닫혀도 즉시 장애로 처리하지 않습니다. heartbeat timeout을 초과한 서버 snapshot만 cleanup scheduler에서 `Unhealthy`로 정규화해 route 후보에서 제외합니다.

```text
ProtoServerHeartbeatRequest
cluster_id                 string
server_id                  int32
instance_id                string
host                       string
port                       int32
health                     ProtoServerHealthState
max_connections            int32
current_connections        int32
reserved_connections       int32
available_connections      int32
resource_usage             ProtoResourceUsageSnapshot
total_accepted_clients     int64
total_closed_clients       int64
total_rejected_clients     int64
total_idle_timeout_clients int64
sent_at_unix_ms            int64
```

## Route

`ROUTE_REQUEST`:

```text
ProtoRouteRequest
client_id           uint32
preferred_server_id optional int32
routing_policy      string
```

`ROUTE_RESPONSE`:

```text
ProtoRouteResponse
success            bool
reservation_id     string
server_id          int32
instance_id        string
host               string
port               int32
expires_at_unix_ms int64
error_message      string
```

클라이언트는 응답받은 SocketServer endpoint로 직접 TCP 연결을 생성합니다.
