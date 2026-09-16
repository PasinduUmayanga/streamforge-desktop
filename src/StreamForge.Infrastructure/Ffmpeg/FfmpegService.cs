using System.Diagnostics;
using Microsoft.Extensions.Logging;
using StreamForge.Core.Interfaces;
using StreamForge.Core.Models;

namespace StreamForge.Infrastructure.Ffmpeg;

public sealed class FfmpegService(IFfmpegLocator locator, ILogger<FfmpegService> logger) : IFfmpegService
{
    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var ffmpegPath = locator.Locate();
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

            logger.LogInformation("Download started.");

            var stdout = ReadProgressAsync(process, parser, progress, cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            await stdout;
            _ = await stderr;

            logger.LogInformation("FFmpeg exit code {ExitCode}", process.ExitCode);
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
            logger.LogInformation("Download cancelled.");
            return new DownloadResult { Succeeded = false, Cancelled = true, ErrorMessage = "Download cancelled." };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FFmpeg download failed.");
            return new DownloadResult { Succeeded = false, ErrorMessage = ex.Message };
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

    private static async Task StopProcessAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.CloseMainWindow();
            await Task.Delay(1500);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}
