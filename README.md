# QuickShot

QuickShot is a tiny Windows screenshot utility built for games and ordinary
desktop windows. It runs in the system tray, captures the complete virtual
desktop with **F11**, and lets you choose the screenshot folder.

## Features

- Global F11 capture while a game or other window has focus
- Multi-monitor screenshots saved as PNG
- Selectable output folder with the choice remembered between launches
- System-tray operation with Capture, Open, and Exit commands
- Key-repeat protection so one press creates one screenshot
- Optional 4× enhancement: AMD FSR 1, NVIDIA Image Scaling, waifu2x, Real-ESRGAN
- No telemetry, network access, accounts, or background services

## Download

Download `QuickShot-win-x64.zip` from the [latest release](../../releases/latest),
extract the whole ZIP, and run `QuickShot.exe`. Keep the `engines` folder beside
the EXE: it contains the local AI tools and models. Moving only the EXE keeps
FSR and NIS available but prevents AI processing.

QuickShot requires the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0).

## Use

1. Run `QuickShot.exe` and approve the Windows administrator prompt. Elevated
   access allows the F11 listener to work when an elevated game has focus.
2. Select **Choose Folder** if you want a custom destination.
3. Minimize QuickShot to the system tray.
4. Press **F11** to capture the complete desktop.

By default, screenshots are stored in a `Screenshots` folder beside the EXE.

### Optional 4× screenshots

Select a mode under **Upscaling**. Every capture first saves its original
PNG, then creates a second file in the same folder:

| Mode | Output suffix | Processing |
|---|---|---|
| AMD FSR 1 — 4× | `_FSR4x.png` | EASU enlargement and gentle RCAS sharpening |
| NVIDIA Image Scaling — 4× | `_NIS4x.png` | Two 2× NIS passes, low final sharpening |
| waifu2x — 4× (AI) | `_waifu2x4x.png` | cunet model, denoising disabled |
| Real-ESRGAN — 4× (AI) | `_RealESRGAN4x.png` | General-image realesrgan-x4plus model |

For example, a 3440×1440 screenshot produces a 13760×5760 copy. The choice is
remembered between launches; it defaults to **Off**.

4× means four times the width and height (16 times the pixels). Previous 2×
preferences reset to Off; select 4× explicitly. The larger image uses more
memory and disk space and cannot recover missing detail. Fine text and edges
may still show enlargement artifacts.

Enhancement runs in the background, with one active image and one waiting.
If both slots are occupied, another capture still saves its original and
reports that the 4× copy was skipped. Closing QuickShot cancels pending
enhancements; originals already saved remain available.

FSR and NIS are spatial filters. The AI modes can change textures and text;
always keep the original for an accurate record. None of these modes increases
game FPS or guarantees native-resolution detail. NIS works on compatible AMD
hardware too; it is a separate NVIDIA algorithm from DLSS.

Everything runs locally without screenshot uploads, servers, accounts or
per-image fees. AI uses a hidden Vulkan process only during enhancement.
An AI job has a 15-minute timeout and is stopped on exit. All modes share one
active slot and one waiting slot, with each job retaining its capture-time
mode and destination. AI can compete with games for GPU resources.

## Build from source

Requirements: Windows and the .NET 9 SDK.

```powershell
dotnet build .\QuickShot.csproj -c Release
dotnet publish .\QuickShot.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:DebugSymbols=false -p:DebugType=None
```

Run the focused Windows tests, including actual GPU shader execution:

```powershell
dotnet run --project .\Tests\QuickShot.Tests.csproj -c Release -- --gpu
# Fixed 3440×1440 fixture, two warmups and ten measured GPU runs:
dotnet run --project .\Tests\QuickShot.Tests.csproj -c Release -- --benchmark
```

To install the pinned AI engines/models when building from source and create
the complete Windows package (PowerShell 7 recommended):

```powershell
.\scripts\Install-Engines.ps1
$env:QUICKSHOT_ENGINE_ROOT = "$PWD\engines"
dotnet run --project .\Tests\QuickShot.Tests.csproj -c Release -- --gpu --ai
.\scripts\Publish-Windows.ps1
```

The setup script downloads about 81 MB from the upstream GitHub releases and
verifies pinned archive and file SHA256 hashes. The packaged app needs no
downloads at runtime. Only the selected models are included. Neither SUPIR
nor diffusion models are included.

The shaders and their license notices are embedded in the published EXE.
The app needs no shader files, model downloads, or network access at runtime.

## Compatibility notes

QuickShot captures the Windows desktop image. Protected video, secure Windows
screens, and some true exclusive-fullscreen or anti-cheat-protected games can
block normal desktop capture. Borderless or windowed mode is the most broadly
compatible option in those cases.

The administrator requirement is declared in `app.manifest`. The hotkey uses a
low-level keyboard hook plus physical-key polling; all processing remains local.

Upscaling supports the existing SDR screenshot path. FSR and NIS require a
hardware Direct3D 11 feature-level 11.0 device; AI requires a compatible Vulkan
driver. AMD, NVIDIA and Intel hardware can meet those requirements; only
hardware actually tested is covered by test evidence.
Enhanced output is limited to 16,384 pixels on either side and 80 million
pixels total. Unsupported sizes or GPU failures leave the original intact.
Native HDR capture and HDR reconstruction are not supported.

## License

[MIT](LICENSE)

Third-party code and bindings are identified in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
