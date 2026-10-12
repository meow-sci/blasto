---
type: Mod Feature
title: First-person view for a kitten on foot
description: V puts the main view at the flown kitten's eyes using a head-bone pose processor and a borrowed Fixed camera mode, right-drag looks around, and a worker-thread patch on KittenLocomotion.StepGrounded turns the walk to follow the look
mod: ksastructures
category: camera
techniques: [harmony-prefix, harmony-postfix, reflection, starmap-lifecycle, anim-processor]
hooks:
  - starmap StarMapAfterGui
  - harmony-prefix KSA.KittenLocomotion.StepGrounded
  - harmony-postfix KSA.KittenLocomotion.StepGrounded
  - reflection KSA.KittenRenderable._characterAvatar
lifecycle: [frame:draw-ui, frame:viewports, physics:vehicle-solver, frame:dispatch-solvers, frame:render]
game_types:
  - KSA.KittenEva
  - KSA.KittenRenderable
  - KSA.CharacterAvatar
  - KSA.AnimatedRenderable
  - KSA.IAnimProcessor
  - KSA.KittenLocomotion
  - KSA.CharacterControlInputs
  - KSA.LocomotionCommand
  - KSA.LocomotionState
  - KSA.LocomotionFacts
  - KSA.PhysicsBubble
  - KSA.GameViewport
  - KSA.FixedController
  - Brutal.ImGuiApi.ImGui
tags: [first-person, kitten, eva, camera, mouse-look, locomotion, anim-processor, hotkeys]
---

Press **V** while flying a kitten on foot and the main view moves to the kitten's eyes; hold the
right mouse button and drag to look round; **V** again, or the settings toggle, hands the view back.
While it is on, W walks where the player looks and A/D side-step rather than turning the kitten
away from the view.[^mod-firstperson][^mod-firstpersonhook] The kitten's own body is hidden from its
eyes but still casts its shadow; that half is described in
[hiding the body but keeping the shadow](./first-person-body-hiding-and-shadow.md).

## How it works

1. **Toggle.** `FirstPerson.Keys` runs from `[StarMapAfterGui]` and flips `Settings.FirstPerson` on
   `ImGui.IsKeyPressed(ImGuiKey.V, repeat: false)` when the controlled vehicle is a `KittenEva` and
   ImGui does not want text input. The same field is a menu item and a checkbox in the settings
   window.[^mod-firstperson][^mod-entry][^mod-settings]
2. **Where the head is.** On the first frame with a kitten, the mod reads the kitten's
   `CharacterAvatar` from the private `KittenRenderable._characterAvatar` field (via the public
   `KittenEva.Renderable`) and adds an `EyesPose` to `avatar.Core.CharacterModel.AnimProcessors`.[^mod-kittenframe][^ksa-kittenrenderable]
   `IAnimProcessor` is KSA's own hook into skeletal animation: `UpdateLocalPose` and
   `UpdateSkeleton` are called per animated renderable, and the stock kitten uses it for its
   expression animation.[^ksa-animprocessor][^ksa-animatedrenderable] `EyesPose.UpdateSkeleton`
   reads the `Head_M` bone's world-space translation from `skeleton.WorldTransforms` into `Head`.[^mod-firstperson]
3. **Model space to the world.** `KittenFrame.ToEcl` repeats the engine's chain from skeleton
   centimetres to the ecliptic: scale by the avatar's `Core.Scale`, rotate -90 degrees about X then Z
   (the same as `KittenRenderable.ModelToBodyMatrix`), subtract the vehicle's centre of mass, rotate
   by `Body2Cce` and add the vehicle's position.[^mod-kittenframe][^ksa-kittenrenderable]
4. **A look measured from the world.** `HeadLook` keeps yaw (from north toward east) and pitch
   (clamped to +-85 degrees) in a local up/north/east frame built from the body's centre and its spin
   axis, not from the kitten, because the kitten turns to follow the look and a look measured from it
   would chase its own tail.[^mod-headlook] On taking the view it starts at the kitten's current facing.[^mod-firstperson]
5. **Borrowing the main view.** Each step `Drive` calls `KsaWorld.TryLookFromMainViewport(offset,
   forward, up, 75, this)` with the eye 9 cm ahead of and 6 cm above the head bone. `FirstPerson`
   is the `IViewPose`, so the installed controller re-asks `TryPose` inside the engine's viewport
   pass, and its `Orbit` property hands the right button to `HeadLook` (see
   [level horizon camera controller](./level-horizon-camera-controller.md)).[^mod-firstperson]
6. **Coexisting with KSA's camera modes.** The view is KSA's own `CameraMode.Fixed` with the camera
   still following the kitten; the previous mode, follow target and field of view are recorded first
   and restored by `Leave`. KSA's mode cycling (`GameViewport.NextCameraMode`) only moves between
   Orbit, Free and IVA and returns false from `Fixed`, and the mod re-asserts `Fixed` every step it
   holds the view.[^ksa-gameviewport]
7. **Walking along the look.** Grounded movement runs on the vehicle worker in
   `KittenLocomotion.StepGrounded`, called from `PhysicsBubble` through `KittenLocomotion.Advance`.[^ksa-locomotion][^ksa-physicsbubble]
   The stock code builds the move from the camera basis copied into `CharacterControlInputs` by
   `KittenEva.PrepareWorker`, and sets `FacingDirPhys` to the direction of travel, so strafing turns
   the kitten.[^ksa-kitteneva][^ksa-locomotion] The mod's prefix replaces `inputs.CameraForwardCce`
   with the look heading (stock would flatten the camera's forward, which degenerates when looking
   straight down), and its postfix sets both `__result.FacingDirPhys` and `state.FacingDirPhys` to
   that heading flattened onto the ground. The heading crosses threads through a `volatile float[]`.[^mod-firstpersonhook]
8. **Leaving.** When the setting goes off, the flown vehicle stops being that kitten, or the scene
   ends, `Leave` clears the hook's body and heading, removes the pose processor and hands the main
   view back. `Unload` calls it as well.[^mod-firstperson][^mod-entry]

## Lifecycle and timing

| Phase | Hook | Work |
|---|---|---|
| `frame:draw-ui` | `[StarMapAfterGui]` -> `_firstPerson.Keys()` | V toggle |
| `frame:draw-ui` (or hidden-UI fallback) | the step -> `_firstPerson.Drive()` | Install the pose processor, borrow the view, publish the heading |
| `frame:dispatch-solvers` | engine `KittenEva.PrepareWorker` | Copies the main camera's basis into the kitten's control inputs |
| `physics:vehicle-solver` | prefix and postfix on `KittenLocomotion.StepGrounded` | Worker thread, per step: replace camera forward, pin facing to the look |
| `frame:viewports` | `LevelHorizonController.OnFrame` -> `FirstPerson.TryPose` | Eye position and direction in phase with the frame |
| `frame:render` | `KittenEva.UpdateRenderData`, called per viewport from `Program.RenderGame` and `RenderViewport` -> skeleton update -> `EyesPose.UpdateSkeleton` | Head bone read for the next pose |

The `Head` value is written during the render's skeleton update (kittens are updated inside
`Program.RenderGame`, not in `UpdateRenderingResources`, which skips them)[^ksa-program] and read in the next frame's
viewport pass, so the eye follows the head bone with a one-frame lag; the pose itself is in phase
with the followed kitten's position.

## Key code

| Path | Role |
|---|---|
| [src/KSAStructures/Ksa/FirstPerson.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPerson.cs#L32-L101) | `Keys`, `Drive`, `Leave` |
| [src/KSAStructures/Ksa/FirstPerson.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPerson.cs#L103-L139) | Heading, `TryPose`, the local frame |
| [src/KSAStructures/Ksa/FirstPerson.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPerson.cs#L141-L188) | `EyesPose`: an `IAnimProcessor` that reads the head bone |
| [src/KSAStructures/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPersonHook.cs#L140-L175) | `StepGrounded` prefix and postfix |
| [src/KSAStructures/Ksa/KittenFrame.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KittenFrame.cs#L16-L71) | Model space to ecliptic and back |
| [src/KSAStructures/Sim/HeadLook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Sim/HeadLook.cs#L10-L83) | Yaw/pitch with KSA's drag constants |

## Snippets

Taking the view and keeping it each step:

```csharp
// src/KSAStructures/Ksa/FirstPerson.cs L40-L74
    public void Drive()
    {
        try
        {
            KittenEva? flown = config.FirstPerson && KsaWorld.InFlightScene && KsaWorld.ControlledVehicle is KittenEva k
                               && KsaWorld.IsAlive(k) ? k : null;

            if (!ReferenceEquals(flown, _kitten)) Leave();
            if (flown is null) return;

            if (_pose is null)
            {
                if (KittenFrame.AvatarOf(flown) is not { } avatar) return;

                _kitten = flown;
                _pose = new EyesPose(flown, avatar);
                avatar.Core.CharacterModel.AnimProcessors.Add(_pose);
            }

            if (!_borrowed)
            {
                _saved = KsaWorld.RememberMainView();
                _borrowed = true;
                if (TryLocalFrame(flown, out _, out double3 n, out double3 e))
                {
                    double3 facing = KittenFrame.DirectionToEcl(flown, new double3(0, 0, 1));
                    _look.LookTowards(Math.Atan2(Vec.Dot(facing, e), Vec.Dot(facing, n)), 0.0);
                }
            }

            FirstPersonHook.Body = _pose;
            FirstPersonHook.Heading = Heading(flown);

            if (TryPose(KsaWorld.PositionEcl(flown), out double3 offset, out double3 forward, out double3 up, out double fov))
                KsaWorld.TryLookFromMainViewport(offset, forward, up, fov, this);
```

The eye, asked inside the viewport pass:

```csharp
// src/KSAStructures/Ksa/FirstPerson.cs L108-L126
    public bool TryPose(double3 followedEcl, out double3 offsetFromFollowed, out double3 forwardEcl,
                        out double3 upEcl, out double fovDeg)
    {
        offsetFromFollowed = forwardEcl = upEcl = default;
        fovDeg = FovDeg;
        if (_kitten is not { } kitten || _pose?.Head is not { } head
            || !TryLocalFrame(kitten, out double3 up, out double3 north, out double3 east)) return false;

        // Ahead of the head along the look rather than along the body, which lags a turn. Measured
        // against the kitten's own position, with the one the engine hands in: both this frame's.
        double3 heading = (north * Math.Cos(_look.Yaw)) + (east * Math.Sin(_look.Yaw));
        double3 headEcl = KittenFrame.ToEcl(kitten, head) - KsaWorld.PositionEcl(kitten) + followedEcl;
        double3 eye = headEcl + (heading * (EyeForwardCm / 100.0)) + (up * (EyeUpCm / 100.0));

        offsetFromFollowed = eye - followedEcl;
        forwardEcl = Vec.Unit((heading * Math.Cos(_look.Pitch)) + (up * Math.Sin(_look.Pitch)));
        upEcl = up;
        return Vec.IsFinite(offsetFromFollowed) && Vec.IsFinite(forwardEcl);
    }
```

Turning the walk to follow the look, on the vehicle worker:

```csharp
// src/KSAStructures/Ksa/FirstPersonHook.cs L140-L175
    // The inputs are the kitten's own copy, written from the camera every step, so replacing the forward
    // changes nothing but this step's walk.
    private static void BeforeStepGrounded(ref CharacterControlInputs inputs)
    {
        try
        {
            if (_heading is { Length: 3 } h) inputs.CameraForwardCce = new float3(h[0], h[1], h[2]);
        }
        catch
        {
            // The camera's own forward is the only failure allowed.
        }
    }

    // Only the flown kitten's inputs carry a camera, so the rest are left alone.
    private static void AfterStepGrounded(ref LocomotionCommand __result, in CharacterControlInputs inputs,
                                          in LocomotionFacts facts, ref LocomotionState state)
    {
        try
        {
            if (_heading is null || inputs.CameraForwardCce.LengthSquared() < 1e-6f) return;

            float3 forward = float3.Transform(inputs.CameraForwardCce, facts.Cce2Phys);
            float3 up = facts.UpDirPhys;
            float3 flat = forward - (up * float3.Dot(forward, up));
            if (flat.LengthSquared() < 0.0025f) return;

            flat = flat.Normalized();
            __result.FacingDirPhys = flat;
            state.FacingDirPhys = flat;
        }
        catch
        {
            // A facing left as KSA chose it is the only failure allowed.
        }
    }
```

The engine's chain from skeleton space to the world, rebuilt:

```csharp
// src/KSAStructures/Ksa/KittenFrame.cs L18-L44
    // KittenEva.Renderable is public; the avatar inside it is not.
    private static readonly System.Reflection.FieldInfo? AvatarField =
        typeof(KittenRenderable).GetField("_characterAvatar", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <summary>The avatar a kitten is drawn through, or null when the private field has moved.</summary>
    public static CharacterAvatar? AvatarOf(KittenEva kitten) => AvatarField?.GetValue(kitten.Renderable) as CharacterAvatar;

    private static readonly doubleQuat ModelToBody = doubleQuat.Concatenate(
        doubleQuat.CreateFromAxisAngle(new double3(1, 0, 0), -Math.PI / 2.0),
        doubleQuat.CreateFromAxisAngle(new double3(0, 0, 1), -Math.PI / 2.0));

    private static double ScaleOf(KittenEva kitten) => AvatarOf(kitten)?.Core.Scale ?? 0.01;

    private static double3 CentreOfMass(Vehicle v)
    {
        float3 c = v.CenterOfMassAsmbF;
        return new double3(c.X, c.Y, c.Z);
    }

    /// <summary>A model-space point, in the ecliptic.</summary>
    public static double3 ToEcl(KittenEva kitten, double3 modelCm) => ToEcl(kitten, modelCm, kitten.Body2Cce);

    private static double3 ToEcl(KittenEva kitten, double3 modelCm, doubleQuat body2Cce)
    {
        double3 body = double3.Transform(modelCm * ScaleOf(kitten), ModelToBody) - CentreOfMass(kitten);
        return kitten.GetPositionEcl() + double3.Transform(body, body2Cce);
    }
```

## Game APIs touched

| API | Decomp | Use |
|---|---|---|
| `KittenEva.Renderable`, `KittenEva.PrepareWorker` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L319-L333) | Renderable access; camera basis copied into inputs |
| `KittenRenderable._characterAvatar` (private), `ModelToBodyMatrix` | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L13-L115) | Avatar and model-to-body transform |
| `IAnimProcessor` | [current/decomp/KSA/IAnimProcessor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IAnimProcessor.cs#L8-L19) | Pose hook |
| `AnimatedRenderable.AnimProcessors` | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L56) | Where the hook is registered |
| `KittenLocomotion.StepGrounded`, `Advance` | [current/decomp/KSA/KittenLocomotion.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotion.cs#L340-L368) | Grounded walk; sets `FacingDirPhys` to the move |
| `PhysicsBubble` (calls `KittenLocomotion.Advance`) | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1976) | Worker-thread caller |
| `VehicleUpdateState.PrepareCharacterFromVehicle` | [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L308-L312) | Inputs are the worker's copy |
| `GameViewport.NextCameraMode`, `SetCameraMode` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L302-L330) | Fixed is outside KSA's cycle |

## Pitfalls and version notes

- **The walk patch runs on worker threads.** Hand data to it through a field written atomically
  (here a `volatile` array reference) and never touch ImGui or the main thread's state there.
- **Only the flown kitten's inputs carry a camera**; the postfix ignores inputs whose
  `CameraForwardCce` is near zero, which leaves other kittens alone.
- **`CharacterControlInputs` is an `in` parameter** of `StepGrounded`; the prefix takes it by `ref`
  and edits the worker's copy, which `PrepareCharacterFromVehicle` rewrites every step.
- **Private field and bone name.** `_characterAvatar` and the `Head_M` bone are not API; if either
  moves, first person does not start (the reflection returns null and the bone index stays -1).
- **Built against 2026.10.7.5541**; members verified in 2026.10.10.5554.

## See also

- [Hiding the body but keeping the shadow](./first-person-body-hiding-and-shadow.md) - the render side of first person
- [Level horizon camera controller](./level-horizon-camera-controller.md) - pose, roll and right-drag
- [Settings menu](./settings-menu.md) - the toggle in the menu
- [Headless tests and CI](./headless-tests-and-ci.md) - `HeadLook` tests
- [Record the main view before the first write when flying another craft or a first-person kitten](../../../howto/camera/main-view/borrow-the-main-camera-and-hand-it-back/borrow-the-main-camera-and-hand-it-back-3.md) - how-to article demonstrating this feature
- [Read the head bone through an IAnimProcessor, pose the eye in the viewport pass and walk where the kitten looks](../../../howto/camera/special-views/first-person-camera-for-a-kitten/first-person-camera-for-a-kitten-1.md) - how-to article demonstrating this feature
- [Convert a standing kitten's model space to the ecliptic or a camera's ego frame](../../../howto/kittens/animation/pose-kitten-bones-with-an-anim-processor/pose-kitten-bones-with-an-anim-processor-5.md) - how-to article demonstrating this feature
- [Find the kitten branches in the worker and patch them from outside the solver](../../../howto/kittens/eva/kitten-locomotion-and-servo-reference/kitten-locomotion-and-servo-reference-1.md) - how-to article demonstrating this feature
- [Walk and face along the look with a StepGrounded prefix and postfix and a volatile heading](../../../howto/kittens/eva/steer-a-walking-kitten/steer-a-walking-kitten-1.md) - how-to article demonstrating this feature
- [Volatile snapshot handoff from worker patches to the main thread](../../../howto/physics/solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-4.md) - how-to article demonstrating this feature

[^mod-firstperson]: src/KSAStructures/Ksa/FirstPerson.cs
[^mod-firstpersonhook]: src/KSAStructures/Ksa/FirstPersonHook.cs
[^mod-kittenframe]: src/KSAStructures/Ksa/KittenFrame.cs
[^mod-headlook]: src/KSAStructures/Sim/HeadLook.cs
[^mod-entry]: src/KSAStructures/Ksa/KSAStructuresMod.cs
[^mod-settings]: src/KSAStructures/Sim/Settings.cs
[^ksa-kitteneva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
[^ksa-kittenrenderable]: KSA.KittenRenderable (decompiled, build 2026.10.10.5554)
[^ksa-animprocessor]: KSA.IAnimProcessor (decompiled, build 2026.10.10.5554)
[^ksa-animatedrenderable]: KSA.AnimatedRenderable (decompiled, build 2026.10.10.5554)
[^ksa-locomotion]: KSA.KittenLocomotion (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
