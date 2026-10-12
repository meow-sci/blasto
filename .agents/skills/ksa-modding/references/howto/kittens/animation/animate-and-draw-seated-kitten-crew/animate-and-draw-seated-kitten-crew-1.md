---
type: How-To
title: Attach a pose processor to a seat's kitten each time the seat rebuilds it, and stop the idle actions fighting the hands
description: KSACars checks every frame whether the driver seat holds a new KittenRenderable, adds a DriverHands processor to its CharacterModel.AnimProcessors, nulls the private _seatedIdleActionAnims and lets the processor put each wrist on a grip that turns with the wheel
category: kittens
group: animation
topic: animate-and-draw-seated-kitten-crew
approach: 1
difficulty: advanced
prerequisites: [starmap, reflection]
demonstrated_by: [mods/ksacars/features/driver-hands-steering-ik, mods/ksacars/features/seats-and-eva-doors]
techniques: [two-bone-ik, anim-processor, reflection]
hooks:
  - starmap StarMapAfterGui
  - reflection KSA.KittenRenderable._characterAvatar
  - reflection KSA.KittenRenderable._seatedIdleActionAnims
lifecycle: [frame:draw-ui, frame:render]
game_types:
  - KSA.IVASeat
  - KSA.KittenRenderable
  - KSA.CharacterAvatar
  - KSA.IAnimProcessor
  - KSA.AnimatedRenderable
  - KSA.Vehicle
  - KSA.Program
tags: [kitten, seated, iva, anim-processor, ik, reflection]
---
This article puts a pose processor on the kitten sitting in a given seat and keeps it there for as long as that seat has a kitten: the driver of a KSACars car holds the wheel and turns it. The pose maths is covered in [two-bone IK](../pose-kitten-bones-with-an-anim-processor/pose-kitten-bones-with-an-anim-processor-4.md); this article is about getting the processor onto the right renderable at the right time. Prerequisites: StarMap, reflection, and a vehicle whose seats are declared in part XML ([add crew seats and EVA doors](../../../vehicles-and-parts/parts/add-crew-seats-and-eva-doors/overview.md)). The mod that demonstrates it is KSACars.

## Background: how the game does it

- A seat's kitten is a `KittenRenderable` held in `IVASeat._renderable`, exposed as the public `IVASeat.Renderable`. `UpdateSeatedRenderable` (called from `Vehicle.UpdateSeatedCrewRenderData` in the render) creates it lazily and **again whenever the assigned kitten changes**: `if (_renderable == null || _renderedKittenHash != AssignedKittenHash) ... _renderable = new KittenRenderable(character, seated: true)` ([current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L80-L99)). A processor added to the old renderable is gone with it.
- The seated renderable loads `_seatedIdleAnim` and the list `_seatedIdleActionAnims` (stretches, shuffles), and `AdvanceIdleActionSchedule` plays an action after 45 to 75 seconds; those clips pull the hands off a wheel ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L246-L266), [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L765-L791)).
- A seated renderable has the MMU and helmet disabled and sets the ear weight to 0 ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L229-L243)).
- The avatar inside is the private `_characterAvatar`; `avatar.Core.CharacterModel.AnimProcessors` is the public list ([reach the avatar](../../characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-1.md)).
- The engine draws seated crew only for `Program.ControlledVehicle`. For an unflown car the renderable may not exist (nobody has drawn it) or may be stale; [approach 2](animate-and-draw-seated-kitten-crew-2.md) makes the crew draw regardless.

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Find the driver seat, attach if the renderable is new | `Buggies.Pose` -> `HoldTheWheel`, from `[StarMapAfterGui]` (or the `[StarMapAfterOnFrame]` fallback) | `frame:draw-ui` |
| Seat updates its kitten | `Program.RenderGame` / `RenderViewport` -> `ControlledVehicle?.UpdateSeatedCrewRenderData` -> `IVASeat.UpdateSeatedRenderable` -> `KittenRenderable.UpdateRenderData` | `frame:render` |
| Processor solves | `AnimatedRenderable.UpdateAnimation` -> `UpdateSkeleton` | `frame:render` |

The attach happens in the UI pass, outside the render, because the engine walks `AnimProcessors` only inside the render. The check runs **every frame** because the seat can replace its renderable at any time ([game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence)).

## Steps

1. **Pick the seat, not by id but by position.** The driver seat is the `IVASeat` module nearest (within 0.25 m) the profile's driver eye point, so the car profile does not need the seat's id:

```csharp
// src/KSACars/Ksa/Buggies.cs L849-L865
    // The seat nearest the one the profile says the driver sits in.
    private static IVASeat? DriverSeat(Entry e)
    {
        double3 eye = e.Part.PositionVehicleAsmb + (e.Part.Asmb2VehicleAsmb * e.Drive.Profile.DriverEye);
        IVASeat? best = null;
        double nearest = 0.25;
        foreach (IVASeat seat in e.Craft.Parts.Modules.Get<IVASeat>())
        {
            double d = Vec.Len(seat.PositionAsmb - eye);
            if (d < nearest)
            {
                nearest = d;
                best = seat;
            }
        }
        return best;
    }
```

2. **Check each frame and attach when the renderable is a new object.** `HandsOn` remembers which renderable already has the processor:

```csharp
// src/KSACars/Ksa/Buggies.cs L838-L847
    // The seat builds its kitten lazily and again whenever the kitten changes, so this is checked each frame.
    private static void HoldTheWheel(Entry e)
    {
        if (DriverSeat(e) is not { Renderable: { } kitten } || ReferenceEquals(kitten, e.HandsOn)) return;
        if (DriverHands.TryAttach(kitten, e.Drive) is { } hands)
        {
            e.HandsOn = kitten;
            e.Hands = hands;
        }
    }
```

3. **Attach the processor and null the idle actions.** One reflected field reaches the avatar; a second private field, `_seatedIdleActionAnims`, is set to null so the idle clips do not run (a null list makes `AdvanceIdleActionSchedule` return immediately). A failed reflection logs once and leaves the kitten as KSA draws it:

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

4. **Solve each wrist onto its grip** (the processor body, [two-bone IK](../pose-kitten-bones-with-an-anim-processor/pose-kitten-bones-with-an-anim-processor-4.md)). The grips are nine and three o'clock on the rim, turned with the wheel by `clamp(steerAngle * SteeringRatio, +-20 degrees)`; past that the rim slides through the hands rather than carrying them over the top, which a seated kitten's short reach could not follow.

5. **Declare the seats in XML, no code.** Seats are `<IVASeat>` modules in `PartGameData` and doors are `<EVADoor SeatId="..."/>` on hidden subparts placed outboard of the seat so a kitten on the ground can reach them (`KittenEva.CanBoardDoor` requires the door within 1 m); see the [seat XML in the add-crew-seats topic](../../../vehicles-and-parts/parts/add-crew-seats-and-eva-doors/overview.md) and the feature page [Seats and EVA doors](../../../../mods/ksacars/features/seats-and-eva-doors.md).

6. **Check the log.** `the driver has taken the wheel` appears when the processor attaches (again after a driver swap); `driver's hands: ...` warnings appear once on a missing bone or a solve exception.

## Complete example

- [src/KSACars/Ksa/DriverHands.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/DriverHands.cs#L18-L150) - processor and attach.
- [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L838-L865) - seat choice and per-frame check.
- [src/KSACars/KSACarsGameData.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsGameData.xml#L41-L55) - the seat XML for the beach buggy.
- Feature pages: [Driver hands and steering IK](../../../../mods/ksacars/features/driver-hands-steering-ik.md), [Seats and EVA doors](../../../../mods/ksacars/features/seats-and-eva-doors.md).

## Variations and gotchas

- **One reflected private field gates the feature.** If `_characterAvatar` is renamed the hands stay in the lap and one warning is logged; the mod's blocked-on-KSA notes list a public accessor as what would unblock it.
- **Re-attach when the renderable changes** (boarding, swapping seats): it is a new object.
- **Idle actions are not restored** when the kitten leaves the driver's seat; the renderable is rebuilt anyway.
- **Model space is centimetres and rotated;** convert with the seat's axes ([IK article](../pose-kitten-bones-with-an-anim-processor/pose-kitten-bones-with-an-anim-processor-4.md)).
- **Animation cull.** `DriverHands.Priority` is 5, so the hands keep solving out to 225 m from the camera; a default priority of 1 would stop at 45 m.
- **Only the controlled vehicle's crew are drawn** by the engine; see [approach 2](animate-and-draw-seated-kitten-crew-2.md).

## See also

- [Animate seated kittens, draw them on unflown craft and fly another craft from a chair](overview.md), [approach 2](animate-and-draw-seated-kitten-crew-2.md), [approach 3](animate-and-draw-seated-kitten-crew-3.md)
- [Pose a kitten's bones and aim its limbs with an IAnimProcessor](../pose-kitten-bones-with-an-anim-processor/overview.md)
- [Reach a kitten's avatar, skeleton, bones and animation catalog](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md)
