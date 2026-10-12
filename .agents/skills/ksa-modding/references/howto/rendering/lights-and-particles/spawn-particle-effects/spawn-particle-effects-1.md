---
type: How-To
title: Take XML-declared ParticleEmitter assets by id from the engine pool and give them back correctly
description: Declare ParticleEmitter assets in your own XML, acquire them with GetAndInitializeEmitters, re-anchor held emitters on a celestial every frame, and always Kill them before returning them to the shared pool
category: rendering
group: lights-and-particles
topic: spawn-particle-effects
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/pooled-particle-emitters]
techniques: [pooled-resource, xml-asset, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapUnload]
lifecycle: [startup:asset-load, frame:draw-ui, frame:celestials, frame:pre-render]
game_types:
  - KSA.Rendering.Particles.ParticleSystem
  - KSA.Rendering.Particles.ParticleEmitter
  - KSA.Rendering.Particles.ParticleEmitterReference
  - KSA.Celestial
  - KSA.BubbleOrigin
  - KSA.Program
  - KSA.Universe
tags: [particles, emitter, pool, xml-asset, endless, burst, anchor]
---
Outcome: rocket flames following a mod-simulated missile, muzzle flashes, flares trailing smoke, chaff and flak puffs, all as stock-format `<ParticleEmitter>` assets in your mod's XML, fired through KSA's own particle system by id. You write no particle code or shaders; you write the discipline around KSA's fixed shared emitter pool: re-anchor, cap, and always give back.

Demonstrated by KSArmory ([Mod-authored particle emitters taken from KSA's shared pool and re-anchored each frame](../../../../mods/ksarmory/features/pooled-particle-emitters.md)). Prerequisites: StarMap. For particles built in C# without XML see [approach 2](spawn-particle-effects-2.md).

## Background: how the game does it

`Program.ParticleSystem` is one `ParticleSystem<ParticleUpdateData, ParticleRenderData>` constructed with 1024 emitters of 512 particles ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1169)). The pool hands out emitters only all-or-nothing:

```csharp
// current/decomp/KSA.Rendering.Particles/ParticleSystem.cs L24-L54
public bool Get(int count, out List<ParticleEmitter<TUpdate, TRender>> emitters)
{
	if (count <= 0 || count > Emitters.Count)
	{
		emitters = null;
		return false;
	}
	emitters = new List<ParticleEmitter<TUpdate, TRender>>(count);
	for (int i = 0; i < Emitters.Count; i++)
	{
		if (emitters.Count >= count)
		{
			break;
		}
		if (Emitters[i].CanAcquireFromPool)
		{
			Emitters[i].AcquireFromPool();
			emitters.Add(Emitters[i]);
		}
	}
	if (emitters.Count == count)
	{
		return true;
	}
	foreach (ParticleEmitter<TUpdate, TRender> emitter in emitters)
	{
		emitter.ResetEmitter();
	}
	emitters = null;
	return false;
}
```

`GetAndInitializeEmitters(id, out handles)` looks up a `ParticleEmitterReference` registered in `ModLibrary` (the stock ones are in `current/Content/Core/ParticleEmitterAssets.xml`; KSA maps `<ParticleEmitter>` to `ParticleEmitterReference`, so your mod's ids resolve like Core's), takes one pool emitter per node of its nested tree, initializes each and returns generation-checked handles:

```csharp
// current/decomp/KSA.Rendering.Particles/ParticleSystem.cs L259-L289
public bool GetAndInitializeEmitters(string modLibraryId, out List<ParticleEmitter<TUpdate, TRender>.Handle> emitterHandles)
{
	ParticleEmitterReference particleEmitterReference = ModLibrary.Get<ParticleEmitterReference>(modLibraryId);
	int totalEmitterCount = particleEmitterReference.GetTotalEmitterCount();
	if (!EmitterPool.Get(totalEmitterCount, out List<ParticleEmitter<TUpdate, TRender>> emitters))
	{
		emitterHandles = null;
		return false;
	}
	int index = 0;
	InitializeEmittersRecursive(particleEmitterReference, emitters, ref index);
	emitterHandles = new List<ParticleEmitter<TUpdate, TRender>.Handle>(emitters.Count);
	foreach (ParticleEmitter<TUpdate, TRender> item in emitters)
	{
		emitterHandles.Add(item.CreateHandle());
	}
	return true;
}

private void InitializeEmittersRecursive(ParticleEmitterReference reference, List<ParticleEmitter<TUpdate, TRender>> emitters, ref int index)
{
	ParticleEmitterReference particleEmitterReference = reference.Get();
	InitializeEmitter(emitters[index++], particleEmitterReference);
	foreach (ParticleEmitterReference particleEmitter in particleEmitterReference.ParticleEmitters)
	{
		if (particleEmitter != null)
		{
			InitializeEmittersRecursive(particleEmitter, emitters, ref index);
		}
	}
}
```

`Celestial.AddEmitter` and `RemoveEmitter` only add or drop the handle from that body's bookkeeping list. The real update loop walks the whole pool every frame, and does nothing at all when the player's particles setting is off:

```csharp
// current/decomp/KSA.Rendering.Particles/ParticleSystem.cs L291-L301
public void UpdateEmitters(float nextSimDeltaTime)
{
	if (!GameSettings.Current.Graphics.Particles)
	{
		return;
	}
	foreach (ParticleEmitter<TUpdate, TRender> emitter in EmitterPool.Emitters)
	{
		emitter.Update(nextSimDeltaTime);
	}
}
```

## Lifecycle and timing

| Point | What happens | Phase |
|---|---|---|
| Asset load | Emitter definitions resolved through `ModLibrary` from `mod.toml` `assets` | `startup:asset-load` |
| Mod writes | Take, point, tune and give inside the mod's simulation step | `[StarMapAfterGui]` (`frame:draw-ui`) or an `OnFrameCelestials` prefix (`frame:celestials`) when the HUD is hidden |
| Engine spawns | `OnPreRender`: `float num = (float)Universe.GetNextSimStep().DeltaTime;` then `ExplosionSystem.Update(num)` and `ParticleSystem.UpdateEmitters(num)` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2408-L2418)) | `frame:pre-render` |
| Emitter update | `ParticleEmitter.Update` sets elapsed time from `Universe.GetElapsedTime()`, rebuilds the model matrix from `Origin` and spawns `SpawnRate * nextSimDeltaTime` particles | `frame:pre-render` |
| GPU step and draw | `ParticleSystem.UpdateParticles(cmd, nextSimDt, ...)` inside `RenderGame`, after the opaque scene pass | `render:particles` (row 10 of the [RenderGame table](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence)) |

**Timing asymmetry.** The anchor you write is a position at `Universe.GetElapsedTime()`, the end of the step applied this frame, and the mod advances its rounds by that same applied step. The engine spawns and integrates the particles by `GetNextSimStep().DeltaTime`, the step the physics workers are computing now. At a steady warp the two are equal; on a frame where the speed changes or the first frame of a pause they differ, which shows as a one-frame spacing glitch in a trail. The engine's own vehicle emitters take their anchor from `Vehicle.BubbleOrigin` inside `UpdateUniforms`, so only mod-anchored emitters see it. See rows 12, 13, 20 and 21 of [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence).

## Steps

1. **Declare emitters in your XML** and list it in `mod.toml` `assets`. Copy the format from Core's `ParticleEmitterAssets.xml`, the only documentation it has. Nest child stages inline (`<ParticleEmitters>`) rather than by id; a by-id child from a mod threw "Invalid renderer type". An endless motor plume:

   ```xml
<!-- src/KSArmory/KSArmoryParticles.xml L82-L106 -->
<ParticleEmitter Id="KSArmoryMotorPlume" Density="1.225">
    <MaxParticles Value="64" />
    <Mesh Id="ParticleSphere"/>
    <Renderer>SimpleColor</Renderer>
    <Updaters>SimpleMovement</Updaters>
    <Updaters>ShrinkOverLifetime</Updaters>
    <Updaters>HueShift</Updaters>
    <Updaters>ModelMatrix</Updaters>
    <Updaters>Age</Updaters>
    <SpawnMode>Endless</SpawnMode>
    <EmitterSpawnLogic>Sphere</EmitterSpawnLogic>
    <EmitterSize Value="0.05"/>
    <EmitterExtents X="0.04" Y="0" Z="0"/>
    <SpawnRate Value="90"/>
    <Force X="0" Y="0" Z="0" />
    <InheritVelocity Value="true"/>
    <ParticleColor X="12" Y="6" Z="2" W="1"/>
    <ParticleColorShift X="0.35" Y="0.2" Z="0"/>
    <ParticleLifespan X="0.05" Y="0.14"/>
    <ParticleSpawnLogic>ScatterFromCenter</ParticleSpawnLogic>
    <ParticleSize X="0.05" Y="0.14"/>
    <ParticleVelocity X="2.5" Y="2.5" Z="2.5"/>
    <ParticleVelocityShift Value="0.7"/>
    <ParticleExtra X="0.5" Y="0" Z="0" W="0"/>
</ParticleEmitter>
```

   and a burst puff that completes by itself:

   ```xml
<!-- src/KSArmory/KSArmoryParticles.xml L261-L285 -->
<ParticleEmitter Id="KSArmoryFlakPuff" Drag="2.0" Density="1.225">
    <MaxParticles Value="16" />
    <Mesh Id="Plane"/>
    <Renderer>Billboard</Renderer>
    <MaterialId Value="KSArmoryFlakMaterial"/>
    <Updaters>SimpleMovement</Updaters>
    <Updaters>GrowOverLifetime</Updaters>
    <!-- W is the grow ratio: the puff ends three times the size it started. -->
    <EmitterExtra W="3.0"/>
    <Updaters>ModelMatrix</Updaters>
    <Updaters>Age</Updaters>
    <SpawnMode>Burst</SpawnMode>
    <EmitterSpawnLogic>Sphere</EmitterSpawnLogic>
    <EmitterSize Value="1"/>
    <SpawnRate Value="16"/>
    <Force X="0" Y="0" Z="0" />
    <InheritVelocity Value="false"/>
    <ParticleColor X="1" Y="1" Z="1" W="1"/>
    <ParticleLifespan X="18" Y="28"/>
    <ParticleSpawnLogic>ScatterFromCenter</ParticleSpawnLogic>
    <ParticleSize X="8" Y="12"/>
    <ParticleVelocity X="4" Y="4" Z="4"/>
    <ParticleVelocityShift Value="0.6"/>
    <ParticleExtra X="0.8" Y="0" Z="0" W="0"/>
</ParticleEmitter>
```

   One id costs `GetTotalEmitterCount()` pool slots: one per node of its tree.

2. **Take.** Acquire the whole set, and attach each emitter to the celestial the effect belongs to (not to a vehicle; KSA cannot follow a mod-simulated object):

   ```csharp
// src/KSArmory/Ksa/EmitterPool.cs L21-L41
public static List<ParticleEmitter<ParticleUpdateData, ParticleRenderData>.Handle>? Take(
    string id, Celestial body)
{
    if (!Program.Instance.ParticleSystem.GetAndInitializeEmitters(id, out var handles)
        || handles is null || handles.Count == 0)
    {
        return null;
    }

    foreach (var handle in handles)
    {
        if (handle.TryGet() is not { } emitter) continue;

        emitter.Context.Astronomical = body;
        emitter.Context.Vehicle = null;
        emitter.Context.Part = null;
        body.AddEmitter(handle);
    }

    return [.. handles];
}
```

3. **Point, every frame while following.** `BubbleOrigin` offers only body-centred frames, so give it the body, `BubbleFrame.Ccf` and a position and velocity in the body's rotating frame. Convert from ecliptic with `(pointEcl - body.GetPositionEcl()).Transform(body.GetCce2Ccf())`:

   ```csharp
// src/KSArmory/Ksa/EmitterPool.cs L43-L63
/// <summary>Where a set of emitters spawns from, at a point in <paramref name="body"/>'s rotating frame.</summary>
public static BubbleOrigin At(Celestial body, double3 positionCcf, double3 velocityCcf) => new()
{
    Time = Universe.GetElapsedTime(),
    Parent = body,
    BubFrame = BubbleFrame.Ccf,
    PositionBub = positionCcf,
    VelocityBub = velocityCcf,
};

/// <summary>Moves every emitter of a set to <paramref name="origin"/>.</summary>
public static void Point(List<ParticleEmitter<ParticleUpdateData, ParticleRenderData>.Handle> handles,
                         BubbleOrigin origin)
{
    foreach (var handle in handles)
    {
        if (handle.TryGet() is not { } emitter) continue;

        emitter.Origin = origin;
    }
}
```

   A plume is placed where the round body is drawn, moved back half a body length along its velocity to the nozzle, and given the round's local (not ecliptic) velocity as `VelocityBub`:

   ```csharp
// src/KSArmory/Ksa/MotorPlume.cs L106-L143
private void Follow(IProjectile round, IEffectSource system)
{
    if (!_burning.TryGetValue(round, out Live? live))
    {
        if (Acquire(system.EffectBody) is not { } fresh) return;

        fresh.Owner = system;
        live = fresh;
        _burning[round] = live;
    }

    // Where the body is drawn while there is one: PlatformEcl + OffsetFromPlatform is the same
    // round measured from the platform's ANALYTIC position, which on a landed craft is metres
    // from where the body is actually placed -- enough to swamp the nozzle offset below and
    // leave the flame amidships. Once the launcher is destroyed there is no body, and the
    // analytic form is then the exact answer rather than the approximate one.
    if (!system.TryRoundEffectEcl(round, out double3 bodyEcl)) return;

    // At the nozzle, not at the round's centre. A body mesh is modelled about its middle, so
    // anchoring the flame there puts it half a missile too far forward: 1.5 m on the AIM-9J,
    // which reads as the rocket burning from its nose.
    double3 along = Vec.Unit(round.VelocityLocal);
    double3 ecl = Vec.Len2(along) > 0.5
                      ? bodyEcl - (along * (round.Munition.BodyLength * 0.5))
                      : bodyEcl;

    double3 centre = live.Body.GetPositionEcl();

    double3 positionCcf = (ecl - centre).Transform(live.Body.GetCce2Ccf());
    if (!Vec.IsFinite(positionCcf)) return;

    // The round's own flight, not its ecliptic velocity: with InheritVelocity the particles
    // are launched with this, so the 29.8 km/s would throw every one of them off the map.
    double3 velocityCcf = round.VelocityLocal.Transform(live.Body.GetCce2Ccf());

    EmitterPool.Point(live.Handles,
                      EmitterPool.At(live.Body, positionCcf, Vec.IsFinite(velocityCcf) ? velocityCcf : double3.Zero));
}
```

4. **Give.** `Kill()` first, then `RemoveEmitter`. `Kill()` is what actually stops an `Endless` emitter and lets it age out; without it the emitter spawns for the rest of the session and its slot never returns to the pool:

   ```csharp
// src/KSArmory/Ksa/EmitterPool.cs L65-L83
/// <summary>Stops a set and hands it back to the pool. Safe on emitters the engine has already reclaimed.</summary>
public static void Give(Celestial body, List<ParticleEmitter<ParticleUpdateData, ParticleRenderData>.Handle> handles)
{
    // Kill() first, and it is what actually stops it. Celestial.RemoveEmitter only drops the
    // handle from that body's list; ParticleSystem.UpdateEmitters walks the whole pool, so a
    // removed emitter keeps being updated. An Endless one never completes its own simulation,
    // so it spawns for the rest of the session and is never returned to the pool -- which is
    // seen as particles frozen where the emitter last was, and eventually as nothing in the
    // world being able to spawn any.
    foreach (var handle in handles)
    {
        try
        {
            if (handle.TryGet() is { } emitter) emitter.Kill();
            body.RemoveEmitter(handle);
        }
        catch { /* An emitter the engine has already reclaimed, or a body torn down mid-frame, has already stopped it. */ }
    }
}
```

   ```csharp
// current/decomp/KSA.Rendering.Particles/ParticleEmitter.cs L652-L657
public void Kill()
{
	ForceSpawningComplete();
	_freezeAge = false;
	_longestParticleDecayTime = 0.0;
}
```

5. **Fire and forget with `Burst`.** Follow the order KSA's own `ExplosionSystem.FireStage` uses: acquire, set `LocalOffset`, context and `Origin`, tune size, lifespan or force, then `AddEmitter`. A `Burst` completes its own simulation and is returned by the engine, so you never `Kill` it:

   ```csharp
// src/KSArmory/Ksa/FlakPuff.cs L47-L83
float scale = (float)Math.Cbrt(chargeKg / ReferenceChargeKg);

BubbleOrigin origin = new()
{
    Time = Universe.GetElapsedTime(),
    Parent = body,
    BubFrame = BubbleFrame.Ccf,
    PositionBub = burstCcf,
    VelocityBub = double3.Zero,
};

if (!Program.Instance.ParticleSystem.GetAndInitializeEmitters(PuffId, out var handles)
    || handles is null || handles.Count == 0)
{
    Warn($"no free emitters for '{PuffId}'; a shell will burst without smoke");
    return;
}

// The order KSA's own ExplosionSystem.FireStage uses: point it, tune it, then register it.
foreach (var handle in handles)
{
    if (handle.TryGet() is not { } emitter) continue;

    emitter.LocalOffset = float4x4.Identity;
    emitter.Context.Vehicle = null;
    emitter.Context.Part = null;
    emitter.Context.Astronomical = body;
    emitter.Origin = origin;

    emitter.EmitterSpawnInfo.Radius = 2.5f * scale;
    emitter.ParticleInfo.Size *= scale;
    emitter.ParticleInfo.Velocity *= scale;
    emitter.ParticleInfo.Lifespan = new float2(Math.Max(18f * scale, 3f), Math.Max(28f * scale, 5f));

    body.AddEmitter(handle);
}
```

6. **Bound and sweep.** Hold at most N sets (flares capped at 12); hold one set per weapon system, not per round (a rapid-fire gun would drain the pool in a second); give back anything whose owner you no longer know about, and everything in `[StarMapUnload]`.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/EmitterPool.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/EmitterPool.cs) | Take, point, give |
| [src/KSArmory/Ksa/MotorPlume.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/MotorPlume.cs) | Per-round held emitter, ownership, sweeps |
| [src/KSArmory/Ksa/MuzzleFlash.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/MuzzleFlash.cs) | Per-system, per-cluster held sets, all-or-nothing acquire |
| [src/KSArmory/Ksa/FlakPuff.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/FlakPuff.cs) | Burst tuned by charge, gated on altitude and pressure |
| [src/KSArmory/KSArmoryParticles.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryParticles.xml) | Emitter assets and format notes |

## Variations and gotchas

- **`RemoveEmitter` is not a stop.** An `Endless` emitter is only reclaimed after `HasCompletedSimulation`, which needs spawning to complete, which only `Kill()` sets for that mode; then it waits `MaxFramesInFlight` frames and resets.
- **The pool is shared.** KSA's own explosions draw from the same 1024. Handle a `false` return, cap held sets, and never take one per bullet.
- **Particles setting off means nothing draws.** `UpdateEmitters`, `UpdateRenderData` and `UpdateParticles` return early; check `GameSettings.Current.Graphics.Particles` before throwing bursts, or an acquired emitter never ticks.
- **Ccf ignores `LocalOffset`.** With `BubbleFrame.Ccf` the model matrix is built from the origin alone.
- **Fountain sprays one world direction.** Cone spawn is about the emitter's local +X, a fixed axis of the planet in a body-fixed frame; use radial spawn logics (`ScatterFromCenter`, `MoveAwayFromCenter`).
- **`GrowOverLifetime` needs `EmitterExtra W`.** Without it the "grow" updater shrinks particles to nothing.
- **`InheritVelocity`** is copied onto the emitter in this build, but nothing in the decompiled code reads it for a celestial-parented emitter; KSArmory's own notes agree that the plume trails because the emitter itself is moved every frame.
- **Density sets gravity.** KSA scales gravity by `1 - airDensity / Density`; a stage without a density falls at full gravity, so flames and smoke should carry sea-level air density (1.225), and below 100 Pa there is no air at all.

## See also

- [Overview](overview.md)
- [Approach 2](spawn-particle-effects-2.md), [approach 3](spawn-particle-effects-3.md)
- [Mod-authored particle emitters taken from KSA's shared pool and re-anchored each frame](../../../../mods/ksarmory/features/pooled-particle-emitters.md)
- [Draw volumetric exhaust plumes and smoke trails without a real engine](../draw-exhaust-plumes-without-an-engine/overview.md)
- [Choose a render frame and anchor effects to bodies, parts and terrain](../../overlays/choose-a-render-frame-and-anchor-effects/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
