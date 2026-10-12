---
type: How-To
title: Get and set every config field by reflection, and script gameplay as composed commands
description: Grow a file bridge with name-based get and set over a settings object and with commands that feed scripted input frames into the mod's own gameplay state
category: testing-and-tooling
group: in-game-testing
topic: drive-a-running-game-through-a-file-bridge
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/file-command-bridge, mods/ksagolf/features/putting-session]
techniques: [file-ipc, reflection, starmap-lifecycle]
hooks: [starmap StarMapAfterOnFrame]
lifecycle: [frame:after-on-frame, frame:input]
game_types: [KSA.KittenEva, KSA.Universe]
tags: [testing, bridge, reflection, settings, scripted-input, kitten]
---

Outcome: once the [reference bridge](drive-a-running-game-through-a-file-bridge-1.md) exists, every setting the mod's panel exposes becomes
scriptable with two commands, `{"cmd":"set","name":"VerboseLog","value":true}` and `{"cmd":"get","name":"..."}`, and a gameplay action that
normally needs a mouse (a putting stroke) becomes a command carrying an array of per-frame mouse movements. Demonstrated by KSAGolf
([bridge feature](../../../../mods/ksagolf/features/file-command-bridge.md), [putting session](../../../../mods/ksagolf/features/putting-session.md)).
Prerequisites: StarMap and a bridge from article 1.

## Background: how the game does it

None of this is engine API. The only game fact involved is where *input* enters the game, because a scripted swing must enter the mod's state
at the same place the real mouse does. KSA polls events in `PrepareFrame` (`frame:input`: `Glfw.PollEvents`, `InputEvents.ApplyInputEvents`, key
dispatch), and KSAGolf's camera controller receives GLFW cursor positions in `OnCursorPos` and forwards them to the golf session
([src/KSAGolf/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/LevelHorizonController.cs#L117-L121)); the bridge runs later in the frame, at `frame:after-on-frame`, so it can only
supply input for the *next* frame's processing. That is why KSAGolf accumulates mouse movement into a field (`_dx`) that both the real
cursor path and the bridge write ([src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L347-L348), [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L429-L436)) and the stroke maths consumes once per frame
([src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L606-L618)). See [the frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Lifecycle and timing

- The commands run in `Bridge.Update` from `[StarMapAfterOnFrame]` (`frame:after-on-frame`), main thread.
- `set` writes a plain field on the mod's `Config` object, which the mod's UI and step code read on the next frame (`frame:draw-ui` or the step hook); nothing is
  marshalled because everything is main-thread.
- A `golf` command with a `swing` array installs a closure that feeds one entry per frame through `Golf.Nudge`, then waits `wait_s` seconds of player time before replying with the state.
  One entry per *frame* matters: the stroke maths averages the swing rate over a 60 ms window ([src/KSAGolf/Sim/PuttingStroke.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/PuttingStroke.cs)), so the scripted movement has to arrive spread over frames as a hand's would.

## Steps

1. **Keep settings as public instance fields of one class.** `TrySetField` finds a field case-insensitively, refuses `readonly` fields and converts only the types a setting has (bool, float, double, int, string, enum), refusing anything else with a reason:

   ```csharp
   // src/KSAGolf/Sim/BridgeCommand.cs L129-L175
       public static bool TrySetField(object target, string field, JsonElement value, out string trouble)
       {
           trouble = string.Empty;

           System.Reflection.FieldInfo? info = target.GetType().GetField(
               field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                      | System.Reflection.BindingFlags.IgnoreCase);

           if (info is null || info.IsInitOnly)
           {
               trouble = $"no settable field '{field}' on {target.GetType().Name}";
               return false;
           }

           try
           {
               Type type = info.FieldType;
               object? converted =
                   type == typeof(bool) ? value.ValueKind switch
                   {
                       JsonValueKind.True => true,
                       JsonValueKind.False => false,
                       JsonValueKind.Number => value.GetDouble() != 0.0,
                       _ => bool.Parse(value.GetString() ?? string.Empty),
                   }
                   : type == typeof(float) ? (float)ToDouble(value)
                   : type == typeof(double) ? ToDouble(value)
                   : type == typeof(int) ? (int)Math.Round(ToDouble(value))
                   : type == typeof(string) ? value.GetString()
                   : type.IsEnum ? Enum.Parse(type, value.GetString() ?? string.Empty, ignoreCase: true)
                   : null;

               if (converted is null)
               {
                   trouble = $"'{field}' is a {type.Name}, which cannot be set from here";
                   return false;
               }

               info.SetValue(target, converted);
               return true;
           }
           catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException)
           {
               trouble = $"'{field}' could not take {value.GetRawText()}: {e.Message}";
               return false;
           }
       }
   ```

2. **Read back as text** so a script can compare what the mod actually holds:

   ```csharp
   // src/KSAGolf/Sim/BridgeCommand.cs L178-L187
       public static string? FieldText(object target, string field)
       {
           System.Reflection.FieldInfo? info = target.GetType().GetField(
               field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                      | System.Reflection.BindingFlags.IgnoreCase);

           return info is null
                      ? null
                      : Convert.ToString(info.GetValue(target), CultureInfo.InvariantCulture);
       }
   ```

3. **Wire `set` and `get`** and re-apply any side effect the panel's checkbox performs next to the write; the field alone changes nothing for settings with side effects:

   ```csharp
   // src/KSAGolf/Ksa/Bridge.cs L252-L271
       private Reply Set(BridgeCommand command)
       {
           string field = command.String("name");
           if (!command.TryRaw("value", out JsonElement value)) return Failed("set needs a name and a value");

           if (!BridgeCommand.TrySetField(_config, field, value, out string trouble)) return Failed(trouble);

           // The panel's tick box does this beside the write; the field alone changes nothing.
           if (field == nameof(Config.VerboseLog)) Log.Threshold = _config.VerboseLog ? Log.Level.Debug : Log.Level.Info;

           return Done(new() { [field] = BridgeCommand.FieldText(_config, field) });
       }

       private Reply Get(BridgeCommand command)
       {
           string field = command.String("name");
           return BridgeCommand.FieldText(_config, field) is { } text
                      ? Done(new() { [field] = text })
                      : Failed($"no field '{field}' on Config");
       }
   ```

4. **Add composed gameplay commands that return a closure.** `golf` runs an action, then feeds the swing array one entry per frame, then waits, then replies with the session report. The reply is what a test asserts on (`struck`, `ball_distance_m`):

   ```csharp
   // src/KSAGolf/Ksa/Bridge.cs L300-L340
       // Putting without a hand on the mouse: "action" is drop, pickup or address (which also steps away),
       // and "swing" is the mouse's horizontal movement in pixels, one entry a frame, fed as the player's
       // would be. Answers with the state once the swing has been fed and "wait_s" more have passed.
       private Reply? GolfCommand(BridgeCommand command)
       {
           switch (command.String("action"))
           {
               case "drop": _golf.DropBall(); break;
               case "pickup": _golf.PickUpBall(); break;
               case "address": _golf.AddressOrStepAway(); break;
           }

           if (command.Has("look_yaw_deg")) _golf.LookBy(command.Number("look_yaw_deg", 0.0));

           List<double> swing = [];
           if (command.TryRaw("swing", out JsonElement moves) && moves.ValueKind == JsonValueKind.Array)
           {
               foreach (JsonElement move in moves.EnumerateArray())
               {
                   if (move.TryGetDouble(out double dx)) swing.Add(dx);
               }
           }

           double wait = command.Number("wait_s", 0.0);
           if (swing.Count == 0 && wait <= 0.0) return Done(_golf.Report());

           int next = 0;
           double waited = 0.0;
           _running = (dtPlayer, _) =>
           {
               if (next < swing.Count)
               {
                   _golf.Nudge(swing[next++]);
                   return null;
               }

               waited += dtPlayer;
               return waited >= wait ? Done(_golf.Report()) : null;
           };

           return null;
   ```

5. **Add commands that reuse the mod's own panels' state**, e.g. a `course` command whose `action` is `place`, `rotate`, `undo`, `save`, `load` and whose `i`, `j` aim the cursor at a grid cell ([src/KSAGolf/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Bridge.cs#L273-L298)), and kitten pose and prop commands that apply lists of bone turns ([src/KSAGolf/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Bridge.cs#L346-L399)). A command should end with `Done(state)` so every call is also a state read.

6. **Test the script.** A session is a numbered list of files:

   ```json
   {"id":"001","cmd":"load","save":"golf-green"}
   {"id":"002","cmd":"set","name":"FirstPerson","value":true}
   {"id":"003","cmd":"golf","action":"drop"}
   {"id":"004","cmd":"golf","action":"address"}
   {"id":"005","cmd":"golf","swing":[2,4,8,12,6,2],"wait_s":3}
   ```

   Adapted: command names and argument keys come from the bridge above (`set` takes `name` and `value`, `golf` takes `action`, `swing`, `wait_s`); the save name and the `FirstPerson` field are placeholders for your own. Each file runs only after the previous reply, so `005` sees the ball dropped and addressed by `003` and `004`.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Sim/BridgeCommand.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/BridgeCommand.cs) | Typed argument access, `TrySetField`, `FieldText` |
| [src/KSAGolf/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Bridge.cs) | `Set`, `Get`, `GolfCommand`, `CourseCommand`, `KittenPose`, `KittenProp` |
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs) | `Nudge` and `Report` used by the commands |

## Variations and gotchas

- **Public fields only.** Properties, private state and struct-valued fields are refused. Add a command, not a special case in `TrySetField`, for those.
- **`set` bypasses the UI's validation.** A panel may clamp a slider; the bridge writes whatever is sent. Validate in the setter or accept that tests can reach illegal states, which is sometimes the point.
- **Side effects.** A field whose change needs an action (the log threshold above) needs a line beside the write.
- **Scripted input is not real input.** `Nudge` bypasses ImGui and the engine's input routing. A test that passes with the bridge can still fail with a real mouse if the real path is blocked (an open ImGui window capturing the mouse, a popup holding the keyboard). See [Fly scripted scenarios unattended](../run-unattended-scenarios-and-report-a-verdict/overview.md) for a runner that closes popups.
- **Keep replies small and typed.** Replies are JSON; return numbers rounded for assertion, not whole objects.
- Steering a walking kitten from a bridge has no written topic yet; [Reach a kitten's avatar and skeleton](../../../kittens/characters/reach-a-kittens-avatar-and-skeleton/overview.md) covers the avatar access the pose commands use.

## See also

- [Overview](overview.md), [article 1: the reference bridge](drive-a-running-game-through-a-file-bridge-1.md), [article 3: lean variant](drive-a-running-game-through-a-file-bridge-3.md)
- [KSAGolf file command bridge](../../../../mods/ksagolf/features/file-command-bridge.md), [KSAGolf putting session](../../../../mods/ksagolf/features/putting-session.md)
