# Game Feature

* [Override the camera field of view and zoom past the stock clamp](overview.md) - Camera.SetFieldOfView takes degrees without clamping, GetFieldOfView answers radians, the zoom keys clamp to 15 to 120 degrees and the projection throws outside 0 to 180, and three ways mods hold a field

# How-To

* [Call SetFieldOfView in degrees, read GetFieldOfView in radians and zoom past the player clamp](override-the-camera-field-of-view-2.md) - KSArmory clamps its own writes to 1 to 120 degrees because the projection throws outside 0 to 180, restates the field every frame because the zoom keys clamp at 15, and resets released camera windows to 50 degrees
* [Hold a lens field of view with prefixes on Camera.UpdateProjection and ChangeFieldOfView, main camera only](override-the-camera-field-of-view-1.md) - Glass writes the private _fovRadians in a prefix on UpdateProjection and swallows the stock zoom keys in a prefix on ChangeFieldOfView, both gated on ViewportRegistry.IsMainCamera, with lens presets driven from a static FovController and saved with the scene
* [Treat the field of view as one half of a borrowed view and always return it first](override-the-camera-field-of-view-3.md) - KSAGolf records the main view's field of view with the mode and follow, clamps its own writes to 1 to 120 degrees and restores the field before the mode because SetFieldOfView does not clamp and the player's zoom keys cannot widen a narrow value
