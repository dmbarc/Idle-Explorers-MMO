Shader "Idle Explorers/Character Sprite"
{
    // A sprite that is never hidden by the world it is standing in.
    //
    // ══ WHY THIS EXISTS ═══════════════════════════════════════════════════════
    //
    // A SPUM character is a flat sprite standing in a 3D scene, and a sprite is
    // depth-tested like anything else. Walk up to a copper rock to mine it and the
    // character's feet — then their legs — disappear INTO the rock, because the rock's
    // opaque mesh is genuinely in front of those pixels. Nothing is wrong with the
    // sorting order; the geometry really does overlap.
    //
    // ZTest Always is the whole fix. The character is drawn over whatever is already
    // in the frame, so they can stand at a mining node, behind a tent or against a
    // cliff and still be entirely visible. In a game whose camera is fixed at fifty
    // degrees and whose subject is one character, never losing sight of them is worth
    // more than the depth cue it costs.
    //
    // Layer ordering WITHIN a character is unaffected: that comes from sortingOrder,
    // which SPUM sets per part, and sorting order still decides draw order inside the
    // transparent queue. Hair still draws over the head.
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags
        {
            "Queue"            = "Transparent"
            "RenderType"       = "Transparent"
            "IgnoreProjector"  = "True"
            "PreviewType"      = "Plane"
            "CanUseSpriteAtlas" = "True"
            "RenderPipeline"   = "UniversalPipeline"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest Always
        Blend One OneMinusSrcAlpha

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 color       : COLOR;
                float2 uv          : TEXCOORD0;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Color;
            CBUFFER_END

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv          = TRANSFORM_TEX(IN.uv, _MainTex);

                // SpriteRenderer.color arrives as a vertex colour, which is how the
                // appearance tint, the skin tint and the target highlight all reach
                // the sprite. Dropping it would flatten every one of them to white.
                OUT.color = IN.color * _Color;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv) * IN.color;

                // Premultiplied, to match the Blend above and Unity's own
                // Sprites/Default. Without this every sprite has a bright halo.
                c.rgb *= c.a;
                return c;
            }
            ENDHLSL
        }
    }

    Fallback "Sprites/Default"
}
