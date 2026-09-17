using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StreamForge.Infrastructure.Ffmpeg;

internal interface IProcessPauseController
{
    bool TrySuspend(Process process);

    bool TryResume(Process process);
}

internal sealed class WindowsProcessPauseController : IProcessPauseController
{
    public bool TrySuspend(Process process)
    {
        return OperatingSystem.IsWindows()
            && NativeMethods.NtSuspendProcess(process.Handle) == 0;
    }

    public bool TryResume(Process process)
    {
        return OperatingSystem.IsWindows()
            && NativeMethods.NtResumeProcess(process.Handle) == 0;
    }

    private static class NativeMethods
    {
        [DllImport("ntdll.dll")]
        internal static extern int NtSuspendProcess(nint processHandle);

        [DllImport("ntdll.dll")]
        internal static extern int NtResumeProcess(nint processHandle);
    }
}
