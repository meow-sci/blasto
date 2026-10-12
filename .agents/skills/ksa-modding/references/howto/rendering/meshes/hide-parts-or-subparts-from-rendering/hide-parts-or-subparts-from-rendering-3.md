---
type: How-To
title: Pose or hide subparts from StarMapAfterGui with an AfterOnFrame fallback
description: Write a subpart's parent-relative transform every frame from a StarMap hook, resolve subparts by instance Id suffix, hide by 0.001 scale, and latch the work so it also runs when the HUD is hidden
category: rendering
group: meshes
topic: hide-parts-or-subparts-from-rendering
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/pose-subparts-at-runtime]
techniques: [starmap-lifecycle, scale-to-hide]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:after-on-frame]
game_types: [KSA.Part, KSA.Vehicle, KSA.Program, KSA.Universe]
tags: [subpart, pose, scale, hud-hidden, frame-latch, wheels]
---
Outcome: every moving body of a vessel (wheels, suspension arms, a steering wheel, iris blades) is its own `<SubPart>` whose transform your mod rewrites each frame from simulation state, and spare bodies are hidden by shrinking them inside the hull. The work runs whether or not the player has hidden the HUD, exactly once per frame.

Demonstrated by KSACars ([Pose subparts at runtime](../../../../mods/ksacars/features/pose-subparts-at-runtime.md)). Prerequisites: StarMap only. For declaring the subparts in XML and for animation design see the assets topic on defining and animating subparts (not yet written).

## Background: how the game does it

KSA parts have no animation hook a mod can drive, but every subpart is a `Part` with public, settable `PositionParentAsmb`, `Asmb2ParentAsmb` and `Scale` (all in the parent part's frame):

```csharp
// current/decomp/KSA/Part.cs L756-L783
public double3 PositionParentAsmb
{
	[CompilerGenerated]
	get
	{
		return _003CPositionParentAsmb_003Ek__BackingField;
	}
	set
	{
		_003CPositionParentAsmb_003Ek__BackingField = value;
		ResetCachedPosMatrixValues();
	}
}

public doubleQuat Asmb2ParentAsmb
{
	[CompilerGenerated]
	get
	{
		return _003CAsmb2ParentAsmb_003Ek__BackingField;
	}
	set
	{
		_003CAsmb2ParentAsmb_003Ek__BackingField = value;
		ResetCachedPosMatrixValues();
	}
}
```

Each setter invalidates cached matrices through `ResetCachedPosMatrixValues` ([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1241-L1249)). The renderer picks the new pose up when `PartTreeRenderData` repacks dirty transforms during `UpdateRenderData` later in the same frame. A subpart has no "is drawn" switch, so scale it to 0.001 (inside the hull) to hide it.

## Lifecycle and timing

`KSACarsMod.StepOnce` (find the cars, pose them, update sounds) is called from two StarMap hooks, and a latch makes only the first caller each frame run it:

| Hook | StarMap patch | Frame position | Why |
|---|---|---|---|
| `[StarMapAfterGui]` (preferred) | postfix on `Program.OnDrawUiViewports` | after `OnFrameViewports` posed this frame's camera, before `OnPreRender` and `Render` | the pose is drawn this frame |
| `[StarMapAfterOnFrame]` (fallback) | postfix on `Program.OnFrame` | after `Render` and `PostRender` | always called; poses a frame late |

`Program.OnFrame` calls `OnDrawUiFrame` and `OnDrawUiViewports` only inside `if (DrawUI)`, so with the UI hidden (F2, or a screenshot capture that clears `DrawUI`) the GUI hook never fires. See rows 12 to 15 and 24 of [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence); StarMap's postfixes are in [StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L26-L48).

Both hooks run on the main thread while the vehicle solver job dispatched earlier in `PrepareFrame` is running. The posed subparts carry no colliders or mass (the car's colliders are declared on the whole part), so moving them does not feed the physics step. Posing from `AfterOnFrame` alone would draw every pose one frame late, which at speed shows as wheels trailing the body. KSArmory solves the same problem with a prefix on `Program.OnFrameCelestials` instead of the late fallback (see [approach 2](hide-parts-or-subparts-from-rendering-2.md)).

## Steps

1. **Declare each moving body as a `<SubPart>`** with its own instance Id in the car's `<Part>` XML (mesh exported recentred on its pivot, so a rotation turns it about the right point). A shipped part's subpart list is append-only; saves pair subparts positionally.

2. **Resolve bodies once per car by instance Id suffix.** `Part.SubParts[i].Id` is the Id the `<Part>` gives the instance (for example `KSACars_Buggy_WheelFL`), not the template Id. Match with `EndsWith`; a body never found is simply never moved, with no log:

   ```csharp
// src/KSACars/Ksa/Buggies.cs L497-L513
// Matched on the Id the <Part> gives the subpart, which ends in the corner's key.
private static Part? SubPart(Part part, string suffix)
{
    try
    {
        ReadOnlySpan<Part> subs = part.SubParts;
        for (int i = 0; i < subs.Length; i++)
        {
            if (subs[i] is { } sub && sub.Id is { } id && id.EndsWith(suffix, StringComparison.Ordinal)) return sub;
        }
    }
    catch
    {
        // Unresolved subparts are simply not posed.
    }
    return null;
}
```

3. **One placement helper.** Write the three properties, skip non-finite input, and optionally set scale. The explicit reset is cheap insurance:

   ```csharp
// src/KSACars/Ksa/Buggies.cs L867-L877
private static void Place(Part? body, double3 position, doubleQuat rotation, double3? scale)
{
    if (body is null || !Vec.IsFinite(position)) return;

    body.PositionParentAsmb = position;
    body.Asmb2ParentAsmb = rotation;
    if (scale is { } s) body.Scale = s;

    // Part caches its matrices; without this the new value is stored and ignored.
    body.ResetCachedPosMatrixValues();
}
```

4. **Pose from state.** Arms rotate about their bushing, the wheel hub follows the arm and spins, the coil-over is squashed along its own mesh X so it shortens in bump:

   ```csharp
// src/KSACars/Ksa/Buggies.cs L784-L800
private static void Pose(Entry e, double dt)
{
    BuggyDrive d = e.Drive;
    BuggyCorner[] corners = d.Profile.Corners;
    for (int i = 0; i < corners.Length; i++)
    {
        BuggyCorner corner = corners[i];
        double angle = BuggyDrive.ArmAngle(corner, d.Travel[i]);
        doubleQuat armTurn = doubleQuat.CreateFromAxisAngle(new double3(0, 0, 1), angle);

        Place(e.Arms[i], corner.ArmPivot, armTurn, null);
        Place(e.Wheels[i], BuggyDrive.OnArm(corner, corner.Hub, angle),
              BuggyDrive.WheelRotation(d.Spin[i], corner.Steers ? d.SteerAngle : 0.0), null);

        (doubleQuat coil, double3 squash) = BuggyDrive.CoilPose(corner, angle);
        Place(e.Coils[i], corner.CoilTop, coil, squash);
    }
```

5. **Hide spare bodies by scale.** Scoop blades are posed at their declared place at scale 1 when chosen, otherwise tucked inside the hull at 0.001:

   ```csharp
// src/KSACars/Ksa/Buggies.cs L826-L832
// Hidden by being shrunk to nothing inside the hull: a subpart has no switch for being drawn.
for (int k = 0; k < d.Profile.Scoops.Length; k++)
{
    bool on = k == e.Scoop;
    Place(e.ScoopBlades[k], on ? Vec.Zero : d.Profile.Scoops[k].Stowed, doubleQuat.Identity,
          on ? new double3(1, 1, 1) : new double3(0.001, 0.001, 0.001));
}
```

6. **Latch the work so it runs once per frame from either hook.** `Claim()` returns true to the first asker after each `EndFrame()`:

   ```csharp
// src/KSACars/Sim/FrameLatch.cs L15-L36
internal sealed class FrameLatch
{
    private bool _taken;

    /// <summary>Whether the frame's work has already been claimed.</summary>
    public bool Claimed => _taken;

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
}
```

7. **Wire both hooks, and release the latch where it cannot be skipped.** `EndFrame()` goes in a `finally` on the frame postfix, the one hook KSA always calls: running the step twice costs a duplicated frame, but failing to release the latch would stop the mod for the session.

   ```csharp
// src/KSACars/Ksa/KSACarsMod.cs L59-L93
[StarMapAfterOnFrame]
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

// The GUI pass is the preferred hook because it runs before the render, so the wheels are drawn
// where this frame put them; F2 hides the UI and skips that pass, and the frame postfix then
// poses them a frame late rather than not at all.
[StarMapAfterGui]
public void OnAfterGui(double dt)
{
    try
    {
        if (_disabled) return;
        StepOnce();
```

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

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L478-L513) | `Resolve` and `SubPart`: find bodies by instance Id suffix |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L760-L877) | `Pose` and `Place` |
| [src/KSACars/Sim/BuggyDrive.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/BuggyDrive.cs#L329-L370) | Pose maths: `ArmAngle`, `OnArm`, `CoilPose`, `WheelRotation` |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L59-L130) | Hooks and `StepOnce` |
| [src/KSACars/Sim/FrameLatch.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/FrameLatch.cs) | Once-per-frame latch |

## Variations and gotchas

- **Instance Id, not template Id.** Match `Part.SubParts[i].Id` against the Id inside `<Part>`; the template Id would match nothing.
- **The GUI hook is skipped when the UI is hidden.** Anything that must happen every frame needs a fallback on `StarMapAfterOnFrame` plus a latch, or a pre-render prefix as in approach 2.
- **Scale is the only visibility switch.** A hidden subpart still exists, is still in the save and still costs a draw.
- **Animation time.** KSACars advances iris blades in world time: wall-clock milliseconds since the last pose, clamped to 0.1 s, times the simulation speed, so slow motion slows them and pause holds them.
- **Make the body's mass and colliders live on the whole part**, not the posed subpart, or moving it feeds the physics step.

## See also

- [Overview](overview.md)
- [Approach 1](hide-parts-or-subparts-from-rendering-1.md), [approach 2](hide-parts-or-subparts-from-rendering-2.md)
- [Pose subparts at runtime](../../../../mods/ksacars/features/pose-subparts-at-runtime.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
