using System;

namespace MangaJaNaiConverterGui.Tests
{
    // DirectML and TensorRT-RTX exist on Windows only (libaji builds neither for Linux): tests of their
    // behaviour, and of the texts that name them, skip elsewhere.
    public static class WindowsOnly
    {
        public const string Reason = "DirectML and TensorRT-RTX are Windows-only";
        public static bool OnWindows => OperatingSystem.IsWindows();
    }
}
