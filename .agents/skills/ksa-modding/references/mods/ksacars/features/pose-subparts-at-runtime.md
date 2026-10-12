---
type: Mod Feature
title: Pose subparts at runtime
description: Animate wheels, suspension arms, squashed coil-overs, the steering wheel, iris blades and scoops by rewriting each subpart's parent-relative transform every frame from the GUI hook, with a frame-postfix fallback when the UI is hidden
mod: ksacars
category: rendering
techniques: [starmap-lifecycle]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:after-on-frame]
game_types: [KSA.Part, KSA.Vehicle, KSA.Program, KSA.Universe]
tags: [animation, subparts, transforms, wheels, steering-wheel, gui-hook, frame-latch, hidden-ui]
---

KSA parts have no runtime animation hook a mod can drive, but every subpart is a `Part` whose
position, rotation and scale relative to its parent are public and settable. KSACars declares each
moving body (four wheels, four trailing arms and coil-overs on the buggy, the steering wheel, the
Eldorado's iris blades and scoop blades) as its own `<SubPart>` and rewrites its transform every
frame from the physics state, then invalidates the part's cached matrices so the renderer picks the
new pose up.[^ksacars-buggies] The same trick hides a subpart: scale it to 0.001 inside the hull,
because "a subpart has no switch for being drawn".

## How it works

**Finding the bodies.** `Buggies.Resolve` looks up each body once per car by the **Id the `<Part>`
gives the subpart instance** (for example `KSACars_Buggy_WheelFL`), matched by suffix
(`<Prefix>WheelFL`, `ArmFL`, `CoilFL`, `Steering`, `Iris<Hatch><n>`, scoop suffixes). The template Id
(`KSACars_BuggyWheelFront_SubPart`) would match nothing; the mod's notes call this out because a
body that is never found is simply never moved, with no log.[^ksacars-notes][^ksacars-assets]

**Placing a body.** `Place(body, position, rotation, scale)` writes `Part.PositionParentAsmb`,
`Part.Asmb2ParentAsmb` and optionally `Part.Scale`, then calls `Part.ResetCachedPosMatrixValues()`,
which invalidates the tree's render transforms (`Tree.RenderData.InvalidateTransforms()`) and the
part's cached matrices; without it the new value is stored and ignored.[^ksa-part]

**What is posed, from where** (all values are the drive's last step, see
[wheel model](./wheel-suspension-tyre-model.md)):[^ksacars-buggydrive]

| Body | Pose |
|---|---|
| Trailing arm | rotated about part +Z by `BuggyDrive.ArmAngle(corner, travel)` around its bushing |
| Wheel | hub carried round the arm (`OnArm`), rotated by `WheelRotation(spin, steer)` (roll about the axle, then steer about up) |
| Coil-over | `CoilPose`: rotated so its bottom eye stays on the arm, and **squashed along its own mesh X** by scale so it shortens in bump |
| Steering wheel | rotated about `SteeringAxis` by `SteerAngle * SteeringRatio` around `SteeringPivot` |
| Iris hatch cup and six blades | cup faces the skin normal; each blade positioned, turned and scaled by `Hatch.Blade(hatch, b, open)` as the iris opens ([rockets](./rocket-lift-hover-boost-downforce.md)) |
| Scoop blade | at its declared place and scale 1 when chosen, else scale 0.001 tucked inside the hull ([scoops](./scoop-colliders-and-loose-clutter.md)) |

Meshes are exported recentred on their own pivot (wheel on its hub, arm on its bushing, coil on
its top mount, steering wheel on its boss), so a rotation in `Asmb2ParentAsmb` turns the body about
the right point.[^ksacars-assets]

**Hatch timing** advances in world time: wall-clock milliseconds since the last pose, clamped to
0.1 s, times `Universe.SimulationSpeed`, so slow motion slows the irises and pause holds them.[^ksacars-hatch]

## Lifecycle and timing

`KSACarsMod.StepOnce` (find the cars, `Buggies.Pose`, update engine and rocket sounds) is called
from two StarMap hooks, and a `FrameLatch` makes sure only the first caller each frame runs it:[^ksacars-mod][^ksacars-framelatch]

| Hook | StarMap patch | Frame position | Why |
|---|---|---|---|
| `[StarMapAfterGui]` (preferred) | postfix on `Program.OnDrawUiViewports` | after `OnFrameViewports` built this frame's camera, before `OnPreRender` and `Render` | the pose is drawn this frame |
| `[StarMapAfterOnFrame]` (fallback) | postfix on `Program.OnFrame` | after `Render` and `PostRender` | always called; poses a frame late |

`Program.OnFrame` only calls `OnDrawUiFrame` and `OnDrawUiViewports` inside `if (DrawUI)`, so with
the UI hidden (F2, or a screenshot capture that clears `DrawUI`) the GUI hook never fires.[^ksa-program][^starmap-programpatcher]
`FrameLatch.Claim()` returns true to the first hook after each `EndFrame()`, and `EndFrame()` sits
in a `finally` on the frame postfix, the one hook KSA always calls: running the step twice costs a
duplicated frame, but failing to release the latch would stop the mod for the session.[^ksacars-framelatch]

Both hooks run on the main thread while the vehicle solver job dispatched earlier in
`PrepareFrame` is running. The posed subparts carry no colliders or mass of their own (the car's
colliders are declared on the whole part), so moving them does not feed the physics step. The
mod's own analysis of why drawing work belongs in the GUI pass is in `docs/KSA-FRAME-ORDER.md`.[^ksacars-frame-order]

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L760-L877) | `Pose`, per-car pose, `HoldTheWheel`, `Place` |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L478-L513) | `Resolve` / `SubPart`: find bodies by instance Id suffix |
| [src/KSACars/Sim/BuggyDrive.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/BuggyDrive.cs#L329-L370) | `ArmAngle`, `OnArm`, `CoilPose`, `WheelRotation` |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L59-L130) | Frame and GUI hooks, `StepOnce` |
| [src/KSACars/Sim/FrameLatch.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/FrameLatch.cs) | Once-per-frame latch released in `finally` |

## Snippets

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

## Game APIs touched

| API | Purpose |
|---|---|
| `Part.PositionParentAsmb`, `Asmb2ParentAsmb`, `Scale` ([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L756-L830)) | Parent-relative transform of a subpart |
| `Part.ResetCachedPosMatrixValues` ([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs)) | Drops cached matrices and invalidates the tree's render transforms |
| `Part.SubParts`, `Part.Id` | Find a subpart by its instance Id |
| `Part.PositionVehicleAsmb`, `Part.Asmb2VehicleAsmb` | The car part's placement in the vehicle |
| `Program.OnFrame`, `OnDrawUiViewports`, `DrawUI` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2330)) | Where the two hooks land and why the GUI one can be skipped |
| `Universe.SimulationSpeed` | Scales hatch animation to world time |

## Pitfalls and version notes

- **Instance Id, not template Id.** Match `Part.SubParts[i].Id` against the Id inside `<Part>`.
- **Always call `ResetCachedPosMatrixValues()`** after writing a transform.
- **The GUI hook is skipped when the UI is hidden.** Anything that must happen every frame needs a
  fallback on `StarMapAfterOnFrame` and a latch so it does not run twice; release the latch in a
  `finally` on the hook that always runs.
- **Scale is the only visibility switch.** A hidden subpart still exists, is still in the save and
  still costs a draw; the mod tucks it inside the hull at 0.001 scale. The Eldorado's XML declares
  its scoops at that scale so the editor thumbnail shows none.[^ksacars-assets]
- **A shipped part's subpart list is append-only.** Saves pair subparts positionally; removing one
  crashes every save holding the part (see [car part assets](./car-part-assets-glb-pbr.md)).
- Posing from `StarMapAfterOnFrame` alone would draw every pose one frame late, which at speed
  shows as wheels trailing the body.

## See also

- [Wheel, suspension and tyre model](./wheel-suspension-tyre-model.md)
- [Driver kitten's hands on the steering wheel](./driver-hands-steering-ik.md)
- [Rocket lift, hover, boost and downforce](./rocket-lift-hover-boost-downforce.md)
- [Car part assets: GLB, PBR materials and subparts](./car-part-assets-glb-pbr.md)
- [Once-per-frame car step and registry](./frame-step-latch-and-car-registry.md) (the latch and the hook choice)
- [Mod overview](../overview.md)
- [Pose or hide subparts from StarMapAfterGui with an AfterOnFrame fallback](../../../howto/rendering/meshes/hide-parts-or-subparts-from-rendering/hide-parts-or-subparts-from-rendering-3.md) - how-to article demonstrating this feature
- [Run per-frame mod logic once per frame from whichever hook fires first (FrameLatch, pre-render prefix and sim-time steps)](../../../howto/ui/overlays/draw-ui-while-the-hud-is-hidden/draw-ui-while-the-hud-is-hidden-4.md) - how-to article demonstrating this feature
- [Pose wheels, arms and blades from StarMapAfterGui with a frame latch](../../../howto/vehicles-and-parts/parts/define-and-animate-subparts/define-and-animate-subparts-1.md) - how-to article demonstrating this feature

[^ksacars-buggies]: src/KSACars/Ksa/Buggies.cs
[^ksacars-mod]: src/KSACars/Ksa/KSACarsMod.cs
[^ksacars-framelatch]: src/KSACars/Sim/FrameLatch.cs
[^ksacars-buggydrive]: src/KSACars/Sim/BuggyDrive.cs
[^ksacars-hatch]: src/KSACars/Sim/Hatch.cs
[^ksacars-assets]: src/KSACars/KSACarsAssets.xml
[^ksacars-notes]: docs/KSA-MODDING-NOTES.md
[^ksacars-frame-order]: docs/KSA-FRAME-ORDER.md
[^starmap-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-part]: KSA.Part (decompiled)
[^ksa-program]: KSA.Program (decompiled)
