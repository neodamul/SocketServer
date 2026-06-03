# SocketSample.iOS

SwiftUI + Network.framework 기반 iOS 네이티브 샘플 클라이언트입니다.

```bash
xcodegen generate --spec Samples/SocketSample.iOS/project.yml
xcodebuild -project Samples/SocketSample.iOS/SocketSampleiOS.xcodeproj -scheme SocketSampleiOS -sdk iphonesimulator -configuration Debug CODE_SIGNING_ALLOWED=NO build
```

UI에서 `Client ID`, `Host`, `Port`, `Transport`, `Message Secret`을 설정하고 connect/register/send/receive를 실행합니다. `Transport=MessageEncryption`은 TLS 없이 AES-GCM/HMAC 메시지 보호 모드로 접속하며, `Message Secret`은 서버의 `SOCKET_MESSAGE_SECRET` 값과 같아야 합니다.
