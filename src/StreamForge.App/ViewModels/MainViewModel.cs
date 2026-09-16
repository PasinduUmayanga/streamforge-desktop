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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    public partial string OutputPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double ProgressPercentage { get; set; }

    [ObservableProperty]
    public partial string DownloadSpeed { get; set; } = "Speed: --";

    [ObservableProperty]
    public partial long DownloadedBytes { get; set; }

    [ObservableProperty]
    public partial string DownloadedBytesDisplay { get; set; } = "Downloaded: 0 MB";

    [ObservableProperty]
    public partial string ElapsedTime { get; set; } = "Elapsed: 00:00:00";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    public partial bool IsAnalyzing { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    public partial bool IsDownloading { get; set; }

    public bool CanAnalyze => !IsAnalyzing && !IsDownloading && Uri.TryCreate(PageUrl, UriKind.Absolute, out _);

    public bool CanDownload => !IsAnalyzing
        && !IsDownloading
        && _detectedStream is not null
        && SelectedQuality is not null
        && !string.IsNullOrWhiteSpace(OutputPath);

    partial void OnPageUrlChanged(string value) => OnPropertyChanged(nameof(CanAnalyze));

    partial void OnOutputPathChanged(string value) => OnPropertyChanged(nameof(CanDownload));

    partial void OnSelectedQualityChanged(StreamQuality? value) => OnPropertyChanged(nameof(CanDownload));

    partial void OnIsAnalyzingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAnalyze));
        OnPropertyChanged(nameof(CanDownload));
    }

    partial void OnIsDownloadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAnalyze));
        OnPropertyChanged(nameof(CanDownload));
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
        StatusMessage = "Finding media stream...";

        try
        {
            var stream = await _streamExtractor.ExtractAsync(pageUri, _operationCts.Token);
            if (stream is null)
            {
                StatusMessage = "No supported media stream was detected.";
                return;
            }

            foreach (var analyzer in _streamAnalyzers.Where(a => a.CanAnalyze(stream)))
            {
                stream = await analyzer.AnalyzeAsync(stream, _operationCts.Token);
            }

            _detectedStream = stream;
            OnPropertyChanged(nameof(CanDownload));
            DetectedStreamUrl = stream.Url.AbsoluteUri;
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
        ProgressPercentage = 0;
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
            IsDownloading = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
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
        AvailableQualities.Clear();
        SelectedQuality = null;
        ProgressPercentage = 0;
    }

    private void UpdateProgress(DownloadProgress progress)
    {
        DownloadedBytes = progress.BytesDownloaded;
        DownloadedBytesDisplay = $"Downloaded: {progress.BytesDownloaded / 1024d / 1024d:0.##} MB";
        DownloadSpeed = $"Speed: {progress.Speed ?? "--"}";
        ElapsedTime = $"Elapsed: {progress.Elapsed:hh\\:mm\\:ss}";
        if (progress.Percentage is { } percentage)
        {
            ProgressPercentage = percentage;
        }
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
