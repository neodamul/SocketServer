# SocketSample.macOS

Native macOS SwiftUI sample client built with Network.framework.

```bash
xcodegen generate --spec Samples/SocketSample.macOS/project.yml
xcodebuild -project Samples/SocketSample.macOS/SocketSampleMac.xcodeproj -scheme SocketSampleMac -configuration Debug -derivedDataPath Samples/SocketSample.macOS/build CODE_SIGNING_ALLOWED=NO build
```

The UI supports connection, client registration, background receive, healthcheck keepalive, and client-to-client message send/receive. `Connect` performs both TCP/TLS connection and client registration. The status panel shows the connected SocketServer endpoint returned by ControlServer routing.

`Control Endpoints` accepts one endpoint per line or comma-separated `host:port` entries. When `Use ControlServer route` is enabled, route resolution and routed SocketServer connection retry across those endpoints before failing. If a route response contains a loopback host, the app replaces it with the ControlServer host that returned the route.

For the default `EndToEndTls` profile, pass the local client certificate directory and password so the app can present `SocketClient-{clientId}.pfx` during mTLS:

```bash
open -n Samples/SocketSample.macOS/build/Build/Products/Debug/SocketSampleMac.app --args --client-id 101 --client-name native-client-101 --host 127.0.0.1 --port 10000 --use-control-server true --auto-connect true --target-client-id 102 --certificate-dir Certificates --certificate-password socket-local-dev
open -n Samples/SocketSample.macOS/build/Build/Products/Debug/SocketSampleMac.app --args --client-id 102 --client-name native-client-102 --host 127.0.0.1 --port 10000 --use-control-server true --auto-connect true --target-client-id 101 --certificate-dir Certificates --certificate-password socket-local-dev
```

`Transport=MessageEncryption` connects without TLS and uses AES-GCM/HMAC frame protection. In that mode, `Message Secret` must match the server's `SOCKET_MESSAGE_SECRET`.
