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

Rendering is Built-in Render Pipeline, D3D11 only (the game's shared surface and
`UnityNativePlugin` are D3D11), Single Pass Instanced stereo.

### FSR upscaling of the game image

Optional AMD FidelityFX Super Resolution 1.0 (EASU + RCAS) on the game image, so a
game can run at lower resolution and be upscaled for the big screen.  Cycle
Off / PRISM sharpen / FSR with Insert, the gamepad Y button, or left A/X on the
controllers.  Command line: `--upscale 1.5` (also turns FSR on) and
`--upscale-sharpness 0.2`.  See [Docs/Upscaling.md](Docs/Upscaling.md).

### Opening the project the first time

Unity will upgrade the 2017 era assets and generate `Packages/packages-lock.json`,
`Assets/XR/...` settings and some `.meta` files.  Commit those after the first open.
