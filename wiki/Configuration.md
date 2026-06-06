# Configuration

각 실행 프로젝트는 JSON 설정 파일을 사용합니다.

```text
SocketControl/config.json
SocketServer/config.json
SocketClient/config.json
```

각 프로젝트는 자체 `log4net.config`를 관리합니다. 실행 프로젝트와 테스트 프로젝트는 빌드 출력으로 자신의 `log4net.config`를 복사하고, 라이브러리 프로젝트는 프로젝트별 설정 파일만 보관해 참조 프로젝트 간 설정 파일 충돌을 피합니다.

`host`, `bindHost`, `controlEndpoints[].host`, `controlServers[].host`, `peers[].host`는 `127.0.0.1` 같은 IP literal과 `localhost` 같은 DNS host name을 모두 사용할 수 있습니다. 연결 시 socket address family에 맞는 주소를 우선 선택합니다.

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

로그 파일은 프로젝트별 `logs/` 디렉터리에 분리 저장합니다.

- 일반 로그: `logs/<project>.log`
- relay 로그: `logs/<project>.relay.log`

일반 로그는 서버 시작/종료, 연결, healthcheck, route, cleanup, 테스트 진행 상태를 기록합니다. Relay 로그는 `SocketRelay` logger로 분리되어 ControlServer peer relay/snapshot sync, SocketServer relay server refresh, client-to-client message relay, target location 조회, broadcast/targeted relay 결과를 기록합니다. 파일 로그는 DEBUG까지 저장하고 콘솔은 INFO 이상만 출력해 테스트 실행 중에는 핵심 진행 상태를 보고, 실패 분석은 로그 파일에서 상세 단계까지 확인합니다.

## Security

실행 프로젝트는 `security` 설정을 통해 전송 보안 정책을 적용합니다. 기본값은 TLS입니다.

```json
{
  "security": {
    "transportMode": "Tls",
    "tlsProtocol": "Tls13",
    "requireTls13": true,
    "requireClientCertificate": false,
    "certificateDirectory": "",
    "certificatePasswordEnvironmentVariable": "SOCKET_CERTIFICATE_PASSWORD",
    "certificateRenewBeforeDays": 30,
    "rootCertificateLifetimeYears": 10,
    "moduleCertificateLifetimeYears": 2,
    "authenticationTimeoutMilliseconds": 30000,
    "messageEncryptionSecretEnvironmentVariable": "SOCKET_MESSAGE_SECRET"
  }
}
```

`transportMode`는 `Tls` 또는 `MessageEncryption`입니다. `Tls`는 `SslStream` 기반 TLS 연결을 사용하고, `MessageEncryption`은 TLS handshake 없이 각 frame payload를 AES-GCM-256으로 암호화하고 HMAC-SHA256으로 envelope를 검증합니다. `transportMode`를 비워두고 `tlsProtocol=None`을 설정해도 `MessageEncryption` 모드로 해석합니다.

`MessageEncryption` 모드는 모든 노드와 클라이언트가 같은 secret을 환경 변수로 제공해야 합니다. 기본 변수명은 `SOCKET_MESSAGE_SECRET`이며, `messageEncryptionSecretEnvironmentVariable`로 바꿀 수 있습니다. secret은 base64 또는 일반 문자열을 사용할 수 있고 내부적으로 AES-GCM key와 HMAC key를 분리 파생합니다.

`tlsProtocol`은 기본 `Tls13`입니다. `Auto`로 설정하면 OS/.NET 기본 협상을 사용합니다. 운영 설정은 TLS 1.3 강제를 기본으로 둡니다.
`requireClientCertificate=true`로 설정하면 mTLS를 사용하며, 클라이언트도 같은 Root CA로 서명된 모듈 인증서를 제시해야 합니다.

## Socket Options

`socketOptions`는 외부 노드에 접속하거나 frame을 읽고 쓰는 네트워크 operation timeout을 설정합니다. 설정이 없거나 0 이하이면 기본값 30초를 사용합니다.

```json
{
  "socketOptions": {
    "connectTimeoutSeconds": 30,
    "readTimeoutSeconds": 30,
    "writeTimeoutSeconds": 30
  }
}
```

- `connectTimeoutSeconds`: ControlServer, SocketServer, client 간 TCP 연결 제한 시간
- `readTimeoutSeconds`: frame header/payload 읽기 제한 시간
- `writeTimeoutSeconds`: frame write/flush 제한 시간

## Dashboard

`SocketDashboard/appsettings.json`은 조회할 ControlServer endpoint를 설정합니다. `dashboard.controlServers` 배열을 사용하면 여러 ControlServer를 Server Inventory에 `ControlServer` type으로 항상 표시하고, 각 endpoint별 조회 상태를 확인할 수 있습니다. 기존 단일 `dashboard.controlServer` 설정은 fallback으로 유지됩니다.

```json
{
  "dashboard": {
    "controlServers": [
      {
        "host": "127.0.0.1",
        "port": 5000
      },
      {
        "host": "127.0.0.1",
        "port": 5002
      }
    ]
  }
}
```

## Certificates

각 모듈은 최초 TLS 연결 시 로컬 인증서를 자동 생성합니다. Root CA는 하나만 만들고, 모듈별 leaf 인증서는 이 Root CA로 서명합니다.

```text
Certificates/SocketServerLocalRootCA.pfx
Certificates/SocketClient.pfx
Certificates/SocketServer.pfx
Certificates/SocketControl.pfx
Certificates/SocketDashboard.pfx
```

기본 저장 위치는 솔루션 루트의 `Certificates/`입니다. `security.certificateDirectory` 또는 `SOCKET_CERTIFICATE_DIR` 환경 변수를 설정하면 다른 로컬 경로를 사용할 수 있습니다. PFX 비밀번호는 `security.certificatePasswordEnvironmentVariable`에 지정한 환경 변수에서 읽으며, 기본 변수명은 `SOCKET_CERTIFICATE_PASSWORD`입니다. 인증서는 로컬 개발/테스트용이며 leaf subject는 `CN=SocketServerLocal`, SAN은 `SocketServerLocal`, `localhost`입니다. TLS 1.3을 필수로 검증해야 하는 환경은 `security.requireTls13=true` 또는 `SOCKET_REQUIRE_TLS13=true`를 설정합니다. 플랫폼이 TLS 1.3을 협상하지 못하면 연결은 실패합니다.

Root CA와 모듈 인증서는 `certificateRenewBeforeDays` 이내로 만료가 가까워지거나, 현재 설정된 PFX 비밀번호로 읽을 수 없으면 삭제 후 재생성됩니다.

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
    "peerSnapshotSyncIntervalSeconds": 30,
    "routeReservationSeconds": 10,
    "routingPolicy": "MostAvailableConnections",
    "degradedCpuPercent": 85,
    "degradedMemoryPercent": 85,
    "degradedStoragePercent": 90
  },
  "peers": [],
  "registry": {
    "provider": "File",
    "syncMode": "ActiveActive",
    "connectionString": "control-registry.json"
  }
}
```

주요 항목:

- `port`: 클라이언트 route 요청과 SocketServer 등록/heartbeat를 받는 기본 포트
- `peerSyncPort`: ControlServer 간 동기화 포트
- `heartbeatTimeoutSeconds`: timeout이 지난 서버는 route 후보에서 제외
- `peerSnapshotSyncIntervalSeconds`: peer full snapshot을 주기적으로 가져와 누락 이벤트를 보정하는 간격
- `routeReservationSeconds`: route 응답 후 실제 접속 전까지의 짧은 예약 TTL
- `degraded*Percent`: resource threshold 초과 시 `Degraded`
- `registry.provider`: `InMemory` 또는 `File`
- `registry.connectionString`: `File` provider의 저장 경로. 비어 있으면 실행 디렉터리의 `{nodeId}-registry.json`을 사용

기본 실행 config는 `File` provider로 ControlServer registry를 저장합니다. 테스트나 임시 실행처럼 상태 공유가 필요 없으면 `InMemory`를 사용합니다. registry 파일에는 서버 snapshot, route reservation, session summary, client location이 저장되며 메시지 payload 프로토콜은 계속 protobuf를 사용합니다.

## SocketServer

```json
{
  "socketAsyncEventArgsPool": {
    "initialSize": 1000,
    "growthSize": 100,
    "maxRetained": 20000
  },
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

`socketAsyncEventArgsPool`은 accept/send/receive에 사용하는 `SocketAsyncEventArgs` pool을 설정합니다. 운영에서는 목표 동접과 메시지 빈도에 맞춰 `initialSize`, `growthSize`, `maxRetained`를 조정합니다. 각 SAEA는 8KB 고정 receive buffer segment를 유지하며, segment는 슬랩 단위로 선할당됩니다.

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

## Sample Clients

샘플 클라이언트 설정:

```text
Samples/SocketSample.Net/appsettings.json
Samples/SocketSample.Android/app/src/main/res/raw/config.json
```

공통 설정 항목:

- `clientId`: 샘플 클라이언트 ID
- `clientName`: 화면과 로그에서 사용할 이름
- `host`, `port`: 직접 SocketServer 또는 ControlServer endpoint
- `useControlServer`: `true`이면 ControlServer route 요청 후 SocketServer에 연결
- `receiveTimeoutSeconds`: receive 버튼 대기 시간
- `healthCheckIntervalSeconds`: 등록 후 keepalive healthcheck 전송 간격
- `security`: TLS/mTLS 설정

iOS/macOS 샘플은 화면에서 `Client ID`, `Host`, `Port`, local self-signed certificate 허용 여부를 설정합니다. Swift 샘플 프로젝트 파일은 `Samples/SocketSample.iOS/project.yml`, `Samples/SocketSample.macOS/project.yml`에서 XcodeGen으로 생성합니다.
