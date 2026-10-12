---
type: Mod Feature
title: Motor and flare smoke through KSA's volumetric booster-trail renderer
description: Burning rounds and flares lay billowing body-fixed smoke by submitting one PlumeTrailEmitterState cursor each per frame to the engine's own VolumetricTrailRenderer, reached through one reflected private field, with each segment thrown back along the trail so it swells
mod: ksarmory
category: rendering
techniques:
  - reflection
  - volumetric-trail-emitter
  - body-fixed-frame
  - engine-renderer-reuse
  - global-renderer-tuning
hooks:
  - starmap StarMapAfterGui (via StepOnce)
  - harmony-prefix KSA.Program.OnFrameCelestials (via StepOnce, HUD hidden)
  - reflection KSA.Program._volumetricTrailRenderer
lifecycle: [frame:draw-ui, frame:celestials, frame:pre-render, render:atmosphere]
game_types:
  - KSA.Program
  - KSA.VolumetricTrailRenderer
  - KSA.PlumeTrailSegmentsManager
  - KSA.PlumeTrailEmitterTracker
  - KSA.PlumeEmitterGrouping
  - KSA.PlumeTrailEmitterState
  - KSA.PlumeTrailEmitterFrame
  - KSA.PlumeSegmentStore
  - KSA.PlumeTrailSettings
  - KSA.PlumeTrailTemplate
  - KSA.Vehicle
  - KSA.Celestial
  - KSA.IParentBody
  - KSA.Universe
tags: [smoke, volumetric, trails, plume, reflection, body-fixed, raymarch, countermeasures]
---

A burning missile leaves a billowing white trail that hangs over the ground for minutes, and a
flare leaves a lingering magnesium-oxide plume, both drawn by the same raymarched volume KSA uses
for solid-booster exhaust.[^ksarmory-motorsmoke][^ksarmory-decoyeffects] The mod writes no
renderer: `VolumetricTrailRenderer.SubmitEmitter` and `PlumeTrailEmitterState` are public, so it
keeps one emitter state per round and submits a position every frame. The only obstacle is that
`Program._volumetricTrailRenderer` is a private field with no accessor, so that one field is read
by reflection.[^ksarmory-plumesmoke][^ksa-program]

## How it works

| Piece | Role |
|---|---|
| `PlumeSmoke` (Ksa) | Resolves the renderer once by reflection, tunes four of its global look fields, and lays one segment per `Strand` per call[^ksarmory-plumesmoke] |
| `PlumeSmoke.Strand` | Wraps a `PlumeTrailEmitterState`: the cursor the engine draws a capsule from where it was to where it is[^ksarmory-plumesmoke] |
| `MotorSmoke` (Ksa) | Keeps a `Strand` per burning round, lays behind the nozzle, forgets rounds that stop burning, are reaped, or whose system the roster no longer knows[^ksarmory-motorsmoke] |
| `DecoyEffects` (Ksa) | Same call for each flare, thrown along the flare's airflow[^ksarmory-decoyeffects] |
| `Config.MotorSmoke`, `MotorSmokeWidth` | The player's switch and a width multiplier (UiSession "Rocket smoke trail", "Smoke width")[^ksarmory-config] |

### Laying one segment (`PlumeSmoke.Lay`)

1. The position is **body-fixed** (CCF). `MotorSmoke` takes the round's drawn position from
   `IEffectSource.TryRoundEffectEcl` (the call that places the round's body), steps it back half a
   body length along the round's local velocity so the smoke leaves the nozzle, subtracts the
   body's centre and rotates by `Celestial.GetCce2Ccf()`.[^ksarmory-motorsmoke][^ksarmory-weaponsystem]
2. No smoke until the round has travelled one body length, so the trail does not start inside the
   launcher tube.[^ksarmory-motorsmoke]
3. A `PlumeTrailEmitterFrame` is built with that position, zero body velocity, identity rotation and
   `Universe.GetElapsedSeconds()` as time.[^ksarmory-plumesmoke][^ksa-plumetrailemitterframe]
4. **Throwing makes it swell.** The engine grows a segment as an entraining jet on its release
   speed, so a segment laid at rest keeps its laid radius. `Lay` solves the throw speed that reaches
   the requested expanded radius in 5 s with the engine's entrainment coefficient (0.2) and the
   tracker's 1.65 nozzle-radius start, and throws it back along the trail.[^ksarmory-plumesmoke][^ksa-plumetrailsettings][^ksa-plumetrailemittertracker]
5. `SubmitEmitter(state, body, frame, offset 0, along * speed, nozzle, expanded, white, density,
   1200 s, isFlowing: true, isInsideAtmosphere: true)`. Passing `isFlowing` and
   `isInsideAtmosphere` itself is what sidesteps the stock gate: the engine's own caller computes
   `isFlowing` from `DutyCycle > 0` and `ExhaustVelocity > 0` on a real nozzle (Vehicle.cs L5713).[^ksarmory-plumesmoke][^ksa-vehicle]

Width comes from the round's `BodyLength` times `MotorSmokeWidth`: laid at 0.08 of that and
expanded to 0.7, so a 30 mm shell and a 4 m missile lay different columns.[^ksarmory-motorsmoke]

**Ending a trail needs no call.** The tracker compares `Program.FrameNumber` with the state's
`LastUpdatedFrame`; a gap deactivates the emitter, and `CloseUnsubmittedSegments` closes any open
segment not submitted this frame (PlumeTrailEmitterTracker.cs L83-L91, L179-L193). So `MotorSmoke`
simply stops submitting for a round, and what is already laid ages out on the engine's clock.[^ksa-plumetrailemittertracker][^ksarmory-motorsmoke]

**Global tuning.** On first resolve `PlumeSmoke.Tune` sets `ErosionMaxDepth = 0.68`,
`ErosionEdgeSharpness = 0.93`, `SelfShadowStepCount = 8`, `SkyAmbientBrightness = 4.0` on the
renderer (stock 0.8, 0.95, 4, 3). These are fields of the one shared renderer, so the game's own
booster trails change too.[^ksarmory-plumesmoke][^ksa-volumetrictrailrenderer]

## Lifecycle and timing

| Value | Where (build 5554) | What runs |
|---|---|---|
| `frame:draw-ui` | `[StarMapAfterGui]` postfix on `OnDrawUiViewports` (Program.cs L2268) runs `StepOnce` then `StepSimulation`; after the rounds are stepped, `_smoke.Update` per system and per loose system (KSArmoryMod.cs L870-L917) | `MotorSmoke.Update` and `DecoyEffects` call `PlumeSmoke.Lay`, which submits straight into the tracker[^ksarmory-mod][^ksa-program] |
| `frame:celestials` | HUD hidden: the mod's prefix on `Program.OnFrameCelestials` (L2320) runs `StepOnce` instead | Same submissions, still before the render |
| `frame:pre-render` | `OnPreRender` (L2323): `PrepareTrailSegments` (L2393), vehicles' `UpdatePlumeTrailEmitters` (L2399), `FinalizeTrailSegments(nearbyCelestial)` (L2403) | Segment maintenance, stock emitters, grouping, upload LOD for the camera's nearby body[^ksa-program] |
| `render:atmosphere` | `RenderGame`: `_planetTransparenciesRenderer.Render` (L4875) or, with the atmosphere hidden and cloud upscaling on, `RenderVolumetricTrails` (L4880), both for `GetCamera().NearbyCelestial` | The raymarch that draws the segments[^ksa-program] |

Submitting during the UI pass works because `SubmitEmitter` writes samples into the segment store
at call time (`SubmitSample` -> `emitter.Update`), and `FrameNumber` is not incremented until
after `PostRender` (L2332), so the end-of-frame check in `CloseUnsubmittedSegments` sees the mod's
segments as submitted this frame.[^ksa-plumetrailemittertracker][^ksa-program] Positions are
body-fixed and stamped with `Universe.GetElapsedSeconds()`, so being submitted earlier in the frame
than the engine's own emitters does not move them.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/PlumeSmoke.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/PlumeSmoke.cs#L84-L110) | `Lay`: throw speed, frame, `SubmitEmitter` |
| [src/KSArmory/Ksa/PlumeSmoke.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/PlumeSmoke.cs#L114-L172) | `Resolve` (reflection, once) and `Tune` |
| [src/KSArmory/Ksa/MotorSmoke.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/MotorSmoke.cs#L43-L150) | Per-round strands, burning test, nozzle offset, CCF conversion, width |
| [src/KSArmory/Ksa/DecoyEffects.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/DecoyEffects.cs#L106-L112) | Flare smoke |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L870-L917) | Per-frame update after the rounds are stepped, including loose systems |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L104-L126) | `EffectBody` and `TryRoundEffectEcl` |
| [src/KSArmory/Sim/Config.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Config.cs#L61-L83) | `MotorSmoke`, `MotorSmokeWidth` |

## Snippets

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

```csharp
// src/KSArmory/Ksa/PlumeSmoke.cs L114-L136
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
```

```csharp
// src/KSArmory/Ksa/MotorSmoke.cs L112-L149
        double clearance = round.Munition.BodyLength;
        if (Vec.Len2(round.TravelSinceLaunch) < clearance * clearance) return;

        if (!system.TryRoundEffectEcl(round, out double3 ecl)) return;
        // ...
        double3 along = Vec.Unit(round.VelocityLocal);
        if (Vec.Len2(along) > 0.5) ecl -= along * (round.Munition.BodyLength * 0.5);

        double3 positionCcf = (ecl - centre).Transform(body.GetCce2Ccf());
        if (!Vec.IsFinite(positionCcf)) return;
        // ...
        double width = round.Munition.BodyLength * _config.MotorSmokeWidth;

        float laid = (float)(width * 0.08);
        float expanded = (float)(width * 0.7);

        PlumeSmoke.Lay(live.Strand, body, positionCcf, (-along).Transform(body.GetCce2Ccf()), laid, expanded);
```

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

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `Program._volumetricTrailRenderer` (private field), `OnPreRender`, `RenderGame`, `FrameNumber` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2392-L2403) | Reflected once (field L185); frame order the submission relies on |
| `VolumetricTrailRenderer.SubmitEmitter`, `PrepareTrailSegments`, `FinalizeTrailSegments`, `ErosionMaxDepth`, `ErosionEdgeSharpness`, `SelfShadowStepCount`, `SkyAmbientBrightness` | [current/decomp/KSA/VolumetricTrailRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricTrailRenderer.cs#L199-L270) | Submission and global look fields |
| `PlumeTrailSegmentsManager.BeginFrame` (`ResetForFrame`, maintenance), `EndFrame` | [current/decomp/KSA/PlumeTrailSegmentsManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlumeTrailSegmentsManager.cs#L40-L55) | Per-frame lifecycle around the submissions |
| `PlumeTrailEmitterTracker.SubmitEmitter`, `FinalizeFrame`, `CloseUnsubmittedSegments` | [current/decomp/KSA/PlumeTrailEmitterTracker.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlumeTrailEmitterTracker.cs#L46-L193) | 1.65 nozzle-radius start, frame-gap deactivation, closing unsubmitted segments |
| `PlumeEmitterGrouping.ResetForFrame` | [current/decomp/KSA/PlumeEmitterGrouping.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlumeEmitterGrouping.cs#L46-L60) | Clears grouping points, including the mod's |
| `PlumeTrailEmitterState`, `PlumeTrailEmitterFrame` | [current/decomp/KSA/PlumeTrailEmitterFrame.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlumeTrailEmitterFrame.cs) | Public cursor state and the per-sample frame |
| `PlumeTrailSettings.ExhaustEntrainmentCoefficient` | [current/decomp/KSA/PlumeTrailSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlumeTrailSettings.cs#L49) | 0.2, mirrored as `Entrainment` |
| `PlumeSegmentStore.MAX_SEGMENTS_PER_PARENT` | [current/decomp/KSA/PlumeSegmentStore.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlumeSegmentStore.cs#L10) | Per-body segment cap (65536 in 5554) |
| `PlumeTrailTemplate.Lifetime` | [current/decomp/KSA/PlumeTrailTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlumeTrailTemplate.cs#L20) | Stock default 1200 s, which the mod passes per submit |
| `Vehicle.UpdatePlumeTrailEmitters` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L5682-L5713) | The stock caller and its `DutyCycle` gate |

## Pitfalls and version notes

- **Reflection on a private field.** A rename returns null, logs one warning ("falling back to
  particles"), and `PlumeSmoke.Available` stays false for the session; nothing else breaks. Resolution
  is attempted once, so if `Program.Instance`'s renderer is not built yet at the first call it stays
  unavailable.[^ksarmory-plumesmoke]
- **Tuning is global.** The four look fields are shared with every booster trail in the game; there
  is no per-emitter override, which the mod accepts as the trade.[^ksarmory-plumesmoke][^ksa-volumetrictrailrenderer]
- **Mod emitters are never grouped (checked against 5554).** The mod submits in the UI pass;
  `PrepareTrailSegments` -> `BeginFrame` -> `ResetForFrame` then clears the grouping points before
  `FinalizeTrailSegments` builds groups, so mod strands skip the engine's multi-emitter merge. The
  author documents the same ordering in `docs/NUCLEAR-EFFECT.md` against an older build.[^ksa-plumetrailsegmentsmanager][^ksa-plumeemittergrouping][^ksarmory-nuclear-effect-doc]
- **Only the camera's nearby body is drawn.** `FinalizeTrailSegments` and the render pass take
  `GetCamera().NearbyCelestial`, so smoke laid over another body is stored but not drawn.[^ksa-program]
- **Comment drift.** `PlumeSmoke`'s summary and `Config.MotorSmoke` say segments cap at 16,384 per body
  and that lifetime is a global engine setting. In 5554 `MAX_SEGMENTS_PER_PARENT` is 65536 and lifetime
  is a `SubmitEmitter` argument (the stock caller passes `PlumeTrailTemplate.Lifetime`, default 1200 s);
  the same summary also says per-emitter lifetime is possible.[^ksarmory-config][^ksa-plumesegmentstore][^ksa-plumetrailtemplate][^ksa-vehicle]
- **`MotorSmoke`'s summary is stale on one point**: it says the mushroom clouds use the same renderer;
  the repo's `CLAUDE.md` file table says the cloud left it for the mod's own raymarch compute
  pass.[^ksarmory-motorsmoke][^ksarmory-claude-md]
- **The mod's `isFlowing: true` is the whole bypass** of the stock nozzle gate; the author's analysis of
  why a real motor part would not work (it would push the launcher) is in `docs/BLOCKED-ON-KSA.md`.[^ksarmory-blocked-doc]

## See also

- [Pooled particle emitters](./pooled-particle-emitters.md)
- [Countermeasure dispensers](./countermeasure-dispensers.md)
- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [Rounds as hidden launcher subparts](./rounds-as-hidden-launcher-subparts.md)
- [Dual-hook simulation step](./dual-hook-simulation-step.md)
- [Raymarched mushroom cloud](./raymarched-mushroom-cloud.md)
- [Spatial gun and motor sounds](./spatial-gun-and-motor-sounds.md)
- [Lay volumetric smoke trails with one PlumeTrailEmitterState cursor per source](../../../howto/rendering/lights-and-particles/draw-exhaust-plumes-without-an-engine/draw-exhaust-plumes-without-an-engine-5.md) - how-to article demonstrating this feature

[^ksarmory-plumesmoke]: src/KSArmory/Ksa/PlumeSmoke.cs
[^ksarmory-motorsmoke]: src/KSArmory/Ksa/MotorSmoke.cs
[^ksarmory-decoyeffects]: src/KSArmory/Ksa/DecoyEffects.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-config]: src/KSArmory/Sim/Config.cs
[^ksarmory-claude-md]: CLAUDE.md (author notes)
[^ksarmory-nuclear-effect-doc]: docs/NUCLEAR-EFFECT.md (author analysis)
[^ksarmory-blocked-doc]: docs/BLOCKED-ON-KSA.md (author analysis)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-volumetrictrailrenderer]: KSA.VolumetricTrailRenderer (decompiled, build 2026.10.10.5554)
[^ksa-plumetrailsegmentsmanager]: KSA.PlumeTrailSegmentsManager (decompiled, build 2026.10.10.5554)
[^ksa-plumetrailemittertracker]: KSA.PlumeTrailEmitterTracker (decompiled, build 2026.10.10.5554)
[^ksa-plumeemittergrouping]: KSA.PlumeEmitterGrouping (decompiled, build 2026.10.10.5554)
[^ksa-plumetrailemitterframe]: KSA.PlumeTrailEmitterFrame (decompiled, build 2026.10.10.5554)
[^ksa-plumesegmentstore]: KSA.PlumeSegmentStore (decompiled, build 2026.10.10.5554)
[^ksa-plumetrailsettings]: KSA.PlumeTrailSettings (decompiled, build 2026.10.10.5554)
[^ksa-plumetrailtemplate]: KSA.PlumeTrailTemplate (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
