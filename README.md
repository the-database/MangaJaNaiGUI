# <img src="logo.png" width="24"></img> MangaJaNai GUI
[![Discord](https://img.shields.io/discord/1121653618173546546?label=Discord&logo=Discord&logoColor=white)](https://discord.gg/EeFfZUBvxj)

## Overview
Formerly MangaJaNaiConverterGui. This project provides a Windows and Linux GUI for upscaling manga images and archives with ONNX models, running on TensorRT-RTX, TensorRT or DirectML (TensorRT on Linux) through [animejanai-inference](https://github.com/the-database/animejanai-inference). It includes a set of models optimized for upscaling manga with Japanese and English text, but many other models are also supported. It also utilizes linear light downscaling to minimize halftone artifacts when downscaling. 

![image](https://github.com/the-database/MangaJaNaiConverterGui/assets/25811902/89095677-5b1f-46c9-9a1d-3d9df80cefe8)


## Instructions
Download and run `MangaJaNaiGUI-Setup-<version>.exe` from the [latest release](https://github.com/the-database/MangaJaNaiGUI/releases/latest). It installs for your user only, so no administrator rights are needed. Setup downloads the upscaling components for your GPU and the default models. Then select your input file or folder, choose your upscale settings, and click Upscale.

With **Auto Update** on (App Settings), updates download in the background and install when you close the app.

### Linux
Download either the AppImage or the `linux-x64.tar.gz` from the [latest release](https://github.com/the-database/MangaJaNaiGUI/releases/latest). An NVIDIA GPU and driver are required; Linux runs the TensorRT backend only.
- **Tarball**: extract it anywhere and run `./install.sh` from inside the extracted folder. This adds a menu entry and installs the components for your GPU. The folder stays in place and updates itself in-app.
- **AppImage**: make it executable and run it. Run it once with `--install` to add a menu entry. The TensorRT kernels for your GPU download on first launch into `~/.local/share/MangaJaNaiGUI`.

Settings, your models and TensorRT engines are kept in `~/.config/MangaJaNaiConverterGui`.

### Requirements
- An NVIDIA RTX GPU for the **TensorRT-RTX** and **TensorRT** backends (fastest), or any DirectX 12 GPU (NVIDIA, AMD, Intel) for the **DirectML** backend. Choose one in App Settings; **Auto** picks TensorRT-RTX when its component is installed, else TensorRT when it can run, else DirectML.
- TensorRT builds an engine for each model and tile size the first time it is used, which takes a few minutes. Later upscales reuse it.
- **TensorRT-RTX** builds its engines in seconds instead, and upscales faster. Setup installs it on GeForce RTX 20 / GTX 16 series and newer GPUs (on other NVIDIA GPUs you can add it in App Settings > Components). Its output is equivalent to TensorRT's but not bit-identical, and it falls back to TensorRT when it can't start or can't build a model's engine, with a warning in the status bar.
- There's no CPU backend.

Keep your NVIDIA drivers up to date.

### Custom models
Put your own models in the models folder (App Settings > Models > Open Models Folder), then pick them in a chain. 2.0 runs ONNX models. Convert PyTorch models (`.pth`, `.safetensors`) to ONNX first: see [tools/onnx-export](tools/onnx-export), which also says which precision runs fastest on your GPU (fp32 is the slowest). Each chain shows its model's precision and says so when a faster one would run.

### Upgrading from 1.x
Version 2.0 uses a new installer and doesn't update itself from 1.x. Install 2.0 with the setup above: it removes the 1.x install, its Python backend and its default PyTorch models, and keeps your workflows and the models you added. Workflows that use the default models switch to their ONNX versions automatically. Don't uninstall 1.x first; its uninstaller deletes the models folder, including any models you added.

### Command line
The upscaling backend, `inference\mangajanai_upscale.exe` (`inference/mangajanai_upscale` on Linux), also runs on its own: `mangajanai_upscale --help`. For a full command line with its own settings file, see [MangaJaNaiCLI](https://github.com/the-database/MangaJaNaiCLI), the project this backend comes from.


## Resources
- [OpenModelDB](https://openmodeldb.info/): Repository of AI upscaling models.

## Related Projects
- [MangaJaNai](https://github.com/the-database/MangaJaNai): Main repository for manga upscaling models.
- [MangaJaNaiCLI](https://github.com/the-database/MangaJaNaiCLI): The same upscaling from the command line, on Windows and Linux
- [mpv-AnimeJaNai](https://github.com/the-database/mpv-AnimeJaNai): Real-time anime upscaling in mpv
- [VideoJaNai](https://github.com/the-database/VideoJaNai): Upscale video files
- [yosh](https://github.com/the-database/yosh): Fast manga, comic and image reader
- [traiNNer-redux](https://github.com/the-database/traiNNer-redux): Train your own upscaling models

## Acknowledgments 
- [chaiNNer](https://github.com/chaiNNer-org/chaiNNer): General purpose tool for AI upscaling. MangaJaNai GUI 1.x used a modified version of its backend for running upscaling models.
