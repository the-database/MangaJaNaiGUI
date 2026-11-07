using MangaJaNaiConverterGui.Drivers;
using MangaJaNaiConverterGui.ViewModels;
using Newtonsoft.Json;
using Xunit;

namespace MangaJaNaiConverterGui.Tests
{
    public class OutputFormatTests
    {
        [Fact]
        public void JpegXlIsExclusiveAndOffersLosslessAndQuality()
        {
            var w = new UpscaleWorkflow();
            w.SetJxlSelected();

            Assert.True(w.JxlSelected);
            Assert.False(w.WebpSelected || w.PngSelected || w.JpegSelected || w.AvifSelected);
            Assert.Equal("jxl", w.ImageFormat);
            Assert.True(w.ShowUseLosslessCompression);
            Assert.True(w.ShowLossyCompressionQuality);

            w.UseLosslessCompression = true;
            Assert.False(w.ShowLossyCompressionQuality);

            w.SetAvifSelected();
            Assert.False(w.JxlSelected);
            Assert.Equal("avif", w.ImageFormat);
            Assert.False(w.ShowUseLosslessCompression);
        }

        // The CLI picks the format from these flags in the saved state, so JxlSelected must round-trip;
        // a workflow saved before it existed reads as not JPEG XL.
        [Fact]
        public void JxlSelectedIsSavedAndOldWorkflowsReadAsBefore()
        {
            var w = new UpscaleWorkflow();
            w.SetJxlSelected();
            var json = JsonConvert.SerializeObject(w, NewtonsoftJsonSuspensionDriver.Settings);
            Assert.Contains("\"JxlSelected\": true", json);

            var back = (UpscaleWorkflow)JsonConvert.DeserializeObject(json, NewtonsoftJsonSuspensionDriver.Settings)!;
            Assert.True(back.JxlSelected);
            Assert.Equal("jxl", back.ImageFormat);

            var old = (UpscaleWorkflow)JsonConvert.DeserializeObject(json.Replace("\"JxlSelected\": true,", "").Replace("\"WebpSelected\": false", "\"WebpSelected\": true"), NewtonsoftJsonSuspensionDriver.Settings)!;
            Assert.False(old.JxlSelected);
            Assert.Equal("webp", old.ImageFormat);
        }

        [Theory]
        [InlineData("page.jxl")]
        [InlineData("page.TIFF")]
        [InlineData("page.tif")]
        public void TheCliImageTypesCountAsImages(string name) =>
            Assert.Contains(MainWindowViewModel.IMAGE_EXTENSIONS, x => name.ToLower().EndsWith(x));

        [Theory]
        [InlineData("vol.7z")]
        [InlineData("vol.cb7")]
        public void TheCliArchiveTypesCountAsArchives(string name) =>
            Assert.Contains(MainWindowViewModel.ARCHIVE_EXTENSIONS, x => name.ToLower().EndsWith(x));
    }
}
