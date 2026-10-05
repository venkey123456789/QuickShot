using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
namespace QuickShot;
internal static class GpuBitmap
{
    internal static Texture2DDescription Describe(int width, int height, Format format, BindFlags flags) => new()
    {
        Width = (uint)width, Height = (uint)height,
        MipLevels = 1, ArraySize = 1, Format = format,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default, BindFlags = flags
    };

    internal static byte[] ReadRgba(Bitmap source)
    {
        var rectangle = new Rectangle(Point.Empty, source.Size);
        using var normalized = source.Clone(rectangle, PixelFormat.Format32bppArgb);
        BitmapData data = normalized.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = checked(source.Width * 4);
            byte[] pixels = new byte[checked(rowBytes * source.Height)];
            for (int y = 0; y < source.Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * rowBytes, rowBytes);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
                pixels[i + 3] = 255;
            }
            return pixels;
        }
        finally { normalized.UnlockBits(data); }
    }

    internal static Bitmap ReadBitmap(MappedSubresource mapped, Size size)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        try
        {
            BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] row = new byte[size.Width * 4];
                for (int y = 0; y < size.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(mapped.DataPointer, checked(y * (int)mapped.RowPitch)), row, 0, row.Length);
                    for (int i = 0; i < row.Length; i += 4)
                    {
                        (row[i], row[i + 2]) = (row[i + 2], row[i]);
                        row[i + 3] = 255;
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
                }
            }
            finally { bitmap.UnlockBits(data); }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

}
