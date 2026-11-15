using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MangaJaNaiUpdater
{
    // Which copy of MangaJaNai GUI this is, and what aji-runtime says about its pack store: the parts
    // of the updater's store mode that only read, so MangaJaNaiConverterGui.Tests can check them (it
    // compiles this file too).
    //
    // Each copy has its own ref in the store. The copy the setup installer made keeps the plain id
    // "mangajanai-gui" (its uninstaller unrefs it); any other Windows copy (a portable zip, maybe next
    // to an installed copy, or a copy of the installed folder) passes --per-copy --app-path <its exe>,
    // so its ref is "mangajanai-gui-<8 hex>" of its path and copies never read or change each other's.
    public static class StoreCopy
    {
        public const string AppId = "mangajanai-gui";

        // Written by the setup installer (installer/mangajanaiconvertergui.iss) before it first runs
        // the updater.
        public const string SetupMarker = "installed-by-setup.txt";

        // A zip copy's default store, inside its own folder: relative to aji-runtime.conf at the
        // install root, so the folder can be moved or copied to another PC.
        public const string InsideStore = "runtime";

        // The installer's AppId (installer/mangajanaiconvertergui.iss; the tests check they match).
        public static readonly string[] InstallerAppIds = ["{530AF8E6-8505-4160-8FBC-C07D0B9A0910}"];

        // Whether this copy is the one the setup installer made: its uninstaller (unins000.exe,
        // unins001.exe, ...) or the marker the installer writes before it first runs the updater is in
        // the folder, AND the installer's own record doesn't put the installed copy in another folder.
        // `setupLocations` are the folders that record names (SetupInstallLocations). Both files come
        // along when someone copies the installed folder, so a copy elsewhere on the same PC is a zip
        // copy; with no record (a first install before Inno writes it, another PC, an unreadable
        // registry) the files decide. Any other copy is a zip copy.
        public static bool IsSetupInstall(string dir, IReadOnlyCollection<string> setupLocations)
        {
            bool setupFiles;
            try
            {
                setupFiles = File.Exists(Path.Combine(dir, SetupMarker)) ||
                             Directory.EnumerateFiles(dir, "unins*.exe").Any(f => Regex.IsMatch(Path.GetFileName(f), @"^unins\d{3}\.exe$", RegexOptions.IgnoreCase));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
            return setupFiles && (setupLocations.Count == 0 || setupLocations.Any(l => SamePath(l, dir)));
        }

        // The folders the setup installer's uninstall entry names: Inno Setup writes InstallLocation
        // ({app}\) and "Inno Setup: App Path" ({app}) under HKCU\...\Uninstall\<AppId>_is1 (the
        // installer is per-user: PrivilegesRequired=lowest). Empty without an entry, off Windows, or on
        // any registry error. MANGAJANAI_SETUP_LOCATION (tests) stands in for the registry: a folder,
        // or empty for no entry.
        public static List<string> SetupInstallLocations()
        {
            var folders = new List<string>();
            if (Environment.GetEnvironmentVariable("MANGAJANAI_SETUP_LOCATION") is { } fake)
            {
                if (fake.Trim().Length > 0)
                {
                    folders.Add(fake.Trim());
                }
                return folders;
            }
            if (!OperatingSystem.IsWindows())
            {
                return folders;
            }
            foreach (var appId in InstallerAppIds)
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{appId}_is1");
                    foreach (var value in new[] { "InstallLocation", "Inno Setup: App Path" })
                    {
                        if (key?.GetValue(value) is string s && s.Trim().Length > 0)
                        {
                            folders.Add(s.Trim());
                        }
                    }
                }
                catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
            }
            return folders;
        }

        public static bool SamePath(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return false;
            }
            try
            {
                return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        // `aji-runtime show` output -> this copy's ref: only when there is a ref and it names this
        // copy's exe (show's app_path is absolute). A ref naming another path is another copy's (or a
        // moved install's: ensuring again writes it for this path). Pack names are the store's.
        public static StoreRef? ParseOwnRef(string showJson, string appPath)
        {
            try
            {
                using var doc = JsonDocument.Parse(showJson);
                var r = doc.RootElement;
                if (r.TryGetProperty("has_ref", out var has) && has.ValueKind == JsonValueKind.False)
                {
                    return null;
                }
                string? refPath = r.TryGetProperty("app_path", out var ap) && ap.ValueKind == JsonValueKind.String ? ap.GetString() : null;
                if (!SamePath(refPath, appPath))
                {
                    return null;
                }
                var packs = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
                if (r.TryGetProperty("packs", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in arr.EnumerateArray())
                    {
                        if (p.TryGetProperty("installed", out var i) && i.ValueKind == JsonValueKind.True)
                        {
                            packs[p.GetProperty("name").GetString() ?? ""] =
                                (p.GetProperty("version").GetString() ?? "", p.GetProperty("dir").GetString() ?? "");
                        }
                    }
                }
                string Str(string key) => r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                return new StoreRef(Str("ref_id"), Str("app_version"), packs);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                return null;
            }
        }

        // `aji-runtime location --json`; null when it names no store.
        public static AjiLocation? ParseLocation(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                string? Str(string key) => r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                bool Flag(string key, bool fallback) => r.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? v.GetBoolean() : fallback;
                if (Str("root") is not { Length: > 0 } root)
                {
                    return null;
                }
                return new AjiLocation(root, Flag("moved", false), Str("target"), Flag("available", true), Str("source"),
                    Str("setting"), Flag("relative", false), Str("settings_file"), Str("default_root"));
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException)
            {
                return null;
            }
        }

        // Which of App Settings > Storage's choices a store is: inside this copy, the per-user store
        // shared with the other JaNai apps, or another folder.
        public static string Choice(AjiLocation loc, string insideDir) =>
            SamePath(loc.Root, insideDir) ? "inside" :
            SamePath(loc.Root, loc.DefaultRoot) ? "shared" : "custom";

        // `--storage <choice>`'s argument -> what `aji-runtime use` takes: the inside folder (relative,
        // so the copy can move), --shared, or a full folder path. null when it is none of those.
        public static string? UseTarget(string choice, string insideDir, string? sharedRoot)
        {
            if (choice.Equals("inside", StringComparison.OrdinalIgnoreCase))
            {
                return InsideStore;
            }
            if (choice.Equals("shared", StringComparison.OrdinalIgnoreCase))
            {
                return "--shared";
            }
            if (!Path.IsPathFullyQualified(choice))
            {
                return null;
            }
            string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(choice));
            return SamePath(folder, insideDir) ? InsideStore : SamePath(folder, sharedRoot) ? "--shared" : folder;
        }
    }

    // This copy's ref in the pack store: its id, the app version it was written for, and its installed
    // packs (store name -> (version, folder)).
    public sealed record StoreRef(string RefId, string AppVersion, Dictionary<string, (string Version, string Dir)> Packs);

    // `aji-runtime location --json`. Folder: where the store's files are (a moved per-user store's
    // target).
    public sealed record AjiLocation(string Root, bool Moved, string? Target, bool Available, string? Source, string? Setting,
                                     bool Relative, string? SettingsFile, string? DefaultRoot)
    {
        public string Folder => Moved && Target is { Length: > 0 } t ? t : Root;
    }
}
