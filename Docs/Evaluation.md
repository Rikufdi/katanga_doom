# Where Katanga can still improve

An evaluation of the Unity 6 / OpenXR version, done after the port. Items marked **done**
were implemented along with this note; the rest are ranked by expected benefit.

## Done

- **Quest 3/3S and Quest Pro controller models.** SteamVR and VDXR only offer the
  `oculus/touch_controller` interaction profile, so every Quest controller looked like a
  Quest 2 controller. `KatangaSystemInfo` (a small OpenXR feature) reads the headset name
  that VDXR and the Meta runtime report, and `ControllerModel` picks Quest 3/3S (Touch
  Plus), Pro, Quest 2, Rift S/Quest 1 or Rift CV1 from it. SteamVR only reports
  `SteamVR/OpenXR : oculus`, so under SteamVR use `--controller-model <id>`.
- **Screen image filtering.** The game texture had no mipmaps, so it aliased (shimmered)
  whenever the screen covered fewer headset pixels than the game renders. `ScreenImage`
  now copies each eye into its own mipmapped texture with 16x anisotropic filtering.
  Separate per eye textures keep the coarse mips from mixing the eyes at the seam.
- **Cheaper default sharpening.** PRISM sharpen is a post effect over both eye buffers, and
  enabling it makes the camera render through an extra intermediate texture. A new default
  mode runs AMD RCAS on the game image instead: once per frame, at game resolution. PRISM
  is still in the cycle for people who prefer it. Toggling RCAS doesn't touch the camera,
  so it should not trigger the hitch seen when toggling PRISM (not yet measured).
- **No forced GC.** `LaunchAndPlay` called `GC.Collect()` every 30 frames. With the
  incremental GC that only adds full collections, the `Slow GC.Collect` log lines.
- **`--render-scale`** for supersampling the VR view, on top of what the runtime sets.

## Recommended next

### 1. Show the screen through an OpenXR composition layer (biggest quality gain)

Right now the game image is drawn into Unity's eye buffers, and the runtime then resamples
those again for lens distortion (and VD's video encoding). Two resamplings are why a
virtual screen always looks a little softer than the game itself.

With a quad or cylinder composition layer (`XR_KHR_composition_layer_cylinder`, via Unity's
`com.unity.xr.compositionlayers`), the runtime samples the game texture directly at display
resolution, once. Virtual Desktop, Bigscreen and SteamVR's own desktop view work this way.
Other benefits: text stays sharp, the screen is reprojected independently of Katanga's
frame rate, and the environment could run at a lower resolution.

Work involved: one layer per eye (left/right half of the SBS image), cylinder for the
curve setting, the environment rendered around an underlay hole, and checks that
SteamVR's and VDXR's OpenXR runtimes support cylinder layers (fall back to quad or the
current path otherwise).

### 2. Frame synchronization with the game (fixes rare torn or mismatched eyes)

The game copies its frame into the shared texture on every Present with no synchronization
against Katanga reading it (`InProc_DX11.cpp`, the texture is created without
`D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX`). Katanga can read while the copy is in progress,
which can show a frame where the eyes come from different game frames, uncomfortable in
stereo. Double buffering (two shared textures and a frame counter in the IPC block) avoids
it without adding waits on either side, and lets Katanga skip the per frame work
(`ScreenImage`, FSR) when the game hasn't produced a new frame.

### 3. Warm up shaders at startup

The FSR, RCAS and PRISM shaders are compiled the first time they are used, which can
hitch the frame when the user first cycles sharpening. Creating the materials and doing
one throwaway blit at startup (or a ShaderVariantCollection) moves that to the loading
screen.

### 4. Headset name under SteamVR

SteamVR's OpenXR runtime doesn't report the headset model. Katanga could query it through
OpenVR's `Prop_ModelNumber_String` when SteamVR is the runtime, so `--controller-model`
isn't needed. It needs `openvr_api.dll` and only matters for Quest-on-SteamVR users.

### 5. Colour space

The project is still in Gamma colour space, from 2017. Games with sRGB backbuffer formats
(28/29/87/91 in `LaunchAndPlay`) are handled by trial and error. Switching to Linear, with
the eye textures sRGB, would make brightness and filtering correct, but needs testing
against games in each format.

### 6. Launcher

3DFixManager still checks for SteamVR before it enables Play VR (`VrHelper.GetVrState`),
which the OpenXR build doesn't need (see AGENTS.md). That check lives in 3DFM, not here.
