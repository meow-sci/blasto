---
type: Mod Feature
title: Set off KSA's own explosion at an arbitrary point, anchored on the planet
description: A burst in mid-air with no vehicle to host it calls ExplosionSystem.SpawnPreset with a BubbleOrigin fixed in the body's rotating frame, real ambient pressure for the pressure-gated stages, and a preset chosen by fireball size because KSA floors explosion intensity
mod: ksarmory
category: rendering
techniques: [engine-explosion-preset, body-fixed-anchor, xml-asset, preset-by-size]
hooks: [starmap StarMapAfterGui (via StepOnce), starmap StarMapAllModsLoaded (preset check)]
lifecycle: [startup:asset-load, startup:all-mods-loaded, frame:draw-ui]
game_types:
  - KSA.ExplosionSystem
  - KSA.ExplosionContext
  - KSA.BubbleOrigin
  - KSA.BubbleFrame
  - KSA.ExplosionReference
  - KSA.Rendering.ExplosionIntensity
  - KSA.Celestial
  - KSA.PhysicalAtmosphereReference
  - KSA.ModLibrary
  - KSA.GameSettings
  - KSA.Universe
tags: [explosions, particles, fireball, presets, bubble-origin, xml]
---

When a KSArmory warhead bursts, the explosion the player sees is KSA's own: flash, sound,
particle stages and fireball volume come from an `<Explosion>` preset fired through
`ExplosionSystem.SpawnPreset`.[^detonation][^ksa-explosionsystem] The engine normally spawns these
on a vehicle or part that blew up; a proximity burst in mid-air has neither (the obvious host, the
target, is about to be destroyed and gets its own explosion from KSA anyway). KSArmory instead
hands the engine a `BubbleOrigin` fixed to the **planet**, which is the reusable trick: any mod can
set off a stock explosion anywhere.[^detonation]

## How it works

1. **Pick a body.** `Detonation.BodyFor` takes the parent `Celestial` of the craft nearest the
   burst, else the body the firing system knows it is over, else the controlled vehicle's.[^detonation]
2. **Convert to body-fixed.** The burst point (ecliptic, `Ecl`) minus `body.GetPositionEcl()`,
   rotated by `body.GetCce2Ccf()`, gives a position in the body's rotating frame (`Ccf`).[^detonation][^ksa-celestial]
3. **Build the context.** `ExplosionContext { Anchor = BubbleOrigin { Time = Universe.GetElapsedTime(),
   Parent = body, BubFrame = BubbleFrame.Ccf, PositionBub = positionCcf, VelocityBub = 0 },
   IntensityJ, AmbientPressurePa }` with no `Vehicle` or `Part`. Zero velocity in `Ccf` means the
   smoke stands over the ground where the round burst instead of flying on with the target.[^detonation][^ksa-explosioncontext][^ksa-bubbleorigin]
4. **Ambient pressure** comes from `KsaWorld.AtmosphericPressureAt(body, burstEcl)`: altitude over
   `MeanRadius`, zero above `air.Height`, else `GetAtmosphericPressureAtAltitude`. KSA uses it to
   gate each emitter and volume stage (`MinPressurePa`/`MaxPressurePa`), so the same preset throws
   air debris in the atmosphere and vacuum debris in space.[^ksaworld][^ksa-atmosphere][^ksa-explosionsystem]
5. **Choose a preset by size, not energy.** KSA divides `IntensityJ` by
   `ReferenceExplosionEnergyJ` (5e10 J) and clamps the result to at least 0.05
   (`ExplosionIntensity.ClampIntensity`), so every conventional warhead would explode at the same
   floor size. `WarheadExplosion.PresetFor(chargeKg)` compares the warhead's fireball radius with
   each Core preset's fireball at that floor on a log scale: `PopSmallExplosion` below about
   0.36 kg, `SmallFire` to about 41 kg, `Explosion_Conflagration` above, and the mod's own
   `KSArmoryNuclearBurst` above the mushroom-cloud threshold.[^warhead-explosion][^ksa-explosionintensity][^ksa-explosion-assets]
6. **A custom preset in XML.** `KSArmoryNuclearBurst` in `KSArmoryParticles.xml` is a Core-shaped
   `<Explosion>` with only a `<Flash>`: no `SoundEvent` (the bang is played later, when the pressure
   front reaches the listener) and no `VolumeRef` (KSA raymarches explosion volumes into the same
   low-resolution images as weather clouds, which the mod's cloud pass reads as weather).[^particles-xml]

Every refusal (no body, non-finite position, an engine exception) is logged once per reason,
because "no explosion" otherwise looks the same as a warhead that never fired.[^detonation]

## Lifecycle and timing

- **Preset XML: `startup:asset-load`**, via `mod.toml` `assets`.
- **Preset check: `startup:all-mods-loaded`.** `OnFullyLoaded` logs whether particles are enabled
  (`GameSettings.Current.Graphics.Particles`) and whether each of `WarheadExplosion.Presets`
  resolves through `ModLibrary.TryGet<ExplosionReference>`; KSA itself only warns in its own log,
  rate-limited, when `SpawnPreset` meets an unknown Id.[^entry][^detonation][^ksa-gamesettings][^ksa-explosionsystem]
- **Spawn: `frame:draw-ui`.** `WeaponSystem.Detonate` (inside `StepSimulation`, normally from the
  `[StarMapAfterGui]` hook) calls `Detonation.Explode` with the burst position re-derived from the
  drawn offset (`DrawnBurstEcl`) so the explosion lines up with the round as it was drawn this
  frame.[^weapon-system][^entry] `SpawnPreset` **drops the call off the main thread**
  (`Program.IsMainThread()` check), so this must never run from a physics worker or a background
  task.[^ksa-explosionsystem]
- The time stamp `Universe.GetElapsedTime()` ties the anchor to the simulation time of the step
  just applied.[^detonation]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/Detonation.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Detonation.cs#L60-L134) | Body choice, Ccf anchor, context, spawn, refusal log |
| [src/KSArmory/Sim/WarheadExplosion.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/WarheadExplosion.cs#L12-L75) | Preset by fireball size; joules per kg TNT |
| [src/KSArmory/KSArmoryParticles.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryParticles.xml#L539-L556) | `KSArmoryNuclearBurst` preset |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L2549-L2565) | `AtmosphericPressureAt` |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L3938-L3948) | Caller at detonation |
| [src/KSArmory/Ksa/BurstTool.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BurstTool.cs) | Developer tool: click to set off a burst |

## Snippets

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

```csharp
// src/KSArmory/Sim/WarheadExplosion.cs L55-L70
    public static string? PresetFor(double chargeKg)
    {
        if (!double.IsFinite(chargeKg) || chargeKg <= 0.0) return null;

        double fireball = Warhead.FireballRadius(chargeKg);
        double smallFire = SmallFireFireballMetres * Math.Cbrt(KsaFloorIntensity);
        double conflagration = ConflagrationFireballMetres
                               * Math.Cbrt(ConflagrationFireballIntensity * KsaFloorIntensity);

        if (fireball < smallFire * 0.5) return Pop;
        if (fireball < Math.Sqrt(smallFire * conflagration)) return SmallFire;

        // Above the threshold this mod draws the smoke itself, so the preset that carries Core's is
        // the wrong one however well it fits the fireball.
        return chargeKg >= MushroomCloud.ThresholdKg ? NuclearBurst : Conflagration;
    }
```

```csharp
// current/decomp/KSA/ExplosionSystem.cs L217-L227
	public static void SpawnPreset(string explosionId, in ExplosionContext context)
	{
		if (!Program.IsMainThread())
		{
			WarnRateLimited(ref _lastSpawnPresetOffThreadWarningTime, "ExplosionSystem.SpawnPreset('" + explosionId + "') called off the main thread, dropped");
		}
		else
		{
			SpawnPresetInternal(explosionId, in context, null);
		}
	}
```

## Game APIs touched

| Member | Use |
|---|---|
| `ExplosionSystem.SpawnPreset(string, in ExplosionContext)` ([current/decomp/KSA/ExplosionSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ExplosionSystem.cs#L217)) | Fire a preset; main thread only |
| `ExplosionSystem.ReferenceExplosionEnergyJ` (L98), `WarpSuppressionThreshold` (L106) | Intensity scale; particle and volume stages are skipped above 1200x warp (flash and sound still fire) |
| `ExplosionIntensity.ClampIntensity` ([current/decomp/KSA.Rendering/ExplosionIntensity.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ExplosionIntensity.cs#L12)) | Floor 0.05, cap 100 |
| `ExplosionContext` ([current/decomp/KSA/ExplosionContext.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ExplosionContext.cs)) | `Vehicle`, `Part`, `Anchor`, `IntensityJ`, `AmbientPressurePa` |
| `BubbleOrigin`, `BubbleFrame.Ccf` ([current/decomp/KSA/BubbleOrigin.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BubbleOrigin.cs#L9)) | Anchor in a body's rotating frame |
| `Celestial.GetCce2Ccf()` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L538)), `GetPositionEcl()` | Ecl to body-fixed |
| `PhysicalAtmosphereReference.GetAtmosphericPressureAtAltitude`, `Height` ([current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L80)) | Ambient pressure |
| `ModLibrary.TryGet<ExplosionReference>` | Preset resolves at load |
| `GameSettings.Current.Graphics.Particles` ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L361)) | Logged: with particles off an explosion only flashes and sounds |

## Pitfalls and version notes

- **Energy alone does not size small explosions**: below 0.05 of 5e10 J every charge clamps to the
  same size, hence preset selection by fireball radius.[^warhead-explosion][^ksa-explosionintensity]
- **Off-main-thread calls are silently dropped** with a rate-limited warning in KSA's log.[^ksa-explosionsystem]
- **Above 1200x simulation speed** KSA skips the particle and volume stages entirely.[^ksa-explosionsystem]
- **An explosion volume in a custom preset** shares KSA's low-resolution cloud images, which
  matters if a mod also composites its own clouds.[^particles-xml]
- Anchoring on a vehicle that is being destroyed is fragile (`SpawnPresetInternal` returns early
  when `Vehicle.IsImpactFxSuppressed()`), one more reason to anchor on the body.[^ksa-explosionsystem]

## See also

- [Blast damage and part health](./blast-damage-and-part-health.md)
- [Pooled particle emitters](./pooled-particle-emitters.md)
- [Raymarched mushroom cloud](./raymarched-mushroom-cloud.md)
- [Burst sound at blast arrival](./burst-sound-at-blast-arrival.md)
- [Ecliptic frames and epochs](./ecliptic-frames-and-epochs.md)
- [Cursor-ray world click tools](./cursor-ray-world-click-tools.md)
- [Fire the game's own ExplosionSystem presets from a point anchored on the planet](../../../howto/rendering/lights-and-particles/spawn-particle-effects/spawn-particle-effects-3.md) - how-to article demonstrating this feature
- [Explosion presets with a BubbleOrigin fixed in the body frame](../../../howto/rendering/overlays/choose-a-render-frame-and-anchor-effects/choose-a-render-frame-and-anchor-effects-3.md) - how-to article demonstrating this feature

[^detonation]: src/KSArmory/Ksa/Detonation.cs
[^warhead-explosion]: src/KSArmory/Sim/WarheadExplosion.cs
[^particles-xml]: src/KSArmory/KSArmoryParticles.xml
[^weapon-system]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^entry]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksa-explosionsystem]: KSA.ExplosionSystem (decompiled, build 2026.10.10.5554)
[^ksa-explosioncontext]: KSA.ExplosionContext (decompiled, build 2026.10.10.5554)
[^ksa-bubbleorigin]: KSA.BubbleOrigin (decompiled, build 2026.10.10.5554)
[^ksa-explosionintensity]: KSA.Rendering.ExplosionIntensity (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-atmosphere]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^ksa-explosion-assets]: Content/Core/ExplosionAssets.xml (build 2026.10.10.5554)
