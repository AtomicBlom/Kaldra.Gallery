// Boot the .NET WASM runtime and run the C# entry point. The WebGPU shim is the backend's own, which it ships at the
// RCL's path (Loom.Backend.WebGPU/wwwroot/loom-webgpu.js), so the gallery and every app run the same one; it is
// imported here (so the static-web-asset import map resolves its fingerprinted URL) and registered under the module
// name the backend's [JSImport]s reference; the device bring-up and render loop are driven from C# (Program.cs).

import { dotnet } from './_framework/dotnet.js'
import * as loomWebgpu from './_content/Loom.Backend.WebGPU/loom-webgpu.js'

const { setModuleImports, runMain } = await dotnet.create();

setModuleImports('loom-webgpu', { ...loomWebgpu });

await runMain();
