# QuickShot enhancement verification — 2026-10-05

## Added local AI and NIS modes

Program.cs, EnhancementSettings.cs and EnhancementWorker.cs now preserve the
selected mode with each queued job and use distinct companion filenames.
ExternalAiUpscaler.cs invokes pinned NCNN Vulkan tools with separate arguments,
hidden windows, bounded diagnostic capture, cancellation, process-tree cleanup,
a 15-minute timeout, and output dimension validation before PNG decoding.
NisUpscaler.cs uses the upstream NVIDIA 1.0.3 shaders and coefficient tables;
two 2× passes respect the SDK's supported scale range. GpuBitmap.cs shares the
existing stride/channel/opaque readback code with Fsr1Upscaler.cs.

Tests/EnhancementTests.cs adds persistence/filenames for every mode, per-job
mode retention, subscriber-failure recovery, missing-engine original
preservation, argument separation, process exit/cancellation/timeout checks,
real NIS shader tests and real Vulkan AI fixture execution. The new-mode test
failed before implementation. Current combined result: 22 passed, 0 failed.

Pinned components: waifu2x-ncnn-vulkan 20250915 (cunet scale model),
Real-ESRGAN's 20220424 Windows package from v0.2.5.0 (realesrgan-x4plus),
NIS commit 35e13ba316c98eeecf16f37eae70ce88019911f6. The installer uses
scripts/engines.lock.json archive and file hashes. Binaries/models are
distributed through the Windows release ZIP, not committed to source Git.
Licenses are included in licenses/ and THIRD-PARTY-NOTICES.txt.

Actual RX 7900 XTX processing of an existing 3440×1440 game screenshot
successfully encoded 13760×5760 outputs with all three new methods:

| Method | Processing | Including final PNG encoding |
|---|---:|---:|
| NIS | 0.95 s | 3.16 s |
| waifu2x | 6.56 s | 9.11 s |
| Real-ESRGAN | 24.47 s | 28.37 s |

These are single cold runs, not distribution statistics. AI processing times
include temporary input/output PNG I/O. Source image was unchanged. Inspected
matching avatar crops: NIS retains more of the source texture, waifu2x smooths
edges, and Real-ESRGAN smooths and changes fine textures. This is not proof of
detail recovery or general visual superiority. Evidence remains local under
.work/ai; private screenshots are excluded from the GitHub source update.

The installed published EXE was exercised autonomously by an elevated local
UI test helper: waifu2x via Screenshot Now, Real-ESRGAN via a physical F11 key
event while minimized, and NIS via right-clicking the real notification icon
and invoking its Screenshot now menu item. All three produced matching
3440×1440 originals and 13760×5760 companions in the configured Screenshots
folder. The initial tray lookup failed because the taskbar was auto-hidden;
revealing it and locating the icon through the Windows Shell API resolved the
test-harness issue. No application code changed for this retry.

Installed EXE SHA256:
CCC5600C6F048F69B041A27F0766A7339F32FFC7887E0FE5B89864C5708E884C.
The extracted distribution matched this EXE and all 11 pinned engine/model
files. Running the tests against those extracted engines again passed all 22
checks. Cross-vendor execution remains unverified. Timings are not game FPS
benchmarks and resource contention with games depends on workload.

## Earlier FSR evidence

## Delivered behavior

The user's 4× request supersedes the original 2× specification. Program.cs,
EnhancementSettings.cs, EnhancementWorker.cs and Fsr1Upscaler.cs now expose
Off / AMD FSR 1 — 4×, use Upscale4x, and save companions as _FSR4x.png.
Originals remain intact. The output limit is 80,000,000 pixels and 16,384 per
dimension, allowing 3440×1440 → 13760×5760. Old Fsr2x settings load as Off.
Shaders retain one FP32 EASU pass and RCAS at 1.0 stop. No new dependencies.
Tests/Program.cs and README.md were updated with the new behavior.

## Checks

- Regression before implementation: expected 13760×5760, received 6880×2880;
  test runner exited 1, with 8 passing and 1 failing test.
- Final focused Windows tests including actual GPU shaders: 13 passed,
  0 failed, exit 0. Covers settings, limits, atomic publication, original
  preservation, queue bounds, destination retention, cancellation, colors,
  borders, orientation, opacity, odd dimensions and negative bitmap stride.
- Release build: exit 0, 0 warnings and 0 errors.
- Framework-dependent win-x64 single-file publish: exit 0. Shader sources
  and third-party notices are embedded. After the user closed the old app,
  QuickShot.exe in the repository root was replaced successfully (exit 0).
  Its SHA256 matches the published EXE:
  81B82AD3E03D68D302DB07766E7FCD70BA6F8CB4CCD226929B0B6DE21ABD0F82.
  Preferences were not modified during installation.
- Visually inspected the 4× gradient/diagonal/HUD fixture. Orientation and
  colors are consistent; fine text/diagonal artifacts remain visible at 4×.

## Measured performance

RX 7900 XTX; fixed generated SDR 3440×1440 fixture. Two warmups, ten measured
runs. Cold shader/processing call: 365.4 ms. Warm enlargement: minimum 206.7,
median 214.2, p90 217.1, maximum 218.8 ms. Original PNG save median 54.6 ms;
original plus enhanced PNG save median 1073.4 ms, p90 1102.0 ms. Process peak
working set 1068.9 MiB; this is not a dedicated VRAM measurement. Output PNG
was successfully encoded at 13760×5760. Raw results: .work/fsr4x/benchmark.txt.

## Boundaries

The prior 2× build produced three user-assisted live capture pairs through
the app/F11/tray workflow. The final 4× EXE has not been launched or exercised
through those controls; current 4× evidence is from the actual GPU pipeline
and tests. Other GPU vendors, driver removal and HDR remain unverified.
The existing administrator manifest prevents automated elevated UI input.
The earlier worker subscriber-exception finding is now fixed and covered by
the queued-mode/subscriber regression check. No subagents were used for the
AI or NIS additions.

FSR 2/3 need renderer data and temporal history unavailable to desktop PNG
capture. DLSS additionally requires compatible NVIDIA RTX hardware. FSR 1
is a spatial enlargement method and does not restore missing native detail.
