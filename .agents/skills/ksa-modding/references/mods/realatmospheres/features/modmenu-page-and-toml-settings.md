---
type: Mod Feature
title: ModMenu page and TOML settings
description: A ModMenu page found through a vendored attribute, drawn with ImGui calls emitted as DynamicMethods because ImString is a ref struct, and persisted to a hand-parsed settings.toml
mod: realatmospheres
category: ui
techniques: [imgui-menu, reflection, toml-config, dynamic-method]
hooks:
  - modmenu ModMenuEntry Real Atmospheres
  - reflection Brutal.ImGuiApi.ImGui.MenuItem
  - reflection Brutal.ImGuiApi.ImGui.SetItemTooltip
  - reflection KSA.Constants.DocumentsFolderPath
lifecycle: [frame:draw-ui, render:atmosphere]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImString, KSA.Constants]
tags: [ui, imgui, modmenu, settings, toml, dynamicmethod, ref-struct, persistence]
---

The mod has one player setting, "Procedural haze bands" for Pluto, Triton and Titan. It is a menu item
on a Real Atmospheres page in the game's Mods menu when ModMenu (a separate mod by MrJeranimo) is
installed, and a line in `RealAtmospheres/settings.toml` otherwise. Because the mod is built without
game references (see [Reflection-only mod build](reflection-only-mod-build.md)), even the ImGui calls
are made by name.[^ra-menu]

## How it works

### The page

- **Vendored attribute.** `ModMenuEntryAttribute(string menuName, string? isModMenuActivePropertyName)`
  is declared inside the mod; ModMenu finds pages by the attribute's name, so no reference to ModMenu
  is needed ([Menu.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Menu.cs#L37-L49)). The page is `[ModMenuEntry("Real Atmospheres")] public
  static void Draw()` ([Menu.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Menu.cs#L16-L34)). That ModMenu matches by attribute name is the
  mod's own statement; ModMenu is not among the analyzed repositories (unverified here).
- **ImGui through DynamicMethod.** `Brutal.ImGuiApi.ImGui.MenuItem(ImString label, ImString shortcut,
  bool selected, bool enabled)` and `SetItemTooltip(ImString)` take `ImString`, a `ref struct`
  ([current/decomp/Brutal.ImGuiApi/ImString.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImString.cs#L14)),
  which cannot be boxed for `MethodInfo.Invoke`. The mod emits small `DynamicMethod`s that convert the
  `string` with `ImString`'s implicit operator on the evaluation stack and call straight through, then
  wraps them in typed delegates ([Menu.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Menu.cs#L93-L140)).[^brutal-imguiapi-imgui]
- **Fail-soft.** Resolution happens on first draw; any failure logs once and leaves the page empty, and
  an exception while drawing disables the page for the session ([Menu.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Menu.cs#L65-L85),
  [Menu.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Menu.cs#L19-L33)). Tooltip text has `%` doubled because it is a printf format.

### The file

- **Location.** `Path.Combine(KSA.Constants.DocumentsFolderPath, "RealAtmospheres", "settings.toml")`,
  read by reflection; `DocumentsFolderPath` is `Documents/My Games/Kitten Space Agency`
  ([Settings.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Settings.cs#L26-L44),
  [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)).[^ksa-constants]
  Reading it through the property getter matters: StarMap prefixes that getter to return an instance
  folder when `-InstancePath` or `STARMAP_INSTANCE_PATH` is set (Borea instances), and the settings
  file follows ([StarMap.Core/Patches/DocumentsPathPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/DocumentsPathPatches.cs#L12-L31)).
- **Format.** A minimal line parser for `key = value` with `#` comments; unknown keys and bad values
  are ignored and the defaults stand ([Settings.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Settings.cs#L68-L89)). Writing produces a
  commented template with the one key ([Settings.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Settings.cs#L112-L118)).
- **Read once, write on click.** `Load` runs once per session; `SetHazeBands` writes immediately
  because a menu click is a discrete event ([Settings.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Settings.cs#L46-L66),
  [Settings.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Settings.cs#L93-L110)).

### How the setting reaches the GPU

No shader is recompiled. The atmosphere prefix sends the haze clock negated when the bands are off,
the shaders test the sign, and the renderer's kept LUTs are invalidated on change (see
[Atmosphere uniform side channel](atmosphere-uniform-side-channel.md)).

## Lifecycle and timing

- **`frame:draw-ui`.** `Menu.Draw` is called by ModMenu while its menu is open, during the game's UI
  pass (ModMenu-side timing unverified). It only flips a static and writes the file.
- **`render:atmosphere`.** The new value is read by the `FillPlanetAtmosphereData` prefix in the same
  or next frame's atmosphere pass, which also loads the file on first use if no menu was drawn
  ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L339-L358)). Both run on the main thread, so a plain static is enough.

## Key code

| Path | Role |
|---|---|
| [Menu.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Menu.cs#L12-L35) | The ModMenu page |
| [Menu.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Menu.cs#L37-L49) | Vendored `ModMenuEntryAttribute` |
| [Menu.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Menu.cs#L56-L141) | `ImGuiCalls`: resolution and `DynamicMethod` emission |
| [Settings.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Settings.cs#L10-L119) | File location, parse, write |

## Snippets

```csharp
// Menu.cs L16-L34
    [ModMenuEntry("Real Atmospheres")]
    public static void Draw()
    {
        if (_broken || !ImGuiCalls.Ready) return;
        try
        {
            Settings.Load();
            if (ImGuiCalls.MenuItem("Procedural haze bands##realatmospheres", Settings.HazeBands))
                Settings.SetHazeBands(!Settings.HazeBands);
            ImGuiCalls.SetItemTooltip(
                "The bands in the haze of Pluto, Triton and Titan, which drift round each body and change with time.\n"
                + "Unticked, their haze is smooth, and the sky there is not worked out pixel by pixel.");
        }
        catch (Exception ex)
        {
            _broken = true;
            ShadowBuilder.Log("WARN: Real Atmospheres' page in the Mods menu stopped: " + ex.GetBaseException().Message);
        }
    }
```

```csharp
// Menu.cs L109-L127
        MethodInfo menuItem = imgui.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "MenuItem" && m.GetParameters() is { Length: 4 } p && p[0].ParameterType == imString
                                 && p[1].ParameterType == imString && p[2].ParameterType == typeof(bool)
                                 && p[3].ParameterType == typeof(bool) && m.ReturnType == typeof(bool))
            ?? throw new InvalidOperationException("ImGui.MenuItem(label, shortcut, selected, enabled) not found");
        var itemIl = new DynamicMethod("RealAtmospheres_ImGui_MenuItem", typeof(bool), new[] { typeof(string), typeof(bool) },
                                       typeof(ImGuiCalls).Module, skipVisibility: true);
        ILGenerator il = itemIl.GetILGenerator();
        LocalBuilder none = il.DeclareLocal(imString);         // the shortcut: an empty ImString, as its default is
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, fromString);
        il.Emit(OpCodes.Ldloca_S, none);
        il.Emit(OpCodes.Initobj, imString);
        il.Emit(OpCodes.Ldloc, none);
        il.Emit(OpCodes.Ldarg_1);                              // selected: shown ticked
        il.Emit(OpCodes.Ldc_I4_1);                             // enabled
        il.Emit(OpCodes.Call, menuItem);
        il.Emit(OpCodes.Ret);
        _menuItem = (MenuItemCall)itemIl.CreateDelegate(typeof(MenuItemCall));
```

```csharp
// Settings.cs L94-L110
    internal static void SetHazeBands(bool bands)
    {
        Load();
        if (bands == HazeBands) return;
        HazeBands = bands;
        string? path = FilePath;
        if (path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Text());
        }
        catch (Exception ex)
        {
            ShadowBuilder.Log("WARN: the settings file could not be written: " + ex.Message);
        }
    }
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ImGui.MenuItem(ImString, ImString, bool, bool)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7442) | Checkable menu item |
| `ImGui.SetItemTooltip(ImString)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7490) | Tooltip |
| `ImString` (`ref struct`, implicit from `string`) | [current/decomp/Brutal.ImGuiApi/ImString.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImString.cs#L14), [current/decomp/Brutal.ImGuiApi/ImString.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImString.cs#L104) | Why IL emission is needed |
| `Constants.DocumentsFolderPath` | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281) | Settings folder |

## Pitfalls and version notes

- **Signature selection by parameter types.** `MenuItem` has two four-parameter overloads (one takes
  `ref bool`), so the mod matches on exact parameter and return types
  ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7453)). The
  emitted methods are owned by the mod's module with `skipVisibility: true`; the targets are public, so
  the flag only matters if a non-public target is ever used.
- **No fallback menu.** Without ModMenu the switch is only reachable by editing the file with the game
  closed; compare mods that add their own menu entry when ModMenu is absent (for example
  [ModMenu entry with a fallback menu (gatOS)](../../gatos/features/modmenu-entry-with-fallback-menu.md)).
- **Hand-rolled TOML.** The parser handles exactly the one-line form it writes; quoted strings,
  tables and arrays are not understood.

## See also

- [Atmosphere uniform side channel](atmosphere-uniform-side-channel.md)
- [Haze-band sky march](haze-band-sky-march.md)
- [Reflection-only mod build](reflection-only-mod-build.md)
- [Read and write a one-key settings.toml by hand, with no TOML library](../../../howto/persistence/settings-formats/persist-mod-settings-toml/persist-mod-settings-toml-3.md) - how-to article demonstrating this feature
- [Store config in a mod-named subfolder under Constants.DocumentsFolderPath](../../../howto/persistence/user-data/choose-where-a-mod-stores-its-files/choose-where-a-mod-stores-its-files-2.md) - how-to article demonstrating this feature
- [Copy the ModMenuEntry attribute into your mod so there is no dependency and no extra DLL](../../../howto/ui/menus/list-a-mod-in-modmenu/list-a-mod-in-modmenu-3.md) - how-to article demonstrating this feature
- [Draw a ModMenu page without referencing the game: DynamicMethod wrappers for ImGui.MenuItem and SetItemTooltip](../../../howto/ui/menus/list-a-mod-in-modmenu/list-a-mod-in-modmenu-4.md) - how-to article demonstrating this feature

[^ra-menu]: Menu.cs
[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^ksa-constants]: KSA.Constants (decompiled, build 2026.10.10.5554)
