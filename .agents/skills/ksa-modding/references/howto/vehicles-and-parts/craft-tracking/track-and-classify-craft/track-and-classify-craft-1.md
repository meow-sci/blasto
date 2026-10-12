---
type: How-To
title: Sync a Vehicle-keyed roster of craft that carry your part, once per frame from two hooks
description: Enumerate Universe.CurrentSystem.All from StarMapAfterGui or StarMapAfterOnFrame behind a latch, add craft that carry the part and drop them when disposed or the part leaves
category: vehicles-and-parts
group: craft-tracking
topic: track-and-classify-craft
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/frame-step-latch-and-car-registry]
techniques: [starmap-lifecycle]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:after-on-frame]
game_types:
  - KSA.Vehicle
  - KSA.Universe
  - KSA.CelestialSystem
  - KSA.StructuralLoad
  - KSA.PhysicsBubble
  - KSA.Part
  - KSA.Program
tags: [roster, vehicles, latch, registry, hud-hidden]
---
Outcome: a dictionary of exactly the craft that currently carry one of your parts, refreshed once per frame from whichever of two StarMap hooks runs first, with entries dropped the moment the craft is disposed or the part leaves it (undocked, staged away, destroyed).
Every Harmony patch in KSACars then looks the vehicle up in that dictionary and returns at once if it is not a car.

Prerequisite: StarMap. Demonstrated by [ksacars/frame-step-latch-and-car-registry](../../../../mods/ksacars/features/frame-step-latch-and-car-registry.md).

## Background: how the game does it

`[StarMapAfterGui]` is StarMap's postfix on `Program.OnDrawUiViewports`, which only runs when the HUD is shown; `[StarMapAfterOnFrame]` is a postfix on `Program.OnFrame` that runs every frame, including frames where `PrepareFrame` returned `Exit`. A mod that wants per-frame work
before the render but must keep working with the HUD hidden has to use both, and make sure only one does the work ([overview of ways to keep drawing](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md)).
The vehicle list should come from `Universe.CurrentSystem.All`, not from `Program.VehiclesInFrame`, which is refilled in `PrepareFrame` and cleared later ([overview](overview.md)).

## Lifecycle and timing

Both hooks run on the main thread after `PrepareFrame` has applied the previous physics step and dispatched the next, so the next step is already in flight during the step: nothing in `StepOnce` writes vehicle state. Cars that appear this frame (spawned, loaded, undocked) are driven from the next frame's worker prefix on
([game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).

| Frame | `[StarMapAfterGui]` | `[StarMapAfterOnFrame]` |
|---|---|---|
| HUD shown | claims the latch and runs the step, then draws | no-op; releases the latch |
| HUD hidden | not called | claims, runs the step, releases (a frame late relative to the render) |
| `PrepareFrame` or `Render` returned `Exit` | skipped or called | called, releases |

## Steps

1. **Add a two-method latch.** `Claim()` returns true to the first caller after each `EndFrame()` and false to everyone else. Running the step twice costs a duplicated frame; a latch that never clears stops the mod for the session, so the release belongs on the hook that cannot be skipped:

```csharp
// src/KSACars/Sim/FrameLatch.cs L22-L35
/// <summary>
/// Whether this caller owns the frame's work. True to the first asker after each
/// <see cref="EndFrame"/>, and false to everyone after it.
/// </summary>
public bool Claim()
{
    if (_taken) return false;

    _taken = true;
    return true;
}

/// <summary>End the frame, whatever happened during it.</summary>
public void EndFrame() => _taken = false;
```

2. **Release in `finally` on the always-called hook, and let the GUI hook be the preferred one.**

```csharp
// src/KSACars/Ksa/KSACarsMod.cs L60-L82
public void OnAfterFrame(double currentPlayerTime, double dtPlayer)
{
    try
    {
        if (_disabled) return;

        // The fallback, and a no-op on every frame the GUI pass ran. See StepOnce.
        StepOnce();

        // After the step, so a command sees this frame's world.
        _bridge?.Update(dtPlayer, KsaWorld.InFlightScene && !KsaWorld.IsPaused ? KsaWorld.SimStepSeconds : 0.0);
    }
    catch (Exception e)
    {
        Fault("frame", e);
    }
    finally
    {
        // Unconditionally: this is the only hook KSA always calls, so a release skipped by an early
        // return or an exception would stop the mod for the rest of the session.
        _frame.EndFrame();
    }
}
```

3. **Enumerate vehicles from the system, once per pass.**

```csharp
// src/KSACars/Ksa/KsaWorld.cs L315-L332
// Universe.CurrentSystem.All rather than Program.VehiclesInFrame: the latter is a per-frame scratch
// buffer refilled at a point in the tick a StarMap hook does not line up with, and reads back empty
// from there. The system's collection is valid from any hook.
private static void CollectVehicles(List<Vehicle> into)
{
    into.Clear();

    try
    {
        if (Universe.CurrentSystem is { } system)
        {
            ReadOnlySpan<Astronomical> all = system.All.AsSpan();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] is Vehicle { IsDisposed: false } v) into.Add(v);
            }
        }
    }
```

4. **Sync the registry.** Each live vehicle not yet known whose part list contains a part whose `Id` equals a registered part Id is added with its resolved subparts; then every entry whose vehicle is disposed or no longer holds that part is dropped and dependent state is told to forget it:

```csharp
// src/KSACars/Ksa/KSACarsMod.cs L112-L130
private void StepOnce()
{
    if (!_frame.Claim()) return;

    if (!KsaWorld.InFlightScene)
    {
        _buggySound.StopAll();
    _rocketSound.StopAll();
        return;
    }

    // Every frame rather than on the clock: the springs step inside the engine's own physics
    // window, so this only has to find the cars and show where their wheels ended up.
    KsaWorld.InvalidateCensus();
    _buggies.Sync(KsaWorld.Vehicles);
    _buggies.Pose();
    _buggySound.Update(_buggies, 1f);
    _rocketSound.Update(_buggies);
}
```

```csharp
// src/KSACars/Ksa/Buggies.cs L84-L117
public void Sync(IReadOnlyList<Vehicle> vehicles)
{
    for (int i = 0; i < vehicles.Count; i++)
    {
        Vehicle v = vehicles[i];
        if (!KsaWorld.IsAlive(v) || Active.ContainsKey(v)) continue;
        if (Find(v) is not { } found) continue;

        Entry entry = new(v, found.Part, found.Profile);
        Resolve(entry);
        // A saved car comes back with its switch as it was left.
        if (found.Part.LightSwitch is { LightIsActive: true })
        {
            entry.Beam = BeamSetting.Low;
            entry.SwitchSeen = true;
        }
        Active[v] = entry;
        Log.Info($"{found.Profile.DisplayName} on {KsaWorld.DisplayName(v)}: "
                 + $"{entry.Wheels.Count(w => w is not null)} wheel(s) found");
    }

    List<Vehicle>? gone = null;
    foreach ((Vehicle v, Entry e) in Active)
    {
        if (!KsaWorld.IsAlive(v) || Find(v)?.Part != e.Part) (gone ??= []).Add(v);
    }
    if (gone is not null)
    {
        foreach (Vehicle v in gone)
        {
            Active.Remove(v);
            RailsHook.Forget(v);
        }
    }
```

5. **Find the part by Id, tolerating a rebuilding tree.**

```csharp
// src/KSACars/Ksa/Buggies.cs L457-L476
private static (Part Part, BuggyProfile Profile)? Find(Vehicle v)
{
    try
    {
        ReadOnlySpan<Part> parts = v.Parts.Parts;
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] is not { } part) continue;
            foreach (BuggyProfile profile in BuggyProfile.All)
            {
                if (part.Id == profile.PartId) return (part, profile);
            }
        }
    }
    catch
    {
        // Part tree can be mid-rebuild during staging or docking.
    }
    return null;
}
```

## Complete example

- [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L60-L130)[^ksacars-ksacarsmod] - both hooks and `StepOnce`.
- [src/KSACars/Sim/FrameLatch.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/FrameLatch.cs#L1-L36)[^ksacars-framelatch] (KSA-free, unit tested) and [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L84-L134)[^ksacars-buggies] (the registry).

## Variations and gotchas

- **Release the latch before any early return.** An exception or `_disabled` return must not leave it taken.
- **The fallback is a frame late.** From `StarMapAfterOnFrame` the poses are drawn by the next frame's render; gizmos submitted there are reset before they are drawn, which is why anything that draws gizmos belongs only in the GUI hook.
- **`VehiclesInFrame` depends on the hook.** Full in the UI pass, empty after `PostRender`.
- **Main-thread dictionary.** Readers on the worker (`Vehicle.PrepareWorker`-side patches) need their own concurrent structure.
- **Key by `Vehicle`, match the part by `Part.Id`.** If craft files may name part instances, `Part.Id` is that name ([Give inert parts behaviour by recognising their part Id](../give-inert-parts-behaviour-by-part-id/overview.md)).
- **Debris.** Part failure sheds fragments as real `Vehicle` objects flagged `IsDebris` in the same collection; filter them if you only want crewed craft.

## See also

- [Overview](overview.md); [Article 2](track-and-classify-craft-2.md)
- [ksacars/frame-step-latch-and-car-registry](../../../../mods/ksacars/features/frame-step-latch-and-car-registry.md)
- [Draw UI while the HUD is hidden](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md)

[^ksacars-ksacarsmod]: KSACars: src/KSACars/Ksa/KSACarsMod.cs
[^ksacars-framelatch]: KSACars: src/KSACars/Sim/FrameLatch.cs
[^ksacars-buggies]: KSACars: src/KSACars/Ksa/Buggies.cs
