---
type: How-To
title: Gate developer features on a custom key in the player's manifest.toml entry
description: Read an extra debug key that the player adds to the mod's entry in the game manifest, and know that the game rewrites the manifest with only id and enabled
category: testing-and-tooling
group: in-game-testing
topic: drive-a-running-game-through-a-file-bridge
approach: 4
difficulty: intermediate
prerequisites: [starmap, tomlet]
demonstrated_by: [mods/kittenextensions/features/patch-debug-stepper]
techniques: [toml-config, reflection]
hooks: [harmony-prefix KSA.ModLibrary.PrepareAll]
lifecycle: [startup:asset-load]
game_types: [KSA.ModManifest, KSA.ModEntry, KSA.ModLibrary, KSA.Constants]
tags: [testing, developer-only, manifest, toml, debug-switch]
---

Outcome: a developer-only feature (here a step debugger that stops the game during loading) that a player or tester switches on by adding
`debug = true` to the mod's entry in `Documents/My Games/Kitten Space Agency/manifest.toml`, with no marker file and no separate build. Demonstrated
by KittenExtensions ([patch debug stepper](../../../../mods/kittenextensions/features/patch-debug-stepper.md)). Use this gate when the
feature must be turnable on by someone who has only the release download, for example to collect a bug report. Prerequisite: StarMap and Tomlet (the game ships it).

## Background: how the game does it

- `ModLibrary.LocalManifestPath` is `Constants.DocumentsFolderPath/manifest.toml` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L180)), and the game loads it with Tomlet into `ModManifest`, a list of `ModEntry`
  ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L274)). `ModEntry` has exactly `id` and `enabled` as serialised fields ([current/decomp/KSA/ModEntry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModEntry.cs#L9-L22)).
- `ModManifest.Save()` writes the file from scratch: for each mod it emits `[[mods]]`, `id` and `enabled`, nothing else:

  ```csharp
  // current/decomp/KSA/ModManifest.cs L27-L48
  	public void Save()
  	{
  		StringBuilder stringBuilder = new StringBuilder();
  		foreach (ModEntry mod in Mods)
  		{
  			stringBuilder.AppendLine("[[mods]]");
  			StringBuilder stringBuilder2 = stringBuilder;
  			StringBuilder stringBuilder3 = stringBuilder2;
  			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder2);
  			handler.AppendLiteral("id = \"");
  			handler.AppendFormatted(mod.Id);
  			handler.AppendLiteral("\"");
  			stringBuilder3.AppendLine(ref handler);
  			stringBuilder2 = stringBuilder;
  			StringBuilder stringBuilder4 = stringBuilder2;
  			handler = new StringBuilder.AppendInterpolatedStringHandler(10, 1, stringBuilder2);
  			handler.AppendLiteral("enabled = ");
  			handler.AppendFormatted(mod.Enabled.ToString().ToLowerInvariant());
  			stringBuilder4.AppendLine(ref handler);
  			stringBuilder.AppendLine();
  		}
  		File.WriteAllText(ModLibrary.LocalManifestPath, stringBuilder.ToString());
  ```

- It is called when new mods are confirmed at startup ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L887-L898)), when orphaned or missing mods are reconciled
  ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L286-L293)), and when the player applies settings ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L1403-L1410)).
  **Every one of those drops an extra key such as `debug`.**

## Lifecycle and timing

KittenExtensions reads the key in `startup:asset-load`: its `ModLibrary.PrepareAll` prefix runs on the main thread inside the `Program` constructor, after the `ModManifest.Save()` of
the new-mods branch at [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L897-L899) and before any mod object exists, so there is no frame hook to rely on; it just reads the file. See the
[startup order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md). Because the new-mods branch saves the manifest first, the first launch after adding any new mod
rewrites the file without `debug` and the key is gone when the mod reads it; re-add it and relaunch. This follows from the code above rather than from a test.

## Steps

1. **Define private TOML classes with the extra key.** Do not touch the game's `ModEntry`; mirror it plus your key:

   ```csharp
   // KittenExtensions/Patch/Patcher.cs L210-L225
       private class ModManifestToml
       {
           [TomlDoNotInlineObject]
           [TomlField("mods")]
           public List<ModEntryToml> Mods = [];
       }

       private class ModEntryToml
       {
           [TomlField("id")]
           public string Id = "";
           [TomlField("enabled")]
           public bool Enabled = true;
           [TomlField("debug")]
           public bool Debug = false;
       }
   ```

2. **Read the file yourself, filtered by your mod id and `enabled`, cache the answer and fail closed:**

   ```csharp
   // KittenExtensions/Patch/Patcher.cs L193-L208
       private static bool? debug = null;
       public static bool DebugEnabled()
       {
           if (debug.HasValue)
               return debug.Value;
           try
           {
               var manifest = TomletMain.To<ModManifestToml>(File.ReadAllText(ModLibrary.LocalManifestPath));
               debug = manifest.Mods.FirstOrDefault(mod => mod.Id == "KittenExtensionsContinued" && mod.Enabled)?.Debug ?? false;
               return debug.Value;
           }
           catch (Exception)
           {
               return false;
           }
       }
   ```

3. **Branch on it where the developer feature lives.** KittenExtensions dumps the unpatched document to `root.xml` and shows its step debugger instead of running the patches straight through:

   ```csharp
   // KittenExtensions/Patch/Patcher.cs L56-L83
       public static void OnPrepare()
       {
           LoadData();

           PatchDebugPopup debugPopup = null;

           try
           {
               if (DebugEnabled())
               {
                   var doc = new XmlDocument();
                   doc.Load(new XPDocReader(Domain.Doc.LatestRoot));
                   doc.Save(Path.Combine(Constants.DocumentsFolderPath, "root.xml"));
                   var patchTask = new PopupTask(debugPopup = new PatchDebugPopup());
                   while (patchTask.Show)
                       patchTask.OnFrame();
           CompleteLogging();
               }
               else
                   RunPatches();
           }
           catch (Exception ex)
           {
               debugPopup?.Active = false;
         CompleteLogging();
               throw ErrorAndExit(ex.ToString(), Executor?.Next?.Patch ?? XPNodeRef.Invalid);
           }
       }
   ```

4. **Document the switch for testers** in the README, exactly as the mod does:

   ```toml
   # README.md L78-L83
   # Documents/My Games/Kitten Space Agency/manifest.toml

   [[mods]]
   id = "KittenExtensionsContinued"
   enabled = true
   debug = true
   ```

5. **Run it.** Add the key, start the game; expect the "KittenExtensions Patch Debug" window before the main menu and `root.xml` next to `manifest.toml`.

## Complete example

| File | Role |
|---|---|
| [KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs) | `DebugEnabled`, private manifest classes, the debug branch of `OnPrepare` |
| [KittenExtensions/Patch/Patcher.Debug.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.Debug.cs) | The popup the key turns on |
| [README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md) | Player-facing instruction |

## Variations and gotchas

- **The key is volatile.** Any settings Apply in the game, any new-mod confirmation or any mod-manager change rewrites the file (see Background). Treat it as a session switch, not as configuration. For something that must persist, use a file of your own.
- **Fail closed.** The reader returns `false` on any exception so a malformed manifest never turns a developer feature on.
- **Cached.** The answer is read once per process, so editing the file during a run does nothing.
- **Which gate?** Marker file ([article 1](drive-a-running-game-through-a-file-bridge-1.md)): invisible to players, survives game saves, needs the developer's own deploy script. Manifest key: needs only a text editor, does not survive a settings save. Compile flag ([article 5](drive-a-running-game-through-a-file-bridge-5.md)): the code is absent from release builds.
- **Game updates.** If `ModEntry` ever gains a `debug`-like field or `Save()` starts preserving unknown keys, the gotcha above disappears; re-read `ModManifest.Save` after an update ([Upgrade a mod to a new KSA build](../../game-updates/upgrade-a-mod-to-a-new-ksa-build/overview.md)).

## See also

- [Overview](overview.md), [article 1](drive-a-running-game-through-a-file-bridge-1.md), [article 5](drive-a-running-game-through-a-file-bridge-5.md)
- [Debug XML patches](../../../assets/validation/debug-xml-patches/overview.md), [mod.toml reference](../../../getting-started/loader/mod-toml-reference/overview.md)
- [KittenExtensions patch debug stepper](../../../../mods/kittenextensions/features/patch-debug-stepper.md)
