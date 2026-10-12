---
type: How-To
title: Build a ParticleEmitterReference in C# with SpawnMode.Burst
description: Burst celebration or danger particles at a vessel or a kitten's face by taking one emitter from the pool, initializing it from a ParticleEmitterReference built in code, and attaching it with the vehicle's bubble origin
category: rendering
group: lights-and-particles
topic: spawn-particle-effects
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/face-particle-bursts]
techniques: [particle-emitter, starmap-lifecycle]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, starmap StarMapBeforeGui]
lifecycle: [frame:viewports, frame:draw-ui, frame:pre-render, render:particles]
game_types:
  - KSA.Rendering.Particles.ParticleSystem
  - KSA.Rendering.Particles.ParticleEmitter
  - KSA.Rendering.Particles.ParticleEmitterReference
  - KSA.Rendering.Particles.EmitterSpawnMode
  - KSA.Vehicle
  - KSA.KittenEva
  - KSA.GameSettings
tags: [particles, burst, emitter-reference, code-built, vehicle-attached, kitten]
---
Outcome: celebration and trouble effects (confetti, sparkle, fire flash) bursting in front of an EVA kitten's face or at any vessel, with no XML and no asset registration. It is the path the game's own particle debug editor uses: borrow an emitter from the pool and initialize it from a template object built in code.

Demonstrated by gatOS' `/sim/debug/fx` ([Particle bursts from the emitter pool](../../../../mods/gatos/features/face-particle-bursts.md)). Prerequisites: StarMap (or any main-thread command drain). Compare [approach 1](spawn-particle-effects-1.md) for XML assets and celestial-anchored, mod-simulated effects.

## Background: how the game does it

Three tiers of control exist: reuse a built-in template by id (`GetAndInitializeEmitters`), ship custom `ParticleEmitterReference` XML, or build the template in C#. `ParticleSystem.InitializeEmitter(emitter, reference)` applies a reference to a pooled emitter, registers a renderer by `reference.Renderer` and compute updaters by `reference.Updaters`:

```csharp
// current/decomp/KSA.Rendering.Particles/ParticleSystem.cs L171-L196
public void InitializeEmitter(ParticleEmitter<TUpdate, TRender> emitter, ParticleEmitterReference reference)
{
	reference.ApplyTo(emitter);
	switch (reference.Renderer)
	{
	case ParticleEmitterReference.ParticleRenderer.SimpleColor:
		emitter.RegisterRenderer(SimpleColorRenderer);
		break;
	case ParticleEmitterReference.ParticleRenderer.Pbr:
		emitter.RegisterRenderer(PbrRenderer);
		break;
	case ParticleEmitterReference.ParticleRenderer.Billboard:
		emitter.RegisterRenderer(BillboardRenderer);
		break;
	case ParticleEmitterReference.ParticleRenderer.Volumetric:
		emitter.RegisterRenderer(VolumetricRenderer);
		break;
	case ParticleEmitterReference.ParticleRenderer.PlanetColor:
		emitter.RegisterRenderer(PlanetColorRenderer);
		break;
	case ParticleEmitterReference.ParticleRenderer.PbrNoCull:
		emitter.RegisterRenderer(PbrNoCullRenderer);
		break;
	default:
		throw new ArgumentOutOfRangeException("ParticleSystem: Invalid renderer type: Renderer");
	}
```

`ParticleEmitterReference` has a public parameterless constructor, public fields and nested enums (shape, spawn type, renderer, updater), and `EmitterSpawnMode` is `None`, `Burst`, `OverTime`, `Endless`; only `Endless` can leak a slot, because a `Burst` completes its own simulation and the slot returns to the pool once every particle has decayed. Particle gravity is density-based (see approach 1).

## Lifecycle and timing

- Spawns run in a frame-phase command drain on the main thread (a prefix on the main viewport's frame, or `StarMapBeforeGui`: `frame:viewports` or `frame:draw-ui`), before `OnPreRender` ticks emitters later that frame, so a burst starts the same frame.
- Per frame, `Program.OnPreRender` calls `vehicle.SpawnParticles(dt)` for each in-frame vehicle, then `ExplosionSystem.Update` and `ParticleSystem.UpdateEmitters(dt)`, with `dt` from `Universe.GetNextSimStep()` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2408-L2418)); the GPU update and draws are recorded in `RenderGame` after the opaque scene pass (`render:particles`).
- The handle sweep runs on the same thread. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence), rows 21 and 22, and the RenderGame table row 10.

## Steps

1. **Refuse when particles are off** (`GameSettings.Current.Graphics.Particles`): an emitter acquired then never ticks and its slot leaks. Also keep your own small cap on live effects.

2. **Acquire one emitter from the pool**; if it is empty return "busy" instead of queueing, so you never starve the game's own effects.

3. **Initialize from a code-built template**, then place it. `LocalOffset` is a translation in the vessel's assembly frame (a kitten's face is about `(0.25, 0, -0.70)`), `Context.Vehicle` and `Context.Astronomical` are the vessel, and `Origin = vehicle.BubbleOrigin` (required, or nothing renders). The engine re-derives the emitter transform from the vehicle every frame. Add the handle to the vessel so already-spawned particles stay correct across floating-origin shifts:

   ```csharp
// gatOS.GameMod/Game/Ksa/Fx/FaceFxManager.cs L99-L130
internal CommandResult Spawn(Vehicle vehicle, string profile, double scale, double3? offsetAsmb)
{
    if (!FaceFxRules.TryParseProfile(profile, out var canonical))
        return new CommandResult(CommandOutcome.Invalid, $"unknown fx profile '{profile}'");
    if (!GameSettings.Current.Graphics.Particles)
        return new CommandResult(CommandOutcome.Unsupported,
            "particles are disabled in the game's graphics settings");

    Sweep();
    if (_live.Count >= MaxLive)
        return new CommandResult(CommandOutcome.Busy,
            $"fx cap reached ({MaxLive} live effects); wait for bursts to finish or write clear");

    var particleSystem = Program.Instance.ParticleSystem;
    if (!particleSystem.EmitterPool.Get(1, out var emitters) || emitters is not [{ } emitter])
        return new CommandResult(CommandOutcome.Busy,
            "the game's particle pool has no free emitters; try again shortly");

    var s = (float)scale;
    particleSystem.InitializeEmitter(emitter, BuildProfile(canonical, s));

    var anchor = offsetAsmb ?? (vehicle is KittenEva ? KittenFaceAsmb : double3.Zero);
    emitter.LocalOffset = float4x4.CreateTranslation(float3.Pack(anchor));
    emitter.Context.Vehicle = vehicle;
    emitter.Context.Astronomical = vehicle;
    emitter.Origin = vehicle.BubbleOrigin;

    var handle = emitter.CreateHandle();
    vehicle.AddEmitter(handle);
    _live.Add(handle);
    return CommandResult.Ok;
}
```

4. **Build the template** with `SpawnMode.Burst`, a built-in mesh id (`Plane`, `ParticleSphere`), the `SimpleColor` renderer, HDR colours (components above 1 bloom; the engine's thruster sparks are 15, 11, 6) and the updaters you need:

   ```csharp
// gatOS.GameMod/Game/Ksa/Fx/FaceFxManager.cs L176-L206
// Confetti: flat Plane chips, wide hue spread, tumbling fast, settling at 12% local gravity at
// reference atmospheric density. KSA recalculates this from local atmosphere each update.
"party" => new ParticleEmitterReference
{
    MaxParticles = new IntegerReference(96),
    Mesh = new ParticleMeshReference { Id = "Plane" },
    Renderer = ParticleEmitterReference.ParticleRenderer.SimpleColor,
    SpawnMode = EmitterSpawnMode.Burst,
    SpawnRate = new FloatReference(96f),
    EmitterSpawnLogic = ParticleEmitterReference.ParticleEmitterShape.Sphere,
    EmitterSize = new FloatReference(0.05f * scale),
    ParticleSpawnLogic = ParticleEmitterReference.ParticleSpawnType.ScatterFromCenter,
    ParticleColor = new Vector4Reference(4f, 2f, 8f, 1f),
    ParticleColorShift = new Vector3Reference(new float3(0.9f, 0.5f, 0.2f)),
    ParticleLifespan = new Vector2Reference(0.9f, 1.9f),
    ParticleSize = new Vector2Reference(0.008f * scale, 0.016f * scale),
    ParticleVelocity = new Vector3Reference(new float3(0.9f * scale)),
    ParticleVelocityShift = new FloatReference(0.45f),
    ParticleAngularVelocity = new Vector3Reference(new float3(12f)),
    ParticleExtra = new Vector4Reference(0.6f, 0f, 0f, 0f),
    Density = ReferenceAtmosphericDensity / (1f - 0.12f),
    Drag = 0f,
    Updaters =
    {
        ParticleEmitterReference.ParticleUpdater.SimpleMovement,
        ParticleEmitterReference.ParticleUpdater.HueShift,
        ParticleEmitterReference.ParticleUpdater.ShrinkOverLifetime,
        ParticleEmitterReference.ParticleUpdater.ModelMatrix,
        ParticleEmitterReference.ParticleUpdater.Age,
    },
},
```

   `MaxParticles` of at least 4 (the getter divides by the particle-quality setting). `Density` and `Drag` are native atmosphere-dependent particle fields; below 100 Pa KSA uses full local gravity.

5. **Track handles, sweep, cut short.** Keep only handles; each sample drop the invalid ones (`IsValid` goes false once the pool has recycled the slot). To stop early without `Kill()`'s abrupt vanish, call `ForceSpawningComplete()`: existing particles live out their lifespan:

   ```csharp
// gatOS.GameMod/Game/Ksa/Fx/FaceFxManager.cs L132-L162
/// <summary>
///     <c>debug.fx_clear</c> and the unload teardown: stop every gatOS effect now. Burst emitters
///     self-retire anyway; this just cuts them short (existing particles live out their lifespan —
///     the graceful stop, not <c>Kill()</c>'s abrupt vanish).
/// </summary>
internal CommandResult Clear()
{
    foreach (var handle in _live)
        try
        {
            handle.TryGet()?.ForceSpawningComplete();
        }
        catch
        {
            // A vehicle torn down mid-frame has already taken its emitters with it.
        }

    _live.Clear();
    return CommandResult.Ok;
}

/// <summary>Unload teardown — identical to clear; named for the Mod teardown call site.</summary>
internal void Shutdown() => Clear();

/// <summary>Drops handles whose burst has completed and been recycled by the pool.</summary>
private void Sweep()
{
    for (var i = _live.Count - 1; i >= 0; i--)
        if (!_live[i].IsValid)
            _live.RemoveAt(i);
}
```

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Fx/FaceFxManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Fx/FaceFxManager.cs) | Pool acquire, C# template, placement, handle tracking |
| [gatOS.SimFs/Fx/FaceFxRules.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Fx/FaceFxRules.cs) | Profile names and validation |
| [docs/PARTICLE_ANALYSIS.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/docs/PARTICLE_ANALYSIS.md) | The engine analysis (some call-site names are from older builds) |

## Variations and gotchas

- **Never use `Endless` for fire-and-forget effects**; their slots never return unless you `Kill()` them.
- **Do not acquire while the particles setting is off.**
- **Origin is required.** Without `Origin = vehicle.BubbleOrigin` the emitter never renders.
- **Long-lived modulated emitters exist in the game** (`Vehicle.UpdateIceDebrisParticles` adjusts tank frost emitters each frame); use them as a model for emitters you keep and tune.
- **Gravity changed to density and buoyancy** in a past build; profiles calibrate buoyancy against a sea-level reference density.
- **Frame of reference.** This anchors to a vehicle (assembly frame). For a point in the world not tied to a vehicle, use the body-frame anchoring of approach 1.

## See also

- [Overview](overview.md)
- [Approach 1](spawn-particle-effects-1.md), [approach 3](spawn-particle-effects-3.md)
- [Particle bursts from the emitter pool](../../../../mods/gatos/features/face-particle-bursts.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
