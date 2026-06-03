# SocketSample.Android

Java + Android SDK 기반 Android 네이티브 샘플 클라이언트입니다.

```bash
cd Samples/SocketSample.Android
gradle :app:assembleDebug
```

Android Studio에서 `Samples/SocketSample.Android` 폴더를 열어도 됩니다.

기본 설정 파일은 `app/src/main/res/raw/config.json`입니다. Android emulator에서 개발 PC의 localhost에 접근하려면 기본 host `10.0.2.2`를 사용합니다. 실제 기기에서는 SocketServer 또는 ControlServer가 실행 중인 장비의 LAN IP로 바꿔야 합니다.
