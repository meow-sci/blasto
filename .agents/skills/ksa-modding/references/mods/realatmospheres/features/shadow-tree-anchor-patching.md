---
type: Mod Feature
title: Anchor-verified text patching of the shadow tree
description: Every edit to the copied shaders and XML is a stock-text anchor with an exact expected count, applied all-or-nothing per file, skipped if its marker is present, and logged and skipped when a game update moves it
mod: realatmospheres
category: shaders
techniques: [glsl-shader, xml-patch, fail-soft-patching]
hooks: [starmap StarMapBeforeMain]
lifecycle: [startup:starmap-before-main]
game_types: []
tags: [shaders, xml, text-patching, fail-soft, idempotent, glsl]
---

The redirects in [Shader file redirect](shader-file-redirect.md) and
[Core XML entry redirect](core-xml-entry-redirect.md) only point the game at copies. The copies are
made by `ShadowBuilder.Build` at every launch from the player's own install, then edited as plain text
with search-and-replace anchors taken verbatim from the stock files. The engine is small and has one
rule: if the stock text is not exactly what the mod was written against, leave that file alone and say
so in the log.[^ra-shadowbuilder]

## How it works

- **Fresh copy each launch.** The shader tree is deleted and copied again
  ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L44-L49)); the system XML goes through the shared shadow,
  which re-copies stock once per game process ([Shared/SharedShadow.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Shared/SharedShadow.cs#L64-L82)).
  Nothing accumulates between launches, and a game update changes the source the copy is made from.
- **LF everywhere.** Files are read with CRLF turned into LF, so multi-line anchors written in C#
  raw string literals match whatever line endings the install has
  ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L1067-L1070)). Payload literals keep their closing `"""` at
  column 0 so the indentation inside them is byte-for-byte the stock file's
  ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L3-L10)).
- **Triples of (stock, corrected, marker).** A pair applies only if the stock text occurs exactly once;
  if the marker (a phrase only the corrected text contains) is already present, the pair is skipped
  silently; any other count logs a warning and skips ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L348-L364)).
  Shader edits use `(file, stock, patch, expectedCount)` because some anchors legitimately appear
  twice or four times ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2283-L2289)).
- **All-or-nothing per file.** Where several sites must agree (Rayleigh extinction and scattering in
  the same shader, the four anchors of the face-lighting patch, Jupiter's heights and densities), every
  anchor is counted first and nothing is written unless all match
  ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L667-L689), [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L536-L574)).
- **Scoped edits.** XML edits that would collide across bodies are applied inside one body's
  `<AtmosphericBody Id="...">` ... `</AtmosphericBody>` span, or inside a layer's `<Layer Id="...">`
  span, found with `IndexOf` ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L401-L432)).
- **Insertions anchor on stable structure.** Functions go in front of `void main()\n{` or before an
  include guard's `#endif`; layers go after a known layer's `</Layer>`
  ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L627-L646), [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L479-L508)).
- **Feature gating by asset presence.** Edits that reference a texture in `assets/` only apply if the
  file exists, so deleting an asset reverts that one feature ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L180-L190)).
- **Invariant numbers.** Every number formatted into XML uses `CultureInfo.InvariantCulture`; a
  decimal comma once made `Astronomicals.xml` fail to load and the game fail to start
  ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L220-L226), [CHANGELOG.md](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/CHANGELOG.md#L40-L44)).

All anchors in the mod at this commit were checked against build 2026.10.10.5554 for this page. The
check ran `ShadowBuilder.Build` with `Payloads.cs`, the generated tables and `SharedShadow.cs` in a
scratch console program against `current/Content/Core`, with placeholder files for the assets. Every
patch applied and no warning was logged. Eleven shader files changed: `AtmosphereData.glsl`,
`AtmosphereFunctions.glsl`, `Atmosphere.comp`, `AerialPerspectiveLut.comp`, `Godrays.glsl`,
`2DCloud.comp`, `CloudFunctions.glsl`, `CloudShadows.glsl`, `RaymarchCloud.comp`, `Planet.frag` and
`GroundClutter/Solid.frag`. Every `.comp`, `.frag` and `.vert` in the patched tree was then compiled
with `glslc --target-env=vulkan1.3`. The set of files that failed was the same as for the stock
tree: four shaders that need defines the game supplies, none of them patched.

## Lifecycle and timing

- **`startup:starmap-before-main`.** `ShadowBuilder.Build` runs inside `ModMain.BeforeMain`, before
  `Program.Main` ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L58-L62)). It only does file IO on the install's
  `Content/Core` and the mod folder, so no game state is needed. The cost (copying the shader tree,
  a few regex passes over the system XML) is paid on the main thread before the game window opens.
- The edited files are consumed later: system XML during `startup:immediate-load` and
  `startup:asset-load`, shaders when they compile in `ModLibrary.LoadAll`.

## Key code

| Path | Role |
|---|---|
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L39-L134) | `Build`: shader tree, phase block choice, shader patches, shared XML shadow |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L348-L364) | `PatchAtmosphereValues`: the (stock, corrected, marker) engine |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L667-L689) | Per-file all-or-nothing site groups |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L983-L990) | `SetElement`: regex replace of a self-closing element's attribute |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L1054-L1088) | `PatchFile`, `ReadLf`, `CopyTree`, `CountOf` |
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1-L16) | Payload conventions (LF, column-0 literals, markers) |

## Snippets

```csharp
// ShadowBuilder.cs L348-L364
    private static string PatchAtmosphereValues(
        string s, (string Stock, string Corrected, string Marker)[] pairs, string label)
    {
        foreach (var (stock, corrected, marker) in pairs)
        {
            if (s.Contains(marker))
                continue; // already applied (e.g. hand-patched install)
            int n = CountOf(s, stock);
            if (n != 1)
            {
                Log($"WARN: {label} atmosphere anchor found {n}x (expected 1), skipping: {Truncate(stock)}");
                continue;
            }
            s = s.Replace(stock, corrected);
        }
        return s;
    }
```

```csharp
// ShadowBuilder.cs L667-L689
    private static void PatchRayleighColour(string shadersDir)
    {
        int done = 0, files = 0;
        foreach (var group in Payloads.RayleighColourSites.GroupBy(site => site.File))
        {
            files++;
            string path = Path.Combine(shadersDir, group.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) { Log($"WARN: {group.Key} missing, skipping its Rayleigh colour"); continue; }
            string s = ReadLf(path);
            if (s.Contains(Payloads.RayleighColourCall)) { done++; continue; } // already applied
            var off = group.Where(site => CountOf(s, site.Stock) != site.Count).ToList();
            if (off.Count > 0)
            {
                Log($"WARN: {group.Key} Rayleigh anchor '{off[0].Stock}' found {CountOf(s, off[0].Stock)}x (expected {off[0].Count}), leaving its colour stock");
                continue;
            }
            foreach (var site in group)
                s = s.Replace(site.Stock, site.Patch);
            File.WriteAllText(path, s);
            done++;
        }
        Log($"Rayleigh colour by altitude ({done}/{files} files)");
    }
```

## Game APIs touched

None at runtime: this is text work on `current/Content/Core`. The files it edits are listed on the
pages for each change, for example
[current/Content/Core/Shaders/Atmosphere/AtmosphereFunctions.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/AtmosphereFunctions.glsl#L12-L22)
(the stock `MiePhaseFunction` span the phase patch replaces).

## Pitfalls and version notes

- **Partial application is a real state.** A game update can move one anchor and leave the rest; the
  mod then runs with some edits and not others. Edits that depend on each other are grouped per file
  for this reason, and a few check for another edit's marker first (the Venus shell patch shades
  `raMapColour` only if the skylight patch added it, [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L1037-L1049)).
- **A shader that fails to compile is not fail-soft.** The anchors guarantee the text was found, not
  that the result compiles. A wrong payload makes `FromFile` throw `ShaderCompilationException` during
  `ModLibrary.LoadAll`. `FileReference.Load` catches it and logs an error, which leaves the
  `ShaderReference` without a module
  ([current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L67-L108)).[^ksa-filereference]
  The first pipeline that converts it to a `VkShaderModule` then throws `NullReferenceException`
  ([current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L64-L67)).[^ksa-shaderreference]
  So the failure surfaces later, as an exception where the renderer that uses the shader is built
  (inferred from the code), and never as a fallback to the stock shader.
- **Detecting the game doing it first.** The cloud ring-shadow patch skips a file that already
  mentions `RingShadow` or `useRingShadows`, so a game build that adds its own ring shadows to clouds
  does not get them twice ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L730-L736)).
- **Version history in the anchors.** Comments record which game build moved an anchor: the haze
  march anchor only matches `void main()\n{` because 2026.9.22 added a line at the top of `main`
  ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2469-L2472)); the march was adapted to the 2026.8.3 move of per-planet
  data into a uniform buffer ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2349-L2351)).

## See also

- [Shader file redirect into a patched shadow tree](shader-file-redirect.md)
- [Core XML entry redirect](core-xml-entry-redirect.md)
- [Shared shadow for two cooperating mods](cross-mod-shared-shadow.md)
- [XPath patch operations (KittenExtensions)](../../kittenextensions/features/xpath-patch-operations.md), a declarative alternative for XML
- [Edit shadow copies with anchor-verified text patches and generated XML between markers](../../../howto/assets/xml-patching/redirect-game-xml-loading/redirect-game-xml-loading-3.md) - how-to article demonstrating this feature
- [Replace or text-patch the game's GLSL: a shadow tree, a FromFile redirect and anchor-verified edits](../../../howto/shaders/stock-shaders/replace-or-patch-game-glsl-without-editing-game-files/replace-or-patch-game-glsl-without-editing-game-files.md) - how-to article demonstrating this feature

[^ra-shadowbuilder]: ShadowBuilder.cs
[^ksa-filereference]: KSA.FileReference (decompiled, build 2026.10.10.5554)
[^ksa-shaderreference]: KSA.ShaderReference (decompiled, build 2026.10.10.5554)
