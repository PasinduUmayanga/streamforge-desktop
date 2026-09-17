using System.Diagnostics;
using Microsoft.Extensions.Logging;
using StreamForge.Core.Interfaces;
using StreamForge.Core.Models;

namespace StreamForge.Infrastructure.Ffmpeg;

public sealed class FfmpegService : IFfmpegService
{
    private readonly IFfmpegLocator _locator;
    private readonly ILogger<FfmpegService> _logger;
    private readonly IProcessPauseController _pauseController;
    private readonly object _activeDownloadSync = new();
    private ActiveDownload? _activeDownload;

    public FfmpegService(IFfmpegLocator locator, ILogger<FfmpegService> logger)
        : this(locator, logger, new WindowsProcessPauseController())
    {
    }

    internal FfmpegService(
        IFfmpegLocator locator,
        ILogger<FfmpegService> logger,
        IProcessPauseController pauseController)
    {
        _locator = locator;
        _logger = logger;
        _pauseController = pauseController;
    }

    public bool IsPaused
    {
        get
        {
            lock (_activeDownloadSync)
            {
                return _activeDownload?.IsPaused == true;
            }
        }
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var ffmpegPath = _locator.Locate();
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return new DownloadResult
            {
                Succeeded = false,
                ErrorMessage = "FFmpeg was not found. Configure or install FFmpeg to continue."
            };
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in FfmpegArgumentBuilder.Build(request))
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var parser = new FfmpegProgressParser();

        try
        {
            if (!process.Start())
            {
                return new DownloadResult { Succeeded = false, ErrorMessage = "FFmpeg could not be started." };
            }

            if (!TrySetActiveDownload(process, parser))
            {
                await StopProcessAsync(process);
                return new DownloadResult
                {
                    Succeeded = false,
                    ErrorMessage = "Another FFmpeg download is already running."
                };
            }

            _logger.LogInformation("Download started.");

            var stdout = ReadProgressAsync(process, parser, progress, cancellationToken);
            var stderr = ReadDiagnosticsAsync(process, parser, cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);

            _logger.LogInformation("FFmpeg exit code {ExitCode}", process.ExitCode);
            return new DownloadResult
            {
                Succeeded = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                ErrorMessage = process.ExitCode == 0 ? null : "FFmpeg exited unexpectedly."
            };
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(process);
            _logger.LogInformation("Download cancelled.");
            return new DownloadResult { Succeeded = false, Cancelled = true, ErrorMessage = "Download cancelled." };
        }
        catch (Exception ex)
        {
            await StopProcessAsync(process);
            _logger.LogError(ex, "FFmpeg download failed.");
            return new DownloadResult { Succeeded = false, ErrorMessage = ex.Message };
        }
        finally
        {
            ClearActiveDownload(process);
        }
    }

    public bool TryPause()
    {
        lock (_activeDownloadSync)
        {
            var download = _activeDownload;
            if (download is null || download.IsPaused || HasExited(download.Process))
            {
                return false;
            }

            try
            {
                if (!_pauseController.TrySuspend(download.Process))
                {
                    return false;
                }

                download.Parser.Pause();
                download.IsPaused = true;
                _logger.LogInformation("Download paused.");
                return true;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "FFmpeg could not be paused.");
                return false;
            }
        }
    }

    public bool TryResume()
    {
        lock (_activeDownloadSync)
        {
            var download = _activeDownload;
            if (download is null || !download.IsPaused || HasExited(download.Process))
            {
                return false;
            }

            try
            {
                if (!_pauseController.TryResume(download.Process))
                {
                    return false;
                }

                download.Parser.Resume();
                download.IsPaused = false;
                _logger.LogInformation("Download resumed.");
                return true;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "FFmpeg could not be resumed.");
                return false;
            }
        }
    }

    private bool TrySetActiveDownload(Process process, FfmpegProgressParser parser)
    {
        lock (_activeDownloadSync)
        {
            if (_activeDownload is not null)
            {
                return false;
            }

            _activeDownload = new ActiveDownload(process, parser);
            return true;
        }
    }

    private void ClearActiveDownload(Process process)
    {
        lock (_activeDownloadSync)
        {
            if (_activeDownload?.Process == process)
            {
                _activeDownload = null;
            }
        }
    }

    private void ResumeBeforeStopping(Process process)
    {
        lock (_activeDownloadSync)
        {
            var download = _activeDownload;
            if (download?.Process != process || !download.IsPaused)
            {
                return;
            }

            try
            {
                _pauseController.TryResume(process);
                download.Parser.Resume();
                download.IsPaused = false;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Paused FFmpeg process could not be resumed before stopping.");
            }
        }
    }

    private static async Task ReadProgressAsync(
        Process process,
        FfmpegProgressParser parser,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            var parsed = parser.ParseLine(line);
            if (parsed is not null)
            {
                progress.Report(parsed);
            }
        }
    }

    private static async Task ReadDiagnosticsAsync(
        Process process,
        FfmpegProgressParser parser,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await process.StandardError.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            parser.ParseDiagnosticLine(line);
        }
    }

    private async Task StopProcessAsync(Process process)
    {
        ResumeBeforeStopping(process);
        if (HasExited(process))
        {
            return;
        }

        try
        {
            process.CloseMainWindow();
            await Task.Delay(1500);
            if (!HasExited(process))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            if (!HasExited(process))
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private sealed class ActiveDownload(Process process, FfmpegProgressParser parser)
    {
        public Process Process { get; } = process;

        public FfmpegProgressParser Parser { get; } = parser;

        public bool IsPaused { get; set; }
    }
}
