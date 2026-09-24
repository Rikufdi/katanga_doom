using UnityEngine;

// Spatial upscaling of the game image with AMD FidelityFX Super Resolution 1.0.
//
// This is Katanga's take on the "run the game at a lower resolution and let a smart
// upscaler make up the difference" idea that Luke Ross' R.E.A.L. VR mods use with
// DLSS.  Those mods sit inside the game engine, so they get depth and motion vectors
// and can use temporal upscalers like DLSS/FSR2+.  Katanga only receives the finished
// side-by-side frame through a shared texture, with no motion vectors, so a temporal
// upscaler is not possible here.  FSR 1.0 is the best fit: it is purely spatial, vendor
// neutral (works on NVIDIA, AMD and Intel), cheap, and MIT licensed.
//
// The game can therefore render at e.g. 67% of its normal resolution per eye, and EASU
// scales it back up to the size it is displayed at in VR, followed by RCAS sharpening.
// With a scale of 1.0 only RCAS runs, as a higher quality replacement for sharpening.
//
// Settings:
//   --upscale <factor>         Command line.  Output size relative to the game image,
//                              e.g. 1.5.  Also turns the upscaler on.
//   --upscale-sharpness <n>    Command line.  RCAS sharpness in stops, 0 = sharpest.
//   PlayerPrefs "upscale-factor" and "upscale-sharpness" hold the same values.
//   The sharpening toggle (Insert key, left A/X) cycles off / RCAS sharpen /
//   PRISM sharpen / FSR upscale.

public class GameUpscaler
{
    // Toggled by ControllerActions sharpening state.
    public static bool enabled = false;

    // Set by --upscale on the command line, to default the sharpening state to FSR.
    public static bool forceEnabled = false;

    // RCAS sharpening only, no upscale.  The default sharpening mode: it runs once per
    // frame at game resolution, far cheaper than a post effect over both eye buffers.
    public static bool sharpenOnly = false;

    public static float Factor
    {
        get { return Mathf.Clamp(PlayerPrefs.GetFloat("upscale-factor", 1.5f), 1.0f, 3.0f); }
        set { PlayerPrefs.SetFloat("upscale-factor", value); }
    }

    public static float Sharpness
    {
        get { return Mathf.Clamp(PlayerPrefs.GetFloat("upscale-sharpness", 0.2f), 0.0f, 2.0f); }
        set { PlayerPrefs.SetFloat("upscale-sharpness", value); }
    }

    Material easuMaterial;
    Material rcasMaterial;
    RenderTexture upscaled;
    RenderTexture sharpened;

    public GameUpscaler()
    {
        Shader shader = Resources.Load<Shader>("KatangaFSR");
        if (shader == null || !shader.isSupported)
        {
            Debug.LogWarning("GameUpscaler: FSR shader not supported, upscaling disabled.");
            return;
        }
        easuMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        rcasMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
    }

    public bool Supported { get { return easuMaterial != null; } }

    // Run EASU (if scaling up) and RCAS on the side-by-side game texture.  Returns the
    // texture to draw on the screen.  Must be called while the game side is locked out
    // by the setup mutex, since it reads the shared surface.

    public Texture Process(Texture source)
    {
        if (!Supported || source == null)
            return source;

        int width = source.width;
        int height = source.height;
        float factor = sharpenOnly ? 1.0f : Factor;

        Texture rcasInput = source;

        if (factor > 1.0f)
        {
            // Keep the output width even so the eye split stays exactly in the middle.
            int outWidth = Mathf.RoundToInt(width * factor / 2.0f) * 2;
            int outHeight = Mathf.RoundToInt(height * factor);
            Ensure(ref upscaled, outWidth, outHeight);

            SetEasuConstants(easuMaterial, width, height, outWidth, outHeight);
            easuMaterial.SetVector("_InputSize", Size(width, height));
            easuMaterial.SetVector("_OutputSize", Size(outWidth, outHeight));
            Graphics.Blit(source, upscaled, easuMaterial, 0);

            rcasInput = upscaled;
            width = outWidth;
            height = outHeight;
        }

        Ensure(ref sharpened, width, height);
        rcasMaterial.SetVector("_RcasCon", RcasConstants(Sharpness));
        rcasMaterial.SetVector("_InputSize", Size(width, height));
        rcasMaterial.SetVector("_OutputSize", Size(width, height));
        Graphics.Blit(rcasInput, sharpened, rcasMaterial, 1);

        return sharpened;
    }

    public void Release()
    {
        Free(ref upscaled);
        Free(ref sharpened);
    }

    // -----------------------------------------------------------------------------

    static Vector4 Size(int width, int height)
    {
        return new Vector4(width, height, 1.0f / width, 1.0f / height);
    }

    static void Ensure(ref RenderTexture rt, int width, int height)
    {
        if (rt != null && rt.width == width && rt.height == height)
            return;

        Free(ref rt);
        rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        rt.filterMode = FilterMode.Bilinear;
        rt.wrapMode = TextureWrapMode.Clamp;
        rt.useMipMap = false;
        rt.Create();
    }

    static void Free(ref RenderTexture rt)
    {
        if (rt == null)
            return;
        rt.Release();
        Object.Destroy(rt);
        rt = null;
    }

    // Port of FsrEasuCon from ffx_fsr1.  The shader wants these as uint bit patterns,
    // so we pass the float values and reinterpret them with asuint on the GPU side.
    // The whole side-by-side texture is treated as one image, the shader handles the seam.

    static void SetEasuConstants(Material m, float inWidth, float inHeight, float outWidth, float outHeight)
    {
        m.SetVector("_EasuCon0", new Vector4(
            inWidth / outWidth,
            inHeight / outHeight,
            0.5f * inWidth / outWidth - 0.5f,
            0.5f * inHeight / outHeight - 0.5f));
        m.SetVector("_EasuCon1", new Vector4(
            1.0f / inWidth,
            1.0f / inHeight,
            1.0f / inWidth,
            -1.0f / inHeight));
        m.SetVector("_EasuCon2", new Vector4(
            -1.0f / inWidth,
            2.0f / inHeight,
            1.0f / inWidth,
            2.0f / inHeight));
        m.SetVector("_EasuCon3", new Vector4(
            0.0f,
            4.0f / inHeight,
            0.0f,
            0.0f));
    }

    // Port of FsrRcasCon.  Only the 32-bit constant is used; .y is the fp16 packed version.
    static Vector4 RcasConstants(float sharpnessStops)
    {
        return new Vector4(Mathf.Pow(2.0f, -sharpnessStops), 0.0f, 0.0f, 0.0f);
    }
}
