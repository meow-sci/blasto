---
type: Mod Feature
title: Enumerate the game's 954 named XKCD colors by reflection
description: XkcdColorHelper reflects over the static Color.Preset properties of KSA.KSAColor.Xkcd once, caches name and float4 pairs sorted by name, and serves them to filterable color pickers
mod: unscience
category: ui
techniques: [reflection, imgui-window]
hooks: [reflection KSA.KSAColor.Xkcd (public static properties)]
lifecycle: [frame:draw-ui]
game_types: [KSA.KSAColor, Brutal.Numerics.Color]
tags: [colors, reflection, imgui, picker, caching]
---

KSA bundles the full XKCD color survey as named presets (`KSAColor.Xkcd.CloudyBlue`,
`KSAColor.Xkcd.RadioactiveGreen`, ...). Mods that let the player pick a light or tint color by name
(Zippo's light colors, Doh's kitten tints) need them as a list, not as compile-time properties.
`XkcdColorHelper` turns the static class into a sorted, cached array on first use.[^xkcd-color-helper]

Mechanism in one line: `typeof(KSAColor.Xkcd).GetProperties(Public | Static)`, read each value as a
`Color.Preset`, convert to `float4` through the preset's implicit operator, sort by name, cache.

## How it works

- In build 5554 `KSAColor.Xkcd` is a nested static class with 954 `public static Color.Preset`
  expression-bodied properties (`=> float3.Rgb(...)`), so they are properties, not fields, and each
  `GetValue(null)` evaluates the getter.[^ksa-ksacolor]
- `Color.Preset` is a readonly struct with implicit conversions to `float3`, `float4`, `byte4` and
  others; the helper casts the boxed value to `Color.Preset` and lets the implicit `float4` operator
  produce RGBA.[^brutal-color]
- Each property read is wrapped in an empty catch so one odd member cannot break the list.
- API: `GetAll()` (sorted `(Name, float4)` array), `FindByName(name)` (case-insensitive),
  `GetNames()` (string array for combo filters). Names are the C# property names
  (`RadioactiveGreen`, not "radioactive green").
- Consumers build filterable ImGui combos with color swatches from `GetAll()`.[^zippo-submod][^doh-submod]

## Lifecycle and timing

Pure main-thread UI helper with no hooks. The first call (typically when a picker first draws in
`frame:draw-ui`) pays the reflection cost once and logs the count; later calls return the cached array.

## Key code

| Path | Role |
|---|---|
| [ksa-abstractions.lib/XkcdColorHelper.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/XkcdColorHelper.cs) | Reflection, cache and lookups |
| [zippo.lib/ZippoSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/ZippoSubmod.cs#L262) | Light color pickers |
| [doh.lib/DohSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/DohSubmod.cs#L498) | Kitten tint picker |

## Snippets

```csharp
// ksa-abstractions.lib/XkcdColorHelper.cs L18-L37
    public static (string Name, float4 Color)[] GetAll()
    {
        if (_colors != null) return _colors;

        var props = typeof(KSAColor.Xkcd).GetProperties(BindingFlags.Public | BindingFlags.Static);
        var list = new List<(string, float4)>();
        foreach (var prop in props)
        {
            try
            {
                float4 val = (Color.Preset)prop.GetValue(null)!;
                list.Add((prop.Name, val));
            }
            catch { }
        }
        list.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase));
        _colors = list.ToArray();
        Console.WriteLine($"ksa-abstractions: Cached {_colors.Length} XKCD colors");
        return _colors;
    }
```

```csharp
// current/decomp/KSA/KSAColor.cs L23-L29
	public static class Xkcd
	{
		public static Color.Preset CloudyBlue => float3.Rgb(0.6745098f, 0.7607843f, 0.8509804f);

		public static Color.Preset DarkPastelGreen => float3.Rgb(0.3372549f, 0.682353f, 0.3411765f);

		public static Color.Preset Dust => float3.Rgb(0.6980392f, 0.6f, 0.4313726f);
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `KSAColor.Xkcd` (static properties) | [current/decomp/KSA/KSAColor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KSAColor.cs#L23) | Source of names and colors |
| `Color.Preset` implicit `float4` | [current/decomp/Brutal.Numerics/Color.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Numerics/Color.cs#L8-L55) | Conversion to RGBA |

## Pitfalls and version notes

- If the game ever turns these properties into fields or another type, `GetProperties` returns nothing
  and the list is silently empty (only the logged count reveals it).
- Alpha is whatever the preset's `float4` conversion yields (the presets are built from RGB); callers that
  need translucency set alpha themselves.

## See also

- [Game-state providers](./lib-game-state-providers.md)
- [Zippo light control](./zippo-light-control.md)
- [Doh runtime GPU materials](./doh-runtime-gpu-materials.md)
- [Mod overview](../overview.md)
- [Settings window: a reflected XKCD colour catalogue behind a filterable picker](../../../howto/ui/windows/build-a-settings-window/build-a-settings-window-5.md) - how-to article demonstrating this feature
- [Searchable lists: a filterable colour catalogue list](../../../howto/ui/windows/build-searchable-lists-and-browsers/build-searchable-lists-and-browsers-4.md) - how-to article demonstrating this feature

[^xkcd-color-helper]: ksa-abstractions.lib/XkcdColorHelper.cs
[^ksa-ksacolor]: KSA.KSAColor (decompiled, build 2026.10.10.5554)
[^brutal-color]: Brutal.Numerics.Color (decompiled, build 2026.10.10.5554)
[^zippo-submod]: zippo.lib/ZippoSubmod.cs
[^doh-submod]: doh.lib/DohSubmod.cs
