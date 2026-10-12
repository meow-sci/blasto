# Game Feature

* [Skipping Harmony prefixes](overview.md) - When and how to return false from a Harmony prefix in KSA to replace, veto, rewrite or consume a call, what a skipped original still owes the game, and when a postfix is safer

# How-To

* [Keyboard and mouse-binding gates with a held-key model](skip-the-original-method-with-a-prefix-5.md) - Three skipping prefixes on Program.OnKey, GameSettings.OnKeyAll and the private Program.DispatchKeyEvent keep game hotkeys from firing while a mod UI owns the keyboard, with a held-key model so releases are never stranded or leaked
* [Mouse prefix that pairs presses with releases and repairs private latches](skip-the-original-method-with-a-prefix-4.md) - Take left clicks away from Program.OnMouseButton while a tool owns them, mirror the stock gate order, consume releases only for presses you consumed, and repair the private latches that the skipped finally block would have reset
* [Re-inject a private field before a rebuild so nothing can change it back](skip-the-original-method-with-a-prefix-3.md) - Two Camera prefixes make a mod-owned field of view stick: ChangeFieldOfView is skipped for the main camera and UpdateProjection writes the override into the private _fovRadians just before the original builds the projection
* [Reproduce the original body for marked objects and return false](skip-the-original-method-with-a-prefix-1.md) - Replace Vehicle.GetWorldMatrix and Vehicle.UpdateRenderData for chosen vessels with a prefix that rebuilds the stock body minus the sub-pixel cull and skips the original, while every other vessel falls through
* [Rewrite by-ref arguments and skip when a limit would throw](skip-the-original-method-with-a-prefix-2.md) - A prefix on InstancedStarTechnique.AddInstance edits scale and color through ref parameters, returns false instead of letting a full buffer throw, and a manual constructor patch rewrites an argument by position with ref int __4
