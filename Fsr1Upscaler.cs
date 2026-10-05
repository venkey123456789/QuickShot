using static QuickShot.GpuBitmap;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace QuickShot;

/// <summary>SDR screenshot upscaling. Owned and called exclusively by the enhancement worker.</summary>
internal sealed class Fsr1Upscaler : IDisposable
{
    private ID3D11Device? device;
    private ID3D11DeviceContext? context;
    private ID3D11ComputeShader? easu, rcas;
    private ID3D11SamplerState? sampler;
    private bool disposed;

    public Bitmap Upscale4x(Bitmap source)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        Size size = EnhancementLimits.GetOutputSize(source.Width, source.Height);
        Initialize();
        byte[] pixels = ReadRgba(source);
        GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var inputDescription = Describe(source.Width, source.Height, Format.R8G8B8A8_UNorm, BindFlags.ShaderResource);
            inputDescription.Usage = ResourceUsage.Immutable;
            using var input = device!.CreateTexture2D(inputDescription,
                new SubresourceData(pin.AddrOfPinnedObject(), (uint)(source.Width * 4), 0));
            // Intermediate float storage avoids 8-bit quantization before RCAS. Arithmetic is FP32.
            using var intermediate = device.CreateTexture2D(Describe(size.Width, size.Height,
                Format.R16G16B16A16_Float, BindFlags.ShaderResource | BindFlags.UnorderedAccess));
            using var output = device.CreateTexture2D(Describe(size.Width, size.Height,
                Format.R8G8B8A8_UNorm, BindFlags.UnorderedAccess));
            var stagingDescription = Describe(size.Width, size.Height, Format.R8G8B8A8_UNorm, BindFlags.None);
            stagingDescription.Usage = ResourceUsage.Staging;
            stagingDescription.CPUAccessFlags = CpuAccessFlags.Read;
            using var staging = device.CreateTexture2D(stagingDescription);
            using var inputView = device.CreateShaderResourceView(input);
            using var intermediateView = device.CreateShaderResourceView(intermediate);
            using var intermediateTarget = device.CreateUnorderedAccessView(intermediate);
            using var outputTarget = device.CreateUnorderedAccessView(output);
            var sizes = new ImageSizes((uint)source.Width, (uint)source.Height, (uint)size.Width, (uint)size.Height);
            using var constants = device.CreateBuffer(sizes, new BufferDescription(16, BindFlags.ConstantBuffer));
            context!.CSSetConstantBuffer(0, constants);
            context.CSSetSampler(0, sampler);
            Dispatch(easu!, inputView, intermediateTarget, size);
            Dispatch(rcas!, intermediateView, outputTarget, size);
            context.CopyResource(staging, output);
            var mapped = context.Map(staging, 0, MapMode.Read);
            try { return ReadBitmap(mapped, size); }
            finally { context.Unmap(staging, 0); }
        }
        finally
        {
            context?.ClearState();
            pin.Free();
        }
    }

    private void Initialize()
    {
        if (device is not null) return;
        try
        {
            // Hardware only: no silent software fallback or vendor-specific driver requirement.
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                [FeatureLevel.Level_11_0], out device, out _, out context).CheckError();
            easu = device.CreateComputeShader(Compile("Easu.hlsl", "FSR_EASU_F").Span);
            rcas = device.CreateComputeShader(Compile("Rcas.hlsl", "FSR_RCAS_F").Span);
            sampler = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                MinLOD = 0, MaxLOD = float.MaxValue,
                MaxAnisotropy = 1,
                ComparisonFunc = ComparisonFunction.Never
            });
        }
        catch
        {
            ReleaseDevice();
            throw;
        }
    }

    private static ReadOnlyMemory<byte> Compile(string wrapper, string operation)
    {
        // Sources: GPUOpen-Effects/FidelityFX-FSR v1.0.2; the original AMD headers are unmodified.
        string source = "#define A_GPU 1\n#define A_HLSL 1\n#define " + operation + " 1\n"
            + Resource("ffx_a.h") + "\n" + Resource("ffx_fsr1.h") + "\n" + Resource(wrapper);
        return Compiler.Compile(source, "Main", wrapper, "cs_5_0", ShaderFlags.OptimizationLevel3);
    }

    private static string Resource(string name)
    {
        using Stream stream = typeof(Fsr1Upscaler).Assembly.GetManifestResourceStream("QuickShot.Shaders." + name)
            ?? throw new InvalidOperationException("Embedded FSR shader is missing: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void Dispatch(ID3D11ComputeShader shader, ID3D11ShaderResourceView source,
        ID3D11UnorderedAccessView target, Size size)
    {
        context!.CSSetShader(shader);
        context.CSSetShaderResource(0, source);
        context.CSSetUnorderedAccessView(0, target);
        context.Dispatch((uint)((size.Width + 7) / 8), (uint)((size.Height + 7) / 8), 1);
        context.CSSetUnorderedAccessView(0, null);
        context.CSSetShaderResource(0, null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ImageSizes(uint InputWidth, uint InputHeight, uint OutputWidth, uint OutputHeight);

    private void ReleaseDevice()
    {
        context?.ClearState();
        sampler?.Dispose(); sampler = null;
        rcas?.Dispose(); rcas = null;
        easu?.Dispose(); easu = null;
        context?.Dispose(); context = null;
        device?.Dispose(); device = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ReleaseDevice();
    }
}
