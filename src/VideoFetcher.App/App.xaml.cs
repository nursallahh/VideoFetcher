using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using VideoFetcher.App.Sniffing;
using VideoFetcher.Core.Interfaces;
using VideoFetcher.Infrastructure.Bootstrap;
using VideoFetcher.Infrastructure.DependencyInjection;
using VideoFetcher.Infrastructure.Library;
using VideoFetcher.Infrastructure.Notifications;
using VideoFetcher.Infrastructure.Transfer;

namespace VideoFetcher.App;

public partial class App : Application
{
    public IServiceProvider Services { get; }
    private Window? _window;

    public App()
    {
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--disable-features=UseHardwareOverlay,CalculateNativeWinOcclusion");
        InitializeComponent();
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.AddDebug());
        sc.AddVideoFetcherCore();
        sc.AddSingleton<WebView2CdpSniffer>();
        sc.AddSingleton<ITransferService, KestrelQrTransferService>();
        sc.AddSingleton<INotificationService, WindowsToastNotificationService>();
        sc.AddSingleton<ILibraryService>(_ => new JsonLibraryService(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VideoFetcher")));
        Services = sc.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ = Services.GetRequiredService<ToolBootstrapService>().EnsureToolsReadyAsync(CancellationToken.None);
        _window = new MainWindow(Services);
        _window.Activate();
    }
}
