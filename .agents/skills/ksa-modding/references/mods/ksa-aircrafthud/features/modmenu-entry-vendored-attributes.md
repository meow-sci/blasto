---
type: Mod Feature
title: ModMenu entry with a vendored attributes DLL
description: Lists the mod in the community ModMenu through a static method tagged [ModMenuEntry("Aircraft HUD")], compiling against a ModMenu.Attributes.dll committed to the repo and shipping that DLL next to the mod so StarMap's per-mod load context resolves it
mod: ksa-aircrafthud
category: ui
techniques: [imgui-menu, inter-mod-api, assembly-load-context]
hooks: [modmenu ModMenuEntry Aircraft HUD]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui]
tags: [modmenu, menu, imgui, attributes, dependency, assembly-load-context, starmap]
---

ModMenu (by MrJeranimo, not in this corpus) gives every mod a submenu under one shared menu. A mod
opts in by putting `[ModMenuEntry("<label>")]` on a static method that draws ImGui menu items.
AircraftHUD uses it as its only entry point to the settings window: "Enable HUD"/"Disable HUD" and
"Settings".[^ah-main] [^ah-readme]

## How it works

1. **Attribute DLL in the repo.** [ModMenu.Attributes.dll](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/ModMenu.Attributes.dll) is committed
   at the repo root. Its metadata identifies it as `ModMenu.Attributes` 1.0.2 by MrJeranimo,
   targeting .NET Standard 2.0, repository `https://github.com/MrJeranimo/ModMenu`, and it defines
   `ModMenu.ModMenuEntryAttribute` with `MenuName` and `IsModMenuActivePropertyName` properties (read
   from the DLL's metadata strings).[^ah-modmenudll]
2. **Compile-time reference.** [AircraftHUD.props](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.props#L24-L26) adds
   `<Reference Include="ModMenu.Attributes"><HintPath>ModMenu.Attributes.dll</HintPath></Reference>`
   with no `Private="False"`, so MSBuild copies it to the output, and the csproj also marks the file
   `CopyToOutputDirectory=Always` ([AircraftHUD.csproj](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.csproj#L27-L29)).[^ah-props]
   [^ah-csproj] In the corpus, gatOS and purrTTY take the attribute from the `ModMenu.Attributes` NuGet package
   (1.0.1), while OhMyStars vendors the DLL the same way as AircraftHUD (its NuGet reference is
   commented out).
3. **Entry.** A public static method on the `[StarMapMod]` class
   ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L153-L165)) draws two `ImGui.MenuItem`s that flip
   static flags read by the HUD and the settings window.
4. **Runtime resolution.** `mod.toml` declares no StarMap dependency on ModMenu, so when the CLR
   needs `ModMenu.Attributes` for AircraftHUD, StarMap's `ModAssemblyLoadContext.Load` checks the
   default context, the game context, declared dependencies (none), and finally the mod's own
   `AssemblyDependencyResolver`, which finds the copy shipped next to `AircraftHUD.dll`
   ([StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L72)).[^starmap-alc]
   If ModMenu had already loaded the attribute assembly into the default context, that copy would be
   returned first.

How ModMenu discovers and calls the method (by attribute type, by attribute name, from which hook)
is not verifiable here because ModMenu's source is not in the corpus. The KSAStructures bundle notes
that ModMenu matches the attribute by name, which would also explain why a separately loaded copy
of the attribute DLL works (unverified).

## Lifecycle and timing

- **`frame:draw-ui`.** The method runs whenever ModMenu draws its menu (ModMenu's hook is not in the
  corpus, so its exact timing is unverified). The game's own menu bar is a separate `Menu Bar` window
  that `Program.DrawMenuBar` fills between `BeginMenuBar` and `EndMenuBar`, called from
  `OnDrawUiThreadSafe`, which runs after `OnDrawUiFrame` and is skipped while the UI is hidden
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3455-L3487)).[^ksa-program]
  If ModMenu draws there, the entry is unreachable with the UI hidden, and a click on "Settings"
  opens the window on the next frame, because `[StarMapBeforeGui]` (a prefix on `OnDrawUiFrame`) has
  already run in the current one (see
  [INI settings file and settings window](ini-settings-and-settings-window.md)). The method only
  toggles two booleans.
- If ModMenu is not installed, nothing calls the method and the attribute is inert metadata. The
  mod then has no way to open its settings window (the HUD is on by default, from `hud.ini`).

## Key code

| Path | Role |
|---|---|
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L153-L165) | `[ModMenuEntry("Aircraft HUD")] CreateModMenu()` |
| [AircraftHUD.props](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.props#L24-L26) | HintPath reference to the vendored DLL |
| [AircraftHUD.csproj](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.csproj#L27-L29) | Copy the DLL to the output |
| [ModMenu.Attributes.dll](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/ModMenu.Attributes.dll) | Vendored attribute assembly |

## Snippets

```csharp
// AircraftHUDMain.cs L153-L165
        [ModMenuEntry("Aircraft HUD")]
        public static void CreateModMenu()
        {
            if (ImGui.MenuItem(HUD.enabled ? "Disable HUD" : "Enable HUD"))
            {
                HUD.enabled = !HUD.enabled;
            }

            if (ImGui.MenuItem("Settings"))
            {
                HUD.settingsPageOn = !HUD.settingsPageOn;
            }
        }
```

```xml
<!-- AircraftHUD.props L24-L26 -->
    <Reference Include="ModMenu.Attributes">
      <HintPath>ModMenu.Attributes.dll</HintPath>
    </Reference>
```

## Game APIs touched

`ImGui.MenuItem(string)` only. No KSA types.

## Pitfalls and version notes

- **No fallback menu.** Without ModMenu the settings are reachable only by editing `hud.ini`. The
  gatOS bundle shows a pattern that draws the same items into the game menu bar when ModMenu is
  absent ([gatOS: ModMenu entry with a fallback menu](../../gatos/features/modmenu-entry-with-fallback-menu.md)).
- **Version drift.** A committed binary does not update; the attribute's constructor signature is
  part of the contract with whatever ModMenu version the player has. The NuGet package makes the
  version explicit.
- **No dependency in `mod.toml`.** StarMap cannot warn that ModMenu is missing or order AircraftHUD
  after it ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L78-L106)).[^starmap-runtimemod]
- The optional second attribute argument (`isModMenuActivePropertyName`) is not used.

## See also

- [INI settings file and settings window](ini-settings-and-settings-window.md)
- [Props-based build straight into the mods folder](props-based-build.md)
- [KSAStructures: Settings menu entry, settings window and optional ModMenu listing](../../ksastructures/features/settings-menu.md)
- [Copy a vendored DLL beside the mod and let AssemblyDependencyResolver find it](../../../howto/getting-started/dependencies/ship-third-party-dlls-with-a-mod/ship-third-party-dlls-with-a-mod-1.md) - how-to article demonstrating this feature
- [Vendor ModMenu.Attributes.dll in your repository and let StarMap's per-mod resolver find it](../../../howto/ui/menus/list-a-mod-in-modmenu/list-a-mod-in-modmenu-2.md) - how-to article demonstrating this feature

[^ah-main]: KSA_AircraftHUD/AircraftHUDMain.cs
[^ah-modmenudll]: KSA_AircraftHUD/ModMenu.Attributes.dll
[^ah-props]: KSA_AircraftHUD/AircraftHUD.props
[^ah-csproj]: KSA_AircraftHUD/AircraftHUD.csproj
[^ah-readme]: KSA_AircraftHUD/README.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^starmap-alc]: StarMap ModAssemblyLoadContext.cs
[^starmap-runtimemod]: StarMap RuntimeMod.cs
