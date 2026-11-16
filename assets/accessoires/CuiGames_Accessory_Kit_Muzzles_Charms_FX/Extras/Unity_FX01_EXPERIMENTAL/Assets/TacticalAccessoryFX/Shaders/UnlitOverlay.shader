Shader "TacticalAccessoryFX/Unlit Overlay"
{
    Properties
    {
        _MainTex ("Texture (RGBA)", 2D) = "white" {}
        [HDR] _Color ("Emission Color / Opacity", Color) = (1, 1, 1, 1)
    }

    // UnityCG is shipped with the Editor. No Universal RP package is required.
    // Both pipeline passes intentionally use the same unlit calculation.
    // HDR color can contribute to bloom when the host project enables bloom.
    CGINCLUDE
    #include "UnityCG.cginc"

    sampler2D _MainTex;
    float4 _MainTex_ST;
    float4 _Color;

    struct OverlayAttributes
    {
        float4 positionOS : POSITION;
        float2 uv : TEXCOORD0;
        UNITY_VERTEX_INPUT_INSTANCE_ID
    };

    struct OverlayVaryings
    {
        float4 positionCS : SV_POSITION;
        float2 uv : TEXCOORD0;
        UNITY_VERTEX_OUTPUT_STEREO
    };

    OverlayVaryings OverlayVertex(OverlayAttributes input)
    {
        OverlayVaryings output;
        UNITY_SETUP_INSTANCE_ID(input);
        UNITY_INITIALIZE_OUTPUT(OverlayVaryings, output);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
        output.positionCS = UnityObjectToClipPos(input.positionOS);
        output.uv = TRANSFORM_TEX(input.uv, _MainTex);
        return output;
    }

    float4 OverlayFragment(OverlayVaryings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        float4 texel = tex2D(_MainTex, input.uv);
        // Straight alpha: RGB stays HDR while opacity remains in [0, 1].
        return float4(texel.rgb * _Color.rgb, saturate(texel.a * _Color.a));
    }
    ENDCG

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "UnlitOverlayURP"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex OverlayVertex
            #pragma fragment OverlayFragment
            #pragma multi_compile_instancing
            ENDCG
        }
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "UnlitOverlayBuiltIn"
            Tags { "LightMode" = "Always" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex OverlayVertex
            #pragma fragment OverlayFragment
            #pragma multi_compile_instancing
            ENDCG
        }
    }

    Fallback Off
}
