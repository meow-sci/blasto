---
type: How-To
title: A request file, a state machine, wall-clock and sim-time budgets and tagged SCENARIO lines
description: Build a developer-only scenario runner that reads a request file once at launch, loads a save, closes engine popups, enforces budgets and writes a PASS, FAIL or TIMEOUT line that a shell harness maps to an exit code
category: testing-and-tooling
group: in-game-testing
topic: run-unattended-scenarios-and-report-a-verdict
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/unattended-scenario-runner]
techniques: [starmap-lifecycle, reflection]
hooks:
  - starmap StarMapAllModsLoaded
  - starmap StarMapAfterGui
  - starmap StarMapAfterOnFrame
  - reflection KSA.Popup.Popups
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:after-on-frame, startup:game-loaded]
game_types: [KSA.GameSaves, KSA.Popup, KSA.Program, KSA.Universe]
tags: [testing, scenario, unattended, state-machine, budgets, popups, exit-code]
---

Outcome: `./tools/scenario.sh head-on` builds and deploys the mod, launches the game, waits, and exits 0 on `PASS` or non-zero on `FAIL`, `TIMEOUT` or a game that exited, while the game does everything itself: load a named save, wait for the world,
spawn targets, drive the engagement, score it. Demonstrated by KSArmory ([unattended scenario runner](../../../../mods/ksarmory/features/unattended-scenario-runner.md)). Prerequisites: StarMap, a developer-install gate
([bridge article 1](../drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-1.md)), a Bash environment that can start the game and `tasklist.exe`/`taskkill.exe` (the author launches a Windows game from WSL).
The scenario content is KSArmory-specific; the pattern is general: request in, tagged log lines out, verdict in the exit code.

## Background: how the game does it

The runner needs three engine behaviours.

- **Loading a save from code.** `GameSaves.LoadSaveGame(string)` is the same method the console `load` action calls ([current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L283-L302)); it logs `save '<name>' not found` for an unknown name instead of throwing.
- **Popups hold the throttle.** `Popup` objects register themselves in a private static list `Popups` ([current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L32)) and draw modal; while one is open the UI owns the keyboard, and the mod found the controlled craft keeps its full throttle through a staging that needed it lowered ([src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5341-L5351)).
  `Popup.AnyOpen` is public ([current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L44)); the `Active` flag is what the OKAY button clears ([current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L34-L38)).
- **Two clocks.** `dtPlayer` keeps running through a pause and ignores timewarp; the simulated step comes from `Universe.GetLastSimStep()` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2381-L2384)).

## Lifecycle and timing

| Phase | Where |
|---|---|
| Read and delete the request, snapshot settings | `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`), `Begin(ScenarioRunner.Requested())`, only if `Build.Developer` ([src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L217-L221)) |
| Per-frame update | At the tail of the mod's per-frame step, after rounds, fire control and effects, so the scenario reads the state this frame produced: normally the `StarMapAfterGui` postfix (`frame:draw-ui`) and, on frames with no UI, the `OnFrameCelestials` prefix or the `StarMapAfterOnFrame` fallback ([src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L972-L977)) |
| Load the save | In `Phase.LoadingSave`, three seconds after start (`startup:game-loaded` follows when the scene exists) |
| Verdict | `Finish` logs `SCENARIO <name>: PASS ...` then `END` |

Commands that move the world (load, speed, staging) are issued from a mod hook after the frame's physics step was dispatched, so they take effect from the next `PrepareFrame`. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Send the request in a file, not an environment variable.** The game is a Windows process launched from WSL and the environment does not survive that; the mod's own comment records that an environment-variable option was tried and never arrived. `scenario.sh` writes four lines (request and save, arm spec, arm phase, space-separated options), always four, so lines can be addressed by position:

   ```bash
   # tools/scenario.sh L209-L227
   # Consumed by the mod as it reads it, so a later launch cannot silently re-run this.
   mkdir -p "$USER_DIR/Logs"
   # Four lines, always, even when empty. The arm spec gets one of its own because it separates arms
   # with the same "|" that separates the request from the save, and the options line is last because a
   # line that is only sometimes written cannot be addressed by position.
   #
   # A file rather than the environment: the game is a Windows process launched from WSL and the
   # environment does not survive that. KSARMORY_SCENARIO_KEEPSTAGES was tried as one and the mod never
   # saw it -- 100 disposals in a run that was meant to keep every stage.
   {
       printf '%s|%s\n' "$SCENARIO" "$SAVE"
       printf '%s\n%s\n' "$ARMS" "$ARM_PHASE"
       printf '%s %s %s %s %s %s %s %s %s %s %s %s %s %s %s %s %s %s\n' "${KSARMORY_SCENARIO_KEEPSTAGES:+keepstages}" "${KSARMORY_SCENARIO_TRACE:+trace}" \
           "${KSARMORY_SCENARIO_VERBOSE:+verbose}" "${KSARMORY_SCENARIO_CHASE:+chase}" "${KSARMORY_SCENARIO_CLOUDS:+clouds}" "${KSARMORY_SCENARIO_NOSHADER:+noshader}" "${KSARMORY_SCENARIO_TWOCLOUDS:+twoclouds=$KSARMORY_SCENARIO_TWOCLOUDS}" "${KSARMORY_SCENARIO_CLOUDWARP:+cloudwarp=$KSARMORY_SCENARIO_CLOUDWARP}" "${KSARMORY_SCENARIO_WATCHELEV:+watchelev=$KSARMORY_SCENARIO_WATCHELEV}" "${KSARMORY_SCENARIO_STILLAT:+stillat=$KSARMORY_SCENARIO_STILLAT}" "${KSARMORY_SCENARIO_BLACKOUT:+blackout=$KSARMORY_SCENARIO_BLACKOUT}" "${KSARMORY_SCENARIO_NOBLACKOUT:+noblackout}" \
           "${KSARMORY_SCENARIO_SPEEDS:+speeds=$KSARMORY_SCENARIO_SPEEDS}" \
           "${KSARMORY_SCENARIO_SITE:+site=$KSARMORY_SCENARIO_SITE}" \
           "${KSARMORY_SCENARIO_FUSE:+fuse=$KSARMORY_SCENARIO_FUSE}" "${KSARMORY_SCENARIO_CHUTE:+chute=$KSARMORY_SCENARIO_CHUTE}" \
           "${KSARMORY_SCENARIO_SELECTOTHER:+selectother}" "${KSARMORY_SCENARIO_HOLDKEYS:+holdkeys}"
   } > "$USER_DIR/Logs/scenario.txt"
   ```

2. **Take it once.** The mod reads and deletes the file, so a relaunch cannot repeat a scenario; it trims each line separately because CRLF can arrive from WSL:

   ```csharp
   // src/KSArmory/Ksa/ScenarioRunner.cs L376-L392
       public static string? Requested()
       {
           try
           {
               string path = Path.Combine(Log.Folder, "scenario.txt");
               if (!File.Exists(path)) return null;

               string text = File.ReadAllText(path).TrimEnd();
               File.Delete(path);

               return string.IsNullOrWhiteSpace(text) ? null : text;
           }
           catch
           {
               return null;
           }
       }
   ```

3. **Begin: snapshot, parse, override.** Everything the scenario overrides (settings, log threshold) is snapshotted so `Finish` can put it back and a game kept open afterwards is a normal session again:

   ```csharp
   // src/KSArmory/Ksa/ScenarioRunner.cs L402-L420
       public void Begin(string? request)
       {
           if (_phase != Phase.Idle || string.IsNullOrWhiteSpace(request)) return;

           // Everything below overrides the player's own settings for the flight. A game kept open
           // after END is somebody's session again, and one left with the shader pass off draws every
           // tracer as a debug line.
           _before = _config.Snapshot();
           _thresholdBefore = Log.Threshold;

           // The request is the first line; the arm spec and its phase are the two after it, and a
           // one-line file is still the whole of the single-arm case.
           // Trimmed per line rather than over the whole text: the file is written from WSL and read
           // by a Windows process, so a line ending can arrive as CRLF and a stray carriage return on
           // the request would go into the save name.
           string[] lines = request.Split('\n');
           request = lines[0].Trim();
           _armSpec = lines.Length > 1 ? lines[1].Trim() : string.Empty;
           _armPhase = lines.Length > 2 && int.TryParse(lines[2].Trim(), out int phase) ? phase : 0;
   ```

4. **Run a phase machine.** `Idle -> LoadingSave -> WaitingForWorld -> Arming -> Engaging/Flying/Dropping -> Settling -> Done`. Each `Update` starts by ticking both clocks, closing popups, and checking the budgets; the wall-clock budget does not apply while `Settling`, which has its own bound:

   ```csharp
   // src/KSArmory/Ksa/ScenarioRunner.cs L823-L860
       public void Update(WeaponSystems roster, IcbmComputers? icbms, double simStep, double playerStep)
       {
           if (_phase is Phase.Idle or Phase.Done) return;
           if (!double.IsFinite(playerStep) || playerStep <= 0.0) return;

           // The UI holding the keyboard is what an open popup does to the rocket being flown, without the popup.
           if (_holdKeys) KsaWorld.HoldTheKeyboard();

           // Nobody is there to click a popup away, and an open one holds the controlled rocket's throttle.
           IReadOnlyList<string> closed = KsaWorld.CloseEnginePopups();
           if (closed.Count > 0)
           {
               Report($"{_name}: closed KSA's {string.Join(", ", closed)}, which holds the throttle of the craft "
                      + "being flown while it is open");
           }

           double dt = double.IsFinite(simStep) && simStep > 0.0 ? simStep : 0.0;

           _elapsed += playerStep;
           _simElapsed += dt;

           // Not while settling. Every flight has already been judged by then, so a run that spent its
           // budget flying and then crossed it waiting for a trace would be reported TIMEOUT with a
           // full set of verdicts in hand. The settle carries its own bound instead.
           if (_phase != Phase.Settling)
           {
               if (_elapsed > _budget)
               {
                   Finish($"TIMEOUT after {_elapsed:F0} s of wall clock -- {Stuck()}");
                   return;
               }

               if (_isBallistic && _simElapsed > BallisticSimBudgetSeconds)
               {
                   Finish($"TIMEOUT after {_simElapsed / 60.0:F0} minutes of world time -- {Stuck()}");
                   return;
               }
           }
   ```

5. **Load the save after a beat** and move on to waiting for the world. A failure to load is a `FAIL` with the exception text; an unknown name shows up later as the stuck state in `TIMEOUT`:

   ```csharp
   // src/KSArmory/Ksa/ScenarioRunner.cs L870-L894
               case Phase.LoadingSave:
                   // A beat after load: asking the game to swap scenes while it is still building
                   // the first one is not a case anything here can recover from.
                   if (_elapsed < 3.0) return;

                   if (_save.Length > 0)
                   {
                       _drop?.NoteTheWorldBeforeTheLoad();

                       try
                       {
                           GameSaves.LoadSaveGame(_save);
                           Report($"{_name}: asked for save '{_save}'");
                       }
                       catch (Exception e)
                       {
                           Finish($"FAIL could not load '{_save}': {e.Message}");
                           return;
                       }
                   }

                   _phase = _isBallistic ? Phase.Flying
                            : _drop is not null ? Phase.Dropping
                            : Phase.WaitingForWorld;
                   return;
   ```

6. **Wait for the world with a described stall.** While no craft is flown or no system crewed, repeat what is missing every ten seconds, so a `TIMEOUT` says where it was stuck:

   ```csharp
   // src/KSArmory/Ksa/ScenarioRunner.cs L910-L930
               case Phase.WaitingForWorld:
                   if (!KsaWorld.InFlight || entry is null)
                   {
                       // Every few seconds, because "TIMEOUT in WaitingForWorld" says which state it
                       // died in and nothing about which of the two things it was missing.
                       if (_elapsed - _lastComplaint > 10.0)
                       {
                           _lastComplaint = _elapsed;
                           Report($"{_name}: waiting -- "
                                  + (KsaWorld.InFlight ? "in flight" : "NO CRAFT IN FLIGHT")
                                  + ", " + (entry is null ? "NO SYSTEM CREWED" : "system crewed"));
                       }
                       return;
                   }

                   if (_elapsed < SettleSeconds) return;

                   Report($"{_name}: crewed {KsaWorld.DisplayName(entry.Weapon.Platform!)} "
                          + $"with {entry.Weapon.Profile.DisplayName}");
                   _phase = Phase.Arming;
                   return;
   ```

7. **Close engine popups by reflection** through the private list, setting the public `Active` flag; warn once if the field moved:

   ```csharp
   // src/KSArmory/Ksa/KsaWorld.cs L5352-L5395
       public static IReadOnlyList<string> CloseEnginePopups()
       {
           if (!KSA.Popup.AnyOpen) return [];
           if (EnginePopups() is not { } popups) return [];

           List<string> closed = [];
           foreach (KSA.Popup popup in popups)
           {
               if (!popup.Active) continue;
               popup.Active = false;
               closed.Add(popup.GetType().Name);
           }

           return closed;
       }

       private static FieldInfo? _popupListField;
       private static bool _lookedForPopupList;

       private static List<KSA.Popup>? EnginePopups()
       {
           if (!_lookedForPopupList)
           {
               _lookedForPopupList = true;
               _popupListField = typeof(KSA.Popup).GetField("Popups", BindingFlags.NonPublic | BindingFlags.Static);

               if (_popupListField?.FieldType != typeof(List<KSA.Popup>))
               {
                   Log.Warn("Popup.Popups has moved - a scenario cannot close KSA's popups, and one left open "
                            + "holds the throttle of the craft being flown");
                   _popupListField = null;
               }
           }

           try
           {
               return _popupListField?.GetValue(null) as List<KSA.Popup>;
           }
           catch
           {
               return null;
           }
       }

   ```

8. **Finish and tag every line.** `Finish` releases its flights, restores config and log threshold, and reports; `Report` prefixes `SCENARIO`:

   ```csharp
   // src/KSArmory/Ksa/ScenarioRunner.cs L1116-L1129
       private void Finish(string outcome)
       {
           _phase = Phase.Done;
           for (int i = 0; i < _flights.Count; i++) _flights[i].Release();
           _drop?.Release();
           _gunnery?.Release();

           if (_before is not null) _config.Restore(_before);
           Log.Threshold = _thresholdBefore;
           Report($"{_name}: {outcome}");
           Report($"{_name}: END");
       }

       private static void Report(string line) => Log.Info($"{Tag} {line}");
   ```

9. **The harness.** `scenario.sh` snapshots and edits `settings.toml` (so the game boots without the start dialog, optionally into a chosen craft and system), closes a game left running, deploys with retries for a locked DLL, launches, tails the log for `SCENARIO` lines, maps them to a verdict, and restores the player's settings on exit:

   ```bash
   # tools/scenario.sh L233-L255
   SETTINGS="$USER_DIR/settings.toml"
   DIALOG_WAS=""
   CRAFT_WAS=""
   if [[ -f "$SETTINGS" ]]; then
       DIALOG_WAS="$(grep -oE '^selectSystemOnStart = (true|false)' "$SETTINGS" | head -1 || true)"
       CRAFT_WAS="$(grep -oE '^startVehicle = ".*"' "$SETTINGS" | head -1 || true)"

       sed -i 's/^selectSystemOnStart = true/selectSystemOnStart = false/' "$SETTINGS"

       # Skipping the dialog does not choose a system -- the game loads lastSystemId, which is
       # whatever was picked last and defaults to the full 25-body "Sol". Every celestial is work the
       # engine does per frame and work the mod does per ground lookup, and a ballistic shot at Earth
       # needs Earth. Defaulted to SolLite for mirv above; KSARMORY_SCENARIO_SYSTEM overrides.
       #
       # Restored on exit with everything else here: it is the player's setting, not the harness's.
       SYSTEM_WAS="$(grep -oE '^lastSystemId = ".*"' "$SETTINGS" | head -1 || true)"

       if [[ -n "${SYSTEM:-}" ]]; then
           sed -i "s|^lastSystemId = \".*\"|lastSystemId = \"$SYSTEM\"|" "$SETTINGS"
       fi

       [[ -n "$CRAFT" ]] && sed -i "s|^startVehicle = \".*\"|startVehicle = \"$CRAFT\"|" "$SETTINGS"
   fi
   ```
   ```bash
   # tools/scenario.sh L341-L367
       COUNT=$(grep -cF SCENARIO "$LOG" 2>/dev/null || true)
       (( ${COUNT:-0} < PRINTED )) && PRINTED=0
       mapfile -t FRESH < <(grep -F SCENARIO "$LOG" 2>/dev/null | tail -n +$(( PRINTED + 1 )))
       PRINTED=$(( PRINTED + ${#FRESH[@]} ))

       for line in "${FRESH[@]}"; do
           echo "   ${line#*SCENARIO }"

           case "$line" in
               *CAPTURE*)
                   # Off unless asked for. screenshot.sh grabs the whole primary screen, not the game
                   # window, so an unattended run photographs whatever happens to be in front.
                   # Verifying by eye is the operator's job; this harness is for the things a log
                   # can answer.
                   if (( SHOTS_ON )); then
                       sleep 1
                       "$REPO_ROOT/tools/screenshot.sh" >/dev/null 2>&1 || true
                       echo "   -> screenshot in screenshots/"
                   fi
                   ;;
               *PASS*)    VERDICT=PASS ;;
               *FAIL*)    VERDICT=FAIL ;;
               *TIMEOUT*) VERDICT=TIMEOUT ;;
           esac
       done

       [[ -n "$VERDICT" ]] && break
   ```
   ```bash
   # tools/scenario.sh L389-L398
   echo
   case "$VERDICT" in
       PASS) echo "scenario '$SCENARIO': PASS" ;;
       EXITED) echo "scenario '$SCENARIO': FAIL -- the game exited before a verdict" >&2
             exit 1 ;;
       "")   echo "scenario '$SCENARIO': no verdict within $(( DEADLINE_SECONDS / 60 )) minutes" >&2
             echo "  the game may still be on StarMap's configuration dialog -- it needs START KSA clicked" >&2
             exit 1 ;;
       *)    echo "scenario '$SCENARIO': $VERDICT" >&2; exit 1 ;;
   esac
   ```

   It also counts a game that exits (three consecutive process listings without it, after it was seen running) as `EXITED`, which reports FAIL instead of waiting for the deadline ([tools/scenario.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/scenario.sh#L369-L379)).

10. **Run and read.** Expect `== deploying`, `== launching, scenario 'head-on', save 'rocket missile'`, then indented `SCENARIO` lines, and finally `scenario 'head-on': PASS` with exit code 0. A `PASS` is not a clean run: read the lines for closed popups, warnings and the trace, as the repo's `verify-in-game` skill says ([.claude/skills/verify-in-game/SKILL.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.claude/skills/verify-in-game/SKILL.md)).

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/ScenarioRunner.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ScenarioRunner.cs) | Request file, options, phase machine, budgets, `Finish` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs) | `CloseEnginePopups`, `HoldTheKeyboard` |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs) | Construction gate and per-frame call |
| [tools/scenario.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/scenario.sh) | Harness: request, deploy, launch, grep, exit code |
| [tools/make-rocket.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/make-rocket.py), [tools/sky-matrix.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/sky-matrix.sh) | Inputs and batches built on top (a save generator, a night-sky burst matrix) |

## Variations and gotchas

- **Appearance is not judged.** The runner scores numbers; pictures arrive through `CAPTURE` markers and, optionally, a whole-screen grab (`--shots`, which uses `tools/screenshot.sh` and refuses unless the game is in front). Use [screenshots from code](../take-screenshots-from-code/overview.md) for a real capture.
- **Popups steal the throttle.** The update notice is one; the runner closes whatever is open each frame, and `holdkeys` reproduces the held-keyboard state deliberately by calling `ImGui.SetNextFrameWantCaptureKeyboard(true)` ([src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5298)).
- **Quote multi-target requests.** `;` separates targets in a `mirv:` request and is also the shell's separator.
- **The mod cannot put a rocket on the pad** in the ballistic scenario; supply the craft through `settings.toml`'s `startVehicle` (`KSARMORY_SCENARIO_CRAFT`) or a save.
- **Pick budgets from the work.** Wall-clock and simulated-time budgets are separate: the simulated one catches a stuck flight, the wall-clock one a slow machine. A budget an arm can fail on for being slower than the baseline measures the budget.
- **Windows-only harness.** `tasklist.exe`, `taskkill.exe` and the WSL path handling are baked in; a native Linux harness needs its own process checks.
- **Game updates.** `GameSaves.LoadSaveGame` and `Popup.AnyOpen` are public; `Popup.Popups` is private and checked at first use.

## See also

- [Overview](overview.md), [article 2](run-unattended-scenarios-and-report-a-verdict-2.md), [article 3](run-unattended-scenarios-and-report-a-verdict-3.md)
- [Drive a running game through a file bridge](../drive-a-running-game-through-a-file-bridge/overview.md), [Show alerts and popups in game](../../../ui/windows/show-alerts-and-popups-in-game/overview.md)
- [KSArmory unattended scenario runner](../../../../mods/ksarmory/features/unattended-scenario-runner.md)
