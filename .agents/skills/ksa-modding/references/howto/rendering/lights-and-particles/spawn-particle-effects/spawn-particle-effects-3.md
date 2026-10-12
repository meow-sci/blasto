---
type: How-To
title: Fire the game's own ExplosionSystem presets from a point anchored on the planet
description: Set off a stock or custom Explosion preset anywhere by building an ExplosionContext whose BubbleOrigin is fixed in a celestial's body frame, with real ambient pressure and a preset chosen by fireball size
category: rendering
group: lights-and-particles
topic: spawn-particle-effects
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/ksa-explosions-from-a-point]
techniques: [engine-explosion-preset, xml-asset]
hooks: [starmap StarMapAfterGui, starmap StarMapAllModsLoaded]
lifecycle: [startup:asset-load, startup:all-mods-loaded, frame:draw-ui]
game_types:
  - KSA.ExplosionSystem
  - KSA.ExplosionContext
  - KSA.BubbleOrigin
  - KSA.BubbleFrame
  - KSA.ExplosionReference
  - KSA.Celestial
  - KSA.Universe
  - KSA.GameSettings
tags: [explosion, preset, bubble-origin, ambient-pressure, flash, sound]
---
Outcome: the explosion the player sees is KSA's own: flash, sound, particle stages and fireball volume, from an `<Explosion>` preset, set off at any point in the world (a mid-air proximity burst, a ground strike) with no vessel to host it. The reusable trick is giving the engine a `BubbleOrigin` fixed to the planet.

Demonstrated by KSArmory ([Set off KSA's own explosion at an arbitrary point, anchored on the planet](../../../../mods/ksarmory/features/ksa-explosions-from-a-point.md)). Prerequisites: StarMap.

## Background: how the game does it

`ExplosionSystem.SpawnPreset(string explosionId, in ExplosionContext context)` is the public entry point. It silently drops the call off the main thread (a rate-limited warning is logged), and `SpawnPresetInternal` returns early when `context.Vehicle.IsImpactFxSuppressed()`; so anchoring on a vehicle that is being destroyed is fragile:

```csharp
// current/decomp/KSA/ExplosionSystem.cs L217-L233
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

private static void SpawnPresetInternal(string explosionId, in ExplosionContext context, float4x4? localOffsetOverride)
{
	Vehicle? vehicle = context.Vehicle;
	if (vehicle != null && vehicle.IsImpactFxSuppressed())
	{
```

`ExplosionContext` carries `Vehicle`, `Part`, `Anchor` (`BubbleOrigin`), `IntensityJ`, `PropellantMassKg`, `AmbientPressurePa`, `Cause` and `RoutedExplosionId` ([current/decomp/KSA/ExplosionContext.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ExplosionContext.cs#L3-L20)). Intensity is `IntensityJ / ReferenceExplosionEnergyJ` (5e10 J), clamped to `[0.05, 100]` by `ExplosionIntensity.ClampIntensity` ([current/decomp/KSA.Rendering/ExplosionIntensity.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ExplosionIntensity.cs#L7-L17)). Each preset stage can be gated by `MinPressurePa`/`MaxPressurePa` against `AmbientPressurePa`. Above `WarpSuppressionThreshold` (1200x simulation speed) the particle and volume stages are skipped; flash and sound still fire. The presets themselves are `<Explosion>` assets, for example:

```xml
<!-- current/Content/Core/ExplosionAssets.xml L118-L129 -->
<Explosion Id="SmallFire">
    <Flash PeakIntensity="500" Range="20" DecaySeconds="0.5" LifeSeconds="1" CastShadows="false" ColorMidAt="0.3">
        <ColorStart R="1" G="0.97" B="0.9"/>
        <ColorMid R="1" G="0.72" B="0.28"/>
        <ColorEnd R="1" G="0.25" B="0.05"/>
    </Flash>
    <SoundEvent SoundId="Explosion"/>
    <VolumeRef Id="FireballVolumeSmall" Delay="0" Intensity="1" FallbackEmitterId="FireBallSmall"/>
    <EmitterRef Id="ExplosionSparks" Delay="0" Intensity="10"/>
    <VolumeRef Id="ShockWaveVolume" Delay="0" Intensity="1" FallbackEmitterId="ShockWave"/>
    <EmitterRef Id="Embers" Delay="0" Intensity="1"/>
</Explosion>
```

## Lifecycle and timing

- **Preset XML:** `startup:asset-load`, via `mod.toml` `assets`.
- **Preset check:** `startup:all-mods-loaded`. `OnFullyLoaded` logs whether particles are enabled and whether each preset resolves through `ModLibrary.TryGet<ExplosionReference>`; KSA itself only warns in its own log, rate-limited, when `SpawnPreset` meets an unknown id.
- **Spawn:** `frame:draw-ui`, from the mod's simulation step normally run from `[StarMapAfterGui]`. The burst position is re-derived from the drawn offset so the explosion lines up with the round as it was drawn this frame. **It must be on the main thread**: never from a physics worker or background task.
- The timestamp `Universe.GetElapsedTime()` ties the anchor to the simulation time of the step just applied. The effect is then updated by `ExplosionSystem.Update(dt)` in `OnPreRender` (`frame:pre-render`, Program.cs L2416); see the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Pick a body**: the parent `Celestial` of the craft nearest the burst, else the body the firing system knows it is over, else the controlled vehicle's.

   ```csharp
// src/KSArmory/Ksa/Detonation.cs L122-L135
    // The body to hang the effect on: whatever the craft nearest the burst is bound to, then the
    // body the firing system knows it is over, then the craft being flown's. All are within a
    // physics bubble of the burst, which is the only accuracy this needs. The middle one is a
    // round whose launcher has been destroyed, where the nearest craft is often the one flown.
    /// <summary>Which body to hang an effect on, given something near it.</summary>
    internal static Celestial? BodyFor(Vehicle? near, Celestial? known = null)
    {
        if (KsaWorld.IsAlive(near) && near!.Parent is Celestial body) return body;
        if (known is not null) return known;
        if (KsaWorld.ControlledVehicle?.Parent is Celestial fallback) return fallback;

        return null;
    }
}
```

2. **Convert to the body-fixed frame and build the context**. Zero velocity in `Ccf` means the smoke stands over the ground where the round burst instead of flying on with the target. Pass real ambient pressure so pressure-gated stages choose air or vacuum debris:

   ```csharp
// src/KSArmory/Ksa/Detonation.cs L83-L106
try
{
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

3. **Compute ambient pressure** from altitude over `MeanRadius`; zero above the atmosphere height:

   ```csharp
// src/KSArmory/Ksa/KsaWorld.cs L2549-L2565
public static float AtmosphericPressureAt(Celestial body, double3 positionEcl)
{
    try
    {
        if (body.GetAtmosphereReference()?.Physical is not { } air) return 0f;

        double altitude = Math.Max(Vec.Len(positionEcl - body.GetPositionEcl()) - body.MeanRadius, 0.0);
        if (altitude >= air.Height) return 0f;

        double pressure = air.GetAtmosphericPressureAtAltitude(altitude);
        return double.IsFinite(pressure) && pressure > 0.0 ? (float)pressure : 0f;
    }
    catch
    {
        return 0f;
    }
}
```

4. **Choose a preset by size, not energy.** Every conventional warhead sits under KSA's intensity floor (0.05 of 5e10 J), so handed energy alone all would explode at the same size. Compare the warhead's fireball radius with each Core preset's fireball at that floor on a log scale (`PopSmallExplosion` below about 0.36 kg, `SmallFire` to about 41 kg, `Explosion_Conflagration` above):

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

5. **Optionally ship your own preset** in your XML, a Core-shaped `<Explosion>`. KSArmory's nuclear one has only a `<Flash>`: no `SoundEvent` (the bang is played later, when the pressure front reaches the listener) and no `VolumeRef` (KSA raymarches explosion volumes into the same low-resolution images as weather clouds, which the mod's cloud pass reads as weather):

   ```xml
<!-- src/KSArmory/KSArmoryParticles.xml L539-L556 -->
<Explosion Id="KSArmoryNuclearBurst">
    <Flash PeakIntensity="5000" Range="200" DecaySeconds="0.2" LifeSeconds="0.55" ColorMidAt="0.3">
        <ColorStart R="1" G="0.97" B="0.9"/>
        <ColorMid R="1" G="0.72" B="0.28"/>
        <ColorEnd R="1" G="0.25" B="0.05"/>
    </Flash>
    <!--
      NO SoundEvent. The engine plays one at the instant of the burst, which is right for
      everything it ships: a conventional explosion is heard from inside its own blast radius
      or not at all. A nuclear one is watched from kilometres off, where the flash and the bang
      are separated by the whole of the time sound takes to cross the gap. Ksa/BurstSound.cs
      plays it when it arrives.

      And NO VolumeRef. KSA raymarches explosion volumes into the same low-resolution images
      as its weather clouds, and CloudPass reads those images as weather: a volume here would
      occlude the burst's own cloud, in blocks.
    -->
</Explosion>
```

6. **Log refusals once per reason.** "No explosion" otherwise looks the same as a warhead that never fired (no body, non-finite position, engine exception):

   ```csharp
// src/KSArmory/Ksa/Detonation.cs L60-L70
public static void Explode(double3 burstEcl, double chargeKg, Vehicle? near, Celestial? body = null)
{
    if (WarheadExplosion.PresetFor(chargeKg) is not { } preset) return;

    string why = TryExplode(preset, burstEcl, chargeKg, near, body);
    if (why.Length == 0) return;

    // A refusal is indistinguishable from a wrong frame and a warhead that never went off: all
    // three are "no explosion". Say which, once per reason.
    if (_reported.Add(why)) Log.Warn($"no explosion ({preset}): {why}");
}
```

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/Detonation.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Detonation.cs) | Body choice, Ccf anchor, context, spawn, refusal log |
| [src/KSArmory/Sim/WarheadExplosion.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/WarheadExplosion.cs) | Preset by fireball size; joules per kg TNT |
| [src/KSArmory/KSArmoryParticles.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryParticles.xml#L525-L556) | `KSArmoryNuclearBurst` preset |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L2549-L2565) | `AtmosphericPressureAt` |

## Variations and gotchas

- **Off-main-thread calls are silently dropped** with a rate-limited warning in KSA's log.
- **Above 1200x simulation speed** KSA skips the particle and volume stages entirely.
- **Particles setting off** means an explosion only flashes and sounds.
- **A volume in a custom preset** shares the low-resolution cloud images, which matters if your mod composites its own clouds.
- **Energy alone does not size small explosions**; pick the preset.
- **Do not anchor on a vehicle being destroyed**; anchor on the body.

## See also

- [Overview](overview.md)
- [Approach 1](spawn-particle-effects-1.md), [approach 2](spawn-particle-effects-2.md)
- [Set off KSA's own explosion at an arbitrary point, anchored on the planet](../../../../mods/ksarmory/features/ksa-explosions-from-a-point.md)
- [Choose a render frame and anchor effects to bodies, parts and terrain](../../overlays/choose-a-render-frame-and-anchor-effects/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
