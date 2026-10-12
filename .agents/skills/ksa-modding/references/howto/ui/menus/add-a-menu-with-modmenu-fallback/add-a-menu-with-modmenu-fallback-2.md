---
type: How-To
title: Name an active-flag property in ModMenuEntry and skip your own menu when ModMenu sets it
description: Pass the name of a static bool property as the second ModMenuEntry argument, check it in a postfix on Program.DrawProgramMenusHook, and keep an auto-hidden menu bar visible with the IGameViewportLifecycle cast
category: ui
group: menus
topic: add-a-menu-with-modmenu-fallback
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/kitten-engineer-redux/features/modmenu-entry-with-standalone-fallback]
techniques: [inter-mod-api, harmony-postfix, imgui-menu]
hooks: [modmenu ModMenuEntry, harmony-postfix KSA.Program.DrawProgramMenusHook, starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.Program, KSA.IGameViewportLifecycle, KSA.IGameViewport, KSA.GameViewport]
tags: [modmenu, isModMenuActivePropertyName, fallback, drawprogrammenushook, setmenubarinuse, harmony]
---
Kitten Engineer Redux (KER) shows two checkable items, "Flight HUD" and "Editor Panel", in a "Kitten Engineer Redux" menu. With ModMenu the menu lives inside ModMenu; without it, KER adds its own top-level menu through a postfix on `Program.DrawProgramMenusHook`. Instead of asking the engine whether ModMenu is loaded (approach 1), KER passes the **name of a static `bool` property** as the attribute's second argument and relies on ModMenu setting that property to true when it adopts the entry. Prerequisites: StarMap, Harmony and the `ModMenu.Attributes` 1.0.2 assembly (or a source copy that has the two-argument constructor; see [List a mod in ModMenu](../list-a-mod-in-modmenu/overview.md)). Demonstrated by [ModMenu entry with a standalone fallback menu](../../../../mods/kitten-engineer-redux/features/modmenu-entry-with-standalone-fallback.md).

## Background: how the game does it

The game side is the same as in approach 1: `DrawProgramMenusHook()` is an empty public method called inside the "Menu Bar" window's `BeginMenuBar` (right before the version string):

```csharp
// current/decomp/KSA/Program.cs L4005-L4020
			DrawProgramMenusHook();
			if (viewport.IsMain())
			{
				ImGui.SetCursorPosY(cursorPosY);
				ImGui.SetCursorPosX((float)windowWidth - ImGui.CalcTextSize(VersionInfo.Current.VersionString).X - 10f);
				ImGui.TextColored(in Color.Gray.AsFloat4, VersionInfo.Current.VersionString);
			}
			ImGui.EndMenuBar();
		}
		ImGui.End();
		ConsoleStyle.PopWidgetStyle();
	}

	public void DrawProgramMenusHook()
	{
	}
```

With auto-hide on, `DrawMenuBar` clears the viewport's "in use" flag every frame before drawing and, when the mouse is below the bar and the flag is not set, hides the bar and returns before the hook, so a dropdown would vanish as the pointer moves down into it ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3455-L3492)).[^ksa-ksa-program] The stock menus set the flag while open. `IGameViewport.MenuBarInUse` is get-only ([current/decomp/KSA/IGameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IGameViewport.cs#L11)),[^ksa-ksa-igameviewport] and `Program.MainViewport` is typed `IGameViewport`; the setter is `IGameViewportLifecycle.SetMenuBarInUse(bool)`, implemented explicitly on `GameViewport` ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L183-L191)).[^ksa-ksa-gameviewport]

The ModMenu side is **unverified**. The attribute's metadata (the 1.0.2 assembly, read by the KER feature page) says the second parameter lets "ModMenu signal when your menu is active" through a boolean property; KER never sets the property itself. When and how ModMenu writes it is in ModMenu's source, which is not in the corpus.

## Lifecycle and timing

- **Apply the postfix at `startup:all-mods-loaded`.** KER's `OnFullyLoaded` runs `PatchAll` for the whole assembly, which includes the postfix class.
- **Draw at `frame:draw-ui`.** `DrawMenuBar` runs from `OnDrawUiThreadSafe` (main viewport hovered) and from `GameViewport.DrawImGui` (hovered secondary viewport), so the postfix is called per bar drawn. Neither runs while the HUD is hidden.
- **The flag's value** is written by ModMenu at some point before or while it draws its menu (unverified). If it is set only when ModMenu actually draws its menu, there can be frames, before ModMenu first draws, in which both menus are possible. KER relies on the common case.
- See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Define the active flag and the ModMenu entry.** A public static property, and the attribute pointing at it with `nameof`:

```csharp
// ModMenuIntegration.cs L1-L14
using ModMenu;

namespace KittenEngineerRedux.UI;

public static class ModMenuIntegration
{
    public static bool IsHandledByModMenu { get; set; }

    [ModMenuEntry("Kitten Engineer Redux", nameof(IsHandledByModMenu))]
    public static void DrawMenu()
    {
        MenuContent.DrawToggles();
    }
}
```

2. **Write the shared content.** Two checkable items bound to the persisted visibility flags (the `selected` overload of `ImGui.MenuItem`):

```csharp
// MenuContent.cs L7-L19
internal static class MenuContent
{
    public static void DrawToggles()
    {
        bool showHud = FlightHud.Visible;
        if (ImGui.MenuItem("Flight HUD"u8, default, showHud))
            FlightHud.Visible = !showHud;

        bool showEditor = EditorPanel.Visible;
        if (ImGui.MenuItem("Editor Panel"u8, default, showEditor))
            EditorPanel.Visible = !showEditor;
    }
}
```

3. **Write the fallback postfix.** It returns if ModMenu has taken the entry; otherwise it draws its own menu, marks the bar in use and ends the menu. Note the `IGameViewportLifecycle` cast (see Background):

```csharp
// StandaloneMenu.cs L7-L23
[HarmonyPatch(typeof(Program), nameof(Program.DrawProgramMenusHook))]
internal static class Patch_StandaloneMenu
{
    private static void Postfix()
    {
        if (ModMenuIntegration.IsHandledByModMenu)
            return;

        if (!ImGui.BeginMenu("Kitten Engineer Redux"u8))
            return;

        ((IGameViewportLifecycle)Program.MainViewport).SetMenuBarInUse(true);
        MenuContent.DrawToggles();

        ImGui.EndMenu();
    }
}
```

4. **Patch from `OnFullyLoaded`, unpatch on unload.** KER uses one Harmony instance for everything:

```csharp
// Mod.cs L19-L36
    private static Harmony? _harmony;

    [StarMapAllModsLoaded]
    public void OnFullyLoaded()
    {
        _harmony = new Harmony("com.matthew.kittenengineerredux");
        _harmony.PatchAll(typeof(Mod).Assembly);
        DefaultCategory.Log.Info("[KER] Loaded and patched.");
    }

    [StarMapUnload]
    public void Unload()
    {
        _harmony?.UnpatchAll(_harmony.Id);
        _harmony = null;
        SequenceAnalyzer.ResetPools();
        DefaultCategory.Log.Info("[KER] Unloaded.");
    }
```

5. **Ship the attribute assembly.** The post-build step copies `ModMenu.Attributes.dll` next to the mod DLL when it exists, so the attribute type resolves when ModMenu reflects over KER's methods:

```xml
<!-- KittenEngineerRedux.csproj L68-L83 -->
    <Target Name="PostBuild" AfterTargets="PostBuildEvent">
        <PropertyGroup>
            <SourceDll>$(TargetDir)KittenEngineerRedux.dll</SourceDll>
            <SourceAttributesDll>$(TargetDir)ModMenu.Attributes.dll</SourceAttributesDll>
            <DestinationDir>C:\Users\Matthew\Documents\My Games\Kitten Space Agency\mods\KittenEngineerRedux\</DestinationDir>
        </PropertyGroup>
        
        <Message Importance="High" Text="Executing Post-Build Deployment..." />
        
        <MakeDir Directories="$(DestinationDir)" Condition="!Exists('$(DestinationDir)')" />
        
        <Copy SourceFiles="$(SourceDll)" DestinationFolder="$(DestinationDir)" SkipUnchangedFiles="false" />
        <Copy SourceFiles="$(SourceAttributesDll)" DestinationFolder="$(DestinationDir)" SkipUnchangedFiles="false" Condition="Exists('$(SourceAttributesDll)')" />
        
        <Message Importance="High" Text="Mod successfully deployed to: $(DestinationDir)" />
    </Target>
```

6. **Test.** With ModMenu: one menu, inside ModMenu. Without it: a "Kitten Engineer Redux" menu appears in the game's bar after the stock menus and the bar stays visible while the pointer is inside the dropdown.

## Complete example

| File | Role |
|---|---|
| [ModMenuIntegration.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/ModMenuIntegration.cs) | Entry method and the active flag |
| [StandaloneMenu.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/StandaloneMenu.cs) | Fallback postfix |
| [MenuContent.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/MenuContent.cs) | Shared toggles |
| [Mod.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Mod.cs#L19-L36) | `PatchAll` and unpatch |
| [KittenEngineerRedux.csproj](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/KittenEngineerRedux.csproj#L68-L83) | Post-build copy of the attribute DLL |

## Variations and gotchas

- **Two menus on an older ModMenu.** If ModMenu is installed but never sets the property (for example a version that ignores the second argument), both menus appear. KER's own feature page records this; approach 1's `ModLibrary.Find("ModMenu")` avoids the dependency on ModMenu cooperating.
- **A stale file ships in the repository.** `Patch_StandaloneMenu` (no extension) is an older copy that assigns `Program.MainViewport.MenuBarInUse = true`. In build 2026.10.10.5554 `MenuBarInUse` is get-only on `IGameViewport`, so that code would not compile; it is excluded from the build only because the SDK compiles `*.cs`. Use the `IGameViewportLifecycle` cast shown above.
- **Per-bar calls.** `DrawProgramMenusHook` runs for every bar drawn, so the menu also appears in a hovered secondary viewport's bar, and `SetMenuBarInUse(true)` is applied to the main viewport either way.
- **One-argument attribute copies.** A source copy of the attribute must have the two-argument constructor for this approach; the 1.0.1 package has only the one-argument constructor (per the KER page).
- **`PatchAll` applies every patch together.** A single failing patch aborts the whole call; isolate optional patches individually as approach 1's purrTTY does.

## See also

- [Overview](overview.md)
- [Approach 1: decide once with ModLibrary.Find](add-a-menu-with-modmenu-fallback-1.md)
- [Approach 3: one settings tree rendered as a menu or a window](add-a-menu-with-modmenu-fallback-3.md)
- [List a mod in ModMenu with the ModMenuEntry attribute](../list-a-mod-in-modmenu/overview.md)
- Mod feature: [ModMenu entry with a standalone fallback menu](../../../../mods/kitten-engineer-redux/features/modmenu-entry-with-standalone-fallback.md)

[^ksa-ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-ksa-igameviewport]: KSA.IGameViewport (decompiled, build 2026.10.10.5554)
[^ksa-ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
