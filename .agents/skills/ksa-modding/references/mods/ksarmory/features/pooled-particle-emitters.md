---
type: Mod Feature
title: Mod-authored particle emitters taken from KSA's shared pool and re-anchored each frame
description: Plumes, muzzle flashes, flares, chaff, flak smoke and blast dust are the mod's own ParticleEmitter assets taken by Id from the engine's fixed pool, anchored to a celestial's rotating frame, re-pointed each frame while following, and always Kill()ed before return
mod: ksarmory
category: rendering
techniques: [xml-asset, pooled-resource, emitter-reanchoring, bounded-acquire]
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
  - KSA.ExplosionSystem
tags: [particles, emitter, pool, plume, muzzle-flash, flare, smoke, bubble-origin, xml-asset]
---

KSArmory draws a rocket flame at each burning missile's nozzle, a flash and smoke at gun muzzles,
white-hot flares trailing smoke, chaff puffs, black flak smoke where a shell burst, and dust thrown
off parts struck by a blast front. Every one is a stock-format `<ParticleEmitter>` asset in the
mod's own `KSArmoryParticles.xml`, fired through KSA's own particle system by Id, so the mod
writes no particle code or shaders for them.[^ksarmory-particles-xml]

The engine's emitters live in one fixed pool shared by the whole game. The mod's contribution is
the discipline around it: re-anchor a held emitter every frame because KSA cannot follow a
mod-simulated object, cap how many are held, and always `Kill()` an endless emitter before giving
it back, because an emitter that is only removed keeps spawning forever and never returns to the
pool.[^ksarmory-emitterpool]

## How it works

**Assets.** `KSArmoryParticles.xml` is listed in `mod.toml`'s `assets`; KSA maps `<ParticleEmitter>`
to `ParticleEmitterReference` (AssetBundle.cs L69), so `ModLibrary` resolves the mod's Ids exactly
like Core's.[^ksarmory-mod-toml][^ksa-assetbundle]

| Id | Spawn mode | Renderer | Driven by | Held for |
|---|---|---|---|---|
| `KSArmoryMotorPlume` | `Endless` | `SimpleColor` | `MotorPlume`, one per burning round | Burn time, re-pointed each frame |
| `KSArmoryMuzzleFlash` (+ inline smoke child) | `Endless` | `SimpleColor` + `Billboard` | `MuzzleFlash`, one set per barrel cluster | While firing plus 0.12 s after each shot |
| `KSArmoryFlare` | `Endless` | `SimpleColor` | `DecoyEffects`, at most `MaxLit = 12` | Until the flare is spent; size rewritten per frame |
| `KSArmoryChaffPuff` | `Burst` | `Billboard` | `DecoyEffects.Puff` | Fire and forget |
| `KSArmoryFlakPuff` | `Burst` | `Billboard` | `FlakPuff.Throw`, scaled by cube root of charge | Fire and forget |
| `KSArmoryBlastPuff` | `Burst` | `Billboard` | `BlastPuff.Throw`, force along the blast | Fire and forget |

Children are nested inline (`<ParticleEmitters>`), not by Id: a by-Id child from a mod threw
"Invalid renderer type". One Id therefore costs `GetTotalEmitterCount()` pool slots: one per node of
its tree (ParticleEmitterReference.cs L286-L297).[^ksa-particleemitterreference]

**The three steps** (`EmitterPool`, used by the held effects):

1. **Take.** `Program.Instance.ParticleSystem.GetAndInitializeEmitters(id, out handles)`; false or
   empty means the pool is short. For each handle: `Context.Astronomical = body`, `Context.Vehicle =
   null`, `Context.Part = null`, then `body.AddEmitter(handle)`.
2. **Point.** Build a `BubbleOrigin { Time = Universe.GetElapsedTime(), Parent = body, BubFrame =
   BubbleFrame.Ccf, PositionBub, VelocityBub }` and assign `emitter.Origin` on every emitter in the
   set. Positions are converted with `(pointEcl - body.GetPositionEcl()).Transform(body.GetCce2Ccf())`.
3. **Give.** `emitter.Kill()` **then** `body.RemoveEmitter(handle)`, each in its own `try`.

Fire-and-forget effects (`FlakPuff`, `BlastPuff`, chaff) follow the order KSA's own
`ExplosionSystem.FireStage` uses: acquire, set `LocalOffset`, context and `Origin`, tune size,
lifespan or force, then `AddEmitter` (ExplosionSystem.cs L479-L509). A `Burst` completes its own
simulation and is returned by the engine.[^ksa-explosionsystem]

**Ownership and leak sweeps.** `MotorPlume` keys held sets by round and records the owning system,
so one system's update never releases another's. Each frame `Sweep(roster)` releases sets whose
system the roster no longer knows (a craft destroyed mid-salvo, a roster discarded on save load),
and `ReleaseAll` runs on `[StarMapUnload]`.[^ksarmory-motorplume][^ksarmory-mod] `MuzzleFlash` holds
one set per *system* (not per round: a CIWS at 75 rounds a second would drain the pool in a second),
one per barrel cluster, and if it cannot get every cluster's set it gives back what it took.[^ksarmory-muzzleflash]

**Anchoring a plume.** The flame is placed where the round body is *drawn* (`TryRoundEffectEcl`),
moved back half a body length along the local velocity to the nozzle, converted to the body's
rotating frame, and the round's local (not ecliptic) velocity is passed as `VelocityBub`.

## Lifecycle and timing

| Point | What happens | Where (build 5554) |
|---|---|---|
| Asset load | Emitter definitions resolved through `ModLibrary` | `startup:asset-load`, from `mod.toml` |
| Mod writes | Take, point, tune and give, all inside `StepSimulation`'s tail (KSArmoryMod.cs L877-L879, L964-L971) | `[StarMapAfterGui]` (Program.cs L2268) or the `OnFrameCelestials` prefix (L2320)[^ksarmory-mod] |
| Engine spawns | `OnPreRender`: `float num = (float)Universe.GetNextSimStep().DeltaTime;` then `ExplosionSystem.Update(num)` and `ParticleSystem.UpdateEmitters(num)` | Program.cs L2409-L2417[^ksa-program] |
| Emitter update | `UpdateEmitters` walks every pool emitter; `ParticleEmitter.Update` sets elapsed time from `Universe.GetElapsedTime()`, rebuilds the model matrix from `Origin` (`UpdateUniforms`) and spawns `SpawnRate * nextSimDeltaTime` particles | ParticleSystem.cs L291-L301, ParticleEmitter.cs L370-L382, L525-L563[^ksa-particlesystem][^ksa-particleemitter] |
| GPU step | `ParticleSystem.UpdateParticles(cmd, (float)Universe.GetNextSimStep().DeltaTime, ...)` inside `RenderGame` | Program.cs L4850 |

**Timing asymmetry.** The anchor the mod writes is a position at `Universe.GetElapsedTime()`, the
end of the step applied this frame, and the mod advances its rounds by that same applied step
(`KsaWorld.ConsumeSimStep` reads `Universe.GetLastSimStep()`).[^ksarmory-ksaworld] The engine
spawns and moves the particles by `GetNextSimStep().DeltaTime`, the step the physics workers are
computing right now (`_nextSimStep` set in `ExecuteNextVehicleSolvers`, Universe.cs L2135).[^ksa-universe]
At a steady warp the two intervals are equal. On a frame where the speed changes, or the first
frame of a pause (the engine still applies one real step while the next one is zero), the emitter
moves by one interval while its particles are spawned and integrated over the other. The
engine's own vehicle emitters share the spawn and integration dt but take their anchor from
`Vehicle.BubbleOrigin` inside `UpdateUniforms` (ParticleEmitter.cs L414-L416), so only mod-anchored
emitters see the mismatch, and only as a one-frame spacing glitch in a trail.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/EmitterPool.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/EmitterPool.cs) | Take, point, give |
| [src/KSArmory/Ksa/MotorPlume.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/MotorPlume.cs) | Per-round held emitter, ownership, sweeps |
| [src/KSArmory/Ksa/MuzzleFlash.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/MuzzleFlash.cs) | Per-system, per-cluster held sets, all-or-nothing acquire |
| [src/KSArmory/Ksa/DecoyEffects.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/DecoyEffects.cs) | Capped flares, burst chaff |
| [src/KSArmory/Ksa/FlakPuff.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/FlakPuff.cs) | Burst tuned by charge, gated on altitude and pressure |
| [src/KSArmory/Ksa/BlastPuff.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BlastPuff.cs) | Burst with a body-frame force along the blast |
| [src/KSArmory/KSArmoryParticles.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryParticles.xml#L1-L40) | Emitter assets and the format notes at the top |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L1007-L1013) | Release everything on unload |
| [current/Content/Core/ParticleEmitterAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/ParticleEmitterAssets.xml) | Stock emitters the format was copied from |

## Snippets

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

```csharp
// src/KSArmory/Ksa/EmitterPool.cs L66-L83
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
// current/decomp/KSA.Rendering.Particles/ParticleEmitter.cs L370-L402
	public void Update(float nextSimDeltaTime)
	{
		if (!CanAcquireFromPool && !TryUnregisterEmitter())
		{
			if (!_simulationStarted)
			{
				_simulationStartTime = Universe.GetElapsedTime().Seconds();
				_simulationStarted = true;
			}
			_simulationTimeElapsed = Universe.GetElapsedTime().Seconds() - _simulationStartTime;
			UpdateUniforms();
			TrySpawnParticles(nextSimDeltaTime);
		}
	}

	public bool TryUnregisterEmitter()
	{
		if (!HasCompletedSimulation)
		{
			return false;
		}
		if (_pendingUnregisterFrames > 0)
		{
			_pendingUnregisterFrames--;
			if (_pendingUnregisterFrames == 0)
			{
				ResetEmitter();
			}
			return true;
		}
		_pendingUnregisterFrames = Program.GetRenderer().MaxFramesInFlight;
		return true;
	}
```

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `Program.ParticleSystem` (1024 emitters x 512 particles) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1169) | The shared pool |
| `ParticleSystem.GetAndInitializeEmitters(string, out List<Handle>)` | [current/decomp/KSA.Rendering.Particles/ParticleSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/ParticleSystem.cs#L259-L275) | Acquire a whole emitter tree by Id, or nothing |
| `ParticleSystem.UpdateEmitters`, `UpdateParticles` | [current/decomp/KSA.Rendering.Particles/ParticleSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/ParticleSystem.cs#L291-L353) | Engine side: spawn and integrate on next-step dt |
| `ParticleEmitter<,>.Handle.TryGet` | [current/decomp/KSA.Rendering.Particles/ParticleEmitter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/ParticleEmitter.cs#L53-L65) | Generation-checked access; null once reclaimed |
| `ParticleEmitter.Context`, `Origin`, `LocalOffset`, `EmitterSpawnInfo`, `ParticleInfo`, `Force`, `Opacity` | [current/decomp/KSA.Rendering.Particles/ParticleEmitter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/ParticleEmitter.cs#L67-L115) | Anchor and tune |
| `ParticleEmitter.Kill` | [current/decomp/KSA.Rendering.Particles/ParticleEmitter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/ParticleEmitter.cs#L652-L657) | Force spawning complete so the slot ages out and resets |
| `Celestial.AddEmitter`, `Celestial.RemoveEmitter` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L252-L260) | Bookkeeping list only |
| `BubbleOrigin` (`Time`, `Parent`, `BubFrame`, `PositionBub`, `VelocityBub`), `BubbleFrame.Ccf` | [current/decomp/KSA/BubbleOrigin.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BubbleOrigin.cs#L9-L29) | Body-fixed anchor |
| `Universe.GetElapsedTime`, `Universe.GetNextSimStep` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2387-L2399) | Anchor time vs particle dt |

## Pitfalls and version notes

- **`RemoveEmitter` is not a stop.** `Celestial.RemoveEmitter` only removes the handle from the
  body's list (Celestial.cs L257-L260); `UpdateEmitters` iterates the whole pool (ParticleSystem.cs
  L297-L300). An `Endless` emitter is only reclaimed after `HasCompletedSimulation`, which needs
  `_spawningComplete`, which only `Kill()` sets for that mode; `TryUnregisterEmitter` then waits
  `MaxFramesInFlight` frames and calls `ResetEmitter`.[^ksa-celestial][^ksa-particleemitter]
- **The pool is all-or-nothing and shared.** `EmitterPool.Get` (ParticleSystem.cs L24-L55) hands
  back nothing unless every emitter of the tree is free, and KSA's own explosions draw from the same
  1024. Handle `false`, cap held sets (flares at 12), and never take one per gun round.
- **Ccf ignores `LocalOffset`.** With `BubbleFrame.Ccf`, `UpdateUniforms` sets `GpuLocalOffset` to
  identity and builds the model matrix from the origin alone (ParticleEmitter.cs L454-L458).
- **Doc vs build 5554, `InheritVelocity`.** The header of `KSArmoryParticles.xml` (L36-L40) says
  `InheritVelocity` works from a celestial parent and is what keeps the plume with the round. In 5554
  the flag is copied onto the emitter (ParticleEmitterReference.cs L224) but nothing else in the
  decompiled code reads it, and `EmitterVelocity` is assigned only on the vehicle-parented,
  `EmitterRelative` path (ParticleEmitter.cs L422-L427). The repo's own `KSA-MODDING-NOTES.md`
  ("An emitter cannot throw particles in a direction of your choosing") agrees with the decomp: the
  plume trails because the emitter itself is moved every frame.[^ksarmory-particles-xml][^ksarmory-modding-notes]
- **Particles setting off means nothing draws.** `UpdateEmitters`, `UpdateRenderData` and
  `UpdateParticles` all return early when `GameSettings.Current.Graphics.Particles` is false; the
  mod checks the same flag (`Detonation.ParticlesEnabled`) before throwing bursts.
- **Fountain sprays one world direction.** Cone spawn is about the emitter's local +X, which for a
  body-fixed origin is a fixed axis of the planet; the mod uses radial spawn logics only.
- **`GrowOverLifetime` needs `EmitterExtra W`.** Without it the "grow" updater shrinks particles to
  nothing (from the XML header; the shader was not re-checked).

## See also

- [KSA explosions from a point](./ksa-explosions-from-a-point.md): the engine's own explosion presets the mod fires
- [Trail renderer smoke](./trail-renderer-smoke.md): the longer motor and flare smoke trails
- [Compute shell tracers](./compute-shell-tracers.md): why gun tracers are not particles
- [Countermeasure dispensers](./countermeasure-dispensers.md): the decoys these effects decorate
- [Blast damage and part health](./blast-damage-and-part-health.md): where blast dust is thrown from
- [Rounds drawn as hidden launcher subparts](./rounds-as-hidden-launcher-subparts.md): the body the plume is anchored to
- [Take XML-declared ParticleEmitter assets by id from the engine pool and give them back correctly](../../../howto/rendering/lights-and-particles/spawn-particle-effects/spawn-particle-effects-1.md) - how-to article demonstrating this feature
- [Pooled emitters re-anchored to the celestial's rotating frame every frame](../../../howto/rendering/overlays/choose-a-render-frame-and-anchor-effects/choose-a-render-frame-and-anchor-effects-4.md) - how-to article demonstrating this feature

[^ksarmory-emitterpool]: src/KSArmory/Ksa/EmitterPool.cs
[^ksarmory-motorplume]: src/KSArmory/Ksa/MotorPlume.cs
[^ksarmory-muzzleflash]: src/KSArmory/Ksa/MuzzleFlash.cs
[^ksarmory-particles-xml]: src/KSArmory/KSArmoryParticles.xml
[^ksarmory-mod-toml]: src/KSArmory/mod.toml
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-modding-notes]: docs/KSA-MODDING-NOTES.md
[^ksa-particlesystem]: KSA.Rendering.Particles.ParticleSystem (decompiled, build 2026.10.10.5554)
[^ksa-particleemitter]: KSA.Rendering.Particles.ParticleEmitter (decompiled, build 2026.10.10.5554)
[^ksa-particleemitterreference]: KSA.Rendering.Particles.ParticleEmitterReference (decompiled, build 2026.10.10.5554)
[^ksa-assetbundle]: KSA.AssetBundle (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-explosionsystem]: KSA.ExplosionSystem (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
