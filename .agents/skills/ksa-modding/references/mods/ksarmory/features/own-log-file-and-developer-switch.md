---
type: Mod Feature
title: Own log file, build stamps and a developer marker file
description: A batched, thread-safe log written to KSArmory.log beside KSA's logs with the previous session kept as KSArmory.prev.log, a version and KSA-build stamp read from assembly metadata, and developer tools switched on by an empty file named developer next to the DLL
mod: ksarmory
category: getting-started
techniques: [file-logging, assembly-metadata, marker-file]
hooks:
  - starmap StarMapImmediateLoad
  - starmap StarMapAllModsLoaded
  - starmap StarMapUnload
  - thread System.Threading.Timer log flush
lifecycle: [startup:immediate-load, startup:all-mods-loaded, thread:background]
game_types: [KSA.Constants, KSA.Program, KSA.Vehicle]
tags: [logging, diagnostics, versioning, assembly-metadata, msbuild, developer-mode, deploy]
---

StarMap mods only reach stdout, and KSA's own session log is written by an internal logger a mod
cannot reach. KSArmory therefore keeps its own `KSArmory.log` in the same folder, with the previous
session preserved, written in batches so a salvo's hundreds of lines a second do not become a
syscall per line on the frame thread. Two small companions make bug reports answerable: `Build`
reads the mod version and the KSA build it was compiled against from assembly attributes, and a
marker file named `developer` beside the DLL turns on the tools only a developer wants.[^ksarmory-log][^ksarmory-build]

## How it works

**Log** (`static class Log`):

| Aspect | Behaviour |
|---|---|
| Levels | `Debug`, `Info`, `Warn`, `Error`, `Off`; `Threshold` defaults to `Debug` in DEBUG builds and `Info` in release; the panel's verbose switch sets it at runtime[^ksarmory-uidebug] |
| Cheap debug | `Debug(Func<string>)` skips building the string below threshold |
| Line | `HH:mm:ss.fff TAG message`; every line also goes to `Console.WriteLine` with a `[KSArmory]` prefix |
| File location | First existing directory of: `MyDocuments/My Games/Kitten Space Agency/Logs`; `$XDG_DATA_HOME` (or `~/.local/share`)`/Kitten Space Agency/Logs`; the same without `Logs`; `~/.config/Kitten Space Agency/Logs`; `~/My Games/Kitten Space Agency/Logs`; the temp directory. Only existing folders are used |
| Rotation | On first use: copy `KSArmory.log` to `KSArmory.prev.log`, truncate with a `=== KSArmory session ... ===` header |
| Batching | Lines appended to a `StringBuilder` under a lock; flushed when the line is `Warn` or worse, the buffer reaches 64 KB, or 100 ms have passed; a `System.Threading.Timer` also flushes every 100 ms so a quiet session still reaches disk |
| Failure | Any write exception sets `_fileBroken`; stdout continues |
| Shutdown | `Log.Shutdown()` disposes the timer and flushes; it is the last call in `[StarMapUnload]`[^ksarmory-mod] |
| Reuse | `Log.Folder` is the mod's only knowledge of the KSA user folder: the bridge, scenario files, the settings fallback and save repair all derive paths from it; `Log.FilePath` flushes before returning, for bug-report attachments |

**Build** (static, computed once):

| Property | Source |
|---|---|
| `Version` | `AssemblyInformationalVersionAttribute` with any `+commit` suffix removed, else `AssemblyName.Version.ToString(3)`; the csproj `<Version>` is set by the release tooling |
| `KsaBuild` | `AssemblyMetadataAttribute("KsaBuild")`, stamped by an MSBuild regex over `ksa-assemblies.lock`'s `build ...` line[^ksarmory-csproj][^ksarmory-lock] |
| `KsaRunning` | `typeof(KSA.Vehicle).Assembly.GetName().Version` |
| `Developer` | `File.Exists(Path.Combine(dirOf(typeof(Build).Assembly.Location), "developer"))`; `tools/deploy.sh` creates the empty file, the release packaging never includes it[^ksarmory-deploy] |

`ReportDraft.GameIsSupported(built, running)` compares the two builds after trimming trailing `.0`
components; `[StarMapImmediateLoad]` logs `KSArmory <version> built for KSA <x>, running <y> -
reporting on|off`, and the panel only offers bug reporting when they match.[^ksarmory-reportdraft]
`[StarMapAllModsLoaded]` starts the scenario runner and the file-command bridge only when
`Build.Developer` is true and logs which kind of install it is.

## Lifecycle and timing

- **First write**: at the latest the `loading` line in `OnImmediateLoad` (StarMap's prefix on
  `Mod.PrepareSystems`, reached from `ModLibrary.PrepareAll` at Program.cs L899); it resolves the
  folder and rotates the file. KSA's
  own logs folder already exists by then: the `Program` constructor creates
  `Constants.LogsFolderPath` at Program.cs L693 before loading mods.[^ksa-program]
- **Static initializers** of `Build` run on first access, also in `OnImmediateLoad`; they touch only
  the mod's and KSA's loaded assemblies and one file-system probe.
- **Writes** come from the main thread (all StarMap hooks and Harmony patches) and from thread-pool
  tasks (the feedback client); one lock covers buffer and writer. The flush timer runs on a
  thread-pool thread.
- **Last write**: `Unload` logs `unloaded` and then calls `Log.Shutdown()`. Under StarMap 4dc60f8 that
  happens after KSA's `Program.Main` returned.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/Log.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Log.cs) | Levels, batching, timer flush, folder search, rotation |
| [src/KSArmory/Ksa/Build.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Build.cs) | Version, built-for and running KSA build, developer marker |
| [src/KSArmory/KSArmory.csproj](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmory.csproj#L125-L140) | `KsaBuild` stamped from the lock file into `AssemblyMetadata` |
| [tools/deploy.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/deploy.sh#L68-L71) | Writes the `developer` marker on a developer install |
| [tools/ksa-user-dir.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/ksa-user-dir.sh) | Shell twin of the folder search, for `tail -F` from outside the game[^ksarmory-userdir] |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L132-L147) | Startup build line |

## Snippets

```csharp
// src/KSArmory/Ksa/Log.cs L202-L229
    private static void EnsureResolved()
    {
        if (_resolved) return;
        _resolved = true;

        foreach (string candidate in CandidateDirectories())
        {
            try
            {
                if (!Directory.Exists(candidate)) continue;

                string path = Path.Combine(candidate, "KSArmory.log");

                // Truncated per session so the file reflects this run, not every run ever -- and the
                // last run kept beside it, because a game relaunched to look into a report is exactly
                // the moment the report's own log would otherwise be lost.
                if (File.Exists(path)) File.Copy(path, Path.Combine(candidate, "KSArmory.prev.log"), overwrite: true);
                File.WriteAllText(path, $"=== KSArmory session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
                _path = path;
                Console.WriteLine($"{Prefix} logging to {path}");
                return;
            }
            catch
            {
                // Try the next candidate.
            }
        }
    }
```

```csharp
// src/KSArmory/Ksa/Log.cs L124-L143
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
```

```xml
<!-- src/KSArmory/KSArmory.csproj L129-L140 -->
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

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `Constants.DocumentsFolderPath`, `Constants.LogsFolderPath` | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281-L283) | Not used by the mod; KSA's own answer to "where is the user folder" (`<Personal>/My Games/Kitten Space Agency`, logs in `logs`)[^ksa-constants] |
| `Program` constructor log setup | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L689-L700) | Creates `LogsFolderPath` and points KSA's logger at it |
| `typeof(KSA.Vehicle).Assembly` | (KSA assembly) | Running build via its assembly version |

## Pitfalls and version notes

- **The folder is guessed, not asked.** KSA exposes `Constants.DocumentsFolderPath`, which StarMap
  can redirect with `-InstancePath` or `STARMAP_INSTANCE_PATH` (DocumentsPathPatches.cs L8-L45). The
  mod's search ignores both, so under an instance path its log, settings fallback and save repair
  look at the default folder.[^starmap-documentspath]
- **Case differs.** KSA's logs folder is `logs` (Constants.cs L283); the mod looks for `Logs`. Same
  folder on Windows; on a case-sensitive file system the first candidate misses and a later one (or
  the temp directory) wins.
- **The running build is read from the KSA assembly's version** (unverified: the decompiled sources
  carry no assembly attributes, so whether that version equals the `2026.x.y.zzzz` build string
  could not be checked here). If it is null or empty the check treats the game as supported.
- **Built for 5541, running 5554**: `ksa-assemblies.lock` at this commit says `build 2026.10.7.5541`,
  so on build 5554 the startup line reads `reporting off`.
- **A timer in a static field outlives an unload** unless `Shutdown` runs; the ticker is re-created
  on the next write so a reloaded mod gets it back.
- `docs/KSA-MODDING-NOTES.md` says `Console.WriteLine` reaches the KSA console; the decomp does not
  show a stdout redirect into the in-game console (unverified either way).

## See also

- [StarMap entry and fault containment](./starmap-entry-and-fault-containment.md)
- [Per-craft settings JSON sidecar](./per-craft-settings-json-sidecar.md)
- [Startup save repair](./startup-save-repair.md)
- [In-game bug reports](./in-game-bug-reports.md)
- [Build, deploy, package, release](./build-deploy-package-release.md)
- [KSA assemblies lock](./ksa-assemblies-lock.md)
- [File command bridge and MCP](./file-command-bridge-and-mcp.md)
- [Unattended scenario runner](./unattended-scenario-runner.md)
- [Stamp the targeted KSA build into assembly metadata and compare it at run time](../../../howto/getting-started/lifecycle/check-the-game-version/check-the-game-version-2.md) - how-to article demonstrating this feature
- [Anchor the log on the game's logs folder and gate developer tools with a marker file](../../../howto/getting-started/lifecycle/write-a-per-mod-log-file/write-a-per-mod-log-file-2.md) - how-to article demonstrating this feature
- [A file-drop bridge with in/ and out/ folders, atomic replies and multi-frame closures](../../../howto/testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-1.md) - how-to article demonstrating this feature

[^ksarmory-log]: src/KSArmory/Ksa/Log.cs
[^ksarmory-build]: src/KSArmory/Ksa/Build.cs
[^ksarmory-csproj]: src/KSArmory/KSArmory.csproj
[^ksarmory-lock]: ksa-assemblies.lock
[^ksarmory-reportdraft]: src/KSArmory/Sim/ReportDraft.cs
[^ksarmory-deploy]: tools/deploy.sh
[^ksarmory-userdir]: tools/ksa-user-dir.sh
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-uidebug]: src/KSArmory/Ksa/Ui/UiDebug.cs
[^starmap-documentspath]: StarMap.Core/Patches/DocumentsPathPatches.cs
[^ksa-constants]: KSA.Constants (decompiled)
[^ksa-program]: KSA.Program (decompiled)
