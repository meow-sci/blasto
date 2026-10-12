---
type: Mod Feature
title: Add planetary rings to any celestial at runtime
description: Bloomin' Onion builds a complete PlanetaryRingsReference tree in code, assigns it to a body template and forces the planetary rings renderer to be recreated so the ring appears without touching game data
mod: unscience
submod: bloomin-onion
category: celestials
techniques: [reflection, xml-asset, vulkan-buffer, imgui-window]
hooks:
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent; Apply / Remove buttons)
  - starmap StarMapBeforeGui (via unscience ISubmod.Update; body and asset refresh every 2 s)
  - reflection KSA.Program._planetTransparenciesRenderer
  - reflection KSA.PlanetTransparenciesRenderer._ringsRenderer
  - reflection KSA.PlanetTransparenciesRenderer._ringRendererCreated
  - reflection KSA.PlanetTransparenciesRenderer._anyRings
  - reflection KSA.StaticCelestial._distantRenderer
  - reflection KSA.DistantSphereRenderer._material (AccessTools.FieldRefAccess)
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
  - KSA.Celestial
  - KSA.PlanetTransparenciesRenderer
  - KSA.Rendering.Rings.Rendering.PlanetaryRingsRenderer
  - KSA.Rendering.Rings.Rendering.PlanetaryRingsRenderData
  - KSA.Program
  - KSA.StaticCelestial
  - KSA.DistantSphereRenderer
  - KSA.DistantSphereMaterialData
  - KSA.MathEx
tags: [rings, celestials, renderer-rebuild, reflection, vulkan, imgui]
---

KSA draws only the rings that celestial XML declares (Saturn's `<Rings>` block). Bloomin' Onion
lets a player author every value that block can hold (geometry, band texture, volumetric dust,
instanced rock field and its material) in an ImGui panel and put the result on any moon or
planet. The ring appears after a short hitch; no game file is changed, and Remove restores
whatever the body had before.[^bloomin-readme]

Mechanism in one line: build the same `PlanetaryRingsReference` object tree the XML loader would
produce, assign it to `celestial.BodyTemplate.RingsReference`, then dispose the game's
`PlanetaryRingsRenderer`, refresh the transparencies renderer's body list and its private
`_anyRings` flag, and call `Program.RebuildRenderer()` so the renderer is constructed again from
the new data.

## How it works

1. **Validate, then resolve every asset first.**
   `RingReferenceBuilder.Validate` checks the numbers (radii, LOD count up to the game's 5, positive
   sizes, the inner radius outside the body, a parent body when the ecliptic frame is used).
   `Build` then resolves the band and control textures, the rock material textures and one mesh per
   LOD before it constructs anything. A failed lookup returns an error string and the game is left
   untouched. Empty asset ids fall back to stock Saturn assets found on any ringed body in the
   current system, or by their known ids (`SaturnRings`, `RingRock_LOD0..4`, the Luna rock atlas).
   See [bloomin-onion.lib/RingReferenceBuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/RingReferenceBuilder.cs#L31-L64)
   and [bloomin-onion.lib/StockRingAssets.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/StockRingAssets.cs#L41-L66).[^ring-reference-builder][^stock-ring-assets]
2. **Construct the tree.** Every class the XML loader would create is built directly with object
   initializers: `PlanetaryRingsReference`, `PlanetaryRingsVolumeReference`,
   `RingRaymarchingStepReference`, `RingObjectsReference`, one `RingLodReference` plus
   `MeshFileReference { Mesh = ... }` per LOD, and a `PbrMaterialReference`. Angles are passed
   through `MathEx.ToDeviationAngle` / `ToCompassAngle`, exactly what
   `PlanetaryRingsReference.OnDataLoad` does to XML values.[^ksa-rings-reference] Freshly constructed
   `MeshFileReference` / `TextureReference` objects resolve `Get()` to themselves because the private
   `FileReference._isReference` stays false unless `OnDataLoad` sets it, so the renderer can
   dereference them like loaded data.[^ksa-file-reference]
3. **Assign and snapshot.** `RingDefinitionController.Apply` records the template's original
   `RingsReference` once per `CelestialTemplate` (null for most bodies, the stock ring for Saturn),
   assigns the new tree, and reverts the assignment if the rebuild fails.[^ring-definition-controller]
4. **Rebuild the ring renderer** with `RingRendererRebuilder.Rebuild` (snippet below):
   `Device.WaitIdle()`, dispose the existing `PlanetaryRingsRenderer` and clear
   `_ringRendererCreated`, call the public `PlanetTransparenciesRenderer.PopulatePlanets()` and write
   its result into the private `_anyRings`, then call `Program.RebuildRenderer()`.[^ring-renderer-rebuilder]
5. **Sync the far-away sphere** and free stale assets: `SyncDistantSphereShadow` rewrites the ring
   fields of the body's `DistantSphereRenderer._material`, then `PruneUnusedAssets` releases painted
   textures and converted meshes that no applied ring references any more.
6. **Inverse mapping.** `RingDefinitionSerializer.FromReference` turns an existing
   `PlanetaryRingsReference` (Saturn's) back into an editable definition for "Copy ring".[^ring-definition-serializer]

Why each rebuild step is needed (verified in build 5554):

| Step | Reason in the game code |
|---|---|
| Dispose the rings renderer | `PlanetTransparenciesRenderer.RebuildFrameResources` only calls `_ringsRenderer.RebuildFrameResources` when `_ringRendererCreated` is true. That destroys and rebuilds pipelines and frame images but never re-reads ring data, because `PlanetaryRingsRenderer.PopulatePlanets` runs only from its constructor.[^ksa-planet-transparencies][^ksa-rings-renderer] |
| Recreate instead of re-calling `PlanetaryRingsRenderer.PopulatePlanets` | That method is public but never clears `PlanetToRingsRenderData`, and the mesh culling buffers are sized from `_maxMeshCount` in the constructor (`PopulatePlanets` runs before `BuildFrameResources`).[^ksa-rings-renderer] |
| `PlanetTransparenciesRenderer.PopulatePlanets()` | Rebuilds `_planetsWithTransparencies` (bodies with an atmosphere or a ring) and the sort arrays; otherwise a newly ringed body is never drawn by the transparencies pass.[^ksa-planet-transparencies] |
| Write `_anyRings` | `RebuildFrameResources` only takes the `CreateRingsRenderer` branch when `_anyRings` is true, and the field is otherwise set only in the constructor. In a system without stock rings this is the switch that creates a rings renderer at all; after removing the last ring it lets the rebuild end with none.[^ksa-planet-transparencies] |
| `Program.RebuildRenderer()` | Runs the create branch with fresh render targets. The new `PlanetaryRingsRenderData` constructor reads the whole tree and bakes it into UBOs, descriptor sets and instance buffers.[^ksa-rings-render-data] |

What is read when:

| Consumer | When it reads `RingsReference` | Effect of a template swap |
|---|---|---|
| `PlanetaryRingsRenderData` constructor | Only at rings-renderer construction | Needs the rebuild above |
| `PlanetaryRingsRenderData.UpdateData` | Every frame, CPU-samples `ControlTexture.Get().TextureAsset` | Control strip must stay CPU-readable |
| `PlanetRenderer.Render` (ring shadow on the nearby planet) | Every frame | Follows immediately[^ksa-planet-renderer] |
| `AtmosphereRenderer.FillPlanetAtmosphereData` (atmospheric ring shadow) | Every frame for each `AtmosphericBody` | Follows immediately; dereferences `Texture` unconditionally[^ksa-atmosphere-renderer] |
| `DistantSphereRenderer` constructor | Once, when the `StaticCelestial` is created | Needs `SyncDistantSphereShadow`[^ksa-distant-sphere] |

## Lifecycle and timing

| Path | Where it runs | Phase |
|---|---|---|
| Apply / Remove / Remove All buttons | `BloominOnionSubmod.RenderContent`, called by unscience from `[StarMapAfterGui]` (Harmony postfix of `Program.OnDrawUiViewports`) | `frame:draw-ui` |
| Body list, catalog and stock-asset refresh | `ISubmod.Update`, throttled to every 2 s, from `[StarMapBeforeGui]` or the hidden-HUD replay | `frame:draw-ui` |
| Save restore (`RestoreRings` / `ResetForSaveLoad`) | Replayed world load at the `PhysicsFrameHook` handoff inside `Program.PrepareFrame` | `frame:prepare` |
| Ring band and volumetrics drawn | `PlanetTransparenciesRenderer.Render` in `RenderGame` (`Program.cs` L4875) | `render:atmosphere` |
| Rock field drawn | `RenderRingMeshes` (`Program.cs` L4831), culled by `UpdateRingMeshes` (L4771) | `render:scene-opaque` |

**The rebuild is a direct, synchronous call from inside the ImGui frame.** The mod does not set
`Program.RendererRebuildNeeded`; it calls `Program.RebuildRenderer()` itself. In build 5554 the
game's own graphics-settings Apply button only calls `Program.ScheduleRendererRebuild()`, which
sets the flag. The flag is consumed by `Program.Render` the same frame (it rebuilds and skips
rendering that frame, L2445-L2450), or at the start of `PrepareFrame` (L2143-L2147), or in
`PostRender` when the submit asks for a resize or the flag is still set (L2351-L2358).[^ksa-program] Bloomin' Onion and Rocky McRock Face share the game's
rebuild routine but not its timing:

- At the moment the button handler runs, `OnFrame` has passed `PrepareFrame`, `PrepareImGui` and
  the viewport and UI phases but has not reached `ImGui.Render` (L2286), `OnPreRender`'s
  `TryAcquireNextFrame` (L2420) or `RenderGame`. No swapchain image is acquired and no command
  buffer for this frame has been recorded.
- GPU work still in flight from earlier frames (up to `MaxFramesInFlight`) may reference the ring
  pipelines, buffers and descriptor sets. `RingRendererRebuilder` therefore calls
  `Program.GetRenderer().Device.WaitIdle()` before `PlanetaryRingsRenderer.Dispose()`, and
  `RebuildRenderer` itself calls `_renderer.Device.WaitIdle()` before disposing render targets
  (L5112) and again as its last statement (L5210). When `Rebuild` returns, no GPU work references
  any old ring resource, which is what makes the following prune of painted textures and mesh
  clones safe.
- `RebuildRenderer` also rebinds the ImGui backend's main render pass (L5116) and sets
  `RendererRebuildNeeded = false` (L5208); the rest of the frame then records against the new
  resources.
- Why not schedule instead: a deferred rebuild would run after the handler returns, so the
  controller could not revert the template on failure or prune assets that only become
  unreferenced once the new render data exists.
- The authors' own acceptance note for the save path says rebuilds must run outside an acquired
  frame and before current-frame ImGui image references are emitted. The UI path satisfies the
  first condition; the second is not checked for windows drawn earlier in the same ImGui frame
  (unverified in game).[^scope-rings]
- The save path avoids the question: native loads are replayed at the `PhysicsFrameHook` handoff in
  `PrepareFrame`, before `PrepareImGui`, so the restore-time rebuild runs outside any ImGui frame.

The template swap itself is main-thread only data: physics workers do not read
`BodyTemplate.RingsReference`, and the renderer reads it on the main thread while recording, so a
swap at `frame:draw-ui` is seen by this frame's render. The hitch the README warns about is the two
`WaitIdle` calls plus rebuilding every render target in `RebuildRenderer`.

## Key code

| Path | Role |
|---|---|
| [bloomin-onion.lib/RingReferenceBuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/RingReferenceBuilder.cs) | Validation, asset resolution and reference-tree construction |
| [bloomin-onion.lib/RingRendererRebuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/RingRendererRebuilder.cs) | Dispose, repopulate, `_anyRings`, `RebuildRenderer`, distant-sphere sync |
| [bloomin-onion.lib/RingDefinitionController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/RingDefinitionController.cs) | Apply / Remove, original snapshot per template, rebind after reload, prune |
| [bloomin-onion.lib/RingDefinition.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/RingDefinition.cs) | Editable model with Saturn defaults |
| [bloomin-onion.lib/StockRingAssets.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/StockRingAssets.cs) | Stock fallbacks from the live system or known ids |
| [bloomin-onion.lib/RingDefinitionSerializer.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/RingDefinitionSerializer.cs#L122-L168) | `FromReference`: game tree back to an editable definition |
| [bloomin-onion.lib/BloominOnionSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/BloominOnionSubmod.cs#L46-L67) | Throttled refresh in `Update` |
| [bloomin-onion.lib/BloominOnionSubmod.Ui.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/BloominOnionSubmod.Ui.cs#L194-L224) | Apply / Remove buttons |
| [bloomin-onion.lib/BloominOnionSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/BloominOnionSubmod.Saves.cs) | Save participant that re-applies rings after a load |

## Snippets

The whole rebuild, run synchronously from the Apply button:

```csharp
// bloomin-onion.lib/RingRendererRebuilder.cs L28-L62
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
```

The game branch the rebuild depends on (build 5554):

```csharp
// current/decomp/KSA/PlanetTransparenciesRenderer.cs L398-L412
		if (GameSettings.ShowRings())
		{
			if (_ringRendererCreated)
			{
				_ringsRenderer.RebuildFrameResources(_target, transparenciesColor, transparenciesTransmittance);
			}
			else if (_anyRings)
			{
				CreateRingsRenderer(_textureSystem, transparenciesColor, transparenciesTransmittance);
			}
		}
		else
		{
			DisposeRingRenderer();
		}
```

Constructing the tree the XML loader would have produced:

```csharp
// bloomin-onion.lib/RingReferenceBuilder.cs L66-L98
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
            // ...
```

Refreshing the ring fields the distant-sphere renderer baked at construction:

```csharp
// bloomin-onion.lib/RingRendererRebuilder.cs L87-L110
    public static void SyncDistantSphereShadow(Celestial celestial)
    {
        try
        {
            if (GetFieldFromHierarchy(celestial, "_distantRenderer") is not DistantSphereRenderer distant) return;
            _distantMaterial ??= AccessTools.FieldRefAccess<DistantSphereRenderer, DistantSphereMaterialData>("_material");
            ref DistantSphereMaterialData material = ref _distantMaterial(distant);

            var rings = celestial.BodyTemplate?.RingsReference;
            material.UseRingShadows = rings != null ? 1 : 0;
            if (rings == null)
            {
                material.RingTextureId = 0; // Matches a renderer constructed without rings.
                return;
            }
            material.RingInnerRadius = (float)rings.InnerRadius.InMeters();
            material.RingOuterRadius = (float)rings.OuterRadius.InMeters();
            material.RingTextureId = rings.Texture.Get().BindlessHandle;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"bloomin-onion: distant sphere ring shadow sync skipped for {celestial.Id}: {ex.Message}");
        }
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `AstronomicalTemplate.RingsReference` (public field) via `Celestial.BodyTemplate` | [current/decomp/KSA/AstronomicalTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AstronomicalTemplate.cs#L66) | The slot that is written and snapshotted |
| `PlanetaryRingsReference` and its child reference classes | [current/decomp/KSA/PlanetaryRingsReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetaryRingsReference.cs#L5-L61) | Constructed in code; `OnDataLoad` shows the angle normalization being mirrored |
| `DistanceReference.IsValid()` | [current/decomp/KSA/DistanceReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DistanceReference.cs#L162-L165) | Requires more than 100 km, which is why `rings.IsValid()` is not used as a gate |
| `FileReference.IsReference()` / `_isReference` | [current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L39-L60) | Default false, so constructed references resolve `Get()` to themselves |
| `Program._planetTransparenciesRenderer` (private) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L177) | Reflected to reach the rings renderer |
| `Program.RebuildRenderer(bool)` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5105-L5211) | Full renderer rebuild with `WaitIdle` before and after |
| `Program.ScheduleRendererRebuild()` / `RendererRebuildNeeded` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5248-L5251) | The deferred path the game's settings use (not used by the mod) |
| `PlanetTransparenciesRenderer._ringsRenderer`, `_ringRendererCreated`, `_anyRings` (private) | [current/decomp/KSA/PlanetTransparenciesRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetTransparenciesRenderer.cs#L41-L71) | Disposed, cleared and set through reflection |
| `PlanetTransparenciesRenderer.PopulatePlanets()` (public) | [current/decomp/KSA/PlanetTransparenciesRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetTransparenciesRenderer.cs#L153-L174) | Rebuilds the list of bodies with rings or atmospheres |
| `PlanetTransparenciesRenderer.RebuildFrameResources` | [current/decomp/KSA/PlanetTransparenciesRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetTransparenciesRenderer.cs#L374-L413) | The create branch the dispose step forces |
| `PlanetaryRingsRenderer` constructor, `PopulatePlanets`, `Dispose` | [current/decomp/KSA.Rendering.Rings.Rendering/PlanetaryRingsRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Rings.Rendering/PlanetaryRingsRenderer.cs#L161-L171) | Constructor-only data population; `Dispose` (L473) frees per-body render data |
| `PlanetaryRingsRenderData` constructor and `UpdateData` | [current/decomp/KSA.Rendering.Rings.Rendering/PlanetaryRingsRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Rings.Rendering/PlanetaryRingsRenderData.cs#L180-L307) | What is baked at construction; `UpdateData` (L72) samples the control strip per frame |
| `StaticCelestial._distantRenderer` (private) | [current/decomp/KSA/StaticCelestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticCelestial.cs#L8) | Found by walking the type hierarchy |
| `DistantSphereRenderer._material` (private `DistantSphereMaterialData`) | [current/decomp/KSA/DistantSphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DistantSphereRenderer.cs#L110-L118) | Ring fields baked in the constructor; uploaded every frame in `Render` (L160) |
| `PlanetRenderer.Render`, `AtmosphereRenderer.FillPlanetAtmosphereData` | [current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1154-L1162) | Per-frame readers that need no sync (`PlanetRenderer.cs` L1991-L1999) |
| `GameSettings.ShowRings()` / `ShowRingMeshes()` | [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs) | Panel warns when rings or ring meshes are off |

## Pitfalls and version notes

- `rings.IsValid()` cannot be used as a sanity check: `DistanceReference.IsValid()` demands more
  than 100 km, which the stock 10 m rock size, 1 km field thickness and 20 km draw distance fail.
  The game never calls it on ring data at load either; the builder's own `Validate` is the gate.
- The atmosphere pass dereferences `RingsReference.Texture.Get()` every frame for atmospheric
  bodies, so a reference tree with a null band texture throws every frame. The builder always sets
  `Texture`.
- `_anyRings` and the other private fields are found by name. A rename makes `SetFieldValue` a
  no-op: in a system without stock rings Apply then "succeeds" but nothing renders. A missing
  `_planetTransparenciesRenderer` is reported as an error instead.
- `DistantSphereRenderer` moved its ring fields from the push-constant struct `_data` into the
  private `_material` struct in an earlier build; the old reflection silently did nothing. The typed
  `AccessTools.FieldRefAccess` now throws on a rename and the error is logged and skipped. All of
  the members above are present with the same shape in build 5554.
- A body without a ring keeps `RingTextureId = 0` after removal so the far sphere never samples a
  freed painted band. Rocky McRock Face does not run this sync after a band swap.
- Density times draw distance cubed sizes the GPU instance buffers built in the render-data
  constructor; very dense rings cost VRAM and frame time after the rebuild.
- The ecliptic frame calls `celestial.Parent.GetCce2Cci()` in both the render-data constructor and
  `ComputeRingNormal`, so it is refused for a body without a parent.
- Rings stay on the body template for the session. After a reload the `Celestial` objects are new
  but the templates survive; `RebindBodies` re-points applied entries by id and drops entries whose
  template no longer carries the mod's reference.

## See also

- [Painted ring band textures](./bloomin-onion-painted-ring-textures.md) (the runtime band and control strips)
- [Rocky McRock Face ring asset swap](./rocky-mcrock-face-ring-asset-swap.md) (the same rebuild on existing rings)
- [Ring asset catalog](./rocky-mcrock-face-asset-catalog.md) and [interleaved mesh clone](./rocky-mcrock-face-interleaved-mesh-clone.md) (LOD mesh sources)
- [Kiwi's Marbles celestial welding](./kiwis-marbles-celestial-welding.md) (weld a ringed moon to a vessel)
- [Physics frame hook](./lib-physics-frame-hook.md) (where save-time rebuilds run)
- [Game state providers](./lib-game-state-providers.md) (`CelestialProvider`, `ReflectionHelpers`)
- [Bloomin' Onion sub-mod](../submods/bloomin-onion.md) and [mod overview](../overview.md)
- [Build the ring reference tree, assign it to BodyTemplate.RingsReference, dispose and recreate the rings renderer](../../../howto/celestials/atmospheres-and-rings/add-or-restyle-planetary-rings/add-or-restyle-planetary-rings-1.md) - how-to article demonstrating this feature
- [Assign to the template and call Program.RebuildRenderer](../../../howto/celestials/atmospheres-and-rings/change-loaded-game-data-and-make-the-renderer-notice/change-loaded-game-data-and-make-the-renderer-notice-3.md) - how-to article demonstrating this feature

[^bloomin-readme]: bloomin-onion/README.md
[^ring-reference-builder]: bloomin-onion.lib/RingReferenceBuilder.cs
[^stock-ring-assets]: bloomin-onion.lib/StockRingAssets.cs
[^ksa-rings-reference]: KSA.PlanetaryRingsReference (decompiled, build 2026.10.10.5554)
[^ksa-file-reference]: KSA.FileReference (decompiled, build 2026.10.10.5554)
[^ring-definition-controller]: bloomin-onion.lib/RingDefinitionController.cs
[^ring-renderer-rebuilder]: bloomin-onion.lib/RingRendererRebuilder.cs
[^ring-definition-serializer]: bloomin-onion.lib/RingDefinitionSerializer.cs
[^ksa-planet-transparencies]: KSA.PlanetTransparenciesRenderer (decompiled, build 2026.10.10.5554)
[^ksa-rings-renderer]: KSA.Rendering.Rings.Rendering.PlanetaryRingsRenderer (decompiled, build 2026.10.10.5554)
[^ksa-rings-render-data]: KSA.Rendering.Rings.Rendering.PlanetaryRingsRenderData (decompiled, build 2026.10.10.5554)
[^ksa-planet-renderer]: KSA.PlanetRenderer (decompiled, build 2026.10.10.5554)
[^ksa-atmosphere-renderer]: KSA.AtmosphereRenderer (decompiled, build 2026.10.10.5554)
[^ksa-distant-sphere]: KSA.DistantSphereRenderer (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^scope-rings]: scope/rings.md (authors' ring integration analysis)
