Shader "TacticalAccessoryFX/Scope Display"
{
    Properties
    {
        _MainTex ("Scope Render Texture", 2D) = "black" {}
        _Tint ("Display Tint", Color) = (1, 1, 1, 1)
    }

    // The controller supplies a circular mesh with standard UVs. The sampled
    // image fills that mesh, and its texture alpha never cuts holes in the lens.
    // UnityCG is an Editor include, independent of any Universal RP package.
    CGINCLUDE
    #include "UnityCG.cginc"

    sampler2D _MainTex;
    float4 _MainTex_ST;
    float4 _Tint;

    struct ScopeAttributes
    {
        float4 positionOS : POSITION;
        float2 uv : TEXCOORD0;
        UNITY_VERTEX_INPUT_INSTANCE_ID
    };

    struct ScopeVaryings
    {
        float4 positionCS : SV_POSITION;
        float2 uv : TEXCOORD0;
        UNITY_VERTEX_OUTPUT_STEREO
    };

    ScopeVaryings ScopeVertex(ScopeAttributes input)
    {
        ScopeVaryings output;
        UNITY_SETUP_INSTANCE_ID(input);
        UNITY_INITIALIZE_OUTPUT(ScopeVaryings, output);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
        output.positionCS = UnityObjectToClipPos(input.positionOS);
        output.uv = TRANSFORM_TEX(input.uv, _MainTex);
        return output;
    }

    float4 ScopeFragment(ScopeVaryings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        return float4(tex2D(_MainTex, input.uv).rgb * _Tint.rgb, 1.0);
    }
    ENDCG

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
            "RenderType" = "Opaque"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ScopeDisplayURP"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend Off
            ZWrite On
            ZTest LEqual
            Cull Off

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex ScopeVertex
            #pragma fragment ScopeFragment
            #pragma multi_compile_instancing
            ENDCG
        }
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Geometry"
            "RenderType" = "Opaque"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ScopeDisplayBuiltIn"
            Tags { "LightMode" = "Always" }
            Blend Off
            ZWrite On
            ZTest LEqual
            Cull Off

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex ScopeVertex
            #pragma fragment ScopeFragment
            #pragma multi_compile_instancing
            ENDCG
        }
    }

    Fallback Off
}
