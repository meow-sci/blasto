---
type: Mod Feature
title: Particle bursts from the emitter pool
description: Spawn one-shot particle effects anchored to a vessel or an EVA kitten's face by taking an emitter from KSA's shared pool and initializing it with a ParticleEmitterReference built entirely in C#, plus the engine facts from the repo's particle-system analysis
mod: gatos
category: rendering
techniques: [particle-emitter, command-queue]
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
  - KSA.Program
tags: [particles, vfx, emitters, kittens, pools, analysis]
---

`/sim/debug/fx` bursts celebration and trouble effects (`party`, `sparkle`, `danger`, `death`) in
front of an EVA kitten's face or at any vessel. It uses the same path as the game's particle debug
editor: borrow an emitter from the game's pool and initialize it from a template object built in
code, with no XML and no asset registration. The feature also carries the repo's analysis of KSA's
particle system, re-checked here against build 2026.10.10.5554.

## How it works

`FaceFxManager.Spawn` ([gatOS.GameMod/Game/Ksa/Fx/FaceFxManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Fx/FaceFxManager.cs#L99-L130)):

1. Refuse when the player has `GameSettings.Current.Graphics.Particles` off (an emitter acquired then
   would never tick and its slot would leak) and when gatOS already holds its own small cap of live
   effects.
2. `Program.Instance.ParticleSystem.EmitterPool.Get(1, out var emitters)`; if the shared pool has no
   free emitter, return `EBUSY` instead of queueing, so gatOS never starves the game's own effects.
3. `ParticleSystem.InitializeEmitter(emitter, BuildProfile(...))` with a `new ParticleEmitterReference { ... }`
   using the `SimpleColor` renderer and built-in meshes. Every profile uses `SpawnMode.Burst`, which
   self-completes and returns its slot.
4. Placement: `emitter.LocalOffset` is a translation in the vessel's assembly frame (a kitten's face is
   about `(0, 0, -0.70)`, the crew-portrait camera's face point), `Context.Vehicle` and
   `Context.Astronomical` are the vessel, and `Origin = vehicle.BubbleOrigin` (required or nothing
   renders). The engine re-derives the emitter transform from the vehicle every frame.
5. `vehicle.AddEmitter(emitter.CreateHandle())` so already-spawned particles stay correct across
   floating-origin shifts. gatOS keeps only handles, sweeps invalid ones each sample, and can stop its
   effects early with `ForceSpawningComplete()`.

## Engine findings from the particle analysis

From [docs/PARTICLE_ANALYSIS.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/docs/PARTICLE_ANALYSIS.md), verified against 5554 where noted:

- One singleton `Program.ParticleSystem` of type `ParticleSystem<ParticleUpdateData, ParticleRenderData>`,
  constructed as `new ParticleSystem<...>(1024, 512)` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1169-L1169)).
- Templates are `ParticleEmitterReference : SerializedId`, loaded from XML (`OnDataLoad`) and
  registered in `ModLibrary`; the stock ones live in `Content/Core/ParticleEmitterAssets.xml`.
  `ParticleSystem.GetAndInitializeEmitters(modLibraryId, out handles)` spawns by id
  ([current/decomp/KSA.Rendering.Particles/ParticleSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/ParticleSystem.cs#L171-L291)).
- `EmitterSpawnMode` is `None`, `Burst`, `OverTime`, `Endless`
  ([current/decomp/KSA.Rendering.Particles/EmitterSpawnMode.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/EmitterSpawnMode.cs)); only `Endless` can leak a slot.
- Per frame, `Program.OnPreRender` calls `vehicle.SpawnParticles(dt)` for each in-frame vehicle, then
  `ExplosionSystem.Update` and `ParticleSystem.UpdateEmitters(dt)`, with `dt` from
  `Universe.GetNextSimStep()` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2408-L2418)); the GPU update and
  draws are recorded in `RenderGame` after the opaque scene pass.
- Three tiers of control the analysis proposes: reuse a built-in template by id, ship custom
  `ParticleEmitterReference` XML, or build the template in C# (what gatOS does).
- Long-lived, runtime-modulated emitters exist in the game: `Vehicle.UpdateIceDebrisParticles` adjusts
  tank frost emitters each frame ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L5818-L5830)). Some call-site
  names and line numbers in the analysis (for example a `TrySpawnGroundImpact` method) were not found
  in 5554; treat its line references as from an older build.

## Lifecycle and timing

- Spawns run in the Frame-phase command drain on the main thread (main-viewport prefix or
  `StarMapBeforeGui`), before `OnPreRender` ticks emitters later that frame, so a burst starts the
  same frame.
- The handle sweep rides the telemetry sampler on the same thread.

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Fx/FaceFxManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Fx/FaceFxManager.cs) | pool acquire, C# template, placement, handle tracking |
| [gatOS.SimFs/Fx/FaceFxRules.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Fx/FaceFxRules.cs) | profile names and validation |
| [docs/PARTICLE_ANALYSIS.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/docs/PARTICLE_ANALYSIS.md) | the engine analysis |
| [examples/fx/README.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/examples/fx/README.md) | Rust CLIs that drive the surface |

## Snippets

```csharp
// gatOS.GameMod/Game/Ksa/Fx/FaceFxManager.cs L112-L129
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
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `ParticleSystem.EmitterPool`, `InitializeEmitter` | [current/decomp/KSA.Rendering.Particles/ParticleSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/ParticleSystem.cs#L75-L171) | pool and template init |
| `ParticleEmitterReference`, `SpawnMode` | [current/decomp/KSA.Rendering.Particles/ParticleEmitterReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/ParticleEmitterReference.cs#L8-L68) | template object |
| `ParticleEmitter.LocalOffset`, `Context`, `Origin`, `CreateHandle` | [current/decomp/KSA.Rendering.Particles/ParticleEmitter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/ParticleEmitter.cs) | placement and handle |
| `Vehicle.AddEmitter`, `BubbleOrigin` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L537-L700) | attach to the vessel |
| Stock templates | [current/Content/Core/ParticleEmitterAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/ParticleEmitterAssets.xml) | built-in effects and meshes |

## Pitfalls and version notes

- Never use `Endless` emitters for fire-and-forget effects; their slots never return.
- Do not acquire emitters while the particles graphics setting is off.
- Without `Origin = vehicle.BubbleOrigin` the emitter never renders (gatOS notes).
- Particle gravity changed to density and buoyancy at 5438 (gatOS notes); profiles calibrate
  buoyancy against a sea-level reference density.

## See also

- [FX editors via reflection](fx-editor-reflection.md)
- [Part light cone control](light-cone-control.md)
- [External API clients and SDK](external-api-clients.md)
- [gatOS overview](../overview.md)
- [Build a ParticleEmitterReference in C# with SpawnMode.Burst](../../../howto/rendering/lights-and-particles/spawn-particle-effects/spawn-particle-effects-2.md) - how-to article demonstrating this feature

