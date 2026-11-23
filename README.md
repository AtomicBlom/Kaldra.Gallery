# Kaldra.Gallery

The engine's showcase and GPU test bed: demos of what the [Kaldra engine](https://github.com/AtomicBlom/Kaldra) (to become
Kaldra) draws, rendered offscreen by its tests and checked against committed golden images and across its backends.
It was split out of the engine's repository, and its projects keep their `Kaldra.Gallery` names for now.

| Project | What it is |
|---|---|
| `Kaldra.Gallery` | The demos (lighting, shadows, particles, skinning, SSAO, Vysara UI and more) and their shaders and assets. |
| `Kaldra.Gallery.Tests` | The demos rendered offscreen and diffed against `Goldens/`, Vulkan against DirectX 12, WebGPU against Vulkan, and on WARP; and checks against every shader the gallery ships. GPU-gated: a test without a GPU skips. |
| `Kaldra.Sandbox`, `Kaldra.Sandbox.Android` | The demos in a window, on the desktop and on Android. |
| `Kaldra.Web` | The demos in a browser, on WebGPU. |
| `samples/HelloAudio` | A synthesizer played from the keyboard: the engine and [Sirrow](https://github.com/AtomicBlom/Sirrow) together. |
| `Scripts/Verification` | The browser half of the WebGPU parity leg. |
| `docs` | The effect cookbook, [WebGPU pixel parity](docs/webgpu-parity.md) and [CI with a software rasterizer](docs/ci-software-rasterizer.md). |

## Building

The gallery builds on the engine, Sirrow and Vysara from checkouts beside this one, built from source
(`build/Sources.props`):

| Property | Default | Is |
|---|---|---|
| `KaldraSource` | `..\Kaldra\` | the engine's checkout (cloned recursively: VMASharp is its submodule) |
| `SirrowSource` | `..\Sirrow\` | the audio's checkout, for HelloAudio |
| `VysaraSource` | `..\Vysara\` | the UI library's checkout |

There is no packages mode: the gallery tests the engine as it stands, its tests take Vysara's software rasterizer, which
no package carries, and it compiles shaders with the engine's own build files. A machine's own choices go in an
untracked `dev.local.props` at the top level.

```
dotnet build Kaldra.Gallery.slnx
dotnet run --project Kaldra.Gallery.Tests
dotnet run --project Kaldra.Sandbox
pwsh tools/golden.ps1 -Update     # regenerate the golden images, then review and commit them
pwsh tools/web-parity.ps1         # publish Kaldra.Web and run the WebGPU parity test
```

`Kaldra.Web` and `Kaldra.Sandbox.Android` are outside `Kaldra.Gallery.slnx`, so the desktop build never needs the wasm-tools
or android workloads; `Kaldra.Web.slnx` and `HelloAudio.slnx` build those on their own.

## Licence

MIT (`LICENSE`). The Kenney assets the demos use are CC0 (`Kaldra.Gallery/Assets/kenney/LICENSE.md`).
