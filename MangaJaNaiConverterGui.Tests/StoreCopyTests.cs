using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MangaJaNaiUpdater;
using Xunit;

namespace MangaJaNaiConverterGui.Tests
{
    // The updater's rules for which copy this is and which ref is its own (MangaJaNaiUpdater/StoreCopy.cs):
    // an installed copy keeps the plain ref id, any other copy its own, and no copy reads or changes
    // another's.
    public sealed class StoreCopyTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"mjn-copy-{Guid.NewGuid():N}");

        public StoreCopyTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private static string RepoFile(string rel)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "MangaJaNaiConverterGui.sln")))
                {
                    return Path.Combine(dir.FullName, rel);
                }
            }
            throw new FileNotFoundException("the repository root");
        }

        [Fact]
        public void The_setup_installers_copy_is_told_from_any_other()
        {
            string dir = Path.Combine(_root, "copy-kind");
            Directory.CreateDirectory(dir);
            Assert.False(StoreCopy.IsSetupInstall(dir, []), "an empty folder counted as installed");
            File.WriteAllText(Path.Combine(dir, "uninstall-notes.exe"), "");
            File.WriteAllText(Path.Combine(dir, "unins.exe"), "");
            Assert.False(StoreCopy.IsSetupInstall(dir, []), "another exe named like an uninstaller counted as installed");
            File.WriteAllText(Path.Combine(dir, "unins000.exe"), "");
            Assert.True(StoreCopy.IsSetupInstall(dir, []), "an install with unins000.exe counted as a zip copy");
            File.Delete(Path.Combine(dir, "unins000.exe"));
            File.WriteAllText(Path.Combine(dir, StoreCopy.SetupMarker), "");
            Assert.True(StoreCopy.IsSetupInstall(dir, []), "the setup's marker was ignored");

            // the installer's uninstall entry (InstallLocation has a trailing separator, App Path none)
            File.WriteAllText(Path.Combine(dir, "unins000.exe"), "");
            Assert.True(StoreCopy.IsSetupInstall(dir, [dir + Path.DirectorySeparatorChar, dir]), "the entry naming this folder made it a zip copy");
            if (OperatingSystem.IsWindows())
            {
                Assert.True(StoreCopy.IsSetupInstall(dir, [dir.ToUpperInvariant() + Path.DirectorySeparatorChar]), "folder case decided the copy kind");
            }
            string elsewhere = Path.Combine(_root, "installed-elsewhere");
            Assert.False(StoreCopy.IsSetupInstall(dir, [elsewhere + Path.DirectorySeparatorChar, elsewhere]),
                "a copy of the installed folder (the entry names another folder) took the installed copy's id");

            string zip = Path.Combine(_root, "copy-kind-zip");
            Directory.CreateDirectory(zip);
            Assert.False(StoreCopy.IsSetupInstall(zip, [zip]), "an entry naming a folder without the setup's files made it installed");
        }

        [Fact]
        public void MANGAJANAI_SETUP_LOCATION_stands_in_for_the_registry()
        {
            var old = Environment.GetEnvironmentVariable("MANGAJANAI_SETUP_LOCATION");
            try
            {
                Environment.SetEnvironmentVariable("MANGAJANAI_SETUP_LOCATION", @"C:\Apps\MangaJaNai ");
                Assert.Equal([@"C:\Apps\MangaJaNai"], StoreCopy.SetupInstallLocations());
                Environment.SetEnvironmentVariable("MANGAJANAI_SETUP_LOCATION", " ");
                Assert.Empty(StoreCopy.SetupInstallLocations());
            }
            finally
            {
                Environment.SetEnvironmentVariable("MANGAJANAI_SETUP_LOCATION", old);
            }
        }

        [Fact]
        public void The_updater_reads_the_installers_AppId()
        {
            var iss = File.ReadAllText(RepoFile(Path.Combine("installer", "mangajanaiconvertergui.iss")));
            // Inno's {{ escape is one brace
            var ids = Regex.Matches(iss, @"^AppId=(\S+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Replace("{{", "{")).ToArray();
            Assert.NotEmpty(ids);
            Assert.Equal(ids.Order(), StoreCopy.InstallerAppIds.Order());
        }

        [Fact]
        public void The_package_preserves_the_store_setting_and_the_store_inside_it()
        {
            var builder = File.ReadAllText(RepoFile(Path.Combine("BuildMangaJaNai", "Program.cs")));
            var line = builder.Split('\n').Single(l => l.TrimStart().StartsWith("user_preserve = ", StringComparison.Ordinal));
            var preserve = Regex.Matches(line, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
            Assert.Contains("aji-runtime.conf", preserve);
            Assert.Contains(StoreCopy.InsideStore, preserve);
            Assert.Contains("inference/aji-runtime.json", preserve);
        }

        private static string Show(string? appPath, bool hasRef = true, string refId = "mangajanai-gui") => JsonSerializer.Serialize(new
        {
            app_id = "mangajanai-gui",
            app_path = appPath,
            app_version = "2.7.0",
            has_ref = hasRef,
            ref_id = refId,
            packs = new object[]
            {
                new { name = "trt-runtime", version = "11.4", dir = @"C:\s\packs\trt-runtime\11.4", installed = true },
                new { name = "models-mangajanai", version = "1.0.0", dir = @"C:\s\packs\models-mangajanai\1.0.0", installed = true },
                new { name = "trt-sm89", version = "11.4", dir = @"C:\s\packs\trt-sm89\11.4", installed = false },
            },
        });

        [Fact]
        public void Only_this_copys_ref_counts()
        {
            string me = OperatingSystem.IsWindows() ? @"C:\Apps\MangaJaNai\MangaJaNaiConverterGui.exe" : "/apps/mangajanai/MangaJaNaiConverterGui";
            string sameDifferentSpelling = OperatingSystem.IsWindows() ? @"c:\apps\MANGAJANAI\.\MangaJaNaiConverterGui.exe" : "/apps/mangajanai/./MangaJaNaiConverterGui";
            var own = StoreCopy.ParseOwnRef(Show(sameDifferentSpelling, refId: "mangajanai-gui-0123abcd"), me);
            Assert.NotNull(own);
            Assert.Equal(["models-mangajanai", "trt-runtime"], own.Packs.Keys.Order());
            Assert.Equal("2.7.0", own.AppVersion);
            Assert.Equal("mangajanai-gui-0123abcd", own.RefId);
            Assert.Null(StoreCopy.ParseOwnRef(Show(OperatingSystem.IsWindows() ? @"C:\Other\MangaJaNai\MangaJaNaiConverterGui.exe" : "/other/MangaJaNaiConverterGui"), me));
            Assert.Null(StoreCopy.ParseOwnRef(Show(null, hasRef: false), me));
            Assert.Null(StoreCopy.ParseOwnRef("not json", me));
        }

        [Fact]
        public void Location_json_is_read()
        {
            var loc = StoreCopy.ParseLocation("""
                {"available": false, "default_root": "C:\\u\\aji", "moved": false, "relative": true,
                 "root": "D:\\p\\runtime", "setting": "runtime",
                 "settings_file": "D:\\p\\aji-runtime.conf", "source": "settings", "target": null}
                """);
            Assert.True(loc is { Available: false, Source: "settings", Relative: true, Folder: @"D:\p\runtime", SettingsFile: @"D:\p\aji-runtime.conf" });
            var moved = StoreCopy.ParseLocation("""{"available": false, "moved": true, "root": "C:\\u\\aji", "target": "E:\\aji"}""");
            Assert.True(moved is { Folder: @"E:\aji", Source: null }, "a moved store's folder isn't its target (or an old aji-runtime got a source)");
            Assert.Null(StoreCopy.ParseLocation("""{"moved": false}"""));
            Assert.Null(StoreCopy.ParseLocation("nope"));
        }

        [Fact]
        public void The_choice_and_what_aji_runtime_use_is_given()
        {
            string inside = Path.Combine(_root, "app", "runtime");
            string shared = Path.Combine(_root, "local", "aji");
            AjiLocation At(string root) => new(root, false, null, true, "settings", null, false, null, shared);
            Assert.Equal("inside", StoreCopy.Choice(At(inside + Path.DirectorySeparatorChar), inside));
            Assert.Equal("shared", StoreCopy.Choice(At(shared), inside));
            Assert.Equal("custom", StoreCopy.Choice(At(Path.Combine(_root, "elsewhere")), inside));

            Assert.Equal("runtime", StoreCopy.UseTarget("inside", inside, shared));
            Assert.Equal("--shared", StoreCopy.UseTarget("SHARED", inside, shared));
            Assert.Equal("runtime", StoreCopy.UseTarget(inside, inside, shared));
            Assert.Equal("--shared", StoreCopy.UseTarget(shared + Path.DirectorySeparatorChar, inside, shared));
            string custom = Path.Combine(_root, "custom");
            Assert.Equal(custom, StoreCopy.UseTarget(custom + Path.DirectorySeparatorChar, inside, shared));
            Assert.Null(StoreCopy.UseTarget("relative\\folder", inside, shared));
        }
    }
}
