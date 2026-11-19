using Microsoft.Extensions.Logging;
using VideoFetcher.Core.Interfaces;

namespace VideoFetcher.Infrastructure.Notifications;

/// <summary>
/// Windows App SDK AppNotificationBuilder için soyutlanmış bildirim servisi.
/// (WinUI 3 App katmanında Microsoft.Windows.AppNotifications ile bağlanır)
/// </summary>
public sealed class WindowsToastNotificationService : INotificationService
{
    private readonly ILogger<WindowsToastNotificationService> _logger;

    public WindowsToastNotificationService(ILogger<WindowsToastNotificationService> logger)
    {
        _logger = logger;
    }

    public void ShowStreamReadyNotification(string jobId, string title, string summaryText)
    {
        // WinUI 3 hedefinde:
        // new AppNotificationBuilder()
        //   .AddText($"Video Hazır: {title}")
        //   .AddText(summaryText)
        //   .AddButton(new AppNotificationButton("Doğrudan İndir").AddArgument("action", "download").AddArgument("jobId", jobId))
        //   .AddButton(new AppNotificationButton("Seçenekler").AddArgument("action", "open_hud").AddArgument("jobId", jobId))
        //   .BuildNotification();
        _logger.LogInformation("[Toast Hazır] {Title} -> {Summary}", title, summaryText);
    }

    public void ShowDrmBlockedNotification(string title, string drmSystem)
    {
        _logger.LogWarning("[Toast DRM Kilitli] {Title} ({Drm}) — Korumalı içerik, indirilemez.", title, drmSystem);
    }
}
