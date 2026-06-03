# SocketServer Samples

샘플 클라이언트는 `SocketClient` 라이브러리를 사용해 연결, 등록, 클라이언트 간 메시지 송수신을 실행합니다.

## .NET Web UI

```bash
dotnet run --project Samples/SocketSample.Net/SocketSample.Net.csproj
```

기본 URL:

```text
http://127.0.0.1:5090
```

서버 정보는 `Samples/SocketSample.Net/appsettings.json`의 `sampleClient` 섹션에서 기본값을 관리하고, 화면에서도 `Host`, `Port`, `Use ControlServer`, `Client ID`를 수정할 수 있습니다. `security.transportMode`는 기본 `Tls`이며, `MessageEncryption`을 사용하려면 서버와 같은 `SOCKET_MESSAGE_SECRET` 값을 설정합니다.

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
xcodebuild -project Samples/SocketSample.macOS/SocketSampleMac.xcodeproj -scheme SocketSampleMac -configuration Debug CODE_SIGNING_ALLOWED=NO build
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
