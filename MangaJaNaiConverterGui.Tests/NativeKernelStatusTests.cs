using MangaJaNaiConverterGui.Services;
using Xunit;

namespace MangaJaNaiConverterGui.Tests
{
    // The status bar's native-kernel status, from mangajanai_upscale's NATIVE / NATIVE_UNUSED /
    // NATIVE_OFF lines.
    public class NativeKernelStatusTests
    {
        static NativeKernelStatus Run(bool offInSettings, params string[] lines)
        {
            var status = new NativeKernelStatus();
            status.Reset(offInSettings);
            foreach (var line in lines)
            {
                status.Take(line);
            }
            return status;
        }

        [Fact]
        public void NothingIsShownUntilTheBackendReports()
        {
            var status = Run(false);
            Assert.Equal("", status.Text);
            Assert.Equal("", status.Tooltip);
        }

        [Fact]
        public void EveryModelOnNativeKernels()
        {
            var status = Run(false,
                "NATIVE 2x_IllustrationJaNai_V3denoise_FDAT_M_unshuffle_30k_fp16: FDAT 2x, 4 groups x 3 blocks, 96 channels, 4 heads, 8x8 windows (shifted)",
                "NATIVE 2x_MangaJaNai_1200p_V1_ESRGAN_70k_fp16: ESRGAN 2x, 23 RRDB, 64 features, growth 32");
            Assert.Equal("Native kernels", status.Text);
            Assert.True(status.AllNative);
            Assert.Equal(
                "2x_IllustrationJaNai_V3denoise_FDAT_M_unshuffle_30k_fp16: native kernels (FDAT 2x, 4 groups x 3 blocks, 96 channels, 4 heads, 8x8 windows (shifted))\n" +
                "2x_MangaJaNai_1200p_V1_ESRGAN_70k_fp16: native kernels (ESRGAN 2x, 23 RRDB, 64 features, growth 32)",
                status.Tooltip);
        }

        [Fact]
        public void SomeModelsOnNativeKernels()
        {
            var status = Run(false,
                "NATIVE 2x_A: ESRGAN 2x",
                "NATIVE_UNUSED 4x_B: no kernel for op GridSample",
                "NATIVE_UNUSED 4x_C: not 3-channel");
            Assert.Equal("Native kernels for 1 of 3 models — 4x_B: no kernel for op GridSample", status.Text);
            Assert.False(status.AllNative);
            Assert.Equal(
                "2x_A: native kernels (ESRGAN 2x)\n" +
                "4x_B: not on native kernels (no kernel for op GridSample)\n" +
                "4x_C: not on native kernels (not 3-channel)",
                status.Tooltip);
        }

        [Fact]
        public void NoModelOnNativeKernels()
        {
            var status = Run(false, "NATIVE_UNUSED 4x_B: out of memory for the tile buffers");
            Assert.Equal("Native kernels not used — 4x_B: out of memory for the tile buffers", status.Text);
            Assert.False(status.AllNative);
        }

        // Turned off in App Settings (the GUI set AJI_NATIVE=off), or by an AJI_NATIVE of the user's own.
        [Fact]
        public void NativeKernelsOff()
        {
            var settings = Run(true, "NATIVE_OFF");
            Assert.Equal("Native kernels off (App Settings)", settings.Text);
            Assert.False(settings.AllNative);
            Assert.Contains("App Settings", settings.Tooltip);
            Assert.Equal("Native kernels off (AJI_NATIVE)", Run(false, "NATIVE_OFF").Text);
        }

        // DirectML has no native kernels: the backend says nothing about them, and nor does the GUI.
        [Fact]
        public void DirectMlShowsNothingEvenWithNativeKernelsOff() =>
            Assert.Equal("", Run(true, "Upscaling with DirectML on NVIDIA GeForce RTX 5090").Text);

        [Fact]
        public void OnlyTheNativeLinesAreTaken()
        {
            var status = new NativeKernelStatus();
            Assert.True(status.Take("NATIVE m: ESRGAN 2x"));
            Assert.True(status.Take("NATIVE_UNUSED n: why"));
            Assert.True(status.Take("NATIVE_OFF"));
            Assert.False(status.Take("NATIVE"));
            Assert.False(status.Take("ENGINE_BUILD m.onnx (tile 512): building the TensorRT engine for this GPU; this takes a few minutes, once"));
            Assert.False(status.Take("BACKEND_FALLBACK TensorRT-RTX failed (x); using TensorRT instead"));
            Assert.False(status.Take("[aji] native kernels: m (ESRGAN 2x)"));
        }

        // A model is counted once, a line without a detail still counts, and a new run starts over.
        [Fact]
        public void ModelsCountOnceAndANewRunStartsOver()
        {
            var status = Run(false, "NATIVE m", "NATIVE m: ESRGAN 2x", "NATIVE_UNUSED m: late");
            Assert.Equal("Native kernels", status.Text);
            Assert.Equal("m: native kernels", status.Tooltip);
            status.Reset(offInSettings: false);
            Assert.Equal("", status.Text);
        }
    }
}
