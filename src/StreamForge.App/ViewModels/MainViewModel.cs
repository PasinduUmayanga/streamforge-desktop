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
        ResetIdentificationSteps();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    public partial string PageUrl { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoToAnalyzeStepCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToStreamStepCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToDownloadStepCommand))]
    public partial int WizardStepIndex { get; set; }

    public bool IsAnalyzeStepActive => WizardStepIndex == 0;

    public bool IsAnalysisProgressStepActive => WizardStepIndex == 1;

    public bool IsStreamStepActive => WizardStepIndex == 2;

    public bool IsDownloadStepActive => WizardStepIndex == 3;

    public bool IsCompleteStepActive => WizardStepIndex == 4;

    [ObservableProperty]
    public partial string DetectedStreamUrl { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    public partial StreamQuality? SelectedQuality { get; set; }

    public ObservableCollection<StreamQuality> AvailableQualities { get; } = [];

    public ObservableCollection<NetworkActivity> NetworkActivities { get; } = [];

    public ObservableCollection<IdentificationStepUpdate> IdentificationSteps { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    public partial string OutputPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool UseLocalAiAdvisor { get; set; }

    [ObservableProperty]
    public partial string LocalAiEndpoint { get; set; } = "http://localhost:11434";

    [ObservableProperty]
    public partial string LocalAiModel { get; set; } = "qwen3-coder-next";

    [ObservableProperty]
    public partial string AiDiagnostic { get; set; } = "Local AI diagnostics are off.";

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
    [NotifyCanExecuteChangedFor(nameof(BrowseOutputCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsAnalyzing { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseOutputCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
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

    public bool CanEditPageUrl => !IsAnalyzing && !IsDownloading;

    public bool CanConfigureDownload => !IsAnalyzing && !IsDownloading;

    public bool CanEditAiSettings => CanEditPageUrl && UseLocalAiAdvisor;

    public bool CanCancel => IsAnalyzing || IsDownloading;

    public bool CanGoToAnalyzeStep => !IsAnalyzing && !IsDownloading;

    public bool CanGoToStreamStep => !IsAnalyzing && !IsDownloading && _detectedStream is not null;

    public bool CanGoToDownloadStep => CanGoToStreamStep && SelectedQuality is not null;

    partial void OnPageUrlChanged(string value) => OnPropertyChanged(nameof(CanAnalyze));

    partial void OnOutputPathChanged(string value) => OnPropertyChanged(nameof(CanDownload));

    partial void OnSelectedQualityChanged(StreamQuality? value)
    {
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanGoToDownloadStep));
        GoToDownloadStepCommand.NotifyCanExecuteChanged();
        DownloadApiUrl = value?.Url.AbsoluteUri ?? _detectedStream?.Url.AbsoluteUri ?? string.Empty;
    }

    partial void OnUseLocalAiAdvisorChanged(bool value) => OnPropertyChanged(nameof(CanEditAiSettings));

    partial void OnIsAnalyzingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAnalyze));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanEditPageUrl));
        OnPropertyChanged(nameof(CanConfigureDownload));
        OnPropertyChanged(nameof(CanEditAiSettings));
        OnPropertyChanged(nameof(CanCancel));
        RefreshWizardNavigation();
    }

    partial void OnIsDownloadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAnalyze));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanEditPageUrl));
        OnPropertyChanged(nameof(CanConfigureDownload));
        OnPropertyChanged(nameof(CanEditAiSettings));
        OnPropertyChanged(nameof(CanCancel));
        RefreshWizardNavigation();
    }

    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
    }

    partial void OnWizardStepIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsAnalyzeStepActive));
        OnPropertyChanged(nameof(IsAnalysisProgressStepActive));
        OnPropertyChanged(nameof(IsStreamStepActive));
        OnPropertyChanged(nameof(IsDownloadStepActive));
        OnPropertyChanged(nameof(IsCompleteStepActive));
    }

    [RelayCommand(CanExecute = nameof(CanGoToAnalyzeStep))]
    private void GoToAnalyzeStep()
    {
        WizardStepIndex = 0;
    }

    [RelayCommand(CanExecute = nameof(CanGoToStreamStep))]
    private void GoToStreamStep()
    {
        WizardStepIndex = 2;
    }

    [RelayCommand(CanExecute = nameof(CanGoToDownloadStep))]
    private void GoToDownloadStep()
    {
        WizardStepIndex = 3;
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        if (!Uri.TryCreate(PageUrl, UriKind.Absolute, out var pageUri))
        {
            StatusMessage = "Invalid page URL.";
            return;
        }

        try
        {
            if (!TryCreateExtractionOptions(out var extractionOptions))
            {
                return;
            }

            ResetDetection();
            WizardStepIndex = 1;
            IsAnalyzing = true;
            _operationCts = new CancellationTokenSource();
            StatusMessage = "Loading page and monitoring media requests...";

            var networkActivity = new Progress<NetworkActivity>(AddNetworkActivity);
            var identificationProgress = new Progress<IdentificationStepUpdate>(UpdateIdentificationStep);
            var extraction = await _streamExtractor.ExtractAsync(
                pageUri,
                extractionOptions,
                networkActivity,
                identificationProgress,
                _operationCts.Token);
            AiDiagnostic = extraction.AiAdvisorUsed
                ? extraction.AiDiagnostic ?? "The local AI advisor returned no additional details."
                : UseLocalAiAdvisor
                    ? "No sanitized player response required AI classification."
                    : "Local AI diagnostics are off.";

            var stream = extraction.Stream;
            if (stream is null)
            {
                UpdateIdentificationStep(Step(
                    IdentificationStepKeys.AnalyzeManifest,
                    8,
                    "Analyze manifest and qualities",
                    IdentificationStepStatus.Skipped,
                    "No stream was selected."));
                StatusMessage = extraction.Message;
                return;
            }

            StatusMessage = "Media stream found; analyzing available qualities...";
            UpdateIdentificationStep(Step(
                IdentificationStepKeys.AnalyzeManifest,
                8,
                "Analyze manifest and qualities",
                IdentificationStepStatus.Identifying,
                $"Inspecting the selected {stream.Type} source."));

            foreach (var analyzer in _streamAnalyzers.Where(a => a.CanAnalyze(stream)))
            {
                stream = await analyzer.AnalyzeAsync(stream, _operationCts.Token);
            }

            UpdateIdentificationStep(Step(
                IdentificationStepKeys.AnalyzeManifest,
                8,
                "Analyze manifest and qualities",
                IdentificationStepStatus.Success,
                stream.Qualities.Count > 0
                    ? $"Found {stream.Qualities.Count} selectable quality option(s)."
                    : "The stream is ready for download."));

            _detectedStream = stream;
            OnPropertyChanged(nameof(CanDownload));
            RefreshWizardNavigation();
            DetectedStreamUrl = stream.Url.AbsoluteUri;
            RequestContextDisplay = BuildRequestContext(stream);
            AvailableQualities.Clear();
            foreach (var quality in stream.Qualities.DefaultIfEmpty(new StreamQuality { Label = "Auto", Url = stream.Url }))
            {
                AvailableQualities.Add(quality);
            }

            SelectedQuality = AvailableQualities.FirstOrDefault();
            StatusMessage = extraction.Message;
            WizardStepIndex = 2;
        }
        catch (OperationCanceledException)
        {
            CompleteActiveIdentificationSteps(
                IdentificationStepStatus.Skipped,
                "Analysis was cancelled.");
            StatusMessage = "Analysis cancelled.";
        }
        catch (Exception ex)
        {
            var currentManifestStep = IdentificationSteps.FirstOrDefault(
                item => item.Key == IdentificationStepKeys.AnalyzeManifest);
            if (currentManifestStep?.Status == IdentificationStepStatus.Identifying)
            {
                UpdateIdentificationStep(Step(
                    IdentificationStepKeys.AnalyzeManifest,
                    8,
                    "Analyze manifest and qualities",
                    IdentificationStepStatus.Failed,
                    "The selected stream could not be analyzed."));
            }

            CompleteActiveIdentificationSteps(
                IdentificationStepStatus.Failed,
                "This step stopped because analysis failed.");

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
        WizardStepIndex = 3;

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
            if (result.Succeeded)
            {
                ProgressPercentage = 100;
                ProgressPercentageDisplay = "100%";
                WizardStepIndex = 4;
            }
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

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _operationCts?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanConfigureDownload))]
    private void BrowseOutput()
    {
        // FileSavePicker requires window handle plumbing; keep path editable for this first vertical slice.
        OutputPath = CreateDefaultOutputPath();
        StatusMessage = "Output path reset to the default Videos filename. You can edit it directly.";
    }

    private void ResetDetection()
    {
        _detectedStream = null;
        WizardStepIndex = 0;
        DetectedStreamUrl = string.Empty;
        DownloadApiUrl = string.Empty;
        RequestContextDisplay = "Request context will appear after analysis.";
        AiDiagnostic = UseLocalAiAdvisor
            ? "Local AI will be used only if deterministic extraction finds no stream."
            : "Local AI diagnostics are off.";
        NetworkActivities.Clear();
        ResetIdentificationSteps();
        AvailableQualities.Clear();
        SelectedQuality = null;
        RefreshWizardNavigation();
        ProgressPercentage = 0;
        ProgressPercentageDisplay = "0%";
        DownloadedBytes = 0;
        DownloadedBytesDisplay = "Downloaded: 0 MB";
        DownloadSpeed = "Speed: --";
        ElapsedTime = "Elapsed: 00:00:00";
        EstimatedTime = "Time remaining: --";
    }

    private bool TryCreateExtractionOptions(out StreamExtractionOptions options)
    {
        options = new StreamExtractionOptions();
        if (!UseLocalAiAdvisor)
        {
            return true;
        }

        if (!Uri.TryCreate(LocalAiEndpoint, UriKind.Absolute, out var endpoint)
            || !endpoint.IsLoopback
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            StatusMessage = "The local AI endpoint must be a loopback HTTP address, such as http://localhost:11434.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(LocalAiModel))
        {
            StatusMessage = "Enter the locally installed Ollama model name.";
            return false;
        }

        options = new StreamExtractionOptions
        {
            EnableLocalAiAdvisor = true,
            LocalAiEndpoint = endpoint,
            LocalAiModel = LocalAiModel.Trim()
        };
        return true;
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

    private void ResetIdentificationSteps()
    {
        IdentificationSteps.Clear();
        IdentificationSteps.Add(Step(IdentificationStepKeys.OpenPage, 1, "Open video page"));
        IdentificationSteps.Add(Step(IdentificationStepKeys.MonitorNetwork, 2, "Monitor media network calls"));
        IdentificationSteps.Add(Step(IdentificationStepKeys.DiscoverSeeds, 3, "Collect player and embed seed links"));
        IdentificationSteps.Add(Step(IdentificationStepKeys.InspectPlayers, 4, "Inspect player APIs and video elements"));
        IdentificationSteps.Add(Step(IdentificationStepKeys.ProbeSeeds, 5, "Probe discovered seed links"));
        IdentificationSteps.Add(Step(IdentificationStepKeys.SelectCandidate, 6, "Validate and select download source"));
        IdentificationSteps.Add(Step(IdentificationStepKeys.AiFallback, 7, "Optional local AI fallback"));
        IdentificationSteps.Add(Step(IdentificationStepKeys.AnalyzeManifest, 8, "Analyze manifest and qualities"));
    }

    private void UpdateIdentificationStep(IdentificationStepUpdate update)
    {
        var existingIndex = IdentificationSteps
            .Select((item, index) => (item, index))
            .FirstOrDefault(pair => pair.item.Key == update.Key)
            .index;

        if (existingIndex >= 0
            && existingIndex < IdentificationSteps.Count
            && IdentificationSteps[existingIndex].Key == update.Key)
        {
            IdentificationSteps[existingIndex] = update;
            return;
        }

        var insertAt = IdentificationSteps.TakeWhile(item => item.Order < update.Order).Count();
        IdentificationSteps.Insert(insertAt, update);
    }

    private void CompleteActiveIdentificationSteps(IdentificationStepStatus status, string detail)
    {
        foreach (var active in IdentificationSteps
                     .Where(item => item.Status == IdentificationStepStatus.Identifying)
                     .ToArray())
        {
            UpdateIdentificationStep(Step(active.Key, active.Order, active.Title, status, detail));
        }
    }

    private void RefreshWizardNavigation()
    {
        OnPropertyChanged(nameof(CanGoToAnalyzeStep));
        OnPropertyChanged(nameof(CanGoToStreamStep));
        OnPropertyChanged(nameof(CanGoToDownloadStep));
        GoToAnalyzeStepCommand.NotifyCanExecuteChanged();
        GoToStreamStepCommand.NotifyCanExecuteChanged();
        GoToDownloadStepCommand.NotifyCanExecuteChanged();
    }

    private static IdentificationStepUpdate Step(
        string key,
        int order,
        string title,
        IdentificationStepStatus status = IdentificationStepStatus.Waiting,
        string detail = "Waiting to start.") => new()
        {
            Key = key,
            Order = order,
            Title = title,
            Status = status,
            Detail = detail
        };

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
