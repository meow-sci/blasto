---
type: How-To
title: Lay volumetric smoke trails with one PlumeTrailEmitterState cursor per source
description: Draw billowing smoke behind projectiles and flares through KSA's volumetric booster-trail renderer by keeping one PlumeTrailEmitterState per source and submitting a body-fixed position every frame, reaching the renderer through one reflected private field
category: rendering
group: lights-and-particles
topic: draw-exhaust-plumes-without-an-engine
approach: 5
difficulty: advanced
prerequisites: [starmap, reflection]
demonstrated_by: [mods/ksarmory/features/trail-renderer-smoke]
techniques: [volumetric-trail-emitter, reflection, starmap-lifecycle]
hooks:
  - starmap StarMapAfterGui
  - harmony-prefix KSA.Program.OnFrameCelestials
  - reflection KSA.Program._volumetricTrailRenderer
lifecycle: [frame:draw-ui, frame:celestials, frame:pre-render, render:atmosphere]
game_types:
  - KSA.Program
  - KSA.VolumetricTrailRenderer
  - KSA.PlumeTrailSegmentsManager
  - KSA.PlumeTrailEmitterTracker
  - KSA.PlumeTrailEmitterState
  - KSA.PlumeTrailEmitterFrame
  - KSA.Celestial
  - KSA.Universe
tags: [smoke, trail, plume-trail, volumetric, projectile, flare]
---
Outcome: a billowing white trail that hangs over the ground for minutes behind a burning missile, or a lingering plume behind a flare, drawn by the same raymarched volume KSA uses for solid-booster exhaust. The mod writes no renderer: `VolumetricTrailRenderer.SubmitEmitter` and `PlumeTrailEmitterState` are public, so it keeps one emitter state per source and submits a position every frame. The only obstacle is that `Program._volumetricTrailRenderer` is private with no accessor, so that one field is read by reflection.

Demonstrated by KSArmory ([Motor and flare smoke through KSA's volumetric booster-trail renderer](../../../../mods/ksarmory/features/trail-renderer-smoke.md)). Prerequisites: StarMap, one reflected field. Particle-based smoke is the alternative ([spawn particle effects](../spawn-particle-effects/overview.md)).

## Background: how the game does it

`Vehicle.UpdatePlumeTrailEmitters` is the stock caller: for each nozzle it builds a `PlumeTrailEmitterFrame` from the vehicle's body-fixed position and velocity and calls `SubmitEmitter(state, celestial, frame, offset, exhaustVelocityBody, nozzleRadius, expandedRadius, color, density, lifetime, isFlowing, isInsideAtmosphere)`; `isFlowing` is computed from a real nozzle's duty cycle and exhaust velocity ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L5682-L5716)). Passing your own `isFlowing` and `isInsideAtmosphere` is what sidesteps that gate. In `OnPreRender` the trail renderer prepares, the vehicles submit and the segments are finalized for the camera's nearby body:

```csharp
// current/decomp/KSA/Program.cs L2392-L2403
Profiler.MainThread.Begin(Profiler.Tag.VolumetricTrails);
_volumetricTrailRenderer.PrepareTrailSegments();
Profiler.MainThread.Begin(Profiler.Tag.TrailEmitters);
ReadOnlySpan<Vehicle> vehiclesInFrame3 = VehiclesInFrame;
for (int k = 0; k < vehiclesInFrame3.Length; k++)
{
	Vehicle vehicle3 = vehiclesInFrame3[k];
	vehicle3.UpdatePlumeTrailEmitters(_volumetricTrailRenderer);
}
Profiler.MainThread.End();
Celestial nearbyCelestial = GetCamera().NearbyCelestial;
_volumetricTrailRenderer.FinalizeTrailSegments(nearbyCelestial);
```

The tracker starts a segment at 1.65 nozzle radii and deactivates an emitter whose last submission was not the previous frame; segments not submitted this frame are closed at frame end ([current/decomp/KSA/PlumeTrailEmitterTracker.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlumeTrailEmitterTracker.cs#L46-L99), [current/decomp/KSA/PlumeTrailEmitterTracker.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlumeTrailEmitterTracker.cs#L179-L194)):

```csharp
// current/decomp/KSA/PlumeTrailEmitterTracker.cs L83-L92
bool flag3 = emitter.HasLastUpdatedFrame && Program.FrameNumber != emitter.LastUpdatedFrame + 1;
bool flag4 = emitter.Parent != null && emitter.Parent != parent;
if (flag3 || flag4)
{
	emitter.ClearGroupAssignment();
	emitter.Deactivate(frame.Time, refreshEndCreatedTime: false);
	emitter.HasPreviousFrame = false;
}
emitter.LastUpdatedFrame = Program.FrameNumber;
emitter.HasLastUpdatedFrame = true;
```

Segments are stored body-fixed, live 1200 s by default and advect through an altitude-sheared wind field. The engine grows a segment as an entraining jet on its release speed, so a segment laid at rest keeps its laid radius ("a glowing wire").

## Lifecycle and timing

| Value | Where | What runs |
|---|---|---|
| `frame:draw-ui` | `[StarMapAfterGui]` postfix on `OnDrawUiViewports` runs the mod's step; after the rounds are stepped, per system | the smoke code calls `Lay`, which submits straight into the tracker |
| `frame:celestials` | HUD hidden: the mod's prefix on `Program.OnFrameCelestials` runs the step instead | same submissions, still before the render |
| `frame:pre-render` | `OnPreRender`: `PrepareTrailSegments` (L2393), vehicles' `UpdatePlumeTrailEmitters` (L2399), `FinalizeTrailSegments(nearbyCelestial)` (L2403) | segment maintenance, stock emitters, grouping, upload LOD for the camera's nearby body |
| `render:atmosphere` | `RenderGame`: `_planetTransparenciesRenderer.Render` (L4875) or, with the atmosphere hidden and cloud upscaling on, `RenderVolumetricTrails` (L4880) | the raymarch that draws the segments |

Submitting during the UI pass works because `SubmitEmitter` writes samples into the segment store at call time, and `FrameNumber` is not incremented until after `PostRender` (L2332), so the end-of-frame check sees the mod's segments as submitted this frame. Positions are body-fixed and stamped with `Universe.GetElapsedSeconds()`, so being submitted earlier in the frame than the engine's own emitters does not move them. See rows 12, 13, 20 and 21 of [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence) and RenderGame row 12.

## Steps

1. **Reach the renderer once.** Read `Program._volumetricTrailRenderer` by reflection, warn once if it is gone, and degrade to particles. Everything after this is ordinary calls:

   ```csharp
// src/KSArmory/Ksa/PlumeSmoke.cs L114-L144
private static VolumetricTrailRenderer? Resolve()
{
    if (_looked) return _renderer;
    _looked = true;

    try
    {
        FieldInfo? field = typeof(Program).GetField(
            "_volumetricTrailRenderer", BindingFlags.NonPublic | BindingFlags.Instance);

        if (field is null)
        {
            Warn("KSA.Program has no _volumetricTrailRenderer field - it has been renamed or "
                 + "given an accessor. If it now has one, use it and delete this.");
            return null;
        }

        _renderer = field.GetValue(Program.Instance) as VolumetricTrailRenderer;

        if (_renderer is null) { Warn("_volumetricTrailRenderer is not yet built"); return null; }

        Tune(_renderer);
        Log.Info("volumetric smoke: the trail renderer is reachable");
    }
    catch (Exception e)
    {
        Warn($"reaching the trail renderer threw: {e.Message}");
    }

    return _renderer;
}
```

2. **Optionally tune the renderer's look fields.** They are the renderer's own fields and therefore global (the game's boosters change too): `ErosionMaxDepth` 0.8 to 0.68, `ErosionEdgeSharpness` 0.95 to 0.93, `SelfShadowStepCount` 4 to 8, `SkyAmbientBrightness` 3 to 4:

   ```csharp
// src/KSArmory/Ksa/PlumeSmoke.cs L152-L172
private static void Tune(VolumetricTrailRenderer renderer)
{
    // Noise eats up to 80% of the shape by default, which shreds an exhaust nicely and leaves a
    // cloud looking frayed. Rather less than that gives billows instead of tatters.
    //
    // But not much less, and the reason is mass rather than taste. A 0.3 kt surface burst lofts
    // about 90 t of soil, which at any concentration that reads as visible dust fills a few
    // hundred million cubic metres of air -- semi-transparently. Drawing the same volume opaque
    // is what makes a small device look like it made far more smoke than it could have. Erosion
    // is the only lever the renderer offers for that: there is no density or absorption field,
    // and trailColor's alpha is unused.
    renderer.ErosionMaxDepth = 0.68f;
    renderer.ErosionEdgeSharpness = 0.93f;

    // The self-shadow ray is only as long as the local radius, so more steps buys resolution
    // inside the billows rather than reach.
    renderer.SelfShadowStepCount = 8;

    // And lift the shadowed side, which is what stops a big volume reading as a silhouette.
    renderer.SkyAmbientBrightness = 4.0f;
}
```

3. **Keep one `PlumeTrailEmitterState` per source.** It is the cursor: move it and the engine draws a capsule from where it was to where it is:

   ```csharp
// src/KSArmory/Ksa/PlumeSmoke.cs L51-L54
public sealed class Strand
{
    internal readonly PlumeTrailEmitterState State = new();
}
```

4. **Lay a segment each frame, thrown so it swells.** Solve the throw speed that reaches the requested expanded radius in 5 s with the engine's entrainment coefficient (0.2) and the tracker's 1.65 nozzle-radius start, then submit with `isFlowing: true`:

   ```csharp
// src/KSArmory/Ksa/PlumeSmoke.cs L84-L110
public static void Lay(Strand strand, Celestial body, double3 positionCcf, double3 thrownCcf,
                       float initialRadius, float expandedRadius, float density = StockDensity)
{

    if (Resolve() is not { } renderer) return;
    if (!Vec.IsFinite(positionCcf)) return;

    float nozzle = initialRadius / LaidPerNozzleRadius;
    double swell = Math.Max(expandedRadius / initialRadius, 1.0);
    double speed = nozzle * ((swell * swell * swell) - 1.0) / (3.0 * Entrainment * SwellSeconds);

    double3 along = Vec.Unit(thrownCcf);
    if (Vec.Len2(along) < 0.5) along = Vec.Unit(positionCcf);

    var frame = new PlumeTrailEmitterFrame(positionCcf, double3.Zero, doubleQuat.Identity,
                                           double3.Zero, Universe.GetElapsedSeconds());
    try
    {
        renderer.SubmitEmitter(strand.State, body, in frame, double3.Zero, along * speed,
                               nozzle, expandedRadius, Colour, density, StockLifetimeSeconds,
                               isFlowing: true, isInsideAtmosphere: true);
    }
    catch (Exception e)
    {
        Warn($"submitting a segment threw: {e.Message}");
    }
}
```

5. **Compute the body-fixed position** from the source's drawn position, behind the nozzle, and wait until the round has travelled one body length so the trail does not start inside the launcher tube:

   ```csharp
// src/KSArmory/Ksa/MotorSmoke.cs L112-L149
double clearance = round.Munition.BodyLength;
if (Vec.Len2(round.TravelSinceLaunch) < clearance * clearance) return;

if (!system.TryRoundEffectEcl(round, out double3 ecl)) return;

// Behind the nozzle, not at the round's centre: smoke laid at the middle of the body reads
// as the missile dragging a column out of its own flank. The same half-length the plume
// steps back by, and for the same reason.
double3 along = Vec.Unit(round.VelocityLocal);
if (Vec.Len2(along) > 0.5) ecl -= along * (round.Munition.BodyLength * 0.5);

double3 positionCcf = (ecl - centre).Transform(body.GetCce2Ccf());
if (!Vec.IsFinite(positionCcf)) return;

if (!_laying.TryGetValue(round, out Live? live))
{
    live = new Live { Strand = new PlumeSmoke.Strand(), Owner = system };
    _laying[round] = live;
}
else
{
    live.Owner = system;
}

// Off the round's own size, so a 30 mm shell does not lay the column a HARM does.
//
// Small fractions, because BodyLength is a missile's *length* and the trail wants its
// calibre: a HARM is 4.17 m long and about a quarter of a metre across, so anything near
// unity here is a column tens of times wider than the round. The laid radius is roughly
// the body, and the expanded one is what makes a moving point read as a billowing trail
// rather than a wire -- reached within PlumeSmoke's 5 s swell, so it is what the trail
// looks like for almost all of its life rather than an eventual size.
double width = round.Munition.BodyLength * _config.MotorSmokeWidth;

float laid = (float)(width * 0.08);
float expanded = (float)(width * 0.7);

PlumeSmoke.Lay(live.Strand, body, positionCcf, (-along).Transform(body.GetCce2Ccf()), laid, expanded);
```

6. **Ending a trail needs no call.** Stop submitting; the tracker sees the frame gap and closes the segment, and what is already laid ages out on the engine's clock. Forget cursors for sources that stop burning, are reaped, or whose owner the roster no longer knows.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/PlumeSmoke.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/PlumeSmoke.cs) | `Lay`, `Resolve` (reflection, once) and `Tune` |
| [src/KSArmory/Ksa/MotorSmoke.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/MotorSmoke.cs) | Per-round strands, burning test, nozzle offset, CCF conversion, width |
| [src/KSArmory/Ksa/DecoyEffects.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/DecoyEffects.cs#L106-L112) | Flare smoke |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L870-L917) | Per-frame update after the rounds are stepped |

## Variations and gotchas

- **Reflection on a private field.** A rename returns null, logs one warning and the smoke stays unavailable for the session; nothing else breaks. Resolution is attempted once, so if the renderer is not built at the first call it stays unavailable.
- **Tuning is global.** There is no per-emitter override of the look fields; colour, density and lifetime are per-emitter arguments.
- **Never grouped.** Mod submissions in the UI pass are cleared from the grouping by `PrepareTrailSegments` and `ResetForFrame` before `FinalizeTrailSegments` builds groups, so mod strands skip the engine's multi-emitter merge.
- **Only the camera's nearby body is drawn.** `FinalizeTrailSegments` and the render pass take `GetCamera().NearbyCelestial`; smoke laid over another body is stored but not drawn.
- **Segment cap.** `PlumeSegmentStore.MAX_SEGMENTS_PER_PARENT` is 65536 in this build; lifetime is a `SubmitEmitter` argument (the stock caller passes `PlumeTrailTemplate.Lifetime`, default 1200 s).
- **Bypass is the point.** `isFlowing: true` is the whole bypass of the stock nozzle gate; a real motor part would push the launcher.

## See also

- [Overview](overview.md)
- [Approach 1](draw-exhaust-plumes-without-an-engine-1.md), [approach 4](draw-exhaust-plumes-without-an-engine-4.md)
- [Motor and flare smoke through KSA's volumetric booster-trail renderer](../../../../mods/ksarmory/features/trail-renderer-smoke.md)
- [Spawn particle effects and explosions from a mod](../spawn-particle-effects/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
