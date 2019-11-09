Shader "Hidden/Universal Render Pipeline/ExpandDepth"
{
    Properties
    {
        _MainTex("Source", 2D) = "white" {}
    }

    HLSLINCLUDE

        #pragma target 3.5

        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Filtering.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/Shaders/PostProcessing/Common.hlsl"

        TEXTURE2D_X(_MainTex);

        TEXTURE2D_X_FLOAT(_CameraDepthTexture);

        float4 _MainTex_TexelSize;

        float _MaxRadius;

        #define BLUR_KERNEL 0

        #if BLUR_KERNEL == 0

        // Offsets & coeffs for optimized separable bilinear 3-tap gaussian (5-tap equivalent)
        const static int kTapCount = 3;
        const static float kOffsets[] = {
            -1.33333333,
             0.00000000,
             1.33333333
        };
        const static half kCoeffs[] = {
             0.35294118,
             0.29411765,
             0.35294118
        };

        #elif BLUR_KERNEL == 1

        // Offsets & coeffs for optimized separable bilinear 5-tap gaussian (9-tap equivalent)
        const static int kTapCount = 5;
        const static float kOffsets[] = {
            -3.23076923,
            -1.38461538,
             0.00000000,
             1.38461538,
             3.23076923
        };
        const static half kCoeffs[] = {
             0.07027027,
             0.31621622,
             0.22702703,
             0.31621622,
             0.07027027
        };

        #endif

        half4 Blur(Varyings input, float2 dir, float premultiply)
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            // Use the center CoC as radius
            int2 positionSS = int2(_MainTex_TexelSize.zw * input.uv);

            float2 offset = _MainTex_TexelSize.xy * dir * _MaxRadius;
            half4 acc = 0.0;

            UNITY_UNROLL
            for (int i = 0; i < kTapCount; i++)
            {
                float2 sampCoord = input.uv + kOffsets[i] * offset;
                half3 sampColor = SAMPLE_TEXTURE2D_X(_MainTex, sampler_LinearClamp, sampCoord).xyz;

                acc += half4(sampColor, 1.0) * kCoeffs[i];
            }

            acc.xyz /= acc.w + 1e-5; // Zero-div guard
            return half4(acc.xyz, 1.0);
        }

        half4 FragBlurH(Varyings input) : SV_Target
        {
            return Blur(input, float2(1.0, 0.0), 1.0);
        }

        half4 FragBlurV(Varyings input) : SV_Target
        {
            return Blur(input, float2(0.0, 1.0), 0.0);
        }

        half4 FragLevels(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            half3 baseColor = LOAD_TEXTURE2D_X(_MainTex, _MainTex_TexelSize.zw * input.uv).xyz;

            return half4(baseColor, 1.0);
        }

        half4 FragBlit(Varyings input) : SV_Target
        {
            half4 col = LOAD_TEXTURE2D_X(_MainTex, _MainTex_TexelSize.zw * input.uv);

            return col;
        }

    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        LOD 100
        ZTest Always ZWrite Off Cull Off

        Pass
        {
            Name "Gaussian Depth Of Field Blur Horizontal"

            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment FragBlurH
            ENDHLSL
        }

        Pass
        {
            Name "Gaussian Depth Of Field Blur Vertical"

            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment FragBlurV
            ENDHLSL
        }

        Pass
        {
            Name "Gaussian Depth Of Field Composite"

            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment FragLevels
                #pragma multi_compile_local _ _HIGH_QUALITY_SAMPLING
            ENDHLSL
        }

        Pass
        {
            Name "Blit"

            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment FragBlit
            ENDHLSL
        }
    }
}
