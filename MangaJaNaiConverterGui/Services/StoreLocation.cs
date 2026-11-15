using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MangaJaNaiConverterGui.Services
{
    // Where this copy keeps the TensorRT runtime, its GPU builder packs and the models (its pack
    // store), and moving it.
    //
    // The choice (MangaJaNaiUpdater `--storage --json` / `--storage <inside|shared|folder>`): inside
    // the install (a portable copy's default), the per-user store every JaNai app can share (an
    // installed copy's), or a folder the user picks (apps set to the same folder share it). The
    // updater moves this copy's packs, and the TensorRT engines, when the choice changes.
    //
    // The shared store itself moves through aji-runtime at the install root: `location --json`,
    // `move`, `move --default`, `reset` (the-database/aji-runtime 0.2.0). A moved shared store leaves
    // a link at the default path, so the other apps keep working without knowing. Without the choice
    // (an updater from before it) that is all there is.
    public static class StoreLocation
    {
        public enum StoreChoice { Inside, Shared, Custom }

        // Root: the store folder this copy uses. Moved/Target: the shared store was moved and links
        // to Target. Available: false while the folder's drive isn't connected.
        public sealed record Info(string Root, bool Moved, string? Target, bool Available)
        {
            // null: the updater can't say (one from before the choice); the store is the shared one.
            public StoreChoice? Choice { get; init; }

            // The folders "inside" and "shared" mean for this copy (only with Choice).
            public string? Inside { get; init; }
            public string? Shared { get; init; }

            // This copy has its packs in its store (only with Choice).
            public bool Installed { get; init; }

            // The size of this copy's packs in its store, which is what a switch moves (only with
            // Choice; null otherwise).
            public long? Bytes { get; init; }

            public bool IsShared => Choice is null or StoreChoice.Shared;

            // The folder the store's files are in.
            public string Folder => IsShared && Moved && Target is not null ? Target : Root;

            // Where the shared store's files are, whichever store this copy uses.
            public string SharedFolder => Moved && Target is not null ? Target : Shared ?? (IsShared ? Root : "");

            // The shared store was moved, and the folder is there: Move Back to Default.
            public bool MovedHere => IsShared && Moved && Available;

            // The folder is gone (an unplugged drive): Try Again, Reset to Default or another folder.
            public bool Missing => !Available;

            public string Text =>
                Missing ? "This folder isn't available. Is its drive connected? Until it is, upscaling can't start."
                : Choice is null ? (Moved
                    ? $"Moved from {Root}, which now links here. All your JaNai apps share it."
                    : "The TensorRT runtime and the models are in the default folder. All your JaNai apps share them.")
                : MovedHere ? $"Moved from {Shared ?? Root}, which now links here."
                : !Installed ? "Nothing is installed here yet. Components you install go in the folder selected above."
                : "";
        }

        // Tests point these at an aji-runtime build or a fake updater.
        public static string? AjiRuntimeOverride { get; set; }
        public static string? UpdaterOverride { get; set; }

        public static string AjiRuntimePath => AjiRuntimeOverride ?? Path.Combine(AppContext.BaseDirectory, BackendService.Exe("aji-runtime"));

        public static string UpdaterPath => UpdaterOverride ?? Path.Combine(AppContext.BaseDirectory, BackendService.Exe("MangaJaNaiUpdater"));

        public static bool Supported => File.Exists(AjiRuntimePath);

        // The per-user store's path, as aji-runtime resolves it.
        public static string DefaultRoot()
        {
            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "the-database", "aji");
            }
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
                ? x
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(data, "the-database", "aji");
        }

        // The store this copy uses, as aji-runtime picks it: AJI_RUNTIME_STORE (tests), else the
        // settings file aji-runtime.conf beside it (AJI_RUNTIME_CONF names another; `settingsFile` for
        // tests), else the per-user store. Quick and offline, for the engine cache's place.
        public static string CurrentRoot(string? settingsFile = null)
        {
            if (Environment.GetEnvironmentVariable("AJI_RUNTIME_STORE") is { Length: > 0 } store)
            {
                return store;
            }
            settingsFile ??= Environment.GetEnvironmentVariable("AJI_RUNTIME_CONF") is { Length: > 0 } conf
                ? conf
                : Path.Combine(Path.GetDirectoryName(AjiRuntimePath) ?? AppContext.BaseDirectory, "aji-runtime.conf");
            if (ReadStoreSetting(settingsFile) is not { } value)
            {
                return DefaultRoot();
            }
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(settingsFile)) ?? ".";
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(dir, value)));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return DefaultRoot();
            }
        }

        // The `store` value of a settings file: comment lines start with # or ;, the value is trimmed
        // and one pair of quotes removed. null without a file, a value, or on any error (aji-runtime
        // then says what's wrong).
        public static string? ReadStoreSetting(string settingsFile)
        {
            try
            {
                if (!File.Exists(settingsFile))
                {
                    return null;
                }
                string? value = null;
                foreach (var line in File.ReadAllLines(settingsFile))
                {
                    var t = line.Trim().TrimStart('﻿');
                    if (t.Length == 0 || t.StartsWith('#') || t.StartsWith(';'))
                    {
                        continue;
                    }
                    int eq = t.IndexOf('=');
                    if (eq < 0 || !t[..eq].Trim().Equals("store", StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }
                    var v = t[(eq + 1)..].Trim();
                    if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
                    {
                        v = v[1..^1];
                    }
                    value = v.Length > 0 ? v : null;
                }
                return value;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        // The message wherever something needs the store while its folder is missing.
        public static string MissingMessage(string folder) =>
            $"The folder with the TensorRT runtime and the models, {folder}, isn't available. Is its drive connected? Reconnect it, or choose another place in App Settings > Storage.";

        // `aji-runtime location --json`.
        public static Info? Parse(string json)
        {
            try
            {
                var o = JObject.Parse(json);
                return new Info((string?)o["root"] ?? "", (bool?)o["moved"] ?? false, (string?)o["target"], (bool?)o["available"] ?? true);
            }
            catch
            {
                return null;
            }
        }

        // `MangaJaNaiUpdater --storage --json`: null unless it names a choice and a folder.
        public static Info? ParseChoice(string json)
        {
            try
            {
                var o = JObject.Parse(json);
                StoreChoice? choice = (string?)o["choice"] switch
                {
                    "inside" => StoreChoice.Inside,
                    "shared" => StoreChoice.Shared,
                    "custom" => StoreChoice.Custom,
                    _ => null,
                };
                if (choice is null || (string?)o["root"] is not { Length: > 0 } root)
                {
                    return null;
                }
                return new Info(root, (bool?)o["moved"] ?? false, (string?)o["target"], (bool?)o["available"] ?? true)
                {
                    Choice = choice,
                    Inside = (string?)o["inside"],
                    Shared = (string?)o["shared"],
                    Installed = (bool?)o["installed"] ?? false,
                    Bytes = o["bytes"] is { Type: JTokenType.Integer } b && (long)b >= 0 ? (long)b : null,
                };
            }
            catch
            {
                return null;
            }
        }

        // What App Settings > Storage shows: the updater's choice, else (an updater from before it)
        // where the shared store is. null: nothing to show.
        public static async Task<Info?> ReadAsync()
        {
            if (!Supported)
            {
                return null;
            }
            return await GetChoiceAsync() ?? await GetAsync();
        }

        // `MangaJaNaiUpdater --storage --json`; null when the updater can't say (missing, or older).
        public static async Task<Info?> GetChoiceAsync()
        {
            if (!File.Exists(UpdaterPath))
            {
                return null;
            }
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var (rc, stdout, _) = await RunAsync(UpdaterPath, ["--storage", "--json"], null, timeout.Token);
                return rc == 0 ? ParseChoice(stdout) : null;
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        // `aji-runtime location --json`; null when aji-runtime can't say (missing, or older than 0.2.0).
        public static async Task<Info?> GetAsync()
        {
            if (!Supported)
            {
                return null;
            }
            try
            {
                var (rc, stdout, _) = await RunAsync(AjiRuntimePath, ["location", "--json"], null, default);
                return rc == 0 ? Parse(stdout) : null;
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        // Moving into folder: aji-runtime needs a new or empty folder, so a folder with files in it
        // gets one of its own inside.
        public static string MoveTarget(string folder)
        {
            var empty = !Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any();
            return empty ? folder : Path.Combine(folder, "JaNai shared runtime");
        }

        // A folder that already is a store (another app keeps its packs there).
        public static bool IsStore(string folder) => File.Exists(Path.Combine(folder, "store.json"));

        // "A folder I choose": a store is used as it is (that is how two apps share one), as is a new
        // or empty folder; any other folder gets one of its own inside (which may be the store another
        // app made there the same way).
        public static string ChoiceTarget(string folder) =>
            IsStore(folder) ? folder : MoveTarget(folder);

        // The updater's argument for switching to folder: inside or shared when it is one of those.
        public static string ChoiceArgument(Info storage, string folder)
        {
            if (SameFolder(folder, storage.Inside))
            {
                return "inside";
            }
            if (SameFolder(folder, storage.Shared) || SameFolder(folder, storage.SharedFolder))
            {
                return "shared";
            }
            return Path.GetFullPath(folder);
        }

        public static bool SameFolder(string a, string? b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return false;
            }
            try
            {
                var ca = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
                var cb = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
                return string.Equals(ca, cb, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        // The bytes in the store's folder, for the confirmation (0 when it can't be read).
        public static long Size(string folder)
        {
            try
            {
                return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch
            {
                return 0;
            }
        }

        // (copied, total) bytes from an AJI_MOVE line.
        private static bool TryProgress(string line, out long done, out long total)
        {
            done = total = 0;
            var parts = line.Trim().Split(' ');
            return parts.Length == 3 && parts[0] == "AJI_MOVE" && long.TryParse(parts[1], out done) && long.TryParse(parts[2], out total);
        }

        // `move <target>`, `move --default` or `reset`, with progress: (copied, total) bytes from
        // AJI_MOVE lines. Returns null on success, else aji-runtime's error.
        public static async Task<string?> ChangeAsync(string[] args, Action<long, long>? progress)
        {
            var (rc, _, stderr) = await RunAsync(AjiRuntimePath, [.. args, "--progress", "lines"], line =>
            {
                if (TryProgress(line, out var done, out var total))
                {
                    progress?.Invoke(done, total);
                }
            }, default);
            if (rc == 0)
            {
                return null;
            }
            var error = stderr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("error: ", StringComparison.Ordinal));
            return error?["error: ".Length..] ?? $"aji-runtime exited with {rc}";
        }

        // `MangaJaNaiUpdater --storage <inside|shared|folder>`: this copy uses that store from now on,
        // and its packs move along. Progress from AJI_MOVE lines. Returns null on success, else the
        // updater's reason (its last line of output).
        public static async Task<string?> SwitchAsync(string target, Action<long, long>? progress)
        {
            var lines = new List<string>();
            var (rc, _, _) = await RunAsync(UpdaterPath, ["--storage", target], line =>
            {
                if (TryProgress(line, out var done, out var total))
                {
                    progress?.Invoke(done, total);
                }
                else if (line.Trim() is { Length: > 0 } text)
                {
                    lock (lines)
                    {
                        lines.Add(text);
                    }
                }
            }, default, errorsToo: true);
            if (rc == 0)
            {
                return null;
            }
            lock (lines)
            {
                return lines.Count > 0 ? lines[^1] : $"{Path.GetFileName(UpdaterPath)} exited with {rc}";
            }
        }

        // onLine sees stdout's lines (and stderr's, with errorsToo) as they come. Runs in exe's folder
        // (the install root).
        private static async Task<(int, string, string)> RunAsync(string exe, string[] args, Action<string>? onLine,
            CancellationToken cancel, bool errorsToo = false)
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe)) ?? AppContext.BaseDirectory,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
            using var p = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }
                lock (stdout)
                {
                    stdout.AppendLine(e.Data);
                }
                onLine?.Invoke(e.Data);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }
                lock (stderr)
                {
                    stderr.AppendLine(e.Data);
                }
                if (errorsToo)
                {
                    onLine?.Invoke(e.Data);
                }
            };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try
            {
                await p.WaitForExitAsync(cancel);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    p.Kill(true);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                throw;
            }
            return (p.ExitCode, stdout.ToString(), stderr.ToString());
        }
    }
}
