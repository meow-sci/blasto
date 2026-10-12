---
type: How-To
title: Explosion presets with a BubbleOrigin fixed in the body frame
description: Spawn a stock explosion anywhere by handing ExplosionSystem.SpawnPreset a BubbleOrigin in the planet's rotating frame, so smoke stays over the ground where the burst happened
category: rendering
group: overlays
topic: choose-a-render-frame-and-anchor-effects
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/ksa-explosions-from-a-point]
techniques: [body-fixed-anchor]
hooks: [starmap StarMapAfterGui (via StepOnce), starmap StarMapAllModsLoaded (preset check)]
lifecycle: [startup:asset-load, startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.ExplosionSystem, KSA.Celestial]
tags: [explosion, bubbleorigin, ccf, body-fixed, particles, anchor]
---

Outcome: a stock KSA explosion (flash, sound, particle stages, fireball volume) at an arbitrary point, with no vehicle or part to host it, that stays on the ground under it as the planet rotates. The trick is a `BubbleOrigin` fixed to the **planet's rotating frame**
(`BubbleFrame.Ccf`). Demonstrated by KSArmory ([Set off KSA's own explosion at an arbitrary point, anchored on the planet](../../../../mods/ksarmory/features/ksa-explosions-from-a-point.md)). This is the body-fixed anchoring sub-step for effects, emitters and meshes (approaches 3 to 5). Prerequisites: StarMap.

## Background: how the game does it

`ExplosionSystem.SpawnPreset(string explosionId, in ExplosionContext context)` [current/decomp/KSA/ExplosionSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ExplosionSystem.cs#L217-L227) spawns a preset from the `<Explosion>` assets; it drops the call off the main thread. The context carries an anchor, an intensity in joules and an
ambient pressure [current/decomp/KSA/ExplosionContext.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ExplosionContext.cs); the anchor is a `BubbleOrigin` [current/decomp/KSA/BubbleOrigin.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BubbleOrigin.cs) with `Time`, `Parent` (a `Celestial`), `BubFrame` ([current/decomp/KSA/BubbleFrame.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BubbleFrame.cs)), `PositionBub` and `VelocityBub`. The engine normally spawns explosions on a vehicle or part that blew up.
Ambient pressure gates each emitter and volume stage (`MinPressurePa`/`MaxPressurePa`), so one preset throws air debris in an atmosphere and vacuum debris in space. A body's rotating frame is reached with `Celestial.GetCce2Ccf()` and `GetCcf2Cce()` [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L532-L538).

## Lifecycle and timing

| Point | Where |
|---|---|
| Preset XML (an `<Explosion>` with only a `<Flash>`) | `startup:asset-load`, via `mod.toml` `assets` |
| Preset check | `startup:all-mods-loaded`: log whether particles are enabled and whether each preset resolves through `ModLibrary.TryGet<ExplosionReference>` (KSA itself only warns, rate-limited) |
| Spawn | `frame:draw-ui`, inside the mod's step from `[StarMapAfterGui]`, on the main thread; the burst position is re-derived from the drawn offset so the explosion lines up with the round as drawn this frame |

Never call it from a physics worker or a background task. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Pick a body:** the parent `Celestial` of the craft nearest the burst, else the body the firing system knows it is over, else the controlled vehicle's.

2. **Convert the burst point to body-fixed:** `(burstEcl - body.GetPositionEcl()).Transform(body.GetCce2Ccf())`.

3. **Build the context** with a `BubbleOrigin` in `Ccf` at rest, no `Vehicle` or `Part`, an intensity and the ambient pressure at the burst, then spawn:

   ```csharp
   // src/KSArmory/Ksa/Detonation.cs L85-L106
   if (BodyFor(near, known) is not { } body) return "no celestial to hang it on";

   double3 positionCcf = (burstEcl - body.GetPositionEcl()).Transform(body.GetCce2Ccf());
   if (!Vec.IsFinite(positionCcf)) return "burst position does not convert to body-fixed";

   // At rest in the body's frame, so the smoke stands over the ground where the round burst
   // rather than flying on with the target it burst beside.
   ExplosionContext context = new()
   {
       Anchor = new BubbleOrigin
       {
           Time = Universe.GetElapsedTime(),
           Parent = body,
           BubFrame = BubbleFrame.Ccf,
           PositionBub = positionCcf,
           VelocityBub = double3.Zero,
       },
       IntensityJ = WarheadExplosion.Joules(chargeKg),
       AmbientPressurePa = KsaWorld.AtmosphericPressureAt(body, burstEcl),
   };

   ExplosionSystem.SpawnPreset(preset, in context);
   ```

4. **Compute ambient pressure** from altitude over `MeanRadius`: zero above the atmosphere's height, otherwise `GetAtmosphericPressureAtAltitude`.

5. **Choose the preset by size, not energy.** KSA divides `IntensityJ` by a 5e10 J reference and clamps to at least 0.05, so every small conventional charge would explode at the same floor size; compare the warhead's fireball radius with each Core preset's fireball on a log scale
   ([src/KSArmory/Sim/WarheadExplosion.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/WarheadExplosion.cs#L55-L70)).

6. **Log every refusal once per reason** (no body, non-finite position, engine exception), because "no explosion" otherwise looks the same as a warhead that never fired.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/Detonation.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Detonation.cs) | Body pick, Ccf anchor, context, spawn |
| [src/KSArmory/Sim/WarheadExplosion.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/WarheadExplosion.cs) | Preset by fireball size |
| [src/KSArmory/KSArmoryParticles.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryParticles.xml) | `KSArmoryNuclearBurst` preset |

## Variations and gotchas

- **Anchor on the body, not the target:** anchoring on a vehicle that is being destroyed is fragile (`SpawnPresetInternal` returns early when `Vehicle.IsImpactFxSuppressed()`).
- **Off-main-thread calls are silently dropped** with a rate-limited warning in KSA's log.
- **Above 1200x simulation speed** KSA skips the particle and volume stages entirely.
- **An explosion volume in a custom preset** shares KSA's low-resolution cloud images; the nuclear preset has no `VolumeRef` and no `SoundEvent` (the bang plays later, when the pressure front arrives).

## See also

- [Overview](overview.md)
- [Approach 4: pooled emitters re-anchored every frame](choose-a-render-frame-and-anchor-effects-4.md)
- [Set off KSA's own explosion at an arbitrary point, anchored on the planet](../../../../mods/ksarmory/features/ksa-explosions-from-a-point.md)
- [Spawn particle effects and explosions from a mod](../../lights-and-particles/spawn-particle-effects/overview.md)
