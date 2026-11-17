using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FH6GarageEditor;

public partial class UpdaterPage : UserControl
{
    private sealed record VersionRow(string Source, string Version, string Date, string Title);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ReleaseUpdates updates = new(Http);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? downloadCancellation;
    private PublishedRelease? latest;
    private bool checking;

    internal Func<bool>? CanStartInstallation { get; set; }
    internal event Action<bool>? UpdateAvailableChanged;
    internal event Action<bool>? InstallStateChanged;

    static UpdaterPage() => Http.DefaultRequestHeaders.UserAgent.ParseAdd($"BetterCarTag/{ReleaseUpdates.CurrentVersion}");

    public UpdaterPage()
    {
        InitializeComponent();
        ShowVersions(null);
    }

    internal void CancelPending() => lifetime.Cancel();

    internal async Task CheckAsync()
    {
        if (checking || downloadCancellation is not null) return;
        checking = true;
        CheckUpdate.IsEnabled = InstallUpdate.IsEnabled = false;
        SetStatus("正在检查 GitHub Release…", "Muted");
        SetNote(null);
        try
        {
            var history = await updates.CheckAsync(lifetime.Token);
            latest = history.FirstOrDefault(release => release.Download is not null);
            ReleaseHistory.ItemsSource = history;
            ShowVersions(latest);
            bool newer = latest is not null && latest.Version > ReleaseUpdates.CurrentVersion;
            UpdateAvailableChanged?.Invoke(newer);
            InstallUpdate.Visibility = newer && ReleaseUpdates.CanInstall ? Visibility.Visible : Visibility.Collapsed;
            if (newer)
            {
                SetStatus($"发现新版本 {latest!.VersionText}", "Accent");
                if (!ReleaseUpdates.CanInstall) SetNote("可在 GitHub 发布页下载新版 Windows 程序。");
            }
            else if (latest is null) SetStatus("GitHub 暂无可校验的 Windows 下载", "Muted");
            else if (latest.Version == ReleaseUpdates.CurrentVersion) SetStatus("已是最新版本", "Success");
            else SetStatus("本地版本高于当前线上版本", "Accent");
            if (history.FirstOrDefault() is { Download: null } newest && newest.Version > ReleaseUpdates.CurrentVersion &&
                (latest is null || newest.Version > latest.Version))
                SetNote($"{newest.VersionText} 暂无可校验的 Windows 下载。");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            latest = null;
            InstallUpdate.Visibility = Visibility.Collapsed;
            UpdateAvailableChanged?.Invoke(false);
            SetStatus("检查更新失败", "Warning");
            SetNote(error.Message);
        }
        finally
        {
            checking = false;
            CheckUpdate.IsEnabled = true;
            InstallUpdate.IsEnabled = latest is not null && latest.Version > ReleaseUpdates.CurrentVersion;
        }
    }

    private async void CheckClick(object sender, RoutedEventArgs args) => await CheckAsync();

    private async void InstallClick(object sender, RoutedEventArgs args)
    {
        if (latest is not { } release || checking || downloadCancellation is not null) return;
        if (CanStartInstallation?.Invoke() == false)
        {
            SetNote("请等待车库读取或应用到游戏完成，再开始更新。");
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        downloadCancellation = cancellation;
        cancellation.CancelAfter(TimeSpan.FromMinutes(15));
        InstallStateChanged?.Invoke(true);
        CheckUpdate.IsEnabled = InstallUpdate.IsEnabled = false;
        CancelUpdate.Visibility = DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        SetStatus($"正在下载 {release.VersionText} · 0%", "Accent");
        SetNote("下载及校验完成后，程序会自动重启。");
        var progress = new Progress<double>(percent =>
        {
            if (downloadCancellation != cancellation) return;
            DownloadProgress.Value = percent;
            SetStatus(percent < 100 ? $"正在下载 {release.VersionText} · {percent:0}%" : "正在校验并准备重启…", "Accent");
        });
        try
        {
            await updates.DownloadAndRestartAsync(release, progress, cancellation.Token);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            SetStatus("下载已取消或超时", "Muted");
            SetNote(null);
        }
        catch (Exception error)
        {
            SetStatus("更新失败", "Warning");
            SetNote(error.Message);
        }
        finally
        {
            downloadCancellation = null;
            CheckUpdate.IsEnabled = InstallUpdate.IsEnabled = true;
            CancelUpdate.Visibility = DownloadProgress.Visibility = Visibility.Collapsed;
            InstallStateChanged?.Invoke(false);
        }
    }

    private void CancelClick(object sender, RoutedEventArgs args) => downloadCancellation?.Cancel();

    private void OpenReleasePage(object sender, RoutedEventArgs args)
    {
        try { using var browser = Process.Start(new ProcessStartInfo(ReleaseUpdates.ReleasePage) { UseShellExecute = true }); }
        catch (Exception error) { SetNote(error.Message); }
    }

    private void ShowVersions(PublishedRelease? online)
    {
        Versions.ItemsSource = new[]
        {
            new VersionRow("本地", $"v{ReleaseUpdates.CurrentVersion}", "—", "当前程序"),
            new VersionRow("线上", online?.VersionText ?? "—", online?.PublishedText ?? "—", online?.Title ?? "—"),
        };
    }

    private void SetStatus(string text, string brush)
    {
        UpdateStatus.Text = text;
        StatusDot.Fill = (Brush)FindResource(brush);
    }

    private void SetNote(string? text)
    {
        UpdateNote.Text = text ?? "";
        UpdateNote.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
