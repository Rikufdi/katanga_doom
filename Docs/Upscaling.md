# Upscaling the game image (DLSS-style, Luke Ross style)

## What Luke Ross' R.E.A.L. VR mods do

Luke Ross' mods run *inside* the game.  They use Alternate Eye Rendering (AER): the
game renders the left eye on one frame and the right eye on the next, and the mod
reprojects to fill in the missing eye.  Because the game is rendering at a lower
internal resolution, the mod leans on the game's own DLSS/FSR/XeSS integration to
upscale.  Those are temporal upscalers, and they need the game's depth buffer,
motion vectors and camera jitter.  The later versions go further and reconstruct
missing data for the other eye ("DLSS fix", optical flow estimator in AER v2).

## Why Katanga can't do the same thing directly

Katanga is on the other side of the fence.  The game (with 3D Vision Automatic /
3Dmigoto) renders both eyes and presents a side-by-side image, which the injected
`GamePlugin` copies into a shared D3D11 texture.  Katanga only ever sees that final,
tonemapped, UI-composited frame:

* no depth buffer
* no motion vectors
* no jitter sequence

DLSS (and FSR 2/3, XeSS) cannot work without those, so there is no way to plug a
temporal upscaler in at the Katanga side.

## What we can do: spatial upscaling (implemented)

A spatial upscaler only needs the final image.  AMD FidelityFX Super Resolution 1.0 is
the best fit:

* vendor neutral, works on NVIDIA, AMD and Intel
* MIT licensed, source is in `UnityScreenApp/Assets/FSR`
* cheap: two full screen passes

`GameUpscaler.cs` + `Resources/KatangaFSR.shader`:

1. **EASU** upscales the side-by-side game texture to `--upscale` x its size.
2. **RCAS** sharpens the result (also used alone when the factor is 1.0).

Both passes clamp their taps to the eye they are working on, so nothing bleeds
across the center seam between left and right eye images.

Typical use: run the game at ~67% resolution (e.g. 2560x1440 instead of 3840x2160 per
eye) and use `--upscale 1.5`.  The GPU time saved in the game is far larger than the
cost of the two FSR passes.

Toggle at runtime: Insert / gamepad Y / left controller A/X cycles
Off → PRISM sharpen (whole VR view) → FSR (game image only).

## Possible future steps

* **NVIDIA Image Scaling (NIS)**: also spatial and MIT licensed.  Could be offered as
  an alternative to EASU; quality is similar.
* **DLSS inside the game process**: the injected `GamePlugin` does have access to the
  game's D3D11 device.  In principle it could hook the game's own DLSS/FSR2 calls
  (as Luke Ross' mods effectively do) or, for games that already ship DLSS, simply
  let the game run at a lower internal resolution and use its own upscaler before
  Katanga ever sees the frame.  That is per-game work and belongs in the game side
  plugin, not in the Unity app.
* **Alternate Eye Rendering**: would need per-game camera hooks to render one eye per
  frame, which 3Dmigoto/3D Vision Automatic does not provide.  Out of scope for Katanga.
