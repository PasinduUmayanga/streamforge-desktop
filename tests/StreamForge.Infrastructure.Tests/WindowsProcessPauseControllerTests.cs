using System.Diagnostics;
using StreamForge.Infrastructure.Ffmpeg;

namespace StreamForge.Infrastructure.Tests;

public sealed class WindowsProcessPauseControllerTests
{
    [Fact]
    public void SuspendAndResume_ControlOwnedChildProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c timeout /t 15 /nobreak >nul",
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Assert.NotNull(process);
        var controller = new WindowsProcessPauseController();

        try
        {
            Assert.True(controller.TrySuspend(process));
            Assert.True(controller.TryResume(process));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
    }
}
