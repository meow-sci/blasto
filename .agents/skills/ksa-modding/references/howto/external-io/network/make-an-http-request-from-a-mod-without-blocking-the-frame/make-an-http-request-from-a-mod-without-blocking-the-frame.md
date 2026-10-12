---
type: How-To
title: FeedbackClient posting JSON with polled Sending/Status fields and reflection for StatusCode
description: Send an HTTP request from an ImGui button on a thread-pool task, poll plain status fields each frame and report a guarded bug-report form without blocking the frame
category: external-io
group: network
topic: make-an-http-request-from-a-mod-without-blocking-the-frame
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/in-game-bug-reports]
techniques: [background-thread, imgui-window, reflection]
hooks: [starmap StarMapAfterGui, reflection System.Net.Http.HttpResponseMessage.StatusCode]
lifecycle: [frame:draw-ui, thread:background]
game_types: [KSA.Program, KSA.Vehicle]
tags: [http, httpclient, bug-report, imgui, task-run, feedback, privacy]
---

Outcome: a mod sends one HTTP request from an ImGui button without ever stalling a frame, shows progress and failure reasons from plain fields, survives a runtime quirk in the game's load context, and does not touch the network unless the player asked. The worked example is a bug report window that posts a summary, details and the tail of the mod's own log to a guarded, self-hosted issue service. Prerequisites: StarMap (`[StarMapAfterGui]`) and the game's ImGui bindings. Demonstrated by [File bug reports from an ImGui form](../../../../mods/ksarmory/features/in-game-bug-reports.md) in KSArmory.

## Background: how the game does it

KSA gives a mod no HTTP helper and no frame-friendly async API, so the rule is the usual one for a main-thread UI loop: the draw code runs once per frame and must return quickly, so any blocking call goes onto a thread-pool task and the draw code only reads fields the task writes. The ImGui window is drawn from a StarMap postfix on `Program.OnDrawUiViewports`, which `Program.OnFrame` calls only while the HUD is shown:

```csharp
// current/decomp/KSA/Program.cs L2264-L2269
		Profiler.MainThread.Begin(Profiler.Tag.DrawUi);
		if (DrawUI)
		{
			OnDrawUiFrame(dtPlayer);
			OnDrawUiViewports(dtPlayer);
		}
```

With the HUD hidden the form is not drawn and nothing can be sent; this is a feature here, because a request is only ever started by a visible click.[^ksa-program-cs]

## Lifecycle and timing

| Work | Where |
|---|---|
| Draw the form, read status fields, handle the click | `[StarMapAfterGui]` (`frame:draw-ui`), main thread, HUD visible only |
| Build the wire record | the click, on the main thread (a few string copies) |
| Read the log tail | the click, on the main thread (see the gotchas) |
| `PostAsJsonAsync`, read the response, set the status fields | a `Task.Run` thread-pool task (`thread:background`) |
| Read `Sending`, `Status`, `Sent`, `IssueUrl` | the next draw pass; plain properties, no lock |

The task writes only those four fields and touches no KSA or ImGui state, so no hand-off back to the main thread is needed beyond the field writes. Frame structure: [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence). The HUD-hidden behaviour is covered by [Draw UI while the HUD is hidden](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md).

## Steps

1. **Decide where the network may be touched, and enforce it.** KSArmory allows networking types in exactly one file and a CI script fails the build otherwise, so a request can only come from the button:

   ```sh
   # tools/check-network.sh L19, L22
   ALLOWED="src/KSArmory/Ksa/FeedbackClient.cs"
   # ...
   PATTERN='HttpClient|WebClient|HttpWebRequest|WebRequest|TcpClient|UdpClient|Socket\(|ClientWebSocket|Dns\.'
   ```

2. **Keep one static `HttpClient` with an explicit timeout and plain status fields.** The timeout is generous because the service does work before it answers; the properties are what the draw code reads:

   ```csharp
   // src/KSArmory/Ksa/FeedbackClient.cs L19-L35
       public const string Endpoint = "https://api.ksarmory.com/feedback";

       // Generous, because the endpoint scores the text with a local model before answering, and a
       // cold container is slower than a warm one.
       private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };

       /// <summary>True while a report is in flight, so the panel can refuse a second one.</summary>
       public bool Sending { get; private set; }

       /// <summary>What the last attempt came back with, for the panel to show.</summary>
       public string? Status { get; private set; }

       /// <summary>Whether <see cref="Status"/> reports success, for colouring it.</summary>
       public bool Sent { get; private set; }

       /// <summary>Where the filed issue ended up, when the endpoint said.</summary>
       public string? IssueUrl { get; private set; }
   ```

3. **Send from a `Task.Run`, build the payload first, and always clear `Sending`.** `Send` returns immediately. It builds the wire record on the calling thread, then the task posts it. A timeout and any other failure become a short user-facing message; `finally` clears `Sending` so the form is usable again:

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

4. **Read the status code by reflection.** In the game, calling the `StatusCode` property directly throws `MissingMethodException` for `get_StatusCode`, although the loaded `System.Net.Http` declares it. The repository's modding notes attribute this to a type-identity mismatch across load contexts and say it is a hypothesis; the workaround is to ask the object it actually has, and to log the assembly location once so the cause can be named:[^ksarmory-ksa-modding-notes-md]

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

5. **Give each status its own message.** The endpoint deliberately says why it refused, so the client reports the reason instead of a generic failure. 202 is success, 426 means the mod is too old, 422 carries a reason in `{"error": ...}`, 429 means the address is rate limited, 503 means the service cannot file:

   ```csharp
   // src/KSArmory/Ksa/FeedbackClient.cs L117-L126, L137-L151
           switch (status)
           {
               case 202:
                   Sent = true;
                   IssueUrl = Url(body);
                   Status = IssueUrl is null ? "thank you - report received" : "thank you - report filed";
                   Log.Info($"report accepted{(IssueUrl is null ? "" : $": {IssueUrl}")}");
                   return;

               case 426:
   // ...
               case 429:
                   Fail("too many reports from here - try again later");
                   return;

               case 503:
                   // The endpoint says when it cannot file, rather than thanking someone for a
                   // report nobody will read.
                   Fail(Reason(body) ?? "the endpoint cannot file reports right now - please try later");
                   return;

               case 400:
                   Fail(string.IsNullOrWhiteSpace(body) ? "the report was rejected" : Trim(body));
                   return;

               default:
   ```

6. **Mirror the service's rules in the draft, so Send is greyed instead of failing.** The limits are the service's own, repeated on purpose; `Problem` returns one reason at a time, in the order a person fills the form in:

   ```csharp
   // src/KSArmory/Sim/ReportDraft.cs L20-L25, L81-L92
       public const int MaxSummary = 120;
       public const int MaxDetail = 4_000;
       public const int MaxLog = 12_000;

       /// <summary>Short enough that a one-word summary is refused before it is sent.</summary>
       public const int MinSummary = 8;
   // ...
       public static string? Problem(string? summary, string? detail)
       {
           string subject = (summary ?? string.Empty).Trim();
           string body = (detail ?? string.Empty).Trim();

           if (subject.Length == 0) return "a one-line summary is needed";
           if (subject.Length < MinSummary) return "the summary is too short to act on";
           if (subject.Length > MaxSummary) return $"the summary is {subject.Length - MaxSummary} characters too long";
           if (body.Length > MaxDetail) return $"the detail is {body.Length - MaxDetail} characters too long";

           return null;
       }
   ```

7. **Gate the whole feature on the build.** Reporting is offered only when the KSA build the mod was compiled against equals the build it is running on. The running build is read off the loaded `KSA.Vehicle` assembly, and an unknown build on either side counts as a match so a missing stamp never removes the buttons:

   ```csharp
   // src/KSArmory/Sim/ReportDraft.cs L52-L57, L62-L73
       public static bool GameIsSupported(string? builtFor, string? running)
       {
           if (string.IsNullOrWhiteSpace(builtFor) || string.IsNullOrWhiteSpace(running)) return true;

           return Normalise(builtFor) == Normalise(running);
       }
   // ...
       private static string Normalise(string version)
       {
           string trimmed = version.Trim().TrimStart('v', 'V');

           while (trimmed.EndsWith(".0", StringComparison.Ordinal)
                  && trimmed.Count(c => c == '.') > 2)
           {
               trimmed = trimmed[..^2];
           }

           return trimmed;
       }
   ```

   [src/KSArmory/Ksa/Build.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Build.cs#L50-L60) supplies the running side.

8. **Draw the form and the button states.** The window uses a stable `###` id so its title can change with the kind. While a request is in flight the button is replaced by "sending...", while the draft is invalid the reason text replaces it, and otherwise **Send** starts the request:

   ```csharp
   // src/KSArmory/Ksa/Ui/UiReport.cs L190-L218
       private void DrawSendButton()
       {
           string? problem = ReportDraft.Problem(_report.Summary, _report.Detail);

           if (_feedback.Sending)
           {
               ImGui.TextDisabled("sending...");
               return;
           }

           if (problem is not null)
           {
               // Said rather than a dead button: a control that does nothing when clicked, with no
               // reason given, reads as broken.
               ImGui.TextDisabled(problem);
               return;
           }

           if (ImGui.Button("Send")) _feedback.Send(_report, _report.AttachLog ? ReadLogTail() : null);

           ImGui.SameLine();

           if (ImGui.Button("Clear"))
           {
               _report.Summary = string.Empty;
               _report.Detail = string.Empty;
               _feedback.Clear();
           }
       }
   ```

   The window itself is opened by `Ui.Draw`, which runs from the `[StarMapAfterGui]` hook ([src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L509-L524)). It re-checks `GameIsSupported` every frame because the window outlives the click that opened it.

9. **Attach the log tail, and say what leaves the machine.** The log is opened with `FileShare.ReadWrite` because the mod has it open for appending; the tail is the last 12,000 characters cut at a line boundary, because the failure is at the bottom of the log:

   ```csharp
   // src/KSArmory/Ksa/Ui/UiReport.cs L243-L262
       private static string? ReadLogTail()
       {
           try
           {
               string? path = Log.FilePath;
               if (path is null) return null;

               // Shared read: the mod has this file open for appending, and so may a tail running
               // beside it.
               using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
               using var reader = new StreamReader(stream);

               return ReportDraft.Tail(reader.ReadToEnd());
           }
           catch (Exception e)
           {
               Log.Warn($"could not read the log to attach it: {e.Message}");
               return null;
           }
       }
   ```

   The form tells the player the size sent and that user names in paths are hidden when the text is published.

10. **Sub-step: the service-side guard.** The mod holds no credential; a service holds the GitHub token and decides what to publish. At this pin, the endpoint rejects bodies over 64 KB, rate limits each address to 5 requests per hour, strips invisible and bidi characters, scrubs user names from log paths, refuses non-English text with 422, enforces a minimum mod version with 426 and keeps a service-wide daily ceiling in a ledger:[^ksarmory-program-cs]

   ```csharp
   // services/feedback/Program.cs L70-L79
               options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
               options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                   RateLimitPartition.GetFixedWindowLimiter(
                       context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                       _ => new FixedWindowRateLimiterOptions
                       {
                           PermitLimit = 5,
                           Window = TimeSpan.FromHours(1),
                           QueueLimit = 0,
                       }));
   ```

   One documentation drift: the service README says an over-the-ceiling report is answered 202 like a duplicate, but the code at this pin answers it 503 with an explanatory message, and the mod's `Finish` shows that message. Trust the code. Per-client rate limiting also depends on `UseForwardedHeaders` when the service sits behind a proxy.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/FeedbackClient.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/FeedbackClient.cs) | The one network request, status mapping and reflection workaround |
| [src/KSArmory/Ksa/Ui/UiReport.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiReport.cs) | Buttons, window, log tail |
| [src/KSArmory/Sim/ReportDraft.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ReportDraft.cs) | Limits, validation, log tail cut and build gate; game-free, so unit-testable |
| [tools/check-network.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-network.sh) | CI guard: networking only in `FeedbackClient.cs`, one request |
| [services/feedback/Program.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/services/feedback/Program.cs) | The service endpoint that files the issue |
| [services/feedback/Guard.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/services/feedback/Guard.cs) | Text sanitising, path scrubbing, mash and language checks |

## Variations and gotchas

- **No token in the DLL.** Anything shipped in a mod is extractable; the optional shared secret only stops casual scripts.
- **Reading the log happens on the main thread.** `ReadLogTail` calls `ReadToEnd` inside the click handler, on the draw thread, before the task starts. A large log makes the click cost a hitch; read the file inside the task if your log can grow beyond a few hundred KB.
- **`MissingMethodException` on a BCL property.** Treat any BCL property that throws in the game but compiles cleanly as a load-context identity problem, and fall back to reflection on the object you hold.
- **Unsupported builds hide the buttons.** The reason goes to the log, not the panel, so check the log line `reporting off` before debugging a missing button.
- **Mirror, do not trust, the service rules.** The service still enforces its own limits because a mod can be edited by anyone.
- **Logs contain user names.** KSA log paths include the account name; say so in the form and scrub on the service.

## See also

- [Overview](overview.md)
- [File bug reports from an ImGui form](../../../../mods/ksarmory/features/in-game-bug-reports.md)
- [Check the game version](../../../getting-started/lifecycle/check-the-game-version/overview.md), [Write a per-mod log file](../../../getting-started/lifecycle/write-a-per-mod-log-file/overview.md)
- [Build a settings window](../../../ui/windows/build-a-settings-window/overview.md)
- [Host a terminal with pty sessions and frame handoff](../../terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/overview.md): the same thread-to-frame rule for a terminal

[^ksa-program-cs]: KSA decomp: KSA/Program.cs (build 2026.10.10.5554)
[^ksarmory-ksa-modding-notes-md]: KSArmory/docs/KSA-MODDING-NOTES.md
[^ksarmory-program-cs]: KSArmory/services/feedback/Program.cs
