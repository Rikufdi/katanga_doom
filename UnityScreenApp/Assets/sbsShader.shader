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
		_Dither ("Dither to the 8 bit eye buffer (set by ScreenImage.cs)", Float) = 1
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
			#pragma target 4.0
			
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
			float _Dither;

			// The eye buffer is 8 bit, and the game image is often 10 bit.  Quantizing smooth dark
			// gradients to 8 bit makes visible bands, so add triangular noise of +-1 step first.
			// It changes every frame, which at 90 Hz averages into a smooth gradient rather than
			// a fixed grain.  Integer hash, so it is stable across GPUs.
			float Rand(uint2 p, uint seed)
			{
				uint h = p.x * 1973u + p.y * 9277u + seed * 26699u;
				h = (h ^ 61u) ^ (h >> 16);
				h *= 9u;
				h ^= h >> 4;
				h *= 0x27d4eb2du;
				h ^= h >> 15;
				return h * (1.0 / 4294967296.0);
			}

			float4 Dither(float4 col, float4 screenPos)
			{
				uint2 p = uint2(screenPos.xy);
				uint frame = (uint)(_Time.y * 90.0) + unity_StereoEyeIndex * 7919u;
				float noise = Rand(p, frame) + Rand(p, frame + 104729u) - 1.0;
				// No dither on exact black or white, so true black stays exactly 0 and never
				// flickers up to 1.  It fades in over the first 8 bit step.
				float3 amount = saturate(col.rgb * 255.0) * saturate((1.0 - col.rgb) * 255.0);
				col.rgb += _Dither * amount * noise / 255.0;
				return col;
			}

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

			float4 tex2Dmultisample(sampler2D tex, float2 uv)
			{
				float2 dx = ddx(uv) * 0.25;
				float2 dy = ddy(uv) * 0.25;

				float4 sample0 = tex2D(tex, uv + dx + dy);
				float4 sample1 = tex2D(tex, uv + dx - dy);
				float4 sample2 = tex2D(tex, uv - dx + dy);
				float4 sample3 = tex2D(tex, uv - dx - dy);

				return (sample0 + sample1 + sample2 + sample3) * 0.25;
			}

			float4 frag (v2f i) : SV_Target
			{
				UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

			#if defined(EYE_TEXTURES)
				// Mips + 16x anisotropic filtering do the anti-aliasing here.  Gradients
				// are taken outside the per eye branch so both samples stay well defined.
				float2 dx = ddx(i.uv);
				float2 dy = ddy(i.uv);
				float4 col = unity_StereoEyeIndex == 0 ? tex2Dgrad(_LeftTex, i.uv, dx, dy)
				                                       : tex2Dgrad(_RightTex, i.uv, dx, dy);
				return Dither(col, i.vertex);
			#else
				// sample the texture
				float4 col = tex2Dmultisample(_MainTex, i.uv);
				return Dither(col, i.vertex);
			#endif
			}
			ENDCG
		}
	}
}
