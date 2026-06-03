# SocketSample.Mobile

.NET MAUI 기반 iOS, macOS(MacCatalyst), Android 샘플 클라이언트입니다.

이 프로젝트는 모바일 workload가 필요하므로 기본 `SocketServer.sln` 빌드에는 포함하지 않습니다.

```bash
dotnet workload install maui
dotnet build Samples/SocketSample.Mobile/SocketSample.Mobile.csproj -f net9.0-android
dotnet build Samples/SocketSample.Mobile/SocketSample.Mobile.csproj -f net9.0-ios
dotnet build Samples/SocketSample.Mobile/SocketSample.Mobile.csproj -f net9.0-maccatalyst
```

UI에서 `Client ID`, `Host`, `Port`, `Use ControlServer`를 설정한 뒤 connect/register/send/receive를 실행할 수 있습니다.
