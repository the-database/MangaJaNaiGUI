// Package builder for MangaJaNai GUI (Inno-installer distribution model on Windows; tarball/AppImage on
// Linux). Ported from VideoJaNai's BuildVideoJaNai, without ffmpeg/RIFE.
//
// Assembles the redistributable install tree (the app + MangaJaNaiUpdater + the inference dir: libaji,
// the mangajanai_upscale backend, and on Windows ONNX Runtime/DirectML and the VC++ runtime) and, under
// --packs, carves the heavy optional pieces (TensorRT runtime, per-GPU-generation builder resources, the
// ONNX models, the TensorRT-RTX backend) into component-*.7z + packs.json release assets, then slims
// them out of the core. End users download the small core plus the packs their hardware needs.
//
//   BuildMangaJaNai <version> [--target win-x64|linux-x64] --app-dir <dir> --updater <exe> [--packs] [--out <dir>]
//
// --target defaults to the host RID; every platform-varying name lives in the Platform descriptor at
// the bottom. Linux has no DirectML and no TensorRT-RTX (libaji builds neither there), so TensorRT is
// its one backend. Its release assets carry the RID, so both platforms share a release.
//
// Outputs, under --out (default: next to this exe):
//   MangaJaNaiConverterGui-v<ver>/                    the (slim) install tree, the Inno installer's SourceDir
//   MangaJaNaiGUI-full-package-<ver>.zip              that tree, the updater's full update (Windows)
//   MangaJaNaiGUI-v<ver>-linux-x64.tar.gz             that tree, the Linux download and full update
//   packs-v<ver>/component-*.7z + packs.json          component packs (with --packs; on Linux
//                                                     component-*-linux-x64.7z + packs-linux-x64.json)
//
// Dev overrides (skip large downloads on local builds):
//   TRT_LOCAL_ZIP           -> a downloaded TensorRT "-external" zip (Windows)
//   TRT_LOCAL_DIR           -> an existing inference dir holding the TensorRT runtime + cudart (e.g. VideoJaNai's;
//                              on Linux, a `mangajanai setup` folder works)
//   TRT_DEBS_DIR            -> a directory holding the pinned TensorRT .debs (Linux; still sha-checked)
//   AJI_LOCAL_ZIP           -> a locally built aji-windows-x64.zip
//   AJI_LOCAL_DIR           -> a directory of aji files copied flat into inference/ (with aji_trtrtx.dll
//                              and its runtime, if it holds them)
//   AJI_TRTRTX_LOCAL_ZIP    -> a locally built aji-trtrtx-windows-x64.zip, from the same build as the
//                              aji files. With AJI_LOCAL_ZIP/AJI_LOCAL_DIR and without this, the released
//                              one is not mixed in: there is no trt-rtx pack unless the dir holds it.
//   MANGAJANAI_CLI_LOCAL    -> a local mangajanai_upscale.exe (Linux: mangajanai_upscale or mangajanai), or a
//                              directory/archive holding it
//   MANGAJANAI_MODELS_DIR   -> a local directory holding the model archives (by their asset names) and/or
//                              loose .onnx files named by the precision rule below
//   VC_CRT_DIR              -> a Microsoft.VC14x.CRT directory (default: found through vswhere)
//   AJI_RUNTIME_LOCAL       -> a local aji-runtime(.exe), the shared pack store's tool (default: the
//                              pinned the-database/aji-runtime release)
//
// --store (what releases build): the core alone. The TensorRT runtime, the builder resources,
// TensorRT-RTX's runtime and the models come from the shared runtime packs at install time (the
// updater's store mode, the AppImage's AppRun), so they aren't downloaded, assembled or packed here;
// aji_trtrtx.dll comes from libaji's small aji-trtrtx-backend-windows-x64.zip. Without --store (and
// without --packs) everything stays in the tree, for local runs.
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.Zip;
using SevenZipExtractor;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static Downloader;

// Third-party component versions. Bump these together when cutting a release.
//
// The TensorRT runtime + trtexec come straight from NVIDIA's public download (the "-external" zip on
// Windows, the CUDA apt repo's .debs on Linux), the same pins as VideoJaNai and MangaJaNaiCLI's runtime
// packs. TrtCudaVersion is its own pin, NOT derivable from TrtVersion: guessing
// it 404s.
const string TrtVersion        = "11.4.0.106";
const string TrtCudaVersion    = "13.4";
// NVIDIA publishes no checksum beside the zip, so this is recorded from a verified download.
const string TrtSha256Win      = "b6fd86301ad803492adf7a06de29ce45e3968d29637a1437d0fb0994f412c980";

// CUDA runtime, from NVIDIA's CUDA redistributable (per-file checksums in
// developer.download.nvidia.com/compute/cuda/redist/redistrib_13.4.1.json).
const string CudartVersion     = "13.4.49";
const string CudartSha256Win   = "e6663f3d3e8949eedc2d5ab92c7c5b9fa3f2a222086d91c42bfc5a38bf2b0225";
const string CudartSha256Linux = "ec3df9f91560db23a064c6b395d4fde17b2893f5bf954c6bd193dd03a44de522";

// Linux TensorRT .debs from the CUDA repo pool; the repo's Packages index publishes these hashes.
const string CudaAptRepo       = "https://developer.download.nvidia.com/compute/cuda/repos/ubuntu2204/x86_64";
var trtDebSha256 = new Dictionary<string, string>
{
    ["libnvinfer11"]        = "bd24db760728779e980c8852ba87e148a0bf9fb4e69d69bc14f3bdabc0423797",
    ["libnvinfer-plugin11"] = "a99bd3de4fd45585d4a1fce8f6a8d6415e1a7946c86d907f64af0b9b487aee66",
    ["libnvonnxparsers11"]  = "d7b0e623e6b8d1c368a000b996938494137e2584b85e2eed8b89f7b34092436c",
    ["libnvinfer-bin"]      = "36b0a4f11c3a15bf9f2d4eb5a33ce02b7248582eb384696a1d80a154abf005f8",
};

// the-database/animejanai-inference. v0.18.0 added the still-image ABI (aji_image.h) the backend uses;
// v0.18.2 makes DirectML device ids the card's PCI address, so they match TensorRT's and survive reboots;
// v0.19.0 makes DirectML ~35-40% faster and adds TensorRT-RTX; v0.19.1 fixes its engine-build teardown;
// v0.21.0 tiles on the GPU (TensorRT/TensorRT-RTX), speeds up DirectML's tiling, and fixes tile seams in
// MangaJaNai's generated screentone (128 px overlaps, blended at their centre).
// Releases of libaji and MangaJaNaiCLI open the PRs that move these pins and their SHA-256s
// (.github/workflows/bump-pins.yml, scripts/bump_pins.py libaji|mangajanai-cli <tag>).
const string AjiVersion        = "v0.23.1";
const string AjiSha256Win      = "c433533f5310cd2443cec19bd412a9387fb1bd5e37225904c3d7fb28bfd3ba79";
// The same release's TensorRT-RTX backend: aji_trtrtx.dll + the TensorRT for RTX runtime and its
// licence notices (~232 MB unpacked). aji_trtrtx.dll stays in the core (it must match aji.dll); the
// runtime becomes the trt-rtx pack.
const string AjiTrtRtxSha256Win = "ce5ce6bc7d0bd7e5fefaa4ad2e952bd9ea3b4c56490aa66db6c03c5f188eb3e3";
// aji_trtrtx.dll alone (store builds): the same release's aji-trtrtx-backend-windows-x64.zip.
const string AjiTrtRtxBackendSha256Win = "e34bfcb945573e2c66f1e9048df191029472666b92780401b6e7d835e0456fd7";
// Linux: libaji.so + libaji_trt.so (TensorRT only).
const string AjiSha256Linux    = "4a5912adceef4c627b7b34f35cc5d5d93b51ebd33013e630da37b8c71853e9c5";
// The shared runtime packs every libaji app pins (animejanai-inference runtime-<n>): TensorRT, the
// per-GPU builder packs, TensorRT-RTX's runtime and the default models, installed once per user into
// the shared pack store by aji-runtime (the updater's store mode). The manifest carries the platform's
// index URL and SHA-256. Its runtime-packs.yml asks bump-pins.yml here for the bump PR.
const string RuntimeRepo       = "the-database/animejanai-inference";
const string RuntimeTag        = "runtime-2";
const string RuntimeIndexSha256Win   = "5b6f0450ba4eed34d6e25dde072ce1298f083d91d142fd858495483e7711aea1";
const string RuntimeIndexSha256Linux = "d441210f7747b255f17483f7c8b921817ad3f997abab5a1ba5d348b51511ab61";
// aji-runtime, the store's tool, from its release (the binary in aji-runtime-<platform>.zip/.tar.gz).
const string AjiRuntimeRepo    = "the-database/aji-runtime";
const string AjiRuntimeVersion = "v0.1.0";
const string AjiRuntimeSha256Win   = "b936caadcff9fe04e7ef1a4cb35e8091f7c9e094eb21e420387176108c178475";
const string AjiRuntimeSha256Linux = "5c97da8db3a3d1bb432bc79d5316cbd59f80385e0ffecc59d40cba7f89d6845d";
const string SevenZipVersion   = "2602";                  // 7-zip "extra": 7za.exe for the updater's packs
// 7-Zip's own GitHub releases (ip7z/7zip; same file as www.7-zip.org/a/, which CI runners have
// failed to resolve). 7z2602-extra.7z:
const string SevenZipSha256    = "081df9e9311dfd9c9e0e98c1c80180b99bb51e4cb24156b5f3057fe3c259d70a";
// Linux: the same release's standalone 7zz console, 7z2602-linux-x64.tar.xz.
const string SevenZipSha256Linux = "41aaba7b1235304ab5aa0624530c67ae829496cd29e875925271efdccc28c03e";
// DirectML backend runtime (aji_dml.dll loads both from its own directory). The last DirectML-flavored
// ORT releases (DML is in sustained engineering); same pins as VideoJaNai.
const string OrtDmlVersion     = "1.24.4";                // Microsoft.ML.OnnxRuntime.DirectML (NuGet)
const string DirectMLVersion   = "1.15.4";                // Microsoft.AI.DirectML (NuGet)

// The upscaling backend: mangajanai_upscale.exe from the-database/MangaJaNaiCLI (until v0.1.4 it was
// yosh's crates/mangajanai-cli, released there on mangajanai-cli-v* tags), built on yosh-image +
// libaji. MANGAJANAI_CLI_LOCAL overrides it with a local build.
const string CliRepo           = "the-database/MangaJaNaiCLI";
// v0.2.0: images with uniform transparency are upscaled once instead of twice, varying transparency gets
// chaiNNer's alpha denoise, and folders also take tif/tiff/jxl images and 7z/cb7 archives. v0.1.4 added
// --model-info and HINT lines, compute_capability in --list-devices, and runs exactly the model file
// it is given. v0.1.2 added --backend tensorrt-rtx and BACKEND_FALLBACK lines.
const string CliVersion        = "v0.4.1";
const string CliAsset          = "mangajanai-cli-windows-x64.zip";
const string CliSha256         = "2fed4b54f54c1bbd189560d27249a01a4b566aaa8d1b56ca1ebabb4f14453875";
// Linux: the release's mangajanai-linux-x64.tar.zst. Its `mangajanai` is the same program as
// mangajanai_upscale (one binary under two names, the GUI's --settings interface included), so it ships
// as inference/mangajanai_upscale. The libaji it carries is left out: InstallAji supplies the same one.
const string CliAssetLinux     = "mangajanai-linux-x64.tar.zst";
const string CliSha256Linux    = "66c175ec06b4c5750efd772dd701519536cdc3b0245a41bad8fa1931612d9e7f";

// The default ONNX models, published as archives on each model's MangaJaNai release, one archive per
// component pack. Every file is named <checkpoint>_<precision of the ONNX itself> (fp16, bf16 or fp32),
// so the model list says what runs. DAT2 (2) and HAT_L overflow in fp16, so they come in two variants:
// bf16 for NVIDIA GPUs from compute capability 8.0 (about 40-50% faster than fp32 on TensorRT and
// TensorRT-RTX, 52-54 dB vs fp32) and fp32 for DirectML and older NVIDIA GPUs, which can't run bf16.
// The updater installs one variant pack per machine (see RecommendedPacks in MangaJaNaiUpdater).
// The release publishes no checksums; recorded from the uploaded assets.
const string ModelsRepo        = "the-database/MangaJaNai";
(string Pack, string Tag, string Asset, string Sha256)[] modelArchives =
[
    ("models-mangajanai", "1.0.0", "MangaJaNai_V1_ONNX_fp16.7z", "2c3809d712e3e96d107e2a59802186c284fac49d91a211435c8c896432ba27ab"),
    ("models-illustrationjanai", "3.0.0", "IllustrationJaNai_V3_ONNX_fp16.7z", "bd7db0632b37b95b06410e99f95347980010b2ccc54730e2d68501f25bae898c"),
    ("models-illustrationjanai-fp32", "3.0.0", "IllustrationJaNai_V3_ONNX_fp32.7z", "8b38bfdc95f194fb18eaa51d7ca869839ebbb1c29bed1e7a943a65b372d090ac"),
    ("models-illustrationjanai-bf16", "3.0.0", "IllustrationJaNai_V3_ONNX_bf16.7z", "48e292c3f92aa2df79f9f83c6318a6c2052c29e6ea9fe2ed23b61cc526900247"),
];

// Which models pack a model file belongs to, by its name; null if it isn't a shipped model.
static string? ModelPack(string fileName)
{
    var stem = Path.GetFileNameWithoutExtension(fileName);
    if (stem.Contains("_MangaJaNai_", StringComparison.Ordinal)) return "models-mangajanai";
    if (!stem.Contains("_IllustrationJaNai_", StringComparison.Ordinal)) return null;
    if (stem.EndsWith("_bf16", StringComparison.Ordinal)) return "models-illustrationjanai-bf16";
    if (stem.EndsWith("_fp32", StringComparison.Ordinal)) return "models-illustrationjanai-fp32";
    return "models-illustrationjanai";
}

// A models pack's manifest dep: its archive, so a new archive reinstalls the pack.
string ModelDep(string pack)
{
    var a = modelArchives.First(m => m.Pack == pack);
    return $"{a.Asset}@{a.Tag}";
}

// The app was MangaJaNaiConverterGui until 2.0. The exe and the install tree's root folder keep that
// name (older updaters look for it in the full package); the release assets carry the new one.
const string AppName = "MangaJaNaiConverterGui";
const string ReleaseName = "MangaJaNaiGUI";

if (args.Length < 1 || args[0].StartsWith("--"))
{
    throw new ArgumentException("Version is required.  Usage: BuildMangaJaNai <version> [--target win-x64|linux-x64] --app-dir <dir> --updater <exe> [--packs] [--out <dir>]");
}

var plat = (Array.IndexOf(args, "--target") is int ti and >= 0 && ti + 1 < args.Length
        ? args[ti + 1]
        : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" : "linux-x64") switch
{
    "win-x64" => Platform.Win,
    "linux-x64" => Platform.Linux,
    var rid => throw new ArgumentException($"Unsupported --target '{rid}' (use win-x64 or linux-x64)"),
};
Console.WriteLine($"Target: {plat.Rid}");
if (plat.IsWindows != RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    // The Windows leg needs the 7-Zip DLL wrapper and vswhere; the Linux one dpkg-deb, tar and 7zz.
    throw new ArgumentException($"Build {plat.Rid} on {(plat.IsWindows ? "Windows" : "Linux")}.");
}

var version = args[0];

string? ArgValue(string flag)
{
    int i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var outDirectory = Path.GetFullPath(ArgValue("--out") ?? AppContext.BaseDirectory);
var installDirectory = Path.Combine(outDirectory, $"{AppName}-v{version}");
var inferencePath = Path.Combine(installDirectory, "inference");
var modelsPath = Path.Combine(installDirectory, "models");
var workDirectory = Path.Combine(outDirectory, "build-work");

// Standalone 7-Zip console: builds the component packs, and ships at the install root for the
// updater to install them (manifest archive_tool).
async Task InstallSevenZip()
{
    Console.WriteLine("Downloading 7-Zip standalone console...");
    var tag = $"{SevenZipVersion[..^2]}.{SevenZipVersion[^2..]}";   // "2602" -> "26.02"
    if (!plat.IsWindows)
    {
        // The linux-x64 console ships as tar.xz; system tar avoids an archive chicken-and-egg.
        var tarPath = Path.Combine(workDirectory, "7z-linux.tar.xz");
        await DownloadFileAsync($"https://github.com/ip7z/7zip/releases/download/{tag}/7z{SevenZipVersion}-linux-x64.tar.xz",
            tarPath, p => Console.WriteLine($"Downloading 7-Zip ({p}%)..."));
        VerifySha256(tarPath, SevenZipSha256Linux);
        var tmp = Path.Combine(workDirectory, "7z-linux");
        Directory.CreateDirectory(tmp);
        await RunProcess("tar", $"-xf \"{tarPath}\" -C \"{tmp}\"");
        var dst = Path.Combine(installDirectory, plat.ArchiveTool);
        File.Copy(Path.Combine(tmp, "7zz"), dst, true);
        MakeExecutable(dst);
        return;
    }
    var downloadUrl = $"https://github.com/ip7z/7zip/releases/download/{tag}/7z{SevenZipVersion}-extra.7z";
    var targetPath = Path.Combine(workDirectory, "7z-extra.7z");
    await DownloadFileAsync(downloadUrl, targetPath, p => Console.WriteLine($"Downloading 7-Zip ({p}%)..."));
    VerifySha256(targetPath, SevenZipSha256);

    var targetExtractPath = Path.Combine(workDirectory, "7z-extra");
    Directory.CreateDirectory(targetExtractPath);
    using (ArchiveFile archiveFile = new(targetPath))
    {
        archiveFile.Extract(targetExtractPath);
    }
    File.Copy(Path.Combine(targetExtractPath, "x64", "7za.exe"), Path.Combine(installDirectory, "7za.exe"), true);
}

async Task InstallInferenceRuntime()
{
    Directory.CreateDirectory(inferencePath);
    var localDir = Environment.GetEnvironmentVariable("TRT_LOCAL_DIR");
    if (!string.IsNullOrEmpty(localDir))
    {
        Console.WriteLine($"Using local TensorRT runtime: {localDir}");
        foreach (var f in Directory.GetFiles(localDir).Where(f => (KeepInferenceFile(Path.GetFileName(f)) && !Path.GetFileName(f).StartsWith("ffmpeg") && !Path.GetFileName(f).StartsWith("DirectML")) || Path.GetFileName(f) == "TensorRT_Acknowledgements.txt"))
        {
            File.Copy(f, Path.Combine(inferencePath, Path.GetFileName(f)), true);
        }
        return;
    }
    if (!plat.IsWindows)
    {
        await InstallTrtLinux();
        return;
    }
    // NVIDIA's "-external" zip. Only bin/ ships; the headers, import libs, cmake/ and python/ are
    // build-time material for animejanai-inference.
    var trtDir = string.Join('.', TrtVersion.Split('.').Take(3));
    var asset = $"TensorRT-Enterprise-{TrtVersion}-Windows-amd64-cuda-{TrtCudaVersion}-Release-external.zip";
    var downloadUrl = $"https://developer.nvidia.com/downloads/compute/machine-learning/tensorrt/{trtDir}/zip/{asset}";

    var localZip = Environment.GetEnvironmentVariable("TRT_LOCAL_ZIP");
    string targetPath;
    if (!string.IsNullOrEmpty(localZip))
    {
        Console.WriteLine($"Using local TensorRT archive: {localZip}");
        targetPath = localZip;
    }
    else
    {
        Console.WriteLine($"Downloading TensorRT {TrtVersion} (CUDA {TrtCudaVersion}) from NVIDIA...");
        targetPath = Path.Combine(workDirectory, asset);
        double lastProgress = -1;
        await DownloadFileAsync(downloadUrl, targetPath, p =>
        {
            if (p >= lastProgress + 5) { Console.WriteLine($"Downloading TensorRT ({p}%)..."); lastProgress = p; }
        });
    }
    VerifySha256(targetPath, TrtSha256Win);

    Console.WriteLine("Extracting TensorRT runtime...");
    ExtractZipFiltered(targetPath, entry =>
    {
        var name = Path.GetFileName(entry);
        if (entry.Contains("/bin/", StringComparison.Ordinal) && KeepInferenceFile(name))
            return Path.Combine(inferencePath, name);
        // NVIDIA's own attribution file, kept beside the binaries it covers.
        if (name.Equals("Acknowledgements.txt", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(inferencePath, "TensorRT_Acknowledgements.txt");
        return null;
    });

    await InstallCudart();
}

// cudart is not part of the TensorRT archive; it comes from NVIDIA's CUDA redistributable.
async Task InstallCudart()
{
    var stem = $"cuda_cudart-windows-x86_64-{CudartVersion}-archive";
    var downloadUrl = $"https://developer.download.nvidia.com/compute/cuda/redist/cuda_cudart/windows-x86_64/{stem}.zip";
    var targetPath = Path.Combine(workDirectory, $"{stem}.zip");
    Console.WriteLine($"Downloading CUDA runtime {CudartVersion}...");
    await DownloadFileAsync(downloadUrl, targetPath, _ => { });
    VerifySha256(targetPath, CudartSha256Win);

    ExtractZipFiltered(targetPath, entry =>
    {
        var name = Path.GetFileName(entry);
        if (name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(inferencePath, "CUDA_LICENSE.txt");
        return KeepInferenceFile(name) ? Path.Combine(inferencePath, name) : null;
    });
}

// Linux: NVIDIA's apt packages for TensorRT, and the CUDA redistributable's cudart tarball. As in
// MangaJaNaiCLI's runtime packs, the libraries are stored under their sonames (what libaji_trt.so and
// trtexec ask for) rather than as symlink chains, and the builder resources keep their file names,
// which is how libnvinfer opens them.
async Task InstallTrtLinux()
{
    var root = Path.Combine(workDirectory, "debroot");
    Directory.CreateDirectory(root);
    var debsDir = Environment.GetEnvironmentVariable("TRT_DEBS_DIR");
    // libnvinfer11 also carries the per-SM builder resources; libnvinfer-bin is where trtexec lives.
    foreach (var (package, sha256) in trtDebSha256)
    {
        var deb = $"{package}_{TrtVersion}-1+cuda{TrtCudaVersion}_amd64.deb";
        var targetPath = !string.IsNullOrEmpty(debsDir) && File.Exists(Path.Combine(debsDir, deb))
            ? Path.Combine(debsDir, deb)
            : Path.Combine(workDirectory, deb);
        if (!File.Exists(targetPath))
        {
            Console.WriteLine($"Downloading {package} ({TrtVersion})...");
            double lastProgress = -1;
            await DownloadFileAsync($"{CudaAptRepo}/{deb}", targetPath, p =>
            {
                if (p >= lastProgress + 10) { Console.WriteLine($"  {package} ({p}%)..."); lastProgress = p; }
            });
        }
        VerifySha256(targetPath, sha256);
        await RunProcess("dpkg-deb", $"-x \"{targetPath}\" \"{root}\"");
    }

    var lib = Path.Combine(root, "usr", "lib", "x86_64-linux-gnu");
    foreach (var soname in new[] { "libnvinfer.so.11", "libnvinfer_plugin.so.11", "libnvonnxparser.so.11" })
    {
        CopyResolved(Path.Combine(lib, soname), Path.Combine(inferencePath, soname));
    }
    foreach (var f in Directory.GetFiles(lib, "libnvinfer_builder_resource_*"))
    {
        if (new FileInfo(f).LinkTarget is null)
        {
            File.Copy(f, Path.Combine(inferencePath, Path.GetFileName(f)), true);
        }
    }
    // libnvinfer-bin puts trtexec in /usr/bin (11.x) or /usr/src/tensorrt/bin (10.x).
    var trtexec = new[] { Path.Combine(root, "usr", "bin", "trtexec"), Path.Combine(root, "usr", "src", "tensorrt", "bin", "trtexec") }
        .First(File.Exists);
    CopyResolved(trtexec, Path.Combine(inferencePath, "trtexec"));
    MakeExecutable(Path.Combine(inferencePath, "trtexec"));
    var copyright = Path.Combine(root, "usr", "share", "doc", "libnvinfer11", "copyright");
    if (File.Exists(copyright))
    {
        File.Copy(copyright, Path.Combine(inferencePath, "TensorRT_Acknowledgements.txt"), true);
    }

    var stem = $"cuda_cudart-linux-x86_64-{CudartVersion}-archive";
    var cudart = Path.Combine(workDirectory, $"{stem}.tar.xz");
    Console.WriteLine($"Downloading CUDA runtime {CudartVersion}...");
    await DownloadFileAsync($"https://developer.download.nvidia.com/compute/cuda/redist/cuda_cudart/linux-x86_64/{stem}.tar.xz",
        cudart, _ => { });
    VerifySha256(cudart, CudartSha256Linux);
    var cudartDir = Path.Combine(workDirectory, "cudart");
    Directory.CreateDirectory(cudartDir);
    await RunProcess("tar", $"-xf \"{cudart}\" -C \"{cudartDir}\"");
    var real = Directory.GetFiles(Path.Combine(cudartDir, stem, "lib"))
        .First(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"^libcudart\.so\.\d+\.\d+\.\d+$"));
    File.Copy(real, Path.Combine(inferencePath, "libcudart.so.13"), true);
    File.Copy(Path.Combine(cudartDir, stem, "LICENSE"), Path.Combine(inferencePath, "CUDA_LICENSE.txt"), true);
}

// Which inference-runtime files from NVIDIA's archives (or TRT_LOCAL_DIR) actually ship.
bool KeepInferenceFile(string name) => plat.IsWindows
    ? name is "nvinfer_11.dll" or "nvinfer_plugin_11.dll" or "nvonnxparser_11.dll" or "trtexec.exe" ||
      name.StartsWith("cudart64_", StringComparison.OrdinalIgnoreCase) ||
      name.StartsWith("nvinfer_builder_resource_", StringComparison.OrdinalIgnoreCase) ||
      name.Contains("LICENSE", StringComparison.OrdinalIgnoreCase)
    : name is "libnvinfer.so.11" or "libnvinfer_plugin.so.11" or "libnvonnxparser.so.11" or "libcudart.so.13" or "trtexec" ||
      name.StartsWith("libnvinfer_builder_resource_", StringComparison.Ordinal) ||
      name.Contains("LICENSE", StringComparison.OrdinalIgnoreCase);

// The TensorRT-RTX backend's files (aji-trtrtx-windows-x64.zip): aji_trtrtx.dll, the TensorRT for RTX
// runtime (tensorrt_rtx_<M>_<m>.dll, tensorrt_onnxparser_rtx_<M>_<m>.dll) and NVIDIA's notices, which
// must ship with the DLLs. IsTrtRtxFile picks them out of the asset; the trt-rtx pack is all of them
// but aji_trtrtx.dll, which stays in the core (IsTrtRtxRuntimeFile).
static bool IsTrtRtxRuntimeFile(string name) =>
    IsTrtRtxFile(name) && !name.Equals("aji_trtrtx.dll", StringComparison.OrdinalIgnoreCase);

static bool IsTrtRtxFile(string name) =>
    name.Equals("aji_trtrtx.dll", StringComparison.OrdinalIgnoreCase) ||
    ((name.StartsWith("tensorrt_rtx_", StringComparison.OrdinalIgnoreCase) ||
      name.StartsWith("tensorrt_onnxparser_rtx_", StringComparison.OrdinalIgnoreCase)) &&
     name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ||
    (name.StartsWith("TensorRT-RTX-", StringComparison.OrdinalIgnoreCase) &&
     name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));

static void VerifySha256(string path, string expected)
{
    using var stream = File.OpenRead(path);
    var actual = Convert.ToHexString(SHA256.HashData(stream));
    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"SHA-256 mismatch for {Path.GetFileName(path)}{Environment.NewLine}" +
            $"  expected {expected}{Environment.NewLine}" +
            $"  actual   {actual.ToLowerInvariant()}");
    }
    Console.WriteLine($"  sha256 verified: {Path.GetFileName(path)}");
}

async Task InstallAji()
{
    Directory.CreateDirectory(inferencePath);

    var localDir = Environment.GetEnvironmentVariable("AJI_LOCAL_DIR");
    var localZip = Environment.GetEnvironmentVariable("AJI_LOCAL_ZIP");
    if (!plat.IsWindows)
    {
        // libaji.so + libaji_trt.so; the test tools in the tarball aren't used by the image backend.
        string dir;
        if (!string.IsNullOrEmpty(localDir))
        {
            Console.WriteLine($"Using local aji files: {localDir}");
            dir = localDir;
        }
        else
        {
            dir = Path.Combine(workDirectory, "aji");
            await ExtractTarZst(await DownloadAjiAsset("aji-linux-x64.tar.zst", AjiSha256Linux), dir);
        }
        foreach (var f in Directory.GetFiles(dir).Where(f => Path.GetFileName(f) is "libaji.so" or "libaji_trt.so" ||
                                                             Path.GetFileName(f).Contains("LICENSE", StringComparison.OrdinalIgnoreCase)))
        {
            File.Copy(f, Path.Combine(inferencePath, Path.GetFileName(f)), true);
        }
        return;
    }
    if (!string.IsNullOrEmpty(localDir))
    {
        Console.WriteLine($"Using local aji files: {localDir}");
        foreach (var f in Directory.GetFiles(localDir))
        {
            File.Copy(f, Path.Combine(inferencePath, Path.GetFileName(f)), true);
        }
    }
    else
    {
        string targetPath;
        if (!string.IsNullOrEmpty(localZip))
        {
            Console.WriteLine($"Using local aji build: {localZip}");
            targetPath = localZip;
        }
        else
        {
            targetPath = await DownloadAjiAsset("aji-windows-x64.zip", AjiSha256Win);
        }
        // Only the libraries: the video tools (aji_encode, harnesses) aren't used by the image backend.
        ExtractZipFiltered(targetPath, entry =>
        {
            var name = Path.GetFileName(entry);
            return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.Contains("LICENSE", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(inferencePath, name)
                : null;
        });
    }

    // The TensorRT-RTX backend, a separate asset. aji_trtrtx.dll must come from the same build as
    // aji.dll, so a local aji build takes it only from AJI_TRTRTX_LOCAL_ZIP (or its own directory).
    var rtxZip = Environment.GetEnvironmentVariable("AJI_TRTRTX_LOCAL_ZIP");
    if (!string.IsNullOrEmpty(rtxZip))
    {
        Console.WriteLine($"Using local aji TensorRT-RTX build: {rtxZip}");
    }
    else if (!string.IsNullOrEmpty(localDir) || !string.IsNullOrEmpty(localZip))
    {
        if (!File.Exists(Path.Combine(inferencePath, "aji_trtrtx.dll")))
        {
            Console.WriteLine("WARNING: local aji build and no AJI_TRTRTX_LOCAL_ZIP; TensorRT-RTX not bundled (no trt-rtx pack).");
        }
        return;
    }
    else
    {
        rtxZip = args.Contains("--store")
            ? await DownloadAjiAsset("aji-trtrtx-backend-windows-x64.zip", AjiTrtRtxBackendSha256Win)
            : await DownloadAjiAsset("aji-trtrtx-windows-x64.zip", AjiTrtRtxSha256Win);
    }
    // A store build keeps aji_trtrtx.dll only: its runtime is the store's trtrtx-runtime pack.
    ExtractZipFiltered(rtxZip, entry =>
    {
        var name = Path.GetFileName(entry);
        bool keep = args.Contains("--store") ? !IsTrtRtxRuntimeFile(name) && IsTrtRtxFile(name) : IsTrtRtxFile(name);
        return keep ? Path.Combine(inferencePath, name) : null;
    });
}

// aji-runtime(.exe), the shared pack store's tool, at the install root beside the updater, which runs it.
async Task InstallAjiRuntime()
{
    string exe = plat.Exe("aji-runtime");
    string dest = Path.Combine(installDirectory, exe);
    var local = Environment.GetEnvironmentVariable("AJI_RUNTIME_LOCAL");
    if (!string.IsNullOrEmpty(local))
    {
        Console.WriteLine($"Using local aji-runtime: {local}");
        File.Copy(local, dest, true);
    }
    else
    {
        string sha = plat.IsWindows ? AjiRuntimeSha256Win : AjiRuntimeSha256Linux;
        if (sha.Length == 0)
        {
            throw new InvalidOperationException($"No aji-runtime {AjiRuntimeVersion} pinned for {plat.Rid}; set AJI_RUNTIME_LOCAL.");
        }
        string asset = plat.IsWindows ? "aji-runtime-windows-x64.zip" : "aji-runtime-linux-x64.tar.gz";
        string archive = Path.Combine(workDirectory, asset);
        Console.WriteLine($"Downloading {asset} {AjiRuntimeVersion}...");
        await DownloadFileAsync($"https://github.com/{AjiRuntimeRepo}/releases/download/{AjiRuntimeVersion}/{asset}", archive, _ => { });
        VerifySha256(archive, sha);
        if (plat.IsWindows)
        {
            ExtractZipFiltered(archive, entry => Path.GetFileName(entry) == exe ? dest : null);
        }
        else
        {
            using var file = File.OpenRead(archive);
            using var gz = new GZipStream(file, CompressionMode.Decompress);
            using var tar = new TarReader(gz);
            while (tar.GetNextEntry() is TarEntry e)
            {
                if (Path.GetFileName(e.Name) == exe && e.DataStream is Stream data)
                {
                    using var o = File.Create(dest);
                    data.CopyTo(o);
                }
            }
        }
        if (!File.Exists(dest))
        {
            throw new InvalidOperationException($"{asset} has no {exe}");
        }
    }
    if (!plat.IsWindows)
    {
        MakeExecutable(dest);
    }
}

// One asset of the pinned animejanai-inference release, sha-verified. Returns the downloaded path.
async Task<string> DownloadAjiAsset(string asset, string sha256)
{
    Console.WriteLine($"Downloading {asset} {AjiVersion}...");
    var targetPath = Path.Combine(workDirectory, asset);
    double lastProgress = -1;
    await DownloadFileAsync($"https://github.com/the-database/animejanai-inference/releases/download/{AjiVersion}/{asset}",
        targetPath, p =>
        {
            if (p >= lastProgress + 10) { Console.WriteLine($"Downloading {asset} ({p}%)..."); lastProgress = p; }
        });
    VerifySha256(targetPath, sha256);
    return targetPath;
}

// ONNX Runtime + DirectML for the DirectML backend. They ship in the core rather than a component
// pack: DirectML is what non-NVIDIA GPUs run on, and those get no TensorRT packs. The .nupkg files
// are plain zips; only the x64 runtime DLLs (and the DirectML license, which the redistribution terms
// require keeping intact) go into the package.
async Task InstallOrtDml()
{
    Directory.CreateDirectory(inferencePath);
    var packages = new (string Name, string Version, string[] CopyFromTo)[]
    {
        ("Microsoft.ML.OnnxRuntime.DirectML", OrtDmlVersion, new[]
        {
            "runtimes/win-x64/native/onnxruntime.dll", "onnxruntime.dll",
        }),
        ("Microsoft.AI.DirectML", DirectMLVersion, new[]
        {
            "bin/x64-win/DirectML.dll", "DirectML.dll",
            "LICENSE.txt", "DirectML_LICENSE.txt",
        }),
    };
    foreach (var (name, pkgVersion, copies) in packages)
    {
        Console.WriteLine($"Downloading {name} {pkgVersion}...");
        var targetPath = Path.Combine(workDirectory, $"{name}.{pkgVersion}.nupkg");
        await DownloadFileAsync($"https://www.nuget.org/api/v2/package/{name}/{pkgVersion}", targetPath, _ => { });
        var tempDirectory = Path.Combine(workDirectory, $"{name}-temp");
        ExtractZip(targetPath, tempDirectory);
        for (var i = 0; i < copies.Length; i += 2)
        {
            File.Copy(Path.Combine(tempDirectory, copies[i].Replace('/', Path.DirectorySeparatorChar)),
                      Path.Combine(inferencePath, copies[i + 1]), true);
        }
    }
}

// libaji's DLLs link the MSVC runtime dynamically; ship it app-local (allowed by the VC++
// redistributable terms) so a machine without the VC++ Redistributable installed still loads them.
void InstallVcRuntime()
{
    var crtDir = Environment.GetEnvironmentVariable("VC_CRT_DIR");
    if (string.IsNullOrEmpty(crtDir))
    {
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(vswhere))
        {
            var psi = new ProcessStartInfo(vswhere,
                @"-latest -products * -find VC\Redist\MSVC\**\x64\Microsoft.VC14*.CRT\vcruntime140.dll")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi)!;
            // vswhere lists every installed redist (CI runners also carry VS 2019's 14.29), in no
            // useful order. Take the newest: the runtime must be at least as new as the toolset
            // libaji was linked with, and this app-local copy beside aji.dll wins over the system one.
            var found = p.StandardOutput.ReadToEnd()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !l.Contains(@"\onecore\", StringComparison.OrdinalIgnoreCase) && File.Exists(l))
                .MaxBy(l =>
                {
                    var v = FileVersionInfo.GetVersionInfo(l);
                    return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart);
                });
            p.WaitForExit();
            crtDir = found is null ? null : Path.GetDirectoryName(found);
        }
    }
    if (string.IsNullOrEmpty(crtDir) || !Directory.Exists(crtDir))
    {
        Console.WriteLine("WARNING: VC++ runtime not found (set VC_CRT_DIR); not bundled.");
        return;
    }
    Console.WriteLine($"Copying VC++ runtime {FileVersionInfo.GetVersionInfo(Path.Combine(crtDir, "vcruntime140.dll")).FileVersion} from {crtDir}...");
    foreach (var dll in Directory.GetFiles(crtDir, "*.dll"))
    {
        File.Copy(dll, Path.Combine(inferencePath, Path.GetFileName(dll)), true);
    }
}

// The upscaling backend executable.
async Task InstallBackendCli()
{
    if (!plat.IsWindows)
    {
        await InstallBackendCliLinux();
        return;
    }
    const string exe = "mangajanai_upscale.exe";
    var local = Environment.GetEnvironmentVariable("MANGAJANAI_CLI_LOCAL");
    string? archive = null;
    if (!string.IsNullOrEmpty(local))
    {
        Console.WriteLine($"Using local backend: {local}");
        if (File.Exists(local) && local.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(local, Path.Combine(inferencePath, exe), true);
            return;
        }
        if (Directory.Exists(local))
        {
            File.Copy(Path.Combine(local, exe), Path.Combine(inferencePath, exe), true);
            return;
        }
        archive = local;
    }
    else if (CliVersion.Length > 0)
    {
        Console.WriteLine($"Downloading {CliAsset} {CliVersion}...");
        archive = Path.Combine(workDirectory, CliAsset);
        await DownloadFileAsync($"https://github.com/{CliRepo}/releases/download/{CliVersion}/{CliAsset}",
            archive, p => Console.WriteLine($"Downloading {CliAsset} ({p}%)..."));
        VerifySha256(archive, CliSha256);
    }
    if (archive is null)
    {
        Console.WriteLine($"WARNING: no backend release pinned and MANGAJANAI_CLI_LOCAL unset; {exe} not bundled.");
        return;
    }
    ExtractZipFiltered(archive, entry =>
    {
        var name = Path.GetFileName(entry);
        return name.Equals(exe, StringComparison.OrdinalIgnoreCase) || name.Contains("LICENSE", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(inferencePath, name.Equals(exe, StringComparison.OrdinalIgnoreCase) ? exe : $"mangajanai_upscale_{name}")
            : null;
    });
}

// Linux: the CLI's own bundle; its `mangajanai` (or a local mangajanai_upscale) becomes
// inference/mangajanai_upscale.
async Task InstallBackendCliLinux()
{
    const string exe = "mangajanai_upscale";
    var dst = Path.Combine(inferencePath, exe);
    var local = Environment.GetEnvironmentVariable("MANGAJANAI_CLI_LOCAL");
    if (!string.IsNullOrEmpty(local) && File.Exists(local) && !local.EndsWith(".tar.zst", StringComparison.Ordinal))
    {
        Console.WriteLine($"Using local backend: {local}");
        File.Copy(local, dst, true);
        MakeExecutable(dst);
        return;
    }
    string dir;
    if (!string.IsNullOrEmpty(local) && Directory.Exists(local))
    {
        Console.WriteLine($"Using local backend: {local}");
        dir = local;
    }
    else
    {
        string archive;
        if (!string.IsNullOrEmpty(local))
        {
            Console.WriteLine($"Using local backend: {local}");
            archive = local;
        }
        else
        {
            Console.WriteLine($"Downloading {CliAssetLinux} {CliVersion}...");
            archive = Path.Combine(workDirectory, CliAssetLinux);
            await DownloadFileAsync($"https://github.com/{CliRepo}/releases/download/{CliVersion}/{CliAssetLinux}",
                archive, p => Console.WriteLine($"Downloading {CliAssetLinux} ({p}%)..."));
            VerifySha256(archive, CliSha256Linux);
        }
        dir = Path.Combine(workDirectory, "cli");
        await ExtractTarZst(archive, dir);
    }
    var src = new[] { exe, "mangajanai" }.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists)
        ?? throw new InvalidOperationException($"no mangajanai_upscale or mangajanai in {dir}");
    File.Copy(src, dst, true);
    MakeExecutable(dst);
    foreach (var license in Directory.GetFiles(dir).Where(f => Path.GetFileName(f).Contains("LICENSE", StringComparison.OrdinalIgnoreCase)))
    {
        File.Copy(license, Path.Combine(inferencePath, $"mangajanai_upscale_{Path.GetFileName(license)}"), true);
    }
}

// A .tar.zst, unpacked with the bundled 7zz (zstd -> tar) and .NET's tar reader, so the build needs no
// zstd program.
async Task ExtractTarZst(string archive, string outDir)
{
    var tmp = Path.Combine(workDirectory, Path.GetFileName(archive) + "-unzst");
    if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    Directory.CreateDirectory(tmp);
    await RunProcess(Path.Combine(installDirectory, plat.ArchiveTool), $"x \"{archive}\" -o\"{tmp}\" -y");
    var tar = Directory.GetFiles(tmp, "*.tar").Single();
    Directory.CreateDirectory(outDir);
    TarFile.ExtractToDirectory(tar, outDir, overwriteFiles: true);
    Directory.Delete(tmp, true);
}

// The default ONNX models, into models/ (split into the models-* component packs under --packs).
async Task InstallModels()
{
    Directory.CreateDirectory(modelsPath);
    var local = Environment.GetEnvironmentVariable("MANGAJANAI_MODELS_DIR");
    var archives = new List<string>();
    if (!string.IsNullOrEmpty(local))
    {
        Console.WriteLine($"Using local models: {local}");
        foreach (var onnx in Directory.GetFiles(local, "*.onnx"))
        {
            File.Copy(onnx, Path.Combine(modelsPath, Path.GetFileName(onnx)), true);
        }
        archives.AddRange(modelArchives.Select(a => Path.Combine(local, a.Asset)).Where(File.Exists));
    }
    else
    {
        foreach (var (_, tag, asset, sha256) in modelArchives)
        {
            Console.WriteLine($"Downloading {asset} ({tag})...");
            var targetPath = Path.Combine(workDirectory, asset);
            double lastProgress = -1;
            await DownloadFileAsync($"https://github.com/{ModelsRepo}/releases/download/{tag}/{asset}", targetPath, p =>
            {
                if (p >= lastProgress + 10) { Console.WriteLine($"Downloading {asset} ({p}%)..."); lastProgress = p; }
            });
            VerifySha256(targetPath, sha256);
            archives.Add(targetPath);
        }
    }
    foreach (var archive in archives)
    {
        Console.WriteLine($"Extracting {Path.GetFileName(archive)}...");
        // SevenZipExtractor wraps the Windows 7z.dll; on Linux the bundled 7zz console unpacks.
        if (plat.IsWindows)
        {
            using ArchiveFile archiveFile = new(archive);
            archiveFile.Extract(modelsPath);
        }
        else
        {
            await RunProcess(Path.Combine(installDirectory, plat.ArchiveTool), $"x \"{archive}\" -o\"{modelsPath}\" -y");
        }
    }

    // The naming rule, checked: every model is <checkpoint>_<fp16|bf16|fp32>, belongs to a pack, and
    // each bf16 variant has its fp32 counterpart (and the reverse), so every machine gets one of them.
    var names = Directory.GetFiles(modelsPath, "*.onnx").Select(f => Path.GetFileNameWithoutExtension(f)!).ToHashSet();
    foreach (var stem in names)
    {
        if (!(stem.EndsWith("_fp16", StringComparison.Ordinal) || stem.EndsWith("_bf16", StringComparison.Ordinal) ||
              stem.EndsWith("_fp32", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"model {stem}.onnx doesn't end in its precision (_fp16, _bf16 or _fp32)");
        }
        if (ModelPack(stem + ".onnx") is null)
        {
            throw new InvalidOperationException($"model {stem}.onnx belongs to no models pack");
        }
        var family = stem[..^5];
        if (stem.EndsWith("_bf16", StringComparison.Ordinal) && !names.Contains(family + "_fp32"))
        {
            throw new InvalidOperationException($"bf16 model {stem}.onnx has no fp32 counterpart for DirectML and older GPUs");
        }
        if (stem.EndsWith("_fp32", StringComparison.Ordinal) && !names.Contains(family + "_bf16"))
        {
            throw new InvalidOperationException($"fp32 model {stem}.onnx has no bf16 counterpart for newer NVIDIA GPUs");
        }
    }
}

// The TensorRT SLA requires this attribution when redistributing the runtime.
void WriteThirdPartyNotices()
{
    if (args.Contains("--store"))
    {
        File.WriteAllText(Path.Combine(inferencePath, "THIRD_PARTY_NOTICES.txt"),
            "Third-party components in this directory\n" +
            "========================================\n\n" +
            "The NVIDIA TensorRT runtime" + (plat.IsWindows ? ", the NVIDIA TensorRT for RTX runtime" : "") +
            " and the default models are not in this folder: they are installed into the shared pack\n" +
            "store (aji-runtime) from the-database/animejanai-inference runtime packs, and NVIDIA's\n" +
            "licence notices and acknowledgements ship in each pack.\n\n" +
            (plat.IsWindows
                ? "ONNX Runtime (onnxruntime.dll), (c) Microsoft Corporation, redistributed\n" +
                  "under the MIT license (https://github.com/microsoft/onnxruntime/blob/main/LICENSE).\n\n" +
                  "DirectML (DirectML.dll), (c) Microsoft Corporation, redistributed as the\n" +
                  "DirectML Redistributable Package under the Microsoft Software License Terms\n" +
                  "shipped alongside it as DirectML_LICENSE.txt (use on Windows and Xbox only).\n\n" +
                  "Microsoft Visual C++ runtime (msvcp140*.dll, vcruntime140*.dll, concrt140.dll),\n" +
                  "(c) Microsoft Corporation, redistributed under the Visual Studio redistributable terms.\n\n" +
                  "aji.dll / aji_trt.dll / aji_dml.dll / aji_trtrtx.dll:\n"
                : "libaji.so / libaji_trt.so:\n") +
            "https://github.com/the-database/animejanai-inference\n\n" +
            "aji-runtime" + (plat.IsWindows ? ".exe" : "") + " (the shared pack store's tool):\n" +
            "https://github.com/the-database/aji-runtime\n\n" +
            "mangajanai_upscale" + (plat.IsWindows ? ".exe" : "") + " (MangaJaNaiCLI, built on yosh-image):\n" +
            "https://github.com/the-database/MangaJaNaiCLI\n");
        return;
    }
    if (!plat.IsWindows)
    {
        File.WriteAllText(Path.Combine(inferencePath, "THIRD_PARTY_NOTICES.txt"),
            "Third-party components in this directory\n" +
            "========================================\n\n" +
            "NVIDIA TensorRT runtime (libnvinfer.so.11, libnvinfer_plugin.so.11, libnvonnxparser.so.11,\n" +
            "libnvinfer_builder_resource_*.so, trtexec) and NVIDIA CUDA runtime (libcudart.so.13),\n" +
            "redistributed under the NVIDIA TensorRT Software License Agreement and\n" +
            "CUDA Toolkit EULA:\n\n" +
            "    This software contains source code provided by NVIDIA Corporation.\n\n" +
            "These files are obtained from NVIDIA's TensorRT and CUDA Linux packages,\n" +
            "redistributed under the same terms. NVIDIA's own acknowledgements\n" +
            "accompany them as TensorRT_Acknowledgements.txt.\n\n" +
            "libaji.so / libaji_trt.so:\n" +
            "https://github.com/the-database/animejanai-inference\n\n" +
            "mangajanai_upscale (MangaJaNaiCLI, built on yosh-image):\n" +
            "https://github.com/the-database/MangaJaNaiCLI\n");
        return;
    }
    var notice =
        "Third-party components in this directory\n" +
        "========================================\n\n" +
        "NVIDIA TensorRT runtime (nvinfer_11.dll, nvinfer_plugin_11.dll, nvonnxparser_11.dll,\n" +
        "nvinfer_builder_resource_*.dll, trtexec.exe) and NVIDIA CUDA runtime (cudart64_*.dll),\n" +
        "redistributed under the NVIDIA TensorRT Software License Agreement and\n" +
        "CUDA Toolkit EULA:\n\n" +
        "    This software contains source code provided by NVIDIA Corporation.\n\n" +
        "These files are obtained from NVIDIA's redistributable TensorRT and CUDA\n" +
        "downloads, redistributed under the same terms. NVIDIA's own acknowledgements\n" +
        "accompany them as TensorRT_Acknowledgements.txt.\n\n" +
        "ONNX Runtime (onnxruntime.dll), (c) Microsoft Corporation, redistributed\n" +
        "under the MIT license (https://github.com/microsoft/onnxruntime/blob/main/LICENSE).\n\n" +
        "DirectML (DirectML.dll), (c) Microsoft Corporation, redistributed as the\n" +
        "DirectML Redistributable Package under the Microsoft Software License Terms\n" +
        "shipped alongside it as DirectML_LICENSE.txt (use on Windows and Xbox only).\n\n" +
        "Microsoft Visual C++ runtime (msvcp140*.dll, vcruntime140*.dll, concrt140.dll),\n" +
        "(c) Microsoft Corporation, redistributed under the Visual Studio redistributable terms.\n\n" +
        "NVIDIA TensorRT for RTX runtime (tensorrt_rtx_*.dll, tensorrt_onnxparser_rtx_*.dll), only\n" +
        "with the optional TensorRT-RTX component: NVIDIA proprietary software, redistributed under the\n" +
        "NVIDIA TensorRT for RTX Software License Agreement. Its notice and NVIDIA's acknowledgements\n" +
        "of the third-party software it includes ship with it as TensorRT-RTX-NOTICE.txt and\n" +
        "TensorRT-RTX-Acknowledgements.txt.\n\n" +
        "aji.dll / aji_trt.dll / aji_dml.dll / aji_trtrtx.dll:\n" +
        "https://github.com/the-database/animejanai-inference\n\n" +
        "mangajanai_upscale.exe (MangaJaNaiCLI, built on yosh-image):\n" +
        "https://github.com/the-database/MangaJaNaiCLI\n";
    File.WriteAllText(Path.Combine(inferencePath, "THIRD_PARTY_NOTICES.txt"), notice);
}

// version.txt + manifest.json at the install root. The updater reads these to know the installed
// version, which files the previous version shipped (to prune ones a new version drops), which paths
// to preserve, and the dep versions that tell whether an installed component pack is still current.
// Written last, after the packs are carved out, so "files" lists the core only.
void WriteVersionAndManifest()
{
    File.WriteAllText(Path.Combine(installDirectory, "version.txt"), version);

    var files = Directory.GetFiles(installDirectory, "*", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(installDirectory, f).Replace('\\', '/'))
        .Append("manifest.json")
        .Distinct()
        .Order(StringComparer.Ordinal)
        .ToArray();

    var manifest = new
    {
        package_version = version,
        app_executable = plat.Exe(AppName),
        archive_tool = plat.ArchiveTool,
        platform = plat.Rid,
        // The versions that govern each component pack's content; bumping one makes the updater
        // reinstall that pack.
        deps = new
        {
            aji = AjiVersion,            // the trt-rtx pack's: aji_trtrtx.dll must match aji.dll
            inference_runtime = $"trt-{TrtVersion}",
            ort_dml = plat.IsWindows ? $"{OrtDmlVersion}+{DirectMLVersion}" : null,
            backend = CliVersion,
            // a pack's content changes only when its archive does
            models_mangajanai = ModelDep("models-mangajanai"),
            models_illustrationjanai = ModelDep("models-illustrationjanai"),
            models_illustrationjanai_fp32 = ModelDep("models-illustrationjanai-fp32"),
            models_illustrationjanai_bf16 = ModelDep("models-illustrationjanai-bf16"),
            sevenzip = SevenZipVersion,
            aji_runtime = AjiRuntimeVersion,
            runtime = RuntimeTag,
        },
        // The shared runtime packs (the updater's store mode): this platform's index and its pin.
        runtime = new
        {
            tag = RuntimeTag,
            index_url = $"https://github.com/{RuntimeRepo}/releases/download/{RuntimeTag}/packs-{(plat.IsWindows ? "windows" : "linux")}-x64.json",
            index_sha256 = plat.IsWindows ? RuntimeIndexSha256Win : RuntimeIndexSha256Linux,
        },
        // Runtime-created data in the install folder that an update must never touch:
        // aji-runtime.json is written by the store for this install.
        user_preserve = new[] { "components.json", "inference/aji-runtime.json" },
        files,
    };

    var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(Path.Combine(installDirectory, "manifest.json"), json);
}

// The full package the updater downloads: the install tree, rooted at its versioned folder name.
void WriteFullPackage()
{
    if (!plat.IsWindows)
    {
        // A tarball keeps the executables' modes, and .NET reads it back without a zstd program. Users
        // download it too (install.sh is inside).
        var tarball = Path.Combine(outDirectory, $"{ReleaseName}-v{version}-{plat.Rid}.tar.gz");
        Console.WriteLine($"Writing {Path.GetFileName(tarball)}...");
        using (var file = File.Create(tarball))
        using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        {
            TarFile.CreateFromDirectory(installDirectory, gzip, includeBaseDirectory: true);
        }
        Console.WriteLine($"  {new FileInfo(tarball).Length / 1048576} MB");
        return;
    }
    var zip = Path.Combine(outDirectory, $"{ReleaseName}-full-package-{version}.zip");
    if (File.Exists(zip)) File.Delete(zip);
    Console.WriteLine($"Writing {Path.GetFileName(zip)}...");
    System.IO.Compression.ZipFile.CreateFromDirectory(installDirectory, zip, CompressionLevel.Optimal, includeBaseDirectory: true);
    Console.WriteLine($"  {new FileInfo(zip).Length / 1048576} MB");
}

// Selective zip extraction: mapEntry returns the destination for an entry, or null to skip it. Used
// for the TensorRT and CUDA archives, where only a handful of files out of ~2.5 GB are wanted.
static void ExtractZipFiltered(string archivePath, Func<string, string?> mapEntry)
{
    using var fsInput = File.OpenRead(archivePath);
    using var zf = new ICSharpCode.SharpZipLib.Zip.ZipFile(fsInput);
    var buffer = new byte[81920];
    foreach (ZipEntry zipEntry in zf)
    {
        if (!zipEntry.IsFile) continue;
        var destination = mapEntry(zipEntry.Name);
        if (destination == null) continue;
        var directoryName = Path.GetDirectoryName(destination);
        if (directoryName?.Length > 0) Directory.CreateDirectory(directoryName);
        Console.WriteLine($"  {Path.GetFileName(destination)}");
        using var zipStream = zf.GetInputStream(zipEntry);
        using Stream fsOutput = File.Create(destination);
        StreamUtils.Copy(zipStream, fsOutput, buffer);
    }
}

static void ExtractZip(string archivePath, string outFolder) =>
    ExtractZipFiltered(archivePath, entry => Path.Combine(outFolder, entry));

static void CopyDirectory(string srcDir, string targetDir)
{
    Directory.CreateDirectory(targetDir);
    foreach (string file in Directory.GetFiles(srcDir))
    {
        File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), true);
    }
    foreach (string subDir in Directory.GetDirectories(srcDir))
    {
        CopyDirectory(subDir, Path.Combine(targetDir, Path.GetFileName(subDir)));
    }
}

// The released full package is the slim core: the TensorRT runtime, per-GPU builder resources and
// the models ship only as component packs, installed on demand. Keeps the base download small.
void SlimInstallTree(List<string> packFiles)
{
    Console.WriteLine("Slimming install tree (component packs ship separately)...");
    long removed = 0;
    foreach (var rel in packFiles)
    {
        var abs = Path.Combine(installDirectory, rel);
        if (!File.Exists(abs)) continue;
        removed += new FileInfo(abs).Length;
        File.Delete(abs);
    }
    foreach (var dir in Directory.GetDirectories(installDirectory, "*", SearchOption.AllDirectories)
                                 .OrderByDescending(d => d.Length))
    {
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }
    Console.WriteLine($"Slimmed {removed / 1048576} MB out of the package tree.");
}

// Component packs: rooted 7z archives + a packs.json index, downloaded on demand by the updater /
// in-app manager. Archive paths are relative to the install root, so extraction over an install IS
// installation. Returns the expanded per-file list packed (so --packs can strip them from the core).
async Task<List<string>> EmitComponentPacks()
{
    Console.WriteLine("Emitting component packs...");
    var packsDir = Path.Combine(outDirectory, $"packs-v{version}");
    if (Directory.Exists(packsDir)) Directory.Delete(packsDir, true);
    Directory.CreateDirectory(packsDir);
    var sevenZ = Path.Combine(installDirectory, plat.ArchiveTool);

    string[] Rel(IEnumerable<string> paths) => paths.Select(f => Path.GetRelativePath(installDirectory, f)).ToArray();
    string[] ModelFiles(string pack) => Directory.Exists(modelsPath)
        ? Rel(Directory.GetFiles(modelsPath, "*.onnx").Where(f => ModelPack(Path.GetFileName(f)) == pack))
        : [];

    // Dep = the manifest.json deps key that governs a pack's content, so the updater can skip an
    // already-present pack whose dep is unchanged across releases.
    var packs = new List<(string Name, string Dep, string[] Files)>
    {
        ("trt-runtime", "inference_runtime", Rel(Directory.GetFiles(inferencePath).Where(f =>
        {
            var n = Path.GetFileName(f);
            bool builderResource = n.Contains("builder_resource");
            bool runtime = n.StartsWith("nvinfer") || n.StartsWith("libnvinfer") ||
                           n.StartsWith("nvonnxparser") || n.StartsWith("libnvonnxparser") ||
                           n.StartsWith("cudart64_") || n.StartsWith("libcudart") ||
                           n == "trtexec.exe" || n == "trtexec" ||
                           n is "CUDA_LICENSE.txt" or "TensorRT_Acknowledgements.txt";
            return runtime && !builderResource;
        }))),
        ("models-mangajanai", "models_mangajanai", ModelFiles("models-mangajanai")),
        ("models-illustrationjanai", "models_illustrationjanai", ModelFiles("models-illustrationjanai")),
        ("models-illustrationjanai-fp32", "models_illustrationjanai_fp32", ModelFiles("models-illustrationjanai-fp32")),
        ("models-illustrationjanai-bf16", "models_illustrationjanai_bf16", ModelFiles("models-illustrationjanai-bf16")),
    };
    foreach (var f in Directory.GetFiles(inferencePath, "*builder_resource_*"))
    {
        // nvinfer_builder_resource_sm120_11.dll / libnvinfer_builder_resource_sm120.so.11.4.0 -> trt-sm120
        var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"builder_resource_([a-z0-9]+)[._]");
        if (m.Success)
        {
            packs.Add(($"trt-{m.Groups[1].Value}", "inference_runtime", Rel([f])));
        }
    }
    // The TensorRT-RTX backend (recommended on RTX 20 / GTX 16 series and newer, see the updater). Its dep is
    // aji: aji_trtrtx.dll has to match aji.dll, so the pack goes stale with every aji bump. It runs
    // on trt-runtime's cudart, and its fallback to TensorRT needs trt-runtime too.
    packs.Add(("trt-rtx", "aji", Rel(Directory.GetFiles(inferencePath).Where(f => IsTrtRtxRuntimeFile(Path.GetFileName(f))))));

    var index = new List<object>();
    var packedFiles = new List<string>();
    foreach (var (name, dep, files) in packs)
    {
        if (files.Length == 0)
        {
            Console.WriteLine($"  component-{name}: nothing to pack in this tree, skipped");
            continue;
        }
        var archive = Path.Combine(packsDir, $"component-{name}{plat.PackSuffix}.7z");
        var fileArgs = string.Join(' ', files.Select(f => $"\"{f}\""));
        var psi = new ProcessStartInfo
        {
            FileName = sevenZ,
            // -spf2: store the relative paths as given
            Arguments = $"a -spf2 -mx=3 \"{archive}\" {fileArgs}",
            WorkingDirectory = installDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using (var proc = Process.Start(psi)!)
        {
            // drain both pipes: a full one would stall the archiver
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0) throw new InvalidOperationException($"{plat.ArchiveTool} failed for pack {name}: {await stderr}");
            await stdout;
        }

        var normalized = files.Select(f => f.Replace('\\', '/')).ToArray();
        // installed_bytes: what this pack occupies once extracted. The updater compares it against the
        // files on disk to decide whether an install is genuinely current, instead of trusting
        // components.json alone.
        long installedBytes = files.Sum(f => new FileInfo(Path.Combine(installDirectory, f)).Length);
        index.Add(new
        {
            name,
            dep,
            asset = Path.GetFileName(archive),
            bytes = new FileInfo(archive).Length,
            installed_bytes = installedBytes,
            files = normalized,
        });
        packedFiles.AddRange(normalized);
        Console.WriteLine($"  {Path.GetFileName(archive)} ({new FileInfo(archive).Length / 1048576} MB, {files.Length} files)");
    }
    File.WriteAllText(Path.Combine(packsDir, $"packs{plat.PackSuffix}.json"),
        JsonSerializer.Serialize(new { package_version = version, packs = index },
            new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Packs written to {packsDir}");
    return packedFiles;
}

if (Directory.Exists(installDirectory)) Directory.Delete(installDirectory, true);
if (Directory.Exists(workDirectory)) Directory.Delete(workDirectory, true);
Directory.CreateDirectory(installDirectory);
Directory.CreateDirectory(workDirectory);

// The published app (self-contained for the target RID) forms the install root.
var appDir = ArgValue("--app-dir") ?? throw new ArgumentException("--app-dir <published app> is required.");
Console.WriteLine($"Copying {AppName} from {appDir}...");
CopyDirectory(Path.GetFullPath(appDir), installDirectory);
// Debug symbols (SkiaSharp's alone are ~100 MB) aren't useful to users.
foreach (var pdb in Directory.GetFiles(installDirectory, "*.pdb", SearchOption.AllDirectories)) File.Delete(pdb);

bool storeBuild = args.Contains("--store");
if (storeBuild && args.Contains("--packs"))
{
    throw new ArgumentException("--store builds the core alone; it takes no --packs.");
}

await InstallSevenZip();
if (!storeBuild)
{
    await InstallInferenceRuntime();
}
await InstallAji();
if (plat.IsWindows)
{
    await InstallOrtDml();
    InstallVcRuntime();
}
await InstallBackendCli();
await InstallAjiRuntime();
if (!storeBuild)
{
    await InstallModels();
}

// The custom updater ships at the install root next to the app.
var updater = ArgValue("--updater") ?? throw new ArgumentException($"--updater <{plat.Exe("MangaJaNaiUpdater")}> is required.");
var updaterPath = Path.Combine(installDirectory, plat.Exe("MangaJaNaiUpdater"));
File.Copy(updater, updaterPath, true);
if (!plat.IsWindows)
{
    MakeExecutable(updaterPath);
    MakeExecutable(Path.Combine(installDirectory, AppName));
}

WriteThirdPartyNotices();

if (args.Contains("--packs"))
{
    SlimInstallTree(await EmitComponentPacks());
}

WriteVersionAndManifest();
WriteFullPackage();
Directory.Delete(workDirectory, true);
Console.WriteLine($"Built {installDirectory}");

// Copy a file, following a symlink (chain) to the file it names.
static void CopyResolved(string src, string dst)
{
    var target = new FileInfo(src).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? src;
    File.Copy(target, dst, true);
}

static void MakeExecutable(string path)
{
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) |
            UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }
}

static async Task RunProcess(string fileName, string arguments)
{
    using var process = Process.Start(new ProcessStartInfo
    {
        FileName = fileName,
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    })!;
    // drain both pipes: a full one would stall the child
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    await stdout;
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"{fileName} failed (exit {process.ExitCode}): {await stderr}");
    }
}

// ---------------------------------------------------------------------------
// Platform descriptor: every platform-varying name in one place.
// ---------------------------------------------------------------------------
record Platform(bool IsWindows, string Rid, string ArchiveTool)
{
    // "MangaJaNaiUpdater" -> "MangaJaNaiUpdater.exe" / "MangaJaNaiUpdater"
    public string Exe(string stem) => IsWindows ? stem + ".exe" : stem;
    // Release-asset suffix so Windows and Linux assets share one release (Windows keeps its unsuffixed
    // names, which installed updaters look for).
    public string PackSuffix => IsWindows ? "" : "-" + Rid;

    public static readonly Platform Win = new(IsWindows: true, Rid: "win-x64", ArchiveTool: "7za.exe");
    public static readonly Platform Linux = new(IsWindows: false, Rid: "linux-x64", ArchiveTool: "7zz");
}
