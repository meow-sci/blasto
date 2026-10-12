---
type: Mod Feature
title: Per-mod log file, build stamp and fault limiter
description: Keep the mod's own batched, thread-safe log file beside KSA's logs with the previous session preserved, stamp the KSA build the DLL was compiled for into the assembly, detect developer installs by a marker file, and disable the mod after repeated faults
mod: ksacars
category: getting-started
techniques: [starmap-lifecycle, background-thread]
hooks:
  - starmap StarMapImmediateLoad
  - starmap StarMapAllModsLoaded
  - starmap StarMapAfterOnFrame
  - starmap StarMapAfterGui
  - starmap StarMapUnload
lifecycle:
  - startup:immediate-load
  - startup:all-mods-loaded
  - frame:draw-ui
  - frame:after-on-frame
  - thread:background
game_types: [KSA.Vehicle, KSA.Constants]
tags: [logging, diagnostics, build-info, developer-mode, fault-handling, starmap]
---

A StarMap mod's `Console.WriteLine` only reaches the terminal that launched the game. KSACars keeps
its own log, `KSACars.log`, in the same folder as KSA's logs, truncated per session with the
previous session kept as `KSACars.prev.log`, and calls it "the only debugging channel it has".[^ksacars-log][^ksacars-claude]
The mod's comment justifies this by saying KSA's own `KittenSpaceAgency.*.log` "is written by its
internal logger, which mods cannot reach". That does not hold in build 2026.10.10.5554:
`Brutal.Logging.DefaultCategory.Log` is a public static `LogCategory`, and the public
`LogCategory.Make(name)` creates a category whose logger comes from the game's own
`LogSystem.Factory` ([current/decomp/Brutal.Logging/LogCategory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/LogCategory.cs#L44-L56));
gatOS in this corpus logs through `LogCategory.Make("gatOS")`.[^ksa-logcategory] A separate file is
still a reasonable choice (batched writes, a fixed name to tail, the previous session kept), just not
the only one.
Alongside it, `Build` reports which mod version and which KSA build the DLL was compiled for versus
the build actually running, and whether this is a developer's install.[^ksacars-build]

## How it works

**`Log`**

| Aspect | Behaviour |
|---|---|
| Location | First existing directory of: `<MyDocuments>/My Games/Kitten Space Agency/Logs`, `$XDG_DATA_HOME` (or `~/.local/share`)`/Kitten Space Agency/Logs` and its parent, `~/.config/Kitten Space Agency/Logs`, `~/My Games/Kitten Space Agency/Logs`, then the temp directory. Only directories that already exist are used |
| Session | On first use, copies `KSACars.log` to `KSACars.prev.log`, truncates it and writes a `=== KSACars session ... ===` header; prints the path to stdout |
| Levels | `Debug`, `Info`, `Warn`, `Error`, `Off`; threshold `Debug` in Debug builds, `Info` in Release (`#if DEBUG`); `Debug(Func<string>)` skips the interpolation when filtered |
| Output | Every line to stdout with a `[KSACars]` prefix, and to the file |
| Batching | Lines are appended to a `StringBuilder` under a lock; flushed when 100 ms have passed, 64 KB is pending, or the line is `Warn`/`Error` (written immediately, since a warning is often the last thing before a crash) |
| Background flush | A `System.Threading.Timer` flushes every 100 ms even when nothing is logging, so a quiet session's last lines reach disk |
| Failure | Any I/O error sets `_fileBroken` and the file is abandoned; stdout keeps working |
| Unload | `Log.Shutdown()` disposes the timer and flushes; called last in `[StarMapUnload]` |

**`Build`**

- `Version` from `AssemblyInformationalVersionAttribute` (stripping the `+<commit>` SourceLink
  suffix), set by semantic-release through `tools/set-version.sh`.
- `KsaBuild`: the csproj reads the `build` line of `ksa-assemblies.lock` at build time and stamps it
  as `[AssemblyMetadata("KsaBuild", ...)]`; `Build` reads it back.[^ksacars-csproj][^ksacars-lock]
- `KsaRunning`: `typeof(KSA.Vehicle).Assembly.GetName().Version`, the running game's own assembly
  version.
- `Developer`: true when a file named `developer` sits beside `KSACars.dll`.
  `tools/deploy.sh` writes it and the release archive never contains it; only then does the
  [command bridge](./file-drop-command-bridge.md) start.[^ksacars-deploy]

**Fault limiter (`KSACarsMod.Fault`).** The frame and GUI hooks wrap their work in `try/catch`; each
exception is logged with its count and, after 10, the mod removes all nine Harmony patches, stops
its sounds, clears its cars and logs "too many faults - cars disabled for this session".[^ksacars-mod]
Patch bodies have their own catch-and-log-once guards, because an exception inside the engine's
loop is "the game rather than a log line".

## Lifecycle and timing

| Hook | Log/Build use |
|---|---|
| `[StarMapImmediateLoad]` (prefix on `Mod.PrepareSystems`) | First log lines: mod id, mod version, KSA build compiled for vs running; the file is created here |
| `[StarMapAllModsLoaded]` | Logs patch installation and "a developer's install" or "a player's install" |
| `[StarMapAfterGui]`, `[StarMapAfterOnFrame]` | Fault counting; car events and a once-a-second drive line per driven car |
| Physics prefix (`frame:dispatch-solvers`) | Same API; lines from inside patches are guarded |
| Timer thread (`thread:background`) | Periodic flush, synchronised with writers by a lock |
| `[StarMapUnload]` | `Log.Shutdown()` after the last line. StarMap calls it after `Program.Dispose` and after its own id-less `UnpatchAll()`, so the hook removals before it are no-ops and the flush is the part that matters |

Writes can come from any thread (the lock keeps lines whole); the timer callback only touches the
buffer and the file, never game state.

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Ksa/Log.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Log.cs) | The logger |
| [src/KSACars/Ksa/Build.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Build.cs) | Version, KSA build stamp, running build, developer marker |
| [src/KSACars/KSACars.csproj](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACars.csproj#L85-L100) | Reads the lock's `build` line into `AssemblyMetadata` |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L154-L175) | `Fault`: count, log, disable after 10 |

## Snippets

```csharp
// src/KSACars/Ksa/Log.cs L111-L139
    private static void Write(Level level, string tag, string message)
    {
        if (level < Threshold) return;

        string line = $"{DateTime.Now:HH:mm:ss.fff} {tag} {message}";

        Console.WriteLine($"{Prefix} {line}");

        lock (Gate)
        {
            EnsureResolved();
            if (_path is null || _fileBroken) return;

            // Here rather than in EnsureResolved, which runs once ever: Shutdown stops the ticker,
            // and a mod loaded again in the same process has to get one back.
            _ticker ??= new Timer(_ => Flush(), null, FlushIntervalMs, FlushIntervalMs);

            Pending.Append(line).Append(Environment.NewLine);

            // Anything that went wrong goes to disk now. A warning is often the last thing written
            // before whatever it was warning about takes the process down.
            if (level >= Level.Warn
                || Pending.Length >= FlushBytes
                || Environment.TickCount64 - _lastFlushMs >= FlushIntervalMs)
            {
                FlushLocked();
            }
        }
    }
```

```xml
<!-- src/KSACars/KSACars.csproj L85-L100 -->
  <!--
    The KSA build this was compiled against, taken from ksa-assemblies.lock rather than written
    down a second time. The log compares it to the build actually running.
  -->
  <PropertyGroup>
    <KsaLockPath>$(MSBuildThisFileDirectory)../../ksa-assemblies.lock</KsaLockPath>
    <KsaBuildLine Condition="Exists('$(KsaLockPath)')">$([System.Text.RegularExpressions.Regex]::Match($([System.IO.File]::ReadAllText('$(KsaLockPath)')), 'build[ \t]+[0-9][^\r\n]*').Value)</KsaBuildLine>
    <KsaBuild Condition="'$(KsaBuildLine)' != ''">$(KsaBuildLine.Substring(6).Trim())</KsaBuild>
  </PropertyGroup>

  <ItemGroup Condition="'$(KsaBuild)' != ''">
    <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
      <_Parameter1>KsaBuild</_Parameter1>
      <_Parameter2>$(KsaBuild)</_Parameter2>
    </AssemblyAttribute>
  </ItemGroup>
```

```csharp
// src/KSACars/Ksa/KSACarsMod.cs L154-L175
    private void Fault(string where, Exception e)
    {
        _faults++;
        Log.Error($"{where} failed ({_faults}/{FaultLimit})", e);

        if (_faults < FaultLimit) return;

        _disabled = true;
        _buggySound.StopAll();
        _rocketSound.StopAll();
        _buggies.Clear();
        PhysicsHook.Remove();
        SeatedCrewHook.Remove();
        LightsHook.Remove();
        LensColourHook.Remove();
        FlamesHook.Remove();
        CrashHook.Remove();
        BoostHook.Remove();
        HudHook.Remove();
        RailsHook.Remove();
        Log.Error("too many faults - cars disabled for this session");
    }
```

## Game APIs touched

| API | Purpose |
|---|---|
| `typeof(KSA.Vehicle).Assembly.GetName().Version` | The running KSA build |
| `Constants.DocumentsFolderPath` / `LogsFolderPath` ([current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281-L283)) | Not used by the mod; KSA's own definition of the user and log folders[^ksa-constants] |

## Pitfalls and version notes

- **The log folder is guessed, not asked.** KSA defines
  `Constants.DocumentsFolderPath = <Personal>/My Games/Kitten Space Agency` and
  `LogsFolderPath = <DocumentsFolderPath>/logs` (lower-case `logs`). On case-sensitive file systems
  the mod's `.../Logs` candidate may not exist and the log falls through to a later candidate or
  the temp directory; the startup line on stdout says where it went.[^ksa-constants]
- **StarMap can move the user folder.** `-InstancePath` / `STARMAP_INSTANCE_PATH` makes StarMap
  patch `Constants.DocumentsFolderPath`; the mod's guess does not follow that override, while
  reading `Constants.LogsFolderPath` would.[^starmap-documentspath]
- **Flush on unload, and dispose timers held in statics**, or they keep firing into an unloaded mod.
- **Never let a hook throw.** Count faults and switch the mod off rather than flooding the log or
  crashing the frame loop.
- Release builds ship no symbols, so stack traces in the log have no line numbers.

## See also

- [One folder as both a content mod and a code mod](./mod-folder-content-and-code-mod.md)
- [File-drop command bridge](./file-drop-command-bridge.md)
- [Package, deploy and release](./package-deploy-release.md)
- [API surface pinning and diff](./api-surface-pinning-and-diff.md)
- [Stamp the targeted KSA build into assembly metadata and compare it at run time](../../../howto/getting-started/lifecycle/check-the-game-version/check-the-game-version-2.md) - how-to article demonstrating this feature
- [Wrap every frame hook in try/catch and disable the mod after ten faults](../../../howto/getting-started/lifecycle/contain-exceptions-in-starmap-hooks/contain-exceptions-in-starmap-hooks-1.md) - how-to article demonstrating this feature
- [Write a batched session log with .prev.log rotation, timer flush and a verbose toggle](../../../howto/getting-started/lifecycle/write-a-per-mod-log-file/write-a-per-mod-log-file-1.md) - how-to article demonstrating this feature

[^ksacars-log]: src/KSACars/Ksa/Log.cs
[^ksacars-build]: src/KSACars/Ksa/Build.cs
[^ksacars-mod]: src/KSACars/Ksa/KSACarsMod.cs
[^ksacars-csproj]: src/KSACars/KSACars.csproj
[^ksacars-lock]: ksa-assemblies.lock
[^ksacars-deploy]: tools/deploy.sh
[^ksacars-claude]: CLAUDE.md
[^ksa-constants]: KSA.Constants (decompiled)
[^starmap-documentspath]: StarMap.Core/Patches/DocumentsPathPatches.cs
[^ksa-logcategory]: Brutal.Logging.LogCategory (decompiled)
