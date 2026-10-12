---
type: Mod Feature
title: Montserrat fonts in the ImGui atlas
description: Lazily adds bundled Montserrat TTFs to the game's ImGui font atlas on the first UI frame, renders them at arbitrary sizes with the two-argument PushFont, and draws tabular digits and drop shadows on a draw list
mod: ksa-public-telemetry
category: ui
techniques: [imgui-font, imgui-drawlist]
hooks: [starmap StarMapImmediateLoad, starmap StarMapAfterGui, harmony-postfix KSA.Program.DrawFps]
lifecycle: [startup:immediate-load, frame:draw-ui]
game_types:
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImFontAtlasPtr
  - Brutal.ImGuiApi.ImFontConfig
  - KSA.FontManager
  - KSA.Mod
tags: [imgui, fonts, ttf, typography, draw-list]
---

The overlay's broadcast look depends on its typeface: Montserrat Bold for numbers and labels and
Montserrat Regular for body text, shipped in a `montserrat/` folder next to the DLL with the SIL Open
Font License. The fonts go into the same ImGui atlas the game uses, so any ImGui call or draw list can
use them, and if the files are missing the overlay quietly falls back to the game font.

## How it works

1. **Find the files.** `[StarMapImmediateLoad]` receives the game's `Mod` object and stores
   `mod.DirectoryPath` ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L32-L37); `Mod.DirectoryPath` is the folder of the
   mod's `mod.toml`, [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L91-L109)).[^ksa-mod] `OverlayFonts.Resolve` tries
   `<mod>/montserrat/<file>`, then `<mod>/<file>`, then a path relative to the working directory
   ([Rendering/OverlayFonts.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayFonts.cs#L80-L99)).
2. **Ship them.** The csproj links `montserrat/*.ttf` and `OFL.txt` into the output with
   `CopyToOutputDirectory`, with a comment that the OFL must travel with the font
   ([KSATelemetryOverlay.csproj](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/KSATelemetryOverlay.csproj#L15-L29)).[^kpt-csproj]
3. **Load once, lazily.** `OverlayFonts.TryLoad` is the first call in every overlay draw and returns
   immediately after the first attempt ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L98-L110),
   [Rendering/OverlayFonts.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayFonts.cs#L21-L62)). It loads three `ImFontPtr`s (`Numeric` from Bold at 44 px,
   `Label` from Bold at 19 px, `Body` from Regular at 20 px) through
   `ImGui.GetIO().Fonts.AddFontFromFileTTF(path, size, &config)`.
4. **Same config as the game.** `Load` fills an `ImFontConfig` with `OversampleH = 3`, `OversampleV = 2`,
   `GlyphMaxAdvanceX = float.MaxValue`, `RasterizerMultiply = 1` and an explicit `SizePixels`, the same
   fields `KSA.FontManager.LoadFont` sets for the game's own fonts (the mod uses density 1 and no pixel
   snap where the game reads both from settings) ([Rendering/OverlayFonts.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayFonts.cs#L64-L78),
   [current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L100-L114)).[^ksa-fontmanager]
5. **Any size at draw time.** Text is drawn with `ImGui.PushFont(font, sizePixels)`, the ImGui 1.92
   two-argument overload Brutal exposes, so one loaded font renders crisply at every scale the player
   picks; a null font pushes `default`, which keeps the current font at the requested size
   ([Rendering/Gfx.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Gfx.cs#L312-L353), [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5872)).[^imgui-imgui]
6. **Tabular digits.** Montserrat has proportional figures, which makes a running clock jitter.
   `Gfx.TextTabular` measures the widest digit and centres every digit in a cell of that width
   ([Rendering/Gfx.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Gfx.cs#L355-L419)). Every text call also draws a 1 px offset shadow first
   ([Rendering/Gfx.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Gfx.cs#L305-L310)).

## Lifecycle and timing

- **Loaded on the first draw, not at startup.** Adding a font needs a live ImGui context and atlas. The
  game creates both in the `Program` constructor before `ModLibrary.LoadAll`
  (`ImGui.CreateContext` and `FontManager.Initialize`,
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L842-L858)), so `[StarMapAllModsLoaded]` would also work; the mod
  simply loads in the first overlay draw (`[StarMapAfterGui]`, or the `DrawFps` postfix if the game
  starts with the UI hidden), once it knows it will draw. Only `mod.DirectoryPath` is taken earlier, in
  `startup:immediate-load`.
- **Mid-frame is legal here.** The game's Vulkan ImGui backend sets
  `ImGuiBackendFlags.RendererHasTextures` ([current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L658)), ImGui 1.92's
  dynamic atlas mode, in which fonts can be added after `NewFrame` and glyphs are rasterised on demand
  at whatever size `PushFont` asks for.[^ksa-imguibackendvulkanimpl]
- **Draw path.** `PushFont`/`PopFont` pairs happen inside the overlay draw (`frame:draw-ui`), between
  `ImGui.NewFrame` and `ImGui.Render`.

## Key code

| Path | Role |
|---|---|
| [Rendering/OverlayFonts.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayFonts.cs#L21-L99) | Resolve, load once, fallback |
| [Rendering/Gfx.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Gfx.cs#L312-L353) | `PushFont(font, size)` helpers and measuring |
| [Rendering/Gfx.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Gfx.cs#L355-L419) | Tabular digits |
| [KSATelemetryOverlay.csproj](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/KSATelemetryOverlay.csproj#L15-L29) | Ship TTFs and OFL with the DLL |

## Snippets

```csharp
// Rendering/OverlayFonts.cs L28-L50, L64-L78
        _attempted = true;

        try
        {
            string? regular = Resolve(modDirectory, RegularFile);
            string? bold = Resolve(modDirectory, BoldFile);

            if (regular is null && bold is null)
            {
                Console.WriteLine(
                    "[KSATelemetryOverlay] Montserrat not found; using the game font.");
                return;
            }

            string numericSource = bold ?? regular!;
            string labelSource = bold ?? regular!;
            string bodySource = regular ?? bold!;

            ImFontAtlasPtr atlas = ImGui.GetIO().Fonts;

            Numeric = Load(atlas, numericSource, ClockSize);
            Label = Load(atlas, labelSource, LabelSize);
            Body = Load(atlas, bodySource, BodySize);
// ...
    private static unsafe ImFontPtr Load(ImFontAtlasPtr atlas, string path, float sizePixels)
    {

        ImFontConfig config = default;

        config.OversampleH = 3;
        config.OversampleV = 2;
        config.PixelSnapH = false;
        config.GlyphMaxAdvanceX = float.MaxValue;
        config.RasterizerMultiply = 1f;
        config.RasterizerDensity = 1f;
        config.SizePixels = sizePixels;

        return atlas.AddFontFromFileTTF(path, sizePixels, &config, default);
    }
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ImFontAtlasExtensions.AddFontFromFileTTF` | [current/decomp/Brutal.ImGuiApi/ImFontAtlasExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImFontAtlasExtensions.cs#L18-L24) | Add a TTF to the shared atlas |
| `ImFontConfig` (`OversampleH`, `RasterizerDensity`, ...) | [current/decomp/Brutal.ImGuiApi/ImFontConfig.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImFontConfig.cs#L71) | Rasteriser settings |
| `ImGui.PushFont(ImFontPtr, float)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5872) | Font plus size per draw |
| `FontManager.LoadFont`, `FontManager.RegenerateFonts` | [current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L55-L59), [current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L100-L114) | The game's equivalent load, and the atlas reset (pitfall) |
| `Mod.DirectoryPath` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L91) | Mod folder for the font files |

## Pitfalls and version notes

- **Font rebuilds invalidate the pointers (inferred from the decomp, not tested in game).** Saving
  interface settings that need new fonts calls `Program.ScheduleFontRebuild`
  ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L1403-L1416)). The next `PrepareFrame` runs
  `FontManager.RegenerateFonts`, which starts with `ImGui.GetIO().Fonts.Clear()` and rebuilds only the
  game's own fonts, then returns `Exit` so that frame has no UI
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2228-L2233), [current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L55-L98)).[^ksa-gamesettings] Clearing the atlas
  destroys the fonts the mod added, so from the next frame on `OverlayFonts.Numeric`/`Label`/`Body`
  point at freed `ImFont`s and `PushFont` receives a dangling pointer; `_attempted` prevents a reload.
  A safer copy remembers a game font it saw at load (for example `FontManager.ConsoleFont`) and re-adds
  its own fonts when that pointer changes.
- **The manifest `fonts` key is not an alternative.** `mod.toml` `fonts` entries go through
  `Mod.LoadFonts` to `FontManager.RegisterFont`, which only adds the name to the interface-font combo
  (`FontManager.FontFiles`); `RegenerateFonts` loads the `Content/*.ttf` list captured in
  `FontManager.Initialize` ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L287-L312),
  [current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L27-L39)). A mod that wants its own typeface on a draw list adds it to
  the atlas itself, as this one does.
- **Working-directory fallback.** The last `Resolve` candidate is relative to the process working
  directory, which under StarMap is the game folder, not the mod folder.
- **Atlas growth.** Three fonts are added once; do not call `AddFontFromFileTTF` per frame.

## See also

- [Broadcast overlay on the ImGui foreground draw list](foreground-drawlist-broadcast-overlay.md)
- [Mission clock and liftoff detection](mission-clock-and-liftoff-detection.md)
- [Add fonts lazily on the first UI frame with the game's own ImFontConfig fields](../../../howto/ui/draw-lists/load-a-custom-font-into-imgui/load-a-custom-font-into-imgui-2.md) - how-to article demonstrating this feature
- [Keep mod fonts alive when the game rebuilds its font atlas](../../../howto/ui/draw-lists/load-a-custom-font-into-imgui/load-a-custom-font-into-imgui-4.md) - how-to article demonstrating this feature

[^ksa-mod]: KSA.Mod (decompiled, build 2026.10.10.5554)
[^kpt-csproj]: KSATelemetryOverlay.csproj
[^ksa-fontmanager]: KSA.FontManager (decompiled, build 2026.10.10.5554)
[^imgui-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^ksa-imguibackendvulkanimpl]: KSA.ImGuiBackendVulkanImpl (decompiled, build 2026.10.10.5554)
[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
