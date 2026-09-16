using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using StreamForge.App.ViewModels;
using StreamForge.Core.Interfaces;
using StreamForge.Infrastructure;
using StreamForge.Infrastructure.Extraction;
using StreamForge.Infrastructure.Ffmpeg;
using StreamForge.Infrastructure.Streaming;

namespace StreamForge.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = Services.GetRequiredService<MainWindow>();
        window.Activate();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        services.AddHttpClient<HlsStreamAnalyzer>();
        services.AddSingleton<IStreamExtractor, PlaywrightStreamExtractor>();
        services.AddSingleton<IStreamAnalyzer>(provider => provider.GetRequiredService<HlsStreamAnalyzer>());
        services.AddSingleton<IFfmpegLocator, FfmpegLocator>();
        services.AddSingleton<IFfmpegService, FfmpegService>();
        services.AddSingleton<IDownloadService, DownloadService>();
        services.AddTransient<MainViewModel>();
        services.AddTransient<MainWindow>();

        return services.BuildServiceProvider();
    }
}
