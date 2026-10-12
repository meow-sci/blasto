---
type: Game Feature
title: Spawn particle effects and explosions from a mod
description: Take emitters from the engine's fixed pool, either from XML-declared ParticleEmitter assets or built in C#, anchor them, return them correctly, and fire the game's own explosion presets where no vehicle hosts the burst
category: rendering
group: lights-and-particles
topic: spawn-particle-effects
demonstrated_by:
  - mods/ksarmory/features/pooled-particle-emitters
  - mods/gatos/features/face-particle-bursts
  - mods/ksarmory/features/ksa-explosions-from-a-point
lifecycle: [startup:asset-load, frame:viewports, frame:pre-render, render:particles]
game_types:
  - KSA.Rendering.Particles.ParticleEmitter
  - KSA.Rendering.Particles.ParticleEmitterReference
  - KSA.Rendering.Particles.ParticleSystem
  - KSA.ExplosionSystem
  - KSA.BubbleOrigin
tags: [particles, emitters, explosions, pool, bubble-origin]
---
KSA's particle system is a single `ParticleSystem<ParticleUpdateData, ParticleRenderData>` constructed with 1024 emitters of 512 particles ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1169)). Everything that spawns particles in the game, from vehicle exhaust to explosions to ice debris, takes emitters from that one pool, so a mod's responsibilities are small but strict: acquire all-or-nothing, anchor correctly, tune, and return what you hold.

## Engine background

- **Emitter references.** `ParticleEmitterReference` [current/decomp/KSA.Rendering.Particles/ParticleEmitterReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/ParticleEmitterReference.cs#L8-L120) is both an XML asset (`<ParticleEmitter Id=...>`, registered in `ModLibrary`) and a plain object you can build in C#. Trees of nested emitters cost one pool slot per node.
- **Acquire and initialize.** `ParticleSystem.GetAndInitializeEmitters(id, out handles)` and `EmitterPool.Get(n, out emitters)` plus `InitializeEmitter(emitter, reference)` ([current/decomp/KSA.Rendering.Particles/ParticleSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/ParticleSystem.cs#L24-L54)). `EmitterSpawnMode` is `None`, `Burst`, `OverTime` or `Endless`; only `Endless` needs a `Kill()` before it is returned.
- **Anchoring.** `emitter.Origin` is a `BubbleOrigin`: a time, a parent, a frame (`Ccf` body-fixed) and a position and velocity. Vehicle-hosted effects take `vehicle.BubbleOrigin` and a `LocalOffset` in the assembly frame.
- **Timeline.** `Program.OnPreRender` spawns (`vehicle.SpawnParticles`, `ExplosionSystem.Update`, `ParticleSystem.UpdateEmitters`) with `dt = Universe.GetNextSimStep().DeltaTime` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2408-L2418)); the GPU update and draw are recorded in `RenderGame` (`render:particles`, rows 10 and 14 of the [RenderGame table](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence)). All of it is skipped if the player's particles graphics setting is off.
- **Explosions.** `ExplosionSystem.SpawnPreset(id, in ExplosionContext)` fires an `<Explosion>` preset (flash, sound, emitter and volume stages) at an anchor; main thread only.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: XML emitter assets by id](spawn-particle-effects-1.md) | KSArmory | `pooled-resource`: acquire, re-anchor each frame on a celestial, `Kill()` then give back | `startup:asset-load`, `frame:draw-ui`, `frame:pre-render` | Designer-friendly assets and held effects that follow a mod-simulated object; strict give-back discipline |
| [2: ParticleEmitterReference built in C#](spawn-particle-effects-2.md) | gatOS | `particle-emitter`: pool `Get(1)`, `InitializeEmitter`, vehicle `BubbleOrigin`, `Burst` | `frame:viewports`, `frame:pre-render` | No assets and self-returning bursts; vehicle-anchored only and look is hand-authored in code |
| [3: ExplosionSystem presets with a body-fixed origin](spawn-particle-effects-3.md) | KSArmory | `engine-explosion-preset`: `ExplosionContext` with `BubbleFrame.Ccf` anchor and real ambient pressure | `frame:draw-ui` | Full stock explosion with sound and volumes at any point; main thread only, preset chosen by size |

## See also

- [Draw volumetric exhaust plumes and smoke trails without a real engine](../draw-exhaust-plumes-without-an-engine/overview.md)
- [Choose a render frame and anchor effects to bodies, parts and terrain](../../overlays/choose-a-render-frame-and-anchor-effects/overview.md)
- [Add dynamic spot and point lights to the world from a mod](../add-dynamic-lights-to-the-world/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
