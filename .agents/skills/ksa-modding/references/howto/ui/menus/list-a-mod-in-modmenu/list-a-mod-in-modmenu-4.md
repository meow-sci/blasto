---
type: How-To
title: "Draw a ModMenu page without referencing the game: DynamicMethod wrappers for ImGui.MenuItem and SetItemTooltip"
description: Resolve Brutal.ImGuiApi.ImGui by name, emit one DynamicMethod per call shape that converts string to the ref struct ImString on the evaluation stack, and keep the page fail-soft with a hand-parsed settings file
category: ui
group: menus
topic: list-a-mod-in-modmenu
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/realatmospheres/features/modmenu-page-and-toml-settings]
techniques: [reflection, inter-mod-api, toml-config]
hooks:
  - modmenu ModMenuEntry
  - reflection Brutal.ImGuiApi.ImGui.MenuItem
  - reflection KSA.Constants.DocumentsFolderPath
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImString, KSA.Constants]
tags: [modmenu, dynamicmethod, il-emit, ref-struct, imstring, reflection-only, settings-toml]
---
Real Atmospheres is built against nothing from the game: its project references only `StarMap.API` and Harmony, and every KSA type is reached by reflection. That includes the ImGui calls of its one ModMenu page. The wrinkle is that `ImGui.MenuItem` and `ImGui.SetItemTooltip` take `ImString`, a `ref struct`, which `MethodInfo.Invoke` cannot box. The mod therefore emits a tiny `DynamicMethod` for each call shape. The same article shows the page's other half, a settings file under the game's documents folder. Prerequisites: StarMap, Harmony (`AccessTools` is used for type lookup) and comfort with IL emission. Demonstrated by [ModMenu page and TOML settings without game references](../../../../mods/realatmospheres/features/modmenu-page-and-toml-settings.md).

Use this only if you really cannot reference the game assemblies (see [Build a mod without game references](../../../getting-started/build/build-a-mod-without-game-references/overview.md)). If you can, approach 3 is much simpler.

## Background: how the game does it

| Fact | Where |
|---|---|
| `ImString` is a `ref struct` with an implicit conversion from `string` (`op_Implicit`) | [current/decomp/Brutal.ImGuiApi/ImString.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImString.cs#L14), conversion at [current/decomp/Brutal.ImGuiApi/ImString.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImString.cs#L104-L107)[^ksa-brutal-imguiapi-imstring] |
| `ImGui.MenuItem` has two four-parameter overloads: `(ImString label, ImString shortcut, bool selected, bool enabled)` and `(ImString label, ImString shortcut, ref bool pSelected, bool enabled)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7442-L7464)[^ksa-brutal-imguiapi-imgui] |
| `ImGui.SetItemTooltip(ImString text)` forwards to a native entry whose parameter is a printf-style `fmt` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7490-L7496), native declaration at [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L4967) |
| `Constants.DocumentsFolderPath` is `Documents/My Games/Kitten Space Agency` | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)[^ksa-ksa-constants] |

The page itself is a ModMenu entry like approaches 1 to 3; ModMenu is said to find it by attribute name (mod comment, unverified, ModMenu's source is not in the corpus).

## Lifecycle and timing

- **The page runs at `frame:draw-ui`**, whenever ModMenu draws its menu (unverified call site). It only reads and flips one boolean and writes one file.
- **Resolution is lazy, on first draw.** `ImGuiCalls.Ready` builds the delegates the first time the page is drawn, so nothing happens at startup and the ImGui types are certainly loaded by then.
- **The setting reaches the renderer later.** The same static is read by an atmosphere prefix in the render phase (`render:atmosphere`); that path also loads the file on first use if no menu was ever drawn. Both run on the main thread, so a plain static suffices. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Declare the attribute by copying it** (approach 3) and the page method, which is `public static` on a `public static class`. Everything is wrapped so a failure in ImGui lookup disables the page instead of the mod:

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

   The attribute copy is in the same file, [Menu.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Menu.cs#L37-L49).

2. **Resolve the game's types by name.** `AccessTools.TypeByName` (Harmony) finds `Brutal.ImGuiApi.ImGui` and `ImString`; the string-to-`ImString` converter is the static `op_Implicit` whose parameter is `string`. Look the overloads up by **exact parameter and return types**, because `MenuItem` has two four-parameter overloads and only one has `bool` in the third position:

```csharp
// Menu.cs L94-L113
    internal static void Resolve()
    {
        Type imgui = AccessTools.TypeByName("Brutal.ImGuiApi.ImGui") ?? throw new InvalidOperationException("Brutal.ImGuiApi.ImGui not found");
        Type imString = AccessTools.TypeByName("Brutal.ImGuiApi.ImString") ?? throw new InvalidOperationException("ImString not found");
        MethodInfo fromString = imString.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "op_Implicit" && m.ReturnType == imString
                                 && m.GetParameters() is { Length: 1 } p && p[0].ParameterType == typeof(string))
            ?? throw new InvalidOperationException("ImString from string not found");

        MethodInfo Find(string name, params Type[] types) =>
            imgui.GetMethod(name, BindingFlags.Public | BindingFlags.Static, types)
            ?? throw new InvalidOperationException($"ImGui.{name} not found");

        _tooltip = TextLike(Find("SetItemTooltip", imString), fromString);

        MethodInfo menuItem = imgui.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "MenuItem" && m.GetParameters() is { Length: 4 } p && p[0].ParameterType == imString
                                 && p[1].ParameterType == imString && p[2].ParameterType == typeof(bool)
                                 && p[3].ParameterType == typeof(bool) && m.ReturnType == typeof(bool))
            ?? throw new InvalidOperationException("ImGui.MenuItem(label, shortcut, selected, enabled) not found");
```

3. **Emit one `DynamicMethod` per call shape.** The method takes the plain `string` and `bool`, converts the string to an `ImString` on the evaluation stack, builds an empty `ImString` shortcut with `initobj`, pushes `selected` and `enabled`, calls the real method and returns. Wrap the method in a typed delegate:

```csharp
// Menu.cs L114-L127
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

   A shape with one string argument (the tooltip) is generated by a shared helper:

```csharp
// Menu.cs L130-L140
    private static TextCall TextLike(MethodInfo call, MethodInfo fromString)
    {
        var method = new DynamicMethod("RealAtmospheres_ImGui_" + call.Name, typeof(void), new[] { typeof(string) },
                                       typeof(ImGuiCalls).Module, skipVisibility: true);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, fromString);
        il.Emit(OpCodes.Call, call);
        il.Emit(OpCodes.Ret);
        return (TextCall)method.CreateDelegate(typeof(TextCall));
    }
```

4. **Call the delegates, not `MethodInfo`.** The page draws one checkable item and a tooltip. A tooltip is a printf format, so `%` is doubled before the call:

```csharp
// Menu.cs L87-L91
    // A tooltip is a printf format, so a percent sign is written twice to show once.
    internal static void SetItemTooltip(string text) => _tooltip!(text.Replace("%", "%%"));

    /// <summary>A menu item, ticked when <paramref name="selected"/>; true on the frame it is clicked.</summary>
    internal static bool MenuItem(string label, bool selected) => _menuItem!(label, selected);
```

5. **Fail soft.** `Ready` resolves once; if any lookup throws, it logs once and leaves the page empty (`_ready` stays false). An exception while drawing sets `_broken` and disables the page for the session. Both matter more than usual here because a game update that renames an overload would otherwise throw into ModMenu's menu build.

6. **Persist the setting through reflection too.** The settings path is read through the property getter so any instance-folder redirection applies, then combined with the mod's folder name. Only the one key is parsed, with a deliberately tiny reader:

```csharp
// Settings.cs L26-L44
    /// <summary>The file, under the game's documents folder (KSA.Constants.DocumentsFolderPath).</summary>
    internal static string? FilePath
    {
        get
        {
            if (_path != null) return _path;
            try
            {
                Type? constants = AccessTools.TypeByName("KSA.Constants");
                if (AccessTools.Property(constants, "DocumentsFolderPath")?.GetValue(null) is string documents)
                    _path = Path.Combine(documents, "RealAtmospheres", "settings.toml");
            }
            catch (Exception ex)
            {
                ShadowBuilder.Log("WARN: the game's documents folder was not found; settings are not kept: " + ex.Message);
            }
            return _path;
        }
    }
```

   Writing is immediate, because a menu click is a discrete event, and fails soft:

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

   The consuming code reads the static and invalidates what depends on it when the value changes ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L339-L358)).

7. **Check it.** With ModMenu installed, open the Real Atmospheres page, click the item, and confirm `settings.toml` appears under `Documents/My Games/Kitten Space Agency/RealAtmospheres/` with the new value. Break one lookup name on purpose to confirm the page stays empty and the log carries a single warning.

## Complete example

| File | Role |
|---|---|
| [Menu.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Menu.cs#L12-L141) | Page, attribute copy, `ImGuiCalls` resolution and IL emission |
| [Settings.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Settings.cs#L10-L119) | File location, parse, write |
| [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L339-L358) | Where the setting is consumed |
| [RealAtmospheres.csproj](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/RealAtmospheres.csproj#L29-L36) | References only `StarMap.API` and `0Harmony` |

## Variations and gotchas

- **Overload selection by exact types.** The `ref bool` overload's third parameter type is `bool&`, not `bool`; comparing with `typeof(bool)` picks the by-value overload, which is what the mod wants. Re-check the signature after each game update.
- **`skipVisibility: true`** on the emitted methods is only needed if a non-public target is ever called; the targets here are public.
- **`ImString` lifetime.** The implicit conversion builds the `ImString` inside the emitted method, on the stack, and the call consumes it immediately; never store one across calls.
- **Only works when ModMenu is present.** Like every `ModMenuEntry` method, nobody calls it otherwise; the file can still be edited by hand with the game closed. A fallback menu that does not need ModMenu (see [the fallback pattern](../add-a-menu-with-modmenu-fallback/overview.md)) would need the same reflection tricks to draw `BeginMenu`/`MenuItem` from a Harmony postfix.
- **The hand-rolled TOML reader** understands exactly the one-line form it writes (`key = value`, `#` comments); quoted strings, tables and arrays are not handled. For anything larger use a real TOML library; see [Persist mod settings as TOML](../../../persistence/settings-formats/persist-mod-settings-toml/overview.md).
- **Compile-time alternative.** If you can reference `Brutal.ImGuiApi`, none of this is needed; the call is `ImGui.MenuItem("label", default, ref flag, true)`.

## See also

- [Overview](overview.md)
- [Approach 3: copy the attribute source into the mod](list-a-mod-in-modmenu-3.md)
- [Build a mod without game references](../../../getting-started/build/build-a-mod-without-game-references/overview.md)
- [Persist mod settings as TOML](../../../persistence/settings-formats/persist-mod-settings-toml/overview.md)
- Mod feature: [ModMenu page and TOML settings without game references](../../../../mods/realatmospheres/features/modmenu-page-and-toml-settings.md)

[^ksa-brutal-imguiapi-imstring]: Brutal.ImGuiApi.ImString (decompiled, build 2026.10.10.5554)
[^ksa-brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^ksa-ksa-constants]: KSA.Constants (decompiled, build 2026.10.10.5554)
