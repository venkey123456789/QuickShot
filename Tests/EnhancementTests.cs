using System.Diagnostics;
using System.Drawing.Imaging;
using QuickShot;

internal static class EnhancementTests
{
    internal static void Register(Action<string, Action> test, string[] args)
    {
        test("all modes persist and publish distinct companion names", () =>
        {
            string dir = Temp();
            try
            {
                using var input = new Bitmap(17, 9);
                string original = Path.Combine(dir, "original.png"); input.Save(original);
                var paths = new HashSet<string>();
                foreach (var mode in Enum.GetValues<UpscalingMode>())
                {
                    string settings = Path.Combine(dir, "mode.txt");
                    EnhancementSettings.Save(settings, mode);
                    Check(EnhancementSettings.Load(settings) == mode);
                    if (mode == UpscalingMode.Off) continue;
                    string result = EnhancedFileWriter.Save(input, original, default, mode);
                    Check(paths.Add(result) && File.Exists(result));
                }
                File.WriteAllText(Path.Combine(dir, "mode.txt"), "2");
                Check(EnhancementSettings.Load(Path.Combine(dir, "mode.txt")) == UpscalingMode.Off);
            }
            finally { Directory.Delete(dir, true); }
        });
        test("queued jobs retain mode and survive a throwing subscriber", () =>
        {
            string dir = Temp();
            using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            using var done = new ManualResetEventSlim();
            var worker = new EnhancementWorker(source =>
            {
                started.Set(); Check(release.Wait(TimeSpan.FromSeconds(5)));
                return new Bitmap(source, source.Width * 4, source.Height * 4);
            });
            try
            {
                using var image = new Bitmap(7, 5);
                string a = Path.Combine(dir, "a.png"), b = Path.Combine(dir, "b.png");
                image.Save(a); image.Save(b);
                worker.Completed += (job, error) =>
                {
                    Check(error is null);
                    if (job.Mode == UpscalingMode.Waifu2x4x) throw new Exception("test subscriber failure");
                    if (job.Mode == UpscalingMode.RealEsrgan4x) done.Set();
                };
                Check(worker.TryEnqueue(a, UpscalingMode.Waifu2x4x));
                Check(started.Wait(TimeSpan.FromSeconds(5)));
                Check(worker.TryEnqueue(b, UpscalingMode.RealEsrgan4x));
                release.Set(); Check(done.Wait(TimeSpan.FromSeconds(5)));
                Check(File.Exists(EnhancementSettings.OutputPath(a, UpscalingMode.Waifu2x4x)));
                Check(File.Exists(EnhancementSettings.OutputPath(b, UpscalingMode.RealEsrgan4x)));
            }
            finally { release.Set(); worker.StopAsync().GetAwaiter().GetResult(); Directory.Delete(dir, true); }
        });
        test("missing AI engine preserves original and reports failure", () =>
        {
            string dir = Temp(); using var done = new ManualResetEventSlim();
            var worker = new EnhancementWorker(engineRoot: Path.Combine(dir, "missing"));
            string? failure = null;
            try
            {
                using var image = new Bitmap(7, 5);
                string original = Path.Combine(dir, "original.png"); image.Save(original);
                byte[] before = File.ReadAllBytes(original);
                worker.Completed += (_, error) => { failure = error; done.Set(); };
                Check(worker.TryEnqueue(original, UpscalingMode.RealEsrgan4x));
                Check(done.Wait(TimeSpan.FromSeconds(5)));
                Check(failure?.Contains("engine is missing") == true);
                Check(before.SequenceEqual(File.ReadAllBytes(original)));
                Check(Directory.GetFiles(dir).Length == 1);
            }
            finally { worker.StopAsync().GetAwaiter().GetResult(); Directory.Delete(dir, true); }
        });
        test("AI arguments preserve paths and fixed model selection", () =>
        {
            var start = new ExternalAiUpscaler(@"C:\engine path").CreateStartInfo(UpscalingMode.RealEsrgan4x,
                @"C:\a & b\input.png", @"C:\out folder\out.png");
            Check(!start.UseShellExecute && start.CreateNoWindow);
            Check(start.ArgumentList.Contains(@"C:\a & b\input.png"));
            Check(start.ArgumentList.Contains("realesrgan-x4plus"));
        });
        test("AI process exit failures and cancellation are observed", () =>
        {
            Throws<InvalidOperationException>(() => ExternalAiUpscaler.RunProcess(Shell("exit 7"), default, TimeSpan.FromSeconds(10)));
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var watch = Stopwatch.StartNew();
            Throws<OperationCanceledException>(() => ExternalAiUpscaler.RunProcess(Shell("Start-Sleep -Seconds 60"), cancel.Token, TimeSpan.FromMinutes(2)));
            Check(watch.Elapsed < TimeSpan.FromSeconds(10));
            Throws<TimeoutException>(() => ExternalAiUpscaler.RunProcess(Shell("Start-Sleep -Seconds 60"), default, TimeSpan.FromMilliseconds(500)));
        });
        if (args.Contains("--gpu"))
        {
            test("real NIS shaders handle solids, borders and odd dimensions", () =>
            {
                using var nis = new NisUpscaler();
                foreach (var color in new[] { Color.Red, Color.Lime, Color.Blue, Color.Black, Color.White })
                {
                    using var input = new Bitmap(65, 37);
                    using (var g = Graphics.FromImage(input)) g.Clear(color);
                    using var output = nis.Upscale4x(input);
                    Check(output.Size == new Size(260, 148));
                    foreach (var p in new[] { new Point(0, 0), new Point(259, 147), new Point(259, 0), new Point(0, 147), new Point(130, 74) })
                        Near(color, output.GetPixel(p.X, p.Y), 1);
                }
                using var fixture = TestProgram.Fixture(173, 93);
                using var result = nis.Upscale4x(fixture);
                Near(fixture.GetPixel(20, 85), result.GetPixel(80, 340), 6);
                result.Save(Path.Combine(Artifacts(), "fixture-NIS4x.png"), ImageFormat.Png);
            });
        }
        if (args.Contains("--ai"))
        {
            foreach (var mode in new[] { UpscalingMode.Waifu2x4x, UpscalingMode.RealEsrgan4x })
                test("real Vulkan AI runtime: " + mode, () =>
                {
                    using var input = TestProgram.Fixture(173, 93);
                    using var output = Engine().Upscale4x(input, mode, default);
                    Check(output.Size == new Size(692, 372));
                    Near(input.GetPixel(20, 85), output.GetPixel(80, 340), 20);
                    Check(output.GetPixel(0, 0).A == 255 && output.GetPixel(691, 371).A == 255);
                    output.Save(Path.Combine(Artifacts(), "fixture" + EnhancementSettings.Suffix(mode) + ".png"), ImageFormat.Png);
                });
        }
    }

    internal static void BenchmarkAll(string? path)
    {
        using var input = path is null ? TestProgram.Fixture(3440, 1440) : new Bitmap(path);
        foreach (var mode in new[] { UpscalingMode.Nis4x, UpscalingMode.Waifu2x4x, UpscalingMode.RealEsrgan4x })
        {
            var watch = Stopwatch.StartNew();
            using var nis = new NisUpscaler();
            using var output = mode == UpscalingMode.Nis4x ? nis.Upscale4x(input) : Engine().Upscale4x(input, mode, default);
            Check(output.Size == EnhancementLimits.GetOutputSize(input.Width, input.Height));
            double processing = watch.Elapsed.TotalSeconds;
            output.Save(Path.Combine(Artifacts(), "full" + EnhancementSettings.Suffix(mode) + ".png"), ImageFormat.Png);
            Console.WriteLine($"BENCH {mode}: {output.Width}x{output.Height}; processing={processing:F2}s; including PNG={watch.Elapsed.TotalSeconds:F2}s");
        }
    }
    private static ExternalAiUpscaler Engine() => new(Environment.GetEnvironmentVariable("QUICKSHOT_ENGINE_ROOT"));
    private static string Artifacts()
    {
        string dir = Environment.GetEnvironmentVariable("QUICKSHOT_TEST_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(dir); return dir;
    }
    private static string Temp() { string dir = Path.Combine(Path.GetTempPath(), "QuickShot-test-" + Guid.NewGuid()); Directory.CreateDirectory(dir); return dir; }
    private static ProcessStartInfo Shell(string command)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command", command }) start.ArgumentList.Add(arg);
        return start;
    }
    private static void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static void Near(Color a, Color b, int tolerance) => Check(Math.Abs(a.R-b.R) <= tolerance && Math.Abs(a.G-b.G) <= tolerance && Math.Abs(a.B-b.B) <= tolerance && b.A == 255);
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
