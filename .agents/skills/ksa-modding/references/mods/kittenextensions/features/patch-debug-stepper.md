---
type: Mod Feature
title: Patch debug stepper
description: With debug = true on the KittenExtensionsContinued manifest entry, startup dumps the unpatched GameData document to root.xml and opens an ImGui debugger that steps patch actions, tests XPath and browses the live document
mod: kittenextensions
category: testing-and-tooling
techniques: [imgui-window, toml-config, xml-patch, modal-loop]
hooks: [harmony-prefix KSA.ModLibrary.PrepareAll]
lifecycle: [startup:asset-load]
game_types: [KSA.ModLibrary, KSA.Constants, KSA.Popup, KSA.Program, KSA.ModManifest, KSA.ModEntry]
tags: [debugging, imgui, xpath, xml-patch, manifest, tooling]
---

Writing XPath against a document assembled from dozens of files is error-prone, so KittenExtensions
ships a step debugger for its own patch run. The player (or mod author) adds `debug = true` to the
`KittenExtensionsContinued` entry in `manifest.toml`; on the next start the game stops during loading
and shows a full-window "KittenExtensions Patch Debug" popup. Patches can be run one action at a time,
every action shows what it targeted and what it read, an XPath tester evaluates expressions against
any node, and a tree shows the GameData document as it changes. The unpatched document is also
written to `root.xml` next to `manifest.toml`.[^kx-readme] [^kx-patcher-debug]

## How it works

1. **The flag.** `XmlPatcher.DebugEnabled()` parses `ModLibrary.LocalManifestPath` with Tomlet into
   private `ModManifestToml` and `ModEntryToml` classes that add a `debug` key the game's own
   `ModEntry` does not have, picks the entry whose `id` is `KittenExtensionsContinued` and that is
   `enabled`, and caches the answer for the process; any exception means "off"
   ([KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L193-L225)).[^kx-patcher]
2. **Dump.** With the flag set, `OnPrepare` loads an `XmlDocument` from
   `new XPDocReader(Domain.Doc.LatestRoot)` before any patch has run and saves it as
   `Path.Combine(Constants.DocumentsFolderPath, "root.xml")`
   ([KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L64-L68)). `ModLibrary.LocalManifestPath` is
   `manifest.toml` in that same folder ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L180),
   [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)).[^ksa-modlibrary] [^ksa-constants]
3. **Block in a popup.** It then creates a `PatchDebugPopup` inside a `PopupTask` and pumps frames
   until the popup closes ([KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L69-L72)); the frame loop is
   described in [Pre-game ImGui popup loop](pre-game-popup-loop.md). The constructor executes the
   first action with `Executor.Step()` ([KittenExtensions/Patch/Patcher.Debug.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.Debug.cs#L47-L55)).
4. **Controls** ([KittenExtensions/Patch/Patcher.Debug.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.Debug.cs#L111-L133)): "Run All Patches"
   (`StepToEnd`), "Step", "Step Over" and "Step Out" while actions remain, all disabled once the
   executor reports an error; "Start Game" appears when `Executor.Done` and closes the popup.
5. **Action Tree tab.** Every `PatchAction` is a tree node labelled with its type, its position for
   positional actions and the target's path relative to its context. Failed actions are red and the
   next action is green ([KittenExtensions/Patch/Patcher.Debug.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.Debug.cs#L269-L309)). Selecting one
   shows Context, Patch, Position, Target, Target XPath, the first 100 target results, Source, Source
   XPath and the first 100 source results; clicking a node jumps the document view to it
   ([KittenExtensions/Patch/Patcher.Debug.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.Debug.cs#L311-L442)).
6. **XPath Tester tab.** The input is evaluated with `XPath.Exec(expression, context)` on every edit;
   results show as booleans, numbers, strings or nodes, a node-set count is printed, and each node row
   has an inline "Set Context" button ([KittenExtensions/Patch/Patcher.Debug.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.Debug.cs#L444-L538)).
7. **Document view.** The right pane draws the live document recursively; an element whose only child
   is a short single-line text or comment is drawn on one line, comments are green
   ([KittenExtensions/Patch/Patcher.Debug.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.Debug.cs#L540-L599)). "Follow" toggles (Last or Next,
   Target or Patch) keep the view on the current action; after an error it follows the failing patch
   element ([KittenExtensions/Patch/Patcher.Debug.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.Debug.cs#L63-L88)).
8. **Finish.** When the popup closes, `OnPrepare` writes the [patch session log](patch-session-log.md)
   summary and startup continues with the patched document.

The trees are drawn with the custom [ImGui tree view widget](imgui-tree-view.md); one-line labels are
assembled without allocations by the `LineBuilder` ref struct over a 64K `char` buffer
([KittenExtensions/Patch/Utils.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Utils.cs#L15-L71)).[^kx-utils]

## Lifecycle and timing

`startup:asset-load`, inside the Harmony prefix on `ModLibrary.PrepareAll`, after the GameData
document is built and before the first patch action beyond the one the constructor steps. Startup is
paused for as long as the popup is open: the `Program` constructor is blocked inside `PrepareAll`
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899)), so no `Mod` object, asset bundle or universe exists, and
no `StarMapImmediateLoad` has run.[^ksa-program] The renderer and ImGui context already exist,
which is why an interactive tool is possible this early. Everything runs on the main thread.

## Key code

| Path | Role |
|---|---|
| [KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L56-L83) | `OnPrepare`: debug branch, `root.xml`, modal loop |
| [KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L193-L225) | `DebugEnabled` and its private manifest classes |
| [KittenExtensions/Patch/Patcher.Debug.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.Debug.cs#L16-L630) | `PatchDebugPopup`: controls, tabs, document view |
| [KittenExtensions/Patch/Utils.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Utils.cs#L15-L264) | `LineBuilder`: allocation-free node and path labels |

## Snippets

```toml
# README.md L78-L83
# Documents/My Games/Kitten Space Agency/manifest.toml

[[mods]]
id = "KittenExtensionsContinued"
enabled = true
debug = true
```

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

```csharp
// KittenExtensions/Patch/Patcher.Debug.cs L111-L133
      const string titleText = "KittenExtensions Patch Debug";
      ImGuiEx.CenterTextCursor(ImGuiEx.AvailableSpace(), titleText);
      ImGui.Text(titleText);

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

## Game APIs touched

| Member | What it is for |
|---|---|
| `ModLibrary.LocalManifestPath` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L180)) | Where the `debug` flag is read |
| `Constants.DocumentsFolderPath` ([current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)) | Folder for `root.xml` and `manifest.toml`; StarMap's `-InstancePath` override patches this getter, so the dump follows it ([StarMap.Core/Patches/DocumentsPathPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/DocumentsPathPatches.cs#L12-L31)) |
| `KSA.Popup` | Base class of the debugger window, drawn by `Popup.DrawAll` |
| `Program.GetWindow` | Sizes the popup to the window minus a 50 px margin |

## Pitfalls and version notes

- **The game erases the flag.** `ModManifest.Save` writes only `id` and `enabled` for each entry
  ([current/decomp/KSA/ModManifest.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModManifest.cs#L27-L48)), and the game saves the manifest after the
  new-mod dialog ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L887-L898)) and inside `PrepareManifest` when it
  restores missing Core entries or drops entries whose folder is gone
  ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L277-L323)); StarMap calls `PrepareManifest` before the game's
  `Main`. Applying changes in the in-game settings also saves the manifest
  ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L1403-L1410),
  [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L2362-L2367)). In a session that discovers a
  new mod, the flag is gone before KittenExtensions reads it. Re-add it after installing mods or
  changing settings.[^ksa-modmanifest] [^ksa-modentry] [^ksa-gamesettings]
- **Use the new id.** The README's install section says "the KittenExtensions mod entry"
  ([README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L30)), but the code only matches `KittenExtensionsContinued`.
- **Read once.** The result is cached in a static field; change the manifest and restart.
- **`root.xml` is the input, not the output.** It is written before any patch runs. To see the
  patched result, step to the end and inspect the document view.
- **No way past an error.** After an error the step buttons are disabled and "Start Game" only
  appears once the executor reports `Done` (whether it does after an error is internal to XPathPatch,
  not visible here). Closing the window exits the process.
- The debugger only shows KittenExtensions' own patch run; it does not debug how the game later
  deserializes the result.

## See also

- [Combined GameData XML document](gamedata-document.md)
- [XML patch files and XPathPatch operations](xpath-patch-operations.md)
- [Pre-game ImGui popup loop and fatal patch errors](pre-game-popup-loop.md)
- [ImGui tree view widget](imgui-tree-view.md)
- [Patch session log](patch-session-log.md)
- [Debug a failing XML patch with the KittenExtensions step debugger](../../../howto/assets/validation/debug-xml-patches/debug-xml-patches-1.md) - how-to article demonstrating this feature
- [Gate developer features on a custom key in the player's manifest.toml entry](../../../howto/testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-4.md) - how-to article demonstrating this feature

[^kx-readme]: KittenExtensions README.md
[^kx-patcher-debug]: KittenExtensions/Patch/Patcher.Debug.cs
[^kx-patcher]: KittenExtensions/Patch/Patcher.cs
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-constants]: KSA.Constants (decompiled, build 2026.10.10.5554)
[^kx-utils]: KittenExtensions/Patch/Utils.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-modmanifest]: KSA.ModManifest (decompiled, build 2026.10.10.5554)
[^ksa-modentry]: KSA.ModEntry (decompiled, build 2026.10.10.5554)
[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
