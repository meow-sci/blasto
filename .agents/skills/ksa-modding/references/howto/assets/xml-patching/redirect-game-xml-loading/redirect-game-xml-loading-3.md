---
type: How-To
title: Edit shadow copies with anchor-verified text patches and generated XML between markers
description: Make stock XML edits that survive game updates by requiring each stock text anchor to occur exactly once, guarding with idempotent markers, formatting numbers invariantly, and splicing generated XML between begin and end markers
category: assets
group: xml-patching
topic: redirect-game-xml-loading
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/realatmospheres/features/shadow-tree-anchor-patching
  - mods/realatmospheres/features/atmosphere-optics-in-system-xml
  - mods/ksastructures/features/generated-deep-ramp
techniques: [xml-patch, code-generation]
hooks: [starmap StarMapBeforeMain]
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types:
  - KSA.Mod
  - KSA.AssetBundle
  - KSA.SystemTemplate
  - KSA.AtmosphericBodyTemplate
  - KSA.PlanetaryBodyTemplate
  - KSA.AstronomicalTemplate
tags: [xml, text-patch, anchors, idempotent, markers, shadow-copy, generated]
---

When the redirect from [Swap Core's XML entries for patched copies with prefixes on the Mod loaders](redirect-game-xml-loading-1.md) hands the game a copy of a Core file, the copy has to be
edited. Real Atmospheres edits it as plain text with search-and-replace anchors taken verbatim from the stock
file, and has one rule: if the stock text is not exactly what the mod was written against, leave that
edit alone and say so in the log. KSAStructures solves the sibling problem for its own hand-written asset XML:
a layout script rewrites only the region between marker comments, so generated colliders and the rest of the
file coexist and a rerun is a no-op when nothing changed. Together they cover two things: editing files you do
not own (anchors) and files you partly generate (markers).

Demonstrated by [Anchor-verified text patching of the shadow tree](../../../../mods/realatmospheres/features/shadow-tree-anchor-patching.md), [Per-body atmosphere optics in the system XML](../../../../mods/realatmospheres/features/atmosphere-optics-in-system-xml.md)
and [A generated spiral ramp - mesh, shadow caster, box colliders and lamps from one script](../../../../mods/ksastructures/features/generated-deep-ramp.md).

## Background: how the game does it

The game reads the XML you produce with its normal serializers, so text edits must stay valid XML for the
template classes. Two engine facts shape the edits in the atmosphere case:

- `AtmosphericBodyTemplate` extends `PlanetaryBodyTemplate`, so renaming a `PlanetaryBody` element to
  `AtmosphericBody` keeps its existing children valid; `MinorBody` is a different branch and needs the mesh and
  colour children added ([current/decomp/KSA/AtmosphericBodyTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphericBodyTemplate.cs), [current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L14-L14)).
- The element type, not just an `<Atmosphere>` child, decides whether the renderer builds an atmosphere for a
  body. Library bodies (`LoadFromLibrary` references in `SolSystem.xml`) can only be edited in
  `Astronomicals.xml`.

Content that is "inline" in the system file and content that is in the library are different files with
different redirects ([Swap Core's XML entries for patched copies with prefixes on the Mod loaders](redirect-game-xml-loading-1.md)).

## Lifecycle and timing

The edits happen at `startup:starmap-before-main`, inside the mod's `BeforeMain`, before `Program.Main`. They
only need file IO on the install's `Content/Core` and the mod folder, so no game state is required. The edited
files are consumed at `startup:immediate-load` and `startup:asset-load`. The cost (a few text passes over the
system XML) is paid on the main thread before the game window opens. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Keep a stock base and copy fresh each launch.** Read with CRLF turned into LF so multi-line anchors
   written in C# raw string literals match whatever line endings the install has:

   ```csharp
   // ShadowBuilder.cs L1054-L1070
       private static bool PatchFile(string path, Func<string, string> transform)
       {
           if (!File.Exists(path))
           {
               Log($"WARN: shadow file missing: {path}");
               return false;
           }
           string before = ReadLf(path);
           string after = transform(before);
           File.WriteAllText(path, after);
           return !ReferenceEquals(before, after) && before != after;
       }

       /// <summary>Read with newlines normalized to LF so multi-line anchors match
       /// regardless of the file's line endings (same as the Python patchers).</summary>
       private static string ReadLf(string path) =>
           File.ReadAllText(path).Replace("\r\n", "\n");
   ```

2. **Express each edit as a triple: stock text, corrected text, marker.** The marker is a phrase only the corrected
   text contains. Payloads keep their closing `"""` at column 0 so the indentation inside them is byte-for-byte
   the stock file's:

   ```csharp
   // Payloads.cs L71-L88
       public static readonly (string Stock, string Corrected, string Marker)[] EarthPairs =
       {
           (
   """
   <RayleighScattering>
                       <Coefficients R="0.0058" G="0.0135" B="0.0331" />
                       <ScaleHeight Km="8" />
                   </RayleighScattering>
   """.Trim('\n'),
   """
   <RayleighScattering>
                       <!-- Real Atmospheres: US Standard Atmosphere 1976 density in the shader; column-equivalent scale height -->
                       <Coefficients R="0.0058" G="0.0135" B="0.0331" />
                       <ScaleHeight Km="8.43" />
                   </RayleighScattering>
   """.Trim('\n'),
               "US Standard Atmosphere 1976 density in the shader"
           ),
       // ...
   ```

3. **Apply with an exactly-once rule.** If the marker is present the pair is skipped silently (idempotent); if
   the stock text occurs any other number of times it logs a warning and skips:

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

4. **Group dependent edits all-or-nothing.** Where several sites must agree (heights and densities of a
   cloud deck, extinction and scattering in one shader) count every anchor first and write nothing unless all
   match; otherwise a single drifted anchor leaves the file half-edited. The Rayleigh colour patch does it per
   file for shaders; the same shape applies to XML:

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

5. **Scope edits to one body.** XML edits that would collide across bodies are applied inside one body's
   `<AtmosphericBody Id="...">` to `</AtmosphericBody>` span (or one `<Layer Id="...">` span), found with
   `IndexOf`. Insertions anchor on stable structure, for example "after the `</Layer>` of a known layer":

   ```csharp
   // ShadowBuilder.cs L479-L507
       private static string PatchCirrusLayer(string s, string assetsDir)
       {
           if (s.Contains(Payloads.CirrusMarker))
               return s; // already present

           int i = s.IndexOf("Id=\"MarsDustStorms\"", StringComparison.Ordinal);
           if (i < 0)
           {
               Log("WARN: MarsDustStorms layer not found, skipping cirrus layer");
               return s;
           }
           int j = s.IndexOf("</Layer>", i, StringComparison.Ordinal);
           if (j < 0)
           {
               Log("WARN: MarsDustStorms </Layer> not found, skipping cirrus layer");
               return s;
           }
           j += "</Layer>".Length;

           string layer = Payloads.CirrusLayerTemplate.Replace("{ASSETS}", assetsDir);
           foreach (string tex in new[] { "MarsCirrusMaskVolumetric.dds", "MarsCirrusMask2D.png" })
           {
               if (!File.Exists(Path.Combine(assetsDir, tex)))
               {
                   Log($"WARN: missing asset {tex}, skipping cirrus layer");
                   return s;
               }
           }
           return s[..j] + layer + s[j..];
       // ...
   ```

6. **Promote or rename an element, and append a block.** `UpgradeBody` renames the open and close tags
   to `AtmosphericBody`, optionally inserts a minimal kit and appends the atmosphere block before the close.
   It is guarded by "already upgraded" and "anchor not found":

   ```csharp
   // ShadowBuilder.cs L581-L603
       private static string UpgradeBody(string s, string name, string openTag, string closeTag,
           string? extraKit, string atmosphere)
       {
           if (s.Contains($"<AtmosphericBody Id=\"{name}\""))
               return s; // already upgraded

           int i = s.IndexOf(openTag, StringComparison.Ordinal);
           int j = i < 0 ? -1 : s.IndexOf(closeTag, i, StringComparison.Ordinal);
           if (j < 0)
           {
               Log($"WARN: {name} body anchor not found, skipping atmosphere upgrade");
               return s;
           }

           string newOpen = openTag
               .Replace("<PlanetaryBody ", "<AtmosphericBody ")
               .Replace("<MinorBody ", "<AtmosphericBody ");
           string insert = (extraKit == null ? "" : "\n        " + extraKit.Trim('\n'))
               + "\n        " + atmosphere.Trim('\n') + "\n    ";
           return s[..i] + newOpen + s[(i + openTag.Length)..j] + insert + "</AtmosphericBody>"
               + s[(j + closeTag.Length)..];
       }

   ```

7. **Gate edits on the assets they reference.** An insertion that points at a texture in the mod's `assets/` only
   applies if the file exists (see `PatchCirrusLayer` above), so deleting an asset reverts that one feature
   and the game never loads a path that is not there.

8. **Format numbers with the invariant culture.** Every number written into XML must use
   `CultureInfo.InvariantCulture`; a decimal comma once made `Astronomicals.xml` fail to load and the game fail to
   start on German, French and Spanish systems ([CHANGELOG.md](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/CHANGELOG.md#L40-L44)). The generated
   XML payloads splice attribute text from a generated table class so a regeneration updates XML and shader
   tables together.

9. **For files you partly generate, rewrite only between markers.** Put begin and end comments in the
   hand-written XML, then replace everything between them and leave the rest alone. KSAStructures'
   script refuses to run if the markers are missing:

   ```python
   # tools/model/office-deep.py L319-L325
   xml_path = ROOT / "KSAStructuresOffice.xml"
   xml = xml_path.read_text()
   begin, end = "  <!-- office-deep.py: begin -->\n", "  <!-- office-deep.py: end -->\n"
   if begin not in xml:
       sys.exit("KSAStructuresOffice.xml has no office-deep.py markers")
   xml = xml[:xml.index(begin) + len(begin)] + "\n".join(lines) + "\n" + xml[xml.index(end):]
   xml_path.write_text(xml)
   ```

   The asset file carries the markers; the generated `PbrMaterial` and the `StaticSubObject` entries with their
   collider boxes lie between them:

   ```xml
   <!-- src/KSAStructures/KSAStructuresOffice.xml L37-L38 -->
     <!-- office-deep.py: begin -->
     <PbrMaterial Id="KSAOffice_DeepMaterial">
     <!-- ... -->
   ```

   Because the region is regenerated whole, a rerun with unchanged inputs produces the same text. Constants that the
   script does not regenerate (for example the C# restatement of shaft bounds) must be updated by hand.

## Complete example

- [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs) (`Build`, `PatchAtmosphereValues`, `UpgradeBody`, `PatchFile`)
- [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs) (the triples and their conventions)
- [tools/model/office-deep.py](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/model/office-deep.py) and [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml)
  (marker splice; the geometry side is in [Export or generate a GLB that KSA loads correctly](../../models-and-textures/export-glb-from-blender-for-ksa/overview.md))

## Variations and gotchas

- **Partial application is a real state.** A game update can move one anchor and leave the rest. Edits that depend
  on each other are grouped per file, and some check for another edit's marker first.
- **Anchors guarantee text, not validity.** A payload that is valid text but invalid for the serializer breaks
  the load of the whole file (for XML) or the shader module (for GLSL, which does not fall back to stock;
  [Replace or text-patch the game's GLSL without editing game files](../../../shaders/stock-shaders/replace-or-patch-game-glsl-without-editing-game-files/overview.md)).
- **Detect the game doing it first.** Patch only if the stock text still has the expected shape, so a game build
  that adds the same feature does not get it twice.
- **Do not edit the install.** Always write copies; the redirect points the game at them and a game update
  simply changes the source the copy is made from.
- **Marker splice vs anchors.** Markers suit files you own (round-trippable); anchors suit files you do not.
  Do not mix them in one file without a clear owner for each region.

## See also

- [Swap Core's XML entries for patched copies with prefixes on the Mod loaders](redirect-game-xml-loading-1.md), [Let two mods edit the same Core file with a shared shadow folder and a named mutex](redirect-game-xml-loading-4.md), [Patch Core or another mod's XML data without copying files](../patch-game-xml-data/overview.md)
- [Define atmospheres, scattering and cloud layers for a body in system XML](../../../celestials/atmospheres-and-rings/define-atmospheres-and-cloud-layers-in-system-xml/overview.md), [Replace or text-patch the game's GLSL without editing game files](../../../shaders/stock-shaders/replace-or-patch-game-glsl-without-editing-game-files/overview.md)
- [Anchor-verified text patching of the shadow tree](../../../../mods/realatmospheres/features/shadow-tree-anchor-patching.md), [A generated spiral ramp - mesh, shadow caster, box colliders and lamps from one script](../../../../mods/ksastructures/features/generated-deep-ramp.md)
