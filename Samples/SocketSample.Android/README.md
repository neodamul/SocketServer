# SocketSample.Android

Native Java Android sample client built with the Android SDK.

```bash
cd Samples/SocketSample.Android
./validate.sh
./validate.sh --protocol-only
./validate.sh --apk
```

You can also open `Samples/SocketSample.Android` in Android Studio.

`validate.sh` validates the native frame/protocol Java code first. When the Android SDK is configured, it also builds the APK. Use `--protocol-only` for protocol validation without the Android SDK, or `--apk` to require APK build. The Homebrew `android-commandlinetools` default path (`/opt/homebrew/share/android-commandlinetools`) is detected automatically.

The default config file is `app/src/main/res/raw/config.json`. When `useControlServer=true`, `host` and `port` are the fallback ControlServer endpoint and `controlEndpoints[]` can list multiple ControlServers. The app retries route resolution and routed SocketServer connection across those endpoints before failing. The UI shows the connected SocketServer endpoint in `Connected Server`.

If a route response contains a loopback host such as `127.0.0.1`, `localhost`, or `::1`, the Android app replaces it with the ControlServer host that returned the route, usually `10.0.2.2` in the emulator. Use a LAN IP for real devices. `transportMode=MessageEncryption` connects without TLS and uses AES-GCM/HMAC frame protection; `messageEncryptionSecret` must match the server's `SOCKET_MESSAGE_SECRET`.
