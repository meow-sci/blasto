---
type: How-To
title: Pooled emitters re-anchored to the celestial's rotating frame every frame
description: Take pooled particle emitters, point their Origin at a body-fixed BubbleOrigin each frame, and always Kill them before giving them back
category: rendering
group: overlays
topic: choose-a-render-frame-and-anchor-effects
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/pooled-particle-emitters]
techniques: [emitter-reanchoring]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapUnload]
lifecycle: [startup:asset-load, frame:draw-ui, frame:celestials, frame:pre-render]
game_types: [KSA.ParticleEmitter, KSA.Celestial]
tags: [particles, emitters, bubbleorigin, ccf, pool, anchor]
---

Outcome: held particle effects (a rocket plume at a mod-simulated missile's nozzle, muzzle flash, flares) follow a moving mod-simulated object and stay on a rotating planet. KSA cannot follow an object the mod simulates, so the mod re-anchors every held emitter each frame
with a `BubbleOrigin` in the body's frame, caps how many it holds, and always `Kill()`s an endless emitter before giving it back. Demonstrated by KSArmory ([Mod-authored particle emitters taken from KSA's shared pool and re-anchored each frame](../../../../mods/ksarmory/features/pooled-particle-emitters.md)). Prerequisites: StarMap; the effects are ordinary `<ParticleEmitter>` assets.

## Background: how the game does it

The engine's emitters live in one fixed pool shared by the whole game. `ParticleSystem.GetAndInitializeEmitters(string modLibraryId, out List<...Handle>)` [current/decomp/KSA.Rendering.Particles/ParticleSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/ParticleSystem.cs#L259) hands out every emitter of an Id's tree or nothing;
`ParticleEmitter.Origin` is a `BubbleOrigin?` [current/decomp/KSA.Rendering.Particles/ParticleEmitter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/ParticleEmitter.cs#L113), and `UpdateUniforms` builds the model matrix from it ([current/decomp/KSA.Rendering.Particles/ParticleEmitter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/ParticleEmitter.cs#L370-L402)); `Kill()` [current/decomp/KSA.Rendering.Particles/ParticleEmitter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/ParticleEmitter.cs#L652) stops spawning.
`Celestial.AddEmitter` / `RemoveEmitter` [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L252-L260) only maintain the body's list; `RemoveEmitter` is not a stop. Particles are updated in `OnPreRender` (`ExplosionSystem.Update`, `ParticleSystem.UpdateEmitters`) and stepped on the GPU in `RenderGame`.

## Lifecycle and timing

| Point | What happens |
|---|---|
| Asset load | Emitter definitions resolved through `ModLibrary` from `mod.toml` `assets` (`startup:asset-load`) |
| Mod writes | Take, point, tune and give, all inside the step's tail from `[StarMapAfterGui]` (`frame:draw-ui`) or the `OnFrameCelestials` prefix (`frame:celestials`) |
| Engine spawns | `OnPreRender` (`frame:pre-render`): `ExplosionSystem.Update`, `ParticleSystem.UpdateEmitters(nextSimDelta)` |
| GPU step | `ParticleSystem.UpdateParticles` inside `RenderGame` (`render:particles`) |

Timing asymmetry: the mod writes an anchor at `Universe.GetElapsedTime()` (the end of the step applied this frame) while the engine spawns and moves particles by `GetNextSimStep().DeltaTime`. At a steady warp they are equal; on a speed change or the first frame of a pause the emitter moves by one interval
while its particles are spawned over the other, a one-frame spacing glitch in a trail. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Take.** Fetch the whole tree, set `Context.Astronomical = body`, null the vehicle and part, and add each handle to the body:

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

2. **Point every frame.** Convert the world position to the body's rotating frame (`(pointEcl - body.GetPositionEcl()).Transform(body.GetCce2Ccf())`) and assign a `BubbleOrigin` in `Ccf` to every emitter of the set; pass the local (not ecliptic) velocity as `VelocityBub`:

   ```csharp
   // src/KSArmory/Ksa/EmitterPool.cs L42-L63

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

3. **Give.** `Kill()` first, then `RemoveEmitter`, each in its own `try`:

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

4. **Anchor a plume where the body is drawn,** moved half a body length back along the local velocity to the nozzle, not at the simulated position.

5. **Own and sweep.** Key held sets by round and record the owning system, sweep sets whose owner the roster no longer knows each frame, and release everything on `[StarMapUnload]`. Hold one set per system for fast guns (a CIWS at 75 rounds a second would drain the pool in a second).

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/EmitterPool.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/EmitterPool.cs) | Take, point, give |
| [src/KSArmory/KSArmoryParticles.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryParticles.xml) | `<ParticleEmitter>` assets |

## Variations and gotchas

- **`RemoveEmitter` is not a stop.** `UpdateEmitters` iterates the whole pool; an `Endless` emitter is reclaimed only after `HasCompletedSimulation`, which needs `Kill()`.
- **The pool is all-or-nothing and shared** with KSA's own explosions: handle a `false` result, cap held sets (flares at 12), never take one per gun round. One Id costs one slot per node of its tree.
- **`Ccf` ignores `LocalOffset`:** the model matrix is built from the origin alone.
- **`InheritVelocity`** is copied onto the emitter but nothing in this build reads it for a celestial parent; the plume trails because the emitter itself is moved every frame.
- **Particles setting off means nothing draws;** check `GameSettings.Current.Graphics.Particles` before throwing bursts.
- Children must be nested inline in the XML; a by-Id child from a mod threw "Invalid renderer type".

## See also

- [Overview](overview.md)
- [Approach 3: explosion presets with a body-frame origin](choose-a-render-frame-and-anchor-effects-3.md)
- [Mod-authored particle emitters taken from KSA's shared pool and re-anchored each frame](../../../../mods/ksarmory/features/pooled-particle-emitters.md)
- [Spawn particle effects and explosions from a mod](../../lights-and-particles/spawn-particle-effects/overview.md)
