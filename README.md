# Kaldra.Gallery

The engine's showcase and GPU test bed: demos of what the [Loom engine](https://github.com/AtomicBlom/Loom) (to become
Kaldra) draws, rendered offscreen by its tests and checked against committed golden images and across its backends.
It was split out of the engine's repository, and its projects keep their `Loom.Gallery` names for now.

| Project | What it is |
|---|---|
| `Loom.Gallery` | The demos (lighting, shadows, particles, skinning, SSAO, SUIsei UI and more) and their shaders and assets. |
| `Loom.Gallery.Tests` | The demos rendered offscreen and diffed against `Goldens/`, Vulkan against DirectX 12, WebGPU against Vulkan, and on WARP; and checks against every shader the gallery ships. GPU-gated: a test without a GPU skips. |
| `Loom.Sandbox`, `Loom.Sandbox.Android` | The demos in a window, on the desktop and on Android. |
| `Loom.Web` | The demos in a browser, on WebGPU. |
| `samples/HelloAudio` | A synthesizer played from the keyboard: the engine and [Sirrow](https://github.com/AtomicBlom/Sirrow) together. |

## Building

The gallery builds on the engine, Sirrow and SUIsei from checkouts beside this one, built from source
(`build/Sources.props`):

| Property | Default | Is |
|---|---|---|
| `LoomSource` | `..\Kaldra\` | the engine's checkout (cloned recursively: SUIsei and VMASharp are its submodules) |
| `SirrowSource` | `..\Sirrow\` | the audio's checkout, for HelloAudio |
| `SUIseiSource` | the SUIsei submodule in the engine's checkout | the UI library's checkout |

There is no packages mode: the gallery tests the engine as it stands, its tests take SUIsei's software rasterizer, which
no package carries, and it compiles shaders with the engine's own build files. A machine's own choices go in an
untracked `dev.local.props` at the top level.

```
dotnet build Loom.Gallery.slnx
dotnet run --project Loom.Gallery.Tests
dotnet run --project Loom.Sandbox
pwsh tools/golden.ps1 -Update     # regenerate the golden images, then review and commit them
pwsh tools/web-parity.ps1         # publish Loom.Web and run the WebGPU parity test
```

`Loom.Web` and `Loom.Sandbox.Android` are outside `Loom.Gallery.slnx`, so the desktop build never needs the wasm-tools
or android workloads; `Loom.Web.slnx` and `HelloAudio.slnx` build those on their own.

## Licence

MIT (`LICENSE`). The Kenney assets the demos use are CC0 (`Loom.Gallery/Assets/kenney/LICENSE.md`).
