# SocketSample.Android

Java + Android SDK 기반 Android 네이티브 샘플 클라이언트입니다.

```bash
cd Samples/SocketSample.Android
./validate.sh
./validate.sh --protocol-only
./validate.sh --apk
```

Android Studio에서 `Samples/SocketSample.Android` 폴더를 열어도 됩니다.

`validate.sh`는 Android 샘플의 프레임/프로토콜 Java 코드를 먼저 검증합니다. Android SDK가 설정된 환경에서는 APK 빌드까지 실행하며, 프로토콜 검증만 실행하려면 `--protocol-only`, APK 빌드를 강제하려면 `--apk` 옵션을 사용합니다. Homebrew `android-commandlinetools` 기본 설치 경로(`/opt/homebrew/share/android-commandlinetools`)도 자동 감지합니다.

기본 설정 파일은 `app/src/main/res/raw/config.json`입니다. `Connect`는 연결과 client register를 한 번에 수행하고, 등록 후 receive loop가 별도 스레드에서 항상 실행되어 수신 메시지와 ACK를 상태창에 표시합니다. `useControlServer=true`이면 ControlServer에서 SocketServer 접속 정보를 받아 연결합니다.

`transportMode=MessageEncryption`으로 바꾸면 TLS 없이 AES-GCM/HMAC 메시지 보호 모드로 접속하며, `messageEncryptionSecret`은 서버의 `SOCKET_MESSAGE_SECRET` 값과 같아야 합니다. Android emulator에서 개발 PC의 localhost에 접근하려면 기본 host `10.0.2.2`를 사용합니다. 실제 기기에서는 SocketServer 또는 ControlServer가 실행 중인 장비의 LAN IP로 바꿔야 합니다.
