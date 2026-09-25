using System;
using UnityEngine;

// Undoes the tone and colour curve Virtual Desktop's OpenXR runtime puts on everything it streams.
//
// Measured on a Quest 3 (adb screencap of the final panel buffer, see AGENTS.md "Colour pipeline"):
// Katanga's eye buffer holds exact values, but through VDXR the panel receives shadows and mid-tones
// lifted (16 -> 23, 64 -> 74, 128 -> 137) and more blue than the Quest's own apps show.  The same
// image through Virtual Desktop's desktop view, or shown by a native Quest app, comes out exact
// apart from the Quest's panel calibration (greys ~10% bluer).  VD's gamma slider doesn't affect
// OpenXR apps.
//
// The correction maps each channel so the panel receives what a native Quest app would give it:
// target = native response, input = inverse of the VDXR response.  Near white the Quest's blue is
// already at 255, so white would lose the bluish tint every grey has and look yellow next to them;
// above level 192 the target keeps the greys' channel ratios and rolls red and green down instead
// (white ~9% dimmer, same tint).
//
// Only for VirtualDesktopXR (measured with runtime 1.0.10); other runtimes need their own
// measurement.  --no-color-correction turns it off, --no-white-fix keeps the plain native target.
// The result is a 256 entry lookup texture that sbsShader and shader2D apply before dithering.

public static class ColorCorrection
{
    // Panel buffer through VDXR (Katanga --show-desktop --no-color-correction showing the reference
    // images), per channel.  Blue reaches 255 from about input 216.
    static readonly float[] vdLevels = { 0, 1, 2, 4, 8, 16, 32, 64, 96, 128, 160, 176, 192, 208, 224, 232, 240, 248, 255 };
    static readonly float[] vdR = { 0, 2.7f, 4.65f, 8.05f, 13.5f, 21.9f, 39.35f, 70.15f, 100.2f, 129.3f, 158.4f, 172.6f, 187.1f, 200.2f, 214.0f, 221.3f, 227.5f, 234.5f, 238.8f };
    static readonly float[] vdG = { 0, 2.05f, 4.0f, 7.65f, 13.8f, 22.95f, 41.2f, 74.0f, 105.85f, 136.9f, 167.8f, 182.9f, 196.6f, 212.2f, 226.0f, 233.0f, 241.3f, 248.8f, 254.6f };
    static readonly float[] vdB = { 0, 4.05f, 7.0f, 12.25f, 19.15f, 29.4f, 50.55f, 88.1f, 124.65f, 160.1f, 195.6f, 212.7f, 230.0f, 246.4f, 255, 255, 255, 255, 255 };

    // Panel buffer for the same image in the Quest's own Files viewer (Quest contrast setting off).
    static readonly float[] levels = { 0, 1, 2, 4, 8, 16, 32, 64, 96, 128, 192, 255 };
    static readonly float[] nativeR = { 0, 1.0f, 1.95f, 3.85f, 7.5f, 15.7f, 31.15f, 63.15f, 94.9f, 126.8f, 190.05f, 252.25f };
    static readonly float[] nativeG = { 0, 1.0f, 2.0f, 4.0f, 7.55f, 15.75f, 31.1f, 63.1f, 94.8f, 126.7f, 189.85f, 252.7f };
    static readonly float[] nativeB = { 0, 1.15f, 2.35f, 4.7f, 9.2f, 18.45f, 35.4f, 70.45f, 105.0f, 140.0f, 209.15f, 255.0f };

    const float whiteKnee = 192.0f;

    public static void Setup(string runtimeName)
    {
        bool vdxr = runtimeName != null && runtimeName.IndexOf("VirtualDesktop", StringComparison.OrdinalIgnoreCase) >= 0;
        bool enabled = vdxr && !KatangaArgs.Has("--no-color-correction");
        bool whiteFix = !KatangaArgs.Has("--no-white-fix");

        Shader.SetGlobalFloat("_KatangaColorCorrect", enabled ? 1.0f : 0.0f);
        if (!enabled)
        {
            Debug.Log("Color correction: off" + (vdxr ? " (--no-color-correction)" : ", runtime " + runtimeName + " is not measured"));
            return;
        }

        Texture2D lut = new Texture2D(256, 1, TextureFormat.RGBAHalf, false, true)
        {
            name = "Katanga Color LUT",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        Color[] entries = new Color[256];
        for (int i = 0; i < 256; i++)
        {
            float r = Invert(vdR, Target(nativeR, nativeG, i, whiteFix));
            float g = Invert(vdG, Target(nativeG, nativeG, i, whiteFix));
            float b = Invert(vdB, Target(nativeB, nativeG, i, whiteFix));
            entries[i] = new Color(r / 255.0f, g / 255.0f, b / 255.0f, 1.0f);
        }
        lut.SetPixels(entries);
        lut.Apply(false, true);
        Shader.SetGlobalTexture("_KatangaColorLut", lut);

        Debug.Log(String.Format("Color correction: on for {0}{1}. 16 -> {2}, 64 -> {3}, 128 -> {4}, 255 -> {5} (G)",
            runtimeName, whiteFix ? ", white fix" : "", Mathf.Round(entries[16].g * 2550) / 10, Mathf.Round(entries[64].g * 2550) / 10,
            Mathf.Round(entries[128].g * 2550) / 10, Mathf.Round(entries[255].g * 2550) / 10));
    }

    // What the panel should receive for input level x: the native response, and above the knee
    // the green curve times this channel's ratio at the knee, rolled down so blue ends at 255.
    static float Target(float[] native, float[] nativeGreen, float x, bool whiteFix)
    {
        if (!whiteFix || x <= whiteKnee)
            return Lerp(native, x);

        float ratio = Lerp(native, whiteKnee) / Lerp(nativeGreen, whiteKnee);
        float blueRatio = Lerp(nativeB, whiteKnee) / Lerp(nativeGreen, whiteKnee);
        float whiteScale = 255.0f / (Lerp(nativeGreen, 255.0f) * blueRatio);
        float t = Mathf.SmoothStep(0.0f, 1.0f, (x - whiteKnee) / (255.0f - whiteKnee));
        return Lerp(nativeGreen, x) * ratio * Mathf.Lerp(1.0f, whiteScale, t);
    }

    // Piecewise linear through the measured native points.
    static float Lerp(float[] table, float x)
    {
        for (int i = 1; i < levels.Length; i++)
            if (x <= levels[i])
                return Mathf.Lerp(table[i - 1], table[i], (x - levels[i - 1]) / (levels[i] - levels[i - 1]));
        return table[table.Length - 1];
    }

    // The input level that makes VDXR deliver the wanted panel value.
    static float Invert(float[] response, float wanted)
    {
        if (wanted <= 0.0f)
            return 0.0f;
        for (int i = 1; i < vdLevels.Length; i++)
            if (wanted <= response[i])
                return Mathf.Lerp(vdLevels[i - 1], vdLevels[i], (wanted - response[i - 1]) / Mathf.Max(response[i] - response[i - 1], 1e-3f));
        return 255.0f;
    }
}
