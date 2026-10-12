---
type: Mod Feature
title: First-person kitten view that keeps the shadow
description: Look out through a kitten's eyes by stripping its skeleton for the main view only, re-skinning the whole kitten into the shadow pool, holding its walking facing on the mouse look, and posing the eye from the head bone each frame
mod: ksagolf
category: camera
techniques:
  - harmony-prefix
  - harmony-postfix
  - reflection
  - anim-processor
  - viewmodel
  - mouse-capture
  - headless-tests
hooks:
  - harmony-prefix KSA.KittenLocomotion.StepGrounded
  - harmony-postfix KSA.KittenLocomotion.StepGrounded
  - harmony-prefix KSA.KittenEva.UpdateRenderData
  - harmony-postfix KSA.KittenEva.UpdateRenderData
  - harmony-prefix KSA.AnimatedRenderable.Draw
  - harmony-postfix KSA.AnimatedRenderable.Draw
  - harmony-prefix KSA.StaticMeshRenderable.Draw
  - harmony-postfix KSA.StaticMeshRenderable.Draw
  - reflection KSA.AnimatedRenderable.MaterialIndices
  - reflection KSA.AnimatedRenderable.ShadowIgnoreIndicies
  - reflection RenderCore.Animation.Skeleton.InverseBindPose
  - engine-callback KSA.IAnimProcessor.UpdateSkeleton
lifecycle:
  - startup:all-mods-loaded
  - physics:vehicle-solver
  - frame:input
  - frame:viewports
  - frame:draw-ui
  - frame:render
game_types:
  - KSA.KittenEva
  - KSA.KittenRenderable
  - KSA.AnimatedRenderable
  - KSA.StaticMeshRenderable
  - KSA.SuperMeshRenderSystem
  - KSA.ShadowRenderable
  - KSA.InstanceData
  - KSA.CharacterAvatar
  - KSA.KittenLocomotion
  - KSA.CharacterControlInputs
  - KSA.LocomotionCommand
  - KSA.LocomotionFacts
  - KSA.LocomotionState
  - KSA.PhysicsBubble
  - RenderCore.Animation.Skeleton
  - KSA.IVASeat
tags: [camera, first-person, kitten, viewmodel, shadow, skinning, locomotion, harmony, mouse-look]
---

With **First-person view** on (V), KSAGolf puts the main camera just above and ahead of the flown
kitten's head and lets the mouse look around. The kitten's own body is folded away for that one
view. Only the paw holding the putter stays while walking, and at address the mod draws a separate
club. Every other view still draws the whole kitten: secondary viewports, crew portraits, and the
shadow on the ground. The kitten also walks relative to where the player looks, side-stepping
instead of turning away from the view.

Four Harmony patches make this work, plus the per-view `UpdateSkeleton` callback of the
[pose processor](../features/putting-address-pose.md). The mod's CLAUDE.md credits the technique to
KSArmory's first-person sidearm. No such code was found in the pinned KSArmory checkout, so that
lineage is unverified here.[^golf-claude]

## How it works

**1. Know which view is being drawn.** The engine calls `KittenEva.UpdateRenderData(viewport, frame)`
once for every view that draws the kitten, secondary views first and the main view
last.[^ksa-program]

- A prefix records the kitten in `_drawingMain` when it is the first-person kitten and the viewport
  is `Program.MainViewport`.
- A postfix clears it.
- Inside that call the engine animates and draws the kitten. `GolfPose.UpdateSkeleton` asks
  `FirstPersonHook.DrawingMainViewOf(kitten)` to learn whether this view is the eyes'.[^golf-firstperson]

**2. Strip the skeleton for that view only.** `AnimatedRenderable.UpdateAnimation` builds the world
transforms again for every view and calls each processor's `UpdateSkeleton` before it computes the
skinning matrices.[^ksa-animatedrenderable] In the main view, `GolfPose.StripForTheEyes` first
copies the full world transforms into `BodyWorld`. It then overwrites every bone with a
zero-scale matrix at the right wrist, except the right paw's subtree while carrying. Collapsing
onto the wrist keeps the skin between the kept paw and the folded arm short. Folding onto the chest
instead stretched skin into sheets across the view. Folding the head also folds the fur and helmet
that hang from it.[^golf-golfpose][^golf-kittenarms]

**3. Give the shadow the whole kitten.** `AnimatedRenderable.Draw` appends the view's skinned
meshes to `SuperMeshRenderSystem.ShadowRenderablePool`, which for the main view is the stripped
skeleton.

- A prefix remembers the pool's length.
- A postfix removes everything that draw added.
- It then allocates a fresh block of skinning matrices from the same per-frame bone buffer
  (`AllocateSkinningMatrices`) and fills it with `InverseBindPose[i] * BodyWorld[i]`.
- It adds one `ShadowRenderable` per shadow-casting mesh, pointing at that block through
  `InstanceData.data.Y`, the slot `Draw` uses for its own animation index.

`InverseBindPose`, `ShadowIgnoreIndicies` and the protected `MaterialIndices` are read by reflection
or `AccessTools.FieldRefAccess`. The comment says their list type is in an assembly the mod does not
reference.[^golf-firstperson][^ksa-supermesh]

**4. Put attachment shadows back on the whole head.** A prefix and postfix on
`StaticMeshRenderable.Draw` do the same for the helmet and visor while the main view is being
drawn. Their shadow entries are moved to `HelmetTransform * SocketTurn * BodyWorld[head] * model`.

**5. Face where the player looks.**

- KSA's grounded walk moves a kitten relative to the camera and turns it to face its motion.
  `KittenEva.PrepareWorker` copies the main camera's forward into `CharacterControlInputs` every
  step.[^ksa-kitteneva]
- The prefix on `KittenLocomotion.StepGrounded` replaces `CameraForwardCce` with the look heading.
  That also fixes KSA's fallback to the camera's up, which points behind the view, when the
  camera looks nearly straight down.
- The postfix flattens that heading into the physics frame and writes it to both
  `LocomotionCommand.FacingDirPhys` and `LocomotionState.FacingDirPhys`, so left and right
  side-step.[^ksa-kittenlocomotion]
- The heading is handed to the worker thread as a `volatile float[]`, swapped as a whole reference.

**6. The look and the eye.** `Sim/HeadLook.cs` holds a world-relative yaw (from north towards east)
and a pitch clamped to ±85°. It is measured from the world because KSA turns a walking kitten
towards the look, so a kitten-relative look would chase its own tail.

- With the cursor captured, every pixel turns the view 0.08°.
- A right-button drag uses KSA's orbit rate (0.003 rad per pixel) and its 2 px² drag threshold,
  shared with `ChaseOrbit`.
- `Golf.TryEyes` builds the eye from `GolfPose.Head` (the head bone in model space), 12 cm ahead
  along the look and 5 cm up. The forward vector comes from yaw and pitch. The up vector is tilted
  with the pitch so that looking straight down and turning spins the picture rather than rolling it.
- While stepping up to the ball, the pitch nods down to the ball by the step's blend.
- A kitten standing still is asleep on the terrain and does not turn, so until it walks the turn to
  the look is drawn on the skeleton (`_turnToLook`).[^golf-headlook][^golf-golf][^golf-test-headlook]

## Lifecycle and timing

| Step | Where it runs | Lifecycle |
|---|---|---|
| Patches installed | `FirstPersonHook.Install` from `[StarMapAllModsLoaded]`; `Remove` at the fault limit (the unload call runs at process exit, after StarMap has unpatched everything)[^golf-mod] | `startup:all-mods-loaded` |
| `FirstPersonHook.Body` and `Heading` set | `Golf.DriveView`, in the step (GUI postfix) | `frame:draw-ui` |
| Mouse into `HeadLook` | `LevelHorizonController.OnCursorPos` / `OnMouseButton`, during `Glfw.PollEvents` | `frame:input` |
| Eye posed (`IViewPose.TryPose`) | `LevelHorizonController.OnFrame` in `OnFrameViewports` | `frame:viewports` |
| `StepGrounded` prefix and postfix | `PhysicsBubble.AdvanceKittenLocomotion` → `KittenLocomotion.Advance`, on the vehicle worker thread, per physics step[^ksa-physicsbubble] | `physics:vehicle-solver` |
| `UpdateRenderData`, `AnimatedRenderable.Draw`, `StaticMeshRenderable.Draw` patches; `StripForTheEyes` | `Program.RenderGame` / `RenderViewport`, per view | `frame:render` |

- **Why the render callbacks are safe.** All of them run on the main thread inside the kitten's own
  draw for one view. `UpdateSkeleton` runs before the skinning matrices are computed, and the Draw
  postfix runs before the next view clears the pool. The cascaded shadow pass reads the pool after
  the main view's draws.
- **What the worker patches may touch.** The locomotion patches run on a worker thread mid-step.
  They touch only the worker's own copy of the inputs, the command and the state, and read one
  immutable array.
- **Nothing may throw.** Every patch body catches everything. The fallback is a frame that looks
  wrong, not a crashed game.

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/FirstPersonHook.cs#L82-L119) | Install: four targets, the walk patch optional |
| [src/KSAGolf/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/FirstPersonHook.cs#L141-L176) | `StepGrounded` prefix and postfix: heading in, facing out |
| [src/KSAGolf/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/FirstPersonHook.cs#L178-L248) | Which view is drawn; shadow pool swap and re-skin |
| [src/KSAGolf/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/FirstPersonHook.cs#L250-L312) | Helmet and visor shadows; reflection for bind pose and shadow meshes |
| [src/KSAGolf/Ksa/GolfPose.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/GolfPose.cs#L114-L153) | `StripForTheEyes`, `Below` (a bone and its subtree) |
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L148-L231) | Borrow for first person, look heading, drawn turn, local north/east/up |
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L679-L712) | `TryEyes`: eye from the head bone, nod to the ball, up tilted with pitch |
| [src/KSAGolf/Sim/HeadLook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/HeadLook.cs) | Captured-mouse and drag look, world-relative |

## Snippets

```csharp
// src/KSAGolf/Ksa/FirstPersonHook.cs L178-L190
    private static void BeforeUpdateRenderData(KittenEva __instance, IViewport viewport)
    {
        try
        {
            _drawingMain = Body is { } b && ReferenceEquals(__instance, b.Kitten) && ReferenceEquals(viewport, Program.MainViewport)
                ? __instance
                : null;
        }
        catch
        {
            _drawingMain = null;
        }
    }
```

```csharp
// src/KSAGolf/Ksa/GolfPose.cs L119-L135
    private void StripForTheEyes(Skeleton skeleton, bool keepPaw)
    {
        Span<float4x4> world = skeleton.WorldTransforms;
        BodyWorld = BodyWorld is { } kept && kept.Length == world.Length ? kept : new float4x4[world.Length];
        world.CopyTo(BodyWorld);

        if (_rightPaw.Length != world.Length) _rightPaw = Below(skeleton, _bones[2]);

        float3 wrist = world[_bones[2]].Translation;
        float4x4 gone = new(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, wrist.X, wrist.Y, wrist.Z, 1f);
        for (int i = 0; i < world.Length; i++)
        {
            if (!(keepPaw && _rightPaw[i])) world[i] = gone;
        }

        ViewmodelApplied = true;
    }
```

```csharp
// src/KSAGolf/Ksa/FirstPersonHook.cs L212-L243
    private static void AfterDraw(AnimatedRenderable __instance)
    {
        int before = _poolBefore;
        _poolBefore = -1;
        try
        {
            if (before < 0 || Body is not { BodyWorld: { } body } || _materialIndices is null) return;

            List<ShadowRenderable> pool = Program.Instance.SuperMeshRenderSystem.ShadowRenderablePool;
            if (pool.Count > before) pool.RemoveRange(before, pool.Count - before);

            Describe(__instance);
            int bones = __instance.Skeleton.BoneCount;
            if (_inverseBind.Length < bones || body.Length < bones) return;

            Span<float4x4> skin = Program.Instance.SuperMeshRenderSystem.AllocateSkinningMatrices(bones, out int index);
            for (int i = 0; i < bones; i++) skin[i] = _inverseBind[i] * body[i];

            int[] materials = _materialIndices(__instance);
            float4x4 model = __instance.Transform;
            float scale = float.Max(model.X.XYZ.Length(), float.Max(model.Y.XYZ.Length(), model.Z.XYZ.Length()));
            foreach (int j in _shadowMeshes)
            {
                if (j >= __instance.DepthMeshBucketHandles.Length || j >= materials.Length) continue;
                pool.Add(new ShadowRenderable
                {
                    Radius = (__instance.GltfAssetRef.Meshes[j]?.BoundingRadius ?? 2f) * scale,
                    BucketHandle = __instance.DepthMeshBucketHandles[j],
                    InstanceData = new InstanceData { model = model, data = new float4(materials[j], index, 1f, 0f) },
                });
            }
        }
```

```csharp
// src/KSAGolf/Ksa/FirstPersonHook.cs L156-L171
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
```

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `KittenEva.UpdateRenderData(IViewport, int)` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L1062-L1078) | Patched to learn which view draws the kitten |
| `KittenEva.PrepareWorker` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L319-L332) | Copies the main camera's basis into `CharacterControlInputs` each step |
| `AnimatedRenderable.UpdateAnimation` / `Draw` | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L134-L258) | Per-view skeleton, then skinning; `Draw` fills the shadow pool (patched) |
| `AnimatedRenderable.MaterialIndices` (protected), `ShadowIgnoreIndicies`, `DepthMeshBucketHandles`, `GltfAssetRef` | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L20-L32) | What a shadow entry is built from |
| `Skeleton.InverseBindPose` / `WorldTransforms` / `NodeInfo` | [current/decomp/RenderCore.Animation/Skeleton.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Animation/Skeleton.cs#L11-L29) | Bind pose for re-skinning; per-view world transforms |
| `SuperMeshRenderSystem.ShadowRenderablePool` / `AllocateSkinningMatrices` | [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L1007-L1013) | The pool the cascades read; the per-frame bone buffer |
| `ShadowRenderable` | [current/decomp/KSA/ShadowRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShadowRenderable.cs) | `BucketHandle`, `Center`, `Radius`, `InstanceData` |
| `StaticMeshRenderable.Draw` | [current/decomp/KSA/StaticMeshRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticMeshRenderable.cs#L56-L90) | Patched for helmet and visor shadows |
| `KittenLocomotion.StepGrounded` | [current/decomp/KSA/KittenLocomotion.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotion.cs#L340) | Patched: grounded walk input and facing |
| `CharacterControlInputs.CameraForwardCce`, `LocomotionFacts.Cce2Phys` / `UpDirPhys`, `LocomotionCommand.FacingDirPhys`, `LocomotionState.FacingDirPhys` | [current/decomp/KSA/CharacterControlInputs.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterControlInputs.cs) | The fields the walk patch reads and writes |
| `PhysicsBubble.AdvanceKittenLocomotion` | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1969-L1976) | Worker-thread caller of the locomotion step |
| `KittenRenderable.HideHead` / `IVASeat` | [current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L103) | The engine's own per-view head hiding for IVA (not used by the mod) |

## Pitfalls and version notes

- **The engine's simpler alternative.** `KittenRenderable.HideHead` is a public field. `IVASeat` sets
  it per view (`IsCameraInThisSeat(viewport)`). `AnimatedRenderable.Draw` then skips the masked head
  meshes in both colour and shadow, and `KittenRenderable` skips the fur.[^ksa-ivaseat][^ksa-kittenrenderable]
  That hides only the head and loses the head's shadow; the mod needed the arms gone and the shadow
  whole.
- **Other kittens may turn too (unverified).** The walk postfix comment says only the flown kitten's
  inputs carry a camera. In the decomp, `KittenEva.PrepareWorker` writes the main camera's forward
  for every kitten.[^ksa-kitteneva] While first person is on, any other grounded kitten in the same
  bubble may be turned to the look as well.
- **Helmet path is dormant while golfing.** `Golf.Hold` disables the helmet (and the MMU), so the
  helmet and visor shadow fix-up only matters for a caller that keeps the helmet on. The visor casts
  no shadow anyway.
- **`ShadowRenderable.Center` (new in 2026.10.10.5554).** In 2026.9.22.5482 a shadow entry's position
  came from `InstanceData.model.Translation`. The current build has an explicit `Center` that
  `AddPoolToShadowBucket` culls cascades with. `AnimatedRenderable.Draw` now fills it from the pose
  bounds and uses a larger pose-based radius.[^ksa-shadowrenderable][^ksa-animatedrenderable]
  `AfterDraw` sets neither, so its whole-kitten entries keep `Center` at the default zero. That is
  the camera origin of the main view, which in first person sits at the kitten's head, so the
  shadow likely still lands. Its old-formula radius may let cascade culling drop it at cascade edges
  (unverified in game). The helmet fix-up likewise moves `InstanceData.model` but leaves `Center` at
  the collapsed head. Set `Center = ((min + max) / 2) * model` from `BodyWorld` to match the engine.
- **Optional walk patch.** If `StepGrounded` is missing, the install logs it and carries on: first
  person then works, but the kitten turns away from the view when strafing.
- **Version notes.** All four targets keep their signatures in 2026.10.10.5554
  (`KittenEva.UpdateRenderData(IViewport, int)`, `AnimatedRenderable.Draw(ViewHandle)`,
  `StaticMeshRenderable.Draw(ViewHandle)`, `KittenLocomotion.StepGrounded(in ..., ref LocomotionState)`).
  `InverseBindPose` and `ShadowIgnoreIndicies` are public fields in both builds. Their element type
  (`NativeList<T>`) is what the mod avoids referencing, which is why it reads them by
  reflection.[^ksa-skeleton]

## See also

- [Putting address pose drawn on the kitten skeleton](../features/putting-address-pose.md)
- [Posing kitten bones through the pose-processor list](../features/kitten-pose-processor.md)
- [Level-horizon camera controller swapped into the main viewport](../features/level-horizon-camera-controller.md)
- [Borrowing the main view and handing it back](../features/borrowed-chase-camera.md)
- [Putting session with a mouse-swung club](../features/putting-session.md)
- [Pose the eye from the head bone in the golf pose with a pitch-tilted up, a captured-cursor look and a drawn turn for a standing kitten](../../../howto/camera/special-views/first-person-camera-for-a-kitten/first-person-camera-for-a-kitten-2.md) - how-to article demonstrating this feature
- [Fold all bones onto the head with zero-scale matrices, then give the shadow pass the full-body pose](../../../howto/kittens/appearance/hide-a-kitten-body-from-one-viewport/hide-a-kitten-body-from-one-viewport-1.md) - how-to article demonstrating this feature
- [Fold onto the wrist and keep the club-holding paw, or use KittenRenderable.HideHead per view](../../../howto/kittens/appearance/hide-a-kitten-body-from-one-viewport/hide-a-kitten-body-from-one-viewport-2.md) - how-to article demonstrating this feature
- [Walk and face along the look with a StepGrounded prefix and postfix and a volatile heading](../../../howto/kittens/eva/steer-a-walking-kitten/steer-a-walking-kitten-1.md) - how-to article demonstrating this feature

[^golf-firstperson]: src/KSAGolf/Ksa/FirstPersonHook.cs
[^golf-golfpose]: src/KSAGolf/Ksa/GolfPose.cs
[^golf-golf]: src/KSAGolf/Ksa/Golf.cs
[^golf-headlook]: src/KSAGolf/Sim/HeadLook.cs
[^golf-mod]: src/KSAGolf/Ksa/KSAGolfMod.cs
[^golf-kittenarms]: docs/KITTEN-ARMS.md
[^golf-claude]: CLAUDE.md
[^golf-test-headlook]: tests/KSAGolf.Tests/HeadLookTests.cs
[^ksa-kitteneva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
[^ksa-kittenrenderable]: KSA.KittenRenderable (decompiled, build 2026.10.10.5554)
[^ksa-animatedrenderable]: KSA.AnimatedRenderable (decompiled, build 2026.10.10.5554)
[^ksa-supermesh]: KSA.SuperMeshRenderSystem (decompiled, build 2026.10.10.5554)
[^ksa-shadowrenderable]: KSA.ShadowRenderable (decompiled, build 2026.10.10.5554)
[^ksa-kittenlocomotion]: KSA.KittenLocomotion (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-skeleton]: RenderCore.Animation.Skeleton (decompiled, build 2026.10.10.5554)
[^ksa-ivaseat]: KSA.IVASeat (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
