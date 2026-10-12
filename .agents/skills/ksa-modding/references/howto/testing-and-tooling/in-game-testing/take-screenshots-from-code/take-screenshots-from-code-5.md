---
type: How-To
title: A capture button that only sets a flag, with player bundles of frames, state and log tail
description: Offer screenshots to players by flagging the request in the UI pass and starting the capture on the next frame hook, saving a note and the log tail beside eight frames
category: testing-and-tooling
group: in-game-testing
topic: take-screenshots-from-code
approach: 5
difficulty: intermediate
prerequisites: [starmap, imgui]
demonstrated_by: [mods/ksarmory/features/file-command-bridge-and-mcp, mods/ksagolf/features/visual-testing-captures]
techniques: [reflection, imgui-window, starmap-lifecycle]
hooks: [starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:after-on-frame, frame:post-render]
game_types: [KSA.ScreenshotCapture, KSA.Program]
tags: [screenshots, ui, bug-report, capture-bundle, developer-only]
---

Outcome: a panel button "Capture for Claude" that, the moment a tester sees something wrong, saves eight consecutive frames, a `note.json` of the mod's state and settings and the last 300 lines of the mod log into
`bridge/out/player-<timestamp>/`. The button never starts a capture itself: it sets a static flag that the next frame hook consumes. Demonstrated by KSArmory
([file command bridge](../../../../mods/ksarmory/features/file-command-bridge-and-mcp.md)) and KSAGolf ([visual testing captures](../../../../mods/ksagolf/features/visual-testing-captures.md)).
Prerequisites: [article 1](take-screenshots-from-code-1.md) and an ImGui panel; shown only on a developer install.

## Background: how the game does it

`ScreenshotCapture` forces `Program.DrawUI = false` for the capture ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L226-L233)), and the UI hooks are inside `if (DrawUI)` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2269)). `Request` only arms the state machine; the next `PrepareFrame` then clears `DrawUI`, so from the following frame until the picture is taken none of the UI hooks run. A capture closure that needs a per-frame tick (poll for the file, count the warm-up) therefore cannot live in an ImGui hook, and the mod's rule is that a capture never starts inside the UI pass (its own comment on `_playerAsked`). The button sets a flag and the always-called frame postfix, `StarMapAfterOnFrame`, does the rest.
`ScreenshotCapture.Request` also drops a request while one is in progress ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L94-L98)), so repeated clicks are harmless.

## Lifecycle and timing

| Step | Hook |
|---|---|
| Button click sets `_playerAsked = true` | `frame:draw-ui` (UI pass) |
| `Bridge.Update` sees the flag, writes `note.json` and `log-tail.txt`, queues a `capture` command | `StarMapAfterOnFrame` (`frame:after-on-frame`) |
| Capture closure: request with warm-up, poll, move files | the following frames, as in article 1 |

The note is written *first*, so it reflects the state at the moment of the click, not after eight frames.

## Steps

1. **A static flag and one public method** so the UI needs no reference to the bridge instance:

   ```csharp
   // src/KSArmory/Ksa/Bridge.cs L76-L81
       // Asked for from the panel, and taken up by the next Update: the panel draws in the UI pass and
       // the bridge runs in the frame hook, and a capture must not start inside the UI pass.
       private static bool _playerAsked;

       /// <summary>Asks for a capture of what the player is looking at, with a note of the state.</summary>
       public static void RequestPlayerCapture() => _playerAsked = true;
   ```

2. **The button** (developer installs only; see [the bridge's marker-file gate](../drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-1.md)):

   ```csharp
   // src/KSArmory/Ksa/Ui/UiSession.cs L157-L167
       // What the player is looking at, kept for whoever is diagnosing it: frames, the state and the
       // log at that moment. A report from play in words is a guess at a cause; this is the evidence.
       private static void DrawCaptureForClaude()
       {
           if (ImGui.Button("Capture for Claude")) Bridge.RequestPlayerCapture();
           Tip("Saves eight frames of what is on screen with a note of the game's state and the end of "
               + "the log, beside the log in bridge/out. Nothing is sent anywhere. Press it the moment "
               + "you see something, and say so.");

           if (Bridge.LastPlayerCapture is { } last) ImGui.TextDisabled($"  saved {last}");
       }
   ```

3. **Consume the flag in the frame hook**, after any running command (a command in flight keeps the queue):

   ```csharp
   // src/KSArmory/Ksa/Bridge.cs L116-L121
               if (_playerAsked)
               {
                   _playerAsked = false;
                   StartPlayerCapture();
                   return;
               }
   ```

4. **Write the evidence, then the frames.** The note holds status, tunables and every public `Config` field; the log is read with `FileShare.ReadWrite` because the mod holds it open for writing; then a synthetic `capture` command (`frames: 8`, `every_frames: 1`) is parsed and started like any file command:

   ```csharp
   // src/KSArmory/Ksa/Bridge.cs L150-L195
       // Eight frames as quickly as KSA will take them, and a note written first, so it is the state at
       // the moment the player pressed rather than after the frames.
       private void StartPlayerCapture()
       {
           string id = "player-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
           string folder = Path.Combine(Outbox, id);
           Directory.CreateDirectory(folder);

           Dictionary<string, object?> note = new()
           {
               ["status"] = Status().Data,
               ["tunables"] = ShaderTunables.All.ToDictionary(t => t.Name, t => (object?)ShaderTunables.Value(t)),
               ["config"] = typeof(Config).GetFields(BindingFlags.Public | BindingFlags.Instance)
                                          .ToDictionary(f => f.Name, f => (object?)Convert.ToString(
                                                            f.GetValue(_config), CultureInfo.InvariantCulture)),
           };
           File.WriteAllText(Path.Combine(folder, "note.json"), JsonSerializer.Serialize(note, JsonOptions));

           try
           {
               // Shared, because the log is held open for writing the whole session.
               using FileStream stream = new(Path.Combine(Log.Folder, "KSArmory.log"), FileMode.Open,
                                             FileAccess.Read, FileShare.ReadWrite);
               using StreamReader reader = new(stream);
               string[] log = reader.ReadToEnd().Split('\n');
               File.WriteAllLines(Path.Combine(folder, "log-tail.txt"), log.Skip(Math.Max(0, log.Length - 300)));
           }
           catch (IOException)
           {
               // A note without the log's tail still says what the state was.
           }

           LastPlayerCapture = folder;
           Log.Info($"capture for Claude: {folder}");

           string request = JsonSerializer.Serialize(new Dictionary<string, object>
           {
               ["id"] = id,
               ["cmd"] = "capture",
               ["label"] = "player",
               ["frames"] = 8,
               ["every_frames"] = 1,
           });

           if (BridgeCommand.TryParse(request, out BridgeCommand? command, out _)) Start(command!);
       }
   ```

5. **Run it.** Press the button while the effect is on screen. Expect `capture for Claude: <folder>` in the log, and in the folder `note.json`, `log-tail.txt`, and `00-player.png` to `07-player.png` with manifests.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Bridge.cs) | Flag, `StartPlayerCapture`, `BeginCapture` |
| [src/KSArmory/Ksa/Ui/UiSession.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiSession.cs) | The button |
| [src/KSAGolf/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Bridge.cs) | The same pattern in KSAGolf (`RequestPlayerCapture`, `StartPlayerCapture`) |
| [docs/VISUAL-TESTING.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/VISUAL-TESTING.md) | Why this exists: a report from play in words is a guess; frames from the moment are evidence |

## Variations and gotchas

- **Frames from the moment.** Eight frames as fast as KSA takes them is the point: flicker that a paused series cannot show is only caught by frames from the moment somebody sees it (the mod's doc says so about the cloud-fade flicker it could not reproduce otherwise).
- **GPU split is a separate command.** The `cost` command reports the pass's GPU cost against the whole frame, from a pinned pose ([src/KSArmory/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Bridge.cs#L1109-L1123)); see [Measure what a mod costs per frame](../measure-and-budget-mod-performance/overview.md). It is not part of the player bundle.
- **Nothing leaves the machine.** The bundle is local; the mod's network-egress check enforces that the only egress is the player's own Send button.
- **Captures hide the HUD**, so a bug that is in the HUD is not in the frames; the note and the log tail carry it.
- **Privacy.** `Config` fields may include paths or names; review `note.json` before sharing.

## See also

- [Overview](overview.md), [article 1](take-screenshots-from-code-1.md)
- [Show alerts and popups in game](../../../ui/windows/show-alerts-and-popups-in-game/overview.md), [Build a settings window](../../../ui/windows/build-a-settings-window/overview.md)
- [KSArmory file command bridge and MCP](../../../../mods/ksarmory/features/file-command-bridge-and-mcp.md)
