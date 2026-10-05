# QuickShot test report

Test date: **5 October 2026**. GPU: **AMD Radeon RX 7900 XTX**.

## What changed

- `Program.cs` adds the upscaling choices and shows save or error messages.
- `EnhancementSettings.cs` remembers the mode and gives each extra copy its own file name.
- `EnhancementWorker.cs` keeps the mode and folder chosen when each capture was taken. It allows one active job and one waiting job.
- `ExternalAiUpscaler.cs` runs the local waifu2x and Real-ESRGAN tools. It stops them on cancellation or after 15 minutes, checks the output size and cleans up temporary files.
- `NisUpscaler.cs` runs two 2× NIS passes to make a 4× image.
- `Fsr1Upscaler.cs` runs FSR enlargement and gentle sharpening. `GpuBitmap.cs` shares the image upload and download code.
- `Tests/` checks these paths. The scripts download checked engine files and build the full Windows ZIP.

## Test results

The latest local run passed **22 tests, with 0 failures and exit code 0**.
[Read the captured output](docs/evidence/gpu-ai-tests.txt).

```powershell
dotnet run --project Tests/QuickShot.Tests.csproj -c Release -- --gpu --ai
```

The tests cover saved preferences, old folder settings, file names, output
limits, full queues, cancellation and errors that must leave originals safe.
They also run the real FSR and NIS shaders and both AI tools. Image checks
cover colors, direction, edges, odd sizes and opaque output.

The Release build passed with **0 warnings and 0 errors**. Single-file publish
passed. Tests using the engines from the extracted ZIP also passed all 22 checks.

[GitHub run for commit f354309](https://github.com/venkey123456789/QuickShot/actions/runs/37285831283)
passed its Release build, 15 basic tests and single-file publish. GitHub does
not run the GPU tests. It reported an action-runtime deprecation notice; the
job still passed.

## Checks in the installed app

An elevated test helper used the real app controls:

| Mode | Capture method | Result |
|---|---|---|
| waifu2x | Screenshot Now button | Passed |
| Real-ESRGAN | F11 while minimized | Passed |
| NIS | Screenshot now in the tray menu | Passed |

Each created a **3440 × 1440** original and a **13760 × 5760** extra copy.
[Read the original log](docs/evidence/installed-ui-results.txt).

The first tray attempts failed because of a hidden taskbar and a helper-script
error. The helper was fixed and the real tray capture then passed. No app code
was changed for the retry. These were earlier installed-app checks; the current
documentation update did not rerun them.

The installed EXE and the EXE in the release ZIP have this SHA256:

```text
CCC5600C6F048F69B041A27F0766A7339F32FFC7887E0FE5B89864C5708E884C
```

The extracted package also matched all 11 checked engine and model files.

## Measured times

One cold run for each new mode, using the same 3440 × 1440 game image:

| Mode | Processing | Including final PNG save |
|---|---:|---:|
| NIS | 0.95 seconds | 3.16 seconds |
| waifu2x | 6.56 seconds | 9.11 seconds |
| Real-ESRGAN | 24.47 seconds | 28.37 seconds |

[Raw benchmark output](docs/evidence/full-benchmark.txt).
These are single measurements, not averages. AI processing includes temporary
PNG reads and writes. Output size was 13760 × 5760 for all three.

An earlier FSR-only test used a generated 3440 × 1440 pattern. After two warmups,
10 runs gave a median processing time of 214.2 ms. The first call took 365.4 ms.
Saving both PNGs took a median of 1073.4 ms. Peak process memory was 1068.9 MiB;
this was not a measurement of GPU memory. The input and test method differ from
the table above, so the numbers are not a direct speed comparison.

## Image quality

[View the image comparison and check results](docs/IMAGE-EXAMPLES.md).
The owner chose the existing `.work/ai/comparison.png` for publication.
It is included unchanged. No pictures from the `Screenshots` folder were added.

NIS keeps more of the source texture in this crop. waifu2x smooths edges, and
Real-ESRGAN smooths and changes fine textures. These results do not prove that
missing detail was recovered or that one mode is best for every image.

## Versions used

- FSR 1 v1.0.2: commit `a21ffb8f6c13233ba336352bdff293894c706575`. One EASU pass, then RCAS sharpening at 1.0 stop.
- NVIDIA NIS 1.0.3: commit `35e13ba316c98eeecf16f37eae70ce88019911f6`. Two 2× passes.
- waifu2x-ncnn-vulkan 20250915: cunet scale model, noise removal off.
- Real-ESRGAN Windows package 20220424 from v0.2.5.0: realesrgan-x4plus model.

`scripts/engines.lock.json` lists the download and file hashes. The ZIP includes
the tools, selected models and license notices. The app runs locally.

## What has not been checked

Other GPU models and vendors, driver removal and HDR have not been tested.
These timings do not measure game FPS. Running AI while gaming can affect
performance. No claim of native-detail recovery is made.

The earlier 2× mode was replaced by 4×. Old 2× settings load as Off. Output is
limited to 16,384 pixels per side and 80 million pixels in total. Originals
are always kept. No subagents were used for the AI or NIS additions.
