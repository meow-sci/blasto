---
type: How-To
title: Walk and face along the look with a StepGrounded prefix and postfix and a volatile heading
description: Two mods publish the look heading as a volatile float array; a prefix on KittenLocomotion.StepGrounded replaces the camera forward and a postfix pins FacingDirPhys to the look in both command and state
category: kittens
group: eva
topic: steer-a-walking-kitten
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksastructures/features/first-person-kitten-view, mods/ksagolf/features/first-person-kitten-view]
techniques: [harmony-prefix, harmony-postfix]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-prefix KSA.KittenLocomotion.StepGrounded
  - harmony-postfix KSA.KittenLocomotion.StepGrounded
lifecycle: [startup:all-mods-loaded, physics:vehicle-solver, frame:draw-ui]
game_types:
  - KSA.KittenLocomotion
  - KSA.CharacterControlInputs
  - KSA.LocomotionCommand
  - KSA.LocomotionState
  - KSA.LocomotionFacts
  - KSA.PhysicsBubble
  - KSA.KittenEva
tags: [kitten, locomotion, steering, first-person, worker-thread, volatile]
---
After this article W walks the kitten where the player looks, A and D side-step instead of turning the kitten away from the view, and looking straight down no longer sends it backwards. The mechanism is a pair of Harmony patches on one static method, plus the discipline for handing a value from the main thread to the physics worker. It is shared by KSAStructures and KSAGolf; the KSAGolf version makes the patch optional and covers the case of a kitten standing still. Prerequisites: StarMap, Harmony, and a source of a look heading (in the reference mods, the first-person controller; see [the first-person camera topic](../../../camera/special-views/first-person-camera-for-a-kitten/overview.md)).

## Background: how the game does it

- `KittenEva.PrepareWorker` copies the main camera's `GetForwardEcl`, `GetRightEcl` and `GetUpEcl` into `CharacterControlInputs` once per frame, as part of the dispatched step; `DirectControl` and ladder inputs follow ([current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L319-L333)).
- The vehicle worker runs `KittenLocomotion.Advance`, which calls `StepGrounded(in inputs, in manualControlInputs, in facts, in tuning, ref state)` for a grounded kitten. `StepGrounded` calls `ResolveTangentBasis` (forward = camera forward flattened onto the ground, falling back to a vector derived from the camera's right and forward when the projection is under about 3 degrees, then to the stored facing), builds the move from the thruster flags (`TranslateForward/Backward/Left/Right`), and sets `state.FacingDirPhys` to the move direction when moving ([current/decomp/KSA/KittenLocomotion.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotion.cs#L320-L369)).
- So the stock behaviour is: forward is where the camera points; the kitten turns toward its own motion, so left turns it left. With the camera glued to the kitten's eyes that rotates the view.
- `StepGrounded` is public static, takes `inputs` by `in`, and returns a `LocomotionCommand`; the command's `FacingDirPhys` is consumed by the servo ([servo reference](../kitten-locomotion-and-servo-reference/overview.md)).

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Install the patches | `FirstPersonHook.Install` from `[StarMapAllModsLoaded]` | `startup:all-mods-loaded` |
| Compute and publish the heading | `Golf.DriveView` / `FirstPerson.Drive`, in the mod's step (GUI postfix) | `frame:draw-ui` |
| Prefix and postfix | `PhysicsBubble.AdvanceKittenLocomotion` -> `KittenLocomotion.Advance` -> `StepGrounded`, on the vehicle worker thread, once per physics timestep (slices of at most 1/60 s of simulated time, so possibly several per frame) | `physics:vehicle-solver` |

The patches run on a worker thread mid-step. They touch only the worker's own copy of the inputs, the command and the state, and read one immutable array. The heading is published as a whole new `float[]` through a `volatile` field, so the worker either sees the old array or the new one, never a half-written vector ([worker-thread-safe patches](../../../physics/solver/write-worker-thread-safe-solver-patches/overview.md), [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#threading-model)).

## Steps

1. **Declare the volatile field and the setter.** The setter rejects non-finite vectors and stores `null` to mean "let KSA decide". The type is an array, not a struct of floats, because a reference write is atomic:

```csharp
// src/KSAStructures/Ksa/FirstPersonHook.cs L54-L54, L68-L77
    private static volatile float[]? _heading;
// ...
    /// <summary>
    /// The way a first-person kitten faces along the ground, in the ecliptic, or null to let KSA turn it.
    /// Handed to the walk in place of the camera's forward too: KSA flattens that onto the ground, and
    /// looking nearly straight down it falls back on the camera's up, which points behind the view.
    /// </summary>
    public static double3? Heading
    {
        set => _heading = value is { } h && Vec.IsFinite(h) ? [(float)h.X, (float)h.Y, (float)h.Z] : null;
    }
```

2. **Compute the heading on the main thread from the look,** in a frame that does not move with the planet: yaw measured from north toward east in the local horizon at the kitten, so the heading is `north * cos(yaw) + east * sin(yaw)`:

```csharp
// src/KSAStructures/Ksa/FirstPerson.cs L103-L106, L128-L139
    private double3? Heading(KittenEva kitten)
        => TryLocalFrame(kitten, out _, out double3 north, out double3 east)
            ? (north * Math.Cos(_look.Yaw)) + (east * Math.Sin(_look.Yaw))
            : null;
// ...
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

3. **Prefix: hand the walk the heading.** Take `inputs` by `ref` and replace `CameraForwardCce`. This also removes the stock fallback to the camera's up when looking nearly straight down:

```csharp
// src/KSAStructures/Ksa/FirstPersonHook.cs L142-L152
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

4. **Postfix: pin the facing to the look** in both the returned command and the stored state, in the physics frame. Only the flown kitten's inputs carry a camera (`CameraForwardCce` is non-zero), so every other kitten is left alone:

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

5. **Install with a way out.** Resolve `StepGrounded` with `AccessTools.Method`; if it is gone, log a warning and carry on without the walk patch. KSAGolf treats the walk patch as optional (the view and body hiding still work), KSAStructures logs the same warning:

```csharp
// src/KSAGolf/Ksa/FirstPersonHook.cs L100-L107
            if (AccessTools.Method(typeof(KittenLocomotion), nameof(KittenLocomotion.StepGrounded)) is { } step)
            {
                _harmony.Patch(step, prefix: Hook(nameof(BeforeStepGrounded)), postfix: Hook(nameof(AfterStepGrounded)));
            }
            else
            {
                Log.Warn("a first-person kitten cannot face where it looks: KSA has no KittenLocomotion.StepGrounded");
            }
```

6. **Draw the turn while the kitten stands still.** A kitten standing still is asleep on the terrain and KSA's servo does not turn it, so until it walks the facing would stay put while the look turns. KSAGolf computes the look direction in model space and draws the turn on the skeleton (a yaw on the root bones, the same trick as [the stance matrix](../../animation/pose-kitten-bones-with-an-anim-processor/pose-kitten-bones-with-an-anim-processor-6.md)); walking, the body itself comes round and the drawn turn falls to nothing:

```csharp
// src/KSAGolf/Ksa/Golf.cs L203-L210
    private void TurnToLook()
    {
        _turnToLook = 0.0;
        if (!_config.FirstPerson || !_borrowed || _phase != Phase.Carrying || _kitten is not { } kitten || LookHeading() is not { } heading) return;

        double3 inModel = KittenFrame.DirectionFromEcl(kitten, heading);
        if (Math.Abs(inModel.X) + Math.Abs(inModel.Z) > 1e-6) _turnToLook = Math.Atan2(inModel.X, inModel.Z);
    }
```

7. **Clear it on release.** `Heading = null` (and `Remove`/unpatch on unload) returns the walk to stock; both mods do this when the first-person view is left.

8. **Check the result.** With the view on, A and D side-step without turning the view, and looking straight down and pressing W walks forward along the look.

## Complete example

- [src/KSAStructures/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPersonHook.cs#L46-L175) and [src/KSAGolf/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/FirstPersonHook.cs#L46-L176) - the patch class in each mod.
- [src/KSAStructures/Ksa/FirstPerson.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPerson.cs#L21-L135) - heading computation and publication.
- [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L121-L218) - the per-frame step, `TurnToLook` and `LookHeading`.
- Feature pages: [KSAStructures first-person kitten view](../../../../mods/ksastructures/features/first-person-kitten-view.md), [KSAGolf first-person kitten view](../../../../mods/ksagolf/features/first-person-kitten-view.md).

## Variations and gotchas

- **`in` parameters.** The stepper takes `inputs` by `in`; the prefix declares it `ref` so the write lands. Check that your Harmony version applies a `ref` prefix parameter to an `in` target (the reference mods do).
- **Publish an immutable value.** Never mutate a shared array in place from the main thread while the worker reads it.
- **Use the physics frame.** The postfix transforms the heading with `facts.Cce2Phys` and flattens onto the ground with `facts.UpDirPhys`; writing an ecliptic vector into `FacingDirPhys` would be wrong.
- **Both command and state.** The command is the servo's target for this step; the state carries the facing into the next step, so write both.
- **Other modes are untouched.** Swimming, MMU and ladder steppers have their own facing logic; a first-person mod that cares needs separate patches.
- **Heading is global.** One heading for all kittens is fine because only the flown kitten's inputs carry a camera.
- **Wrap every body in try/catch.** The worker thread has no safety net; the fallback is a kitten that walks the stock way.

## See also

- [Make a walking kitten move and face along the look direction, and drive it with the mouse](overview.md), [approach 2: captured mouse](steer-a-walking-kitten-2.md)
- [Kitten locomotion, modes and the servo, from the decompiled game](../kitten-locomotion-and-servo-reference/overview.md)
- [Put the camera in an EVA kitten's eyes](../../../camera/special-views/first-person-camera-for-a-kitten/overview.md), [Hide a kitten's body from the main view only](../../appearance/hide-a-kitten-body-from-one-viewport/overview.md)
- [Isolate patch failures and fault-proof patch bodies](../../../patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md)
