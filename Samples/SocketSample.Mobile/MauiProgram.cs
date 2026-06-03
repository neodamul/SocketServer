using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SocketSample.Shared;

namespace SocketSample.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        MauiAppBuilder builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>();

        builder.Services.AddSingleton<SampleSocketClientSession>();
        builder.Services.AddTransient<MainPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
