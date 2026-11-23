# WebGPU pixel parity

How the browser backend is held to the same pixels as the desktop ones, why that check has to leave the test
process, and what currently diverges.

## Why this is a separate leg

`GalleryParityTests` renders each demo on Vulkan and on DirectX 12 in the same process and diffs the two frames.
WebGPU cannot join it. The backend only exists inside a browser, and driving it means the wasm SDK — which
`BinaryVibrance.Kaldra.Tests` deliberately does not reference, because the browser toolchain would then be restored for every
desktop test run. (`Kaldra.Web` is kept out of `BinaryVibrance.Kaldra.slnx` for the same reason.)

So the WebGPU leg is out-of-process, and it is asymmetric on purpose: **Vulkan is the reference**, rendered
in-process through the same `GalleryCapture` the desktop parity test uses, and WebGPU is the subject, rendered by
a real headless browser. Both halves render 1280×720 and grab frame 60 on a frozen clock — that agreement is what
makes the two images comparable at all, and it is why `App.CaptureWidth`/`CaptureHeight` in `Kaldra.Web` must keep
matching `GalleryCapture.Width`/`Height`.

## Running it

```powershell
pwsh tools/web-parity.ps1              # publish Kaldra.Web, then run the leg
pwsh tools/web-parity.ps1 -SkipPublish # reuse the last publish
```

Needs the `wasm-tools` workload (`dotnet workload install wasm-tools`, from an **elevated** terminal — the MSI's
UAC prompt is silently cancelled in a non-interactive shell), a Vulkan-capable GPU, and Edge or Chrome. Missing
any of those, `WebGpuParityTests` SKIPs rather than fails, so it is safe to run anywhere.

The test never publishes: publishing takes minutes, so it reads `KALDRA_WEB_PUBLISH` and skips without it.
`tools/web-parity.ps1` is the wrapper that supplies one.

## The pieces

| Piece | Job |
|---|---|
| `Scripts/Verification/web-capture.ps1` | Serves a published `Kaldra.Web`, drives the browser once per demo, saves each posted PNG |
| `Kaldra.Gallery.Tests/WebGpuParityTests.cs` | Renders the Vulkan reference, diffs, decides skip-vs-fail |
| `tools/web-parity.ps1` | Publishes, sets `KALDRA_WEB_PUBLISH`, runs the test |

The tolerance is the same `ImageComparison.Compare` bar the desktop leg uses — a pixel fails if **any** channel
differs by more than 4/255, and the frame fails if more than **0.5%** of pixels are flagged. Note that this is
stricter than the engine's `Scripts/Verification/diff.ps1`, which averages the four channels: on `fullreference` the averaged
metric reports 12.1% of pixels over threshold where the per-channel one reports 20.4%. Reason about parity with
the test's metric, not the script's.

## Skip versus fail

A browser that cannot get a WebGPU adapter throws on the page and never posts, so **every** demo times out —
that is a capability gap, not a regression, and the test skips. A **partial** result is a failure: the browser
demonstrably works, so a demo that produced nothing is a demo that broke. `web-capture.ps1` reports this through
machine-readable exit codes (0 captured, 2 no browser, 3 no publish directory, 4 timed out) so the test can tell
the two apart without parsing prose.

Two guards stop a green result from being meaningless. The frame must hold at least 64 distinct colours on both
sides — two blank frames compare as identical, so without a floor a browser that rendered nothing would "pass" —
and at least one demo must actually have been compared.

## Stray browsers

`web-capture.ps1` launches the browser with a fixed `--user-data-dir` unique to its port, and that directory
doubles as an ownership marker: every process the browser spawns inherits it on the command line. The script
sweeps for processes carrying that marker **before and after every capture**, not just at the end.

This is not tidiness. A surviving browser from an interrupted run owns the profile lock, so the next launch
attaches to it and never navigates — the capture simply never arrives and the run reports a timeout with no hint
as to why. That failure mode is silent and it is sticky: it produced two confidently-wrong measurements during
this leg's development before the cause was found. The sweep matches on the profile marker and never on the
executable name, because the developer's own browser is very likely open.

## What diverges today

Measured against a Vulkan reference on an Adreno X1-85, with the browser on a real hardware adapter
(`qualcomm adreno-7xx` — `web-capture.ps1` prints `ADAPTER:` for exactly this reason; a silent software fallback
would invalidate the numbers).

| Demo | Result |
|---|---|
| `pixelart` | Bit-identical |
| `bloom` | Passes (max channel diff 1) |
| `particles` | Passes |
| `cube` | Matches within tolerance, but only 5 distinct colours — too flat to be evidence either way |
| `fullreference` | **Fails** — maxDiff 128, mean 2.330, 20.38% of pixels over 4/255 |
| `stylized` | **Fails** — maxDiff 160, mean 11.786, 49.39% of pixels over 4/255 |

The first three are enforced. The last two are excluded **by name, with their numbers written down**, rather than
by widening the tolerance: a global loosening would hide these two getting worse and would stop the other three
from catching anything. WGSL and SPIR-V do diverge more than SPIR-V and DXIL do — `NormalMapping` is excluded from
the desktop pairing for that reason at 0.9% — but nothing about precision explains half a frame moving. These are
functional divergences to be found and fixed, and each deserves its own issue.

`cube` is a useful negative result: it renders the same in the browser as on Vulkan. A flat-shaded cube on a
ground plane genuinely is about five colours, so a capture that looks suspiciously unlit is the demo, not the
backend. It stays out of the enforced list because a frame that flat cannot clear the near-empty guard, and
would add no signal if it could.

## In CI

`.github/workflows/web-parity.yml`, on manual dispatch and a nightly schedule rather than on every pull request:
publishing the wasm app costs minutes, and the runner needs both a GPU and a browser. The leg skips cleanly on a
runner that has neither, so a scheduled run reports honestly rather than failing.
