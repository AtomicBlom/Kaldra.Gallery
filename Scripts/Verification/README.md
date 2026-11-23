# Verification scripts

The browser half of the WebGPU parity leg (`docs/webgpu-parity.md`). The engine's `Scripts/Verification` has the image
inspectors (`colorcount.ps1`, `diff.ps1`, `coverage.ps1`).

| Script | Does |
|---|---|
| `web-capture.ps1` | Serves a published `Loom.Web` from a local `HttpListener`, drives a headless browser at `?capture=<name>&post` once per demo, and saves the PNG each page posts back. `WebGpuParityTests` runs it. Exit codes: **0** captured, **2** no browser, **3** no publish directory, **4** something timed out. `ADAPTER:` names the GPU the browser used: check it first when a parity result looks wrong. |
| `web-shot.ps1` | Loads a URL in headless Edge over the DevTools protocol, waits real time for the .NET runtime to boot and render, then reads a page expression and takes a screenshot. |

```powershell
dotnet publish Loom.Web -c Release -o pub
pwsh Scripts/Verification/web-capture.ps1 -PublishDir pub -Names pixelart,bloom -OutDir web-shots
```

The whole leg (publish, then compare against Vulkan) is `pwsh tools/web-parity.ps1`.
