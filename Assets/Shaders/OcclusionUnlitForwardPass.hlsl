// AR Foundation Samples の OcclusionUnlitForwardPass.hlsl 準拠（DEBUG_DISPLAY 系を除去）。
#ifndef BLOCKFIELD_OCCLUSION_UNLIT_PASS_INCLUDED
#define BLOCKFIELD_OCCLUSION_UNLIT_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Unlit.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "OcclusionComputation.hlsl"

struct Attributes
{
    float4 positionOS : POSITION;
    float2 uv : TEXCOORD0;
    #if defined(_VERTEX_COLOR)
    float4 color : COLOR;
    #endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float2 uv : TEXCOORD1;
    float fogCoord : TEXCOORD2;
    #if defined(_VERTEX_COLOR)
    float4 color : COLOR;
    #endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

Varyings UnlitPassVertex(Attributes input)
{
    Varyings output = (Varyings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);

    output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
    output.fogCoord = ComputeFogFactor(vertexInput.positionCS.z);
    output.positionWS = vertexInput.positionWS;
    output.positionCS = vertexInput.positionCS;
    #if defined(_VERTEX_COLOR)
    output.color = input.color;
    #endif

    return output;
}

half4 UnlitPassFragment(Varyings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    half2 uv = input.uv;
    half4 texColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);
    half3 color = texColor.rgb * _BaseColor.rgb;
    half alpha = texColor.a * _BaseColor.a;
    #if defined(_VERTEX_COLOR)
    color *= input.color.rgb;
    alpha *= input.color.a;
    #endif

    alpha = AlphaDiscard(alpha, _Cutoff);
    color = AlphaModulate(color, alpha);

    half4 finalColor = half4(color, alpha);
    finalColor.rgb = MixFog(finalColor.rgb, input.fogCoord);
    finalColor.a = OutputAlpha(finalColor.a, IsSurfaceTypeTransparent(_Surface));

    // 環境深度との比較。オクルードされたピクセルは rgb/a とも 0 になり、
    // パススルー合成 (Premultiply) でその部分に現実が見える
    float4 occluded;
    SetOcclusion_float(input.positionWS, finalColor, occluded);
    return half4(occluded);
}

#endif
