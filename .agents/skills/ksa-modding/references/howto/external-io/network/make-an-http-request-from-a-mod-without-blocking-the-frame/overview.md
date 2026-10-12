---
type: Game Feature
title: Make an HTTP request from a mod and add an in-game bug report form
description: Send an HTTP request from a visible click on a background task, poll plain status fields from the draw each frame and handle load-context quirks of the game runtime
category: external-io
group: network
topic: make-an-http-request-from-a-mod-without-blocking-the-frame
demonstrated_by: [mods/ksarmory/features/in-game-bug-reports]
lifecycle: [frame:draw-ui, thread:background]
game_types: [KSA.Program, KSA.Vehicle]
tags: [http, httpclient, bug-report, imgui, task-run]
---

Sometimes a mod must reach out: a bug report, a version check the player asked for, a lookup on a service. The game provides no HTTP helper and no async bridge to the frame loop, so the pattern is the same as for any blocking work: start the request from a visible click on a thread-pool task, write the outcome into plain fields, and let the next draw pass read them. This topic covers that pattern, a game-specific runtime quirk that makes the obvious status check throw, and the policy side (the network is only touched when the player clicks).

## Engine background

| Type | File | Why it matters |
|---|---|---|
| `Program.OnFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2334) | One main-thread frame; the draw code must return quickly |
| `Program.OnDrawUiViewports` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3162-L3176) | The StarMap `StarMapAfterGui` postfix target; skipped when `DrawUI` is false |
| `Vehicle` assembly version | the loaded `KSA.Vehicle` assembly | KSArmory reads the running game build from its assembly version |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [FeedbackClient with polled status fields and reflection for StatusCode](make-an-http-request-from-a-mod-without-blocking-the-frame.md) | ksarmory | `background-thread` | `frame:draw-ui`, `thread:background` | Simple and race-free for one in-flight request; needs a service to hold credentials, and the reflection workaround is a hypothesis-driven patch |

## See also

- [Host HTTP and MQTT servers in the game process](../host-http-and-mqtt-servers-in-the-game-process/overview.md): the opposite direction
- [Check the game version](../../../getting-started/lifecycle/check-the-game-version/overview.md), [Build a settings window](../../../ui/windows/build-a-settings-window/overview.md)
- [Host a terminal with pty sessions and frame handoff](../../terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/overview.md)
- [Drive a running game from scripts through a file-drop command bridge](../../../testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/overview.md) - related topic

