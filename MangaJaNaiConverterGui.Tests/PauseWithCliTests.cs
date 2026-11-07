using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Collections;
using MangaJaNaiConverterGui.Services;
using MangaJaNaiConverterGui.ViewModels;
using Newtonsoft.Json;
using Xunit;

namespace MangaJaNaiConverterGui.Tests
{
    // A backend that is a real mangajanai_upscale build, for running it through the view model.
    public sealed class CliBackendService(string exe, string aji, string state) : IBackendService
    {
        public string AppStateFolder => state;
        public string AppStateFilename => "appstate2.json";
        public string AppStatePath => Path.Combine(state, AppStateFilename);
        public string LogsDirectory => state;
        public string InferenceDirectory => aji;
        public string BackendExePath => exe;
        public bool IsBackendInstalled => true;
        public bool IsTensorRtRtxInstalled => false;
        public bool IsTensorRtRuntimeInstalled => false;
        public string UserModelsDirectory => state;
        public IReadOnlyList<string> StoreModelsDirectories => [];
        public string BundledModelsDirectory => state;
        public IReadOnlyList<string> ModelsDirectories => [state];
        public bool AreModelsInstalled => true;
        public string EngineCacheDirectory => Path.Combine(state, "engines");
        public AvaloniaList<string> AllModels { get; } = [];
        public bool RefreshModels() => false;
        public string? ResolveModelPath(string? model) => null;
    }

    public class PauseWithCliTests
    {
        // Pause and Resume through the view model against a real CLI (MANGAJANAI_CLI_EXE: a build with
        // --control stdin; MANGAJANAI_AJI_DIR: a folder with libaji). A no-model chain resizing many
        // pages: paused, nothing more is written until Resume, and the run then completes.
        [Fact]
        public async Task PauseStopsTheRunAndResumeFinishesIt()
        {
            var exe = Environment.GetEnvironmentVariable("MANGAJANAI_CLI_EXE");
            var aji = Environment.GetEnvironmentVariable("MANGAJANAI_AJI_DIR");
            Assert.SkipUnless(File.Exists(exe) && Directory.Exists(aji), "set MANGAJANAI_CLI_EXE and MANGAJANAI_AJI_DIR");
            var dir = Path.Combine(Path.GetTempPath(), "mangajanai-pause-cli-" + Guid.NewGuid().ToString("N"));
            try
            {
                var input = Directory.CreateDirectory(Path.Combine(dir, "in")).FullName;
                var output = Directory.CreateDirectory(Path.Combine(dir, "out")).FullName;
                var state = Directory.CreateDirectory(Path.Combine(dir, "state")).FullName;
                var page = new byte[2000 * 3000];
                new Random(1).NextBytes(page);
                var png = Png(2000, 3000, page);
                for (var i = 0; i < 60; i++)
                {
                    File.WriteAllBytes(Path.Combine(input, $"p{i:000}.png"), png);
                }
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
                                Chains = new { values = new[] { new { MinResolution = "0x0", MaxResolution = "0x0", IsGrayscale = true, IsColor = true, ModelFilePath = "No Model" } } },
                            },
                        },
                    },
                };
                var json = JsonConvert.SerializeObject(settings).Replace("\"values\"", "\"$values\"");
                File.WriteAllText(Path.Combine(state, "appstate2.json"), json);

                var vm = new MainWindowViewModel(new CliBackendService(exe!, aji!, state), new FakeUpdateManagerService()) { Upscaling = true };
                var run = vm.RunCommand(["--settings", Path.Combine(state, "appstate2.json"), "--aji-dir", aji!, "--cache-dir", Path.Combine(state, "engines"), "--control", "stdin"]);

                await Until(() => Directory.GetFiles(output, "*.png").Length >= 3, 60);
                vm.TogglePause();
                Assert.True(vm.Paused);
                await Until(() => vm.PauseReleased, 30);
                await Task.Delay(1000); // pages already past the model finish writing
                var atPause = Directory.GetFiles(output, "*.png").Length;
                await Task.Delay(2000);
                Assert.Equal(atPause, Directory.GetFiles(output, "*.png").Length);
                Assert.True(atPause < 60, "paused before the end");

                vm.TogglePause();
                Assert.False(vm.Paused || vm.PauseReleased);
                Assert.Equal(0, await run);
                Assert.Equal(60, Directory.GetFiles(output, "*.png").Length);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static async Task Until(Func<bool> ok, int seconds)
        {
            for (var i = 0; i < seconds * 10 && !ok(); i++)
            {
                await Task.Delay(100);
            }
            Assert.True(ok(), "timed out");
        }

        private static uint Crc(byte[] bytes)
        {
            var c = 0xFFFFFFFFu;
            foreach (var b in bytes)
            {
                c ^= b;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
            }
            return ~c;
        }

        // An 8-bit gray PNG (quick to write and decode).
        private static byte[] Png(int w, int h, byte[] gray)
        {
            using var raw = new MemoryStream();
            for (var y = 0; y < h; y++)
            {
                raw.WriteByte(0);
                raw.Write(gray, y * w, w);
            }
            using var z = new MemoryStream();
            using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Fastest, true))
            {
                raw.Position = 0;
                raw.CopyTo(zs);
            }
            using var png = new MemoryStream();
            png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            void Chunk(string type, byte[] data)
            {
                var t = System.Text.Encoding.ASCII.GetBytes(type);
                png.Write(BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(data.Length)));
                png.Write(t);
                png.Write(data);
                png.Write(BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(Crc([.. t, .. data]))));
            }
            var ihdr = new byte[13];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), w);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
            ihdr[8] = 8;
            Chunk("IHDR", ihdr);
            Chunk("IDAT", z.ToArray());
            Chunk("IEND", []);
            return png.ToArray();
        }
    }
}
