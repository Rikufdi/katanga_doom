// Default Shader from creating Unlit.
// Modified to handle SBS texture using which eye is active in VR.
//
// unity_StereoEyeIndex is the variable for which eye is active.

Shader "Unlit/sbsShader"
{
	Properties
	{
		[NoScaleOffset] _MainTex ("_bothEyes Texture", 2D) = "grey" {}
		[NoScaleOffset] _LeftTex ("Left eye (set by ScreenImage.cs)", 2D) = "grey" {}
		[NoScaleOffset] _RightTex ("Right eye (set by ScreenImage.cs)", 2D) = "grey" {}
	}
	SubShader
	{
		Tags { "RenderType" = "Opaque" }
		LOD 100

		Pass
		{
			CGPROGRAM
			#pragma vertex vert
			// EYE_TEXTURES: ScreenImage.cs has split the SBS image into one mipmapped,
			// anisotropically filtered texture per eye.  Without it we fall back to
			// sampling the SBS texture directly, as before.
			#pragma multi_compile_local __ EYE_TEXTURES
			#pragma fragment frag
			
			#include "UnityCG.cginc" 

			// Instancing/stereo macros are required for Single Pass Instanced XR rendering,
			// where both eyes are drawn in one instanced draw call.
			struct appdata
			{
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct v2f
			{
				float2 uv : TEXCOORD0;
				float4 vertex : SV_POSITION;
				UNITY_VERTEX_OUTPUT_STEREO
			};

			sampler2D _MainTex;			
			float4 _MainTex_ST;
			sampler2D _LeftTex;
			sampler2D _RightTex;

			v2f vert (appdata v)
			{
				v2f o;
				UNITY_SETUP_INSTANCE_ID(v);
				UNITY_INITIALIZE_OUTPUT(v2f, o);
				UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
			#if defined(EYE_TEXTURES)
				// Eye split and texture scale/offset were already applied by ScreenImage.
                o.vertex = UnityObjectToClipPos(v.vertex);
				o.uv = v.uv;
			#else

				float4 sb;

				// Modify uv fetched, based on the active eye,
				// by applying a different bias/offset to alternate eyes.
				// The net effect here is to show either the right or
				// left half of the incoming Texture, half for each eye.
				// This is called once per eye, including for Single Pass Instanced where
				// unity_StereoEyeIndex comes from the instance ID set up above.
				//
				// The scale/offset is applied directly rather than with
				// UnityStereoScreenSpaceUVAdjust, because that helper only does anything
				// for the old double-wide single pass mode, and is a no-op for instanced.

				sb.x = 0.5;									// Scale by half as it's 2x width
				sb.y = 1.0;									// No vertical scaling, full size.
				sb.z = unity_StereoEyeIndex ? 0.0 : 0.5;	// Offset to half for left eye
				sb.w = 0.0;									// No vertical offset.
				v.uv = v.uv * sb.xy + sb.zw;
					
                o.vertex = UnityObjectToClipPos(v.vertex);
				o.uv = TRANSFORM_TEX(v.uv, _MainTex);
			#endif

				return o;
			}
			
			// Clear up shimmering using multisampling as described:
			// https://developer.oculus.com/blog/common-rendering-mistakes-how-to-find-them-and-how-to-fix-them/

			fixed4 tex2Dmultisample(sampler2D tex, float2 uv)
			{
				float2 dx = ddx(uv) * 0.25;
				float2 dy = ddy(uv) * 0.25;

				float4 sample0 = tex2D(tex, uv + dx + dy);
				float4 sample1 = tex2D(tex, uv + dx - dy);
				float4 sample2 = tex2D(tex, uv - dx + dy);
				float4 sample3 = tex2D(tex, uv - dx - dy);

				return (sample0 + sample1 + sample2 + sample3) * 0.25;
			}

			fixed4 frag (v2f i) : SV_Target
			{
				UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

			#if defined(EYE_TEXTURES)
				// Mips + 16x anisotropic filtering do the anti-aliasing here.  Gradients
				// are taken outside the per eye branch so both samples stay well defined.
				float2 dx = ddx(i.uv);
				float2 dy = ddy(i.uv);
				if (unity_StereoEyeIndex == 0)
					return tex2Dgrad(_LeftTex, i.uv, dx, dy);
				return tex2Dgrad(_RightTex, i.uv, dx, dy);
			#else
				// sample the texture
				fixed4 col = tex2Dmultisample(_MainTex, i.uv);
				return col;
			#endif
			}
			ENDCG
		}
	}
}
