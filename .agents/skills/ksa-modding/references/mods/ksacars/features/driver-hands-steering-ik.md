---
type: Mod Feature
title: Driver kitten's hands on the steering wheel
description: Attach a custom IAnimProcessor to the seated driver kitten's character model through a reflected private field and solve each arm onto the turning steering-wheel rim with a two-bone IK after the seated animation and before skinning
mod: ksacars
category: kittens
techniques: [reflection, starmap-lifecycle, two-bone-ik]
hooks:
  - starmap StarMapAfterGui
  - reflection KSA.KittenRenderable._characterAvatar
  - reflection KSA.KittenRenderable._seatedIdleActionAnims
lifecycle: [frame:draw-ui, frame:render]
game_types:
  - KSA.IAnimProcessor
  - KSA.KittenRenderable
  - KSA.CharacterAvatar
  - KSA.AnimatedRenderable
  - KSA.IVASeat
  - KSA.Vehicle
  - RenderCore.Animation.Skeleton
tags: [kittens, animation, ik, skeleton, steering-wheel, reflection, iva-seat]
---

KSA has no steering animation and no IK in use, yet the driver kitten in a KSACars car holds the
wheel and turns it. The mod adds its own `IAnimProcessor` to the seated kitten's character model,
the same extension point KSA uses to turn a kitten's eyes and ears, and in it rotates the shoulder
and elbow bones so each wrist lands on its grip on the steering-wheel rim.[^ksacars-driverhands]
Past 20 degrees of wheel the rim slides through the hands rather than carrying them over the top,
which a seated kitten's short reach could not follow.[^ksacars-steeringgrip]

## How it works

**Attaching.** `Buggies.HoldTheWheel` runs every frame from the pose pass. It finds the driver's
seat (the `IVASeat` whose `PositionAsmb` is within 0.25 m of the profile's `DriverEye`), and when
the seat's `Renderable` (a `KittenRenderable`) is a new object it calls `DriverHands.TryAttach`.[^ksacars-buggies]
`TryAttach` reads the private `KittenRenderable._characterAvatar` by reflection, adds a new
`DriverHands` to `avatar.Core.CharacterModel.AnimProcessors`, and nulls the private
`_seatedIdleActionAnims` so the seated idle clips (stretches, shuffles) do not pull the hands off
the rim.[^ksacars-driverhands] The seat builds a new renderable whenever its kitten changes, which
discards the old processor with it, hence the per-frame check.[^ksa-ivaseat]

**Where the hands go.** `SteeringGrip.GripsPart` takes nine and three o'clock on the rim
(`SteeringRimRadius` across the column axis), turned with the wheel by
`clamp(steerAngle * SteeringRatio, +-20 deg)`. `ToKittenModel` converts a part-frame point into the
seated kitten's model space: centimetres, +Y up, +Z the seat's forward, +X its left, from an origin
0.63 m below the seat's eye point, the engine's `KittenLocomotionTuning.SeatedOffset` of
`(0, 0, -0.63)`.[^ksacars-steeringgrip][^ksa-kittenlocomotiontuning] Because that model space is
anchored to the seat, where the car is in the world never enters the solve.

**Solving.** In `UpdateSkeleton(transform, skeleton, dt)` the processor resolves the bones
`Shoulder_R`, `Elbow_R`, `Wrist_R`, `Shoulder_L`, `Elbow_L`, `Wrist_L` once by name from
`skeleton.BoneNames`, then for each arm:

1. Reads shoulder, elbow and wrist positions from `skeleton.WorldTransforms`.
2. Aims the wrist 4.5 cm short of the grip (`PalmMetres`), so the hand closes on the rim.
3. `SteeringGrip.SolveElbow` clamps the target into reach and places the elbow by the law of
   cosines, bent outward and down.
4. Rotates the shoulder's world transform about the shoulder so the upper arm points at the new
   elbow, re-reads the elbow, then rotates the elbow so the forearm points at the wrist target,
   writing each with `skeleton.SetWorldTransform`.
5. Records `MissCm` (wrist to target) and `Solves` for the bridge.

Any exception switches the processor off for good and logs once; the kitten keeps its seated pose.

## Lifecycle and timing

| Step | Where | Thread |
|---|---|---|
| Attach check | `Buggies.Pose` -> `HoldTheWheel`, from `[StarMapAfterGui]` (or the `[StarMapAfterOnFrame]` fallback) | main |
| Seated kitten updated | `Program.RenderGame` / `RenderViewport` -> `ControlledVehicle?.UpdateSeatedCrewRenderData` -> `IVASeat.UpdateSeatedRenderable` -> `KittenRenderable.UpdateRenderData` -> `CharacterModel.UpdateAnimation` | main, inside the render (`frame:render`) |
| Processor runs | `AnimatedRenderable`: `RuntimeAnim.Update`, every processor's `UpdateLocalPose`, `Skeleton.UpdateLocalTransforms` / `UpdateWorldTransforms`, then every processor's **`UpdateSkeleton`**, then skinning matrices from `InverseBindPose * WorldTransformList` | main |

So `UpdateSkeleton` sees the seated animation already applied in world (model) space and its edits
go straight into the skinning matrices.[^ksa-animatedrenderable] It is called once per viewport
that draws the kitten; because the solve sets joints absolutely, the second call changes nothing.
The steer angle it reads is the drive's state from the frame's physics prefix. Seated crew are only
drawn for `Program.ControlledVehicle` by the engine (`Program.cs` calls
`ControlledVehicle?.UpdateSeatedCrewRenderData`), which is why the mod also
[draws the crew of unflown cars](./seated-crew-of-unflown-craft.md).[^ksa-program]

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Ksa/DriverHands.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/DriverHands.cs) | The `IAnimProcessor`, bone lookup, two-bone turn, reflection attach |
| [src/KSACars/Sim/SteeringGrip.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/SteeringGrip.cs) | Grip points on the rim, part to kitten-model space, elbow solve (KSA-free) |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L838-L865) | `HoldTheWheel`, `DriverSeat` |
| [src/KSACars/Sim/BuggyProfile.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/BuggyProfile.cs#L66-L75) | `SteeringPivot`, `SteeringAxis`, `SteeringRatio`, `SteeringRimRadius`[^ksacars-buggyprofile] |
| [tests/KSACars.Tests/SteeringGripTests.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tests/KSACars.Tests/SteeringGripTests.cs) | The driver reaches the rim of each car's wheel[^ksacars-steeringgrip-tests] |

## Snippets

```csharp
// src/KSACars/Ksa/DriverHands.cs L37-L60
    public void UpdateLocalPose(float4x4 transform, Skeleton skeleton, Span<TransformTRS> localPose, float dt)
    {
    }

    public void UpdateSkeleton(float4x4 transform, Skeleton skeleton, float dt)
    {
        if (_failed) return;

        try
        {
            if ((_bones ??= Resolve(skeleton)) is not { } bones) return;

            (double3 leftPart, double3 rightPart) = SteeringGrip.GripsPart(_drive.Profile, _drive.SteerAngle);
            double right = Hold(skeleton, bones[0], bones[1], bones[2], SteeringGrip.ToKittenModel(_drive.Profile, rightPart), -1.0);
            double left = Hold(skeleton, bones[3], bones[4], bones[5], SteeringGrip.ToKittenModel(_drive.Profile, leftPart), 1.0);
            MissCm = Math.Max(right, left);
            Solves++;
        }
        catch (Exception e)
        {
            _failed = true;
            Log.Warn($"driver's hands: {e.Message}; the kitten keeps its seated pose");
        }
    }
```

```csharp
// src/KSACars/Ksa/DriverHands.cs L115-L150
    private static readonly FieldInfo? AvatarField =
        typeof(KittenRenderable).GetField("_characterAvatar", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? IdleActionsField =
        typeof(KittenRenderable).GetField("_seatedIdleActionAnims", BindingFlags.NonPublic | BindingFlags.Instance);

    private static bool _warned;

    /// <summary>
    /// Puts a processor on a newly seated driver. The seat builds a new renderable whenever its kitten
    /// changes, and the old one takes its processor with it.
    /// </summary>
    public static DriverHands? TryAttach(KittenRenderable renderable, BuggyDrive drive)
    {
        try
        {
            if (AvatarField?.GetValue(renderable) is not CharacterAvatar avatar)
            {
                WarnOnce("KSA's seated kitten has no _characterAvatar; the driver's hands stay in its lap");
                return null;
            }

            DriverHands hands = new(drive);
            avatar.Core.CharacterModel.AnimProcessors.Add(hands);

            // A driver does not stretch or shuffle between laps: those clips would pull the hands off the rim.
            IdleActionsField?.SetValue(renderable, null);
            Log.Info("the driver has taken the wheel");
            return hands;
        }
        catch (Exception e)
        {
            WarnOnce($"driver's hands could not be attached: {e.Message}");
            return null;
        }
    }
```

```csharp
// src/KSACars/Sim/SteeringGrip.cs L31-L44
    public static (double3 Left, double3 Right) GripsPart(BuggyProfile p, double steerAngle)
    {
        double wheel = Math.Clamp(steerAngle * p.SteeringRatio, -HandsFollowRad, HandsFollowRad);
        doubleQuat turn = doubleQuat.CreateFromAxisAngle(p.SteeringAxis, wheel);
        double3 across = Vec.Unit(Vec.RejectFrom(new double3(0, 0, 1), p.SteeringAxis)) * p.SteeringRimRadius;
        return (p.SteeringPivot + (turn * across), p.SteeringPivot - (turn * across));
    }

    /// <summary>A part-frame point in the driver kitten's model space, centimetres.</summary>
    public static double3 ToKittenModel(BuggyProfile p, double3 part)
    {
        double3 d = part - (p.DriverEye - new double3(SeatedDrop, 0, 0));
        return new double3(100.0 * d.Z, 100.0 * d.X, 100.0 * d.Y);
    }
```

## Game APIs touched

| API | Purpose |
|---|---|
| `IAnimProcessor` ([current/decomp/KSA/IAnimProcessor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IAnimProcessor.cs)) | `Priority`, `ShouldUpdate`, `CanCacheAnimation`, `UpdateLocalPose`, `UpdateSkeleton`[^ksa-ianimprocessor] |
| `AnimatedRenderable.AnimProcessors` ([current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L56)) and its update loop (L134-L185) | Public list; processors run after the clip, `UpdateSkeleton` after world transforms, before skinning |
| `KittenRenderable._characterAvatar`, `_seatedIdleActionAnims` ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L13-L59)) | Private; the only route to a kitten's model, and its idle clips[^ksa-kittenrenderable] |
| `CharacterAvatar.Core.CharacterModel` | The `AnimatedRenderable` the processor is added to |
| `IVASeat.Renderable`, `PositionAsmb`, `UpdateSeatedRenderable` ([current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L40-L110)) | Seated kitten, rebuilt when the assigned kitten changes |
| `KittenLocomotionTuning.SeatedOffset` ([current/decomp/KSA/KittenLocomotionTuning.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotionTuning.cs#L220)) | The 0.63 m drop from seat point to model origin |
| `RenderCore.Animation.Skeleton.BoneNames`, `WorldTransforms`, `SetWorldTransform` | Bone lookup and world-space joint edits |

## Pitfalls and version notes

- **One reflected private field gates the feature.** If KSA renames `_characterAvatar`, the hands
  stay in the lap and one warning is logged; the docs list a public accessor as what would unblock
  it.[^ksacars-blocked]
- **Re-attach when the renderable changes.** A seat creates a new `KittenRenderable` whenever its
  kitten changes (boarding, swapping seats), dropping processors added to the old one.
- **Idle action clips fight IK.** Nulling `_seatedIdleActionAnims` is what stops the kitten's
  between-lap animations; restoring them is not done when the kitten leaves the driver's seat (the
  renderable is rebuilt anyway).
- **Model space is centimetres** with the body rotated (`ModelToBodyMatrix` applies scale and two
  -90 degree turns); convert with the seat's axes, not the world.
- **Reach.** A seated kitten's arms are short; the profile places the seat "so a kitten's hands
  reach the rim", and `SteeringGripTests` fails a car whose rim is out of reach.
- **Animation LOD.** `KittenRenderable.UpdateAnimationLod` switches a processor's `ShouldUpdate` off
  beyond `45 m * Priority` from the camera; `DriverHands.Priority` is 5, so the hands keep solving
  out to 225 m.[^ksa-kittenrenderable]
- The processor runs inside the render pass: it must not throw or allocate heavily.

## See also

- [Seats and EVA doors](./seats-and-eva-doors.md)
- [Seated crew of unflown cars](./seated-crew-of-unflown-craft.md)
- [Pose subparts at runtime](./pose-subparts-at-runtime.md) (the steering wheel itself)
- [Wheel, suspension and tyre model](./wheel-suspension-tyre-model.md) (the steer angle)
- [Attach a pose processor to a seat's kitten each time the seat rebuilds it, and stop the idle actions fighting the hands](../../../howto/kittens/animation/animate-and-draw-seated-kitten-crew/animate-and-draw-seated-kitten-crew-1.md) - how-to article demonstrating this feature
- [Overwrite world transforms in UpdateSkeleton once per view](../../../howto/kittens/animation/pose-kitten-bones-with-an-anim-processor/pose-kitten-bones-with-an-anim-processor-2.md) - how-to article demonstrating this feature
- [Solve a two-bone arm in seat-relative model space with a clamped reach and an outward-and-down elbow](../../../howto/kittens/animation/pose-kitten-bones-with-an-anim-processor/pose-kitten-bones-with-an-anim-processor-4.md) - how-to article demonstrating this feature

[^ksacars-driverhands]: src/KSACars/Ksa/DriverHands.cs
[^ksacars-steeringgrip]: src/KSACars/Sim/SteeringGrip.cs
[^ksacars-steeringgrip-tests]: tests/KSACars.Tests/SteeringGripTests.cs
[^ksacars-buggies]: src/KSACars/Ksa/Buggies.cs
[^ksacars-buggyprofile]: src/KSACars/Sim/BuggyProfile.cs
[^ksacars-blocked]: docs/BLOCKED-ON-KSA.md
[^ksa-ianimprocessor]: KSA.IAnimProcessor (decompiled)
[^ksa-animatedrenderable]: KSA.AnimatedRenderable (decompiled)
[^ksa-kittenrenderable]: KSA.KittenRenderable (decompiled)
[^ksa-ivaseat]: KSA.IVASeat (decompiled)
[^ksa-kittenlocomotiontuning]: KSA.KittenLocomotionTuning (decompiled)
[^ksa-program]: KSA.Program (decompiled)
