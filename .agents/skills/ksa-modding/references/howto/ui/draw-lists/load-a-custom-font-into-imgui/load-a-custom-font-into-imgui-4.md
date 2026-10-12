---
type: How-To
title: Keep mod fonts alive when the game rebuilds its font atlas
description: Why FontManager.RegenerateFonts invalidates every cached ImFontPtr and how to guard against it by looking the font up in FontManager.Fonts each frame and re-adding it when missing, an adapted and untested design
category: ui
group: draw-lists
topic: load-a-custom-font-into-imgui
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/compendium/features/custom-fonts-in-imgui
  - mods/purrtty/features/nerd-font-loading-into-imgui-atlas
  - mods/ksa-aircrafthud/features/custom-hud-font
  - mods/ksa-public-telemetry/features/montserrat-fonts-in-imgui-atlas
techniques: [reflection, imgui-font, harmony-postfix]
hooks: [harmony-postfix KSA.FontManager.RegenerateFonts]
lifecycle: [frame:prepare, frame:draw-ui]
game_types:
  - Brutal.ImGuiApi.ImFontAtlasPtr
  - Brutal.ImGuiApi.ImGui
  - KSA.FontManager
  - KSA.GameSettings
  - KSA.Program
tags: [font, regeneratefonts, atlas-clear, dangling-pointer, guard, untested]
---
Changing the interface font settings in KSA schedules `FontManager.RegenerateFonts`, which clears the whole ImGui atlas and re-adds only the game's fonts. Every `ImFontPtr` a mod cached from `AddFontFromFileTTF` stops belonging to the atlas. All four reference mods that add fonts record this hazard in their feature pages; none implements a fix, and none was tested against it in game. This article explains the mechanism from the decomp and gives a guard that is clearly labelled as adapted and untested.

## Background: how the game does it

The chain from player action to cleared atlas, all verified in the decomp:

1. Saving or applying interface settings that change the font calls `Program.ScheduleFontRebuild()` when `RequireFontRegeneration()` is true ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L1405-L1414)[^ksa-gamesettings]; the apply path at L2362-L2373 does the same).
2. `ScheduleFontRebuild` sets a flag ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5273-L5276)[^ksa-program]).
3. The next `PrepareFrame` calls `FontManager.RegenerateFonts()`, waits for the GPU, clears the flag and returns `OnFrameResult.Exit`, so that frame has no UI at all ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2228-L2233)[^ksa-program]; `OnFrame` returns early on `Exit`, L2243-L2248):

```csharp
// current/decomp/KSA/Program.cs L2228-L2233
if (_rebuildFontNeeded)
{
	FontManager.RegenerateFonts();
	_renderer.Device.WaitIdle();
	_rebuildFontNeeded = false;
	return OnFrameResult.Exit;
```

4. `RegenerateFonts` begins by clearing the atlas and the name dictionary, then re-adds every `Content/**/*.ttf`, the console font, and a small variant of the interface font:

```csharp
// current/decomp/KSA/FontManager.cs L55-L60
public static void RegenerateFonts()
{
	ImGui.GetIO().Fonts.Clear();
	ImFontAtlasPtr fonts = ImGui.GetIO().Fonts;
	Fonts.Clear();
	ImGuiHelper.SetTooltipFont(null);
```

5. Dear ImGui's `ImFontAtlas::Clear` destroys the fonts the atlas owns. That last step is upstream library behaviour and cannot be read from the decomp, which is why the effect on a cached mod pointer is described as inferred.

The game's own UI does not hold stale pointers because it re-resolves each frame. `ConsoleStyle.RefreshFontCache` looks fonts up in `FontManager.Fonts` once per ImGui frame:

```csharp
// current/decomp/KSA/ConsoleStyle.cs L462-L470
{
	int frameCount = ImGui.GetFrameCount();
	if (frameCount != _fontCacheFrame)
	{
		_fontCacheFrame = frameCount;
		_interfaceFontResolved = FontManager.Fonts.TryGetValue(GameSettings.GetFontName(), out _interfaceFont);
		_gaugeFontResolved = FontManager.Fonts.TryGetValue("MilSpec33558", out _gaugeFont);
	}
}
```

That is the pattern to copy: do not keep the pointer, keep the name.

## Lifecycle and timing

| Moment | What happens | What a mod sees |
|---|---|---|
| `frame:prepare` | `RegenerateFonts` clears the atlas and `FontManager.Fonts`, re-adds game fonts | Nothing yet; `[StarMapAfterGui]` does not run on this frame because `PrepareFrame` returned `Exit` |
| Next `frame:draw-ui` | UI runs again | `FontManager.Fonts` holds only game entries; mod entries are gone; cached `ImFontPtr`s are stale |
| Startup | `FontManager.Initialize` also calls `RegenerateFonts` once | Before any mod loads, so a postfix on it is not yet applied |

Because the rebuild happens in `PrepareFrame`, before any UI code of the frame, a check at the start of your draw is always consistent. Adding a font mid-frame is legal with the dynamic atlas (`RendererHasTextures`, [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L656-L658)[^ksa-imguibackendvulkanimpl]). See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

All code below is adapted by the author of this corpus, not taken from any mod, and has not been run in game. It compiles against the named types in build 2026.10.10.5554.

1. **Register your font in `FontManager.Fonts` under a unique key** when you add it. `Fonts` is `public static readonly Dictionary<string, ImFontPtr>` ([current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L11)[^ksa-fontmanager]); purrTTY already inserts its fonts there ([purrTTY.Display/Rendering/PurrTTYFontManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Rendering/PurrTTYFontManager.cs#L70-L77)). Use a mod-prefixed key so it cannot collide with game names.

2. **Resolve through the dictionary every frame, and re-add when the key is missing.** A missing key is exactly "the atlas was rebuilt since I added it", because `RegenerateFonts` calls `Fonts.Clear()` and re-adds only game fonts:

```csharp
// adapted guard, untested - not from any reference mod
using Brutal.ImGuiApi;
using KSA;

internal static class ModFont
{
    private const string Key = "MyMod.Display";   // unique, mod-prefixed
    private static string? s_path;                  // absolute path, set at immediate load
    private const float BaseSize = 32f;

    public static void Configure(string absolutePath) => s_path = absolutePath;

    /// Call at the start of every UI frame that uses the font.
    public static bool TryGet(out ImFontPtr font)
    {
        font = default;
        if (s_path is null || !File.Exists(s_path))
        {
            return false;
        }

        if (!FontManager.Fonts.TryGetValue(Key, out font))
        {
            // First use, or the atlas was cleared by RegenerateFonts: add again.
            font = ImGui.GetIO().Fonts.AddFontFromFileTTF(new ImString(s_path), BaseSize);
            FontManager.Fonts[Key] = font;
        }

        return true;
    }
}
```

3. **Use it with a balanced push.** Take the font fresh each frame and never store it in a field:

```csharp
// adapted usage, untested
bool pushed = ModFont.TryGet(out ImFontPtr font);
if (pushed) ImGui.PushFont(font, 26f);
try { /* draw */ }
finally { if (pushed) ImGui.PopFont(); }
```

4. **Alternative: invalidate from a Harmony postfix.** If you cannot register in `FontManager.Fonts`, patch the public static `FontManager.RegenerateFonts` with a postfix that clears your own dictionary or bumps a generation counter, and re-add lazily on the next draw. Do not re-add inside the postfix if your fonts may be needed before the next frame; it runs in `PrepareFrame`, where adding is also legal but the context state is mid-rebuild:

```csharp
// adapted alternative, untested
[HarmonyPatch(typeof(FontManager), nameof(FontManager.RegenerateFonts))]
internal static class FontRebuildGuard
{
    internal static int Generation;
    private static void Postfix() => Generation++;   // callers compare with their cached generation
}
```

5. **Do not rely on the manifest.** `mod.toml` `fonts` entries only call `FontManager.RegisterFont`, which adds a name to the picker (`FontManager.FontFiles`); they neither load nor protect a file.

## Complete example

There is none: no reference mod implements the guard. The nearest pieces are the four feature pages that record the hazard and the game's own `ConsoleStyle.RefreshFontCache`:

| Piece | Where |
|---|---|
| Fonts added once, cached, never reloaded | [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L701-L713), [src/CdmLoadFonts.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmLoadFonts.cs#L28-L40), [Rendering/OverlayFonts.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayFonts.cs#L21-L62), [purrTTY.Display/Rendering/PurrTTYFontManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Rendering/PurrTTYFontManager.cs#L34-L88) |
| Per-frame re-resolution through `FontManager.Fonts` | `ConsoleStyle.RefreshFontCache` (game) |

## Variations and gotchas

- **Untested.** Nothing here has been run; verify by changing the interface font in settings and watching the log and the screen. A crash or garbled text on the first frame after the rebuild means the guard missed a cached copy.
- **Other caches.** Anything derived from a font (cell metrics, glyph batch caches, measured widths) must be invalidated with it. purrTTY's `GlyphBatchCache` and cell metrics are reset only when family, size or the loaded-font count changes, which would not fire here.
- **Sentinel semantics.** The membership check assumes only `RegenerateFonts` empties `FontManager.Fonts` (it is the only `Fonts.Clear()` in the decompiled `KSA` sources) and that no other mod removes your key.
- **Do not call `RegenerateFonts` yourself** to "refresh"; it ends the frame and rebuilds everything.
- **Version.** Depends on `FontManager.Fonts` and `RegenerateFonts` being public and behaving as in 2026.10.10.5554; re-read `FontManager.cs` after each game update.

## See also

- [Overview of custom fonts](overview.md)
- [Approach 1: add at immediate load](load-a-custom-font-into-imgui-1.md)
- [Approach 2: add lazily](load-a-custom-font-into-imgui-2.md)
- [Approach 3: renamed extension](load-a-custom-font-into-imgui-3.md)
- Mod feature: [Custom TTF font in the ImGui atlas](../../../../mods/ksa-aircrafthud/features/custom-hud-font.md)
- Mod feature: [Custom fonts in the ImGui atlas](../../../../mods/compendium/features/custom-fonts-in-imgui.md)
- Mod feature: [Montserrat fonts in the ImGui atlas](../../../../mods/ksa-public-telemetry/features/montserrat-fonts-in-imgui-atlas.md)
- Mod feature: [Bundled Nerd Fonts loaded into the ImGui atlas](../../../../mods/purrtty/features/nerd-font-loading-into-imgui-atlas.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-fontmanager]: KSA.FontManager (decompiled, build 2026.10.10.5554)
[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^ksa-imguibackendvulkanimpl]: KSA.ImGuiBackendVulkanImpl (decompiled, build 2026.10.10.5554)
