---
type: How-To
title: Build the ring reference tree, assign it to BodyTemplate.RingsReference, dispose and recreate the rings renderer
description: Give any moon or planet a ring by constructing the same reference classes the XML loader would, validating every asset first, assigning the tree to the body template and forcing the rings renderer to be recreated, with painted band textures
category: celestials
group: atmospheres-and-rings
topic: add-or-restyle-planetary-rings
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/unscience/features/bloomin-onion-runtime-planetary-rings
  - mods/unscience/features/bloomin-onion-painted-ring-textures
techniques: [reflection]
hooks:
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent)
  - reflection KSA.Program._planetTransparenciesRenderer
  - reflection KSA.PlanetTransparenciesRenderer._ringsRenderer
  - reflection KSA.PlanetTransparenciesRenderer._ringRendererCreated
  - reflection KSA.PlanetTransparenciesRenderer._anyRings
lifecycle: [frame:draw-ui, frame:prepare, render:atmosphere, render:scene-opaque]
game_types:
  - KSA.PlanetaryRingsReference
  - KSA.PlanetaryRingsVolumeReference
  - KSA.RingRaymarchingStepReference
  - KSA.RingObjectsReference
  - KSA.RingLodReference
  - KSA.MeshFileReference
  - KSA.PbrMaterialReference
  - KSA.AstronomicalTemplate
  - KSA.PlanetTransparenciesRenderer
  - KSA.Program
  - KSA.StaticCelestial
  - KSA.DistantSphereRenderer
  - KSA.TextureReference
tags: [rings, reflection, renderer-rebuild, painted-texture, template]
---
Outcome: a ring appears on a body that has none (or replaces the one it has) after a short hitch, no game file is changed, and Remove restores whatever the body had before. Demonstrated by unscience's Bloomin' Onion ([Bloomin' Onion runtime planetary rings](../../../../mods/unscience/features/bloomin-onion-runtime-planetary-rings.md), [painted ring textures](../../../../mods/unscience/features/bloomin-onion-painted-ring-textures.md)).

## Background: how the game does it

The ring is `celestial.BodyTemplate.RingsReference`. The render data (meshes, index counts, material handles, field settings, descriptor sets, instance buffers) is built by the `PlanetaryRingsRenderData` constructor, which only runs from `PlanetaryRingsRenderer.PopulatePlanets`, which only runs from the renderer's constructor ([current/decomp/KSA.Rendering.Rings.Rendering/PlanetaryRingsRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Rings.Rendering/PlanetaryRingsRenderer.cs#L161-L172)). Why each rebuild step is needed (verified in build 5554):

| Step | Reason in the game code |
|---|---|
| Dispose the rings renderer, clear `_ringRendererCreated` | `PlanetTransparenciesRenderer.RebuildFrameResources` only calls `_ringsRenderer.RebuildFrameResources` when the flag is true, and that never re-reads ring data ([current/decomp/KSA/PlanetTransparenciesRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetTransparenciesRenderer.cs#L374-L430)) |
| Recreate rather than call `PlanetaryRingsRenderer.PopulatePlanets` again | It is public but never clears `PlanetToRingsRenderData`, and the mesh culling buffers are sized in the constructor |
| `PlanetTransparenciesRenderer.PopulatePlanets()` and write `_anyRings` | Rebuilds the list of bodies with atmosphere or ring; the field is set only in the constructor, and is the switch that creates a rings renderer at all in a system without stock rings ([current/decomp/KSA/PlanetTransparenciesRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetTransparenciesRenderer.cs#L153)) |
| `Program.RebuildRenderer()` | Runs the create branch with fresh render targets; the new render data reads the whole tree ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5105)) |

What reads `RingsReference` when: the render-data constructor once (needs the rebuild); `UpdateData` every frame, CPU-sampling the control texture (it must stay a CPU-readable RGBA texture); the planet renderer's ring shadow and `AtmosphereRenderer.FillPlanetAtmosphereData`'s atmospheric ring shadow every frame (follow immediately; the latter dereferences `Texture` unconditionally); `DistantSphereRenderer` once, when the `StaticCelestial` is created (needs a sync).

## Lifecycle and timing

The Apply and Remove buttons run from the panel inside `[StarMapAfterGui]` (`frame:draw-ui`). **The rebuild is a direct, synchronous call from inside the ImGui frame**: the mod does not set `Program.RendererRebuildNeeded`; it calls `Program.RebuildRenderer()` itself. At that moment `OnFrame` has passed `PrepareFrame` and the viewport and UI phases but has not reached `ImGui.Render`, `OnPreRender`'s swapchain acquire or `RenderGame`, so no image is acquired and no command buffer is recorded. In-flight frames may still reference the ring pipelines, buffers and descriptor sets, hence `Device.WaitIdle()` before disposing the old renderer; `RebuildRenderer` itself waits again before disposing render targets and as its last statement. A deferred rebuild would run after the handler returns, so the controller could not revert the template on failure or prune assets that only become unreferenced once the new render data exists. Saves replay the ring at the physics handoff in `PrepareFrame` (`frame:prepare`), before ImGui starts, so that rebuild runs outside any ImGui frame. The ring is drawn in `render:atmosphere` (band and volumetrics) and `render:scene-opaque` (rock field). See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [handle renderer rebuilds](../../../rendering/vulkan-passes/handle-renderer-rebuilds/overview.md).

## Steps

1. **Validate, then resolve every asset first.** Check radii, LOD count (up to 5), positive sizes and that the inner radius is outside the body; resolve the band and control textures, the rock material textures and one mesh per LOD before constructing anything. A failed lookup returns an error and the game is untouched. Empty ids fall back to stock Saturn assets found on any ringed body in the current system or by their known ids (`SaturnRings`, `RingRock_LOD0..4`):

```csharp
// bloomin-onion.lib/RingReferenceBuilder.cs L55-L65
public PlanetaryRingsReference? Build(RingDefinition definition, Celestial celestial, out string error)
{
    error = Validate(definition, celestial) ?? "";
    if (error.Length > 0) return null;

    if (!ResolveBand(definition, out var band, out var control, out error)) return null;
    if (!ResolveTexture(definition.DiffuseId, _stock.Diffuse, "rock diffuse", out var diffuse, out error)) return null;
    if (!ResolveTexture(definition.PbrId, _stock.Pbr, "rock AoRoughMetal", out var pbr, out error)) return null;
    if (!ResolveNormal(definition.NormalId, out var normal, out error)) return null;
    if (!ResolveLods(definition, out var lods, out error)) return null;

```

2. **Construct the tree** with the same classes the XML loader creates. Angles go through `MathEx.ToDeviationAngle` / `ToCompassAngle` as `OnDataLoad` does. Freshly constructed `MeshFileReference` / `TextureReference` objects resolve `Get()` to themselves, so the renderer can dereference them like loaded data. Do not gate on `rings.IsValid()`: `DistanceReference.IsValid()` demands more than 100 km and the stock 10 m rocks fail it too ([current/decomp/KSA/DistanceReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DistanceReference.cs#L162-L165)):

```csharp
// bloomin-onion.lib/RingReferenceBuilder.cs L66-L111
var rings = new PlanetaryRingsReference
{
    DefinitionFrame = definition.UseEclipticFrame ? OrbitDefinitionFrame.Ecliptic : OrbitDefinitionFrame.Equatorial,
    // The game normalizes these the same way when it loads XML (PlanetaryRingsReference.OnDataLoad).
    Inclination = new RadianReference(MathEx.ToDeviationAngle(Degrees(definition.InclinationDeg))),
    LongitudeOfAscendingNode = new RadianReference(MathEx.ToCompassAngle(Degrees(definition.LongitudeOfAscendingNodeDeg))),
    InnerRadius = Km(definition.InnerRadiusKm),
    OuterRadius = Km(definition.OuterRadiusKm),
    Texture = band,
    ControlTexture = control,
    DetailScale = DoubleReference.FromValue(definition.DetailScale),
    Volume = new PlanetaryRingsVolumeReference
    {
        MinThickness = Km(definition.VolumeMinThicknessKm),
        MaxThickness = Km(definition.VolumeMaxThicknessKm),
        MinRenderDistance = Km(definition.VolumeMinRenderDistanceKm),
        MaxRenderDistance = Km(definition.VolumeMaxRenderDistanceKm),
        Step = new RingRaymarchingStepReference
        {
            Scale = definition.StepScale,
            MinSize = Km(definition.StepMinSizeKm),
            MaxSize = Km(definition.StepMaxSizeKm),
        },
        FadeToMeshes = new BoolReference(definition.FadeToMeshes),
    },
    RingObjects = new RingObjectsReference
    {
        Name = string.IsNullOrWhiteSpace(definition.ObjectsName) ? "BloominOnionRocks" : definition.ObjectsName,
        Thickness = Km(definition.ObjectThicknessKm),
        Size = new DistanceReference(definition.ObjectSizeM),
        RenderDistance = Km(definition.ObjectRenderDistanceKm),
        Density = DoubleReference.FromValue(definition.ObjectDensityPerKm3),
        Lods = lods,
        MaterialReference = new PbrMaterialReference
        {
            DiffuseReference = diffuse,
            NormalReference = normal,
            PBRMap = pbr,
        },
    },
};

// Deliberately NOT gated on rings.IsValid(): DistanceReference.IsValid() demands > 100 km,
// which the stock 10 m rocks / 1 km thickness / 20 km draw distance fail too — the game
// never enforces it. Validate() above is the real check.
return rings;
```

3. **Assign and snapshot.** Record the template's original `RingsReference` once per `CelestialTemplate` (null for most bodies, the stock ring for Saturn), assign the new tree, and revert the assignment if the rebuild fails:

```csharp
// bloomin-onion.lib/RingDefinitionController.cs L73-L102
public bool Apply(Celestial celestial, RingDefinition definition, out string message)
{
    var template = celestial.BodyTemplate;
    if (template == null)
    {
        message = $"{celestial.Id} has no body template";
        return false;
    }

    var reference = Builder.Build(definition, celestial, out message);
    if (reference == null) return false;

    if (!_originals.ContainsKey(template)) _originals[template] = template.RingsReference;
    var previous = template.RingsReference;
    template.RingsReference = reference;

    if (!RingRendererRebuilder.Rebuild(out var rebuildMessage))
    {
        template.RingsReference = previous;
        message = rebuildMessage;
        return false;
    }

    _applied[celestial.Id] = new AppliedRing(celestial, definition.Clone(), reference);
    RingRendererRebuilder.SyncDistantSphereShadow(celestial);
    PruneUnusedAssets();
    message = $"ring '{definition.Name}' applied to {celestial.Id}; {rebuildMessage}";
    return true;
}

```

4. **Rebuild the rings renderer** (device wait, dispose, clear the flag, repopulate and write `_anyRings`, `RebuildRenderer`):

```csharp
// bloomin-onion.lib/RingRendererRebuilder.cs L28-L77
public static bool Rebuild(out string message)
{
    try
    {
        var program = Program.Instance;
        if (program == null)
        {
            message = "game renderer not ready";
            return false;
        }
        var transparencies = ReflectionHelpers.GetFieldValue<PlanetTransparenciesRenderer>(program, "_planetTransparenciesRenderer");
        if (transparencies == null)
        {
            message = "Program._planetTransparenciesRenderer not found (game update?)";
            return false;
        }

        // In-flight frames may still reference ring pipelines/buffers/textures.
        Program.GetRenderer().Device.WaitIdle();
        DisposeRingsRenderer(transparencies);

        bool anyRings = transparencies.PopulatePlanets();
        ReflectionHelpers.SetFieldValue(transparencies, "_anyRings", anyRings);

        program.RebuildRenderer();
        message = anyRings ? "renderer rebuilt with rings" : "renderer rebuilt (no rings in system)";
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"bloomin-onion: renderer rebuild failed: {ex}");
        message = $"renderer rebuild failed: {ex.Message}";
        return false;
    }
}

/// <summary>True while the game's planetary rings renderer exists.</summary>
public static bool IsRingsRendererCreated()
{
    var transparencies = ReflectionHelpers.GetFieldValue(Program.Instance, "_planetTransparenciesRenderer");
    return ReflectionHelpers.GetFieldValue(transparencies, "_ringRendererCreated") is true;
}

private static void DisposeRingsRenderer(PlanetTransparenciesRenderer transparencies)
{
    if (ReflectionHelpers.GetFieldValue(transparencies, "_ringRendererCreated") is not true) return;
    if (ReflectionHelpers.GetFieldValue(transparencies, "_ringsRenderer") is not PlanetaryRingsRenderer ringsRenderer) return;
    ringsRenderer.Dispose();
    ReflectionHelpers.SetFieldValue(transparencies, "_ringRendererCreated", false);
}
```

5. **Sync the far-away sphere and free stale assets.** `SyncDistantSphereShadow` rewrites the ring fields of the body's `DistantSphereRenderer._material` (reflection; cosmetic, so a mismatch is logged and skipped); `PruneUnusedAssets` releases painted textures and converted meshes that no applied ring references any more, safe only after the rebuild because the device is then idle.

6. **Remove** restores the snapshot and rebuilds the same way; `RemoveAll` restores every body and rebuilds once.

## Painted band and control textures

A ring can be authored without a content file by rasterising stripes into two one-row RGBA8 strips: the band (colour and alpha, GPU sampled, also drives the ring shadow) and the control strip (R = rock field allowed, G = volumetric thickness blend; CPU and GPU sampled). The strips become game textures through a `TextureReference` subclass that seeds the private `TextureAsset` backing field by reflection and then calls the game's own `Bind`, so the renderer treats it exactly like a stock texture. Keep the CPU copy alive: the ring renderer samples the control strip through `TextureAsset.Texture.Data` every frame.

```csharp
// bloomin-onion.lib/PaintedTextureReference.cs L50-L73
var texture = GenericTexture.Defaults.RGBA8UNorm(new int2(width, height));
rgba.AsSpan().CopyTo(texture.Data);

var reference = new PaintedTextureReference
{
    Id = id,
    Category = TextureCategory.Default,
    Width = width,
    Height = height,
};
reference.SetHash();
TextureAssetField.SetValue(reference, new TextureAsset(texture, id));

var renderer = Program.GetRenderer();
// StagingPool disposal submits and waits, so the upload is complete on return.
using (var stagingPool = renderer.Allocator.CreateStagingPool(renderer.GraphicsAndCompute, 1))
    reference.Bind(renderer, stagingPool);

if (reference.BindlessHandle == 0)
{
    error = $"texture '{id}' did not receive a bindless handle";
    return null;
}
return reference;
```

```csharp
// bloomin-onion.lib/RingBandPainter.cs L17-L45
public static byte[] PaintBand(RingDefinition definition)
{
    var pixels = new byte[Width * 4];
    for (int x = 0; x < Width; x++)
    {
        float4 color = Evaluate(definition, x);
        pixels[x * 4 + 0] = ToByte(color.X);
        pixels[x * 4 + 1] = ToByte(color.Y);
        pixels[x * 4 + 2] = ToByte(color.Z);
        pixels[x * 4 + 3] = ToByte(color.W);
    }
    return pixels;
}

/// <summary>The control strip derived from the band's opacity.</summary>
public static byte[] PaintControl(RingDefinition definition)
{
    var pixels = new byte[Width * 4];
    float threshold = (float)Math.Clamp(definition.MeshCoverageThreshold, 0.0, 1.0);
    for (int x = 0; x < Width; x++)
    {
        float alpha = Evaluate(definition, x).W;
        float meshes = threshold >= 1f ? 0f : Math.Clamp((alpha - threshold) / 0.1f, 0f, 1f);
        pixels[x * 4 + 0] = ToByte(meshes);
        pixels[x * 4 + 1] = ToByte(alpha);
        pixels[x * 4 + 2] = 0;
        pixels[x * 4 + 3] = 255;
    }
    return pixels;
```

## Complete example

| File | Role |
|---|---|
| [bloomin-onion.lib/RingReferenceBuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/RingReferenceBuilder.cs) | Validation, asset resolution and tree construction |
| [bloomin-onion.lib/RingRendererRebuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/RingRendererRebuilder.cs) | Dispose, repopulate, `_anyRings`, `RebuildRenderer`, distant-sphere sync |
| [bloomin-onion.lib/RingDefinitionController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/RingDefinitionController.cs) | Apply, Remove, snapshot per template, prune |
| [bloomin-onion.lib/PaintedTextureReference.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/PaintedTextureReference.cs) | In-memory texture bound by the game's own `Bind` |
| [bloomin-onion.lib/RingBandPainter.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/RingBandPainter.cs) | Stripes to band and control strips |
| [bloomin-onion.lib/StockRingAssets.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/StockRingAssets.cs) | Stock fallbacks from the live system or known ids |

## Variations and gotchas

- **Private fields.** `_planetTransparenciesRenderer`, `_ringsRenderer`, `_ringRendererCreated`, `_anyRings`, `TextureAsset`'s backing field and `DistantSphereRenderer._material` are all private; the mod reports a clear error ("game update?") when one is missing.
- **The hitch** is two `WaitIdle` calls plus rebuilding every render target.
- **Frame unverified.** The authors' acceptance note says rebuilds must run before current-frame ImGui image references are emitted; the UI path is not checked for windows drawn earlier in the same ImGui frame (unverified in game).
- **Physics workers do not read `RingsReference`**, so the swap is main-thread data only.

## See also

- [Overview](overview.md); [approach 2](add-or-restyle-planetary-rings-2.md)
- [Bloomin' Onion runtime planetary rings](../../../../mods/unscience/features/bloomin-onion-runtime-planetary-rings.md), [Bloomin' Onion painted ring textures](../../../../mods/unscience/features/bloomin-onion-painted-ring-textures.md)
- [Change loaded game data and make the renderer notice](../../../celestials/atmospheres-and-rings/change-loaded-game-data-and-make-the-renderer-notice/overview.md)
