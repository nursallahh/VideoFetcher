using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using VideoFetcher.Core.Interfaces;

namespace VideoFetcher.Infrastructure.Transfer;

/// <summary>
/// Aynı Wi-Fi ağındaki telefon/tabletlerin ek uygulama kurmadan videoyu indirmesi için
/// tek kullanımlık token'lı ve HTTP Range destekli yerel Kestrel sunucusu.
/// </summary>
public sealed class KestrelQrTransferService : ITransferService
{
    private readonly ILogger<KestrelQrTransferService> _logger;
    private readonly ConcurrentDictionary<string, (string FilePath, DateTimeOffset ExpiresAt)> _sessions = new();
    private WebApplication? _app;
    private int _port = 5180;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public KestrelQrTransferService(ILogger<KestrelQrTransferService> logger)
    {
        _logger = logger;
    }

    public async Task<TransferSession> StartShareSessionAsync(string filePath, TimeSpan? ttl, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Paylaşılacak video bulunamadı.", filePath);

        await EnsureServerRunningAsync(cancellationToken);

        string token = Guid.NewGuid().ToString("N")[..12];
        var expiresAt = DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromMinutes(15));
        _sessions[token] = (filePath, expiresAt);

        string localIp = GetLocalLanIpAddress();
        string fileName = Path.GetFileName(filePath);
        string url = $"http://{localIp}:{_port}/dl/{token}/{Uri.EscapeDataString(fileName)}";

        _logger.LogInformation("QR Aktarım oturumu açıldı: {Url} (Bitiş: {Exp})", url, expiresAt);
        return new TransferSession(token, fileName, url, expiresAt);
    }

    public Task StopSessionAsync(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }

    private async Task EnsureServerRunningAsync(CancellationToken ct)
    {
        if (_app is not null) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_app is not null) return;

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(_port));

            var app = builder.Build();
            app.MapGet("/dl/{token}/{fileName}", (string token, string fileName) =>
            {
                if (!_sessions.TryGetValue(token, out var entry) || entry.ExpiresAt < DateTimeOffset.UtcNow)
                {
                    _sessions.TryRemove(token, out _);
                    return Results.NotFound("Aktarım bağlantısının süresi dolmuş veya geçersiz.");
                }

                return Results.File(entry.FilePath, "video/mp4", Path.GetFileName(entry.FilePath), enableRangeProcessing: true);
            });

            await app.StartAsync(ct);
            _app = app;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static string GetLocalLanIpAddress()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr.Address))
                    return addr.Address.ToString();
            }
        }
        return "127.0.0.1";
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
            _app = null;
        }
        _initLock.Dispose();
    }
}
