using System;
using System.IO;
using System.Linq;
using MangaJaNaiConverterGui.Services;
using Xunit;

namespace MangaJaNaiConverterGui.Tests
{
    public class LegacyCleanupTests
    {
        private static string Folder()
        {
            var dir = Path.Combine(Path.GetTempPath(), "mangajanai-legacy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "models"));
            return dir;
        }

        private static void Write(string path, int bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes]);
        }

        [Theory]
        [InlineData("2x_MangaJaNai_1200p_V1_ESRGAN_70k.pth", true)]
        [InlineData("4x_IllustrationJaNai_V3denoise_FDAT_M_47k_fp16.safetensors", true)]
        [InlineData("4X_MANGAJANAI_2048P_V1_ESRGAN_70K.PTH", true)]
        [InlineData("2x_MangaJaNai_1200p_V1_ESRGAN_70k_fp16.onnx", false)] // 2.x's own
        [InlineData("4x_MyOwnModel.pth", false)]
        [InlineData("x_MangaJaNai_1200p.pth", false)]
        [InlineData("MangaJaNai_1200p.pth", false)]
        public void OnlyOneXsDefaultModelsCount(string name, bool legacy)
        {
            Assert.Equal(legacy, LegacyCleanup.IsLegacyDefaultModel(name));
        }

        [Fact]
        public void RemovesThePythonBackendAndTheDefaultModels()
        {
            var dir = Folder();
            try
            {
                Write(Path.Combine(dir, "python", "Version.txt"), 5);
                Write(Path.Combine(dir, "python", "python", "python.exe"), 1000);
                Write(Path.Combine(dir, "models", "2x_MangaJaNai_1200p_V1_ESRGAN_70k.pth"), 300);
                Write(Path.Combine(dir, "models", "4x_IllustrationJaNai_V3denoise_FDAT_M_47k_fp16.safetensors"), 200);
                Write(Path.Combine(dir, "models", "4x_MyOwnModel.pth"), 50);
                Write(Path.Combine(dir, "models", "4x_MangaJaNai_1200p_V1_ESRGAN_70k_fp16.onnx"), 70);
                Write(Path.Combine(dir, "appstate2.json"), 10);

                Assert.Equal(1505, LegacyCleanup.Run(dir));
                Assert.False(Directory.Exists(Path.Combine(dir, "python")));
                Assert.Equal(["4x_MangaJaNai_1200p_V1_ESRGAN_70k_fp16.onnx", "4x_MyOwnModel.pth"],
                    Directory.EnumerateFiles(Path.Combine(dir, "models")).Select(f => Path.GetFileName(f)!).Order().ToArray());
                Assert.True(File.Exists(Path.Combine(dir, "appstate2.json")), "settings stay");

                Assert.Equal(0, LegacyCleanup.Run(dir));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        // As in the installer: without 1.x's Python backend the models folder is never touched, so a
        // 2.x user's own copy of a 1.x model stays.
        [Fact]
        public void WithoutThePythonBackendNothingIsRemoved()
        {
            var dir = Folder();
            try
            {
                Write(Path.Combine(dir, "models", "2x_MangaJaNai_1200p_V1_ESRGAN_70k.pth"), 300);
                Assert.Equal(0, LegacyCleanup.Run(dir));
                Assert.True(File.Exists(Path.Combine(dir, "models", "2x_MangaJaNai_1200p_V1_ESRGAN_70k.pth")));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
