---
type: How-To
title: Stop channels on world reload with stopImmediate and treat unload-time StopAll as moot
description: Latch a save load with a postfix on Program.OnGameLoaded, stop held channels with stopImmediate on the next frame, and know that the StopAll in StarMapUnload runs after FMOD has been closed
category: audio
group: channels
topic: release-sound-channels-at-scene-change-and-exit
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksarmory/features/spatial-gun-and-motor-sounds
  - mods/ksarmory/features/burst-sound-at-blast-arrival
techniques: [starmap-lifecycle, harmony-postfix, fmod-audio]
hooks: [harmony-postfix KSA.Program.OnGameLoaded, starmap StarMapUnload]
lifecycle: [event:load, event:shutdown, frame:draw-ui]
game_types: [KSA.Program, KSA.GameAudio, KSA.ExplosionSoundSystem, KSA.IChannel, KSA.Universe]
tags: [audio, lifecycle, cleanup, save-load, harmony, stop-immediate]
---

Outcome: after the player loads a save, none of your sounds from the previous world are still playing; and your
`[StarMapUnload]` does not pretend to clean up audio. Prerequisites: StarMap, Harmony (for the reload latch), and
the channel tables from [hold a channel](../../playback/play-a-sound-at-a-world-position-and-hold-its-channel/play-a-sound-at-a-world-position-and-hold-its-channel-2.md).
Demonstrated by KSArmory, whose gun, motor and blast sounds are cleared by a world-reload hook.

## Background: how the game does it

Loading a save is `Universe.DeserializeSave`. It stops the simulation, waits for the job systems, destroys every
vehicle, and stops the engine's own explosion sounds. Nothing in it touches a mod's channels:

```csharp
// current/decomp/KSA/Universe.cs L2430-L2446
public static void DeserializeSave(UniverseData universeData)
{
	if (CurrentSystem == null)
	{
		throw new NullReferenceException("Current system is null");
	}
	SimulationSpeed = 0.0;
	JobSystems.WaitAll();
	Program.Instance.ParticleSystem.EmitterPool.ResetAll();
	Program.Instance.ClearPlumeTrails();
	ExplosionVolumeSystem.Clear();
	Program.EditorFlag = false;
	CurrentSystem.DestroyAllVehicles();
	DestroyAllPhysicsBubbles();
	_clutterExclusions.Clear();
	Program.GetPlanetRenderer().GroundClutterRenderer?.Reset();
	ExplosionSoundSystem.StopAll();
```

`ExplosionSoundSystem.StopAll` stops each tracked sound with `stopImmediate: true` and clears its list.[^ksa-explosionsoundsystem]
`UncompressedSave.Load` follows `DeserializeSave` with `Program.OnGameLoaded()`, which in this build is called from
that one place.[^ksa-uncompressedsave]

## Lifecycle and timing

| Moment | Where | Vocabulary |
|---|---|---|
| Save loaded | `Program.OnGameLoaded` postfix sets a flag | `event:load` |
| Next mod step | The flag is consumed at the top of the mod's per-frame step; channels are stopped there | `frame:draw-ui` (or the hidden-HUD fallbacks) |
| Exit | `[StarMapUnload]` after `Program.Dispose` has closed FMOD | `event:shutdown` |

KSArmory latches instead of acting inside the postfix: the load runs deep in the engine, and tearing the mod's state
down from under it is the kind of fault the mod avoids by queueing.[^ksarmory-worldreloadhook]

## Steps

1. **Hook the end of a load.** `Program.OnGameLoaded` is `public static`, so the mod pins it in its own metadata and a
   KSA change becomes a build error:

   ```csharp
   // src/KSArmory/Ksa/WorldReloadHook.cs L71-L86
   MethodInfo? target = AccessTools.Method(typeof(Program), nameof(Program.OnGameLoaded),
                                           Type.EmptyTypes);

   if (target is null)
   {
       Trouble = "KSA has no Program.OnGameLoaded() to hook";
       Log.Warn($"save loads cannot be seen: {Trouble} -- rounds in the air will outlive "
                + "a reload");
       return;
   }

   MethodInfo postfix = typeof(WorldReloadHook).GetMethod(
       nameof(AfterGameLoaded), BindingFlags.NonPublic | BindingFlags.Static)!;

   _harmony = new Harmony(HarmonyId);
   _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
   ```

   and the postfix only sets a flag, with a guard because it runs inside the engine's load path:

   ```csharp
   // src/KSArmory/Ksa/WorldReloadHook.cs L117-L123
   private static void AfterGameLoaded()
   {
       // Setting a bool cannot fail, but this runs in the engine's load path where an exception
       // is the game rather than a log line, and the guard costs nothing.
       try { _pending = true; }
       catch { }
   }
   ```

2. **Consume the flag before anything integrates.** The first thing the step does, so the previous world's systems get
   no further step:

   ```csharp
   // src/KSArmory/Ksa/KSArmoryMod.cs L332-L334
   // Before anything integrates: the roster still holds the previous world's systems, and one
   // more step of them is one more step of rounds that no longer have a world to fly in.
   if (WorldReloadHook.ConsumeReload()) ForgetTheWorld();
   ```

3. **Stop what the old world owned, immediately.** The blast sound clears its table with `stopImmediate: true`, the
   same flag the engine uses for its own, so a fade-out does not play on into the new scene:

   ```csharp
   // src/KSArmory/Ksa/BurstSound.cs L80-L92
   /// <summary>Forgets every front still on its way, for a scene that no longer contains them.</summary>
   public static void Clear()
   {
       _fronts.Clear();

       foreach ((IChannel channel, _, _) in _playing)
       {
           try { channel.Stop(stopImmediate: true); }
           catch { /* Gone with the scene. */ }
       }

       _playing.Clear();
   }
   ```

   (`NuclearClouds.Clear()`, called from the mod's `ForgetTheWorld`, reaches this.)[^ksarmory-ksarmorymod]

4. **Do not rely on `StarMapUnload`.** KSArmory's `Unload` calls `StopAll` on the gun and motor players, with the
   comment that pooled channels belong to the game and unloading while anything is burning "would keep them for the
   process":

   ```csharp
   // src/KSArmory/Ksa/KSArmoryMod.cs L1007-L1010
   // Pooled emitters and audio channels belong to the game and nothing else gives them
   // back: unloading while anything is burning or firing would keep them for the process.
   _motors?.StopAll();
   _gunSound?.StopAll();
   ```

   In this build that premise does not hold for FMOD: `Program.Dispose` has already called `GameAudio.Release()`
   (`System.Close()`, `System.Release()`), so these calls reach no live mixer. They are harmless when guarded; the
   mod's players catch around every channel call (`SoundChannels.Cut`).[^ksa-program][^ksa-gameaudio]

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/WorldReloadHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WorldReloadHook.cs) | Postfix on `Program.OnGameLoaded` and a consume-once flag |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L332-L334) | Consumes the flag, then `ForgetTheWorld` |
| [src/KSArmory/Ksa/BurstSound.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BurstSound.cs#L80-L92) | Clears fronts and stops channels immediately |
| [src/KSArmory/Ksa/GunSound.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/GunSound.cs#L86-L91) | `StopAll` on the gun players |

## Variations and gotchas

- **The mod's reload hook weakens, not breaks.** If the patch cannot apply, the mod logs once and only the stale
  rounds and sounds of one reload remain.
- **`Stop()` versus `Stop(stopImmediate: true)`.** Plain `Stop` runs the sound's `FadeOut` if it has one; use
  `stopImmediate` for a scene that is being replaced.
- **`StarMapUnload` does not clear the blast sound.** KSArmory's `Unload` never calls `NuclearClouds.Clear`, so a bang
  that is playing at exit would only end because FMOD is closed.
- **Alternative to Harmony.** If you cannot patch, watch for your sources' disposal (`Vehicle.IsDisposed`) each frame
  and stop on that, as article 2 does for scenes.

## See also

- [Overview of this topic](overview.md)
- [Approach 2: stop on scene exit and tolerate a dead audio system](release-sound-channels-at-scene-change-and-exit-2.md)
- [Approach 3: dispose all streams on save reset](release-sound-channels-at-scene-change-and-exit-3.md)
- [Play a sound at a world position and hold its spatial channel](../../playback/play-a-sound-at-a-world-position-and-hold-its-channel/overview.md)
- [Delay a world sound by the time sound takes to reach the listener](../../playback/delay-a-sound-until-the-blast-front-arrives/overview.md)
- Mod features: [spatial gun and motor sounds](../../../../mods/ksarmory/features/spatial-gun-and-motor-sounds.md), [blast-arrival sound](../../../../mods/ksarmory/features/burst-sound-at-blast-arrival.md)

[^ksa-explosionsoundsystem]: KSA.ExplosionSoundSystem (decompiled, build 2026.10.10.5554)
[^ksa-uncompressedsave]: KSA.UncompressedSave (decompiled, build 2026.10.10.5554)
[^ksarmory-worldreloadhook]: KSArmory: src/KSArmory/Ksa/WorldReloadHook.cs
[^ksarmory-ksarmorymod]: KSArmory: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
