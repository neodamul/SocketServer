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

서버 정보는 `Samples/SocketSample.Net/appsettings.json`의 `sampleClient` 섹션에서 기본값을 관리하고, 화면에서도 `Host`, `Port`, `Use ControlServer`, `Client ID`를 수정할 수 있습니다.

## iOS, macOS, Android

모바일 샘플은 .NET MAUI 프로젝트입니다.

```bash
dotnet workload install maui
dotnet build Samples/SocketSample.Mobile/SocketSample.Mobile.csproj -f net9.0-android
dotnet build Samples/SocketSample.Mobile/SocketSample.Mobile.csproj -f net9.0-ios
dotnet build Samples/SocketSample.Mobile/SocketSample.Mobile.csproj -f net9.0-maccatalyst
```

기본 설정 파일은 `Samples/SocketSample.Mobile/Resources/Raw/config.json`입니다. 실제 Android/iOS 기기에서 `127.0.0.1`은 기기 자신을 의미하므로, 개발 PC나 서버의 접근 가능한 IP로 `Host`를 변경해야 합니다.
