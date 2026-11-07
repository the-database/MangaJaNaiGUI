using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MangaJaNaiConverterGui.Services
{
    // Where the shared pack store is (the TensorRT runtime, its GPU builder packs and the models every
    // JaNai app shares), through aji-runtime at the install root: `location --json`, `move`,
    // `move --default`, `reset` (the-database/aji-runtime 0.2.0). A moved store leaves a link at the
    // default path, so the other apps keep working without knowing.
    public static class StoreLocation
    {
        public sealed record Info(string Root, bool Moved, string? Target, bool Available)
        {
            // The folder the store's files are in.
            public string Folder => Moved && Target is not null ? Target : Root;
        }

        // Tests point it at an aji-runtime build.
        public static string? AjiRuntimeOverride { get; set; }

        public static string AjiRuntimePath => AjiRuntimeOverride ?? Path.Combine(AppContext.BaseDirectory, BackendService.Exe("aji-runtime"));

        public static bool Supported => File.Exists(AjiRuntimePath);

        // The store's default path, as aji-runtime resolves it (AJI_RUNTIME_STORE for tests).
        public static string DefaultRoot()
        {
            if (Environment.GetEnvironmentVariable("AJI_RUNTIME_STORE") is { Length: > 0 } store)
            {
                return store;
            }
            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "the-database", "aji");
            }
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
                ? x
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(data, "the-database", "aji");
        }

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

        public static async Task<Info?> GetAsync()
        {
            if (!Supported)
            {
                return null;
            }
            var (rc, stdout, _) = await RunAsync(["location", "--json"], null);
            return rc == 0 ? Parse(stdout) : null;
        }

        // Moving into folder: aji-runtime needs a new or empty folder, so a folder with files in it
        // gets one of its own inside.
        public static string MoveTarget(string folder)
        {
            var empty = !Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any();
            return empty ? folder : Path.Combine(folder, "JaNai shared runtime");
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

        // `move <target>`, `move --default` or `reset`, with progress: (copied, total) bytes from
        // AJI_MOVE lines. Returns null on success, else aji-runtime's error.
        public static async Task<string?> ChangeAsync(string[] args, Action<long, long>? progress)
        {
            var (rc, _, stderr) = await RunAsync([.. args, "--progress", "lines"], line =>
            {
                var parts = line.Split(' ');
                if (parts.Length == 3 && parts[0] == "AJI_MOVE" && long.TryParse(parts[1], out var done) && long.TryParse(parts[2], out var total))
                {
                    progress?.Invoke(done, total);
                }
            });
            if (rc == 0)
            {
                return null;
            }
            var error = stderr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("error: ", StringComparison.Ordinal));
            return error?["error: ".Length..] ?? $"aji-runtime exited with {rc}";
        }

        private static async Task<(int, string, string)> RunAsync(string[] args, Action<string>? onLine)
        {
            var psi = new ProcessStartInfo(AjiRuntimePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
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
                if (e.Data is not null)
                {
                    lock (stderr)
                    {
                        stderr.AppendLine(e.Data);
                    }
                }
            };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            return (p.ExitCode, stdout.ToString(), stderr.ToString());
        }
    }
}
