using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

// Measures what Katanga really writes into the headset's eye buffer.  --color-diagnostics
//
// The project renders in Gamma color space: shaders pass values through untouched, so a game
// pixel stored as 0.5 should reach the headset stored as 0.5 (128 in 8 bit).  The OpenXR runtime
// may hand us an sRGB swap chain, though, and if Unity writes into it through an sRGB view the
// GPU encodes every pixel a second time: 0.5 becomes ~0.735 (188), near-black 0.05 becomes ~0.25.
// That would raise black levels and wash out everything.
//
// For a few frames this clears the eye buffer to known levels with nothing drawn on top, copies
// the eye buffer's raw bits (CopyTexture, no conversion) and reads them back.  Player.log gets
// the format information and, per level, the stored value next to what correct and double
// encoded storage would give.

public class ColorDiagnostics : MonoBehaviour
{
    public static bool Requested
    {
        get { return Array.IndexOf(Environment.GetCommandLineArgs(), "--color-diagnostics") >= 0; }
    }

    static readonly float[] levels = { 0.5f, 0.2f, 0.05f, 0.02f, 0.0f, 1.0f };

    Camera cam;
    CommandBuffer copy;
    RenderTexture target;
    float currentLevel;

    private IEnumerator Start()
    {
        cam = GetComponent<Camera>();

        // Let XR settle: the eye texture only exists once the session runs.
        yield return new WaitForSeconds(5.0f);

        RenderTextureDescriptor eye = XRSettings.eyeTextureDesc;
        print(String.Format("[Color] Unity color space: {0}", QualitySettings.activeColorSpace));
        print(String.Format("[Color] Eye texture: {0}x{1} x{2} slices, graphicsFormat {3}, colorFormat {4}, sRGB {5}, dimension {6}",
            eye.width, eye.height, eye.volumeDepth, eye.graphicsFormat, eye.colorFormat, eye.sRGB, eye.dimension));
        print(String.Format("[Color] XR device: {0}, render mode: {1}", XRSettings.loadedDeviceName, XRSettings.stereoRenderingMode));

        // Same format as the eye texture, so CopyTexture copies raw bits with no conversion.
        RenderTextureDescriptor desc = eye;
        desc.dimension = TextureDimension.Tex2D;
        desc.volumeDepth = 1;
        desc.msaaSamples = 1;
        desc.depthBufferBits = 0;
        desc.useMipMap = false;
        target = new RenderTexture(desc) { name = "Color Diagnostics Copy" };
        target.Create();

        copy = new CommandBuffer { name = "Color Diagnostics" };
        copy.CopyTexture(BuiltinRenderTextureType.CameraTarget, 0, 0, target, 0, 0);
        cam.AddCommandBuffer(CameraEvent.AfterEverything, copy);

        CameraClearFlags savedFlags = cam.clearFlags;
        Color savedBackground = cam.backgroundColor;
        int savedMask = cam.cullingMask;

        foreach (float level in levels)
        {
            currentLevel = level;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(level, level, level, 1.0f);
            cam.cullingMask = 0;

            // A few frames so the copy surely contains a frame cleared to this level.
            for (int i = 0; i < 4; i++)
                yield return null;

            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(target, 0);
            while (!request.done)
                yield return null;
            Report(level, request);
        }

        cam.RemoveCommandBuffer(CameraEvent.AfterEverything, copy);

        // --color-levels: hold the whole view at exact 8 bit levels, 4 s each, so what the headset
        // really displays can be captured (adb screencap) and compared against the input.
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--color-levels") >= 0)
        {
            int[] steps = { 0, 1, 2, 3, 4, 5, 6, 8, 10, 12, 16, 20, 24, 32, 48, 64, 128, 192, 255 };
            foreach (int step in steps)
            {
                cam.backgroundColor = new Color32((byte)step, (byte)step, (byte)step, 255);
                print(String.Format("[Color] level {0} shown", step));
                yield return new WaitForSeconds(4.0f);
            }
            print("[Color] levels done");
        }

        cam.clearFlags = savedFlags;
        cam.backgroundColor = savedBackground;
        cam.cullingMask = savedMask;
        target.Release();
        print("[Color] Diagnostics done, normal rendering restored.");
    }

    void Report(float level, AsyncGPUReadbackRequest request)
    {
        if (request.hasError)
        {
            print(String.Format("[Color] level {0:F2}: readback failed", level));
            return;
        }

        // Center pixel.  Only 8 bit per channel formats are decoded here, the others are
        // reported raw so they can be interpreted from the logged format.
        var data = request.GetData<byte>();
        int bpp = data.Length / (target.width * target.height);
        int offset = ((target.height / 2) * target.width + target.width / 2) * bpp;

        float linearToSrgb = level <= 0.0031308f ? level * 12.92f : 1.055f * Mathf.Pow(level, 1.0f / 2.4f) - 0.055f;
        string raw = "";
        for (int i = 0; i < bpp && offset + i < data.Length; i++)
            raw += data[offset + i].ToString("X2") + " ";

        if (bpp == 4)
            print(String.Format("[Color] cleared to {0:F2}: stored R={1} G={2} B={3} | correct would be {4}, encoded twice would be {5}",
                level, data[offset], data[offset + 1], data[offset + 2],
                Mathf.RoundToInt(level * 255), Mathf.RoundToInt(linearToSrgb * 255)));
        else
            print(String.Format("[Color] cleared to {0:F2}: raw bytes {1}({2} bytes per pixel)", level, raw, bpp));
    }
}
