---
type: How-To
title: Debug a failing XML patch with the KittenExtensions step debugger
description: Set debug = true on the KittenExtensions manifest entry to stop startup in an ImGui stepper with Run All, Step, Step Over, Step Out, an XPath tester and a live document tree
category: assets
group: validation
topic: debug-xml-patches
approach: 1
difficulty: intermediate
prerequisites: [starmap, kittenextensions]
demonstrated_by: [mods/kittenextensions/features/patch-debug-stepper]
techniques: [xml-patch, imgui-window]
hooks: [harmony-prefix KSA.ModLibrary.PrepareAll]
lifecycle: [startup:asset-load]
game_types: [KSA.ModLibrary, KSA.ModEntry, KSA.ModManifest, KSA.Constants, KSA.Popup, KSA.Program]
tags: [xml-patch, debugging, imgui, manifest, kittenextensions, xpath]
---

An XPath that matches nothing is, as far as the README and the KittenExtensions code show, not reported as an error
(the XPathPatch executor is not checked out, so confirm it in the stepper): the operation has no target and the game
starts as if your patch did not exist. KittenExtensions (KX) ships a step debugger for exactly this. Add `debug = true` to its entry in
`manifest.toml` and the next launch stops during loading in a full-window popup where you can run the patches one
action at a time, see what each targeted and read, try XPath expressions against any node and watch the document
change. The unpatched document is also written to `root.xml`.

Demonstrated by [Patch debug stepper](../../../../mods/kittenextensions/features/patch-debug-stepper.md). Patch basics are in [Patch Core or another mod's XML data without copying files](../../xml-patching/patch-game-xml-data/overview.md) and the
operations in [Write XPath patch operations for game XML](../../xml-patching/xpath-xml-patch-recipes/overview.md).

## Background: how the game does it

The game's `ModEntry` has only `id` and `enabled` TOML fields, so a `debug` key is KX's own addition: KX parses
`manifest.toml` a second time with private classes that include `debug`. The manifest lives in the documents
folder next to the logs:

```csharp
// current/decomp/KSA/ModLibrary.cs L176-L180
	private static readonly StringEnumCollection<TextureAction> _textureActions = new StringEnumCollection<TextureAction>(toProper: false);

	public static string LocalModsFolderPath => Path.Combine(Constants.DocumentsFolderPath, "mods");

	public static string LocalManifestPath => Path.Combine(Constants.DocumentsFolderPath, "manifest.toml");
```

The popup is possible this early because `Program`'s constructor has already created the window, renderer and ImGui
context and then blocks inside `ModLibrary.PrepareAll` while KX's prefix pumps frames itself
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L880-L900)). That is the same mechanism the game uses for its own loading and new-mods dialogs.

## Lifecycle and timing

`startup:asset-load`, inside KX's Harmony prefix on `ModLibrary.PrepareAll`, after the GameData document is built and
before any patch action beyond the first one the popup constructor steps. Startup is paused while the popup is open: no
`Mod` object, asset bundle or universe exists and no `StarMapImmediateLoad` has run. Everything runs on the main
thread. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Enable the flag.** Edit `Documents/My Games/Kitten Space Agency/manifest.toml` and add `debug = true` to the
   KittenExtensions entry (the README documents the id `KittenExtensionsContinued`):

   ```toml
   # README.md L78-L83
   # Documents/My Games/Kitten Space Agency/manifest.toml

   [[mods]]
   id = "KittenExtensionsContinued"
   enabled = true
   debug = true
   ```

   KX only honours the flag on an entry whose `id` is `KittenExtensionsContinued` and that is enabled; any exception
   while reading it means off:

   ```csharp
   // KittenExtensions/Patch/Patcher.cs L193-L209
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

2. **Launch the game.** With the flag set, `OnPrepare` dumps the unpatched document to `root.xml` beside
   `manifest.toml` and then blocks in a `PatchDebugPopup` until it is closed:

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

3. **Run or step.** The popup title is "KittenExtensions Patch Debug". Use these controls while actions remain, all
   disabled once the executor reports an error; "Start Game" appears when everything has run:

   ```csharp
   // KittenExtensions/Patch/Patcher.Debug.cs L115-L133
         if (!Executor.Done)
         {
           if (StepButton("Run All Patches", enabled: !HasError, sameLine: false))
           {
             Executor.StepToEnd();
             UpdatePath();
           }
           if (StepButton("Step", enabled: !HasError))
             OnStep(Executor.Step());
           if (StepButton("Step Over", enabled: !HasError))
             OnStep(Executor.StepOver());
           if (StepButton("Step Out", enabled: !HasError))
             OnStep(Executor.StepOut());
         }
         else
         {
           if (ImGui.Button("Start Game"))
             Active = false;
         }
   ```

   | Control | Effect (the executor's `StepToEnd`, `Step`, `StepOver`, `StepOut`; the XPathPatch source is not in the checkout, so the nested-block behaviour is by the usual debugger meaning) |
   |---|---|
   | Run All Patches | `StepToEnd`: run every remaining action |
   | Step | execute the next action, descending into `With`/`If` bodies |
   | Step Over | execute the next action as one unit |
   | Step Out | run to the end of the enclosing block |

4. **Read the Action Tree tab.** Each patch action is a node labelled with its type, position (for positional
   actions) and target path relative to its context. Failed actions are red and the next action is green. Selecting
   one shows Context, Patch, Position, Target, Target XPath and the first 100 target results, Source, Source XPath
   and the first 100 source results. A target with zero results is a path that matched nothing, which is the usual
   bug.

5. **Test XPath in the XPath Tester tab.** The expression is evaluated on every edit against the chosen context
   node; results show as boolean, number, string or node, with a node-set count and a "Set Context" button per
   node. Iterate there before editing the patch file.

6. **Watch the document.** The right pane draws the live GameData document. "Follow" toggles keep the view on the
   current or next action, on its target or its patch; after an error it follows the failing patch element.

7. **Fix, restart, repeat.** Patches are read at startup only, so each fix needs a restart. Compare against
   `root.xml` to see what the document looked like before your patch. When finished, remove `debug = true`
   (or set it false) and click Start Game to continue with the patched document.

## Complete example

- [KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L56-L83) (`OnPrepare`), [KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L193-L209) (`DebugEnabled`)
- [KittenExtensions/Patch/Patcher.Debug.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.Debug.cs) (`PatchDebugPopup`)
- [README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L76-L85) (documented flag)

## Variations and gotchas

- **The game can silently drop the flag.** `ModManifest.Save()` writes only `id` and `enabled` for each entry
  ([current/decomp/KSA/ModManifest.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModManifest.cs#L27-L48)). It runs when the game confirms new mods in `Program`'s constructor
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L887-L898)) and when `PrepareManifest` reconciles orphaned or missing mods
  ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L274-L323)), all before KX's prefix reads the file. On such a launch the debugger does not
  appear; add `debug = true` again and restart. (Derived from the code, not tried on a live manifest.)
- **Wrong id means no popup.** The flag is matched on `id == "KittenExtensionsContinued"`; an install of the
  original KittenExtensions with another id never debugs.
- **Startup is blocked.** Leaving the popup open stalls loading until it is closed; use Start Game once every action has run.
- **Errors exit the game.** In normal mode a failing patch shows an error popup and exits. In debug mode the error
  is shown in the tree with the failing element so you can read the XPath results first.
- **Disabled mods still patch.** Every manifest entry's patches are in the document, enabled or not; check the
  tree's `Mod` nodes when a value changes unexpectedly.
- **Use the log for the aftermath.** [Audit what patches changed from KittenExtensions.log](debug-xml-patches-2.md) shows what changed after the run.

## See also

- [Audit what patches changed from KittenExtensions.log](debug-xml-patches-2.md), [Patch Core or another mod's XML data without copying files](../../xml-patching/patch-game-xml-data/overview.md), [Write XPath patch operations for game XML](../../xml-patching/xpath-xml-patch-recipes/overview.md), [Understand when and in which order KSA loads mod XML](../../asset-xml/mod-xml-load-order/overview.md)
- [Drive a running game from scripts through a file-drop command bridge](../../../testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/overview.md), [Validate asset XML ids and paths before launching the game (offline)](../validate-asset-xml-references/overview.md)
- [Patch debug stepper](../../../../mods/kittenextensions/features/patch-debug-stepper.md), [Pre-game ImGui popup loop and fatal patch errors](../../../../mods/kittenextensions/features/pre-game-popup-loop.md)
