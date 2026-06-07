# SocketServer Samples

The sample clients demonstrate connect/register, background receive, keepalive, and client-to-client message send/receive. The .NET sample uses the `SocketClient` library; iOS, macOS, and Android use each platform's native networking APIs while speaking the same frame/protobuf protocol.

## .NET Web UI

```bash
dotnet run --project Samples/SocketSample.Net/SocketSample.Net.csproj
```

The .NET Web UI binds to a dynamic local port by default. Check the `Now listening on` log line for the URL.

Default server settings live in `Samples/SocketSample.Net/appsettings.json` under `sampleClient`, and the UI can edit `Host`, `Port`, `Control Endpoints`, `Use ControlServer`, and `Client ID`. `Connect` performs both connection and client registration. After registration, the receive loop stays active so inbound messages and ACKs appear in the status panel, healthcheck keepalive runs at `healthCheckIntervalSeconds`, and the routed SocketServer appears as `connectedServer`. The default `security.transportMode` is `Tls`; use `MessageEncryption` only when the server uses the same `SOCKET_MESSAGE_SECRET`.

## iOS

Native SwiftUI app built with Network.framework.

```bash
xcodegen generate --spec Samples/SocketSample.iOS/project.yml
xcodebuild -project Samples/SocketSample.iOS/SocketSampleiOS.xcodeproj -scheme SocketSampleiOS -sdk iphonesimulator -configuration Debug CODE_SIGNING_ALLOWED=NO build
```

The Apple sample accepts multiple ControlServer endpoints via the `Control Endpoints` UI field or `--control-endpoints host:port,host:port`. It retries route resolution and routed SocketServer connection across those endpoints before failing. If a route response contains a loopback host, the Apple sample replaces it with the ControlServer host that returned the route. Set `Transport` to `MessageEncryption` to connect without TLS and use AES-GCM/HMAC frame protection. In that mode, `Message Secret` must match the server's `SOCKET_MESSAGE_SECRET`.

## macOS

Native SwiftUI app built with Network.framework.

```bash
xcodegen generate --spec Samples/SocketSample.macOS/project.yml
xcodebuild -project Samples/SocketSample.macOS/SocketSampleMac.xcodeproj -scheme SocketSampleMac -configuration Debug -derivedDataPath Samples/SocketSample.macOS/build CODE_SIGNING_ALLOWED=NO build
```

Run two native sample clients against the broker endpoint to test message delivery across routed SocketServer instances. `Connect` performs both connection and client registration, and the receive loop stays active so inbound messages, ACKs, and the connected SocketServer endpoint appear in the status panel.

For the default `EndToEndTls` profile, pass the local client certificate directory and password so each app instance can present `SocketClient-{clientId}.pfx` during mTLS.

```bash
open -n Samples/SocketSample.macOS/build/Build/Products/Debug/SocketSampleMac.app --args --client-id 101 --client-name native-client-101 --host 127.0.0.1 --port 10000 --use-control-server true --auto-connect true --target-client-id 102 --certificate-dir Certificates --certificate-password socket-local-dev
open -n Samples/SocketSample.macOS/build/Build/Products/Debug/SocketSampleMac.app --args --client-id 102 --client-name native-client-102 --host 127.0.0.1 --port 10000 --use-control-server true --auto-connect true --target-client-id 101 --certificate-dir Certificates --certificate-password socket-local-dev
```

The Apple sample accepts multiple ControlServer endpoints via the `Control Endpoints` UI field or `--control-endpoints host:port,host:port`. It retries route resolution and routed SocketServer connection across those endpoints before failing. If a route response contains a loopback host, the Apple sample replaces it with the ControlServer host that returned the route. Set `Transport` to `MessageEncryption` to connect without TLS and use AES-GCM/HMAC frame protection. In that mode, `Message Secret` must match the server's `SOCKET_MESSAGE_SECRET`.

## Android

Native Java app built with the Android SDK.

```bash
cd Samples/SocketSample.Android
./validate.sh
./validate.sh --protocol-only
./validate.sh --apk
```

`validate.sh` validates the native frame/protocol code and builds the APK when the Android SDK is available. Use `--protocol-only` for protocol validation without the Android SDK, or `--apk` to require APK build.

The default config file is `Samples/SocketSample.Android/app/src/main/res/raw/config.json`. When `useControlServer=true`, `host` and `port` are the fallback ControlServer endpoint and `controlEndpoints[]` can list multiple ControlServers. The app retries route resolution and routed SocketServer connection across those endpoints before failing. If the route response contains a loopback host such as `127.0.0.1`, `localhost`, or `::1`, the Android app replaces it with the ControlServer host that returned the route, usually `10.0.2.2` in the emulator. Use a LAN IP for real devices. `MessageEncryption` mode requires the same `messageEncryptionSecret` as the server's `SOCKET_MESSAGE_SECRET`.
