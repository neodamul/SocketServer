# SocketSample.iOS

Native SwiftUI iOS sample client built with Network.framework.

```bash
xcodegen generate --spec Samples/SocketSample.iOS/project.yml
xcodebuild -project Samples/SocketSample.iOS/SocketSampleiOS.xcodeproj -scheme SocketSampleiOS -sdk iphonesimulator -configuration Debug CODE_SIGNING_ALLOWED=NO build
```

The UI supports connection, client registration, background receive, healthcheck keepalive, and client-to-client message send/receive. `Connect` performs both TCP/TLS connection and client registration. The status panel shows the connected SocketServer endpoint returned by ControlServer routing.

`Control Endpoints` accepts one endpoint per line or comma-separated `host:port` entries. When `Use ControlServer route` is enabled, route resolution and routed SocketServer connection retry across those endpoints before failing. If a route response contains a loopback host, the app replaces it with the ControlServer host that returned the route.

`Transport=MessageEncryption` connects without TLS and uses AES-GCM/HMAC frame protection. In that mode, `Message Secret` must match the server's `SOCKET_MESSAGE_SECRET`.
