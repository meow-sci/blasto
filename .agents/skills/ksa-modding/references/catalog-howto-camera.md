# Camera

Camera modes and their controllers, writing the pose in phase with the frame, replacing or borrowing the main view, following arbitrary points, secondary viewports and part-mounted cameras, IVA, first-person kitten views, cinematic camera animation and field of view.

## Controllers

How KSA cameras are driven: modes and controllers, replacing a controller, writing the pose in phase with the frame, and Fixed mode.

### [Drive the main camera through Fixed mode without crashing it, with a stable up vector](howto/camera/controllers/drive-the-main-camera-in-fixed-mode/overview.md)

What Fixed mode requires (a followed object, a non-zero CameraRotation, a way out for the player) and how mods keep the horizon level with their own up vector

- [Never enter Fixed with a zero or along-axis forward, and re-assert the mode every step](howto/camera/controllers/drive-the-main-camera-in-fixed-mode/drive-the-main-camera-in-fixed-mode-1.md) — intermediate
- [Set CameraRotation before the mode and restore the previous mode explicitly because Fixed has no player exit](howto/camera/controllers/drive-the-main-camera-in-fixed-mode/drive-the-main-camera-in-fixed-mode-2.md) — intermediate
- [Park the viewport in Fixed through the Mode setter, suppress the entry alert and keep CameraRotation coherent](howto/camera/controllers/drive-the-main-camera-in-fixed-mode/drive-the-main-camera-in-fixed-mode-3.md) — advanced
- [Aim an unfollowed Fixed camera on a secondary viewport with LookAt and the platform's local up](howto/camera/controllers/drive-the-main-camera-in-fixed-mode/drive-the-main-camera-in-fixed-mode-4.md) — intermediate
- [Carry a rate-limited StableUp reference instead of switching up vectors near the vertical](howto/camera/controllers/drive-the-main-camera-in-fixed-mode/drive-the-main-camera-in-fixed-mode-5.md) — advanced
- [Add the up vector the stock FixedController lacks and solve up and aim together for a mod-simulated body](howto/camera/controllers/drive-the-main-camera-in-fixed-mode/drive-the-main-camera-in-fixed-mode-6.md) — advanced

### [Replace a viewport's camera controller and take input through it](howto/camera/controllers/replace-a-viewport-camera-controller/overview.md)

How mods install a FixedController or IVAController subclass into a GameViewport despite the protected setters, and how a replacement controller receives and routes camera input

- [Install a FixedController subclass by writing the auto-property backing field, check it took and restore on unload](howto/camera/controllers/replace-a-viewport-camera-controller/replace-a-viewport-camera-controller-1.md) — advanced
- [Fetch the protected setters once with GetSetMethod(nonPublic true) and park the viewport in Fixed](howto/camera/controllers/replace-a-viewport-camera-controller/replace-a-viewport-camera-controller-2.md) — advanced
- [Swap IvaController with a typed open-instance delegate and forward every input callback to a private FlyController](howto/camera/controllers/replace-a-viewport-camera-controller/replace-a-viewport-camera-controller-3.md) — advanced
- [Take right-drag look through the controller with orbit-camera constants and IsMouseDrag to keep part windows shut](howto/camera/controllers/replace-a-viewport-camera-controller/replace-a-viewport-camera-controller-4.md) — intermediate
- [Route orbit drag, club swing and builder input through the controller and keep ImGui-captured releases from sticking a drag](howto/camera/controllers/replace-a-viewport-camera-controller/replace-a-viewport-camera-controller-5.md) — advanced
- [Match the orbit camera's drag feel with 0.003 rad per pixel, a squared-2 threshold, 1.1 per wheel notch and an ease back](howto/camera/controllers/replace-a-viewport-camera-controller/replace-a-viewport-camera-controller-6.md) — intermediate

### [Understand KSA camera modes, controllers, the viewport frame order and which camera is which](howto/camera/controllers/understand-camera-modes-and-controllers/overview.md)

Reference for every camera mod covering the per-viewport Orbit, Free, Map, IVA and Fixed controllers, the viewport pass order, mode-change timing, Fixed requirements and how to pick the right camera object

- [Read the decompiled frame order, per-mode behaviour and crash inventory before writing to a camera](howto/camera/controllers/understand-camera-modes-and-controllers/understand-camera-modes-and-controllers-1.md) — intermediate
- [Read the scene camera with MainViewport.BaseCamera when the main camera may be the map camera](howto/camera/controllers/understand-camera-modes-and-controllers/understand-camera-modes-and-controllers-2.md) — beginner
- [Gate Harmony patches on Camera so they act on the main viewport's camera only](howto/camera/controllers/understand-camera-modes-and-controllers/understand-camera-modes-and-controllers-3.md) — intermediate
- [Run per-frame camera work from a prefix on the main viewport's GameViewport.OnFrame](howto/camera/controllers/understand-camera-modes-and-controllers/understand-camera-modes-and-controllers-4.md) — advanced

### [Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](howto/camera/controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md)

Why a camera pose set from a StarMap GUI hook is drawn a frame late and the four ways mods write it where the engine's own controller does

- [Ask a pose source from inside a FixedController subclass's OnFrame so the aim matches the frame being drawn](howto/camera/controllers/write-the-camera-pose-in-phase-with-the-frame/write-the-camera-pose-in-phase-with-the-frame-1.md) — advanced
- [Offset against the followed object sampled in the same call, with sampling and publishing on GameViewport.OnFrame](howto/camera/controllers/write-the-camera-pose-in-phase-with-the-frame/write-the-camera-pose-in-phase-with-the-frame-2.md) — advanced
- [Skip the stock FixedController.OnFrame for the viewports you own with a Harmony prefix](howto/camera/controllers/write-the-camera-pose-in-phase-with-the-frame/write-the-camera-pose-in-phase-with-the-frame-3.md) — advanced
- [Pre-empt Orbit or Free by prefixing both controllers' OnFrame with one shared patch while a sequence plays](howto/camera/controllers/write-the-camera-pose-in-phase-with-the-frame/write-the-camera-pose-in-phase-with-the-frame-4.md) — intermediate

## Main view

Taking over the main view: borrowing and handing back, following arbitrary points, chasing fast bodies and field of view.

### [Borrow the main camera, hand it back and arbitrate between camera users](howto/camera/main-view/borrow-the-main-camera-and-hand-it-back/overview.md)

How a mod takes the player's main view, records what it changes, returns each half only if the player did not change it meanwhile, and shares the view with other features or mods

- [Record mode, follow target and field of view before the first write, stand down when the player changes either half and restore the halves separately](howto/camera/main-view/borrow-the-main-camera-and-hand-it-back/borrow-the-main-camera-and-hand-it-back-1.md) — advanced
- [Hand back the field of view first and the follow only if it is still the borrower's, with explicit StandDown, Yield, Hold, GiveBack and Take decisions](howto/camera/main-view/borrow-the-main-camera-and-hand-it-back/borrow-the-main-camera-and-hand-it-back-2.md) — advanced
- [Record the main view before the first write when flying another craft or a first-person kitten](howto/camera/main-view/borrow-the-main-camera-and-hand-it-back/borrow-the-main-camera-and-hand-it-back-3.md) — intermediate
- [Capture the baseline pose, follow, mode and tidal locking, blend back on release and detect a second writer](howto/camera/main-view/borrow-the-main-camera-and-hand-it-back/borrow-the-main-camera-and-hand-it-back-4.md) — advanced
- [Rank several camera users with a pure ViewClaim ladder and drive the claimants in claim order](howto/camera/main-view/borrow-the-main-camera-and-hand-it-back/borrow-the-main-camera-and-hand-it-back-5.md) — advanced

### [Chase a projectile or other fast body with the main camera](howto/camera/main-view/chase-a-fast-body-with-the-main-camera/overview.md)

A mod-simulated shell, ball or missile is not a Vehicle, so the camera follows it through a custom IFollowable and a level, stable pose behind the flight path

- [Chase a round with a RoundFollowable on a level-horizon controller, the pose solved in the viewport pass](howto/camera/main-view/chase-a-fast-body-with-the-main-camera/chase-a-fast-body-with-the-main-camera-1.md) — advanced
- [Hold a line to the target near the flight path with a carried lift and lean off the singular axis](howto/camera/main-view/chase-a-fast-body-with-the-main-camera/chase-a-fast-body-with-the-main-camera-2.md) — advanced

### [Make the camera follow a point or mod-simulated object, and change the follow target without a jump](howto/camera/main-view/follow-a-point-that-is-not-a-vehicle/overview.md)

Camera.Following takes any IFollowable, so a mod object can be followed with no registration; what the interface must answer, and how SetFollow's teleport and control swap are avoided

- [Follow a point with an anchor IFollowable that re-resolves a vessel, part, body or held point on every call](howto/camera/main-view/follow-a-point-that-is-not-a-vehicle/follow-a-point-that-is-not-a-vehicle-1.md) — intermediate
- [Report the drawn position of a mod-simulated round from a RoundFollowable](howto/camera/main-view/follow-a-point-that-is-not-a-vehicle/follow-a-point-that-is-not-a-vehicle-2.md) — advanced
- [Change the follow target with changeControl and alert false, then write the captured position back](howto/camera/main-view/follow-a-point-that-is-not-a-vehicle/follow-a-point-that-is-not-a-vehicle-3.md) — intermediate
- [Follow on the main viewport without taking control, with follow and mode kept as separate halves](howto/camera/main-view/follow-a-point-that-is-not-a-vehicle/follow-a-point-that-is-not-a-vehicle-4.md) — intermediate
- [Re-apply the follow after every viewport claim on a secondary camera](howto/camera/main-view/follow-a-point-that-is-not-a-vehicle/follow-a-point-that-is-not-a-vehicle-5.md) — intermediate

### [Override the camera field of view and zoom past the stock clamp](howto/camera/main-view/override-the-camera-field-of-view/overview.md)

Camera.SetFieldOfView takes degrees without clamping, GetFieldOfView answers radians, the zoom keys clamp to 15 to 120 degrees and the projection throws outside 0 to 180, and three ways mods hold a field

- [Hold a lens field of view with prefixes on Camera.UpdateProjection and ChangeFieldOfView, main camera only](howto/camera/main-view/override-the-camera-field-of-view/override-the-camera-field-of-view-1.md) — intermediate
- [Call SetFieldOfView in degrees, read GetFieldOfView in radians and zoom past the player clamp](howto/camera/main-view/override-the-camera-field-of-view/override-the-camera-field-of-view-2.md) — beginner
- [Treat the field of view as one half of a borrowed view and always return it first](howto/camera/main-view/override-the-camera-field-of-view/override-the-camera-field-of-view-3.md) — beginner

## Special views

IVA, first-person kitten views and scripted cinematic camera animation.

### [Animate the flight camera with eased moves, keyframe sequences and saved recipes](howto/camera/special-views/chain-camera-moves-into-keyframe-sequences/overview.md)

Small animation classes that edit the live Orbit or Free camera each frame, queued into a sequence that plays, pauses, groups moves, blends back to the start and is stored as validated recipe JSON

- [Queue animations into a KeyframeSequencePlayer with groups and return-to-start, and save the recipes with the scene](howto/camera/special-views/chain-camera-moves-into-keyframe-sequences/chain-camera-moves-into-keyframe-sequences-1.md) — intermediate
- [Write eased single moves as IKeyframeAnimation classes driven from the OnFrame prefix](howto/camera/special-views/chain-camera-moves-into-keyframe-sequences/chain-camera-moves-into-keyframe-sequences-2.md) — intermediate

### [Put the camera in an EVA kitten's eyes, with mouse look and look-driven walking](howto/camera/special-views/first-person-camera-for-a-kitten/overview.md)

How a mod reads the head bone, borrows the main view in Fixed mode at that point, keeps a mouse look in a world frame and makes the walking kitten face where the player looks

- [Read the head bone through an IAnimProcessor, pose the eye in the viewport pass and walk where the kitten looks](howto/camera/special-views/first-person-camera-for-a-kitten/first-person-camera-for-a-kitten-1.md) — advanced
- [Pose the eye from the head bone in the golf pose with a pitch-tilted up, a captured-cursor look and a drawn turn for a standing kitten](howto/camera/special-views/first-person-camera-for-a-kitten/first-person-camera-for-a-kitten-2.md) — advanced

### [Play scripted cinematic camera tracks with blends and easing](howto/camera/special-views/play-cinematic-camera-tracks/overview.md)

KSA has no camera track system, so mods compose keyframed shots over the player's camera in game-free code and apply them from the viewport pass, blending in and out

- [Compose each frame's pose from keyframed tracks, manual overrides and a captured baseline](howto/camera/special-views/play-cinematic-camera-tracks/play-cinematic-camera-tracks-1.md) — advanced
- [Smoothstep the eye, lerp the look at equal depth and pace the ease with an averaged step](howto/camera/special-views/play-cinematic-camera-tracks/play-cinematic-camera-tracks-2.md) — advanced
- [Blend back to the start pose when a sequence ends](howto/camera/special-views/play-cinematic-camera-tracks/play-cinematic-camera-tracks-3.md) — intermediate
- [Release with a blend and shape tracks with named easing functions](howto/camera/special-views/play-cinematic-camera-tracks/play-cinematic-camera-tracks-4.md) — advanced

### [Let the IVA camera leave the seat and fly around the cabin](howto/camera/special-views/unlock-the-iva-camera/overview.md)

How IVA mode is pinned to a seat, what depends on the mode staying IVA, and the controller-swap approach that frees the camera without losing interiors, audio or ray tracing

- [Let the IVA camera leave the seat with an IVAController subclass that delegates movement to a private FlyController](howto/camera/special-views/unlock-the-iva-camera/unlock-the-iva-camera.md) — advanced

## Secondary viewports

Secondary viewports: leasing one, aiming its camera and mounting cameras on parts.

### [Aim a secondary viewport camera, rebuild its matrices and give it its nearby body](howto/camera/secondary-viewports/aim-a-secondary-viewport-camera/overview.md)

A secondary camera follows nothing and has its matrices built before a GUI hook runs, and the engine fills its nearby planet only for the frame camera; two ways to aim it correctly

- [Aim from the GUI hook with LookAt, then rebuild the matrices and per-viewport shader data and fill the nearby body yourself](howto/camera/secondary-viewports/aim-a-secondary-viewport-camera/aim-a-secondary-viewport-camera-1.md) — advanced
- [Write the pose in a FixedController.OnFrame prefix before Camera.OnFrame and synchronize the nearby body inside it](howto/camera/secondary-viewports/aim-a-secondary-viewport-camera/aim-a-secondary-viewport-camera-2.md) — advanced

### [Lease a secondary viewport for a mod camera and show it in a window](howto/camera/secondary-viewports/borrow-a-secondary-viewport/overview.md)

KSA seals its viewport registry at boot and offers four spare secondary camera windows; how mods claim, configure, track and release one and show its picture in KSA's window or their own

- [Open a secondary viewport, make it visible and re-resolve its index by reference every frame](howto/camera/secondary-viewports/borrow-a-secondary-viewport/borrow-a-secondary-viewport-1.md) — intermediate
- [Claim a viewport per camera with an IViewportOwner, track the lease every update and keep KSA's window](howto/camera/secondary-viewports/borrow-a-secondary-viewport/borrow-a-secondary-viewport-2.md) — intermediate
- [Clear HasUi and RenderOrbitLines through the reflected OptionFlags setter and draw the viewport texture in your own window](howto/camera/secondary-viewports/borrow-a-secondary-viewport/borrow-a-secondary-viewport-3.md) — advanced

### [Mount a camera on a vessel part and look through it](howto/camera/secondary-viewports/mount-a-camera-on-a-part/overview.md)

Posing a secondary or main camera from a sub-part's transform so it rides a nose, mast or turret, including scaled and articulated parts and mounts that survive staging

- [Pose a camera from the part's Asmb2Ego matrices in a FixedController.OnFrame prefix](howto/camera/secondary-viewports/mount-a-camera-on-a-part/mount-a-camera-on-a-part-1.md) — advanced
- [Resolve the eye, sight line and roll from an optical head sub-part for the main view or a camera window](howto/camera/secondary-viewports/mount-a-camera-on-a-part/mount-a-camera-on-a-part-2.md) — advanced
- [Click a hull point and store it in the hit sub-part's local frame](howto/camera/secondary-viewports/mount-a-camera-on-a-part/mount-a-camera-on-a-part-3.md) — intermediate
