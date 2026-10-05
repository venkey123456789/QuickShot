// AMD FSR1 v1.0.2, FP32 path. Headers are embedded and prepended by Fsr1Upscaler.
Texture2D<float4> InputImage : register(t0);
RWTexture2D<float4> OutputImage : register(u0);
SamplerState LinearClamp : register(s0);
cbuffer ImageSizes : register(b0) { uint2 InputSize; uint2 OutputSize; };

float4 FsrEasuRF(float2 p) { return InputImage.GatherRed(LinearClamp, p); }
float4 FsrEasuGF(float2 p) { return InputImage.GatherGreen(LinearClamp, p); }
float4 FsrEasuBF(float2 p) { return InputImage.GatherBlue(LinearClamp, p); }

[numthreads(8, 8, 1)]
void Main(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= OutputSize)) return;
    uint4 c0, c1, c2, c3;
    FsrEasuCon(c0, c1, c2, c3, InputSize.x, InputSize.y,
        InputSize.x, InputSize.y, OutputSize.x, OutputSize.y);
    float3 rgb;
    FsrEasuF(rgb, id.xy, c0, c1, c2, c3);
    OutputImage[id.xy] = float4(saturate(rgb), 1.0);
}
