---
type: Mod Feature
title: File bug reports from an ImGui form through a guarded issue service
description: An ImGui report window posts a summary, details and the tail of the mod's own log to a small self-hosted service that files a GitHub issue, sending from a background task only when the player clicks Send and only when the running KSA build matches the one the mod was built for
mod: ksarmory
category: external-io
techniques: [imgui-window, background-thread, http-client, reflection]
hooks: [starmap StarMapAfterGui, thread Task.Run (FeedbackClient.Send)]
lifecycle: [frame:draw-ui, thread:background]
game_types: [KSA.Vehicle]
tags: [feedback, bug-report, http, imgui, github-issues, abuse-protection, privacy]
---

The KSArmory panel ends in two buttons, **Report bug** and **Feedback**. They open an ImGui window
where the player types a summary and details, optionally attaches the tail of the mod's own log,
and clicks **Send**. The mod posts JSON to `https://api.ksarmory.com/feedback` on a background
task; a small ASP.NET service holding the GitHub token sanitises the text, rate-limits, scores it
with a local toxicity model and files an issue, answering with its URL. The mod holds no
credential, never touches the network unless the player clicks Send (a CI check enforces that),
and hides the buttons when the KSA build it is running on is not the one it was built for.

## How it works

### In the mod

| Step | Code |
|---|---|
| The footer of the main panel draws the pane toggles and, only if `ReportDraft.GameIsSupported(Build.KsaBuild, Build.KsaRunning)`, the two buttons right-aligned with `CalcTextSize` arithmetic | [src/KSArmory/Ksa/Ui/UiReport.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiReport.cs#L26-L66)[^ui-report] |
| `DrawReportWindow` opens `ImGui.Begin("...###KSArmoryReport", ref _reportOpen)` with kind switch, multiline inputs, log attachment line ("last N KB of M KB") and Send; Send is replaced by the reason text while the draft is invalid | [src/KSArmory/Ksa/Ui/UiReport.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiReport.cs#L84-L218)[^ui-report] |
| Length limits mirrored from the service (`MaxSummary` 120, `MaxDetail` 4,000, `MaxLog` 12,000, `MinSummary` 8), `Problem()` for the greyed reason, `Tail()` for the log, build comparison with a trailing `.0` normalised | [src/KSArmory/Sim/ReportDraft.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ReportDraft.cs)[^report-draft] |
| The log is read with `FileShare.ReadWrite` because the mod has it open for appending | [src/KSArmory/Ksa/Ui/UiReport.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiReport.cs#L243-L262)[^ui-report] |
| `FeedbackClient.Send` sets `Sending`, builds a wire record (kind, summary, detail, log, mod version, running KSA version, platform) and `Task.Run`s `PostAsJsonAsync` with a 45 s timeout; the panel polls `Sending`/`Status`/`IssueUrl` each frame | [src/KSArmory/Ksa/FeedbackClient.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/FeedbackClient.cs#L44-L90)[^feedback-client] |
| Status codes are read by reflection (`GetProperty("StatusCode")`) because the direct property call throws `MissingMethodException` in game; 202, 426 (too old, with the version needed), 400, 422, 429, 503 each get their own message | [src/KSArmory/Ksa/FeedbackClient.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/FeedbackClient.cs#L92-L160)[^feedback-client][^modding-notes] |

### In the service

[services/feedback/README.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/services/feedback/README.md)
documents the guards:[^service-readme]

- every field rendered inside a code fence with backticks replaced, invisible and bidi characters
  stripped, user names scrubbed from log paths (`Guard`);[^service-guard]
- length caps and a 64 KB body limit, keyboard-mash and English-language checks;
- a fingerprint with a six-hour duplicate window, 5 reports per hour per address and **60 issues
  per day service-wide** (`Ledger`, in memory);[^service-ledger]
- a log gate deciding whether an attached log may be published (`LogGate`), a minimum mod
  version, and an optional local multi-label toxicity classifier (`Classifier`).[^service-loggate][^service-classifier]

`202` is returned for filed, duplicate and over-ceiling reports alike so a flooder learns nothing.
The service is deployed behind Caddy on a VPS with OpenTofu-managed Cloudflare DNS
([infra/README.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/infra/README.md)),
and its text rules are unit-tested without the game in `tests/Feedback.Tests`.[^infra-readme][^feedback-tests]

## Lifecycle and timing

- **Drawing and input: `frame:draw-ui`.** `Ui.Draw` (which calls `DrawReportFooter` and
  `DrawReportWindow`) runs from `KSArmoryMod.OnAfterGui`, the `[StarMapAfterGui]` postfix on
  `Program.OnDrawUiViewports`, inside the `if (DrawUI)` block of `Program.OnFrame`
  (Program.cs L2265-L2269), after `PrepareImGui` and before `ImGui.Render()`.[^ksa-program][^ui]
  With the HUD hidden (F2 or a screenshot capture) the form is not drawn and nothing can be sent.
- **The request: `thread:background`.** `Send` returns immediately; the POST, the response read
  and `Finish`/`Fail` run on a thread-pool task. They only write plain fields (`Sending`, `Status`,
  `Sent`, `IssueUrl`) that the next GUI pass reads, and touch no KSA or ImGui state, so no
  synchronisation back to the main thread is needed beyond those field writes.[^feedback-client]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/Ui/UiReport.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiReport.cs) | Buttons, report window, log tail |
| [src/KSArmory/Ksa/FeedbackClient.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/FeedbackClient.cs) | The one network request, off the frame thread |
| [src/KSArmory/Sim/ReportDraft.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ReportDraft.cs) | Limits, validation, log tail, build gate (testable headlessly) |
| [services/feedback/Program.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/services/feedback/Program.cs) | The endpoint that files issues[^service-program] |
| [tools/check-network.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-network.sh) | CI guard: networking types only in `FeedbackClient.cs`, one request, from `Send()`[^check-network] |

## Snippets

```csharp
// src/KSArmory/Ksa/FeedbackClient.cs L50-L90
    public void Send(ReportDraft draft, string? log)
    {
        if (Sending) return;

        Sending = true;
        Status = "sending...";
        Sent = false;
        IssueUrl = null;

        var wire = new Wire(
            ReportDraft.Wire(draft.Kind),
            draft.Summary.Trim(),
            draft.Detail.Trim(),
            string.IsNullOrWhiteSpace(log) ? null : log,
            Build.Version,
            Build.KsaRunning,
            Platform());

        _ = Task.Run(async () =>
        {
            try
            {
                using HttpResponseMessage response = await Http.PostAsJsonAsync(Endpoint, wire);
                Finish(StatusOf(response), await response.Content.ReadAsStringAsync());
            }
            catch (TaskCanceledException)
            {
                Fail("timed out - the endpoint did not answer");
            }
            catch (Exception e)
            {
                // The message rather than the type: "no such host" tells the player they are
                // offline, where "HttpRequestException" tells them nothing.
                Fail($"could not reach the endpoint: {e.Message}");
            }
            finally
            {
                Sending = false;
            }
        });
    }
```

```csharp
// src/KSArmory/Ksa/FeedbackClient.cs L92-L109
    // Read by reflection: in game, calling the property directly throws MissingMethodException
    // for get_StatusCode against a System.Net.Http that plainly declares it. Whatever the runtime
    // resolves the type to, reflection asks the object it actually has. The assembly location is
    // logged once so the cause can be named.
    private static int StatusOf(HttpResponseMessage response)
    {
        Type type = response.GetType();

        if (!_reportedAssembly)
        {
            _reportedAssembly = true;
            Log.Info($"http type {type.FullName} from {type.Assembly.Location} "
                     + $"({type.Assembly.GetName().Version})");
        }

        object? value = type.GetProperty("StatusCode")?.GetValue(response);
        return value is null ? 0 : Convert.ToInt32(value);
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `Program.OnDrawUiViewports` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3162)) | Host of the `[StarMapAfterGui]` postfix the form draws from |
| `KSA.Vehicle` assembly version | The running build sent with each report and compared with the built-for build (see the lock feature) |
| ImGui (`Brutal.ImGuiApi.ImGui`): `Begin`, `Button`, `InputTextMultiline`, `CalcTextSize`, `GetContentRegionAvail`, `TextDisabled`, `TextColored` | The form |

## Pitfalls and version notes

- **No token in the DLL.** Anything shipped in a mod is extractable; the optional
  `FEEDBACK_SECRET` stops only casual scripts for the same reason.[^service-readme]
- **`HttpResponseMessage.StatusCode` throws `MissingMethodException` in game** although the loaded
  `System.Net.Http` declares it; the repo's notes attribute it to a type-identity mismatch on the
  return type across load contexts (a hypothesis, not proven) and read it by reflection.[^modding-notes]
- **Unsupported builds hide the buttons** rather than letting reports arrive against a game the
  mod was not built for; the reason is in the log, not the panel.[^ui-report]
- **Logs contain user names.** KSA log paths include the account name; the service scrubs paths
  before publishing, and the form says so.[^service-guard]
- The client mirrors the service's limits for a greyed button, but the service still enforces its
  own because a mod can be edited by anyone.[^report-draft]

## See also

- [Operator panel in ImGui](./operator-panel-imgui.md)
- [Own log file and developer switch](./own-log-file-and-developer-switch.md)
- [Pin the KSA build with a hashed assemblies lock file](./ksa-assemblies-lock.md)
- [One check script shared by the pre-push hook and CI](./repo-static-checks.md)
- [FeedbackClient posting JSON with polled Sending/Status fields and reflection for StatusCode](../../../howto/external-io/network/make-an-http-request-from-a-mod-without-blocking-the-frame/make-an-http-request-from-a-mod-without-blocking-the-frame.md) - how-to article demonstrating this feature

[^ui-report]: src/KSArmory/Ksa/Ui/UiReport.cs
[^feedback-client]: src/KSArmory/Ksa/FeedbackClient.cs
[^report-draft]: src/KSArmory/Sim/ReportDraft.cs
[^ui]: src/KSArmory/Ksa/Ui/Ui.cs
[^service-readme]: services/feedback/README.md
[^service-program]: services/feedback/Program.cs
[^service-guard]: services/feedback/Guard.cs
[^service-ledger]: services/feedback/Ledger.cs
[^service-loggate]: services/feedback/LogGate.cs
[^service-classifier]: services/feedback/Classifier.cs
[^infra-readme]: infra/README.md
[^check-network]: tools/check-network.sh
[^feedback-tests]: tests/Feedback.Tests/GuardTests.cs
[^modding-notes]: docs/KSA-MODDING-NOTES.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
