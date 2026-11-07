using System.IO;
using System.Linq;
using MangaJaNaiConverterGui.Services;
using Xunit;

namespace MangaJaNaiConverterGui.Tests
{
    // The same cases as MangaJaNaiCLI's run::tests, which this naming mirrors.
    public class OutputNamesTests
    {
        private static string[] Names(string template, params (string Source, string Directory, string Extension)[] inputs) =>
            OutputNames.Assign(inputs.Select(i => (i.Source, Path.Join("out", i.Directory), i.Extension)).ToList(), template)
                .Select(p => Path.GetRelativePath("out", p).Replace('\\', '/'))
                .ToArray();

        [Fact]
        public void UniqueNamesStayAsTheyAre()
        {
            Assert.Equal(["Vol 1-mangajanai.cbz", "Vol 2-mangajanai.cbz", "001-mangajanai.webp"],
                Names("%filename%-mangajanai", ("in/Vol 1.cbz", "", "cbz"), ("in/Vol 2.cbz", "", "cbz"), ("in/001.png", "", "webp")));
            Assert.Equal(["upscaled.cbz"], Names("upscaled", ("in/Vol 1.cbz", "", "cbz")));
            Assert.Equal(["upscaled.cbz", "upscaled.webp"], Names("upscaled", ("in/Vol 1.cbz", "", "cbz"), ("in/001.png", "", "webp")));
            Assert.Equal(["a/upscaled.cbz", "b/upscaled.cbz"], Names("upscaled", ("in/a/x.cbz", "a", "cbz"), ("in/b/y.cbz", "b", "cbz")));
        }

        [Fact]
        public void ANameWithoutFilenameGetsItWhereOutputsCollide()
        {
            Assert.Equal(["upscaled-Vol 1.cbz", "upscaled-Vol 2.cbz", "upscaled.webp"],
                Names("upscaled", ("in/Vol 1.cbz", "", "cbz"), ("in/Vol 2.cbz", "", "cbz"), ("in/001.png", "", "webp")));
        }

        [Fact]
        public void SameNamesWithDifferentExtensionsGetTheExtension()
        {
            Assert.Equal(["Vol 1-mangajanai-cbz.cbz", "Vol 1-mangajanai-zip.cbz", "001-mangajanai-png.webp", "001-mangajanai-jpg.webp"],
                Names("%filename%-mangajanai", ("in/Vol 1.cbz", "", "cbz"), ("in/Vol 1.zip", "", "cbz"), ("in/001.png", "", "webp"), ("in/001.JPG", "", "webp")));
            Assert.Equal(["upscaled-Vol 1-cbz.cbz", "upscaled-Vol 1-zip.cbz"], Names("upscaled", ("in/Vol 1.cbz", "", "cbz"), ("in/Vol 1.zip", "", "cbz")));
        }

        [Fact]
        public void AnythingLeftGetsANumber()
        {
            Assert.Equal(["x-a-png.webp", "x-a-png-2.webp"], Names("x", ("in/a.png", "", "webp"), ("in/a.PNG", "", "webp")));
            Assert.Equal(["a-png.webp", "a-png-2.webp", "a-jpg.webp"],
                Names("%filename%", ("in/a-png.png", "", "webp"), ("in/a.png", "", "webp"), ("in/a.jpg", "", "webp")));
        }

        [Fact]
        public void AnEmptyNameMeansTheInputsName()
        {
            Assert.Equal(["001.webp"], Names("", ("in/001.png", "", "webp")));
        }
    }
}
