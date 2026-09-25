// Last steps of every screen shader, where the image goes to the 8 bit eye buffer:
// the runtime colour correction (ColorCorrection.cs) and dithering.

#ifndef KATANGA_COLOR_INCLUDED
#define KATANGA_COLOR_INCLUDED

sampler2D _KatangaColorLut;		// 256 x 1, set globally by ColorCorrection.cs
float _KatangaColorCorrect;		// 1 when the runtime's curve is corrected

float3 KatangaCorrect(float3 rgb)
{
	if (_KatangaColorCorrect < 0.5)
		return rgb;
	// Entry centres, so bilinear filtering interpolates between neighbouring levels and keeps the
	// precision of 10 bit input.
	float3 u = saturate(rgb) * (255.0 / 256.0) + (0.5 / 256.0);
	return float3(tex2Dlod(_KatangaColorLut, float4(u.r, 0.5, 0, 0)).r,
	              tex2Dlod(_KatangaColorLut, float4(u.g, 0.5, 0, 0)).g,
	              tex2Dlod(_KatangaColorLut, float4(u.b, 0.5, 0, 0)).b);
}

// The eye buffer is 8 bit, and the game image is often 10 bit.  Quantizing smooth dark
// gradients to 8 bit makes visible bands, so add triangular noise of +-1 step first.
// It changes every frame, which at 90 Hz averages into a smooth gradient rather than
// a fixed grain.  Integer hash, so it is stable across GPUs.
float KatangaRand(uint2 p, uint seed)
{
	uint h = p.x * 1973u + p.y * 9277u + seed * 26699u;
	h = (h ^ 61u) ^ (h >> 16);
	h *= 9u;
	h ^= h >> 4;
	h *= 0x27d4eb2du;
	h ^= h >> 15;
	return h * (1.0 / 4294967296.0);
}

float4 KatangaDither(float4 col, float4 screenPos, float strength)
{
	uint2 p = uint2(screenPos.xy);
	uint frame = (uint)(_Time.y * 90.0) + unity_StereoEyeIndex * 7919u;
	float noise = KatangaRand(p, frame) + KatangaRand(p, frame + 104729u) - 1.0;
	// No dither on exact black or white, so true black stays exactly 0 and never
	// flickers up to 1.  It fades in over the first 8 bit step.
	float3 amount = saturate(col.rgb * 255.0) * saturate((1.0 - col.rgb) * 255.0);
	col.rgb += strength * amount * noise / 255.0;
	return col;
}

// Correction, then dither.
float4 KatangaOutput(float4 col, float4 screenPos, float dither)
{
	col.rgb = KatangaCorrect(col.rgb);
	return KatangaDither(col, screenPos, dither);
}

#endif
