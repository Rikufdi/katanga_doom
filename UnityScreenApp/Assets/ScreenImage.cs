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

    // Supersampling of the whole VR view, 1.0 = the runtime's recommended size.
    public static float RenderScale
    {
        get { return Mathf.Clamp(PlayerPrefs.GetFloat("render-scale", 1.0f), 0.5f, 2.0f); }
        set { PlayerPrefs.SetFloat("render-scale", value); }
    }

    // Slightly sharper than plain trilinear, which reads soft for text in VR.
    const float mipBias = -0.5f;

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

        material.SetTexture("_LeftTex", leftEye);
        material.SetTexture("_RightTex", rightEye);
        material.EnableKeyword("EYE_TEXTURES");
    }

    private void OnDestroy()
    {
        Free(ref leftEye);
        Free(ref rightEye);
    }

    static void Ensure(ref RenderTexture rt, int width, int height, string name)
    {
        if (rt != null && rt.width == width && rt.height == height)
            return;

        Free(ref rt);
        rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
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
