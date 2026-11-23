# CI with a software rasterizer (no GPU)

Loom's strongest regression net — the golden-image and cross-backend parity captures (`tools/golden.ps1`,
`GalleryGoldenTests`, `GalleryParityTests`) — needs a real GPU to produce its captures, so it runs only on a
GPU-equipped machine; GitHub-hosted runners have none, and CI skips it (see `.github/workflows/ci.yml`).
`EngineSettings.PreferSoftwareAdapter` plus `windows-latest`'s bundled WARP (Microsoft's software D3D12
rasterizer) close part of that gap: a GPU-less runner can still render every tracked demo and prove it produced
real pixels, not a crash or a black frame.

The workflow: [`.github/workflows/software-capture.yml`](../.github/workflows/software-capture.yml). It runs the
in-process `GallerySoftwareAdapterTests` (`Loom.Gallery.Tests`) in Release: that test forces the DirectX12 backend onto WARP
(`EngineSettings.PreferSoftwareAdapter`) and renders each tracked demo offscreen — no window, no capture flags —
failing if a demo crashes or comes back with a near-empty colour count. This is a **colorcount smoke test**, not a
pixel-parity gate — see "Parity baseline: deferred" below for why.

> The workflow originally drove `Loom.Sandbox` with `--headless`/`--capture` flags. Those never graduated onto the
> reusable desktop host (`Loom.Hosting.Desktop`) — capture is a test concern, not a production-host one (see the
> recipe below) — so the sandbox silently ignored them, opened a window, and crashed on the windowless runner. The
> smoke now lives in a test, which is where headless verification was always meant to go.

## Forcing a software adapter

`EngineSettings.PreferSoftwareAdapter` (`Loom.Core/Hosting/EngineSettings.cs`) asks a backend for a CPU/software
rendering adapter instead of a real GPU:

- **DirectX12** resolves WARP via `IDXGIFactory4.EnumWarpAdapter` and logs `[loom] adapter=WARP (software)`.
- **Vulkan** prefers a physical device whose `PhysicalDeviceType` is `Cpu` (a software rasterizer such as
  lavapipe or SwiftShader). Not every box has one — the selection degrades cleanly to the normal best-device
  choice and logs that no software device was found, rather than throwing.

Both the desktop host (`Loom.Hosting.Desktop`'s `HostOptions`) and `Loom.Sandbox` expose this as a
`--software-adapter` command-line flag.

## Recipe for a consuming game

Keep two concerns separate: forcing a software adapter (a real launch option) and verifying rendering headlessly
(a test concern).

**Force a software adapter.** The reusable desktop host exposes `--software-adapter`
(→ `EngineSettings.PreferSoftwareAdapter`), so any `LoomApplication` can be launched onto WARP / a CPU device on a
GPU-less runner. That is real runtime config a game can pass.

**Verify rendering headlessly — with an in-process test, not a host flag.** The production host does not have a
`--capture` or a fixed-frame-count mode, and deliberately so: a shipped game has no deterministic frame to grab
(it opens on an animated splash or a menu waiting for input), so a frozen-frame capture is meaningless for it.
Verify rendering with a GPU-optional test that brings the engine up **offscreen** — no window, no special host
flags — and asserts a real frame came out:

```csharp
// Skips cleanly when Backends.Available is empty; forcing PreferSoftwareAdapter runs it on WARP with no GPU.
var engine = backend.Create(Surface.Offscreen(width, height), width, height, settings);
// … set a graph, render a few frames pumping ServicePendingUploads, then engine.RequestCapture(path) …
// assert the PNG decodes to width x height and isn't a near-empty/black frame.
```

See the engine's [`Loom.Tests/OffscreenEngineTests.cs`](https://github.com/AtomicBlom/Loom/blob/master/Loom.Tests/OffscreenEngineTests.cs) for the end-to-end shape — this
is how Loom itself verifies rendering on a GPU-less runner, and it's the pattern a game (or a template) should
copy. The frozen-frame `--capture` used by `Loom.Sandbox` and `tools/golden.ps1` is a framework/demo regression
tool for deterministic scenes, not a shipped-game feature.

## Offscreen: windowless / Linux (lavapipe) runners

`--software-adapter` still creates a window-bound swapchain by default (`Loom.Sandbox` always opens a window,
even for `--capture`). For a truly windowless runner (no window server at all — common on Linux CI with
lavapipe as the Vulkan software ICD), pair it with `Surface.Offscreen` instead of a window surface: the engine
renders into an owned readable target with no `HWND` / `VkSurfaceKHR` involved, and `IEngine.RequestCapture`
extracts pixels from it exactly as it does for a windowed frame —
`backend.Create(Surface.Offscreen(width, height), width, height, settings)` in place of a real window surface.
See the engine's `Loom.Tests/OffscreenEngineTests.cs` for the shape this takes end to end (it exercises every available
backend against a real hardware or software device, whichever `Backends.Available` finds).

## Gotchas found bringing this up

- **GPU-based validation crashes WARP.** `DirectX12EngineSettings.GpuBasedValidation` defaults to `true` in Debug
  builds. GPU-based validation patches shaders to add on-GPU checks, and that patching is not reliably supported
  when the device is WARP — it can crash the process outright rather than failing cleanly. `GallerySoftwareAdapterTests`
  forces `GpuBasedValidation = false` so it is safe in Debug too, and the workflow additionally builds in Release
  (where it already defaults off) as belt-and-suspenders — Release also leaves the D3D12 debug layer's ordinary
  (non-GPU-based) validation on.
- **A "software" Vulkan device can come from more than one source.** On a box that also has Microsoft's D3D12
  mapping layer registered (`dzn`, the "Dozen" Vulkan-on-D3D12 driver — see the `VK_LAYER_MSFT_driver_sorting`
  warning already documented in the root `CLAUDE.md`), `vkEnumeratePhysicalDevices` can report a
  `PhysicalDeviceType.Cpu` device that is itself a translation layer over WARP, distinct from a "real" software
  ICD like lavapipe or SwiftShader. Loom's selection logic picks whichever suitable device reports `Cpu`
  first — that is correct per the Vulkan device-type contract, but it means "a Cpu device was found" does not
  always mean "a dedicated software rasterizer like lavapipe is present."

## Fidelity measurement (informational; not a committed baseline)

Measured locally (win-arm64, Adreno X1-85, Release build) by diffing a hardware DirectX12 capture against a
`--software-adapter` (WARP) capture of the same demo with
[`Scripts/Verification/diff.ps1`](https://github.com/AtomicBlom/Loom/blob/master/Scripts/Verification/diff.ps1):

| Demo    | Mean diff | Max diff | Pixels over 16/255 |
|---------|-----------|----------|---------------------|
| `cube`  | 0.00/255  | 5        | 0.0%                |
| `Bloom` | 0.00/255  | 5        | 0.0%                |

Both comfortably clear the cross-backend parity bar documented in the root `CLAUDE.md` (4/255 mean per channel,
<0.5% of pixels). That is a promising signal for WARP as a parity baseline — but it is one box, one GPU family,
one build. It has not been measured on the `windows-latest` runners CI actually uses, across the full
tracked-demo set, or over time (a driver/OS update could change it). **No software-adapter parity baseline is
committed:** `.github/workflows/software-capture.yml` is a colorcount smoke test only. Promoting WARP captures to
a committed golden/parity baseline needs the fidelity first measured on the runner that would actually enforce
it.
