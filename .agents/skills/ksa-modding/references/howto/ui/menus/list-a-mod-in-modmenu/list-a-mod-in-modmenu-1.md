---
type: How-To
title: Reference ModMenu.Attributes, tag a static method and ship the DLL beside your mod
description: Add the ModMenu.Attributes assembly to your project, put ModMenuEntry on a static method that draws menu items, toggle your window from it and copy the DLL into the mod folder
category: ui
group: menus
topic: list-a-mod-in-modmenu
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/compendium/features/modmenu-window-toggle
  - mods/ohmystars/features/modmenu-entry-and-console-style-windows
  - mods/mpfx/features/effect-settings-window
techniques: [inter-mod-api, imgui-menu]
hooks: [modmenu ModMenuEntry, starmap StarMapAfterGui, starmap StarMapBeforeGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImString]
tags: [modmenu, modmenuentry, attribute, nuget, hintpath, menu]
---
The shortest way to appear in ModMenu's shared menu is to reference the `ModMenu.Attributes` assembly, tag one static method with `[ModMenuEntry("Your Mod")]`, and ship the assembly next to your mod DLL. Three reference mods do exactly that: Compendium (one menu item that toggles its window), Oh My Stars (two items for two windows) and MPFX (a text line and an "Open" button). Prerequisite: a working StarMap mod that already draws its windows (see [Add a menu to the game's top menu bar](../add-items-to-the-game-menu-bar/overview.md) for the other way in). Demonstrated by [ModMenu entry that toggles the window](../../../../mods/compendium/features/modmenu-window-toggle.md), [ModMenu entry and game-styled windows](../../../../mods/ohmystars/features/modmenu-entry-and-console-style-windows.md) and [Effect settings window](../../../../mods/mpfx/features/effect-settings-window.md).

## Background: how the game does it

The game has no concept of a ModMenu entry. ModMenu is itself a StarMap mod; the contract below is what the three mods rely on, taken from their comments and from the attribute assembly's metadata, and is **unverified** against ModMenu's source (not in this corpus):

- The attribute is `ModMenu.ModMenuEntryAttribute`, valid on methods, with a `MenuName` (and, in `ModMenu.Attributes` 1.0.2, an optional `IsModMenuActivePropertyName`). Oh My Stars' feature page read the assembly metadata of its 1.0.2 copy; Compendium's copy is 1.0.1 by the same author.
- ModMenu finds tagged methods itself and calls them while it draws its menu. What you write is menu content: `ImGui.MenuItem`, `ImGui.BeginMenu`. Both are public static members of `Brutal.ImGuiApi.ImGui`: [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7429-L7465).[^ksa-brutal-imguiapi-imgui]
- With ModMenu absent nothing calls the method.

## Lifecycle and timing

- **Your entry method runs at `frame:draw-ui`, when ModMenu draws its menu** (call site unverified). The game's own menu bar is a separate "Menu Bar" window filled by `Program.DrawMenuBar` from `OnDrawUiThreadSafe`, so a toggle you flip in the menu is seen by your window code on the **next** frame when your window draws from `[StarMapAfterGui]` or `[StarMapBeforeGui]`. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).
- **Do nothing heavy and draw no windows inside the entry.** Compendium and Oh My Stars only flip a flag; their windows are drawn by a separate StarMap hook every frame.
- **Hidden HUD.** If ModMenu draws inside the game's menu bar, the entry is unreachable while the HUD is hidden (the bar is skipped when `DrawUI` is false); that is inferred, not checked against ModMenu.

## Steps

1. **Add the attribute assembly to the project.** MPFX restores `ModMenu.Attributes` 1.0.2 from NuGet and copies the DLL to its output folder so it travels with the mod:

```xml
<!-- MPFX/MPFX.csproj L53-L61 -->
	<ItemGroup>
		<PackageReference Include="Lib.Harmony" Version="2.4.2" />
		<PackageReference Include="ModMenu.Attributes" Version="1.0.2" GeneratePathProperty="true" />
	</ItemGroup>

	<ItemGroup>
		<None Include="$(PkgModMenu_Attributes)\lib\netstandard2.0\ModMenu.Attributes.dll"
			Link="ModMenu.Attributes.dll" CopyToOutputDirectory="PreserveNewest" />
	</ItemGroup>
```

   Compendium and Oh My Stars could not use the package: both comment the `PackageReference` out ("nuget.org doesn't seem to actually have this as a package") and commit the DLL instead, referenced by `HintPath`. That is [approach 2](list-a-mod-in-modmenu-2.md) in detail:

```xml
<!-- src/Compendium.csproj L70-L72 -->
    <!-- nuget.org doesn't seem to actually have this as a package on nuget right now -->
    <!-- (terminal command to add via nuget:) dotnet add package ModMenu.Attributes -->
    <!-- <PackageReference Include="ModMenu.Attributes" Version="1.0.1" /> -->
```

```xml
<!-- src/Compendium.csproj L98-L100 -->
    <Reference Include="ModMenu.Attributes">
      <HintPath>ModMenu.Attributes.dll</HintPath>
    </Reference>
```

   Pick one: try the package first, fall back to the DLL.

2. **Tag a static method.** The class carrying it needs `using ModMenu;`. Compendium's whole integration is one method that flips the static field its window reads:

```csharp
// src/CompendiumMain.cs L45-L54
        [ModMenuEntry("Compendium Window")]

        // Makes the menu entry to open/close the compendium window using ModMenu.Attributes.dll
        public static void DrawSubMenuEntry()
        {
            if (ImGui.MenuItem("Compendium Window"))
            { CompendiumWindow = !CompendiumWindow; 
            Console.WriteLine("Toggled Compendium ModMenu Window variable");
            }
        }
```

   Note `ImGui.MenuItem(string)` with only a label: a plain click item. `ImGui.MenuItem(label, default, ref bool, true)` (the `pSelected` overload) draws a ticked checkbox item instead.

3. **Group several items under one menu name.** The menu name is the label ModMenu shows for your mod; put every item for that mod inside the single tagged method. Oh My Stars lists a window per item, and draws both windows from `[StarMapBeforeGui]`, the hook that fires before the stock UI of the same frame:

```csharp
// src/OhMyStarsEntryPoint.cs L34-L49
    [ModMenuEntry("Oh My Stars")]
    public static void DrawMenu() {
        if(ImGui.MenuItem("Oh My Stars Window")) {
            OhMyStarsWindow.ToggleWindow();
        }
        if(ImGui.MenuItem("Star Editor")) {
            StarsEditWindow.ToggleWindow();
        }
    }

    [StarMapBeforeGui]
    public static void OnBeforeGui(double dt) {
        OhMyStarsWindow.Draw();
        StarsEditWindow.Draw();
        OhMyStarsWindow.PersistWindowOpenStates();
    }
```

4. **A button is also fine.** MPFX's entry is a text line and a button that re-opens a window the player closed with its title-bar X:

```csharp
// MPFX/MPFX.cs L42-L47
        [ModMenuEntry("MPFX")]
        public static void DrawMenu()
        {
            ImGui.Text("MPFX Mod Menu");
            if (ImGui.Button("Open")) ShowWindow = true;
        }
```

5. **Ship the DLL.** The copy step belongs in the build that fills your mod folder. Compendium copies it explicitly with the rest of the mod:

```xml
<!-- src/Compendium.csproj L35-L37 -->
		<Copy SourceFiles="$(BuildDir)$(ModNamespace).deps.json" DestinationFolder="$(SendBuildToTestDir)" />
		<Copy SourceFiles="$(BuildDir)$(ModNamespace).dll" DestinationFolder="$(SendBuildToTestDir)" />
    <Copy SourceFiles="ModMenu.Attributes.dll" DestinationFolder="$(SendBuildToTestDir)" />
```

   At run time StarMap's per-mod load context returns an already loaded assembly of the same name from the default or game context first, then tries declared mod dependencies, and only then resolves files from your mod folder ([StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L73)).[^starmap-starmap-core-modrepository-modassemblyloadcontext] Whether your copy and ModMenu's copy end up being the same type is therefore decided by how ModMenu loads it; the mods get by because ModMenu matches by name, per their comments (see [approach 3](list-a-mod-in-modmenu-3.md)).

6. **Run it.** Start KSA with ModMenu installed and look for your menu name under ModMenu's shared menu on the menu bar. Click an item and confirm the window appears on the next frame. Without ModMenu you will see no entry and no error.

## Complete example

| File | Role |
|---|---|
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L45-L54) | Compendium: the tagged method |
| [src/Compendium.csproj](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium.csproj#L98-L100) | Compendium: `HintPath` reference and DLL copy (L37) |
| [src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L34-L49) | Oh My Stars: entry with two items plus the `StarMapBeforeGui` window hook |
| [src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj#L52-L55) | Oh My Stars: package reference commented out, DLL referenced instead |
| [MPFX/MPFX.csproj](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.csproj#L53-L61) | MPFX: NuGet reference plus a copy item |
| [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L42-L47) | MPFX: entry with an "Open" button |

## Variations and gotchas

- **No fallback.** Once the player closes the window with its X, ModMenu is the only way to reopen it. Without ModMenu the window can only be reopened by editing the mod's settings file (Compendium: `ShowWindow=True` in `settings.ini`). See [the fallback pattern](../add-a-menu-with-modmenu-fallback/overview.md).
- **Package or DLL disagreement.** The mods disagree about whether `ModMenu.Attributes` can be restored from nuget.org (MPFX, gatOS, purrTTY and Kitten Engineer Redux restore it; Compendium and Oh My Stars comment it out). Treat the feed as something to test, not assume.
- **Version drift.** A committed DLL does not update. The one-argument constructor exists in both versions; the second argument (`isModMenuActivePropertyName`) only in 1.0.2.
- **Do not declare a dependency in `mod.toml`.** None of the three mods lists ModMenu as a StarMap dependency, which keeps the mod loadable without it; the cost is that StarMap cannot warn the player about a missing ModMenu.
- **Closing the window.** MPFX's window uses `ImGuiWindowFlags.NoSavedSettings` and `ShowWindow = true` at startup, so it opens on every launch and the entry is only a re-open button.

## See also

- [Overview](overview.md)
- [Approach 2: vendored DLL resolved by StarMap's per-mod resolver](list-a-mod-in-modmenu-2.md)
- [Approach 3: copy the attribute source into the mod](list-a-mod-in-modmenu-3.md)
- [Approach 4: DynamicMethod wrappers](list-a-mod-in-modmenu-4.md)
- [Add a menu that uses ModMenu when present and the game menu bar otherwise](../add-a-menu-with-modmenu-fallback/overview.md)
- [Ship a third-party DLL with a mod](../../../getting-started/dependencies/ship-third-party-dlls-with-a-mod/overview.md)
- Mod features: [Compendium](../../../../mods/compendium/features/modmenu-window-toggle.md), [Oh My Stars](../../../../mods/ohmystars/features/modmenu-entry-and-console-style-windows.md), [MPFX](../../../../mods/mpfx/features/effect-settings-window.md)

[^ksa-brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^starmap-starmap-core-modrepository-modassemblyloadcontext]: StarMap: StarMap.Core/ModRepository/ModAssemblyLoadContext.cs
