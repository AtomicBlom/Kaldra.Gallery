# Loom Effect Cookbook

Reference patterns for the shipped rendering effects. Each entry shows the resource topology and the key graph structure. See `Loom.Sandbox/Demos/` for complete implementations.

---

## Deferred G-buffer + multi-light lighting

Classic deferred shading: geometry pass writes packed G-buffer targets; lighting pass reads them and accumulates lit colour.

```
scene ──► G-buffer(albedo, normal, material, depth)
                         │
               ┌─────────▼──────────┐
               │  lighting          │ .Samples(albedo, normal, material)
               │  (fullscreen)      │ .ReadsDepth(depth)
               └────────────────────┘
                         │
                    hdr colour
```

Key points:
- G-buffer normal pass: `.Writes(albedo).Writes(normal).Writes(material).WritesDepth(depth)`
- Lighting pass: `.Samples(albedo).Samples(normal).Samples(material).ReadsDepth(depth).Writes(hdr)`
- Multiple `.Writes` on a pass = multiple render targets (MRT)

---

## Single shadow map

Shadow caster depth pass feeds the lighting pass via a sampled shadow map.

```
g.Pass("shadow",  pb => pb.WritesDepth(shadowMap).Draw(…))
g.Pass("lighting", pb => pb.Samples(shadowMap).Writes(hdr).Draw(ctx =>
    ctx.FullScreen("lighting", albedo, normal, material, shadowMap)))
```

---

## SSAO

Screen-space ambient occlusion: two passes (SSAO computation, then blur) both operating in a low-res domain.

```csharp
var lowRes = g.Domain("ssao", scale: 0.5f);
var ao     = g.Color("ao",   Format.R8_UNorm, lowRes);
var aoBlur = g.Color("aoblur", Format.R8_UNorm, lowRes);

g.Pass("ssao",     pb => pb.Samples(depth).Samples(normal).Writes(ao).Draw(…));
g.Pass("ssao-blur", pb => pb.Samples(ao).Writes(aoBlur).Draw(…));
// Lighting pass then samples aoBlur
```

---

## Bloom pyramid

Iterative downsample then upsample using two ping-pong colour handles.

```csharp
var (read, write) = (g.Color("bloom-a", fmt, domain), g.Color("bloom-b", fmt, domain));

// Prefilter (bright-pass)
g.Pass("bloom-prefilter", pb => pb.Samples(hdr).Writes(write).Draw(…));
(read, write) = (write, read);

// Downsample chain
for (var i = 0; i < mips; i++)
{
    g.Pass($"bloom-down-{i}", pb => pb.Samples(read).Writes(write).Draw(…));
    (read, write) = (write, read);
}

// Upsample chain — blends to accumulate
for (var i = 0; i < mips; i++)
{
    g.Pass($"bloom-up-{i}", pb => pb.Samples(read).Blends(write).Draw(…));
    (read, write) = (write, read);
}

// Composite onto hdr
g.Pass("bloom-composite", pb => pb.Samples(read).Blends(hdr).Draw(…));
```

The handle-swap pattern (Invariant #5) is the idiomatic way to express iterative effects.

---

## GPU particles (compute → vertex)

A compute pass updates the particle buffer; a render pass reads it as vertex data.

```csharp
var particles = g.Buffer("particles", stride: 48, count: maxParticles);

g.Pass("particle-update",
    pb => pb.ReadWrites(particles).Dispatch(ctx => ctx.Dispatch("particle-update", groups, 1, 1)));

g.Pass("particle-render",
    pb => pb.ReadsVertex(particles).Blends(hdr).Draw(ctx =>
        ctx.DrawInstanced(quad, "particle-render", maxParticles)));
```

`.ReadWrites` for compute UAV; `.ReadsVertex` for vertex-buffer read.

---

## JFA outlines + GPU→CPU readback

Jump Flood Algorithm: seed pass, iterative JFA, resolve pass. Plus a readback pass for hover-object picking.

```csharp
var (jfaA, jfaB) = (g.Color("jfa-a", fmt, domain), g.Color("jfa-b", fmt, domain));
var idTarget     = g.Color("id",  Format.R32_UInt, domain);

// Seed: write object IDs
g.Pass("jfa-seed", pb => pb.Writes(jfaA).Writes(idTarget).Draw(…));
var (read, write) = (jfaA, jfaB);

// Iterations
for (var i = 0; i < iterations; i++)
{
    g.Pass($"jfa-{i}", pb => pb.Samples(read).Writes(write).Draw(…));
    (read, write) = (write, read);
}

// Outline composite
g.Pass("jfa-resolve", pb => pb.Samples(read).Blends(hdr).Draw(…));

// CPU readback for picking (hover ID)
g.Pass("id-readback", pb => pb.Samples(idTarget).ReadBack(idTarget));
```

The host reads the readback result from `FrameInputs.HoverId` next frame.

---

## Auto-exposure + tonemap

Two-pass HDR grading: compute average luminance, then tonemap using it.

```csharp
var exposure = g.Buffer("exposure", stride: 4, count: 1);

g.Pass("exposure-compute",
    pb => pb.Samples(hdr).ReadWrites(exposure).Dispatch(…));

g.Pass("tonemap",
    pb => pb.Samples(hdr).Samples(exposure).Writes(g.Backbuffer).Present().Draw(…));
```

---

## Forward alpha-blend transparency

A forward pass that blends translucent geometry on top of the deferred opaque result.

```csharp
g.Pass("transparent",
    pb => pb.Blends(hdr).ReadsDepth(depth).Draw(ctx =>
        ctx.Draw(transparentMesh, "transparent")));
```

`.Blends` + `.ReadsDepth` = depth-tested, depth-read-only, colour-additive/blend. No `.Writes` on depth means no depth clear — the opaque depth buffer remains intact.

---

## Low-resolution world + upscale

A fixed-pixel-art domain that renders at a constant size regardless of window dimensions, then upscales.

```csharp
var pixel = g.Domain("pixel", width: 320, height: 180);   // fixed size
var world = g.Color("world", Format.R8G8B8A8_UNorm, pixel);

g.Pass("world",  pb => pb.Writes(world).Draw(…));
g.Pass("upscale", pb => pb.Samples(world).Writes(g.Backbuffer).Present()
    .Draw(ctx => ctx.FullScreen("blit", world)));
```

The `blit` technique is engine-built-in and samples its input at the output resolution (nearest-neighbour, pixel-art clean).
