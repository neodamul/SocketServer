# Configuration

각 실행 프로젝트는 JSON 설정 파일을 사용합니다.

```text
SocketControl/config.json
SocketServer/config.json
SocketClient/config.json
```

각 프로젝트는 자체 `log4net.config`를 관리합니다. 실행 프로젝트와 테스트 프로젝트는 빌드 출력으로 자신의 `log4net.config`를 복사하고, 라이브러리 프로젝트는 프로젝트별 설정 파일만 보관해 참조 프로젝트 간 설정 파일 충돌을 피합니다.

```text
SocketCommon/log4net.config
SocketClient/log4net.config
SocketControl/log4net.config
SocketServer/log4net.config
SocketDashboard/log4net.config
SocketLoadTest/log4net.config
SocketTests/log4net.config
```

`SocketLoadTest`는 부하 테스트용 `log4net.load-test.config`를 우선 사용하고, 해당 파일이 없으면 `log4net.config`를 fallback으로 사용합니다.

## Security

실행 프로젝트는 `security` 설정을 통해 TLS 정책을 적용합니다.

```json
{
  "security": {
    "tlsProtocol": "Tls13",
    "requireTls13": true,
    "certificateDirectory": "",
    "authenticationTimeoutMilliseconds": 5000
  }
}
```

`tlsProtocol`은 기본 `Tls13`입니다. `Auto` 또는 `None`으로 설정하면 OS/.NET 기본 협상을 사용합니다. 운영 설정은 TLS 1.3 강제를 기본으로 둡니다.

## Certificates

각 모듈은 최초 TLS 연결 시 로컬 인증서를 자동 생성합니다. Root CA는 하나만 만들고, 모듈별 leaf 인증서는 이 Root CA로 서명합니다.

```text
Certificates/SocketServerLocalRootCA.pfx
Certificates/SocketClient.pfx
Certificates/SocketServer.pfx
Certificates/SocketControl.pfx
Certificates/SocketDashboard.pfx
```

기본 저장 위치는 솔루션 루트의 `Certificates/`입니다. `security.certificateDirectory` 또는 `SOCKET_CERTIFICATE_DIR` 환경 변수를 설정하면 다른 로컬 경로를 사용할 수 있습니다. 인증서는 로컬 개발/테스트용이며 leaf subject는 `CN=SocketServerLocal`, SAN은 `SocketServerLocal`, `localhost`입니다. TLS 1.3을 필수로 검증해야 하는 환경은 `security.requireTls13=true` 또는 `SOCKET_REQUIRE_TLS13=true`를 설정합니다. 플랫폼이 TLS 1.3을 협상하지 못하면 연결은 실패합니다.

## ControlServer

```json
{
  "controlServer": {
    "clusterId": "socket-cluster-1",
    "nodeId": "control-1",
    "host": "127.0.0.1",
    "port": 5000,
    "peerSyncPort": 5020,
    "heartbeatTimeoutSeconds": 90,
    "routeReservationSeconds": 10,
    "routingPolicy": "MostAvailableConnections",
    "degradedCpuPercent": 85,
    "degradedMemoryPercent": 85,
    "degradedStoragePercent": 90
  },
  "peers": [],
  "registry": {
    "provider": "InMemory",
    "syncMode": "ActiveActive",
    "connectionString": ""
  }
}
```

주요 항목:

- `port`: 클라이언트 route 요청과 SocketServer 등록/heartbeat를 받는 기본 포트
- `peerSyncPort`: ControlServer 간 동기화 포트
- `heartbeatTimeoutSeconds`: timeout이 지난 서버는 route 후보에서 제외
- `routeReservationSeconds`: route 응답 후 실제 접속 전까지의 짧은 예약 TTL
- `degraded*Percent`: resource threshold 초과 시 `Degraded`

## SocketServer

```json
{
  "controlServers": [
    {
      "host": "127.0.0.1",
      "port": 5000
    }
  ],
  "servers": [
    {
      "serverId": 1,
      "instanceId": "server-1-a",
      "name": "socket-server-1",
      "bindHost": "127.0.0.1",
      "portRangeStart": 5100,
      "portRangeEnd": 5199,
      "maxConnections": 10000,
      "pendingAcceptCount": 100,
      "idleTimeoutSeconds": 90,
      "heartbeatIntervalSeconds": 30
    }
  ]
}
```

`portRangeStart=0`, `portRangeEnd=0`이면 OS 동적 포트 바인딩을 사용합니다. 운영 설정은 명시적인 port range 사용을 권장합니다.

## SocketClient

```json
{
  "client": {
    "clientId": 1,
    "name": "socket-client-1",
    "controlEndpoints": [
      {
        "host": "127.0.0.1",
        "port": 5000
      }
    ],
    "healthCheckIntervalSeconds": 30
  }
}
```

`controlEndpoints`는 여러 개 설정할 수 있습니다. 클라이언트는 ControlServer 장애 시 다음 endpoint로 route 요청을 시도할 수 있습니다.
