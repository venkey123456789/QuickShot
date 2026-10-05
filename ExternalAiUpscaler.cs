using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text;

namespace QuickShot;

/// <summary>Offline, bounded invocation of the pinned NCNN Vulkan tools.</summary>
internal sealed class ExternalAiUpscaler(string? engineRoot = null)
{
    private readonly string root = Path.GetFullPath(engineRoot ?? Path.Combine(AppContext.BaseDirectory, "engines"));

    public Bitmap Upscale4x(Bitmap source, UpscalingMode mode, CancellationToken cancellationToken)
    {
        Size expected = EnhancementLimits.GetOutputSize(source.Width, source.Height);
        var start = CreateStartInfo(mode, "input.png", "output.png");
        if (!File.Exists(start.FileName))
            throw new FileNotFoundException("AI engine is missing. Extract the complete QuickShot Windows ZIP including its engines folder.");
        cancellationToken.ThrowIfCancellationRequested();
        string temporary = Path.Combine(Path.GetTempPath(), "QuickShot-AI", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string input = Path.Combine(temporary, "input.png"), output = Path.Combine(temporary, "output.png");
            source.Save(input, ImageFormat.Png);
            start = CreateStartInfo(mode, input, output);
            RunProcess(start, cancellationToken, TimeSpan.FromMinutes(15));
            cancellationToken.ThrowIfCancellationRequested();
            // Validate PNG dimensions before decoding a potentially oversized output.
            using (var file = File.OpenRead(output))
            {
                Span<byte> header = stackalloc byte[24];
                file.ReadExactly(header);
                ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
                if (!header[..8].SequenceEqual(signature) || !header[12..16].SequenceEqual("IHDR"u8)
                    || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..20]) != expected.Width
                    || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[20..24]) != expected.Height)
                    throw new InvalidDataException("AI output is not a PNG with the expected 4× dimensions.");
            }
            using var decoded = new Bitmap(output);
            // Copy into an opaque bitmap and release the file before temporary cleanup.
            var result = new Bitmap(expected.Width, expected.Height, PixelFormat.Format32bppArgb);
            try
            {
                using var graphics = Graphics.FromImage(result);
                graphics.Clear(Color.Black);
                graphics.DrawImageUnscaled(decoded, 0, 0);
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        finally { Directory.Delete(temporary, true); }
    }

    internal ProcessStartInfo CreateStartInfo(UpscalingMode mode, string input, string output)
    {
        string directory, executable, model;
        switch (mode)
        {
            case UpscalingMode.Waifu2x4x:
                directory = Path.Combine(root, "waifu2x"); executable = "waifu2x-ncnn-vulkan.exe";
                model = Path.Combine(directory, "models-cunet"); break;
            case UpscalingMode.RealEsrgan4x:
                directory = Path.Combine(root, "realesrgan"); executable = "realesrgan-ncnn-vulkan.exe";
                model = Path.Combine(directory, "models"); break;
            default: throw new ArgumentOutOfRangeException(nameof(mode));
        }
        var start = new ProcessStartInfo(Path.Combine(directory, executable))
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (string argument in new[] { "-i", input, "-o", output, "-s", "4", "-t", "128", "-m", model,
            "-n", mode == UpscalingMode.Waifu2x4x ? "-1" : "realesrgan-x4plus", "-j", "1:1:1", "-f", "png" })
            start.ArgumentList.Add(argument);
        return start;
    }

    internal static void RunProcess(ProcessStartInfo start, CancellationToken token, TimeSpan timeout)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        using var process = new Process { StartInfo = start };
        var diagnostic = new StringBuilder();
        var logGate = new object();
        void ReadLine(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (logGate)
            {
                diagnostic.AppendLine(e.Data);
                if (diagnostic.Length > 4000) diagnostic.Remove(0, diagnostic.Length - 4000);
            }
        }
        process.ErrorDataReceived += ReadLine;
        process.OutputDataReceived += ReadLine;
        if (!process.Start()) throw new InvalidOperationException("Could not start the AI engine.");
        try
        {
            process.BeginErrorReadLine(); process.BeginOutputReadLine();
            process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult();
            process.WaitForExit(); // Drain asynchronous stream callbacks.
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"AI engine failed (exit {process.ExitCode}): {diagnostic}");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("AI enhancement exceeded 15 minutes; original retained."); }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                process.WaitForExit();
            }
        }
    }
}
