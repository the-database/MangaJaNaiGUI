using MangaJaNaiConverterGui.Services;
using MangaJaNaiConverterGui.ViewModels;
using System.Linq;
using Xunit;

namespace MangaJaNaiConverterGui.Tests
{
    // NVIDIA GPUs older than Turing (compute capability below 7.5: GTX 10 series, Volta) can't run
    // the shipped TensorRT or TensorRT-RTX, so they count as no NVIDIA GPU there and get DirectML
    // (the-database/VideoJaNai#70). The GPU lists are injected; no such card is needed.
    public class PreTuringGpuTests
    {
        const string Gtx1080Name = "NVIDIA GeForce GTX 1080";
        static readonly BackendDevice Gtx1080 = new() { Id = "65792", Name = Gtx1080Name, ComputeCapability = "6.1" };
        static readonly BackendDevice Rtx3080 = new() { Id = "131328", Name = "NVIDIA GeForce RTX 3080", ComputeCapability = "8.6" };

        const string Gtx1080Message =
            "TensorRT needs an NVIDIA GeForce RTX 20 / GTX 16 series or newer GPU (compute capability 7.5+), " +
            "and NVIDIA GeForce GTX 1080 (compute 6.1) is older. Choose DirectML in App Settings, which runs on this GPU.";

        [Theory]
        [InlineData("5.2", false)]
        [InlineData("6.1", false)]
        [InlineData("7.0", false)] // Volta
        [InlineData("7.2", false)]
        [InlineData("7.5", true)]  // Turing: RTX 20 / GTX 16
        [InlineData("8.6", true)]
        [InlineData("12.0", true)]
        [InlineData("13.0", true)] // newer than the builder packs: the PTX fallback
        [InlineData(null, true)]   // unknown (an older backend): the backend decides
        [InlineData("junk", true)]
        public void TensorRtNeedsComputeCapability75(string? cc, bool capable) =>
            Assert.Equal(capable, GpuSupport.TensorRtCapable(cc));

        [Fact]
        public void TheThresholdIsTuring() => Assert.Equal((7, 5), GpuSupport.TrtMinComputeCapability);

        // The updater's view (NVML): an old GPU alone is no NVIDIA GPU; next to a new one it's left out.
        [Fact]
        public void AnOldGpuAloneIsNoNvidiaGpu()
        {
            var s = GpuSupport.Summarize([new(Gtx1080Name, 6, 1)]);
            Assert.Equal((false, "", "", "NVIDIA GeForce GTX 1080 (compute 6.1)"), s);
        }

        [Fact]
        public void AnOldGpuNextToANewOneIsLeftOut()
        {
            var s = GpuSupport.Summarize([new(Gtx1080Name, 6, 1), new("NVIDIA GeForce RTX 3080", 8, 6), new("Tesla V100", 7, 0)]);
            Assert.Equal((true, "sm86", "NVIDIA GeForce RTX 3080", "NVIDIA GeForce GTX 1080 (compute 6.1) + Tesla V100 (compute 7.0)"), s);
        }

        [Fact]
        public void NewGpusAreAllReported()
        {
            var s = GpuSupport.Summarize([new("NVIDIA GeForce RTX 5090", 12, 0), new("NVIDIA GeForce RTX 2060", 7, 5), new("NVIDIA GeForce RTX 5080", 12, 0)]);
            Assert.Equal((true, "sm120, sm75", "NVIDIA GeForce RTX 5090 + NVIDIA GeForce RTX 2060 + NVIDIA GeForce RTX 5080", ""), s);
            Assert.Equal((false, "", "", ""), GpuSupport.Summarize([]));
        }

        // The PTX fallback is for generations newer than the packs, which the old ones never reach.
        [Fact]
        public void BuilderPacksFallBackToPtxForNewGenerationsOnly()
        {
            string[] published = ["trt-runtime", "trt-sm75", "trt-sm86", "trt-sm120", "trt-ptx"];
            Assert.Equal(["trt-sm86", "trt-ptx"], GpuSupport.BuilderPacks(published, "sm86, sm130"));
            Assert.Equal(["trt-sm120", "trt-sm75"], GpuSupport.BuilderPacks(published, "sm120, sm75, sm120"));
            var (_, sm, _, _) = GpuSupport.Summarize([new(Gtx1080Name, 6, 1)]);
            Assert.Empty(GpuSupport.BuilderPacks(published, sm));
        }

        // The app's view (--list-devices): TensorRT and TensorRT-RTX lists lose the old GPUs.
        [Theory]
        [InlineData("tensorrt")]
        [InlineData("tensorrt-rtx")]
        public void TensorRtDeviceListsDropOldGpus(string backend)
        {
            var (usable, tooOld) = UpscaleBackends.SplitDevices(backend, [Gtx1080, Rtx3080]);
            Assert.Equal([Rtx3080], usable);
            Assert.Equal([Gtx1080], tooOld);
        }

        [Fact]
        public void DirectMlKeepsEveryGpu()
        {
            var dml = new BackendDevice { Id = "65792", Name = Gtx1080Name };
            var (usable, tooOld) = UpscaleBackends.SplitDevices(UpscaleBackends.CliDirectMl, [dml, Rtx3080]);
            Assert.Equal([dml, Rtx3080], usable);
            Assert.Empty(tooOld);
        }

        [Theory]
        [InlineData("TensorRT", true, Gtx1080Message)]
        [InlineData("TensorRT-RTX", true, Gtx1080Message)]
        [InlineData("Auto", true, null)]   // Windows: DirectML takes over
        [InlineData("DirectML", true, null)]
        [InlineData("Auto", false,
            "TensorRT needs an NVIDIA GeForce RTX 20 / GTX 16 series or newer GPU (compute capability 7.5+), " +
            "and NVIDIA GeForce GTX 1080 (compute 6.1) is older. On Linux, MangaJaNai GUI runs on TensorRT only, so it can't upscale on this GPU.")]
        public void ForcedTensorRtOnAnOldGpuSaysWhy(string choice, bool windows, string? message) =>
            Assert.Equal(message, UpscaleBackends.TooOldGpuMessage(choice, [Gtx1080], windows));

        [Fact]
        public void NoOldGpuNoMessage() => Assert.Null(UpscaleBackends.TooOldGpuMessage("TensorRT", [], true));

        [Theory]
        [InlineData(false, "", "", "", true, "No NVIDIA GPU detected — using the built-in DirectML engine.")]
        [InlineData(false, "", "", "NVIDIA GeForce GTX 1080 (compute 6.1)", true,
            "NVIDIA GeForce GTX 1080 (compute 6.1) is too old for TensorRT (it needs an RTX 20 / GTX 16 series or newer) — using the built-in DirectML engine.")]
        [InlineData(false, "", "", "NVIDIA GeForce GTX 1080 (compute 6.1)", false,
            "NVIDIA GeForce GTX 1080 (compute 6.1) is too old for TensorRT (it needs an RTX 20 / GTX 16 series or newer). On Linux, MangaJaNai GUI needs one (TensorRT).")]
        [InlineData(true, "NVIDIA GeForce RTX 3080", "sm86", "NVIDIA GeForce GTX 1080 (compute 6.1)", true,
            "NVIDIA GeForce RTX 3080 (sm86). NVIDIA GeForce GTX 1080 (compute 6.1) is too old for TensorRT (it needs an RTX 20 / GTX 16 series or newer), so TensorRT leaves it out.")]
        [InlineData(true, "NVIDIA GeForce RTX 3080", "sm86", "", true, "NVIDIA GeForce RTX 3080 (sm86)")]
        public void AppSettingsSaysWhichGpuTensorRtUses(bool nvidia, string name, string sm, string unsupported, bool windows, string text) =>
            Assert.Equal(text, MainWindowViewModel.GpuStatus(nvidia, name, sm, unsupported, windows));

        static MainWindowViewModel Vm(string choice) => new(new FakeBackendService([])) { UpscaleBackend = choice };

        // TensorRT chosen on a PC whose only NVIDIA GPU is old: no device, and the plain reason.
        [Fact]
        public void ChosenTensorRtOnAnOldGpuListsNoDeviceAndSaysWhy()
        {
            var vm = Vm(UpscaleBackends.TensorRt);
            vm.UseDeviceList(UpscaleBackends.CliTensorRt, [Gtx1080]);
            Assert.Empty(vm.DeviceList);
            Assert.Equal(Gtx1080Message, vm.DeviceStatusText);
        }

        // TensorRT-RTX chosen (its component missing) on an old GPU: the GPU is the reason, not the component.
        [Fact]
        public void ChosenTensorRtRtxOnAnOldGpuSaysWhyBeforeAskingForComponents()
        {
            var vm = Vm(UpscaleBackends.TensorRtRtx);
            vm.UseDeviceList(UpscaleBackends.CliTensorRt, [], [Gtx1080]);
            Assert.Equal(Gtx1080Message, vm.DeviceStatusText);
        }

        // A mixed PC: TensorRT runs on the new GPU only.
        [Fact]
        public void ChosenTensorRtOnAMixedPcUsesTheNewGpu()
        {
            var vm = Vm(UpscaleBackends.TensorRt);
            vm.UseDeviceList(UpscaleBackends.CliTensorRt, [Gtx1080, Rtx3080]);
            Assert.Equal(["NVIDIA GeForce RTX 3080"], vm.DeviceList);
            Assert.Equal("Using TensorRT.", vm.DeviceStatusText);
        }

        // Auto went on to DirectML for the old GPU (RefreshDevices passes what TensorRT dropped).
        [Fact]
        public void AutoOnAnOldGpuUsesDirectMl()
        {
            var vm = Vm(UpscaleBackends.Auto);
            var dml = new BackendDevice { Id = "65792", Name = Gtx1080Name };
            vm.UseDeviceList(UpscaleBackends.CliDirectMl, [dml], [Gtx1080]);
            Assert.Equal([Gtx1080Name], vm.DeviceList);
            Assert.Equal("Using DirectML.", vm.DeviceStatusText);
            Assert.Null(vm.BackendSetupHint); // no "TensorRT is much faster" for a GPU it can't run on
        }
    }
}
