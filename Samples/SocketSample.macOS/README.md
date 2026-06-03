# SocketSample.macOS

SwiftUI + Network.framework 기반 macOS 네이티브 샘플 클라이언트입니다.

```bash
xcodegen generate --spec Samples/SocketSample.macOS/project.yml
xcodebuild -project Samples/SocketSample.macOS/SocketSampleMac.xcodeproj -scheme SocketSampleMac -configuration Debug CODE_SIGNING_ALLOWED=NO build
```

UI에서 `Client ID`, `Host`, `Port`를 설정하고 connect/register/send/receive를 실행합니다.
