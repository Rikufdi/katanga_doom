# AGENTS.md

Notes for an AI agent (or a human) working on this fork of Katanga. `README.md` describes the
app; this file is the operational detail: how to build it, how to test it, and what has been
learned about the tools around it.

Machine-specific details (tool paths, headset, the Q3Diag diagnostics setup) are kept out of git in
`local/MACHINE.md`. Read it if it exists.

## What this is

Katanga shows a 3D Vision-fixed DX9/DX11 game (3Dmigoto / HelixMod fix) on a big virtual screen
in VR. Two halves:

- **C++ plugins** (`Katanga.sln`)
  - `DeviarePlugin` → `GamePlugin.dll` / `GamePlugin64.dll`, injected into the game by Deviare.
    It shares the game's stereo backbuffer.
  - `UnityNativePlugin` → `UnityNativePlugin64.dll`, which opens that shared surface on the
    Unity side. A named mutex (`KatangaSetupMutex`) is held from `LaunchAndPlay.Update` until
    `WaitForEndOfFrame` every frame.
- **Unity app** (`UnityScreenApp`, Unity **6000.3.19f1**, OpenXR, D3D11, single pass instanced,
  Mono backend). The branch `claude/blissful-gauss-5mafop` holds the Unity 6 / OpenXR / FSR port.

## Building

### C++ plugins

Needs VS Build Tools with the **v143** toolset and **"C++ ATL for v143 build tools (x86 & x64)"**
(`DeviarePlugin.cpp` includes `atlbase.h`). The build copies the DLLs into
`UnityScreenApp/Assets/Plugins/`, which is gitignored.

```
msbuild /m /p:Configuration=VR Katanga.sln /p:platform=x64   /v:minimal /target:rebuild /p:PlatformToolset=v143
msbuild /m /p:Configuration=VR Katanga.sln /p:platform=Win32 /v:minimal /target:rebuild /p:PlatformToolset=v143
```

Both platforms are needed: the injected plugin must match the game's bitness.

### Unity app

Build the plugins first. Close any editor that has the project open (Unity locks the project),
then:

```
Unity.exe -batchmode -quit -projectPath UnityScreenApp -buildTarget Win64 ^
          -executeMethod ReleaseBuild.Build -logFile unity_build.log
```

`ReleaseBuild.Build` (the F5 "Build/Release Build" menu item) deletes and rewrites `../Release/`,
so it fails while `Release/katanga.exe` is running. Check the log for
`Build Finished, Result: Success.` and `warning CS` / `error CS`; the build currently has no C#
warnings. The first build after a clean clone takes several minutes to import packages.

Unity rewrites some `ProjectSettings/*.asset` files when it opens the project. Don't commit those
along with unrelated changes.

### Output layout (important for 3DFixManager)

Unity 6 puts native plugins in `katanga_Data/Plugins/x86_64/`. `ReleaseBuild.CopyDeviare` and the
CI workflow also put a complete Deviare set (`DeviareCOM(64).dll`, `DvAgent(64).dll`,
`deviare32/64.db`) flat in `katanga_Data/Plugins/`. That copy is required, see below.

## 3DFixManager integration

3DFixManager (3DFM) is the usual launcher. It expects Katanga at
`<3DFM>\Tools\Katanga\katanga.exe`. Findings from reading its code (v1.8.7.0):

- **The Play VR button only appears if both
  `Tools\Katanga\katanga_Data\Plugins\DeviareCOM.dll` and `DeviareCOM64.dll` exist**
  (`Katanga.IsKatangaAvailable()`). It checks once, at window load, relative to 3DFM's working
  directory, so restart 3DFM after changing files. If the check fails it also writes
  `VrModeEnabled = False` to its settings.
- On startup it registers both DLLs from that path with `regsvr32 /s` (elevated). Katanga creates
  `NktSpyMgr` through COM, so the registered path must contain a working Deviare set.
- It launches Katanga with `--game-path`, `--launch-type`, `--steam-path`, `--steam-appid`,
  `--epic-appid` and `--waitfor-exe`. `Game.cs` parses all of them.
- Before Play VR it runs a SteamVR check (`SteamVR.Init` in `VrHelper.GetVrState`). This is left
  over from the OpenVR version of Katanga; the Unity 6 build itself uses OpenXR and does not need
  SteamVR.
- Its `VrFramerateLimit` setting caps the game. **With Katanga's frame sync (below), set it to
  unlimited**: a second limiter fights the pacing.
- **It forces NVIDIA's global vsync On at every game launch** (`SetVsyncMode(VsyncMode.On)`), and
  only turns it Off again in VR mode when SteamVR initialised and the desktop refresh is lower than
  the headset's. With vsync forced on, Katanga's desktop mirror window waits on the monitor, which
  held the whole VR loop at 60 fps on a 60 Hz TV. Fix: an NVIDIA program profile for
  `katanga.exe` with **Vertical sync: Off**, which overrides the global setting. Katanga makes
  sure of that itself at startup (`UnityNativePlugin/DriverProfile.cpp`, NvAPI DRS): it reuses the
  profile that holds `katanga.exe` or creates "Katanga VR", and sets VSYNCMODE force off and
  adaptive vsync off. The driver reads profiles when a process starts, so a profile created now
  takes effect from the next launch. `Player.log` says `NVIDIA profile: ...`. Writing needs admin
  rights, which Katanga has under 3DFM. `--no-vsync-profile` skips it.
- That check initialises OpenVR as an overlay application (`SteamVR.Init` in
  `OpenVRApiModule`), which starts SteamVR if it isn't running. Katanga then opens its OpenXR
  session on the active runtime (Virtual Desktop's VDXR here), Virtual Desktop switches away from
  SteamVR, and SteamVR hangs, restarts its processes or quits. Starting SteamVR first helps only
  partly. **`Extras/3DFixManagerOpenVRStub/`** is an optional stand-in `openvr_api.dll` for
  3DFixManager that answers its check without starting SteamVR (install steps and limits in its
  README). Katanga itself can't be pointed at SteamVR's OpenXR runtime instead, because it runs
  elevated and the OpenXR loader ignores `XR_RUNTIME_JSON` in elevated processes.
- Most DX11 games launch as `DX11Exe`: the fix's 3Dmigoto `d3d11.dll` shares frames with Katanga
  itself ("DirectConnection"), and GamePlugin is only injected for pacing (see Frame pacing).

## Frame pacing (frame sync)

The game and the headset each run their own clock. Even at the same nominal rate they drift,
and while the game's frames land near the moment Katanga takes the image, the headset shows
game frames twice and skips others: judder when the camera pans. No frame limiter can fix that,
because it doesn't know when the headset refreshes. Katanga instead makes the game wait for it:

- Katanga creates the auto-reset event `KatangaFrameEvent` and signals it once per VR frame,
  right after `ScreenImage` has taken its snapshot of the game image
  (`LaunchAndPlay.GameFrameTaken`, with a fallback at `EndOfFrame`). The game plugin waits for
  it (at most 40 ms, then runs free until the signal returns) at the top of `Present`, so the game
  presents exactly once per headset frame. `--no-frame-sync` turns it off.
- Releasing at the end of Katanga's frame instead was also tried. It looked worse in the
  headset: on the shared GPU the game's ~6.5 ms of work stretches to ~10.5 ms, and it needs the
  extra time.
- `ScreenImage` takes **one** snapshot of the side-by-side image before cutting the eyes. Two reads
  of the shared texture let the game's write land between them, and the eyes then show different
  frames ("two frames mixed"), which pacing made happen every frame.
- **The game's frame must be finished on the GPU before the hook waits.** The copy into the shared
  texture (3Dmigoto's, or GamePlugin's own) sits in the game's D3D11 command buffer until `Present`
  flushes it, and the hook waits at the top of `Present`. Without anything else, the release let
  that copy and Katanga's snapshot start on the GPU together, and whichever ran first decided
  whether the headset got the new frame or the previous one. The stutter followed scene load
  (Metro Exodus: always at the same spot when turning, worse in foliage) while the frame count
  looked perfect. The hook now copies, flushes, waits on an `ID3D11Fence` until the GPU is done,
  counts the frame and only then waits for the VR frame. `katanga.log` says `game frames finish
  on the GPU before the VR frame wait (fence)`. In PresentMon the game's `MsRenderPresentLatency`
  fell from ~1.6 ms to ~0.2 ms.
- For `DX11Exe` (3Dmigoto) games Katanga injects GamePlugin in **pacing only mode**: it loads it
  with `LoadCustomDll` (not unloaded on exit, so the hook never points at freed code) and calls the
  exported `StartPacing` through `CallCustomApi`, because `OnLoad` only runs for DLLs attached to a
  hook. The plugin must hook the **real** `dxgi.dll` `IDXGISwapChain::Present`: a swap chain
  created inside the game comes back wrapped by 3Dmigoto, whose `Present` the game doesn't use.
  Katanga finds `Present` in its own clean process and publishes its offset in `dxgi.dll` through
  the `Local\KatangaPacing` mapping (`Shared/KatangaPacing.h`). System DLLs load at the same
  address in every process for the whole boot, and the plugin checks the `dxgi.dll` build matches.
  Katanga is 64 bit and can't look into the 32 bit `dxgi.dll`, so for 32 bit games it runs
  `%windir%\SysWOW64\rundll32.exe "<Plugins>\GamePlugin.dll",ProbePresent`: the 32 bit plugin
  finds `Present` in that clean 32 bit process and fills in the mapping.
- Proof in `katanga.log`: `pacing hook on IDXGISwapChain::Present installed`, `first paced
  Present`, and every 900 presents `N of the last 900 held for the VR frame`. `Player.log` says
  `Frame sync: pacing active` or why not. The hook also logs each distinct
  `Present(SyncInterval, Flags)` combination and each swap chain once.
- **`Present` with `DXGI_PRESENT_TEST` (Flags 0x1) is not paced.** It only asks whether the window
  is visible and shows nothing, and Windows doesn't log it as a frame (PresentMon doesn't see it).
  Metro Exodus calls it once per real frame; pacing it too ran Metro at half the headset rate
  (36 fps at 72 Hz). A game paced at an exact fraction of the headset rate while it needs far
  less time per frame means more than one paced `Present` per frame: check those log lines.
- Requirements: the `katanga.exe` vsync-off profile above (Katanga sets it), and the 3DFM limiter
  at unlimited.
- `local/pacetest/` (see `local/MACHINE.md`) tests the pacing plugin without a game or headset,
  64 and 32 bit, including the 32 bit `rundll32` probe (`pacetest32.exe <GamePlugin.dll> probe`).

## Colour pipeline

The goal is that the headset shows the game's pixel values unchanged, with black at exactly 0 and
every shadow step intact. PCVR is known to get this wrong (double sRGB encoding, video range,
crushed or lifted blacks), so each layer has been measured.

**Katanga's side:**

- The project renders in **Gamma** colour space: shaders pass stored values through, no
  conversions. Unity picks an `R8G8B8A8_UNORM_SRGB` swap chain (`Player.log`, OpenXR report,
  `[c:...]`) and renders into it through a non-sRGB view (`R8G8B8A8_UNorm`, 3072×3264 × 2 slices,
  single pass instanced): the stored values are sRGB values, in a buffer marked sRGB.
- **Game → shared texture:** 3Dmigoto or GamePlugin copies the back buffer. Most games are
  `R8G8B8A8_UNORM` (Metro Exodus, DXGI 28) or 10 bit `R10G10B10A2` (Little Nightmares II,
  DXGI 24), read as stored.
- **`_SRGB` back buffers (DXGI 29/91)** sample as linear through the shared texture, which in a
  Gamma pipeline looks too dark with crushed shadows. The snapshot shader `KatangaSnapshot`
  (in `Resources`) encodes them back to sRGB. `Player.log` says `Game DXGI format N` and whether
  it converts. Not yet tested with such a game (The Surge is one).
- **Precision:** the snapshot and the per-eye copies are 10 bit (`ARGB2101010`). The only drop to
  8 bit is the eye buffer, where `KatangaColor.cginc` dithers (±1 step triangular noise, new every frame,
  faded out at exact 0 and 1 so black stays 0). `--no-dither` turns it off.

**Measured** (`--color-diagnostics`: clears the eye buffer to known levels and reads the raw bits
back):

| Written | 0.00 | 0.02 | 0.05 | 0.20 | 0.50 | 1.00 |
|---|---|---|---|---|---|---|
| Eye buffer (8 bit) | 0 | 5 | 13 | 51 | 127 | 255 |

Exact: no double sRGB encoding (0.02 would be 39, 0.5 would be 188).

**After Virtual Desktop** (`--color-levels` holds the whole view at 8 bit levels;
`local/tools/color/capture_levels.sh` grabs the Quest's final panel buffer with `adb exec-out
screencap`, 4128×2208, both eyes). Quest settings: VD colour saturation off, Quest brightness
max, accessibility contrast 0. Flat fields, averaged:

| Input | 0 | 1 | 2 | 4 | 8 | 16 | 32 | 64 | 128 | 192 | 255 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| G | 0 | 2 | 4 | 8 | 14 | 24 | 43 | 76 | 140 | 201 | 254 |
| B | 0 | 3 | 5–6 | 11 | 18–19 | 29–30 | 51 | 88–90 | 161–165 | 232–237 | 255 |
| R | 0 | 1–2 | 3 | 7 | 12–13 | 21–22 | 39–40 | 70–72 | 130–133 | 188–193 | 236–242 |

- **Black stays 0 and every step from 1 up stays distinct** through the video encode and decode:
  no crushing, no lifted blacks, no limited range.
- On top there is a **tone and colour curve**: shadows and mid-tones raised (about
  output = input^0.86), blue boosted 15–25%, red lowered at white. That lowers contrast, which fits
  "less vivid than the OLED TV, closer with added contrast".
- **It is Virtual Desktop's OpenXR path, not the Quest.** A reference image
  (`local/tools/color/make_reference.py`: flat patches 0–255) was captured on four paths:

  | Path | Tone curve | Blue/green at mid-grey |
  |---|---|---|
  | Quest Browser and Files viewer (native Quest apps) | exact (exponent 1.00–1.01) | 1.09 |
  | Virtual Desktop desktop view (PC monitor) | exact | 1.17 |
  | Katanga through VDXR (`--show-desktop` on the same PC image, or `--color-levels`) | lifted (16→23, 64→74, 128→137) | 1.16 |
  | Same, Katanga built in Linear colour space | lifted, the same | — |

  The Quest's own calibration makes greys ~10% bluer, and every native app gets that. The lift
  only happens on VD's OpenXR path, whatever Katanga does (colour space, clear colour or textured
  screen). VD's streaming gamma slider (0.6–1.4) has no visible effect on OpenXR apps.
  On that path **blue reaches 255 from input ~216**, so from there up only red and green rise, and
  white loses the bluish tint of the greys: that is the "yellow white".
- **Correction** (`ColorCorrection.cs`, applied in `KatangaColor.cginc` by `sbsShader` and
  `shader2D`, before the dither): a 256 entry per-channel lookup that makes the VDXR path deliver
  what a native Quest app shows. It holds the measured tables. Above input 192 it keeps the greys'
  channel ratios and rolls red and green down instead, so white keeps the same tint, ~9% dimmer
  (G 232 instead of 253). On automatically for runtime `VirtualDesktopXR` (measured with 1.0.10);
  `Player.log` says `Color correction: on ...`. `--no-color-correction` turns it off,
  `--no-white-fix` keeps the plain native target at the top. Verified: 8→7.4, 32→30.5, 64→62.7,
  128→127.8, 192→190.5 (native 7.5, 31.1, 63.1, 126.7, 189.8), blue/green 1.09–1.10 from 64 to
  white. A VD update, or another runtime, needs a new measurement.
- **Measuring through a Quest app:** push the image (`adb push ... /sdcard/Download/`), open it in
  Files, or serve it on `127.0.0.1` and `adb reverse tcp:8765 tcp:8765` for the Browser; then
  `local/tools/color/capture_reference.sh`. Turn the Quest's accessibility contrast off first: it
  crushes 1–16 to black. For the VDXR path, show the same page full screen on the PC and run
  `katanga.exe --show-desktop` (with `--no-color-correction` for the raw curve).
- **Backlight:** the Quest's LCD backlight adapts to the content. A capture right after a level
  change and one a second later are identical to the decimal, yet the eye sees a short brightness
  bump. It is outside the image data and `screencap` can't see it.

**Capture traps:** the headset display is off (captures black) unless someone wears it; any
open Quest or VD menu ends up in the capture; the panel lags the PC by up to a second, so capture
at least 1–2 s after each level change.

## Testing and measuring

- **Slideshow mode:** run `katanga.exe` with no arguments. Hold Ctrl while starting it to get a
  file picker for a game exe instead.
- **Logs** (in `%USERPROFILE%\AppData\LocalLow\Katanga\Katanga\`):
  - `Player.log` holds Unity output and the OpenXR diagnostic report (runtime, per-eye
    resolution). It has timestamped `Hitch: N ms frame` lines for frames over 25 ms,
    `Slow GC.Collect` lines, and timestamped environment, sharpening and hint changes.
    With frame sync it counts game frames per headset frame (GamePlugin increments `presentCount`
    in the pacing mapping once a frame is finished, Katanga reads it before each snapshot):
    `Game frames last 5 s: N new, N stale (repeated), N skipped` every 5 s, plus a timestamped
    `Stale frame` line at most once a second. This is the direct measure of judder; 0 stale at
    `Hz × 5` new is perfect.
  - `katanga.log` is native plugin logging. `WAIT_TIMEOUT` there means the per-frame mutex stalled.
    One `ReleaseMutex ERROR_NOT_OWNER` per frame is expected (a deliberate double release).
- **Frame timing:** PresentMon on `katanga.exe` gives Katanga's own frame times, because Unity
  presents its mirror window once per frame. Run a second PresentMon on the game exe to get the
  game's own frame rate. PresentMon traps, all hit in practice:
  - 2.5.1 takes **one** `--process_name`; a second one silently matches nothing. Run one session
    per process with `--date_time`, which puts both files on the same clock.
  - Without admin rights it only resolves names of processes that already run when it starts, so
    start it after the game and Katanga are up.
  - A PresentMon that is killed can leave its ETW session behind (`logman query -ets`), which then
    starves new captures (tens of thousands of lost events, empty output). Stop sessions with
    `PresentMon --terminate_existing_session --session_name <name>`, not by killing the process.
- **Judging pacing from present times is only valid when the game presents before Katanga's
  image snapshot.** With the game released right after the snapshot, it presents in the middle of
  Katanga's frame, and comparing present times shows fake repeat/skip pairs. Use the `Game frames`
  lines in `Player.log` instead, and trust the headset.
- **Unreal Engine games drop to 3 fps whenever their window is not in the foreground**, for
  example when alt-tabbing out during a test. Ignore those stretches.
- **Patterns to recognise:**
  - Runs of frames at *exactly* 100 or 200 ms with an idle GPU mean the OpenXR runtime (Virtual
    Desktop) is throttling Katanga. It is not Katanga's own workload, and it also pauses pacing.
  - Katanga at exactly 60 fps on a 90 Hz headset: forced vsync on a 60 Hz desktop display (see
    3DFixManager above).
  - The game at 144 fps and GPU near 100% with "frame sync on": pacing is not reaching the game's
    `Present`. Check `katanga.log` for `first paced Present`.
  - Long stutters right after `Controller left/right connected` lines: Quest controllers sleep when
    put down and reconnect often. `ControllerModel` used to rebuild the glTF model on every
    reconnect; it now keeps it. Compare those timestamped lines with the `Hitch` lines.
  - Short stutters when the game spawns NPCs, streams in areas or saves: the game's own CPU work
    (PresentMon shows high `MsCPUBusy` for the game, Katanga's frames stay clean). With frame sync
    a late game frame shows as one repeated frame. Not something Katanga can fix.

## CPU headroom experiments (Little Nightmares II, 5 min runs, same section)

Virtual Desktop's OpenXR runtime spins one thread inside Katanga at a full core, for exact frame
delivery; Katanga itself uses very little CPU. These options exist, all off by default, and can go
in `katanga_options.txt` next to `katanga.exe` (3DFixManager passes fixed arguments):

- `--cpu-isolation N`: Katanga, spinner included, on the last N physical cores, the game on the rest.
- `--isolate-spinner`: only the spinning thread gets a logical CPU of its own (found by measuring on
  a background thread; sampling on the main thread stops the runtime spinning).
- `--game-priority`: the game above normal priority.

| Run | Game frames >15 ms / >25 ms per min | Headset repeats/skips |
|---|---|---|
| A: frame sync, no options | 12.6 / 1.6 | ≈ 0 |
| B: + `--cpu-isolation 1 --game-priority` | 11.8 / 2.5 | ≈ 0 |
| D: + `--isolate-spinner` | 17.5 / 2.1 | ≈ 0 |
| E: `--no-frame-sync`, 3DFM limiter at headset Hz | 15.8 / 4.5 | ~8% of frames |

| F: frame sync at **72 Hz** (slot 13.9 ms) | missed slot 7.1/min (outside two throttles) | ≈ 0 |
| G: frame sync at **72 Hz**, 8 min | missed slot 4.2/min, >25 ms 1.9 | ≈ 0 |

None of the CPU options gave the game measurable headroom; the differences are within session to
session variation. **A lower headset refresh rate does help**: at 72 Hz the game misses its slot
roughly a third as often as at 90 Hz, because each frame gets 13.9 ms instead of 11.1 ms.

The occasional **major slowdown** (seconds of exactly 100 ms Katanga frames, pacing drops out) is
Virtual Desktop's runtime holding Katanga back. The headset's logcat shows the effect, not the
cause: the decoder's input rate falls (72 → ~50/s), stale frames appear and VD's predicted latency
jumps (59 → 82 ms). It comes and goes between sessions (two in 4 minutes, none in 8). One was caught with Q3Diag
running (`local/tools/pacing/q3diag_session.py`): it lined up with a **Wi-Fi error burst on the
headset's 6 GHz link** (542 retries and 58 lost packets in one ~2 s sample, against 0-20 retries and
0 lost normally; RSSI -35 → -39 dBm), while the PC was calm (NVENC 17-21%, GPU, DPC, game and
streamer normal). No controller connect or disconnect coincided. The burst's trigger is outside
Katanga: channel interference, the Quest radio (it serves the Touch controllers' 2.4 GHz Wi-Fi
Direct link on the same chip, "RSDB"), or the signal being blocked. Katanga recovers by itself:
pacing drops out and resumes. Turning pacing off did not reduce the game's slow frames either (the driver's
frame queue is back then), so the remaining stutters are the game's own work and a frame queue in
Katanga would only add latency. A lower headset refresh rate gives each game frame more time.
Keep the options for games that load every core, and remeasure there.

## Known issues

- Toggling sharpening is followed about 1.3 s later by a ~50 ms + ~95 ms hitch. The cause is not
  confirmed; the toggle code itself is trivial.
- The first seconds after a game starts have 80–160 ms frames while the shared surface is created
  and recreated.
- On a Quest 3 the controllers are detected correctly (`meta-quest-touch-plus-v2`), but the model
  looks like a special edition (possibly the Xbox edition of the Quest 3S controllers), not the
  standard Touch Plus. Cosmetic only; low priority.
- With frame sync, a game frame that doesn't finish within the headset frame (heavy scenes, the
  game's own save or loading stalls) is shown one frame late: a small drop, no judder. Lower game
  settings or a lower headset refresh rate give it more room.
- Desktop mode (`--show-desktop`, also shown after a game exits) is much less sharp than Virtual
  Desktop's own desktop view. `shader2D` samples the full size desktop with a 4 tap average, without
  the mips and anisotropic filtering `ScreenImage` gives games, and everything Katanga shows is
  also resampled into the eye buffer, video compressed and reprojected, where VD draws its desktop
  on the Quest directly.
- VRAM: Katanga falls to about 4–6 fps when VRAM is nearly full, for example with a local AI
  model server loaded. Check `nvidia-smi` and per-process GPU memory before profiling.

## `local/` (gitignored, never commit)

Local-only material that must not be published:

- `local/MACHINE.md`: this PC's tool paths, headset and Q3Diag setup.
- `local/tools/ilspycmd/`: ILSpy command-line decompiler 11.1.0 (NuGet package, unpacked).
  Needs the .NET 10 runtime, no SDK:

  ```
  dotnet local/tools/ilspycmd/tools/net10.0/any/ilspycmd.dll -p -o <outdir> <assembly.exe>
  ```

- `local/3dfm_src/`: decompiled 3DFixManager 1.8.7.0, for reference only. It is third-party code,
  so read it but don't copy it into this repo. The useful files are `FixManager/Katanga.cs`,
  `FixManager.Models/VrHelper.cs` and `FixManager/MainWindow.xaml.cs` (`toggleVrMode`,
  `registerKatangaDlls`).
