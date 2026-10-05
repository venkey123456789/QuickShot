using System.Drawing.Imaging;

namespace QuickShot;

internal enum UpscalingMode { Off, Fsr4x, Waifu2x4x, RealEsrgan4x, Nis4x }

internal sealed record EnhancementJob(string OriginalPath, UpscalingMode Mode)
{
    public string OutputPath => EnhancementSettings.OutputPath(OriginalPath, Mode);
}

internal static class EnhancementSettings
{
    public static UpscalingMode Load(string path)
    {
        try
        {
            string value = File.ReadAllText(path).Trim();
            return Enum.GetValues<UpscalingMode>().FirstOrDefault(mode => mode.ToString() == value);
        }
        catch (IOException) { return UpscalingMode.Off; }
        catch (UnauthorizedAccessException) { return UpscalingMode.Off; }
    }

    public static void Save(string path, UpscalingMode mode)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        File.WriteAllText(path, mode.ToString());
    }

    public static string Suffix(UpscalingMode mode) => mode switch
    {
        UpscalingMode.Fsr4x => "_FSR4x",
        UpscalingMode.Waifu2x4x => "_waifu2x4x",
        UpscalingMode.RealEsrgan4x => "_RealESRGAN4x",
        UpscalingMode.Nis4x => "_NIS4x",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static string OutputPath(string originalPath, UpscalingMode mode) => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(originalPath))!,
        Path.GetFileNameWithoutExtension(originalPath) + Suffix(mode) + ".png");
}

internal static class EnhancementLimits
{
    public static Size GetOutputSize(int width, int height)
    {
        long w = (long)width * 4, h = (long)height * 4;
        if (width <= 0 || height <= 0 || w > 16384 || h > 16384 || w * h > 80_000_000)
            throw new ArgumentOutOfRangeException(nameof(width), "4× output must fit within 16,384 pixels per side and 80 million pixels.");
        return new Size((int)w, (int)h);
    }
}

internal static class EnhancedFileWriter
{
    public static string Save(Bitmap image, string originalPath, CancellationToken cancellationToken,
        UpscalingMode mode = UpscalingMode.Fsr4x)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(originalPath))!;
        string output = EnhancementSettings.OutputPath(originalPath, mode);
        string temporary = Path.Combine(directory, ".quickshot-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                image.Save(stream, ImageFormat.Png);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, output, overwrite: false);
            return output;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
