using System.Net;
using Microsoft.Extensions.DependencyInjection;
using VideoFetcher.Core.Interfaces;
using VideoFetcher.Core.Services;
using VideoFetcher.Infrastructure.Bootstrap;
using VideoFetcher.Infrastructure.Download;
using VideoFetcher.Infrastructure.Extractors;
using VideoFetcher.Infrastructure.Media;
using VideoFetcher.Infrastructure.Subtitles;

namespace VideoFetcher.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVideoFetcherCore(this IServiceCollection services)
    {
        services.AddHttpClient("MediaDownloader");
        services.AddHttpClient("HighSpeedMediaClient", client =>
        {
            client.DefaultRequestVersion = HttpVersion.Version20;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
            client.Timeout = TimeSpan.FromMinutes(10);
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 128,
            EnableMultipleHttp2Connections = true,
            EnableMultipleHttp3Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        });

        services.AddSingleton<ToolBootstrapService>();
        services.AddSingleton<IHlsManifestParser, HlsManifestParser>();
        services.AddSingleton<IDashManifestParser, DashManifestParser>();
        services.AddSingleton<IConversionPlanner, ConversionPlanner>();
        services.AddSingleton<IExtractorBridge, YtDlpExtractorBridge>();
        services.AddSingleton<IHighSpeedDownloadManager, HighSpeedDownloadManager>();
        services.AddSingleton<IDownloadManager>(sp => sp.GetRequiredService<IHighSpeedDownloadManager>());
        services.AddSingleton<IConversionEngine, FfmpegConversionEngine>();
        services.AddSingleton<ISubtitleService, OpenSubtitlesService>();
        return services;
    }
}
