using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MangaJaNaiConverterGui.Services
{
    // Pausing a running upscale: the backend process is frozen where it is (its GPU work finishes
    // the step in flight) and continues from there. It keeps its VRAM while paused.
    public static class ProcessControl
    {
        public static bool Suspend(Process process) => Signal(process, suspend: true);

        public static bool Resume(Process process) => Signal(process, suspend: false);

        private static bool Signal(Process process, bool suspend)
        {
            try
            {
                if (process.HasExited)
                {
                    return false;
                }
                if (OperatingSystem.IsWindows())
                {
                    return (suspend ? NtSuspendProcess(process.Handle) : NtResumeProcess(process.Handle)) == 0;
                }
                if (OperatingSystem.IsLinux())
                {
                    return kill(process.Id, suspend ? SIGSTOP : SIGCONT) == 0;
                }
            }
            catch
            {
            }
            return false;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll")]
        private static extern int NtResumeProcess(IntPtr processHandle);

        // Linux x86-64 and arm64 signal numbers.
        private const int SIGCONT = 18;
        private const int SIGSTOP = 19;

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int sig);
    }
}
