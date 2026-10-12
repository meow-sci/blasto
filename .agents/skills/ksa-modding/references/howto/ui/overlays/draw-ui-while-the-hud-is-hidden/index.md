# Game Feature

* [Keep mod UI and per-frame work running when the HUD is hidden (F2)](overview.md) - Why StarMap's GUI hooks stop when the player hides the HUD, which game calls still run, and four ways mods keep drawing or stepping

# How-To

* [Keep drawing when the HUD is hidden: Harmony postfix on Program.OnFrameEditor](draw-ui-while-the-hud-is-hidden-1.md) - Hang a per-frame ImGui draw on the private Program.OnFrameEditor, which runs after NewFrame and outside the DrawUI gate, so it survives F2
* [Keep drawing when the HUD is hidden: reflection-installed postfix on the private Program.DrawFps](draw-ui-while-the-hud-is-hidden-2.md) - Hook the one UI-phase call that always runs without a compile-time Harmony reference, with graceful degradation and the failure shown in the settings UI
* [Keep per-frame work running when the HUD is hidden: replay StarMap GUI work from a prefix on OnDrawUiConsole](draw-ui-while-the-hud-is-hidden-3.md) - Re-run the work of your StarMapBeforeGui method on hidden-HUD frames from a Harmony prefix on Program.OnDrawUiConsole, deliberately leaving window drawing out
* [Run per-frame mod logic once per frame from whichever hook fires first (FrameLatch, pre-render prefix and sim-time steps)](draw-ui-while-the-hud-is-hidden-4.md) - Step a mod's simulation exactly once per frame from the GUI hook when the HUD is drawn, a prefix on OnFrameCelestials when it is hidden and the frame postfix as the fallback, on the engine's simulated time
