---
type: How-To
title: Read the head bone through an IAnimProcessor, pose the eye in the viewport pass and walk where the kitten looks
description: KSAStructures adds an EyesPose animation processor to the flown kitten's avatar, converts the Head_M bone to the ecliptic, borrows the main view in Fixed mode, takes right-drag look through the controller and patches KittenLocomotion.StepGrounded to face and walk along the look
category: camera
group: special-views
topic: first-person-camera-for-a-kitten
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony, reflection]
demonstrated_by: [mods/ksastructures/features/first-person-kitten-view]
techniques: [anim-processor, controller-subclass, harmony-prefix, harmony-postfix]
hooks:
  - starmap StarMapAfterGui
  - reflection KSA.KittenRenderable._characterAvatar
  - harmony-prefix KSA.KittenLocomotion.StepGrounded
  - harmony-postfix KSA.KittenLocomotion.StepGrounded
lifecycle: [frame:draw-ui, frame:viewports, frame:render, frame:input, physics:vehicle-solver]
game_types:
  - KSA.KittenEva
  - KSA.KittenRenderable
  - KSA.CharacterAvatar
  - KSA.IAnimProcessor
  - KSA.GameViewport
  - KSA.FixedController
  - KSA.Controller
  - Brutal.GlfwApi.GlfwCursorMode
  - KSA.KittenLocomotion
  - KSA.CharacterControlInputs
  - KSA.LocomotionCommand
  - KSA.LocomotionState
  - KSA.PhysicsBubble
tags: [camera, first-person, kitten, eva, anim-processor, mouse-look, locomotion]
---
Press V while flying a kitten on foot and the main view moves to the kitten's eyes; hold the right mouse button to look round; V again hands the view back. While it is on, W walks where the player looks and A/D side-step instead of turning the kitten away. Four things make that work:
a head position read from the skeleton, a borrowed `Fixed` view posed in the viewport pass, a mouse look kept in a world-built frame, and a patch on the walking code. Hiding the kitten's own body from its eyes is a separate topic,
[Hide a kitten's body from the main view only, keeping its shadow](../../../kittens/appearance/hide-a-kitten-body-from-one-viewport/overview.md). Prerequisites: StarMap, Harmony, reflection, and the borrowed-view helpers ([Borrow the main camera, hand it back and arbitrate between camera users](../../main-view/borrow-the-main-camera-and-hand-it-back/overview.md)).

## Background: how the game does it

- `AnimatedRenderable.AnimProcessors` is a public list of `IAnimProcessor`s; for each animated renderable and view the engine calls `UpdateLocalPose` then, after the world transforms are rebuilt, `UpdateSkeleton(transform, skeleton, dt)` ([current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L150-L180); interface [current/decomp/KSA/IAnimProcessor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IAnimProcessor.cs)).
- The kitten's avatar is a private field, `KittenRenderable._characterAvatar` ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L13)); the model chain to the body frame is `ModelToBodyMatrix`: scale, then -90 degrees about X and about Z ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L107-L110)).
- `KittenEva.PrepareWorker` copies the **main camera's** forward, right and up into the kitten's control inputs every step ([current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L319-L330)), and `KittenLocomotion.StepGrounded` builds the move from them and turns the kitten to face its motion ([current/decomp/KSA/KittenLocomotion.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotion.cs#L340-L369)).
- Stock `Fixed` view and `NextCameraMode`: see [Drive the main camera through Fixed mode without crashing it, with a stable up vector](../../controllers/drive-the-main-camera-in-fixed-mode/overview.md).

## Lifecycle and timing

| Phase | Hook | Work |
|---|---|---|
| `frame:draw-ui` | `[StarMapAfterGui]` -> `Keys()`, `Drive()` | V toggle; install the processor, borrow the view, publish the heading |
| `frame:input` | Controller input virtuals | Right-drag look |
| `frame:viewports` | `LevelHorizonController.OnFrame` -> `FirstPerson.TryPose` | Eye position and direction in phase with the frame |
| `frame:render` | `KittenEva.UpdateRenderData` per view -> `EyesPose.UpdateSkeleton` | Head bone read for the next pose |
| `physics:vehicle-solver` | Prefix and postfix on `KittenLocomotion.StepGrounded` | Worker thread: replace camera forward, pin facing |

The head value is written during the render's skeleton update and read in the next frame's viewport pass, so the eye follows the bone with a one-frame lag; the pose itself is in phase with the followed kitten's position ([game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence)).
Worker-thread patches must not touch ImGui or main-thread state ([Write worker-thread-safe patches inside the vehicle solver](../../../physics/solver/write-worker-thread-safe-solver-patches/overview.md)).

## Steps

1. **Find the avatar and add a pose processor.** The private field is read once by reflection; `EyesPose` implements `IAnimProcessor` and reads the `Head_M` bone's world translation from `skeleton.WorldTransforms`:

```csharp
// src/KSAStructures/Ksa/KittenFrame.cs L18-L23
// KittenEva.Renderable is public; the avatar inside it is not.
private static readonly System.Reflection.FieldInfo? AvatarField =
    typeof(KittenRenderable).GetField("_characterAvatar", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

/// <summary>The avatar a kitten is drawn through, or null when the private field has moved.</summary>
public static CharacterAvatar? AvatarOf(KittenEva kitten) => AvatarField?.GetValue(kitten.Renderable) as CharacterAvatar;
```

```csharp
// src/KSAStructures/Ksa/FirstPerson.cs L142-L144, L150-L154, L160-L173
private sealed class EyesPose(KittenEva kitten, CharacterAvatar avatar) : IAnimProcessor, IFirstPersonBody
{
    private int _headBone = -1;
    // ...
    public double3? Head { get; private set; }

    public float Priority => 1f;
    public bool ShouldUpdate { get; set; } = true;
    public bool CanCacheAnimation => false;
    // ...
    // Inside the engine's render: an unposed kitten is better than a thrown frame.
    public void UpdateSkeleton(float4x4 transform, Skeleton skeleton, float dt)
    {
        ViewmodelApplied = false;
        try
        {
            if (_headBone < 0) _headBone = skeleton.BoneNames?.IndexOf("Head_M") ?? -1;
            if (_headBone < 0) return;

            Span<float4x4> world = skeleton.WorldTransforms;
            float3 head = world[_headBone].Translation;
            Head = new double3(head.X, head.Y, head.Z);

            if (!FirstPersonHook.DrawingMainViewOf(kitten)) return;
```

2. **Convert the bone to the ecliptic** with the chain the engine draws through: scale by the avatar's `Core.Scale`, rotate -90 degrees about X then Z, subtract the vehicle's centre of mass, rotate by `Body2Cce` and add the vehicle's position:

```csharp
// src/KSAStructures/Ksa/KittenFrame.cs L25-L44
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

3. **Borrow the view and pose it from the controller**, recording first ([approach 3](../../main-view/borrow-the-main-camera-and-hand-it-back/overview.md)). Each step `Drive` calls `TryLookFromMainViewport(offset, forward, up, 75, this)` and the replacement controller re-asks `TryPose` in the viewport pass
   ([Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md), [Replace a viewport's camera controller and take input through it](../../controllers/replace-a-viewport-camera-controller/overview.md)):

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

## Mouse look

4. **Keep yaw and pitch in a world-built frame**, not relative to the kitten. KSA turns a walking kitten towards the look, so a look measured from the kitten would chase its own tail. The frame is up (away from the body's centre), north (the spin axis rejected from up) and east:

```csharp
// src/KSAStructures/Ksa/FirstPerson.cs L128-L139
// Up, north and east over the kitten, in the ecliptic.
private static bool TryLocalFrame(KittenEva kitten, out double3 up, out double3 north, out double3 east)
{
    up = north = east = default;
    if (kitten.Parent is not Celestial body) return false;

    up = Vec.Unit(KsaWorld.PositionEcl(kitten) - body.GetPositionEcl());
    double3 axis = double3.Transform(new double3(0, 0, 1), doubleQuat.Inverse(body.GetCce2Ccf()));
    north = Vec.Unit(Vec.RejectFrom(axis, up));
    east = Vec.Cross(north, up);
    return Vec.IsFinite(north) && Vec.Len2(north) > 0.5;
}
```

   Yaw runs from north toward east, pitch is clamped to 85 degrees, and the look starts from the kitten's current facing when the view is taken ([src/KSAStructures/Ksa/FirstPerson.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPerson.cs#L40-L80)).

5. **Take right-drag through the controller.** The pose source's `Orbit` property returns the `HeadLook` while borrowed, so the controller takes the right button with the orbit camera's constants (0.003 rad per pixel, a squared threshold of 2) and answers `IsMouseDrag` ([approach 4](../../controllers/replace-a-viewport-camera-controller/overview.md)):

```csharp
// src/KSAStructures/Sim/HeadLook.cs L12-L17
/// <summary>What KSA's orbit camera turns per pixel dragged.</summary>
public const double RadiansPerPixel = 0.003;

/// <summary>A press becomes a drag past this squared distance, in pixels: KSA's own threshold.</summary>
public const double DragStartsPastSquared = 2.0;
public static readonly double MaxPitchRad = double.DegreesToRadians(85.0);
```

## Make the walking kitten follow the look

6. **Replace the camera forward the walk reads.** Stock flattens the camera forward onto the ground and, looking nearly straight down, falls back on the camera's up, which points behind the view. The prefix writes the look heading into the worker's own copy of the inputs:

```csharp
// src/KSAStructures/Ksa/FirstPersonHook.cs L140-L152
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
```

7. **Pin the facing to the look** in the postfix so strafing side-steps instead of turning the kitten away. The heading crosses threads as a `volatile float[]` swapped as a whole reference:

```csharp
// src/KSAStructures/Ksa/FirstPersonHook.cs L154-L175
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

8. **Install and remove the patches** with the mod; if `StepGrounded` is missing, first person still works but the kitten turns away when strafing ([src/KSAStructures/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPersonHook.cs#L81-L118)).

## Complete example

- [src/KSAStructures/Ksa/FirstPerson.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPerson.cs), [src/KSAStructures/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPersonHook.cs), [src/KSAStructures/Ksa/KittenFrame.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KittenFrame.cs)
- [src/KSAStructures/Sim/HeadLook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Sim/HeadLook.cs), [src/KSAStructures/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/LevelHorizonController.cs)

## Variations and gotchas

- **The walk patch runs on worker threads.** Hand data across with an atomically replaced reference and never touch main-thread state there.
- **`CharacterControlInputs` is an `in` parameter** of `StepGrounded`; the prefix takes it by `ref` and edits the worker's copy, which is rewritten every step.
- **Other kittens.** `PrepareWorker` writes the main camera's forward into every `KittenEva`'s inputs; the postfix ignores inputs whose forward is near zero, which does not exclude other kittens, so other grounded kittens in the bubble may also be turned (not checked in game).
- **Private field and bone name** (`_characterAvatar`, `Head_M`) are not API; if either moves, first person does not start.
- **Built against 5541**; members verified in 5554.

## See also

- [Topic overview](overview.md); [approach 2](first-person-camera-for-a-kitten-2.md)
- [First-person view for a kitten on foot](../../../../mods/ksastructures/features/first-person-kitten-view.md)
- [Hide a kitten's body from the main view only, keeping its shadow](../../../kittens/appearance/hide-a-kitten-body-from-one-viewport/overview.md), [Pose a kitten's bones and aim its limbs with an IAnimProcessor](../../../kittens/animation/pose-kitten-bones-with-an-anim-processor/overview.md), [Make a walking kitten move and face along the look direction, and drive it with the mouse](../../../kittens/eva/steer-a-walking-kitten/overview.md), [Share the keyboard and mouse between ImGui, mod tools and the game](../../../ui/input/coordinate-imgui-focus-with-game-input/overview.md)
