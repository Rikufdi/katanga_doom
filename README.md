## katanga

Has some experimental code. Last version to ship was [f5c2d3](https://github.com/bo3b/katanga/commit/f5c2d346ee1fd152f0203df1a6e90eacb1d07ef4).

Tools used:
1. VS2022, toolset v143, latest Windows 10/11 SDK.
1. Unity 6.3 LTS (6000.3.19f1 or any later 6000.3 patch).

### VR runtime: OpenXR

Katanga now uses Unity's OpenXR plugin instead of the SteamVR Unity plugin (OpenVR).
It works with any OpenXR runtime:

* SteamVR (set SteamVR as the OpenXR runtime in SteamVR Settings > OpenXR)
* VDXR / Virtual Desktop
* Meta Quest Link / Oculus
* Windows Mixed Reality

The active runtime is whichever one the system has registered as the OpenXR runtime.
Katanga logs the runtime name at startup (`OpenXR runtime: ...` in the log).

Controller input goes through the Unity Input System with generic XR bindings, see
`UnityScreenApp/Assets/KatangaInput.cs`.  Interaction profiles for Index, Vive, Touch
(including Quest Touch Plus/Pro), WMR, HP Reverb G2 and KHR Simple are enabled by
`Assets/Editor/KatangaXRSetup.cs`, which configures XR Plug-in Management and OpenXR
when the project is opened and before every build (menu: Build > Configure XR).

Controller models come from the [WebXR Input Profiles](https://github.com/immersive-web/webxr-input-profiles)
assets (MIT, `Assets/Resources/ControllerModels`), imported with glTFast.  The model is
chosen from the OpenXR interaction profile the runtime reports for each hand (Index,
Vive, Touch, Quest Touch Plus/Pro, WMR, Reverb G2, generic fallback), see
`Assets/ControllerModel.cs`.  SteamVR and VDXR report every Quest controller as the old
Touch profile, so for those the headset name decides (Quest 3/3S, Pro, 2, Rift S/Quest 1,
Rift CV1).  VDXR and the Meta runtime report the headset name; SteamVR does not, so under
SteamVR pass e.g. `--controller-model meta-quest-touch-plus-v2` (any folder name in
`ControllerModels`, or `auto`).

Rendering is Built-in Render Pipeline, D3D11 only (the game's shared surface and
`UnityNativePlugin` are D3D11), Single Pass Instanced stereo, 4x MSAA.  The game image is
split into one mipmapped, 16x anisotropic texture per eye before it is drawn
(`ScreenImage.cs`), so it doesn't shimmer when the screen is smaller than the game's
resolution.  `--render-scale 1.3` supersamples the whole VR view on top of the runtime's
own setting.

### FSR upscaling of the game image

Optional AMD FidelityFX Super Resolution 1.0 (EASU + RCAS) on the game image, so a
game can run at lower resolution and be upscaled for the big screen.  Insert, the
gamepad Y button, or left A/X on the controllers cycle Off / Sharpen (RCAS on the game
image, the default) / PRISM sharpen (whole VR view, costlier) / FSR upscale.  Command line: `--upscale 1.5` (also turns FSR on) and
`--upscale-sharpness 0.2`.  See [Docs/Upscaling.md](Docs/Upscaling.md).

### Opening the project the first time

Unity will upgrade the 2017 era assets and generate `Packages/packages-lock.json`,
`Assets/XR/...` settings and some `.meta` files.  Commit those after the first open.
