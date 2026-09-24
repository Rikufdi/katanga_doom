// Copies the game's side-by-side image into ScreenImage's snapshot, in one draw.
//
// Katanga renders in Gamma color space: values pass through untouched.  Games whose back
// buffer is an _SRGB format (DXGI 29 R8G8B8A8_UNORM_SRGB, 91 B8G8R8A8_UNORM_SRGB) share an
// sRGB texture, and the shader view the native plugin creates on it has to keep that format,
// so every sample comes back converted to linear.  In a Gamma pipeline that shows the game far
// too dark with crushed shadows.  _LinearToSRGB turns such samples back into the stored sRGB
// values, so those games look the same as every other game.

Shader "Hidden/KatangaSnapshot"
{
	Properties
	{
		_MainTex ("Game image", 2D) = "black" {}
		_LinearToSRGB ("Re-encode linear samples to sRGB", Float) = 0
	}
	SubShader
	{
		Cull Off ZWrite Off ZTest Always

		Pass
		{
			CGPROGRAM
			#pragma vertex vert_img
			#pragma fragment frag
			#include "UnityCG.cginc"

			sampler2D _MainTex;
			float _LinearToSRGB;

			float4 frag (v2f_img i) : SV_Target
			{
				float4 col = tex2D(_MainTex, i.uv);
				if (_LinearToSRGB > 0.5)
					col.rgb = float3(LinearToGammaSpaceExact(col.r),   // exact sRGB curve,
					                 LinearToGammaSpaceExact(col.g),   // one channel at a time
					                 LinearToGammaSpaceExact(col.b));
				return col;
			}
			ENDCG
		}
	}
}
