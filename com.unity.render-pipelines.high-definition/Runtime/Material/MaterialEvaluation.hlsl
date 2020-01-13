// This files include various function uses to evaluate material

//-----------------------------------------------------------------------------
// Lighting structure for light accumulation
//-----------------------------------------------------------------------------

// These structure allow to accumulate lighting accross the Lit material
// AggregateLighting is init to zero and transfer to EvaluateBSDF, but the LightLoop can't access its content.
struct DirectLighting
{
    real3 diffuse;
    real3 specular;
};

struct IndirectLighting
{
    real3 specularReflected;
    real3 specularTransmitted;
};

struct AggregateLighting
{
    DirectLighting   direct;
    IndirectLighting indirect;
};

void AccumulateDirectLighting(DirectLighting src, inout AggregateLighting dst)
{
    dst.direct.diffuse += src.diffuse;
    dst.direct.specular += src.specular;
}

void AccumulateIndirectLighting(IndirectLighting src, inout AggregateLighting dst)
{
    dst.indirect.specularReflected += src.specularReflected;
    dst.indirect.specularTransmitted += src.specularTransmitted;
}

// RED FRIDAY

#define RED_FRIDAY_TOON_SHADING 1

float3 RGB_To_HSV(float3 RGB)
{
    float h, s, v, max, min, d;

    max = min = RGB.r;
    if (RGB.g>max) max = RGB.g; if (RGB.g<min) min = RGB.g;
    if (RGB.b>max) max = RGB.b; if (RGB.b<min) min = RGB.b;
    d = max - min;
    v = max;
    s = (max>0) ? d / max : 0;

    if (s == 0) h = 0;
    else {
        h = 60 * ((RGB.r == max) ? (RGB.g - RGB.b) / d : ((RGB.g == max) ? 2 + (RGB.b - RGB.r) / d : 4 + (RGB.r - RGB.g) / d));
        if (h<0) h += 360;
    }

    return float3(h, s, v);
}

float3 Hue_To_RGB(in float H)
{
    float R = abs(H * 6 - 3) - 1;
    float G = 2 - abs(H * 6 - 2);
    float B = 2 - abs(H * 6 - 4);
    return (float3(R, G, B));
}

float3 HSV_To_RGB(in float3 HSV)
{
    while (HSV.x < 0) HSV.x += 360;
    HSV.x %= 360;

    if (HSV.y == 0) return HSV.zzz;
    else {
        HSV.x /= 60;
        float i;
        float f = HSV.x - (i = floor(HSV.x));
        float p = HSV.z * (1 - HSV.y);
        float q = HSV.z * (1 - HSV.y * f);
        float t = HSV.z * (1 - HSV.y * (1 - f));
        switch (i) {
        case 0: return float3(HSV.z, t, p);
        case 1: return float3(q, HSV.z, p);
        case 2: return float3(p, HSV.z, t);
        case 3: return float3(p, q, HSV.z);
        case 4: return float3(t, p, HSV.z);
        case 5: return float3(HSV.z, p, q);
        default: return float3(1.0f, 0.0f, 1.0f);
        }
    }
}

bool Unity_IsNan_float(float In)
{
   return !(In < 0.0 || In > 0.0 || In == 0.0);
}

float3 ApplyLightRamp(float3 RGB/*, texture2D ramp*/, float increment)
{
    if (Unity_IsNan_float(RGB.x)) RGB.x = 0.0f;
    if (Unity_IsNan_float(RGB.y)) RGB.y = 0.0f;
    if (Unity_IsNan_float(RGB.z)) RGB.z = 0.0f;

    float3 HSV = RGB_To_HSV(RGB);

    //HSV.z = SAMPLE_TEXTURE2D_LOD(ramp, s_linear_clamp_sampler, float2(HSV.z, 0.5f), 0).x;

    /*int i = 0;

    float nextIncrement = increment;

    while (i == 0 && HSV.z > 0.0f)
    {
        increment = nextIncrement;
        i = HSV.z / increment;

        nextIncrement = increment / 2;
    }

    if (i > 0)
    {
        HSV.z = i * increment;
    }*/

    if (HSV.z < 0.15)
    {
        //HSV.z = 0.1;
    }
    else if (HSV.z < 0.5f)
    {
        HSV.z = 0.4f;
    }
    else if (HSV.z < 0.6f)
    {
        HSV.z = 0.4f;
    }
    else if (HSV.z < 1.0f)
    {
        HSV.z = 0.7f;
    }

    //HSV.z = min(HSV.z, 1.0f);

    return HSV_To_RGB(HSV);
}

void ApplyToonShading(inout float3 diffuseLighting, inout float3 specularLighting)
{
    diffuseLighting = ApplyLightRamp(diffuseLighting, 0.3f);
    specularLighting = ApplyLightRamp(specularLighting, 0.5f);
}

void ApplyToonDirectLighting(inout AggregateLighting aggregateLighting)
{
    ApplyToonShading(aggregateLighting.direct.diffuse, aggregateLighting.direct.specular);
}

// RED FRIDAY END

//-----------------------------------------------------------------------------
// Ambient occlusion helper
//-----------------------------------------------------------------------------

// Ambient occlusion
struct AmbientOcclusionFactor
{
    real3 indirectAmbientOcclusion;
    real3 directAmbientOcclusion;
    real3 indirectSpecularOcclusion;
    real3 directSpecularOcclusion;
};

// Get screen space ambient occlusion only:
float GetScreenSpaceDiffuseOcclusion(float2 positionSS)
{
    #if (SHADERPASS == SHADERPASS_RAYTRACING_INDIRECT) || (SHADERPASS == SHADERPASS_RAYTRACING_FORWARD)
        // When we are in raytracing mode, we do not want to take the screen space computed AO texture
        float indirectAmbientOcclusion = 1.0;
    #else
        // Note: When we ImageLoad outside of texture size, the value returned by Load is 0 (Note: On Metal maybe it clamp to value of texture which is also fine)
        // We use this property to have a neutral value for AO that doesn't consume a sampler and work also with compute shader (i.e use ImageLoad)
        // We store inverse AO so neutral is black. So either we sample inside or outside the texture it return 0 in case of neutral
        // Ambient occlusion use for indirect lighting (reflection probe, baked diffuse lighting)
        #ifndef _SURFACE_TYPE_TRANSPARENT
        float indirectAmbientOcclusion = 1.0 - LOAD_TEXTURE2D_X(_AmbientOcclusionTexture, positionSS).x;
        #else
        float indirectAmbientOcclusion = 1.0;
        #endif
    #endif

    return indirectAmbientOcclusion;
}

void GetScreenSpaceAmbientOcclusion(float2 positionSS, float NdotV, float perceptualRoughness, float ambientOcclusionFromData, float specularOcclusionFromData, out AmbientOcclusionFactor aoFactor)
{
    float indirectAmbientOcclusion = GetScreenSpaceDiffuseOcclusion(positionSS);
    float directAmbientOcclusion = lerp(1.0, indirectAmbientOcclusion, _AmbientOcclusionParam.w);

    float roughness = PerceptualRoughnessToRoughness(perceptualRoughness);
    float indirectSpecularOcclusion = GetSpecularOcclusionFromAmbientOcclusion(ClampNdotV(NdotV), indirectAmbientOcclusion, roughness);
    float directSpecularOcclusion = lerp(1.0, indirectSpecularOcclusion, _AmbientOcclusionParam.w);

    aoFactor.indirectSpecularOcclusion = lerp(_AmbientOcclusionParam.rgb, float3(1.0, 1.0, 1.0), min(specularOcclusionFromData, indirectSpecularOcclusion));
    aoFactor.indirectAmbientOcclusion = lerp(_AmbientOcclusionParam.rgb, float3(1.0, 1.0, 1.0), min(ambientOcclusionFromData, indirectAmbientOcclusion));
    aoFactor.directSpecularOcclusion = lerp(_AmbientOcclusionParam.rgb, float3(1.0, 1.0, 1.0), directSpecularOcclusion);
    aoFactor.directAmbientOcclusion = lerp(_AmbientOcclusionParam.rgb, float3(1.0, 1.0, 1.0), directAmbientOcclusion);    
}

// Use GTAOMultiBounce approximation for ambient occlusion (allow to get a tint from the diffuseColor)
void GetScreenSpaceAmbientOcclusionMultibounce(float2 positionSS, float NdotV, float perceptualRoughness, float ambientOcclusionFromData, float specularOcclusionFromData, float3 diffuseColor, float3 fresnel0, out AmbientOcclusionFactor aoFactor)
{
    float indirectAmbientOcclusion = GetScreenSpaceDiffuseOcclusion(positionSS);
    float directAmbientOcclusion = lerp(1.0, indirectAmbientOcclusion, _AmbientOcclusionParam.w);

    float roughness = PerceptualRoughnessToRoughness(perceptualRoughness);
    float indirectSpecularOcclusion = GetSpecularOcclusionFromAmbientOcclusion(ClampNdotV(NdotV), indirectAmbientOcclusion, roughness);
    float directSpecularOcclusion = lerp(1.0, indirectSpecularOcclusion, _AmbientOcclusionParam.w);

    aoFactor.indirectSpecularOcclusion = GTAOMultiBounce(min(specularOcclusionFromData, indirectSpecularOcclusion), fresnel0);
    aoFactor.indirectAmbientOcclusion = GTAOMultiBounce(min(ambientOcclusionFromData, indirectAmbientOcclusion), diffuseColor);
    aoFactor.directSpecularOcclusion = GTAOMultiBounce(directSpecularOcclusion, fresnel0);
    aoFactor.directAmbientOcclusion = GTAOMultiBounce(directAmbientOcclusion, diffuseColor);
}

void ApplyAmbientOcclusionFactor(AmbientOcclusionFactor aoFactor, inout BuiltinData builtinData, inout AggregateLighting lighting)
{
    // Note: In case of deferred Lit, builtinData.bakeDiffuseLighting contains indirect diffuse * surfaceData.ambientOcclusion + emissive,
    // so SSAO is multiplied by emissive which is wrong.
    // Also, we have double occlusion for diffuse lighting since it already had precomputed AO (aka "FromData") applied
    // (the * surfaceData.ambientOcclusion above)
    // This is a tradeoff to avoid storing the precomputed (from data) AO in the GBuffer.
    // (This is also why GetScreenSpaceAmbientOcclusion*() is effectively called with AOFromData = 1.0 in Lit:PostEvaluateBSDF() in the 
    // deferred case since DecodeFromGBuffer will init bsdfData.ambientOcclusion to 1.0 and we will only have SSAO in the aoFactor here)
    builtinData.bakeDiffuseLighting *= aoFactor.indirectAmbientOcclusion;
    lighting.indirect.specularReflected *= aoFactor.indirectSpecularOcclusion;
    lighting.direct.diffuse *= aoFactor.directAmbientOcclusion;
    lighting.direct.specular *= aoFactor.directSpecularOcclusion;
}

#ifdef DEBUG_DISPLAY
// mipmapColor is color use to store texture streaming information in XXXData.hlsl (look for DEBUGMIPMAPMODE_NONE)
void PostEvaluateBSDFDebugDisplay(  AmbientOcclusionFactor aoFactor, BuiltinData builtinData, AggregateLighting lighting, float3 mipmapColor,
                                    inout float3 diffuseLighting, inout float3 specularLighting)
{
    if (_DebugShadowMapMode != 0)
    {
        switch (_DebugShadowMapMode)
        {
        case SHADOWMAPDEBUGMODE_SINGLE_SHADOW:
            diffuseLighting = g_DebugShadowAttenuation.xxx;
            specularLighting = float3(0, 0, 0);
            break ;
        }
    }
    if (_DebugLightingMode != 0)
    {
        // Caution: _DebugLightingMode is used in other part of the code, don't do anything outside of
        // current cases
        switch (_DebugLightingMode)
        {
        case DEBUGLIGHTINGMODE_LUX_METER:
            // Note: We don't include emissive here (and in deferred it is correct as lux calculation of bakeDiffuseLighting don't consider emissive)
            diffuseLighting = lighting.direct.diffuse + builtinData.bakeDiffuseLighting;

            //Compress lighting values for color picker if enabled
            if (_ColorPickerMode != COLORPICKERDEBUGMODE_NONE)
                diffuseLighting = diffuseLighting / LUXMETER_COMPRESSION_RATIO;
            
            specularLighting = float3(0.0, 0.0, 0.0); // Disable specular lighting
            break;

        case DEBUGLIGHTINGMODE_INDIRECT_DIFFUSE_OCCLUSION:
            diffuseLighting = aoFactor.indirectAmbientOcclusion;
            specularLighting = float3(0.0, 0.0, 0.0); // Disable specular lighting
            break;

        case DEBUGLIGHTINGMODE_INDIRECT_SPECULAR_OCCLUSION:
            diffuseLighting = aoFactor.indirectSpecularOcclusion;
            specularLighting = float3(0.0, 0.0, 0.0); // Disable specular lighting
            break;

        case DEBUGLIGHTINGMODE_VISUALIZE_SHADOW_MASKS:
            #ifdef SHADOWS_SHADOWMASK
            diffuseLighting = float3(
                builtinData.shadowMask0 / 2 + builtinData.shadowMask1 / 2,
                builtinData.shadowMask1 / 2 + builtinData.shadowMask2 / 2,
                builtinData.shadowMask2 / 2 + builtinData.shadowMask3 / 2
            );
            specularLighting = float3(0, 0, 0);
            #endif
            break ;
        }
    }
    else if (_DebugMipMapMode != DEBUGMIPMAPMODE_NONE)
    {
        diffuseLighting = mipmapColor;
        specularLighting = float3(0.0, 0.0, 0.0); // Disable specular lighting
    }
}
#endif
