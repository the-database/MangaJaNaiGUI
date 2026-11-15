using MangaJaNaiConverterGui.Services;
using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace MangaJaNaiConverterGui.Tests
{
    // The shared pack store: inference/aji-runtime.json names the model and runtime pack folders.
    public sealed class StoreFoldersTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"mjn-store-{Guid.NewGuid():N}");
        private string Inference => Path.Combine(_root, "app", "inference");
        private string State => Path.Combine(_root, "state");

        public StoreFoldersTests()
        {
            Directory.CreateDirectory(Inference);
            Directory.CreateDirectory(Path.Combine(State, "models"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private string Pack(string name, params string[] files)
        {
            string dir = Path.Combine(_root, "store", "packs", name, "1");
            Directory.CreateDirectory(dir);
            foreach (var f in files)
            {
                File.WriteAllText(Path.Combine(dir, f), "x");
            }
            return dir;
        }

        private void WriteRuntimeJson(object json) =>
            File.WriteAllText(Path.Combine(Inference, "aji-runtime.json"), JsonSerializer.Serialize(json));

        private BackendService Service() => new(new FakeUpdateManagerService(), Inference, State);

        [Fact]
        public void Without_aji_runtime_json_only_the_install_folders_count()
        {
            var s = Service();
            Assert.Empty(s.StoreModelsDirectories);
            Assert.Equal([s.UserModelsDirectory, s.BundledModelsDirectory], s.ModelsDirectories);
            Assert.False(s.AreModelsInstalled);
            Assert.False(s.IsTensorRtRuntimeInstalled);
        }

        [Fact]
        public void Store_model_packs_are_searched_after_the_users_models()
        {
            string manga = Pack("models-mangajanai", "2x_MangaJaNai_1200p_V1_ESRGAN_70k_fp16.onnx", "shared.onnx");
            File.WriteAllText(Path.Combine(State, "models", "shared.onnx"), "mine");
            WriteRuntimeJson(new { format = 1, models = new[] { manga } });
            var s = Service();
            Assert.Equal([s.UserModelsDirectory, manga, s.BundledModelsDirectory], s.ModelsDirectories);
            Assert.True(s.AreModelsInstalled);
            Assert.Contains("2x_MangaJaNai_1200p_V1_ESRGAN_70k_fp16.onnx", s.AllModels);
            Assert.Single(s.AllModels, m => m == "shared.onnx");
            Assert.Equal(Path.Combine(manga, "2x_MangaJaNai_1200p_V1_ESRGAN_70k_fp16.onnx"),
                s.ResolveModelPath("2x_MangaJaNai_1200p_V1_ESRGAN_70k_fp16.onnx"));
            // the user's own file of the same name wins
            Assert.Equal(Path.Combine(State, "models", "shared.onnx"), s.ResolveModelPath("shared.onnx"));
        }

        [Fact]
        public void The_tensorrt_runtime_can_be_in_the_store()
        {
            string trt = Pack("trt-runtime", BackendService.Exe("trtexec"), OperatingSystem.IsWindows() ? "cudart64_13.dll" : "libcudart.so.13");
            WriteRuntimeJson(new { format = 1, trt });
            Assert.True(Service().IsTensorRtRuntimeInstalled);
        }

        // A store inside a portable copy (relative mode): aji-runtime.json names the folders relative to
        // its own folder, so the copy can be moved.
        [Fact]
        public void Relative_folders_are_relative_to_aji_runtime_json()
        {
            string inside = Path.Combine(_root, "app", "runtime", "packs");
            string trt = Path.Combine(inside, "trt-runtime", "1");
            string models = Path.Combine(inside, "models-mangajanai", "1");
            Directory.CreateDirectory(trt);
            Directory.CreateDirectory(models);
            File.WriteAllText(Path.Combine(trt, BackendService.Exe("trtexec")), "x");
            File.WriteAllText(Path.Combine(trt, OperatingSystem.IsWindows() ? "cudart64_13.dll" : "libcudart.so.13"), "x");
            File.WriteAllText(Path.Combine(models, "a.onnx"), "x");
            var sep = Path.DirectorySeparatorChar;
            WriteRuntimeJson(new
            {
                format = 1,
                trt = $"..{sep}runtime{sep}packs{sep}trt-runtime{sep}1",
                models = new[] { $"..{sep}runtime{sep}packs{sep}models-mangajanai{sep}1" },
            });
            var s = Service();
            Assert.True(s.IsTensorRtRuntimeInstalled);
            Assert.Equal([models], s.StoreModelsDirectories);
            Assert.Contains("a.onnx", s.AllModels);
        }

        [Fact]
        public void A_store_pack_folder_that_is_gone_does_not_count()
        {
            WriteRuntimeJson(new { format = 1, trt = Path.Combine(_root, "gone") });
            Assert.False(Service().IsTensorRtRuntimeInstalled);
        }

        [Fact]
        public void An_unreadable_aji_runtime_json_is_ignored()
        {
            File.WriteAllText(Path.Combine(Inference, "aji-runtime.json"), "{ not json");
            var s = Service();
            Assert.Empty(s.StoreModelsDirectories);
            Assert.False(s.IsTensorRtRuntimeInstalled);
        }
    }
}
