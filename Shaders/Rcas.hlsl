Texture2D<float4> InputImage : register(t0);
RWTexture2D<float4> OutputImage : register(u0);
cbuffer ImageSizes : register(b0) { uint2 InputSize; uint2 OutputSize; };

float4 FsrRcasLoadF(int2 p)
{
    // Texture.Load does not clamp; otherwise RCAS darkens the outermost pixels.
    return InputImage.Load(int3(clamp(p, int2(0, 0), int2(OutputSize) - 1), 0));
}
void FsrRcasInputF(inout float r, inout float g, inout float b) { }

[numthreads(8, 8, 1)]
void Main(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= OutputSize)) return;
    uint4 constants;
    FsrRcasCon(constants, 1.0); // One stop below maximum sharpness, fixed gentle setting.
    float r, g, b;
    FsrRcasF(r, g, b, id.xy, constants);
    OutputImage[id.xy] = float4(saturate(float3(r, g, b)), 1.0);
}
