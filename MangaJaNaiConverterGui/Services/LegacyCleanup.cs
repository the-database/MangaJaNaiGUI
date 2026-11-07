using System;
using System.IO;
using System.Linq;

namespace MangaJaNaiConverterGui.Services
{
    // MangaJaNaiConverterGui 1.x left its Python backend (several GB) and its default PyTorch models in
    // %APPDATA%\MangaJaNaiConverterGui, the folder 2.x keeps its settings and user models in. The
    // installer removes them when it upgrades from 1.x (installer/mangajanaiconvertergui.iss,
    // CurStepChanged), but only since 2.0.1, and an in-app update never runs the installer: a 1.x user
    // who installed 2.0.0 kept them. So the app does the same once it's an installed copy, with the
    // installer's rules.
    public static class LegacyCleanup
    {
        // The installer's IsLegacyDefaultModel: 1.x's default models, <digit>x_MangaJaNai_* or
        // <digit>x_IllustrationJaNai_*, ending in .pth or .safetensors. 2.x ships their ONNX exports
        // and moves saved chains to them (ModelVariants.MigrateLegacyName). Models the user added under
        // any other name, and every .onnx, stay.
        public static bool IsLegacyDefaultModel(string name)
        {
            var lower = name.ToLowerInvariant();
            var ext = Path.GetExtension(lower);
            return lower.Length > 3 && char.IsAsciiDigit(lower[0]) && lower[1] == 'x' &&
                   (lower.IndexOf("_mangajanai_", StringComparison.Ordinal) == 2 || lower.IndexOf("_illustrationjanai_", StringComparison.Ordinal) == 2) &&
                   (ext == ".pth" || ext == ".safetensors");
        }

        // Removes 1.x's leftovers from appStateFolder, if its Python backend is still there (the sign
        // of a 1.x upgrade, as for the installer: without it the models folder is never touched).
        // Returns the bytes freed. Anything that can't be deleted (in use) is left for the next start.
        public static long Run(string appStateFolder)
        {
            var python = Path.Combine(appStateFolder, "python");
            if (!Directory.Exists(python))
            {
                return 0;
            }
            long freed = 0;
            var models = Path.Combine(appStateFolder, "models");
            if (Directory.Exists(models))
            {
                foreach (var file in Directory.EnumerateFiles(models).Where(f => IsLegacyDefaultModel(Path.GetFileName(f))).ToList())
                {
                    try
                    {
                        var size = new FileInfo(file).Length;
                        File.Delete(file);
                        freed += size;
                    }
                    catch
                    {
                    }
                }
            }
            var before = Size(python);
            try
            {
                Directory.Delete(python, recursive: true);
            }
            catch
            {
            }
            return freed + before - (Directory.Exists(python) ? Size(python) : 0);
        }

        private static long Size(string dir)
        {
            try
            {
                return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch
            {
                return 0;
            }
        }
    }
}
