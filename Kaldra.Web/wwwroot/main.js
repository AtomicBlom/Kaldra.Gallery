// Boot the .NET WASM runtime and run the C# entry point. The WebGPU shim is the backend's own, which it ships at the
// RCL's path (BinaryVibrance.Kaldra.Backend.WebGPU/wwwroot/kaldra-webgpu.js), so the gallery and every app run the same one; it is
// imported here (so the static-web-asset import map resolves its fingerprinted URL) and registered under the module
// name the backend's [JSImport]s reference; the device bring-up and render loop are driven from C# (Program.cs).

import { dotnet } from './_framework/dotnet.js'
import * as kaldraWebgpu from './_content/BinaryVibrance.Kaldra.Backend.WebGPU/kaldra-webgpu.js'

const { setModuleImports, runMain } = await dotnet.create();

setModuleImports('kaldra-webgpu', { ...kaldraWebgpu });

await runMain();
