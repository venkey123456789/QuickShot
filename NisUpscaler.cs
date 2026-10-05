using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static QuickShot.GpuBitmap;

namespace QuickShot;

/// <summary>NVIDIA Image Scaling 1.0.3, FP32 SDR, two supported 2x passes.</summary>
internal sealed class NisUpscaler : IDisposable
{
    private ID3D11Device? device;
    private ID3D11DeviceContext? context;
    private ID3D11ComputeShader? shader;
    private ID3D11SamplerState? sampler;
    private ID3D11Texture2D? scaleCoefficients, sharpCoefficients;
    private ID3D11ShaderResourceView? scaleView, sharpView;
    private bool disposed;

    public Bitmap Upscale4x(Bitmap source)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        Size outputSize = EnhancementLimits.GetOutputSize(source.Width, source.Height);
        Size middleSize = new(source.Width * 2, source.Height * 2);
        Initialize();
        var pin = GCHandle.Alloc(ReadRgba(source), GCHandleType.Pinned);
        try
        {
            var description = Describe(source.Width, source.Height, Format.R8G8B8A8_UNorm, BindFlags.ShaderResource);
            description.Usage = ResourceUsage.Immutable;
            using var input = device!.CreateTexture2D(description,
                new SubresourceData(pin.AddrOfPinnedObject(), (uint)source.Width * 4, 0));
            using var middle = device.CreateTexture2D(Describe(middleSize.Width, middleSize.Height,
                Format.R16G16B16A16_Float, BindFlags.ShaderResource | BindFlags.UnorderedAccess));
            using var output = device.CreateTexture2D(Describe(outputSize.Width, outputSize.Height,
                Format.R8G8B8A8_UNorm, BindFlags.UnorderedAccess));
            Dispatch(input, middle, source.Size, middleSize, 0f);
            Dispatch(middle, output, middleSize, outputSize, 0.2f);
            var stagingDescription = Describe(outputSize.Width, outputSize.Height, Format.R8G8B8A8_UNorm, BindFlags.None);
            stagingDescription.Usage = ResourceUsage.Staging;
            stagingDescription.CPUAccessFlags = CpuAccessFlags.Read;
            using var staging = device.CreateTexture2D(stagingDescription);
            context!.CopyResource(staging, output);
            var mapped = context.Map(staging, 0, MapMode.Read);
            try { return ReadBitmap(mapped, outputSize); }
            finally { context.Unmap(staging, 0); }
        }
        finally { context?.ClearState(); pin.Free(); }
    }

    private void Initialize()
    {
        if (device is not null) return;
        try
        {
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                [FeatureLevel.Level_11_0], out device, out _, out context).CheckError();
            string source = "#define NIS_VIEWPORT_SUPPORT 1\n#define NIS_CLAMP_OUTPUT 1\n"
                + "#define NIS_BLOCK_WIDTH 32\n#define NIS_BLOCK_HEIGHT 24\n#define NIS_THREAD_GROUP_SIZE 256\n"
                + Resource("NIS_Main.hlsl").Replace("#include \"NIS_Scaler.h\"", Resource("NIS_Scaler.h"));
            shader = device.CreateComputeShader(Compiler.Compile(source, "main", "NIS_Main.hlsl",
                "cs_5_0", ShaderFlags.OptimizationLevel3).Span);
            sampler = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
                MinLOD = 0, MaxLOD = float.MaxValue, MaxAnisotropy = 1, ComparisonFunc = ComparisonFunction.Never
            });
            scaleCoefficients = Coefficients("coef_scale"); sharpCoefficients = Coefficients("coef_usm");
            scaleView = device.CreateShaderResourceView(scaleCoefficients);
            sharpView = device.CreateShaderResourceView(sharpCoefficients);
        }
        catch { Release(); throw; }
    }

    private ID3D11Texture2D Coefficients(string name)
    {
        // Read the exact upstream FP32 table, avoiding a separately maintained copy.
        string table = Regex.Match(Resource("NIS_Config.h"),
            @"constexpr float " + name + @"\[kPhaseCount\]\[kFilterSize\] = \{(.*?)\};", RegexOptions.Singleline).Groups[1].Value;
        float[] values = Regex.Matches(table, @"-?\d+\.\d+").Select(m => float.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
        if (values.Length != 64 * 8) throw new InvalidDataException("Invalid embedded NIS coefficient table.");
        var pinned = GCHandle.Alloc(values, GCHandleType.Pinned);
        try
        {
            var desc = Describe(2, 64, Format.R32G32B32A32_Float, BindFlags.ShaderResource);
            desc.Usage = ResourceUsage.Immutable;
            return device!.CreateTexture2D(desc, new SubresourceData(pinned.AddrOfPinnedObject(), 32, 0));
        }
        finally { pinned.Free(); }
    }

    private void Dispatch(ID3D11Texture2D input, ID3D11Texture2D output, Size inputSize, Size outputSize, float sharpness)
    {
        using var srv = device!.CreateShaderResourceView(input);
        using var uav = device.CreateUnorderedAccessView(output);
        using var constants = device.CreateBuffer(Config.Create(inputSize, outputSize, sharpness),
            new BufferDescription(112, BindFlags.ConstantBuffer));
        context!.CSSetShader(shader);
        context.CSSetSampler(0, sampler);
        context.CSSetConstantBuffer(0, constants);
        context.CSSetShaderResource(0, srv);
        context.CSSetShaderResource(1, scaleView);
        context.CSSetShaderResource(2, sharpView);
        context.CSSetUnorderedAccessView(0, uav);
        context.Dispatch((uint)((outputSize.Width + 31) / 32), (uint)((outputSize.Height + 23) / 24), 1);
        context.CSSetUnorderedAccessView(0, null);
        context.CSSetShaderResource(0, null);
    }

    private static string Resource(string file)
    {
        using var stream = typeof(NisUpscaler).Assembly.GetManifestResourceStream("QuickShot.Shaders." + file)
            ?? throw new InvalidOperationException("Missing embedded NIS resource: " + file);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Layout and SDR equations ported from NVIDIA NVScalerUpdateConfig, NIS_Config.h v1.0.3.
    [StructLayout(LayoutKind.Sequential)]
    private struct Config
    {
        public float DetectRatio, DetectThres, MinContrastRatio, RatioNorm;
        public float ContrastBoost, Eps, SharpStartY, SharpScaleY;
        public float SharpStrengthMin, SharpStrengthScale, SharpLimitMin, SharpLimitScale;
        public float ScaleX, ScaleY, DstNormX, DstNormY, SrcNormX, SrcNormY;
        public uint InputOriginX, InputOriginY, InputWidth, InputHeight;
        public uint OutputOriginX, OutputOriginY, OutputWidth, OutputHeight;
        public float Reserved0, Reserved1;

        public static Config Create(Size input, Size output, float sharpness)
        {
            float slider = Math.Clamp(sharpness, 0f, 1f) - 0.5f;
            float minScale = slider >= 0 ? 1.25f : 1f;
            float maxScale = slider >= 0 ? 1.25f : 1.75f;
            float strengthMin = Math.Max(0, 0.4f + slider * minScale * 1.2f);
            float limitMin = Math.Max(0.1f, 0.14f + slider * minScale * 0.32f);
            return new Config
            {
                DetectRatio = 2 * 1127f / 1024f, DetectThres = 64f / 1024f,
                MinContrastRatio = 2, RatioNorm = 1f / 8f, ContrastBoost = 1, Eps = 1f / 255f,
                SharpStartY = 0.45f, SharpScaleY = 1f / (0.9f - 0.45f),
                SharpStrengthMin = strengthMin, SharpStrengthScale = 1.6f + slider * maxScale * 1.8f - strengthMin,
                SharpLimitMin = limitMin, SharpLimitScale = 0.5f + slider * minScale * 0.6f - limitMin,
                ScaleX = (float)input.Width / output.Width, ScaleY = (float)input.Height / output.Height,
                SrcNormX = 1f / input.Width, SrcNormY = 1f / input.Height,
                DstNormX = 1f / output.Width, DstNormY = 1f / output.Height,
                InputWidth = (uint)input.Width, InputHeight = (uint)input.Height,
                OutputWidth = (uint)output.Width, OutputHeight = (uint)output.Height
            };
        }
    }

    private void Release()
    {
        context?.ClearState();
        scaleView?.Dispose(); sharpView?.Dispose(); scaleCoefficients?.Dispose(); sharpCoefficients?.Dispose();
        sampler?.Dispose(); shader?.Dispose(); context?.Dispose(); device?.Dispose(); device = null;
        scaleView = null; sharpView = null; scaleCoefficients = null; sharpCoefficients = null;
        sampler = null; shader = null; context = null;
    }
    public void Dispose() { if (disposed) return; disposed = true; Release(); }
}
