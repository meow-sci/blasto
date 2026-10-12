# Game Feature

* [Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](overview.md) - Why a camera pose set from a StarMap GUI hook is drawn a frame late and the four ways mods write it where the engine's own controller does

# How-To

* [Ask a pose source from inside a FixedController subclass's OnFrame so the aim matches the frame being drawn](write-the-camera-pose-in-phase-with-the-frame-1.md) - KSAGolf, KSAStructures and KSArmory install a FixedController subclass that calls the borrower's IViewPose.TryPose inside the viewport pass, against the followed object's position as the engine holds it at that instant
* [Offset against the followed object sampled in the same call, with sampling and publishing on GameViewport.OnFrame](write-the-camera-pose-in-phase-with-the-frame-2.md) - gatOS' CameraPoseController reads following.GetPositionEcl() at that instant and writes PositionEcl and LocalRotation, while a prefix on the main GameViewport.OnFrame prepares the pose and a postfix publishes the clamped result
* [Pre-empt Orbit or Free by prefixing both controllers' OnFrame with one shared patch while a sequence plays](write-the-camera-pose-in-phase-with-the-frame-4.md) - Camera Controller Override patches OrbitController.OnFrame and FlyController.OnFrame with the same prefix, edits the live camera while a sequence plays and returns false to skip the stock camera math for that frame
* [Skip the stock FixedController.OnFrame for the viewports you own with a Harmony prefix](write-the-camera-pose-in-phase-with-the-frame-3.md) - Hot Pursuit leaves the controller alone and prefixes FixedController.OnFrame, returning false only for viewports it has leased, writing a pose recomposed from the part's current transform and repeating ClampCamera
