using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using VideoFetcher.App.Sniffing;
using VideoFetcher.Core.Interfaces;
using VideoFetcher.Core.Models;
using VideoFetcher.Infrastructure.Bootstrap;
using VideoFetcher.Infrastructure.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace VideoFetcher.App;

public sealed partial class MainWindow : Window
{
    private sealed class BrowserTabSession
    {
        public string Id { get; } = Guid.NewGuid().ToString("N")[..6];
        public WebView2 WebView { get; } = new();
        public string Title { get; set; } = "Yeni Sekme";
        public string Url { get; set; } = "https://www.google.com";
        public long Generation { get; set; }
        public CancellationTokenSource PageCts { get; set; } = new();
        public List<MediaStreamCandidate> PrimaryStreams { get; } = [];
        public List<MediaStreamCandidate> OtherStreams { get; } = [];
        public List<SubtitleCandidate> InPageSubtitles { get; } = [];
        public List<SubtitleCandidate?> SubtitleCandidates { get; } = [];
        public bool BingeTriggered { get; set; }
    }

    private sealed record QueuedDownloadJob(
        MediaStreamCandidate Candidate,
        SubtitleCandidate? Subtitle,
        string EnhanceTag,
        string? TrimStart,
        string? TrimEnd);

    private static readonly string[] BlockedAdDomains =
    [
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "adservice.google",
        "popads.net", "popcash.net", "exoclick.com", "juicyads.com", "propellerads.com",
        "adsterra.com", "histats.com", "mc.yandex.ru", "googletagmanager.com", "adnxs.com"
    ];

    private readonly IServiceProvider _services;
    private readonly IHlsManifestParser _hlsParser;
    private readonly IDashManifestParser _dashParser;
    private readonly IConversionPlanner _planner;
    private readonly IConversionEngine _engine;
    private readonly IHighSpeedDownloadManager _downloader;
    private readonly IExtractorBridge _extractor;
    private readonly ISubtitleService _subtitles;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ToolBootstrapService _bootstrap;
    private bool _bootstrapChecked;

    private sealed class BrowserHistoryEntry
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public DateTime VisitedAt { get; set; } = DateTime.Now;
    }

    private sealed class BrowserStateData
    {
        public List<string> OpenTabs { get; set; } = [];
        public int ActiveTabIndex { get; set; }
        public List<BrowserHistoryEntry> History { get; set; } = [];
        public List<BrowserHistoryEntry> Bookmarks { get; set; } = [];
    }

    private static readonly string BrowserStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VideoFetcher",
        "browser_state.json");

    private readonly List<BrowserHistoryEntry> _history = [];
    private readonly List<BrowserHistoryEntry> _bookmarks = [];
    private readonly List<string> _libraryFiles = [];
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingFetches = new();
    private readonly List<BrowserTabSession> _tabs = [];
    private readonly Queue<QueuedDownloadJob> _downloadQueue = new();
    private BrowserTabSession? _activeTab;
    private CancellationTokenSource? _downloadCts;
    private bool _isDownloading;
    private double _maxProgressPct;
    private bool _adBlockEnabled = true;
    private static readonly Version CurrentAppVersion = new(1, 8, 0);
    private string? _latestUpdateTag;

    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico")); } catch { }
        _services = services;
        _hlsParser = services.GetRequiredService<IHlsManifestParser>();
        _dashParser = services.GetRequiredService<IDashManifestParser>();
        _planner = services.GetRequiredService<IConversionPlanner>();
        _engine = services.GetRequiredService<IConversionEngine>();
        _downloader = services.GetRequiredService<IHighSpeedDownloadManager>();
        _extractor = services.GetRequiredService<IExtractorBridge>();
        _subtitles = services.GetRequiredService<ISubtitleService>();
        _httpFactory = services.GetRequiredService<IHttpClientFactory>();
        _bootstrap = services.GetRequiredService<ToolBootstrapService>();
        Activated += OnWindowFirstActivated;

        Closed += (_, _) => SaveBrowserState();
        LoadBrowserStateAndRestoreTabs();
    }

    private async Task CheckForAppUpdateAsync()
    {
        try
        {
            var client = _httpFactory.CreateClient("HighSpeedMediaClient");
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/nursallahh/VideoFetcher/releases/latest");
            req.Headers.TryAddWithoutValidation("User-Agent", "VideoFetcher-Updater");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var resp = await client.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            string? tag = doc.RootElement.TryGetProperty("tag_name", out var tp) ? tp.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) return;

            string cleanVer = tag.TrimStart('v', 'V').Split('-')[0];
            if (!Version.TryParse(cleanVer, out var latestVer) || latestVer <= CurrentAppVersion) return;

            _latestUpdateTag = tag;
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    UpdatePillButton.Content = $"🆕 Güncelle ({tag})";
                    UpdatePillButton.Visibility = Visibility.Visible;
                    if (Content?.XamlRoot is null) return;
                    var dlg = new ContentDialog
                    {
                        Title = $"Yeni Güncelleme Mevcut ({tag})",
                        Content = $"Mevcut sürüm: v{CurrentAppVersion}\nYeni sürüm: {tag}\n\nAçık sekmeleriniz, yer imleriniz ve ayarlarınız korunarak tek tıkla güncellensin mi?",
                        PrimaryButtonText = "Şimdi Güncelle",
                        CloseButtonText = "Daha Sonra",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = Content.XamlRoot
                    };
                    if (await dlg.ShowAsync() == ContentDialogResult.Primary)
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"irm https://raw.githubusercontent.com/nursallahh/VideoFetcher/main/scripts/install.ps1 | iex\"",
                            UseShellExecute = true
                        });
                        Application.Current.Exit();
                    }
                }
                catch { }
            });
        }
        catch { }
    }

    private async void OnUpdatePillClick(object sender, RoutedEventArgs e)
    {
        if (Content?.XamlRoot is null || string.IsNullOrWhiteSpace(_latestUpdateTag)) return;
        var dlg = new ContentDialog
        {
            Title = $"Yeni Güncelleme Mevcut ({_latestUpdateTag})",
            Content = $"Mevcut sürüm: v{CurrentAppVersion}\nYeni sürüm: {_latestUpdateTag}\n\nAçık sekmeleriniz, yer imleriniz ve ayarlarınız korunarak tek tıkla güncellensin mi?",
            PrimaryButtonText = "Şimdi Güncelle",
            CloseButtonText = "Daha Sonra",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"irm https://raw.githubusercontent.com/nursallahh/VideoFetcher/main/scripts/install.ps1 | iex\"",
                UseShellExecute = true
            });
            Application.Current.Exit();
        }
    }

    private async void OnWindowFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_bootstrapChecked || Content?.XamlRoot is null) return;
        _bootstrapChecked = true;
        var missing = _bootstrap.GetMissingTools();
        if (missing.Count == 0)
        {
            _ = _bootstrap.EnsureToolsReadyAsync(CancellationToken.None);
            _ = CheckForAppUpdateAsync();
            return;
        }

        try
        {
            var dlg = new ContentDialog
            {
                Title = "Gerekli Medya Araçları Eksik",
                Content = $"Videoları kayıpsız birleştirmek ve indirmek için şu araçların indirilmesi gerekiyor:\n• {string.Join("\n• ", missing)}\n\nResmi GitHub sürümlerinden (%LocalAppData%\\VideoFetcher\\tools) tek seferlik indirilsin mi?",
                PrimaryButtonText = "İzin Ver ve İndir",
                CloseButtonText = "Daha Sonra",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };
            var res = await dlg.ShowAsync();
            if (res == ContentDialogResult.Primary)
            {
                FloatingHud.Visibility = Visibility.Visible;
                ProgressPanel.Visibility = Visibility.Visible;
                HudProgress.IsIndeterminate = true;
                var status = new Progress<string>(msg => DispatcherQueue.TryEnqueue(() => HudProgressLabel.Text = msg));
                await _bootstrap.DownloadMissingToolsAsync(status, CancellationToken.None);
                HudProgress.IsIndeterminate = false;
                HudProgress.Value = 100;
            }
            _ = CheckForAppUpdateAsync();
        }
        catch { HudProgress.IsIndeterminate = false; }
    }

    private async Task CreateNewTabAsync(string initialUrl)
    {
        var tab = new BrowserTabSession { Url = initialUrl };
        _tabs.Add(tab);
        BrowserContainer.Children.Add(tab.WebView);
        SwitchToTab(tab);

        await tab.WebView.EnsureCoreWebView2Async();
        var sniffer = _services.GetRequiredService<WebView2CdpSniffer>();
        sniffer.MediaCandidateDetected += (_, c) => OnMediaDetected(tab, c);
        sniffer.SubtitleDetected += (_, s) => OnInPageSubtitleDetected(tab, s);
        sniffer.DrmDetected += (_, k) => OnDrmDetected(tab, k);
        await sniffer.AttachAsync(tab.WebView.CoreWebView2, CancellationToken.None);

        // Reklam ve izleyici engelleyici
        tab.WebView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        tab.WebView.CoreWebView2.WebResourceRequested += (_, args) =>
        {
            if (!_adBlockEnabled) return;
            if (Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var u))
            {
                string host = u.Host;
                if (BlockedAdDomains.Any(d => host.Contains(d, StringComparison.OrdinalIgnoreCase)))
                {
                    args.Response = tab.WebView.CoreWebView2.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                }
            }
        };

        // Pop-up / yeni sekme isteklerini ana pencere sekmesi olarak açmak yerine reklam pop-uplarını engelle
        tab.WebView.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; };

        tab.WebView.CoreWebView2.DocumentTitleChanged += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                string t = tab.WebView.CoreWebView2?.DocumentTitle ?? "Sekme";
                tab.Title = t.Length > 20 ? t[..20] + "…" : t;
                RecordHistory(t, tab.Url);
                RefreshTabStrip();
            });
        };

        tab.WebView.CoreWebView2.WebMessageReceived += (_, args) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(args.WebMessageAsJson);
                if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "vf_fetch_res")
                {
                    string? id = doc.RootElement.GetProperty("id").GetString();
                    string text = doc.RootElement.TryGetProperty("text", out var txt) ? txt.GetString() ?? "" : "";
                    if (id is not null && _pendingFetches.TryRemove(id, out var tcs))
                        tcs.TrySetResult(text);
                }
            }
            catch { }
        };

        tab.WebView.CoreWebView2.NavigationStarting += (_, e) =>
        {
            ResetTabStreams(tab, e.Uri ?? tab.WebView.Source?.ToString() ?? "");
        };

        tab.WebView.CoreWebView2.SourceChanged += (_, _) =>
        {
            string newUrl = tab.WebView.Source?.ToString() ?? "";
            if (!string.Equals(newUrl, tab.Url, StringComparison.OrdinalIgnoreCase))
            {
                ResetTabStreams(tab, newUrl);
                RecordHistory(tab.Title, newUrl);
                SaveBrowserState();
            }
            else if (tab == _activeTab)
                DispatcherQueue.TryEnqueue(() => AddressBox.Text = newUrl);
        };

        tab.WebView.Source = new Uri(initialUrl);
        RefreshTabStrip();
    }

    private void SwitchToTab(BrowserTabSession tab)
    {
        _activeTab = tab;
        foreach (var t in _tabs)
            t.WebView.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed;

        AddressBox.Text = tab.Url;
        RefreshHudFromTab(tab);
        RefreshTabStrip();
    }

    private void RefreshTabStrip()
    {
        TabsPanel.Children.Clear();
        foreach (var t in _tabs)
        {
            bool active = t == _activeTab;
            var btn = new Button
            {
                Content = string.IsNullOrWhiteSpace(t.Title) ? "Sekme" : t.Title,
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 11,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(active ? ColorHelper.FromArgb(255, 30, 41, 59) : ColorHelper.FromArgb(255, 15, 19, 29)),
                Foreground = new SolidColorBrush(active ? Colors.White : ColorHelper.FromArgb(255, 148, 163, 184))
            };
            btn.Click += (_, _) => SwitchToTab(t);

            var closeBtn = new Button
            {
                Content = "×",
                Padding = new Thickness(5, 2, 5, 2),
                FontSize = 11,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 148, 163, 184))
            };
            closeBtn.Click += (_, _) => CloseTab(t);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            row.Children.Add(btn);
            if (_tabs.Count > 1) row.Children.Add(closeBtn);
            TabsPanel.Children.Add(row);
        }
    }

    private void CloseTab(BrowserTabSession tab)
    {
        if (_tabs.Count <= 1) return;
        tab.PageCts.Cancel();
        tab.PageCts.Dispose();
        BrowserContainer.Children.Remove(tab.WebView);
        try { tab.WebView.Close(); } catch { }
        int idx = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        if (_activeTab == tab) SwitchToTab(_tabs[Math.Max(0, idx - 1)]);
        else RefreshTabStrip();
    }

    private void ResetTabStreams(BrowserTabSession tab, string newUrl)
    {
        tab.Generation++;
        tab.BingeTriggered = false;
        tab.Url = newUrl;
        try { tab.PageCts.Cancel(); tab.PageCts.Dispose(); } catch { }
        tab.PageCts = new CancellationTokenSource();

        DispatcherQueue.TryEnqueue(() =>
        {
            tab.PrimaryStreams.Clear();
            tab.OtherStreams.Clear();
            tab.SubtitleCandidates.Clear();
            tab.InPageSubtitles.Clear();

            if (tab == _activeTab)
            {
                AddressBox.Text = newUrl;
                RefreshHudFromTab(tab);
                if (!_isDownloading) ResetDownloadUi();
            }
        });
    }

    private void RefreshHudFromTab(BrowserTabSession tab)
    {
        QualitySelector.Items.Clear();
        foreach (var item in tab.PrimaryStreams)
        {
            var ts = TimeSpan.FromSeconds(item.DurationSeconds);
            string durText = item.DurationSeconds > 0 ? (ts.TotalHours >= 1 ? $" • {ts:hh\\:mm\\:ss}" : $" • {ts:mm\\:ss}") : "";
            string bwText = item.BandwidthBps.HasValue ? $" • {item.BandwidthBps.Value / 1_000_000.0:F1} Mbps" : "";
            string sizeText = "";
            if (item.DurationSeconds > 0 && item.BandwidthBps is > 0)
            {
                double estMb = (item.DurationSeconds * item.BandwidthBps.Value / 8.0) / (1024.0 * 1024.0);
                sizeText = estMb >= 1024 ? $" • ~{estMb / 1024.0:F2} GB" : $" • ~{estMb:F0} MB";
            }
            QualitySelector.Items.Add($"{FormatQualityLabel(item)} — {item.Title}{durText}{bwText}{sizeText} (Alaka %{item.RelevanceScore})");
        }
        if (QualitySelector.Items.Count > 0) QualitySelector.SelectedIndex = 0;

        OtherStreamsList.Items.Clear();
        foreach (var o in tab.OtherStreams)
            OtherStreamsList.Items.Add($"{FormatQualityLabel(o)} — {o.Title} (Alaka %{o.RelevanceScore})");
        OtherStreamsExpander.Header = $"Diğer / Düşük Alakalı Akışlar ({tab.OtherStreams.Count})";

        SubtitleSelector.Items.Clear();
        foreach (var s in tab.SubtitleCandidates)
        {
            if (s is null) SubtitleSelector.Items.Add("— Altyazısız (Sadece Video) —");
            else if (s.SourceKind == "Siteİçi") SubtitleSelector.Items.Add($"[Dahili • {s.Language.ToUpperInvariant()}] {s.ReleaseName}");
            else SubtitleSelector.Items.Add($"[{s.Language.ToUpperInvariant()} • {s.SourceKind}] {s.ReleaseName} ({s.DownloadCount} indirme)");
        }
        if (SubtitleSelector.Items.Count > 1) SubtitleSelector.SelectedIndex = 1;
        else if (SubtitleSelector.Items.Count == 1) SubtitleSelector.SelectedIndex = 0;

        if (tab.PrimaryStreams.Count > 0)
        {
            RadarPillButton.Content = $"● {tab.PrimaryStreams.Count} Kalite Hazır";
            RadarPillButton.Visibility = Visibility.Visible;
            HudTitleText.Text = tab.PrimaryStreams[0].Title;
            if (string.IsNullOrWhiteSpace(SubtitleQueryBox.Text)) SubtitleQueryBox.Text = tab.PrimaryStreams[0].Title;
            HudDownloadButton.IsEnabled = !_isDownloading;
            FloatingHud.Visibility = Visibility.Visible;

            if (BingeModeCheck.IsChecked == true && !tab.BingeTriggered && !_isDownloading && tab.PrimaryStreams[0].DurationSeconds >= 600)
            {
                tab.BingeTriggered = true;
                var job = BuildCurrentJob();
                if (job is not null) _ = ExecuteDownloadJobAsync(job);
            }
        }
        else
        {
            RadarPillButton.Visibility = Visibility.Collapsed;
            FloatingHud.Visibility = Visibility.Collapsed;
        }
    }

    private void ResetDownloadUi()
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        HudProgress.Value = 0;
        HudProgressLabel.Text = "";
        HudStatusText.Text = "● Kayıpsız İndirmeye Hazır";
        HudDownloadButton.Content = "En Yüksek Hızda İndir";
        HudDownloadButton.IsEnabled = (_activeTab?.PrimaryStreams.Count ?? 0) > 0;
        HudResetButton.Visibility = Visibility.Collapsed;
        HudCancelButton.Visibility = Visibility.Collapsed;
    }

    private void OnInPageSubtitleDetected(BrowserTabSession tab, SubtitleCandidate sub)
    {
        long gen = tab.Generation;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (gen != tab.Generation) return;
            if (tab.InPageSubtitles.Any(x => x.DownloadUrlOrFileId == sub.DownloadUrlOrFileId && x.ReleaseName == sub.ReleaseName)) return;

            tab.InPageSubtitles.Add(sub);
            if (tab.SubtitleCandidates.Count == 0)
                tab.SubtitleCandidates.Add(null);
            tab.SubtitleCandidates.Insert(1, sub);

            if (tab == _activeTab) RefreshHudFromTab(tab);
        });
    }

    private async void OnYtDlpScanClick(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null) return;
        var tab = _activeTab;
        string url = tab.Url;
        if (string.IsNullOrWhiteSpace(url)) return;
        long gen = tab.Generation;
        var ct = tab.PageCts.Token;

        YtDlpScanButton.IsEnabled = false;
        YtDlpScanButton.Content = "Taranıyor...";
        try
        {
            var results = await _extractor.ExtractAsync(url, null, ct);
            if (gen != tab.Generation) return;

            foreach (var c in results)
            {
                int score = CalculateRelevance(c.DurationSeconds, c.BandwidthBps, c.Url, c.Resolution, c.Protocol);
                AddCandidateToTab(tab, c with { Title = FormatDisplayTitle(c.Title), RelevanceScore = score }, gen);
            }
            if (results.Count == 0)
            {
                HudProgressLabel.Text = "yt-dlp bu sayfada açık akış bulamadı.";
                ProgressPanel.Visibility = Visibility.Visible;
                FloatingHud.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            HudProgressLabel.Text = $"yt-dlp hatası: {ex.Message}";
            DiagnosticLogBox.Text = ex.ToString();
            ProgressPanel.Visibility = Visibility.Visible;
            FloatingHud.Visibility = Visibility.Visible;
        }
        finally
        {
            YtDlpScanButton.Content = "⚡ yt-dlp Tara";
            YtDlpScanButton.IsEnabled = true;
        }
    }

    private async void OnSearchSubtitlesClick(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null) return;
        var tab = _activeTab;
        var ct = tab.PageCts.Token;

        string title = !string.IsNullOrWhiteSpace(SubtitleQueryBox.Text) ? SubtitleQueryBox.Text.Trim() : HudTitleText.Text;
        if (string.IsNullOrWhiteSpace(title) || title == "Video") title = tab.WebView.CoreWebView2?.DocumentTitle ?? "";

        tab.SubtitleCandidates.Clear();
        tab.SubtitleCandidates.Add(null);
        foreach (var inSub in tab.InPageSubtitles)
            tab.SubtitleCandidates.Add(inSub);

        SubtitleSelector.PlaceholderText = "Dahili + Harici altyazılar aranıyor...";
        string langTag = (SubtitleLangBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "tr,en";

        try
        {
            var tasks = langTag.Split(',').Select(l => _subtitles.SearchAsync(title, l.Trim(), ct)).ToList();
            var results = await Task.WhenAll(tasks);
            foreach (var sub in results.SelectMany(r => r))
                tab.SubtitleCandidates.Add(sub);

            if (tab == _activeTab) RefreshHudFromTab(tab);
            if (tab.SubtitleCandidates.Count <= 1) SubtitleSelector.PlaceholderText = "Uygun altyazı bulunamadı";
        }
        catch (OperationCanceledException) { }
    }

    private async void OnMediaDetected(BrowserTabSession tab, MediaStreamCandidate candidate)
    {
        long gen = tab.Generation;
        var ct = tab.PageCts.Token;
        string cleanTitle = FormatDisplayTitle(candidate.Title);

        if (candidate.Protocol == StreamProtocol.Hls)
        {
            try
            {
                var client = _httpFactory.CreateClient("HighSpeedMediaClient");
                string m3u8 = !string.IsNullOrWhiteSpace(candidate.PrefetchedManifestContent)
                    ? candidate.PrefetchedManifestContent
                    : await FetchTextAsync(client, candidate.Url, candidate.RequestHeaders, ct);
                if (gen != tab.Generation) return;

                var parsed = _hlsParser.Parse(m3u8, new Uri(candidate.Url), cleanTitle, candidate.RequestHeaders);
                if (parsed.IsEncrypted) { OnDrmDetected(tab, parsed.KeyMethod ?? "HLS-AES"); return; }

                if (parsed.Subtitles is { Count: > 0 })
                {
                    foreach (var st in parsed.Subtitles)
                    {
                        OnInPageSubtitleDetected(tab, new SubtitleCandidate(
                            Guid.NewGuid().ToString("N")[..8], st.Language, st.Name, 999999, "Siteİçi", st.Url.ToString()));
                    }
                }

                if (parsed.IsMaster && parsed.Variants.Count > 0)
                {
                    double dur = 0;
                    try
                    {
                        string sub = await FetchTextAsync(client, parsed.Variants[0].Url, candidate.RequestHeaders, ct);
                        if (gen != tab.Generation) return;
                        dur = _hlsParser.Parse(sub, new Uri(parsed.Variants[0].Url), cleanTitle, candidate.RequestHeaders).TotalDurationSeconds;
                    }
                    catch { }

                    foreach (var v in parsed.Variants)
                    {
                        int score = CalculateRelevance(dur, v.BandwidthBps, v.Url, v.Resolution, v.Protocol);
                        AddCandidateToTab(tab, v with { Title = cleanTitle, DurationSeconds = dur, RelevanceScore = score, MasterUrl = candidate.Url }, gen);
                    }
                    return;
                }
                else
                {
                    double dur = parsed.TotalDurationSeconds;
                    int score = CalculateRelevance(dur, candidate.BandwidthBps, candidate.Url, candidate.Resolution, candidate.Protocol);
                    AddCandidateToTab(tab, candidate with { Title = cleanTitle, DurationSeconds = dur, RelevanceScore = score }, gen);
                    return;
                }
            }
            catch { }
        }
        else if (candidate.Protocol == StreamProtocol.Dash)
        {
            try
            {
                var client = _httpFactory.CreateClient("HighSpeedMediaClient");
                string mpd = !string.IsNullOrWhiteSpace(candidate.PrefetchedManifestContent)
                    ? candidate.PrefetchedManifestContent
                    : await FetchTextAsync(client, candidate.Url, candidate.RequestHeaders, ct);
                if (gen != tab.Generation) return;

                var dash = _dashParser.Parse(mpd, new Uri(candidate.Url), cleanTitle, candidate.RequestHeaders);
                if (dash.IsDrmProtected) { OnDrmDetected(tab, dash.DrmScheme ?? "DASH-EME"); return; }

                string? audioUrl = dash.AudioTracks.FirstOrDefault()?.Url;
                if (dash.VideoTracks.Count > 0)
                {
                    foreach (var v in dash.VideoTracks)
                    {
                        // DASH manifest URL'sini sakla ki yt-dlp veya parça indirici tam MPD üzerinden indirebilsin
                        int score = CalculateRelevance(0, v.BandwidthBps, candidate.Url, v.Resolution, StreamProtocol.Dash);
                        AddCandidateToTab(tab, v with { Url = candidate.Url, Title = cleanTitle, SeparateAudioUrl = audioUrl, RelevanceScore = score }, gen);
                    }
                    return;
                }
            }
            catch { }
        }

        if (gen != tab.Generation) return;
        AddCandidateToTab(tab, candidate with { Title = cleanTitle, RelevanceScore = CalculateRelevance(0, candidate.BandwidthBps, candidate.Url, candidate.Resolution, candidate.Protocol) }, gen);
    }

    private static string FormatDisplayTitle(string raw)
    {
        string s = raw.Split('|')[0].Trim();
        s = Regex.Replace(s, @"(?i)(\d+)\.\s*sezon\s*(\d+)\.\s*b[oö]l[uü]m", m => $"S{int.Parse(m.Groups[1].Value):D2}E{int.Parse(m.Groups[2].Value):D2}");
        s = Regex.Replace(s, @"(?i)\b(izle|türkçe|altyazılı|dublaj|full|hd)\b", " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static string FormatQualityLabel(MediaStreamCandidate item)
    {
        string res = item.Resolution ?? "";
        if (res is "HLS" or "DASH" or "MP4" or "Auto" or "")
        {
            var urlRes = Regex.Match(item.Url, @"(?i)(?:^|[/_.-])(2160|1440|1080|720|480|360|240)p?(?:[/_.-]|$)");
            if (urlRes.Success) res = urlRes.Groups[1].Value + "p";
        }

        if (res.Contains("2160")) return "4K (2160p)";
        if (res.Contains("1440")) return "2K (1440p)";
        if (res.Contains("1080")) return "1080p Full HD";
        if (res.Contains("720")) return "720p HD";
        if (res.Contains("480")) return "480p";
        if (res.Contains("360")) return "360p";
        if (res.Contains("240")) return "240p";

        var heightMatch = Regex.Match(res, @"\d+x(\d+)");
        if (heightMatch.Success) return $"{heightMatch.Groups[1].Value}p ({res})";

        return res is "HLS" or "DASH" or "MP4" or "Auto" or ""
            ? $"Otomatik ({item.Protocol.ToString().ToUpperInvariant()})"
            : res;
    }

    private static int CalculateRelevance(double durationSeconds, long? bandwidthBps, string url, string resolution = "", StreamProtocol protocol = StreamProtocol.Hls)
    {
        int score = 45;
        if (durationSeconds >= 1800) score += 35;
        else if (durationSeconds >= 600) score += 25;
        else if (durationSeconds >= 120) score += 10;
        else if (durationSeconds > 0 && durationSeconds < 120) score -= 35;

        if (resolution.Contains("2160") || resolution.Contains("1080")) score += 15;
        else if (resolution.Contains("720")) score += 10;
        else if (resolution.Contains("360") || resolution.Contains("240")) score -= 10;

        if (bandwidthBps >= 2_000_000) score += 10;
        if (protocol == StreamProtocol.Hls || protocol == StreamProtocol.Dash) score += 5;

        string[] adPatterns = ["/ads/", "/ad/", "doubleclick", "googlesyndication", "preroll", "vast", "trailer", "teaser", "preview", "/promo/"];
        if (adPatterns.Any(p => url.Contains(p, StringComparison.OrdinalIgnoreCase))) score -= 45;

        return Math.Clamp(score, 0, 100);
    }

    private static string GetBasePath(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path) : url.Split('?')[0];

    private void AddCandidateToTab(BrowserTabSession tab, MediaStreamCandidate candidate, long gen)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (gen != tab.Generation) return;

            // Aynı URL önce genel "HLS" (sessiz/çözünürlüksüz) olarak gelip sonra Master playlistten tam çözünürlük + SeparateAudioUrl ile geldiyse eskisini güncelle
            var genericExisting = tab.PrimaryStreams.FirstOrDefault(x => x.Url == candidate.Url && x.Resolution == "HLS" && candidate.Resolution != "HLS");
            if (genericExisting is not null)
                tab.PrimaryStreams.Remove(genericExisting);

            bool isDuplicate = tab.PrimaryStreams.Any(x => x.Url == candidate.Url
                                || (x.Protocol == StreamProtocol.DirectFile && candidate.Protocol == StreamProtocol.DirectFile && GetBasePath(x.Url) == GetBasePath(candidate.Url)))
                            || tab.OtherStreams.Any(x => x.Url == candidate.Url);
            if (isDuplicate) return;

            if (candidate.RelevanceScore >= 40)
            {
                tab.PrimaryStreams.Add(candidate);
                var sorted = tab.PrimaryStreams.OrderByDescending(x => x.RelevanceScore).ThenByDescending(x => x.BandwidthBps ?? 0).ToList();
                tab.PrimaryStreams.Clear();
                tab.PrimaryStreams.AddRange(sorted);
            }
            else
            {
                tab.OtherStreams.Add(candidate);
            }

            if (tab == _activeTab) RefreshHudFromTab(tab);
        });
    }

    private void OnQualitySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isDownloading && ProgressPanel.Visibility == Visibility.Visible) ResetDownloadUi();
    }

    private void OnResetDownloadClick(object sender, RoutedEventArgs e)
    {
        if (!_isDownloading) ResetDownloadUi();
    }

    private void OnOtherStreamSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_activeTab is null) return;
        int idx = OtherStreamsList.SelectedIndex;
        if (idx < 0 || idx >= _activeTab.OtherStreams.Count) return;
        var chosen = _activeTab.OtherStreams[idx];
        _activeTab.PrimaryStreams.Insert(0, chosen);
        RefreshHudFromTab(_activeTab);
    }

    private void OnDrmDetected(BrowserTabSession tab, string keySystem)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (tab != _activeTab) return;
            RadarPillButton.Content = "🔒 Korumalı İçerik";
            RadarPillButton.Visibility = Visibility.Visible;
            HudStatusText.Text = "Korumalı İçerik — İndirilemez";
            HudDownloadButton.IsEnabled = false;
            FloatingHud.Visibility = Visibility.Visible;
        });
    }

    private void OnCancelDownloadClick(object sender, RoutedEventArgs e) => _downloadCts?.Cancel();

    private void OnAddToQueueClick(object sender, RoutedEventArgs e)
    {
        var job = BuildCurrentJob();
        if (job is null) return;
        if (!_isDownloading)
        {
            _ = ExecuteDownloadJobAsync(job);
            return;
        }
        _downloadQueue.Enqueue(job);
        RefreshQueueUi();
    }

    private QueuedDownloadJob? BuildCurrentJob()
    {
        if (_activeTab is null) return null;
        int idx = QualitySelector.SelectedIndex;
        if (idx < 0 || idx >= _activeTab.PrimaryStreams.Count) return null;
        var candidate = _activeTab.PrimaryStreams[idx];

        int subIdx = SubtitleSelector.SelectedIndex;
        SubtitleCandidate? sub = (subIdx >= 0 && subIdx < _activeTab.SubtitleCandidates.Count) ? _activeTab.SubtitleCandidates[subIdx] : null;
        string enhanceTag = (VideoEnhanceMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "hdr_only";
        return new QueuedDownloadJob(candidate, sub, enhanceTag, TrimStartBox.Text, TrimEndBox.Text);
    }

    private void RefreshQueueUi()
    {
        QueueList.Items.Clear();
        foreach (var q in _downloadQueue)
            QueueList.Items.Add($"⏳ {FormatQualityLabel(q.Candidate)} — {q.Candidate.Title}");
        AdvancedExpander.Header = $"Kuyruk ({_downloadQueue.Count}) • Zaman Kırpma • Teknik Günlük";
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        var job = BuildCurrentJob();
        if (job is null) return;
        await ExecuteDownloadJobAsync(job);
    }

    private async Task ExecuteDownloadJobAsync(QueuedDownloadJob job)
    {
        var candidate = job.Candidate;
        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;

        _isDownloading = true;
        _maxProgressPct = 0;
        HudDownloadButton.IsEnabled = false;
        HudDownloadButton.Content = "İndiriliyor...";
        HudResetButton.Visibility = Visibility.Collapsed;
        HudCancelButton.Visibility = Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Visible;
        HudProgress.Value = 5;

        string workTempDir = Path.Combine(Path.GetTempPath(), "vf_work_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VideoFetcher");
            Directory.CreateDirectory(outDir);
            string safeName = string.Join("_", candidate.Title.Split(Path.GetInvalidFileNameChars())).Trim();
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "video";
            Directory.CreateDirectory(workTempDir);
            string rawVideoPath = Path.Combine(workTempDir, "raw.mp4");
            string finalPath = Path.Combine(outDir, $"{safeName}_{DateTime.Now:HHmmss}.mp4");
            string m3u8Preview = "";

            var progress = new Progress<DownloadProgress>(p => DispatcherQueue.TryEnqueue(() =>
            {
                if (p.Percentage < _maxProgressPct) return;
                _maxProgressPct = p.Percentage;
                HudProgress.Value = p.Percentage;
                HudProgressLabel.Text = p.StatusMessage;
            }));

            if (candidate.Protocol == StreamProtocol.Hls)
            {
                try
                {
                    HudProgressLabel.Text = "HLS segment listesi alınıyor...";
                    var client = _httpFactory.CreateClient("HighSpeedMediaClient");
                    string m3u8Text = !string.IsNullOrWhiteSpace(candidate.PrefetchedManifestContent)
                        ? candidate.PrefetchedManifestContent
                        : await FetchTextWithWebViewFallbackAsync(client, _activeTab, candidate.Url, candidate.RequestHeaders, ct);
                    m3u8Preview = string.Join(" | ", m3u8Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Take(8));
                    var parsed = _hlsParser.Parse(m3u8Text, new Uri(candidate.Url), candidate.Title, candidate.RequestHeaders);

                    string? sepAudioUrl = candidate.SeparateAudioUrl;
                    if (parsed.IsMaster && parsed.Variants.Count > 0)
                    {
                        var best = parsed.Variants[0];
                        sepAudioUrl ??= best.SeparateAudioUrl;
                        string subM3u8 = await FetchTextWithWebViewFallbackAsync(client, _activeTab, best.Url, candidate.RequestHeaders, ct);
                        parsed = _hlsParser.Parse(subM3u8, new Uri(best.Url), candidate.Title, candidate.RequestHeaders);
                    }

                    if (parsed.Segments.Count == 0)
                        throw new InvalidOperationException("HLS segment listesi boş.");

                    string tempDir = Path.Combine(Path.GetTempPath(), "vf_" + Guid.NewGuid().ToString("N")[..8]);
                    string combinedVideo = await _downloader.DownloadHlsSegmentsAsync(parsed.Segments, candidate.RequestHeaders, tempDir, progress, candidate.Id, ct);

                    if (!string.IsNullOrWhiteSpace(sepAudioUrl) && _engine is FfmpegConversionEngine ffMux)
                    {
                        HudProgressLabel.Text = "Ayrı ses akışı indiriliyor...";
                        string audioM3u8 = await FetchTextWithWebViewFallbackAsync(client, _activeTab, sepAudioUrl, candidate.RequestHeaders, ct);
                        var audioParsed = _hlsParser.Parse(audioM3u8, new Uri(sepAudioUrl), candidate.Title, candidate.RequestHeaders);

                        if (audioParsed.Segments.Count > 0)
                        {
                            string audioDir = Path.Combine(tempDir, "audio");
                            string combinedAudio = await _downloader.DownloadHlsSegmentsAsync(audioParsed.Segments, candidate.RequestHeaders, audioDir, progress, candidate.Id + "_a", ct);

                            HudProgressLabel.Text = "Video + Ses senkronlu MP4 olarak birleştiriliyor...";
                            await ffMux.MuxSeparateVideoAndAudioAsync(combinedVideo, combinedAudio, rawVideoPath, ct);
                        }
                        else
                        {
                            HudProgressLabel.Text = "FFmpeg ile video + ses MP4 olarak birleştiriliyor...";
                            await ffMux.RemuxHlsToMp4Async(combinedVideo, rawVideoPath, ct);
                        }
                    }
                    else if (_engine is FfmpegConversionEngine ffHls)
                    {
                        HudProgressLabel.Text = "FFmpeg ile video + ses MP4 olarak birleştiriliyor...";
                        await ffHls.RemuxHlsToMp4Async(combinedVideo, rawVideoPath, ct);
                    }
                    else
                    {
                        var plan = _planner.BuildPlan(candidate, TargetDeviceProfile.PcOriginal, "libx264");
                        await _engine.ExecuteAsync(combinedVideo, rawVideoPath, plan, ct);
                    }
                    try { Directory.Delete(tempDir, true); } catch { }

                    if (_engine is FfmpegConversionEngine ffAudioChk && string.IsNullOrWhiteSpace(await ffAudioChk.ProbeAudioCodecAsync(rawVideoPath, ct)))
                    {
                        bool audioPaired = false;
                        var otherHls = (_activeTab?.PrimaryStreams ?? []).Concat(_activeTab?.OtherStreams ?? [])
                            .Where(s => s.Protocol == StreamProtocol.Hls && s.Url != candidate.Url)
                            .ToList();

                        foreach (var alt in otherHls)
                        {
                            try
                            {
                                HudProgressLabel.Text = "Ayrı ses akışı aranıyor ve eşleştiriliyor...";
                                string altText = await FetchTextAsync(client, alt.Url, alt.RequestHeaders, ct);
                                var altParsed = _hlsParser.Parse(altText, new Uri(alt.Url), alt.Title, alt.RequestHeaders);
                                if (!altParsed.IsMaster && altParsed.Segments.Count > 0)
                                {
                                    string altDir = Path.Combine(Path.GetTempPath(), "vf_alt_" + Guid.NewGuid().ToString("N")[..6]);
                                    string altCombined = await _downloader.DownloadHlsSegmentsAsync(altParsed.Segments, alt.RequestHeaders, altDir, progress, alt.Id, ct);
                                    if (!string.IsNullOrWhiteSpace(await ffAudioChk.ProbeAudioCodecAsync(altCombined, ct)))
                                    {
                                        string muxedPath = Path.Combine(workTempDir, "mux.mp4");
                                        await ffAudioChk.MuxSeparateVideoAndAudioAsync(rawVideoPath, altCombined, muxedPath, ct);
                                        try { File.Delete(rawVideoPath); Directory.Delete(altDir, true); } catch { }
                                        rawVideoPath = muxedPath;
                                        audioPaired = true;
                                        break;
                                    }
                                    try { Directory.Delete(altDir, true); } catch { }
                                }
                            }
                            catch { }
                        }

                        if (!audioPaired)
                        {
                            HudProgressLabel.Text = "Ses akışı doğrulanıyor (yt-dlp yedeği)...";
                            _maxProgressPct = 0;
                            try { if (File.Exists(rawVideoPath)) File.Delete(rawVideoPath); } catch { }
                            await _extractor.DownloadWithYtDlpAsync(candidate.MasterUrl ?? candidate.Url, rawVideoPath, candidate.RequestHeaders, progress, ct);
                        }
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
                {
                    if (string.IsNullOrEmpty(m3u8Preview)) m3u8Preview = $"HLS hata: {ex.Message}";
                    HudProgressLabel.Text = "yt-dlp (64 kanal) yedeği ile video indiriliyor...";
                    try { if (File.Exists(rawVideoPath)) File.Delete(rawVideoPath); } catch { }
                    await _extractor.DownloadWithYtDlpAsync(candidate.Url, rawVideoPath, candidate.RequestHeaders, progress, ct);

                    if (!string.IsNullOrWhiteSpace(candidate.SeparateAudioUrl)
                        && _engine is FfmpegConversionEngine ffYtMux
                        && string.IsNullOrWhiteSpace(await ffYtMux.ProbeAudioCodecAsync(rawVideoPath, ct)))
                    {
                        HudProgressLabel.Text = "yt-dlp ile ayrı ses akışı indiriliyor...";
                        _maxProgressPct = 0;
                        string ytAudioPath = Path.Combine(workTempDir, "ytaudio.mp4");
                        await _extractor.DownloadWithYtDlpAsync(candidate.SeparateAudioUrl, ytAudioPath, candidate.RequestHeaders, progress, ct);
                        HudProgressLabel.Text = "Video + Ayrı Ses FFmpeg ile birleştiriliyor...";
                        string muxedPath = Path.Combine(workTempDir, "mux.mp4");
                        await ffYtMux.MuxSeparateVideoAndAudioAsync(rawVideoPath, ytAudioPath, muxedPath, ct);
                        try { File.Delete(rawVideoPath); File.Delete(ytAudioPath); } catch { }
                        rawVideoPath = muxedPath;
                    }
                }
            }
            else if (candidate.Protocol == StreamProtocol.Dash)
            {
                HudProgressLabel.Text = "DASH (.mpd) 64 kanal ile indiriliyor...";
                await _extractor.DownloadWithYtDlpAsync(candidate.MasterUrl ?? candidate.Url, rawVideoPath, candidate.RequestHeaders, progress, ct);
            }
            else
            {
                string directTmp = Path.Combine(workTempDir, "dl.mp4");
                await _downloader.DownloadDirectRangeParallelAsync(new Uri(candidate.Url), candidate.RequestHeaders, directTmp, new HighSpeedDownloadOptions(MaxConcurrency: 64), progress, candidate.Id, ct);
                if (_engine is FfmpegConversionEngine ffDirect)
                {
                    HudProgressLabel.Text = "Ses uyumluluğu doğrulanıyor...";
                    await ffDirect.EnsureCompatibleAudioAsync(directTmp, rawVideoPath, ct);
                }
                else
                {
                    File.Move(directTmp, rawVideoPath, overwrite: true);
                }
            }

            string extraTags = "";
            if (_engine is FfmpegConversionEngine ffEnh)
            {
                if (!string.IsNullOrWhiteSpace(job.TrimStart) || !string.IsNullOrWhiteSpace(job.TrimEnd))
                {
                    HudProgressLabel.Text = "Seçilen zaman aralığı kırpılıyor...";
                    string trimPath = Path.Combine(workTempDir, "trim.mp4");
                    await ffEnh.TrimVideoAsync(rawVideoPath, trimPath, job.TrimStart, job.TrimEnd, ct);
                    try { File.Delete(rawVideoPath); } catch { }
                    rawVideoPath = trimPath;
                    extraTags += "_Trim";
                }

                if (job.EnhanceTag == "cinema")
                {
                    HudProgressLabel.Text = "Sinema iyileştirme uygulanıyor (netlik, renk)...";
                    string enhPath = Path.Combine(workTempDir, "enh.mp4");
                    await ffEnh.EnhanceCinemaQualityAsync(rawVideoPath, enhPath, ct);
                    try { File.Delete(rawVideoPath); } catch { }
                    rawVideoPath = enhPath;
                    extraTags += "_Cinema";
                }
                else if (job.EnhanceTag == "hdr_only" && await ffEnh.IsHdrVideoAsync(rawVideoPath, ct))
                {
                    HudProgressLabel.Text = "HDR video SDR canlı renklere dönüştürülüyor...";
                    string sdrPath = Path.Combine(workTempDir, "sdr.mp4");
                    if (await ffEnh.EnsureSdrColorsIfHdrAsync(rawVideoPath, sdrPath, ct))
                    {
                        try { File.Delete(rawVideoPath); } catch { }
                        rawVideoPath = sdrPath;
                        extraTags += "_SDR";
                    }
                }
            }

            string resTag = string.IsNullOrWhiteSpace(candidate.Resolution) ? "Video" : candidate.Resolution.Replace("x", "p");
            if (job.Subtitle is { } sub && _engine is FfmpegConversionEngine ffmpegEngine)
            {
                finalPath = Path.Combine(outDir, $"{safeName}_{resTag}{extraTags}_{sub.Language.ToUpperInvariant()}-Sub_{DateTime.Now:HHmmss}.mp4");
                HudProgressLabel.Text = $"Altyazı indiriliyor ve gömülüyor ({sub.Language.ToUpperInvariant()})...";
                string srtPath = Path.Combine(Path.GetTempPath(), $"sub_{Guid.NewGuid():N}.srt");
                try
                {
                    await _subtitles.DownloadToSrtAsync(sub, srtPath, ct);
                    string head = File.Exists(srtPath) ? File.ReadAllText(srtPath).TrimStart() : "";
                    if (head.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase))
                    {
                        string vttPath = Path.ChangeExtension(srtPath, ".vtt");
                        File.Move(srtPath, vttPath, overwrite: true);
                        srtPath = vttPath;
                    }
                    string lang3 = sub.Language.ToLowerInvariant() switch { "tr" => "tur", "de" => "ger", "fr" => "fre", "es" => "spa", _ => "eng" };
                    await ffmpegEngine.EmbedSoftSubtitleAsync(rawVideoPath, srtPath, lang3, finalPath, ct);
                    try { File.Delete(rawVideoPath); } catch { }
                }
                catch
                {
                    finalPath = Path.Combine(outDir, $"{safeName}_{resTag}{extraTags}_{DateTime.Now:HHmmss}.mp4");
                    File.Move(rawVideoPath, finalPath, overwrite: true);
                }
                finally { try { File.Delete(srtPath); } catch { } }
            }
            else
            {
                finalPath = Path.Combine(outDir, $"{safeName}_{resTag}{extraTags}_{DateTime.Now:HHmmss}.mp4");
                File.Move(rawVideoPath, finalPath, overwrite: true);
            }

            string qualityInfo = _engine is FfmpegConversionEngine ff ? await ff.ProbeResolutionSummaryAsync(finalPath, ct) : candidate.Resolution;
            string audioInfo = _engine is FfmpegConversionEngine ffA ? await ffA.ProbeAudioCodecAsync(finalPath, ct) : "";
            DiagnosticLogBox.Text = $"Kaynak: {candidate.Protocol} | URL: {candidate.Url}\nAyrı Ses: {candidate.SeparateAudioUrl ?? "Yok"}\nSes Kodeği: {(string.IsNullOrEmpty(audioInfo) ? "YOK" : audioInfo)} | Video: {qualityInfo}\nM3U8: {m3u8Preview}";
            double mb = new FileInfo(finalPath).Length / (1024.0 * 1024.0);
            HudProgress.Value = 100;
            HudProgressLabel.Text = $"Tamamlandı ✓ • {(string.IsNullOrEmpty(qualityInfo) ? candidate.Resolution : qualityInfo)} • {mb:F1} MB | {Path.GetFileName(finalPath)}";
            HudStatusText.Text = "● İndirildi ve Kaydedildi";
            HudDownloadButton.Content = "Yeniden İndir";
            HudResetButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            HudProgressLabel.Text = "İndirme iptal edildi.";
            HudDownloadButton.Content = "Yeniden İndir";
            HudResetButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            HudProgressLabel.Text = $"Hata: {ex.Message}";
            DiagnosticLogBox.Text = (_engine is FfmpegConversionEngine ffErr && !string.IsNullOrWhiteSpace(ffErr.LastDiagnosticLog))
                ? ffErr.LastDiagnosticLog
                : ex.ToString();
            HudDownloadButton.Content = "Yeniden Dene";
            HudResetButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _isDownloading = false;
            HudDownloadButton.IsEnabled = (_activeTab?.PrimaryStreams.Count ?? 0) > 0;
            HudCancelButton.Visibility = Visibility.Collapsed;
            try { if (Directory.Exists(workTempDir)) Directory.Delete(workTempDir, true); } catch { }
        }

        if (_downloadQueue.Count > 0)
        {
            var next = _downloadQueue.Dequeue();
            RefreshQueueUi();
            await ExecuteDownloadJobAsync(next);
        }
    }

    private void OnCopyDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DiagnosticLogBox.Text)) return;
        var dp = new DataPackage();
        dp.SetText(DiagnosticLogBox.Text);
        Clipboard.SetContent(dp);
    }

    private void LoadBrowserStateAndRestoreTabs()
    {
        try
        {
            if (File.Exists(BrowserStatePath))
            {
                var data = JsonSerializer.Deserialize<BrowserStateData>(File.ReadAllText(BrowserStatePath));
                if (data is not null)
                {
                    _history.Clear();
                    _history.AddRange(data.History.Take(50));
                    _bookmarks.Clear();
                    _bookmarks.AddRange(data.Bookmarks);
                    RefreshHistoryUi();
                    RefreshBookmarksUi();

                    if (data.OpenTabs.Count > 0)
                    {
                        _ = RestoreTabsAsync(data.OpenTabs, data.ActiveTabIndex);
                        return;
                    }
                }
            }
        }
        catch { }
        _ = CreateNewTabAsync("https://www.google.com");
    }

    private async Task RestoreTabsAsync(List<string> urls, int activeIndex)
    {
        foreach (var u in urls.Where(x => Uri.TryCreate(x, UriKind.Absolute, out _)).Take(8))
            await CreateNewTabAsync(u);

        if (_tabs.Count == 0)
            await CreateNewTabAsync("https://www.google.com");
        else if (activeIndex >= 0 && activeIndex < _tabs.Count)
            SwitchToTab(_tabs[activeIndex]);
    }

    private void RecordHistory(string title, string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
        _history.RemoveAll(h => string.Equals(h.Url, url, StringComparison.OrdinalIgnoreCase));
        _history.Insert(0, new BrowserHistoryEntry
        {
            Title = string.IsNullOrWhiteSpace(title) ? url : title,
            Url = url,
            VisitedAt = DateTime.Now
        });
        if (_history.Count > 50) _history.RemoveRange(50, _history.Count - 50);
        RefreshHistoryUi();
        SaveBrowserState();
    }

    private void RefreshHistoryUi()
    {
        HistoryList.Items.Clear();
        foreach (var h in _history)
            HistoryList.Items.Add($"{h.Title}\n{h.Url}");
    }

    private void SaveBrowserState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BrowserStatePath)!);
            var data = new BrowserStateData
            {
                OpenTabs = _tabs.Select(t => t.Url).Where(u => !string.IsNullOrWhiteSpace(u)).ToList(),
                ActiveTabIndex = _activeTab is not null ? Math.Max(0, _tabs.IndexOf(_activeTab)) : 0,
                History = _history.Take(50).ToList(),
                Bookmarks = _bookmarks.ToList()
            };
            File.WriteAllText(BrowserStatePath, JsonSerializer.Serialize(data));
        }
        catch { }
    }

    private void OnHistoryItemSelected(object sender, SelectionChangedEventArgs e)
    {
        int idx = HistoryList.SelectedIndex;
        if (idx < 0 || idx >= _history.Count || _activeTab is null) return;
        string url = _history[idx].Url;
        HistoryList.SelectedIndex = -1;
        HistoryButton.Flyout?.Hide();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            _activeTab.WebView.Source = uri;
    }

    private void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        RefreshHistoryUi();
        SaveBrowserState();
    }

    private void OnNewTabClick(object sender, RoutedEventArgs e) => _ = CreateNewTabAsync("https://www.google.com");

    private void OnAdBlockToggleClick(object sender, RoutedEventArgs e)
    {
        _adBlockEnabled = AdBlockToggle.IsChecked == true;
        AdBlockToggle.Content = _adBlockEnabled ? "🛡️ Reklam Kalkanı: Açık" : "🛡️ Reklam Kalkanı: Kapalı";
    }

    private static async Task<string> FetchTextWithWebViewFallbackAsync(
        HttpClient client, BrowserTabSession? tab, string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        try
        {
            return await FetchTextAsync(client, url, headers, ct);
        }
        catch (HttpRequestException) when (tab?.WebView.CoreWebView2 is not null)
        {
            string reqId = Guid.NewGuid().ToString("N")[..8];
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingFetches[reqId] = tcs;
            try
            {
                string js = $"(async () => {{ let text = ''; try {{ const r = await fetch({JsonSerializer.Serialize(url)}, {{ credentials: 'include' }}); if (r.ok) text = await r.text(); }} catch {{}} window.chrome.webview.postMessage(JSON.stringify({{ type: 'vf_fetch_res', id: {JsonSerializer.Serialize(reqId)}, text }})); }})()";
                await tab.WebView.CoreWebView2.ExecuteScriptAsync(js);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(2.5));
                string text = await tcs.Task.WaitAsync(timeoutCts.Token);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            catch { }
            finally { _pendingFetches.TryRemove(reqId, out _); }
            throw;
        }
    }

    private void RefreshBookmarksUi()
    {
        BookmarksList.Items.Clear();
        foreach (var b in _bookmarks)
            BookmarksList.Items.Add($"{b.Title}\n{b.Url}");
    }

    private void OnAddBookmarkClick(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null || string.IsNullOrWhiteSpace(_activeTab.Url)) return;
        if (_bookmarks.Any(b => string.Equals(b.Url, _activeTab.Url, StringComparison.OrdinalIgnoreCase))) return;
        _bookmarks.Insert(0, new BrowserHistoryEntry { Title = _activeTab.Title, Url = _activeTab.Url, VisitedAt = DateTime.Now });
        RefreshBookmarksUi();
        SaveBrowserState();
    }

    private void OnBookmarkSelected(object sender, SelectionChangedEventArgs e)
    {
        int idx = BookmarksList.SelectedIndex;
        if (idx < 0 || idx >= _bookmarks.Count || _activeTab is null) return;
        string url = _bookmarks[idx].Url;
        BookmarksList.SelectedIndex = -1;
        BookmarksButton.Flyout?.Hide();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) _activeTab.WebView.Source = uri;
    }

    private void OnRefreshLibraryClick(object sender, RoutedEventArgs e)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VideoFetcher");
        Directory.CreateDirectory(dir);
        _libraryFiles.Clear();
        LibraryList.Items.Clear();
        var files = new DirectoryInfo(dir).GetFiles("*.mp4").OrderByDescending(f => f.LastWriteTime).Take(40);
        foreach (var f in files)
        {
            _libraryFiles.Add(f.FullName);
            double mb = f.Length / (1024.0 * 1024.0);
            LibraryList.Items.Add($"{f.Name}\n{mb:F1} MB • {f.LastWriteTime:dd.MM HH:mm}");
        }
    }

    private void OnPlayLibraryItemClick(object sender, RoutedEventArgs e)
    {
        int idx = LibraryList.SelectedIndex;
        if (idx < 0 || idx >= _libraryFiles.Count) return;
        Process.Start(new ProcessStartInfo(_libraryFiles[idx]) { UseShellExecute = true });
    }

    private void OnDeleteLibraryItemClick(object sender, RoutedEventArgs e)
    {
        int idx = LibraryList.SelectedIndex;
        if (idx < 0 || idx >= _libraryFiles.Count) return;
        try { File.Delete(_libraryFiles[idx]); } catch { }
        OnRefreshLibraryClick(sender, e);
    }

    private static async Task<string> FetchTextAsync(HttpClient client, string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private void OnAddressKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || _activeTab is null) return;
        string input = AddressBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(input)) return;

        bool looksLikeUrl = input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                         || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                         || (!input.Contains(' ') && input.Contains('.'));

        if (looksLikeUrl)
        {
            string url = input.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? input : "https://" + input;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) _activeTab.WebView.Source = uri;
        }
        else
        {
            _activeTab.WebView.Source = new Uri($"https://www.google.com/search?q={Uri.EscapeDataString(input)}");
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e) { if (_activeTab?.WebView.CanGoBack == true) _activeTab.WebView.GoBack(); }
    private void OnForwardClick(object sender, RoutedEventArgs e) { if (_activeTab?.WebView.CanGoForward == true) _activeTab.WebView.GoForward(); }
    private void OnReloadClick(object sender, RoutedEventArgs e) => _activeTab?.WebView.Reload();
    private void OnToggleHudClick(object sender, RoutedEventArgs e) =>
        FloatingHud.Visibility = FloatingHud.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    private void OnOpenLibraryFolderClick(object sender, RoutedEventArgs e)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VideoFetcher");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }
}
