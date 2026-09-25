using System;
using UnityEngine;
using UnityEngine.XR;

// Prepares the side-by-side image on the big screen for high quality sampling.
//
// The game's shared texture (and the FSR output) has no mipmaps.  Whenever the screen
// covers fewer headset pixels than the game renders, which is most of the time, sampling
// it directly aliases and text shimmers as the head moves.  The old sbsShader countered
// that with a 4 tap supersample, which only helps up to about 2:1.
//
// Here each eye is copied into its own texture with a full mip chain and 16x anisotropic
// filtering, so any screen size, distance, curve or viewing angle is filtered properly.
// Separate textures per eye also keep the coarser mip levels from mixing the two eyes
// together at the center seam.  The copy is two blits and a mip generation per game
// frame, a fraction of a millisecond.
//
// Runs in LateUpdate, after LaunchAndPlay/SlideShow/GameUpscaler have set the texture,
// and while LaunchAndPlay still holds the setup mutex for the game's shared surface.

public class ScreenImage : MonoBehaviour
{
    // Set by LaunchAndPlay while the texture on the screen is the live game image, which
    // changes every frame.  Otherwise we only redo the copy when the texture changes.
    public static bool sourceIsLive = false;

    // Set by LaunchAndPlay when the game's back buffer is an _SRGB format.  Its shared texture
    // then samples as linear, and the snapshot turns it back into sRGB (see KatangaSnapshot).
    public static bool sourceIsSRGBView = false;

    // Dither where the image drops to the 8 bit eye buffer, see sbsShader.  --no-dither turns
    // it off, for instance if a video encoder shows the noise.
    public static readonly bool dither = !KatangaArgs.Has("--no-dither");

    // Supersampling of the whole VR view, 1.0 = the runtime's recommended size.
    public static float RenderScale
    {
        get { return Mathf.Clamp(PlayerPrefs.GetFloat("render-scale", 1.0f), 0.5f, 2.0f); }
        set { PlayerPrefs.SetFloat("render-scale", value); }
    }

    // Slightly sharper than plain trilinear, which reads soft for text in VR.
    // --mip-bias overrides it, for measuring sharpness against aliasing.
    static readonly float mipBias = MipBiasOption(-0.5f);

    static float MipBiasOption(float fallback)
    {
        string[] args = KatangaArgs.All;
        int at = Array.IndexOf(args, "--mip-bias");
        if (at >= 0 && at + 1 < args.Length &&
            float.TryParse(args[at + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float bias))
        {
            print("--mip-bias: " + bias);
            return bias;
        }
        return fallback;
    }

    Renderer screen;
    RenderTexture leftEye;
    RenderTexture rightEye;

    Texture lastSource;
    Vector2 lastScale, lastOffset;

    private void Start()
    {
        screen = GetComponent<Renderer>();

        if (RenderScale != 1.0f)
        {
            XRSettings.eyeTextureResolutionScale = RenderScale;
            print("Eye texture resolution scale: " + RenderScale);
        }
    }

    private void LateUpdate()
    {
        Material material = screen.material;

        // Only the stereo shader uses per eye textures; desktop mode switches to shader2D.
        if (!material.HasProperty("_LeftTex"))
            return;

        Texture source = material.mainTexture;
        if (source == null)
        {
            material.DisableKeyword("EYE_TEXTURES");
            return;
        }

        Vector2 scale = material.mainTextureScale;
        Vector2 offset = material.mainTextureOffset;

        if (!sourceIsLive && source == lastSource && scale == lastScale && offset == lastOffset)
            return;
        lastSource = source;
        lastScale = scale;
        lastOffset = offset;

        // The live game texture is shared with the game, which writes each new frame into it
        // with no GPU sync against us.  Reading it twice, once per eye, lets a write land in
        // between, and then the eyes show two different frames.  So take one snapshot of the
        // whole side-by-side image first, and cut both eyes from that.
        if (sourceIsLive)
        {
            // Count first: a frame counted before the snapshot is queued is complete on the GPU.
            TrackFrameDelivery();
            source = Snapshot(source);

            // We have this frame's image, the game can start on its next one.
            LaunchAndPlay.GameFrameTaken();
        }

        int width = Mathf.Max(1, source.width / 2);
        int height = Mathf.Max(1, source.height);
        Ensure(ref leftEye, width, height, "Screen Left Eye");
        Ensure(ref rightEye, width, height, "Screen Right Eye");

        // Same mapping as sbsShader always used: the left eye (index 0) shows the right
        // half of the texture, the right eye the left half, and the material's texture
        // scale/offset (SlideShow flips Y with it) applies on top.
        Vector2 halfScale = new Vector2(0.5f * scale.x, scale.y);
        Graphics.Blit(source, leftEye, halfScale, new Vector2(0.5f * scale.x + offset.x, offset.y));
        Graphics.Blit(source, rightEye, halfScale, new Vector2(offset.x, offset.y));

        material.SetFloat("_Dither", dither ? 1.0f : 0.0f);
        material.SetTexture("_LeftTex", leftEye);
        material.SetTexture("_RightTex", rightEye);
        material.EnableKeyword("EYE_TEXTURES");
    }

    // Did each snapshot get a new game frame?  GamePlugin counts the game's real Presents in the
    // pacing mapping, once the frame has finished on the GPU.  Per snapshot: +1 is a new frame, +0 means no new frame arrived in time and
    // the headset shows the last one again (a stale frame), +2 or more means frames were never
    // shown.  Summary every 5 s, and a timestamp for stale frames at most once a second.

    [System.Runtime.InteropServices.DllImport("UnityNativePlugin64")]
    private static extern int GamePresentCount();

    int lastPresentCount = -1;
    int freshFrames, staleFrames, skippedFrames;
    float nextSummary, nextStaleLog;

    void TrackFrameDelivery()
    {
        int count = GamePresentCount();
        if (count < 0)
            return;   // no pacing mapping, e.g. frame sync off
        if (lastPresentCount < 0 || count < lastPresentCount)
        {
            lastPresentCount = count;
            nextSummary = Time.realtimeSinceStartup + 5.0f;
            return;
        }

        int delta = count - lastPresentCount;
        lastPresentCount = count;
        if (delta == 1)
            freshFrames++;
        else if (delta == 0)
        {
            staleFrames++;
            if (Time.realtimeSinceStartup >= nextStaleLog)
            {
                print(String.Format("[{0:HH:mm:ss.fff}] Stale frame: no new game frame for this headset frame", DateTime.Now));
                nextStaleLog = Time.realtimeSinceStartup + 1.0f;
            }
        }
        else
            skippedFrames += delta - 1;

        if (Time.realtimeSinceStartup >= nextSummary)
        {
            print(String.Format("[{0:HH:mm:ss.fff}] Game frames last 5 s: {1} new, {2} stale (repeated), {3} skipped",
                DateTime.Now, freshFrames, staleFrames, skippedFrames));
            freshFrames = staleFrames = skippedFrames = 0;
            nextSummary = Time.realtimeSinceStartup + 5.0f;
        }
    }

    private void OnDestroy()
    {
        Free(ref leftEye);
        Free(ref rightEye);
        Free(ref snapshot);
    }

    RenderTexture snapshot;
    Material snapshotMaterial;

    // One draw that copies the whole source, so both eyes come from one game frame.  A blit
    // rather than CopyTexture: the shared texture is wrapped as RGBA32 whatever the game
    // really uses (often R10G10B10A2), and a raw copy between those formats is not allowed.
    // 10 bit keeps the precision of HDR-ish 10 bit games.
    Texture Snapshot(Texture source)
    {
        if (snapshot == null || snapshot.width != source.width || snapshot.height != source.height)
        {
            Free(ref snapshot);
            snapshot = new RenderTexture(source.width, source.height, 0, RenderTextureFormat.ARGB2101010, RenderTextureReadWrite.Linear)
            {
                name = "Screen Snapshot",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            snapshot.Create();
        }

        if (snapshotMaterial == null)
            snapshotMaterial = new Material(Resources.Load<Shader>("KatangaSnapshot")) { hideFlags = HideFlags.HideAndDontSave };
        snapshotMaterial.SetFloat("_LinearToSRGB", sourceIsSRGBView ? 1.0f : 0.0f);
        Graphics.Blit(source, snapshot, snapshotMaterial);
        return snapshot;
    }

    static void Ensure(ref RenderTexture rt, int width, int height, string name)
    {
        if (rt != null && rt.width == width && rt.height == height)
            return;

        Free(ref rt);
        // 10 bit, like the snapshot and most games: the only drop to 8 bit is then the eye
        // buffer itself, where sbsShader dithers.
        rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB2101010, RenderTextureReadWrite.Linear)
        {
            name = name,
            useMipMap = true,
            autoGenerateMips = true,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 16,
            mipMapBias = mipBias,
            wrapMode = TextureWrapMode.Clamp,
        };
        rt.Create();
    }

    static void Free(ref RenderTexture rt)
    {
        if (rt == null)
            return;
        rt.Release();
        Destroy(rt);
        rt = null;
    }
}
