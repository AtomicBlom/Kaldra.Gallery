# Loom Effect Cookbook

Reference patterns for the shipped rendering effects. Each entry shows the resource topology and the key graph structure in current API. These are *application-side* recipes: they add ordinary passes that name app-supplied shaders, declare their reads and writes, and let the engine derive the ordering, every barrier and layout transition, and transient-memory aliasing. Nothing here is privileged engine vocabulary.

The shapes below are taken from the live demos in [`Loom.Gallery/Demos/`](../Loom.Gallery/Demos) — read those for the complete implementations (shader bodies, per-frame data, ImGui knobs).

A few conventions used throughout:

- A graph is authored once with `RenderGraph.Define(g => { … })`; passes chain their verbs — `g.Pass("name").Writes(x).Samples(y).Draw(ctx => …)` — and a compute pass is `g.Compute("name").ReadWrites(b).Dispatch(ctx => …)`.
- `g.Present(handle)` and `g.Readback(handle)` are **graph-builder** methods, not pass verbs.
- Targets are allocated on a `Domain`: `g.Screen.Color("name", Format.X)` (native size), `domain.Color("name", Format.X)`, `domain.Depth("name", Format.Depth32F)`, or a fractional mip with `domain.Color("name", Format.X, scale: 0.5f)`. `g.Backbuffer` is the swapchain target.
- `.Draw` bodies are elided with `…` where the body is incidental; the **graph structure** — passes, verbs, handles, formats — is real.

---

## Deferred G-buffer + lighting

Classic deferred shading. The geometry pass writes a packed G-buffer (albedo + world normal + world position, plus depth) as a multiple-render-target write; a single fullscreen lighting pass samples those targets and produces the lit HDR colour. Lighting accumulates inside the shader — the per-frame light/sun data rides the fragment push (or a constants channel) the pass reads.

```
scene ─► gbuffer { albedo, normal, position, depth }
                          │  .Samples(albedo, normal, position)
                ┌─────────▼──────────┐
                │  lighting           │
                │  (fullscreen)       │
                └─────────────────────┘
                          │
                       lit (HDR)
```

```csharp
var albedo   = g.Screen.Color("albedo",   Format.Rgba8Srgb);
var normal   = g.Screen.Color("normal",   Format.Rgba16F);
var position = g.Screen.Color("position", Format.Rgba16F);
var depth    = g.Screen.Depth("depth",    Format.Depth32F);
var lit      = g.Screen.Color("lit",      Format.Rgba16F);

g.Pass("gbuffer")
    .Reads(viewProj)
    .Writes(albedo, normal, position)   // multiple .Writes targets = MRT
    .WritesDepth(depth)
    .Draw(ctx => { /* draw scene meshes with the "gbuffer" technique */ });

g.Pass("lighting")
    .Samples(albedo, normal, position)
    .Writes(lit)
    .Draw(ctx => ctx.FullScreen("lighting", albedo, normal, position));
```

Notes:

- Multiple `.Writes` on one pass is the MRT idiom — the engine packs them as the pass's colour attachments in order, and `ctx.FullScreen` binds the lighting pass's `.Samples` inputs in the same order they're declared.
- **Many lights** can be folded into the single fullscreen pass (a light loop in the shader), or layered as additive light volumes — see [2D normal-mapped lights](#forward-alpha-blend-transparency) for the `.Blends`-accumulate variant.

---

## Single shadow map (+ cascaded)

A depth-only pass renders the casters from the *sun's* view into a shadow map that lives in its own square domain; the lighting pass samples that depth map and compares against each pixel's projected sun-space depth. The shadow pass reads the sun's view-projection; the G-buffer pass reads the camera's — the deferred split is what lets each transform be a single published matrix.

```
            sun view                       camera view
                │                               │
   ┌────────────▼───────────┐    ┌──────────────▼───────────┐
   │ shadow (depth-only)     │    │ gbuffer (MRT + depth)    │
   │  .WritesDepth(shadowMap)│    │  .Writes(albedo, normal,…)│
   └────────────┬───────────┘    └──────────────┬───────────┘
                └───────────────┬───────────────┘
                     ┌──────────▼──────────┐
                     │ lighting             │  .Samples(…, shadowMap)
                     └──────────────────────┘
```

```csharp
var sun       = g.Domain("sun_shadow", 1024, 1024);     // a fixed-size square domain
var shadowMap = sun.Depth("sun_depth", Format.Depth32F);

g.Pass("shadow")
    .Reads(sunViewProj)
    .WritesDepth(shadowMap)
    .Draw(ctx => { /* draw casters with the "depthonly" technique */ });

g.Pass("gbuffer")
    .Reads(viewProj)
    .Writes(albedo, normal, position)
    .WritesDepth(depth)
    .Draw(ctx => { /* … */ });

g.Pass("lighting")
    .Samples(albedo, normal, position, shadowMap)
    .Writes(screen)
    .Draw(ctx => ctx.FullScreen("light_shadow", sunViewProjPush, albedo, normal, position, shadowMap));
```

The sun's view-projection rides the fullscreen push (`ctx.FullScreen(technique, push, inputs…)`) so the lighting shader can project each pixel into the same clip space the shadow pass drew with.

**Cascaded shadow maps** are the same shape scaled up: render N depth slices (cascades) fitted to slices of the camera frustum, then a `lighting_csm` pass selects a cascade per pixel by view depth and samples that slice. The cascade matrices and split distances ride the per-frame uniform channel.

---

## SSAO

Screen-space ambient occlusion: a G-buffer pass writes world position + normal; the `ssao` pass samples them and writes a single-channel occlusion term; a blur pass smooths the rotation noise; the lighting (or a view) pass then samples the blurred AO. Adding SSAO is just adding edges — `gbuffer → ssao → ssao.blur → lighting` — and the engine derives the ordering.

```
gbuffer { position, normal } ─► ssao ─► ssao.blur ─► (sampled by lighting)
                                 R16F      R16F
```

```csharp
var position = g.Screen.Color("position", Format.Rgba16F);
var normal   = g.Screen.Color("normal",   Format.Rgba16F);
var ao       = g.Screen.Color("ao",       Format.R16F);   // single-channel occlusion
var aoBlur   = g.Screen.Color("ao_blur",  Format.R16F);

g.Pass("ssao.calculate")
    .Samples(position, normal)
    .Writes(ao)
    .Draw(ctx => ctx.FullScreen("ssao", cameraViewProjPush, position, normal));

g.Pass("ssao.blur")
    .Samples(ao)
    .Writes(aoBlur)
    .Draw(ctx => ctx.FullScreen("ssao.blur", ao));

// the lighting pass then adds aoBlur to its .Samples and multiplies it into ambient
```

The AO target is `R16F`, not an 8-bit format. SSAO commonly runs in a half-resolution domain (`g.Domain("ssao", 0.5f)`) to save fill — allocate `ao`/`aoBlur` from that domain instead of `g.Screen` and the blur/sample chain follows.

---

## Bloom pyramid

Iterative downsample then additive upsample over a chain of progressively smaller mips. This is the canonical reusable graph fragment — it composes as a plain extension method on `GraphBuilder` and returns its output handle. The structure is **bright-pass → N downsamples → N−1 additive upsamples → composite**.

```
hdr ─► prefilter ─► mip0
                     │ down
                     ▼
                    mip1 ─ down ─► mip2 ─ down ─► … ─► mipN-1
                     ▲                                   │
                     │  up (.Blends, additive)           │
                     └──────────────◄────────────────────┘
        composite(source, mip0) ─► output
```

```csharp
public static ColorHandle Bloom(this GraphBuilder g, ColorHandle source, float threshold, int mips, float intensity)
{
    var domain = source.Domain;

    // The mip chain: each mip is half the size of the one above (scale 1/2, 1/4, 1/8, …).
    var chain = new ColorHandle[mips];
    for (var i = 0; i < mips; i++)
        chain[i] = domain.Color($"bloom_mip{i}", Format.Rgba16F, scale: 1f / (1 << (i + 1)));

    // Bright-pass + first halving into mip0 (threshold rides the fragment push).
    g.Pass("bloom.prefilter")
        .Samples(source)
        .Writes(chain[0])
        .Draw(ctx => ctx.FullScreen("bloom.prefilter", [threshold], source));

    // Downsample: each mip is the box-filtered half of the one above.
    for (var i = 1; i < mips; i++)
    {
        var (read, write) = (chain[i - 1], chain[i]);
        g.Pass($"bloom.down{i}")
            .Samples(read)
            .Writes(write)
            .Draw(ctx => ctx.FullScreen("bloom.down", read));
    }

    // Upsample from the smallest mip back up, additively accumulating into the larger mip the
    // downsample already wrote. .Blends (LoadOp.Load + additive) preserves + adds; the RAW edge
    // on each .Blends target forces the strict mipN → … → mip0 ordering.
    for (var i = mips - 2; i >= 0; i--)
    {
        var (smaller, larger) = (chain[i + 1], chain[i]);
        g.Pass($"bloom.up{i}")
            .Samples(smaller)
            .Blends(larger)
            .Draw(ctx => ctx.FullScreen("bloom.up", smaller));
    }

    // Composite the fully-accumulated mip0 back over the source into a fresh output — a plain
    // .Writes, so the bloom mips are dead afterwards and their memory can alias away.
    var output = domain.Color("lit_bloomed", Format.Rgba16F);
    g.Pass("bloom.composite")
        .Samples(source, chain[0])
        .Writes(output)
        .Draw(ctx => ctx.FullScreen("bloom.composite", [intensity], source, chain[0]));

    return output;
}
```

The `.Writes` vs `.Blends` distinction is load-bearing: the upsample passes `.Blends` the larger mip so the graph preserves the downsample's contents and accumulates onto them. A graph that only knew `.Writes` would lose the accumulation or miss the dependency.

### Instantiating a fragment more than once

Every name above is fixed — `bloom_mip0`, `bloom.prefilter`, `lit_bloomed` — and names are unique per graph, so calling `g.Bloom(…)` twice would collide. Wrap each call in a name scope instead of threading a prefix parameter through the fragment:

```csharp
ColorHandle left, right;
using (g.Scope("left"))  left  = g.Bloom(litLeft);
using (g.Scope("right")) right = g.Bloom(litRight);
```

The fragment is unchanged and unaware: its `bloom_mip0` becomes `left.bloom_mip0` and `right.bloom_mip0`, its passes become `left.bloom.prefilter` and so on, and the two chains derive as independent dependency chains. Scopes nest, and the qualified name is what the duplicate-name check and the graph hash see.

Two things are deliberately *not* scoped. **Techniques** are registered on the engine rather than declared on the graph, so `ctx.FullScreen("bloom.prefilter", …)` inside a scope still names the technique exactly as registered — only the pass sharing that name gets qualified. And the **backbuffer** keeps its identity whatever scope first touches it, since there is one swapchain image.

Pass names get a bonus: `DebugLabelScopes` derives the RenderDoc/PIX event tree from the same dots, so a scoped fragment's passes group under its scope in the capture with no extra work.

---

## GPU particles (compute → vertex)

A compute pass writes particle state into a storage buffer; a render pass reads that same buffer as a per-instance vertex stream and additively blends the sparks over the scene. Two edges — `.ReadWrites` then `.ReadsVertex` — and the engine derives the compute→vertex-input barrier between them (the one piece of sync unique to this effect).

```
particles (g.Buffer) ──.ReadWrites──► [compute: particle.update]
        │
        └──.ReadsVertex──► [draw: particle.render]  ──.Blends──► lit
```

```csharp
// A field, declared once beside the technique registration: one float4 per particle (xyz position + remaining life)
// as the per-instance stream at binding 0. "particle.render" names this same layout as its `Instance` stream, so the
// buffer's elements and the vertex stream that walks them cannot drift apart.
static readonly VertexLayout ParticleLayout = new(
    VertexInputRate.PerInstance, binding: 0,
    new VertexAttribute("POSITION", VertexFormat.Float4));

const int particleCount = 4096;
var particles = g.Buffer("particles", particleCount, ParticleLayout);   // sized count × the layout's 16-byte stride

g.Compute("particle.update")
    .ReadWrites(particles)
    .Dispatch(ctx => ctx.Dispatch(
        "particle.update",
        ((uint)(particleCount / 64), 1, 1),   // workgroup grid
        [ctx.Time],                            // up-to-16-float push block
        [],                                    // sampled inputs (none)
        [particles]));                         // read-write storage (the .ReadWrites resources)

g.Pass("particles")
    .Reads(viewProj)
    .Blends(lit)
    .ReadsVertex(particles)
    .Draw(ctx => ctx.Draw(new DrawCall
    {
        Technique = "particle.render",
        VertexCount = 6,                        // a billboard quad fabricated per particle from SV_VertexID
        Instances = DrawCall.Gpu(particles, particleCount),
    }));
```

`ctx.Dispatch` takes the workgroup grid as a `(uint X, uint Y, uint Z)` tuple, then three spans: the push block, the sampled inputs, and the read-write storage (matching the pass's `.Samples` / `.ReadWrites`). The render is a *procedural* draw — `Mesh` is null, `VertexCount` sets the per-instance vertex count, and `DrawCall.Gpu(buffer, count)` points the instance stream at the compute-filled buffer (CPU-sourced instances use `DrawCall.From(span)` instead).

Because the buffer was declared in terms of a layout rather than a byte count, both ways this can go wrong are caught on the CPU instead of reading past the end of the buffer on the GPU: `DrawCall.Gpu` rejects a `count` larger than the buffer holds, and the draw rejects a technique whose per-instance stream advances by a different stride than the buffer's elements.

---

## JFA outlines + GPU→CPU readback

A Jump Flood Algorithm builds a screen-space distance field to draw an outline, and a readback edge hands a picked object id back to the CPU. The cube writes its object id into an id target; that target is read back so the host knows which object the cursor is over; a seed pass marks the hovered object's pixels and a jump-flood ping-pong (steps 4→2→1) propagates the nearest seed; a resolve pass turns nearest-seed distance into an emissive ring composited over the lit scene. Runs in a fixed `320×180` domain so the flood has concrete pixel dimensions and the cursor maps cleanly.

```
scene ─► lit
  └─► id (R16F) ──► g.Readback(id) ─ ─ ─► host: engine.Read(id) at the cursor → hoveredId
                                                   │ (next frame, via a constants channel)
                                                   ▼
   seed ─► [jfa step4] ─► [jfa step2] ─► [jfa step1] ─► resolve(flood, lit) ─► outlined
            (Rg16F ping-pong: read/write handles swapped each iteration)
```

```csharp
var world = g.Domain("world", 320, 180);
var lit   = world.Color("lit",       Format.Rgba16F);
var ids   = world.Color("object_id", Format.R16F);     // id target (R16F here; R32Uint for large id ranges)
var seedA = world.Color("outline_a", Format.Rg16F);    // JFA stores nearest-seed coords
var seedB = world.Color("outline_b", Format.Rg16F);

// Write the cube's object id (the id rides the per-draw uniform block), then read it back.
g.Pass("id")
    .Reads(viewProj)
    .Writes(ids)
    .Draw(ctx => { /* draw cube with the "id" technique; SetUniforms the id */ });
g.Readback(ids);

g.Pass("outline.seed")
    .Samples(ids)
    .Reads(hoverParams)        // the host writes the hovered id into this constants channel
    .Writes(seedA)
    .Draw(ctx => ctx.FullScreen("outline.seed", ids));

// Ping-pong via handle swap (the handle-swap pillar) — two physical targets, swapped each step.
var (read, write) = (seedA, seedB);
for (var step = 4; step >= 1; step >>= 1)
{
    var src = read;            // capture per-iteration so the closure follows this step's handle
    float jump = step;
    g.Pass($"outline.jfa{step}")
        .Samples(src)
        .Writes(write)
        .Draw(ctx => ctx.FullScreen("outline.jfa", [jump, world.Width, world.Height], src));
    (read, write) = (write, read);   // swap; the next pass reads what this one wrote
}

var flood = read;              // the final flood after the swaps
g.Pass("outline.resolve")
    .Samples(flood, lit)
    .Writes(outlined)
    .Draw(ctx => ctx.FullScreen("outline.resolve", flood, lit));
```

**Reading the pick result.** There is no engine picking state and no `FrameInputs` hover field. The host queries the readback target directly with `IEngine.Read(handle)`, which returns the target's texels tightly packed in its declared format, a frame or two old:

```csharp
// host side, after Render: decode the id under the cursor (the id target is R16F → Half)
var (pw, ph) = idTarget.Resolve();                         // the target's own resolution
var pixels   = MemoryMarshal.Cast<byte, Half>(engine.Read(idTarget));
var i        = cursorY * pw + cursorX;
uint hoverId = i < pixels.Length ? (uint)MathF.Round((float)pixels[i]) : 0u;
// feed it back for next frame's seed pass:
engine.Write(hoverParams, new OutlineParams(hoverId));
```

The latency is real (the copy is from a frame the GPU has finished, so it never races in-flight work) — the picked id arrives one to two frames later and is fed into the *next* frame's seed pass through an ordinary constants channel.

---

## Auto-exposure + tonemap

Two-stage HDR grading: a compute pass meters the lit scene's average luminance into a tiny **persistent** 1×1 target that carries across frames (so the adaptation is temporal), then a tone-map pass scales by that exposure and rolls off the highlights to the display. This is the GPU→persistent→GPU loop plus the compute→fragment hand-off the engine derives.

```
lit ──.Samples──► [compute: exposure] ──.ReadWrites──► exposure (1×1 Persistent, R16F)
                                                              │ carried frame to frame
 lit ─┬──.Samples──────────────────────────────────.Samples─┘
      └──► [tonemap] ──► graded
```

```csharp
var lit      = g.Screen.Color("lit", Format.Rgba16F);
var meter    = g.Domain("meter", 1, 1);                       // a fixed 1×1 domain
var exposure = meter.Persistent("exposure", Format.R16F);     // cross-frame, NOT a g.Buffer
var graded   = g.Screen.Color("graded", Format.Rgba16F);

// Meter the lit scene's luminance into the 1×1 persistent exposure (the adaptation rate rides the push).
g.Compute("exposure")
    .Samples(lit)
    .ReadWrites(exposure)
    .Dispatch(ctx => ctx.Dispatch("exposure", (1, 1, 1), [8f * ctx.DeltaTime], [lit], [exposure]));

// Scale by exposure + roll off highlights (SDR), or preserve the extended range for HDR output.
g.Pass("tonemap")
    .Samples(lit, exposure)
    .Writes(graded)
    .Draw(ctx => ctx.FullScreen("tonemap", [ctx.HdrOutput ? 1f : 0f], lit, exposure));
```

The exposure history is a `domain.Persistent(...)` target, not a storage buffer — a persistent target keeps its contents across frames so this frame's metering reads last frame's value and eases toward the new one. The dispatch is a single 1×1×1 group reducing the whole image; the adaptation rate (here `8 · ΔtimeDelta`) rides the push block.

---

## Forward alpha-blend transparency

A forward pass that blends translucent geometry over the deferred opaque result. It `.Blends` the lit colour (load + alpha-over) and `.ReadsDepth` the opaque depth (depth-tested but read-only), so the opaque scene occludes the transparent geometry without the transparent pass clearing or writing depth.

```
(opaque lit + depth)
        │  .Blends(lit)  + .ReadsDepth(depth)
        ▼
   transparent ─► lit (blended in place)
```

```csharp
engine.DefineTechnique("transparent",
    new TechniqueDescriptor("transparent", HasVertexInput: true, Blend: BlendMode.AlphaOver));

g.Pass("transparent")
    .Reads(viewProj)
    .Blends(lit)
    .ReadsDepth(depth)
    .Draw(ctx => ctx.Draw(DrawCall.Create(transparentMesh, "transparent")));
```

`.Blends` + `.ReadsDepth` = depth-tested, depth-read-only, colour-blended. No `.WritesDepth` means no depth clear — the opaque depth buffer stays intact and keeps occluding. The same shape with `Blend: BlendMode.Additive` (and a depth test) is how emissive volumes, a glowing summoning ring, or additive 2D light volumes layer onto a lit scene.

---

## Low-resolution world + upscale

A fixed-pixel-art domain renders at a constant size regardless of window dimensions; a final fullscreen pass samples that low-res target up to the backbuffer. The upscale *is* the pixelation — nearest-neighbour sampling keeps the pixels crisp.

```
       g.Domain("world", 320, 180)            g.Backbuffer (native)
   ┌──────────────────────────────┐        ┌──────────────────────┐
   │  scene → … → world (Rgba16F)  │ ─────► │  upscale (blit)      │
   └──────────────────────────────┘ sample └──────────────────────┘
```

```csharp
var world  = g.Domain("world", 320, 180);              // fixed size, not window-relative
var lit    = world.Color("lit", Format.Rgba16F);
var screen = g.Backbuffer;

g.Pass("scene")
    .Writes(lit)
    .WritesDepth(world.Depth("depth", Format.Depth32F))
    .Draw(ctx => { /* render the world at 320×180 */ });

g.Pass("upscale")
    .Samples(lit)
    .Writes(screen)
    .Draw(ctx => ctx.FullScreen("blit", lit));

g.Present(screen);
```

`ctx.FullScreen` with a single input is the blit/upscale path — a fullscreen *sample* of the source at the output resolution, not a copy. The `blit` shader ships in Loom's shared shader library; you still register it as a technique (`engine.DefineTechnique("blit", new TechniqueDescriptor("blit"))`) — define it without `LinearFilter` for the nearest-neighbour, pixel-art-clean look. A full pixel-art scene typically inserts a `g.Bloom(lit, …)` between the world and the upscale so the lights and sparks glow before they're enlarged.
