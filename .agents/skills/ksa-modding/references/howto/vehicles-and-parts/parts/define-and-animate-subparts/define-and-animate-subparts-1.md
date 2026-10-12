---
type: How-To
title: Pose wheels, arms and blades from StarMapAfterGui with a frame latch
description: Author each moving body as its own recentred SubPart, find it by instance Id, write its parent-relative transform every frame and fall back to StarMapAfterOnFrame when the HUD is hidden
category: vehicles-and-parts
group: parts
topic: define-and-animate-subparts
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/pose-subparts-at-runtime, mods/ksacars/features/car-part-assets-glb-pbr]
techniques: [starmap-lifecycle, xml-asset]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [startup:asset-load, frame:draw-ui, frame:after-on-frame, frame:render]
game_types: [KSA.Part, KSA.SubPartTemplate, KSA.PartTree, KSA.Vehicle]
tags: [subpart, animation, wheels, steering, frame-latch, hidden-ui]
---
Outcome: wheels turn, trailing arms swing, coil-overs squash, a steering wheel rotates and iris blades open, all
on a vehicle whose physics the game does not know about. KSACars poses the two cars this way every frame from
its own suspension model. The same trick hides a subpart (scale it to 0.001).

Prerequisite: StarMap, and a part whose moving bodies are separate subparts in the XML. Demonstrated by
[ksacars/pose-subparts-at-runtime](../../../../mods/ksacars/features/pose-subparts-at-runtime.md) and [ksacars/car-part-assets-glb-pbr](../../../../mods/ksacars/features/car-part-assets-glb-pbr.md).

## Background: how the game does it

See the [overview](overview.md). Specifics this article uses:

- The three transform setters reset cached matrices themselves; calling `ResetCachedPosMatrixValues()` after the
  write is still the safe habit and is required for any descendant subparts
  [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L756-L781)[^ksa-part] [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1241-L1249).
- The renderer reads transforms in `Vehicle.UpdateRenderData` -> `PartTree.UpdateRenderData` ->
  `RenderData.EnsureBuilt`, inside `Program.Render`, after the GUI pass of the same frame
  [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3713-L3726)[^ksa-vehicle] [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L464-L480)[^ksa-parttreerenderdata]. A write from `StarMapAfterGui` is drawn the
  same frame; one from `StarMapAfterOnFrame` (after `Render`) is drawn a frame late.
- `Program.OnFrame` calls `OnDrawUiFrame` and `OnDrawUiViewports` only inside `if (DrawUI)`, so with the HUD
  hidden (F2, or a screenshot that clears it) the GUI hook never fires; the frame postfix is the one hook KSA
  always calls (see [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).
- Posed subparts carry no colliders or mass of their own (the car's colliders are declared on the whole part), so
  moving them does not feed the physics step.

## Lifecycle and timing

| Hook | Frame position | Why |
|---|---|---|
| `[StarMapAfterGui]` (preferred) | after this frame's viewports, before `OnPreRender` and `Render` | the pose is drawn this frame |
| `[StarMapAfterOnFrame]` (fallback) | after `Render` and `PostRender`; always called | poses a frame late, but never not at all |

Both run on the main thread while the vehicle solver job dispatched in `PrepareFrame` is running. A
`FrameLatch` makes sure only the first caller each frame runs the step. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md).

## Steps

1. **Author the subparts.** One `SubPart` per moving body, each mesh exported recentred on its own pivot (wheel on
   its hub, arm on its bushing, coil on its top mount, steering wheel on its boss) so a rotation about the
   subpart origin turns it about the right point. One wheel mesh can serve both sides:

```xml
<!-- src/KSACars/KSACarsAssets.xml L26-48 -->
  <!--
    Each moving body is exported recentred on its own pivot: a wheel on its hub, an arm on its
    chassis bushing, a coil-over on its top mount, the steering wheel on its boss. One front and
    one rear wheel mesh serve both sides, since a wheel is symmetric about its own mid-plane.
  -->
  <SubPart Id="KSACars_BuggyBody_SubPart">
    <PartModel Id="KSACars_Subpart_BuggyBody_Model">
      <Mesh Id="KSACars_Subpart_BuggyBody" />
      <Material Id="KSACars_Buggy_Material" />
    </PartModel>
    <MeshView>
      <Mesh Id="KSACars_Subpart_BuggyBody_VM" />
    </MeshView>
  </SubPart>
  <SubPart Id="KSACars_BuggyWheelFront_SubPart">
    <PartModel Id="KSACars_Subpart_BuggyWheelFront_Model">
      <Mesh Id="KSACars_Subpart_BuggyWheelFront" />
      <Material Id="KSACars_Buggy_Material" />
    </PartModel>
    <MeshView>
      <Mesh Id="KSACars_Subpart_BuggyWheelFront_VM" />
    </MeshView>
  </SubPart>
```

2. **Instance them in the `Part`** with a `Transform`; the **instance Id** is what code finds at runtime (the
   template Id would match nothing, and a body that is never found is simply never moved, with no log):

```xml
<!-- src/KSACars/KSACarsAssets.xml L167-175 -->
  <Part Id="KSACars_Prefab_Buggy" CrashTolerance="1e12">

    <SubPart Id="KSACars_Buggy_Body" InstanceOf="KSACars_BuggyBody_SubPart" />

    <SubPart Id="KSACars_Buggy_WheelFL" InstanceOf="KSACars_BuggyWheelFront_SubPart">
      <Transform>
        <Position X="0.34000" Y="1.15000" Z="0.84000" />
      </Transform>
    </SubPart>
```

3. **Find them once per craft** by suffix of the instance Id:

```csharp
// src/KSACars/Ksa/Buggies.cs L478-513
    private static void Resolve(Entry e)
    {
        BuggyProfile p = e.Drive.Profile;
        for (int i = 0; i < p.Corners.Length; i++)
        {
            e.Wheels[i] = SubPart(e.Part, p.SubpartPrefix + "Wheel" + p.Corners[i].Key);
            e.Arms[i] = SubPart(e.Part, p.SubpartPrefix + "Arm" + p.Corners[i].Key);
            e.Coils[i] = SubPart(e.Part, p.SubpartPrefix + "Coil" + p.Corners[i].Key);
        }
        e.Steering = SubPart(e.Part, p.SubpartPrefix + "Steering");
        for (int k = 0; k < p.Scoops.Length; k++) e.ScoopBlades[k] = SubPart(e.Part, p.SubpartPrefix + p.Scoops[k].SubpartSuffix);
        for (int k = 0; k < p.Hatches.Length; k++)
        {
            e.HatchCups[k] = SubPart(e.Part, p.SubpartPrefix + p.Hatches[k].Suffix);
            for (int b = 0; b < Hatch.Blades; b++)
                e.HatchBlades[(k * Hatch.Blades) + b] = SubPart(e.Part, $"{p.SubpartPrefix}Iris{p.Hatches[k].Suffix}{b}");
        }
    }

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

4. **Write the transform.** Position, rotation and (optionally) scale, then reset caches:

```csharp
// src/KSACars/Ksa/Buggies.cs L867-877
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

5. **Pose from the model every frame.** Arm about its bushing, wheel carried round the arm, coil-over rotated and
   squashed, steering wheel about its axis:

```csharp
// src/KSACars/Ksa/Buggies.cs L784-800
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

6. **Hide by shrinking.** "A subpart has no switch for being drawn": park it inside the hull at scale 0.001:

```csharp
// src/KSACars/Ksa/Buggies.cs L826-832
        // Hidden by being shrunk to nothing inside the hull: a subpart has no switch for being drawn.
        for (int k = 0; k < d.Profile.Scoops.Length; k++)
        {
            bool on = k == e.Scoop;
            Place(e.ScoopBlades[k], on ? Vec.Zero : d.Profile.Scoops[k].Stowed, doubleQuat.Identity,
                  on ? new double3(1, 1, 1) : new double3(0.001, 0.001, 0.001));
        }
```

7. **Hook twice, run once.** The latch hands each frame's work to whichever hook reaches it first; `EndFrame()`
   belongs in a `finally` on the frame postfix, the hook that cannot be skipped:

```csharp
// src/KSACars/Sim/FrameLatch.cs L15-45
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

```csharp
// src/KSACars/Ksa/KSACarsMod.cs L59-82
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
```

```csharp
// src/KSACars/Ksa/KSACarsMod.cs L85-93, L112-130
    // where this frame put them; F2 hides the UI and skips that pass, and the frame postfix then
    // poses them a frame late rather than not at all.
    [StarMapAfterGui]
    public void OnAfterGui(double dt)
    {
        try
        {
            if (_disabled) return;
            StepOnce();
// ...
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

- [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs)[^ksacars-buggies] - find, pose, hide
- [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs)[^ksacars-ksacarsmod] - hooks
- [src/KSACars/Sim/FrameLatch.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/FrameLatch.cs)[^ksacars-framelatch] - the latch

## Variations and gotchas

- **Time.** Hatch timing advances in world time (wall-clock milliseconds, clamped to 0.1 s, times
  `Universe.SimulationSpeed`), so slow motion slows the irises and pause holds them.
- **Rotations are XYZ radians in XML**, quaternions in code.
- **Latch failure is worse than double work.** Running the step twice costs a duplicated frame; failing to
  release the latch stops the mod for the session.
- **Do not match template Ids.** Resolve by the instance Id the `Part` gives the subpart.
- **Save safety.** Do not remove or reorder these subparts in later releases; see
  [Change a shipped part without breaking saves](../evolve-a-shipped-part-without-breaking-saves/overview.md).

## See also

- [Overview](overview.md), [Article 2](define-and-animate-subparts-2.md), [Article 4](define-and-animate-subparts-4.md)
- [ksacars/pose-subparts-at-runtime](../../../../mods/ksacars/features/pose-subparts-at-runtime.md)
- [hide-parts-or-subparts-from-rendering](../../../rendering/meshes/hide-parts-or-subparts-from-rendering/overview.md), [choose-a-render-frame-and-anchor-effects](../../../rendering/overlays/choose-a-render-frame-and-anchor-effects/overview.md)

[^ksacars-ksacarsmod]: KSACars: src/KSACars/Ksa/KSACarsMod.cs
[^ksacars-buggies]: KSACars: src/KSACars/Ksa/Buggies.cs
[^ksa-parttreerenderdata]: current/decomp/KSA/PartTreeRenderData.cs (decompiled, build 2026.10.10.5554)
[^ksacars-framelatch]: KSACars: src/KSACars/Sim/FrameLatch.cs
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
