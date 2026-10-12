# Game Feature

* [Drive the main camera through Fixed mode without crashing it, with a stable up vector](overview.md) - What Fixed mode requires (a followed object, a non-zero CameraRotation, a way out for the player) and how mods keep the horizon level with their own up vector

# How-To

* [Add the up vector the stock FixedController lacks and solve up and aim together for a mod-simulated body](drive-the-main-camera-in-fixed-mode-6.md) - KSArmory's controller subclass supplies the roll the stock FixedController derives from the followed object's type, which is wrong for a round the mod simulates itself
* [Aim an unfollowed Fixed camera on a secondary viewport with LookAt and the platform's local up](drive-the-main-camera-in-fixed-mode-4.md) - KSArmory's camera window unfollows every frame, sets Fixed and writes the pose with Camera.LookAt using the optical head's boresight as the up vector so the horizon sits level
* [Carry a rate-limited StableUp reference instead of switching up vectors near the vertical](drive-the-main-camera-in-fixed-mode-5.md) - KSAGolf's StableUp keeps last frame's up, orthogonal to the view, and rotates it toward the wanted one by at most a bounded step scaled by how much of the wanted up lies across the view, so no hard switch happens near vertical
* [Never enter Fixed with a zero or along-axis forward, and re-assert the mode every step](drive-the-main-camera-in-fixed-mode-1.md) - KSAGolf writes CameraRotation and CameraOffset before SetCameraMode(Fixed), refuses a zero or along-up forward, and sets Fixed again each step because the player's mode key cannot leave it
* [Park the viewport in Fixed through the Mode setter, suppress the entry alert and keep CameraRotation coherent](drive-the-main-camera-in-fixed-mode-3.md) - gatOS parks the main viewport directly through the reflected Mode setter, silences the Fixed camera alert for its own park and rewrites CameraOffset and CameraRotation after each pose so a fallback into base.OnFrame never sees a zero rotation
* [Set CameraRotation before the mode and restore the previous mode explicitly because Fixed has no player exit](drive-the-main-camera-in-fixed-mode-2.md) - KSArmory records the main view's mode, follow target and field of view before its first write, and is the only thing that can put the mode back
