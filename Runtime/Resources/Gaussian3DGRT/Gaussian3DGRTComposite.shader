// SPDX-License-Identifier: MIT
Shader "Hidden/Gaussian Splatting/3DGRT Composite"
{
    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #pragma use_dxc
            #include "UnityCG.cginc"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                float2 position = float2(vertexID & 1, (vertexID >> 1) & 1) * 4.0 - 1.0;
                output.positionCS = float4(position, 1, 1);
                return output;
            }

            Texture2D _Gaussian3DGRTOutput;

            float4 Frag(Varyings input) : SV_Target
            {
                return _Gaussian3DGRTOutput.Load(int3(input.positionCS.xy, 0));
            }
            ENDHLSL
        }
    }
}
