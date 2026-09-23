// AMD FidelityFX Super Resolution 1.0 (EASU upscale + RCAS sharpen) for the game image.
//
// The input is the double-width side-by-side texture shared from the game, so both
// passes clamp their taps to the half (eye) that the output pixel belongs to.  Without
// that, the filter kernels would bleed a few pixels of one eye into the other at the
// center seam, which is very visible in stereo.
//
// Pass 0: EASU  - edge adaptive spatial upscale from _MainTex to the render target.
// Pass 1: RCAS  - robust contrast adaptive sharpening, same size in and out.
//
// Constants come from GameUpscaler.cs, which ports FsrEasuCon/FsrRcasCon from the
// CPU side of ffx_fsr1.  They are passed as float bit patterns and reinterpreted
// with asuint here, the same as the reference implementation does.

Shader "Hidden/KatangaFSR"
{
	Properties
	{
		_MainTex ("Game Texture", 2D) = "black" {}
	}

	CGINCLUDE
		#pragma target 5.0
		#pragma only_renderers d3d11

		#include "UnityCG.cginc"

		Texture2D _MainTex;
		SamplerState sampler_LinearClamp;

		float4 _InputSize;		// width, height, 1/width, 1/height of _MainTex
		float4 _OutputSize;		// width, height, 1/width, 1/height of the target

		float4 _EasuCon0;
		float4 _EasuCon1;
		float4 _EasuCon2;
		float4 _EasuCon3;
		float4 _RcasCon;

		// Per pixel limits of the current eye, set before calling into FSR.
		static float2 gEyeMinUV;
		static float2 gEyeMaxUV;
		static int2 gEyeMinPx;
		static int2 gEyeMaxPx;

		#define A_GPU 1
		#define A_HLSL 1
		#include "../FSR/ffx_a.hlsl"

		#define FSR_EASU_F 1
		AF4 FsrEasuRF(AF2 p) { return _MainTex.GatherRed(sampler_LinearClamp, clamp(p, gEyeMinUV, gEyeMaxUV)); }
		AF4 FsrEasuGF(AF2 p) { return _MainTex.GatherGreen(sampler_LinearClamp, clamp(p, gEyeMinUV, gEyeMaxUV)); }
		AF4 FsrEasuBF(AF2 p) { return _MainTex.GatherBlue(sampler_LinearClamp, clamp(p, gEyeMinUV, gEyeMaxUV)); }

		#define FSR_RCAS_F 1
		AF4 FsrRcasLoadF(ASU2 p) { return _MainTex.Load(int3(clamp(p, gEyeMinPx, gEyeMaxPx), 0)); }
		void FsrRcasInputF(inout AF1 r, inout AF1 g, inout AF1 b) {}

		#include "../FSR/ffx_fsr1.hlsl"

		// Which half of the side-by-side output this pixel lands in.
		bool RightHalf(uint2 ip)
		{
			return ip.x >= (uint)(_OutputSize.x * 0.5);
		}

		float4 fragEasu(v2f_img i) : SV_Target
		{
			uint2 ip = uint2(i.pos.xy);

			// Keep the 2x2 gathers one texel inside the eye, in input UV space.
			float halfWidth = _InputSize.x * 0.5;
			float left = RightHalf(ip) ? halfWidth : 0.0;
			float right = RightHalf(ip) ? _InputSize.x : halfWidth;
			gEyeMinUV = float2(left + 1.0, 1.0) * _InputSize.zw;
			gEyeMaxUV = float2(right - 1.0, _InputSize.y - 1.0) * _InputSize.zw;

			AF3 color;
			FsrEasuF(color, ip, asuint(_EasuCon0), asuint(_EasuCon1), asuint(_EasuCon2), asuint(_EasuCon3));
			return float4(color, 1.0);
		}

		float4 fragRcas(v2f_img i) : SV_Target
		{
			uint2 ip = uint2(i.pos.xy);

			// RCAS reads a 3x3 cross at the same resolution, clamp it to the eye in pixels.
			int halfWidth = (int)(_InputSize.x * 0.5);
			gEyeMinPx = int2(RightHalf(ip) ? halfWidth : 0, 0);
			gEyeMaxPx = int2((RightHalf(ip) ? (int)_InputSize.x : halfWidth) - 1, (int)_InputSize.y - 1);

			AF1 r, g, b;
			FsrRcasF(r, g, b, ip, asuint(_RcasCon));
			return float4(r, g, b, 1.0);
		}
	ENDCG

	SubShader
	{
		ZTest Always Cull Off ZWrite Off

		// 0: EASU
		Pass
		{
			CGPROGRAM
			#pragma vertex vert_img
			#pragma fragment fragEasu
			ENDCG
		}

		// 1: RCAS
		Pass
		{
			CGPROGRAM
			#pragma vertex vert_img
			#pragma fragment fragRcas
			ENDCG
		}
	}

	FallBack Off
}
