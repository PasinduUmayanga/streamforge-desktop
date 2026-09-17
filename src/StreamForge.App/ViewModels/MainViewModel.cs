using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamForge.Core.Interfaces;
using StreamForge.Core.Models;

namespace StreamForge.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IStreamExtractor _streamExtractor;
    private readonly IEnumerable<IStreamAnalyzer> _streamAnalyzers;
    private readonly IDownloadService _downloadService;
    private CancellationTokenSource? _operationCts;
    private MediaStream? _detectedStream;

    public MainViewModel(
        IStreamExtractor streamExtractor,
        IEnumerable<IStreamAnalyzer> streamAnalyzers,
        IDownloadService downloadService)
    {
        _streamExtractor = streamExtractor;
        _streamAnalyzers = streamAnalyzers;
        _downloadService = downloadService;
        OutputPath = CreateDefaultOutputPath();
        StatusMessage = "Ready.";
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    public partial string PageUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetectedStreamUrl { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    public partial StreamQuality? SelectedQuality { get; set; }

    public ObservableCollection<StreamQuality> AvailableQualities { get; } = [];

    public ObservableCollection<NetworkActivity> NetworkActivities { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    public partial string OutputPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DownloadApiUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RequestContextDisplay { get; set; } = "Request context will appear after analysis.";

    [ObservableProperty]
    public partial double ProgressPercentage { get; set; }

    [ObservableProperty]
    public partial string ProgressPercentageDisplay { get; set; } = "0%";

    [ObservableProperty]
    public partial string DownloadSpeed { get; set; } = "Speed: --";

    [ObservableProperty]
    public partial long DownloadedBytes { get; set; }

    [ObservableProperty]
    public partial string DownloadedBytesDisplay { get; set; } = "Downloaded: 0 MB";

    [ObservableProperty]
    public partial string ElapsedTime { get; set; } = "Elapsed: 00:00:00";

    [ObservableProperty]
    public partial string EstimatedTime { get; set; } = "Time remaining: --";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    public partial bool IsAnalyzing { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    public partial bool IsPaused { get; set; }

    public bool CanAnalyze => !IsAnalyzing && !IsDownloading && Uri.TryCreate(PageUrl, UriKind.Absolute, out _);

    public bool CanDownload => !IsAnalyzing
        && !IsDownloading
        && _detectedStream is not null
        && SelectedQuality is not null
        && !string.IsNullOrWhiteSpace(OutputPath);

    public bool CanPause => IsDownloading && !IsPaused;

    public bool CanResume => IsDownloading && IsPaused;

    partial void OnPageUrlChanged(string value) => OnPropertyChanged(nameof(CanAnalyze));

    partial void OnOutputPathChanged(string value) => OnPropertyChanged(nameof(CanDownload));

    partial void OnSelectedQualityChanged(StreamQuality? value)
    {
        OnPropertyChanged(nameof(CanDownload));
        DownloadApiUrl = value?.Url.AbsoluteUri ?? _detectedStream?.Url.AbsoluteUri ?? string.Empty;
    }

    partial void OnIsAnalyzingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAnalyze));
        OnPropertyChanged(nameof(CanDownload));
    }

    partial void OnIsDownloadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAnalyze));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
    }

    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        if (!Uri.TryCreate(PageUrl, UriKind.Absolute, out var pageUri))
        {
            StatusMessage = "Invalid page URL.";
            return;
        }

        ResetDetection();
        IsAnalyzing = true;
        _operationCts = new CancellationTokenSource();
        StatusMessage = "Loading page and monitoring media requests...";

        try
        {
            var networkActivity = new Progress<NetworkActivity>(AddNetworkActivity);
            var stream = await _streamExtractor.ExtractAsync(pageUri, networkActivity, _operationCts.Token);
            if (stream is null)
            {
                StatusMessage = "No supported media stream was detected.";
                return;
            }

            StatusMessage = "Media stream found; analyzing available qualities...";

            foreach (var analyzer in _streamAnalyzers.Where(a => a.CanAnalyze(stream)))
            {
                stream = await analyzer.AnalyzeAsync(stream, _operationCts.Token);
            }

            _detectedStream = stream;
            OnPropertyChanged(nameof(CanDownload));
            DetectedStreamUrl = stream.Url.AbsoluteUri;
            RequestContextDisplay = BuildRequestContext(stream);
            AvailableQualities.Clear();
            foreach (var quality in stream.Qualities.DefaultIfEmpty(new StreamQuality { Label = "Auto", Url = stream.Url }))
            {
                AvailableQualities.Add(quality);
            }

            SelectedQuality = AvailableQualities.FirstOrDefault();
            StatusMessage = $"Detected {stream.Type} stream.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Analysis cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsAnalyzing = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        if (_detectedStream is null || SelectedQuality is null)
        {
            return;
        }

        IsDownloading = true;
        IsPaused = false;
        ProgressPercentage = 0;
        ProgressPercentageDisplay = "0%";
        DownloadedBytes = 0;
        DownloadedBytesDisplay = "Downloaded: 0 MB";
        DownloadSpeed = "Speed: Calculating...";
        ElapsedTime = "Elapsed: 00:00:00";
        EstimatedTime = "Time remaining: Calculating...";
        _operationCts = new CancellationTokenSource();
        StatusMessage = "Download started...";

        var progress = new Progress<DownloadProgress>(UpdateProgress);
        try
        {
            var result = await _downloadService.DownloadAsync(
                new DownloadRequest
                {
                    Stream = _detectedStream,
                    Quality = SelectedQuality,
                    OutputPath = OutputPath
                },
                progress,
                _operationCts.Token);

            StatusMessage = result.Succeeded
                ? "Download completed."
                : result.ErrorMessage ?? "Download failed.";
        }
        finally
        {
            IsPaused = false;
            IsDownloading = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        if (!_downloadService.TryPause())
        {
            StatusMessage = "The active download could not be paused.";
            return;
        }

        IsPaused = true;
        DownloadSpeed = "Speed: Paused";
        EstimatedTime = "Time remaining: Paused";
        StatusMessage = "Download paused.";
    }

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume()
    {
        if (!_downloadService.TryResume())
        {
            StatusMessage = "The paused download could not be resumed.";
            return;
        }

        IsPaused = false;
        DownloadSpeed = "Speed: Calculating...";
        EstimatedTime = "Time remaining: Calculating...";
        StatusMessage = "Download resumed.";
    }

    [RelayCommand]
    private void Cancel()
    {
        _operationCts?.Cancel();
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        // FileSavePicker requires window handle plumbing; keep path editable for this first vertical slice.
        OutputPath = CreateDefaultOutputPath();
        StatusMessage = "Output path reset to the default Videos filename. You can edit it directly.";
    }

    private void ResetDetection()
    {
        _detectedStream = null;
        DetectedStreamUrl = string.Empty;
        DownloadApiUrl = string.Empty;
        RequestContextDisplay = "Request context will appear after analysis.";
        NetworkActivities.Clear();
        AvailableQualities.Clear();
        SelectedQuality = null;
        ProgressPercentage = 0;
        ProgressPercentageDisplay = "0%";
        DownloadedBytes = 0;
        DownloadedBytesDisplay = "Downloaded: 0 MB";
        DownloadSpeed = "Speed: --";
        ElapsedTime = "Elapsed: 00:00:00";
        EstimatedTime = "Time remaining: --";
    }

    private void AddNetworkActivity(NetworkActivity activity)
    {
        const int maximumActivityCount = 200;
        while (NetworkActivities.Count >= maximumActivityCount)
        {
            NetworkActivities.RemoveAt(0);
        }

        NetworkActivities.Add(activity);
    }

    private static string BuildRequestContext(MediaStream stream)
    {
        var values = new List<string>();
        if (!string.IsNullOrWhiteSpace(stream.Referer))
        {
            values.Add($"Referer: {SanitizeContextUrl(stream.Referer)}");
        }

        if (!string.IsNullOrWhiteSpace(stream.Origin))
        {
            values.Add($"Origin: {stream.Origin}");
        }

        if (!string.IsNullOrWhiteSpace(stream.UserAgent))
        {
            values.Add($"User-Agent: {stream.UserAgent}");
        }

        return values.Count == 0 ? "No additional request headers were detected." : string.Join(Environment.NewLine, values);
    }

    private static string SanitizeContextUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Path)
            : value;
    }

    private void UpdateProgress(DownloadProgress progress)
    {
        DownloadedBytes = progress.BytesDownloaded;
        DownloadedBytesDisplay = $"Downloaded: {progress.BytesDownloaded / 1024d / 1024d:0.##} MB";
        DownloadSpeed = progress.BytesPerSecond is { } bytesPerSecond
            ? $"Speed: {FormatTransferRate(bytesPerSecond)}"
            : "Speed: Calculating...";
        ElapsedTime = $"Elapsed: {progress.Elapsed:hh\\:mm\\:ss}";
        EstimatedTime = progress.EstimatedTimeRemaining is { } remaining
            ? $"Time remaining: {FormatDuration(remaining)}"
            : "Time remaining: Calculating...";
        if (progress.Percentage is { } percentage)
        {
            ProgressPercentage = percentage;
            ProgressPercentageDisplay = $"{percentage:0}%";
        }
    }

    private static string FormatTransferRate(double bytesPerSecond)
    {
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        var value = Math.Max(0, bytesPerSecond);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        return duration.TotalDays >= 1
            ? $"{(int)duration.TotalDays}d {duration:hh\\:mm\\:ss}"
            : $"{duration:hh\\:mm\\:ss}";
    }

    private static string CreateDefaultOutputPath()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrWhiteSpace(videos))
        {
            videos = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        return Path.Combine(videos, $"video-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
    }
}
