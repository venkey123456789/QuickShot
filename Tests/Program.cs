using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using QuickShot;

internal static class TestProgram
{
    private static int passed, failed;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--benchmark")) { Benchmark(); return 0; }
        if (args.Contains("--benchmark-all")) { EnhancementTests.BenchmarkAll(args.SkipWhile(a => a != "--input").Skip(1).FirstOrDefault()); return 0; }
        EnhancementTests.Register(Test, args);
        Test("all requested enhancement modes exist", () =>
        {
            foreach (string mode in new[] { "Waifu2x4x", "RealEsrgan4x", "Nis4x" })
                True(Enum.GetNames<UpscalingMode>().Contains(mode));
        });
        Test("missing preference defaults to Off", () =>
        {
            using var dir = new TestDirectory();
            Equal(UpscalingMode.Off, EnhancementSettings.Load(Path.Combine(dir.Path, "upscaling.txt")));
        });
        Test("preference roundtrip preserves legacy folder", () =>
        {
            using var dir = new TestDirectory();
            string mode = Path.Combine(dir.Path, "upscaling.txt");
            string legacy = Path.Combine(dir.Path, "settings.txt");
            File.WriteAllText(legacy, @"G:\Screenshots");
            EnhancementSettings.Save(mode, UpscalingMode.Fsr4x);
            Equal(UpscalingMode.Fsr4x, EnhancementSettings.Load(mode));
            Equal(@"G:\Screenshots", File.ReadAllText(legacy));
            EnhancementSettings.Save(mode, UpscalingMode.Off);
            Equal(UpscalingMode.Off, EnhancementSettings.Load(mode));
            File.WriteAllText(mode, "Fsr2x"); // Old 2x preference must not silently enable heavier 4x work.
            Equal(UpscalingMode.Off, EnhancementSettings.Load(mode));
            File.WriteAllText(mode, "corrupt/4x");
            Equal(UpscalingMode.Off, EnhancementSettings.Load(mode));
        });
        Test("4x dimensions and exact pixel ceiling", () =>
        {
            Equal(new Size(13760, 5760), EnhancementLimits.GetOutputSize(3440, 1440));
            Equal(new Size(16000, 5000), EnhancementLimits.GetOutputSize(4000, 1250));
            Equal(new Size(16384, 4), EnhancementLimits.GetOutputSize(4096, 1));
            Throws<ArgumentOutOfRangeException>(() => EnhancementLimits.GetOutputSize(4097, 1));
            Throws<ArgumentOutOfRangeException>(() => EnhancementLimits.GetOutputSize(4000, 1251));
            Throws<ArgumentOutOfRangeException>(() => EnhancementLimits.GetOutputSize(int.MaxValue, int.MaxValue));
            Throws<ArgumentOutOfRangeException>(() => EnhancementLimits.GetOutputSize(0, 2));
        });
        Test("enhanced PNG is atomic and never overwrites", () =>
        {
            using var dir = new TestDirectory();
            string original = Path.Combine(dir.Path, "Screenshot_test.png");
            using var image = Solid(7, 5, Color.CornflowerBlue);
            image.Save(original, ImageFormat.Png);
            string before = Hash(original);
            string enhanced = EnhancedFileWriter.Save(image, original, CancellationToken.None);
            Equal(Path.Combine(dir.Path, "Screenshot_test_FSR4x.png"), enhanced);
            Equal(before, Hash(original));
            string enhancedBefore = Hash(enhanced);
            Throws<IOException>(() => EnhancedFileWriter.Save(image, original, CancellationToken.None));
            Equal(enhancedBefore, Hash(enhanced));
            Equal(0, Directory.GetFiles(dir.Path, "*.tmp").Length);
        });
        Test("cancelled enhancement does not publish or leave temporary files", () =>
        {
            using var dir = new TestDirectory();
            using var image = Solid(3, 3, Color.Red);
            string original = Path.Combine(dir.Path, "cancel.png");
            image.Save(original, ImageFormat.Png);
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            Throws<OperationCanceledException>(() => EnhancedFileWriter.Save(image, original, cancel.Token));
            Equal(1, Directory.GetFiles(dir.Path).Length);
        });
        Test("worker allows one active plus one waiting job", () =>
        {
            using var dir = new TestDirectory();
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var done = new CountdownEvent(2);
            using var image = Solid(13, 9, Color.Green);
            string a = Save(image, dir.Path, "a"), b = Save(image, dir.Path, "b"), c = Save(image, dir.Path, "c");
            var worker = new EnhancementWorker(source =>
            {
                started.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                return new Bitmap(source, source.Width * 4, source.Height * 4);
            });
            worker.Completed += (_, error) => { if (error is null) done.Signal(); };
            try
            {
                True(worker.TryEnqueue(a));
                True(started.Wait(TimeSpan.FromSeconds(5)));
                True(worker.TryEnqueue(b));
                True(!worker.TryEnqueue(c));
                release.Set();
                True(done.Wait(TimeSpan.FromSeconds(10)));
                using var aOutput = new Bitmap(Path.Combine(dir.Path, "a_FSR4x.png"));
                Equal(new Size(52, 36), aOutput.Size);
                True(!File.Exists(Path.Combine(dir.Path, "c_FSR4x.png")));
            }
            finally { release.Set(); worker.StopAsync().GetAwaiter().GetResult(); }
        });
        Test("GPU failure preserves original and reports enhancement error", () =>
        {
            using var dir = new TestDirectory();
            using var image = Solid(9, 5, Color.Blue);
            string original = Save(image, dir.Path, "failure");
            string before = Hash(original);
            using var done = new ManualResetEventSlim();
            string? message = null;
            var worker = new EnhancementWorker(_ => throw new InvalidOperationException("GPU unavailable"));
            worker.Completed += (_, error) => { message = error; done.Set(); };
            True(worker.TryEnqueue(original));
            True(done.Wait(TimeSpan.FromSeconds(5)));
            worker.StopAsync().GetAwaiter().GetResult();
            True(message?.Contains("GPU unavailable") == true);
            Equal(before, Hash(original));
            Equal(1, Directory.GetFiles(dir.Path).Length);
        });
        Test("queued captures keep their own folder when the destination changes", () =>
        {
            using var firstFolder = new TestDirectory();
            using var secondFolder = new TestDirectory();
            using var image = Solid(7, 5, Color.Blue);
            string first = Save(image, firstFolder.Path, "first");
            string second = Save(image, secondFolder.Path, "second");
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var done = new CountdownEvent(2);
            var worker = new EnhancementWorker(source =>
            {
                started.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                return new Bitmap(source, source.Width * 4, source.Height * 4);
            });
            worker.Completed += (_, error) => { if (error is null) done.Signal(); };
            try
            {
                True(worker.TryEnqueue(first));
                True(started.Wait(TimeSpan.FromSeconds(5)));
                True(worker.TryEnqueue(second));
                release.Set();
                True(done.Wait(TimeSpan.FromSeconds(10)));
                True(File.Exists(Path.Combine(firstFolder.Path, "first_FSR4x.png")));
                True(File.Exists(Path.Combine(secondFolder.Path, "second_FSR4x.png")));
                True(!File.Exists(Path.Combine(secondFolder.Path, "first_FSR4x.png")));
            }
            finally { release.Set(); worker.StopAsync().GetAwaiter().GetResult(); }
        });
        Test("exit cancels active publishing and pending jobs", () =>
        {
            using var dir = new TestDirectory();
            using var image = Solid(8, 5, Color.Red);
            string a = Save(image, dir.Path, "active"), b = Save(image, dir.Path, "pending");
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var worker = new EnhancementWorker(source =>
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                return new Bitmap(source, source.Width * 4, source.Height * 4);
            });
            True(worker.TryEnqueue(a));
            True(started.Wait(TimeSpan.FromSeconds(5)));
            True(worker.TryEnqueue(b));
            Task stopping = worker.StopAsync();
            True(!worker.TryEnqueue(a));
            release.Set();
            True(stopping.Wait(TimeSpan.FromSeconds(10)));
            Equal(2, Directory.GetFiles(dir.Path).Length);
        });

        if (args.Contains("--gpu") || args.Contains("--benchmark"))
        {
            Test("real FSR shaders preserve solid colors including borders", () =>
            {
                using var fsr = new Fsr1Upscaler();
                foreach (var color in new[] { Color.Black, Color.White, Color.Red, Color.Lime, Color.Blue, Color.FromArgb(34, 117, 203) })
                {
                    using var input = Solid(17, 11, color);
                    using var output = fsr.Upscale4x(input);
                    Equal(new Size(68, 44), output.Size);
                    foreach (var p in new[] { new Point(0, 0), new Point(67, 0), new Point(0, 43), new Point(67, 43), new Point(34, 22) })
                        Near(color, output.GetPixel(p.X, p.Y), 1);
                }
            });
            Test("FSR retains orientation, color order and opaque output", () =>
            {
                using var input = Solid(65, 37, Color.Black);
                using (var graphics = Graphics.FromImage(input))
                {
                    graphics.FillRectangle(Brushes.Red, 0, 0, 32, 18);
                    graphics.FillRectangle(Brushes.Lime, 32, 0, 33, 18);
                    graphics.FillRectangle(Brushes.Blue, 0, 18, 32, 19);
                    graphics.FillRectangle(Brushes.White, 32, 18, 33, 19);
                }
                using var fsr = new Fsr1Upscaler();
                using var output = fsr.Upscale4x(input);
                Equal(new Size(260, 148), output.Size);
                Near(Color.Red, output.GetPixel(10, 10), 1);
                Near(Color.Lime, output.GetPixel(220, 20), 1);
                Near(Color.Blue, output.GetPixel(20, 120), 1);
                Near(Color.White, output.GetPixel(220, 120), 1);
                Equal(255, (int)output.GetPixel(64, 36).A);
            });
            Test("gradient, diagonal and HUD fixture produces complete PNG", () =>
            {
                using var fixture = Fixture(173, 93);
                using var fsr = new Fsr1Upscaler();
                using var output = fsr.Upscale4x(fixture);
                Equal(new Size(692, 372), output.Size);
                Near(fixture.GetPixel(20, 85), output.GetPixel(80, 340), 6);
                string artifacts = Environment.GetEnvironmentVariable("QUICKSHOT_TEST_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
                Directory.CreateDirectory(artifacts);
                fixture.Save(Path.Combine(artifacts, "fixture-original.png"), ImageFormat.Png);
                output.Save(Path.Combine(artifacts, "fixture-FSR4x.png"), ImageFormat.Png);
                Near(Color.White, fixture.GetPixel(130, 36), 0);
                Near(Color.White, output.GetPixel(520, 144), 1);
            });
            Test("negative bitmap stride retains top and bottom orientation", () =>
            {
                const int width = 17, height = 11, row = width * 4;
                IntPtr memory = Marshal.AllocHGlobal(row * height);
                try
                {
                    using var input = new Bitmap(width, height, -row, PixelFormat.Format32bppArgb,
                        IntPtr.Add(memory, row * (height - 1)));
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                            input.SetPixel(x, y, y < 5 ? Color.Red : Color.Blue);
                    using var fsr = new Fsr1Upscaler();
                    using var output = fsr.Upscale4x(input);
                    Near(Color.Red, output.GetPixel(5, 2), 1);
                    Near(Color.Blue, output.GetPixel(10, 38), 1);
                }
                finally { Marshal.FreeHGlobal(memory); }
            });
        }
        Console.WriteLine($"RESULT: {passed} passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static void Benchmark()
    {
        string artifacts = Environment.GetEnvironmentVariable("QUICKSHOT_TEST_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(artifacts);
        using var input = Fixture(3440, 1440);
        input.Save(Path.Combine(artifacts, "benchmark-source.png"), ImageFormat.Png);
        using var fsr = new Fsr1Upscaler();
        var durations = new List<double>();
        var watch = Stopwatch.StartNew();
        using (var first = fsr.Upscale4x(input))
        {
            Console.WriteLine($"BENCH cold: {watch.Elapsed.TotalMilliseconds:F1} ms, {first.Width}x{first.Height}");
            first.Save(Path.Combine(artifacts, "benchmark-FSR4x.png"), ImageFormat.Png);
        }
        // Two warmups; ten measured runs. Source and dimensions do not change.
        for (int i = 0; i < 12; i++)
        {
            watch.Restart();
            using var output = fsr.Upscale4x(input);
            if (i >= 2) durations.Add(watch.Elapsed.TotalMilliseconds);
        }
        durations.Sort();
        Console.WriteLine($"BENCH warm n=10: min={durations[0]:F1}, median={(durations[4]+durations[5])/2:F1}, p90={durations[8]:F1}, max={durations[9]:F1} ms");
        Console.WriteLine($"BENCH peak working set: {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F1} MiB");
        var control = new List<double>();
        var candidate = new List<double>();
        // Compare identical input/disk operations. Candidate includes PNG reload, GPU work,
        // PNG encoding and atomic publication, matching the application's background path.
        for (int i = 0; i < 12; i++)
        {
            string controlPath = Path.Combine(artifacts, "timing-control.png");
            string candidatePath = Path.Combine(artifacts, "timing-candidate.png");
            string enhancedPath = Path.Combine(artifacts, "timing-candidate_FSR4x.png");
            watch.Restart();
            input.Save(controlPath, ImageFormat.Png);
            if (i >= 2) control.Add(watch.Elapsed.TotalMilliseconds);
            File.Delete(enhancedPath);
            watch.Restart();
            input.Save(candidatePath, ImageFormat.Png);
            using (var reloaded = new Bitmap(candidatePath))
            using (var enhanced = fsr.Upscale4x(reloaded))
                EnhancedFileWriter.Save(enhanced, candidatePath, CancellationToken.None);
            if (i >= 2) candidate.Add(watch.Elapsed.TotalMilliseconds);
        }
        PrintDistribution("original PNG save", control);
        PrintDistribution("original + enhanced PNG save", candidate);
        Console.WriteLine($"BENCH final peak working set: {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F1} MiB");
        File.WriteAllText(Path.Combine(artifacts, "benchmark.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            Source = "fixed generated SDR gradient/diagonal/HUD fixture", Width = 3440, Height = 1440,
            Warmups = 2, MeasuredRuns = 10, UpscaleMilliseconds = durations,
            OriginalSaveMilliseconds = control, OriginalAndEnhancedSaveMilliseconds = candidate,
            PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
    private static void PrintDistribution(string name, List<double> values)
    {
        values.Sort();
        Console.WriteLine($"BENCH {name} n=10: min={values[0]:F1}, median={(values[4]+values[5])/2:F1}, p90={values[8]:F1}, max={values[9]:F1} ms");
    }
    internal static Bitmap Fixture(int width, int height)
    {
        var image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(image);
        for (int x = 0; x < width; x++)
        {
            using var pen = new Pen(Color.FromArgb(x * 255 / width, 80, 255 - x * 255 / width));
            graphics.DrawLine(pen, x, 0, x, height);
        }
        graphics.FillRectangle(Brushes.White, width / 2, 8, width / 3, height / 3);
        graphics.DrawLine(Pens.Yellow, 0, height - 1, width - 1, 0);
        using var font = new Font("Segoe UI", Math.Max(9, height / 40), FontStyle.Bold);
        graphics.DrawString("HUD 123 FPS", font, Brushes.Black, width / 2, 10);
        return image;
    }
    private static Bitmap Solid(int w, int h, Color color)
    {
        var image = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(image); graphics.Clear(color); return image;
    }
    private static string Save(Bitmap image, string directory, string name)
    { string path = Path.Combine(directory, name + ".png"); image.Save(path, ImageFormat.Png); return path; }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS: " + name); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL: " + name + ": " + error); }
    }
    private static void True(bool value) { if (!value) throw new Exception("Expected true"); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void Near(Color expected, Color actual, int tolerance)
    {
        if (Math.Abs(expected.R - actual.R) > tolerance || Math.Abs(expected.G - actual.G) > tolerance || Math.Abs(expected.B - actual.B) > tolerance || actual.A != 255)
            throw new Exception($"Expected {expected} within {tolerance}, got {actual}");
    }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "QuickShotTests", Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
