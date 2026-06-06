# SocketServer Samples

샘플 클라이언트는 연결, 등록, 클라이언트 간 메시지 송수신을 실행합니다. .NET 샘플은 `SocketClient` 라이브러리를 사용하고, iOS/macOS/Android 샘플은 각 플랫폼 네이티브 네트워크 API로 동일한 프로토콜을 구현합니다.

## .NET Web UI

```bash
dotnet run --project Samples/SocketSample.Net/SocketSample.Net.csproj
```

.NET Web UI는 기본적으로 `127.0.0.1`의 동적 포트에 바인딩됩니다. 실행 로그의 `Now listening on` 주소를 확인합니다.

서버 정보는 `Samples/SocketSample.Net/appsettings.json`의 `sampleClient` 섹션에서 기본값을 관리하고, 화면에서도 `Host`, `Port`, `Use ControlServer`, `Client ID`를 수정할 수 있습니다. `Connect`는 연결과 client register를 한 번에 수행합니다. 등록 후 receive loop가 항상 실행되어 수신 메시지와 ACK를 상태창에 표시하고, `healthCheckIntervalSeconds` 간격으로 keepalive를 유지합니다. `security.transportMode`는 기본 `Tls`이며, `MessageEncryption`을 사용하려면 서버와 같은 `SOCKET_MESSAGE_SECRET` 값을 설정합니다.

## iOS

SwiftUI + Network.framework 기반 네이티브 앱입니다.

```bash
xcodegen generate --spec Samples/SocketSample.iOS/project.yml
xcodebuild -project Samples/SocketSample.iOS/SocketSampleiOS.xcodeproj -scheme SocketSampleiOS -sdk iphonesimulator -configuration Debug CODE_SIGNING_ALLOWED=NO build
```

UI의 `Transport`를 `MessageEncryption`으로 바꾸면 TLS 없이 AES-GCM/HMAC 메시지 보호 모드로 접속합니다. 이때 `Message Secret`은 서버의 `SOCKET_MESSAGE_SECRET` 값과 같아야 합니다.

## macOS

SwiftUI + Network.framework 기반 네이티브 앱입니다.

```bash
xcodegen generate --spec Samples/SocketSample.macOS/project.yml
xcodebuild -project Samples/SocketSample.macOS/SocketSampleMac.xcodeproj -scheme SocketSampleMac -configuration Debug -derivedDataPath Samples/SocketSample.macOS/build CODE_SIGNING_ALLOWED=NO build
```

두 개의 네이티브 샘플 클라이언트를 동시에 띄워 메시지를 테스트할 수 있습니다. `Connect`는 연결과 client register를 한 번에 수행하고, receive loop가 항상 실행되어 수신 메시지와 ACK를 상태창에 표시합니다.

```bash
open -n Samples/SocketSample.macOS/build/Build/Products/Debug/SocketSampleMac.app --args --client-id 101 --client-name native-client-101 --host 127.0.0.1 --port 10000 --use-control-server true --auto-connect true --target-client-id 102
open -n Samples/SocketSample.macOS/build/Build/Products/Debug/SocketSampleMac.app --args --client-id 102 --client-name native-client-102 --host 127.0.0.1 --port 10000 --use-control-server true --auto-connect true --target-client-id 101
```

UI의 `Transport`를 `MessageEncryption`으로 바꾸면 TLS 없이 AES-GCM/HMAC 메시지 보호 모드로 접속합니다. 이때 `Message Secret`은 서버의 `SOCKET_MESSAGE_SECRET` 값과 같아야 합니다.

## Android

Java + Android SDK 기반 네이티브 앱입니다.

```bash
cd Samples/SocketSample.Android
./validate.sh
./validate.sh --protocol-only
./validate.sh --apk
```

`validate.sh`는 Android 네이티브 샘플의 프레임/프로토콜 코드를 검증하고, Android SDK가 설정되어 있으면 APK 빌드까지 실행합니다. 프로토콜 검증만 실행하려면 `--protocol-only`, APK 빌드를 강제하려면 `--apk` 옵션을 사용합니다.

기본 설정 파일은 `Samples/SocketSample.Android/app/src/main/res/raw/config.json`입니다. `transportMode`를 `MessageEncryption`으로 바꾸면 TLS 없이 AES-GCM/HMAC 메시지 보호 모드로 접속하며, `messageEncryptionSecret`은 서버의 `SOCKET_MESSAGE_SECRET` 값과 같아야 합니다. Android emulator에서 개발 PC의 localhost에 접근하려면 host `10.0.2.2`를 사용합니다. 실제 기기에서는 SocketServer 또는 ControlServer가 실행 중인 장비의 LAN IP로 바꿔야 합니다.

Android UI의 `Connect`도 연결과 client register를 한 번에 수행합니다. 등록 후 receive loop가 별도 스레드에서 항상 실행되어 수신 메시지와 ACK를 상태창에 표시하고, `Use ControlServer route`가 켜져 있으면 ControlServer에서 SocketServer 접속 정보를 받아 연결합니다.
