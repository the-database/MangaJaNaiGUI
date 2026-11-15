using System;
using System.IO;
using System.Threading.Tasks;
using MangaJaNaiConverterGui.ViewModels;
using Newtonsoft.Json;
using Xunit;

namespace MangaJaNaiConverterGui.Tests
{
    [Collection("Real CLI")]
    public class NativeKernelsWithCliTests
    {
        // The status bar's native-kernel status through the view model against a real CLI
        // (MANGAJANAI_CLI_EXE: a build that prints NATIVE lines; MANGAJANAI_AJI_DIR: a folder with
        // libaji and its TensorRT runtime; MANGAJANAI_NATIVE_MODEL: an ONNX model libaji runs on
        // native kernels, like 2x_IllustrationJaNai_V3denoise_FDAT_M_unshuffle_30k_fp16.onnx). With
        // the setting on the model runs on them; turned off, the status says so.
        [Theory]
        [InlineData(true, "Native kernels")]
        [InlineData(false, "Native kernels off (App Settings)")]
        public async Task TheStatusBarSaysWhetherTheModelRanOnNativeKernels(bool useNative, string expected)
        {
            var exe = Environment.GetEnvironmentVariable("MANGAJANAI_CLI_EXE");
            var aji = Environment.GetEnvironmentVariable("MANGAJANAI_AJI_DIR");
            var model = Environment.GetEnvironmentVariable("MANGAJANAI_NATIVE_MODEL");
            Assert.SkipUnless(File.Exists(exe) && Directory.Exists(aji) && File.Exists(model),
                "set MANGAJANAI_CLI_EXE, MANGAJANAI_AJI_DIR and MANGAJANAI_NATIVE_MODEL");
            var dir = Path.Combine(Path.GetTempPath(), "mangajanai-native-cli-" + Guid.NewGuid().ToString("N"));
            try
            {
                var input = Directory.CreateDirectory(Path.Combine(dir, "in")).FullName;
                var output = Directory.CreateDirectory(Path.Combine(dir, "out")).FullName;
                var state = Directory.CreateDirectory(Path.Combine(dir, "state")).FullName;
                var page = new byte[256 * 256];
                new Random(1).NextBytes(page);
                File.WriteAllBytes(Path.Combine(input, "p.png"), PauseWithCliTests.Png(256, 256, page));
                var settings = new
                {
                    SelectedWorkflowIndex = 0,
                    Workflows = new
                    {
                        values = new[]
                        {
                            new
                            {
                                SelectedTabIndex = 1, InputFolderPath = input, OutputFolderPath = output, OutputFilename = "%filename%",
                                UpscaleImages = true, OverwriteExistingFiles = true, PngSelected = true, WebpSelected = false,
                                ModeScaleSelected = true, UpscaleScaleFactor = 2,
                                Chains = new { values = new[] { new { MinResolution = "0x0", MaxResolution = "0x0", IsGrayscale = true, IsColor = true, ModelFilePath = model } } },
                            },
                        },
                    },
                };
                var json = JsonConvert.SerializeObject(settings).Replace("\"values\"", "\"$values\"");
                File.WriteAllText(Path.Combine(state, "appstate2.json"), json);

                var vm = new MainWindowViewModel(new CliBackendService(exe!, aji!, state), new FakeUpdateManagerService())
                {
                    Upscaling = true,
                    UseNativeKernels = useNative,
                };
                var exit = await vm.RunCommand(["--settings", Path.Combine(state, "appstate2.json"), "--aji-dir", aji!, "--cache-dir", Path.Combine(state, "engines")]);

                Assert.Equal(0, exit);
                Assert.Single(Directory.GetFiles(output, "*.png"));
                Assert.Equal(expected, vm.NativeStatusText);
                Assert.True(vm.ShowNativeStatus);
                Assert.Equal(useNative, vm.NativeStatusAllNative);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
