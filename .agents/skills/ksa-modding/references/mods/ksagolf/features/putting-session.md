---
type: Mod Feature
title: Putting session with a mouse-swung club
description: The flown kitten carries a putter, drops a ball, steps up to it with G and swings with the mouse, the session tying the drawn pose, the borrowed view, the simulated ball and the strike sound together across the frame
mod: ksagolf
category: kittens
techniques:
  - starmap-lifecycle
  - anim-processor
  - runtime-attachment
  - mouse-capture
  - controller-subclass
  - headless-tests
hooks:
  - starmap StarMapAfterGui
  - starmap StarMapAfterOnFrame
  - controller-override KSA.FixedController.OnCursorPos (LevelHorizonController)
  - controller-override KSA.FixedController.OnFrame (LevelHorizonController asks IViewPose)
  - harmony-postfix KSA.SuperMeshRenderSystem.ClearBuckets
  - engine-callback KSA.IAnimProcessor.UpdateLocalPose/UpdateSkeleton
lifecycle: [frame:input, frame:viewports, frame:draw-ui, frame:render, frame:after-on-frame]
game_types:
  - KSA.KittenEva
  - KSA.Program
  - KSA.Celestial
  - KSA.CharacterAvatar
  - KSA.StaticMeshRenderable
  - KSA.Camera
  - KSA.FixedController
  - Brutal.ImGuiApi.ImGui
  - Brutal.GlfwApi.GlfwWindowAttribute
tags: [golf, putting, kitten, mouse, input, imgui, stroke, ball, first-person, bridge]
---

Putting is KSAGolf's one playable golf feature. With **Play golf** on (the default), the kitten
the player flies picks up a putter. **Drop a ball** puts a ball just ahead of the kitten's feet. G
steps the kitten up to the ball, side-on to a target on its left, and moving the mouse swings the
club: away from the target to take it back, then towards it to strike. G again steps away. V
switches between third person and looking out through the kitten's eyes.[^golf-golf][^golf-ui]

`Ksa/Golf.cs` is the session that ties the pieces together. It does not draw the kitten or
integrate the ball itself. It holds the state those features read, and it drives them from the
right hooks:

- the drawn pose ([Putting address pose](../features/putting-address-pose.md))
- the putter on the paw ([Kitten held props](../features/kitten-held-props.md))
- the simulated ball ([Golf ball physics](../features/golf-ball-physics.md))
- the borrowed camera ([Level-horizon camera controller](../features/level-horizon-camera-controller.md))
- the ball draw ([Mod mesh draw hook](../features/mod-mesh-draw-hook.md))
- the strike sound ([World-positioned sounds](../features/world-positioned-sounds.md))

## How it works

| Phase | What `Golf` does |
|---|---|
| Pick up | Each step, if `PlayGolf` is on and the controlled vehicle is a `KittenEva`, `Hold` builds the putter, adds it to `Wrist_R`, adds `GolfPose` to the model's `AnimProcessors`, and hides the MMU and helmet (restored on `LetGo`). A change of kitten lets go of the old one first |
| Drop | `DropBall` converts the rig's ball spot to the body-fixed (CCF) frame through `KittenFrame.ToEcl`, finds the surface radius there (height field, sea, launch-site top, or a laid course), and places a `GolfBall` with its lowest point 1 cm above it. The ball mesh is built once with `AttachmentMesh` |
| Address (G) | `TryAddress` needs a resting ball on the same body. The target is the kitten's left, or in first person the left of where the player looks, so stepping up never turns the view. The third-person view offset is stored in CCF so it turns with the ground. `Stance.TryFor` gives the drawn step. The stroke is reset, and the mouse direction that hits is chosen from where the target lies on screen |
| Swing | Mouse x movement accumulates in `_dx`. After 0.5 s of settling, `PuttingStroke.Update` turns it into a club angle (0.0035 rad per pixel, clamped to ±1.3 rad) and tests for the face crossing the ball. The strike calls `GolfBall.Strike(target × speed)` and `GolfSounds.Play` |
| Follow-through | The club keeps moving; once the ball rests or 6 s pass, the session leaves address and reports the distance rolled |
| Abort | If the real body drifts more than 15 cm from where it stood, or the ball moves before the strike, it steps away |

**The strike (`Sim/PuttingStroke.cs`).** The swing rate is averaged over a 60 ms window because
mouse input arrives in bursts. The averaged rate is clamped to ±40 rad/s, since anything faster is
a mouse jump rather than a stroke.

A strike needs all of these:

- the club moving forward,
- the leading face crossing `ball.X - radius` this update,
- the sole below the ball's centre.

The launch speed is `1.4 × rate × swing radius`. The factor 1.4 is (1 + e)·M/(M + m) for a rubber
face with e ≈ 0.6 and a head eight times the ball's mass. The stroke runs on **player** time: it is
the player's hand. The ball it launches runs on **simulated** time.[^golf-stroke][^golf-test-stroke]

**Input taken from the game.**

- **G and V** are read with `ImGui.IsKeyPressed` from the GUI pass, not through KSA's key bindings.
  They are skipped while ImGui wants text input or while the course builder has suspended golf.
- **Mouse movement** reaches the session through the borrowed camera controller.
  `LevelHorizonController.OnCursorPos` hands the position to `IViewPose.Swing` (the `Golf` object)
  and returns true, so nothing else sees it.
- **Left clicks are swallowed**, and the cursor is put in `Disabled` mode while a swing is possible
  so it can move without bound. The first jump after capture (over 400 px) is ignored.
- **Released when wanted.** The code means to give the mouse back when the game window is not
  focused, while Alt is held, or while a KSA window is open (`Program.IsWindowOpen`), so the panel
  stays reachable. The window test has no effect where it is asked; see Pitfalls.[^golf-golf][^golf-levelhorizon]

**The bridge `golf` command** drives the same session without a hand on the mouse:

- `action` is `drop`, `pickup` or `address` (address also steps away).
- `look_yaw_deg` turns the first-person look.
- `swing` is a list of pixel movements, fed one per frame through `Nudge` as if the mouse had moved.
- `wait_s` keeps the command open before it answers with `Golf.Report()`: phase, angle, struck,
  ball speed and position in kitten model space, the stance, the last sound, and a ground
  profile.[^golf-bridge]

## Lifecycle and timing

| Work | Hook | Lifecycle |
|---|---|---|
| Mouse position into `_dx` or the head look | `LevelHorizonController.OnCursorPos`, called from GLFW callbacks during `Glfw.PollEvents` in `Program.PrepareFrame`[^ksa-imguibackend][^ksa-program] | `frame:input` |
| The view is posed: `IViewPose.TryPose` | `LevelHorizonController.OnFrame` in `Program.OnFrameViewports` | `frame:viewports` |
| `Golf.Step`: hold or let go, step the ball on `dtSim`, blend on `dtPlayer`, swing, borrow or hand back the view | `KSAGolfMod.StepOnce`, which runs from `[StarMapAfterGui]` (postfix on `OnDrawUiViewports`) when the HUD is drawn, from the `OnFrameCelestials` prefix when it is not, and from `[StarMapAfterOnFrame]` as the last fallback; a frame latch makes it run once[^golf-mod][^starmap-programpatcher] | `frame:draw-ui` |
| `Golf.Keys`: G and V | `[StarMapAfterGui]` only | `frame:draw-ui` |
| Pose processor and club placement; ball and first-person club drawn | Inside `Program.RenderGame` / `RenderViewport` | `frame:render` |
| Bridge `golf` command, scripted `Nudge` | `[StarMapAfterOnFrame]` | `frame:after-on-frame` |

The step runs in the GUI postfix because that hook comes after `PrepareFrame` has applied the
finished physics step, so the kitten and the planet positions are this frame's. Like every StarMap
frame hook it also runs after `PrepareFrame` has dispatched the next physics step; that is safe here
because the step writes only mod state (ball, stroke, pose inputs, the borrowed view) and render-side
kitten data (the processor list, cosmetic attachments, MMU and helmet flags), never the kitten's
simulated state. It also comes
before the render, so anything the step writes is drawn this frame.[^ksa-program] The one thing
that cannot be done there is aiming the camera, because the viewport pass has already built this
frame's matrices. That is why the view is posed again from inside the controller (see
[Level-horizon camera controller](../features/level-horizon-camera-controller.md)). The mouse
arrives earlier in the frame, during input polling, and is only accumulated there. The angle and
the strike are applied in the step, so the input handler never touches the ball or the pose.

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L126-L174) | `Step` and `DriveView`: the per-frame session |
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L233-L250) | `Keys`: G and V through ImGui |
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L286-L315) | `DropBall` |
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L428-L439) | `Cursor`: mouse movement from the controller |
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L471-L538) | `Hold` / `LetGo` |
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L542-L655) | `TryAddress`, `Swinging`, `Leave` |
| [src/KSAGolf/Sim/PuttingStroke.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/PuttingStroke.cs) | Mouse to club angle, smoothed rate, strike test and launch speed |
| [src/KSAGolf/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Bridge.cs#L300-L341) | The bridge `golf` command |
| [src/KSAGolf/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Ui/Ui.cs#L141-L181) | Panel controls: Play golf, First-person view (V), Drop, Pick up, Address (G) |
| [tests/KSAGolf.Tests/PuttingStrokeTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/PuttingStrokeTests.cs) | Strike once, backswing does not strike, bursty input averaged, twitch at address does not reach the ball |

## Snippets

```csharp
// src/KSAGolf/Ksa/Golf.cs L126-L146
    /// <summary>One simulation step, from whichever hook takes the frame.</summary>
    public void Step(double dtSim, double dtPlayer)
    {
        KittenEva? kitten = _config.PlayGolf && KsaWorld.InFlightScene ? KsaWorld.ControlledVehicle as KittenEva : null;
        if (!ReferenceEquals(kitten, _kitten))
        {
            LetGo();
            if (kitten is not null) Hold(kitten);
        }

        StepBall(dtSim);

        if (_kitten is null) return;
        if (_kitten.Parent is Celestial world) PadSurface.Want(world);

        double towards = _phase == Phase.Addressing ? 1.0 : 0.0;
        _blend = Math.Clamp(_blend + (Math.Sign(towards - _blend) * dtPlayer / StepSeconds), 0.0, 1.0);

        if (_phase == Phase.Addressing) Swinging(dtPlayer);
        DriveView();
    }
```

```csharp
// src/KSAGolf/Ksa/Golf.cs L233-L239
    /// <summary>The keys, from the GUI pass: ImGui is what sees them.</summary>
    public void Keys()
    {
        if (!_config.PlayGolf || _suspended || ImGui.GetIO().WantTextInput) return;
        if (ImGui.IsKeyPressed(ImGuiKey.G, repeat: false)) AddressOrStepAway();
        if (ImGui.IsKeyPressed(ImGuiKey.V, repeat: false)) _config.FirstPerson = !_config.FirstPerson;
    }
```

```csharp
// src/KSAGolf/Sim/PuttingStroke.cs L52-L68
    public bool Update(double dxPixels, double dtPlayer, double towardsTarget, PutterRig rig, out double launchSpeed)
    {
        launchSpeed = 0.0;
        double before = Theta;
        Theta = Math.Clamp(Theta + (dxPixels * RadiansPerPixel * Math.Sign(towardsTarget)), -MaxTheta, MaxTheta);
        Rate = Smoothed(Math.Max(dtPlayer, 0.0), Theta - before);

        if (Struck || Theta <= before) return false;

        double contact = rig.Ball.X - PutterRig.BallRadiusCm;
        if (rig.LeadingFaceX(before) >= contact || rig.LeadingFaceX(Theta) < contact) return false;
        if (rig.SoleY(Theta) > rig.Ball.Y) return false;

        Struck = true;
        launchSpeed = Transfer * Rate * rig.SwingRadiusCm / 100.0;
        return launchSpeed > 0.0;
    }
```

```csharp
// src/KSAGolf/Ksa/Golf.cs L635-L646
        _sinceAddress += dtPlayer;
        if (KsaWorld.IsPaused || _sinceAddress < SettleSeconds) return;

        if (_stroke.Update(dx, dtPlayer, _towards, _rig, out double speed))
        {
            _struckFromCcf = ball.PositionCcf;
            ball.Strike(_targetCcf * speed);
            GolfSounds.Play(StrikeSound.ForStrike(speed), kitten, BallEcl());
            _sinceStrike = 0.0;
            Status = $"Struck at {speed:F2} m/s.";
            Log.Info($"struck the ball at {speed:F2} m/s (swing {double.RadiansToDegrees(_stroke.Rate):F0} deg/s)");
        }
```

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `Program.ControlledVehicle`, `IsWindowOpen`, `GetWindow`, `GetMainCamera`, `MainViewport` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L318) | Who is flown; whether to give the cursor back; focus; the view to borrow |
| `Program.OnFrame` / `PrepareFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2333) | Frame order the session's hooks sit in |
| `ImGuiBackendGlfwImpl.OnCursorPos` → `Program.OnCursorPos` | [current/decomp/KSA/ImGuiBackendGlfwImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendGlfwImpl.cs#L463-L476) | Cursor events reach the active controller during event polling |
| `GameViewport.GetActiveController` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L284-L295) | Fixed mode routes input to the (replaced) `FixedController` |
| `Celestial.GetCcf2Cce` / `GetCce2Ccf` / `GetTerrainHeightFromDirCcf` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L532-L541) | Body-fixed frame for the ball, the target and the held view |
| `ImGui.IsKeyPressed`, `ImGui.GetIO().WantTextInput` / `KeyAlt` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs) | G and V, and the guards |

## Pitfalls and version notes

- **G and V need the HUD.** `Keys()` runs only from `[StarMapAfterGui]`, which KSA skips when the
  HUD is hidden (F2). The step still runs then, but the keys do not. The panel buttons have the same
  limit, so with the HUD hidden only the bridge can address the ball.[^golf-mod]
- **Not rebindable.** The keys are ImGui key reads, not KSA input actions, so they do not appear in
  KSA's controls settings.
- **The "KSA window open" release never fires.** `Golf.Swing` and `Golf.Orbit` test
  `Program.IsWindowOpen`, a static that KSA windows set to true while they draw in the UI pass and
  that `Program.OnDrawUiConsole` resets to false at the end of every frame's UI. The controller asks
  `Swing` and `Orbit` from GLFW callbacks during `PrepareFrame` and from `GetCursorMode` in
  `OnFrameController`, both outside the UI pass, where the flag is always false (both builds). In
  practice only Alt and window focus give the cursor back.[^ksa-program][^golf-golf][^golf-levelhorizon]
- **Player time against simulated time.** The step-up blend and the stroke use `dtPlayer`. The ball
  uses the simulated step. While paused the stroke is frozen (`IsPaused`), but the blend still
  completes.
- **Only one kitten.** The session follows `Program.ControlledVehicle`. Switching to another vessel
  lets go of the putter, and losing the controlled kitten is handled through `InFlightScene`, not
  `InFlight`.[^golf-claude]
- **Verified status.** The mod's CLAUDE.md lists what has been seen in game since its last cut:
  loading with `KSAGolfProps.xml`, the bridge's `frame`, `capture` and `kitten_prop`, and the putter
  and ball drawn on a kitten. The full swing and the strike are covered by the headless tests but
  are not on that list.[^golf-claude]
- **Version notes.** Built against 2026.9.22.5482. The input path (`ImGuiBackendGlfwImpl` callbacks
  into `Program.OnCursorPos`, then the active controller) and the `Program.OnFrame` order are the
  same in 2026.10.10.5554.

## See also

- [Putting address pose drawn on the kitten skeleton](../features/putting-address-pose.md)
- [Golf ball physics in the body-fixed frame](../features/golf-ball-physics.md)
- [Level-horizon camera controller swapped into the main viewport](../features/level-horizon-camera-controller.md)
- [First-person kitten view that keeps the shadow](../features/first-person-kitten-view.md)
- [Drawing a mod's static mesh into every viewport](../features/mod-mesh-draw-hook.md)
- [World-positioned sounds](../features/world-positioned-sounds.md)
- [Dual-hook frame stepping on simulated time](../features/dual-hook-frame-stepping.md)
- [Launch-site surface by raycast into a copied collider](../features/launch-site-surface-raycast.md)
- [File command bridge and MCP server](../features/file-command-bridge.md)
- [Developer panel and menu-bar entry](../features/developer-panel-and-menu-bar.md)
- [Hide the helmet, visor and MMU per avatar by clearing Helmet.Enabled and Mmu.Enabled](../../../howto/kittens/appearance/tint-and-restyle-kitten-appearance/tint-and-restyle-kitten-appearance-5.md) - how-to article demonstrating this feature
- [Capture the cursor and drive a kitten action with raw mouse deltas](../../../howto/kittens/eva/steer-a-walking-kitten/steer-a-walking-kitten-2.md) - how-to article demonstrating this feature
- [A file-drop bridge with in/ and out/ folders, atomic replies and multi-frame closures](../../../howto/testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-1.md) - how-to article demonstrating this feature
- [Get and set every config field by reflection, and script gameplay as composed commands](../../../howto/testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-2.md) - how-to article demonstrating this feature

[^golf-golf]: src/KSAGolf/Ksa/Golf.cs
[^golf-ui]: src/KSAGolf/Ksa/Ui/Ui.cs
[^golf-stroke]: src/KSAGolf/Sim/PuttingStroke.cs
[^golf-test-stroke]: tests/KSAGolf.Tests/PuttingStrokeTests.cs
[^golf-levelhorizon]: src/KSAGolf/Ksa/LevelHorizonController.cs
[^golf-bridge]: src/KSAGolf/Ksa/Bridge.cs
[^golf-mod]: src/KSAGolf/Ksa/KSAGolfMod.cs
[^golf-claude]: CLAUDE.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-imguibackend]: KSA.ImGuiBackendGlfwImpl (decompiled, build 2026.10.10.5554)
[^starmap-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
