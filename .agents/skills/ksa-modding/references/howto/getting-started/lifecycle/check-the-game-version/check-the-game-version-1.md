---
type: How-To
title: Compare VersionInfo.Current with a tested-version constant at AllModsLoaded
description: Warn, without blocking, when the running KSA version differs from the one the mod was tested on, using a single constant that CI can also read
category: getting-started
group: lifecycle
topic: check-the-game-version
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/ksaero/features/game-version-check
  - mods/ksa-measuretools/features/resilient-patching-and-diagnostics
techniques: [starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded]
game_types: [KSA.VersionInfo]
tags: [version-check, versioninfo, tested-version, advisory, spacedock]
---

KSA is in pre-alpha, and its internals change between builds. KSAero and KSA-MeasureTools each record the build they
were tested against as a constant and compare it with the running version at startup. A mismatch is a **warning
only**: the mod still installs its patches, because the structural checks of the patch target decide whether features
run. The pair gives bug reports a useful line ("tested against X, current is Y"). The build-time counterpart,
stamping the build the DLL was *compiled* against, is [Stamp the targeted build into assembly metadata](./check-the-game-version-2.md).

## Background: how the game does it

`KSA.VersionInfo.Current` is a `static readonly` instance built in a private constructor from the `KSA` assembly's
`AssemblyInformationalVersionAttribute`, parsed with a generated regex (`^v?(Year).(Month).(Build).(Revision)` with an
optional `-Suffix` and an ignored `+metadata`) and formatted as `v{Major}.{Minor}.{Build}.{Revision}{Suffix}`: a
leading `v`, the `+commit` part dropped, a pre-release suffix kept.[current/decomp/KSA/VersionInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VersionInfo.cs#L124-L145)

```csharp
// current/decomp/KSA/VersionInfo.cs L124-L145
	private VersionInfo()
	{
		Assembly element = Assembly.Load("KSA");
		string text = element.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
		if (text == null)
		{
			throw new NullReferenceException("Assembly version is null");
		}
		Match match = MyRegex().Match(text);
		if (!match.Success)
		{
			throw new FormatException("Version string is not in the correct format");
		}
		(int, int, int, int, string) tuple = ParseVersion(text);
		Major = tuple.Item1;
		Minor = tuple.Item2;
		Build = tuple.Item3;
		Revision = tuple.Item4;
		Suffix = tuple.Item5;
		VersionString = $"v{Major}.{Minor}.{Build}.{Revision}{Suffix}";
		Url = DEFAULT_URL;
	}
```

In build 2026.10.10.5554 (`current/version.json`: `"build": "2026.10.10.5554"`) the informational version is
`2026.10.10.5554+<commit>`, so `VersionString` is `v2026.10.10.5554`.[current/version.json](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/version.json) `VersionInfo` implements
`IComparable<VersionInfo>`; its `CompareTo` compares `Revision` first, then `Build`, then `Suffix` (it ignores `Major`
and `Minor`).

## Lifecycle and timing

`startup:all-mods-loaded`: the check is the first statement of `[StarMapAllModsLoaded]`. `VersionInfo.Current` needs
only the loaded `KSA` assembly, so it is valid at any hook; putting the check next to the patch means the warning
lands in the log right before any "Patching failed" line.[StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L14-L28) Which hook to choose:
[StarMapAllModsLoaded - work that needs the full mod set](../choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-3.md).

## Steps

1. **Add the constant, with the `v`.** `VersionString` always starts with `v`; the informational version does not:[KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L8-L21)

   ```csharp
   // KSAero/KSAero/Mod.cs L8-L21
   [StarMapMod]
   public sealed class Mod
   {
       private const string TestedGameVersion = "v2026.10.7.5541";
       private const string HarmonyId = "ksaero";

       private static Harmony? _harmony;

       [StarMapAllModsLoaded]
       public void OnFullyLoaded()
       {
           string gameVersion = VersionInfo.Current.VersionString;
           if (gameVersion != TestedGameVersion)
               DefaultCategory.Log.Warning($"[KSAero] Tested against {TestedGameVersion}, current is {gameVersion}.");
   ```

2. **Log both versions, always**, so every report states its context. MeasureTools logs the running version at Info
   and warns on mismatch with a consequence:[MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs#L19-L55)

   ```csharp
   // MeasureTools/Mod.cs L19-L28
   [StarMapAllModsLoaded]
   public void OnFullyLoaded()
   {
       string gameVersion = VersionInfo.Current.VersionString;
       DefaultCategory.Log.Info($"[MeasureTools] Game version: {gameVersion}");
       if (gameVersion != TestedGameVersion)
           DefaultCategory.Log.Warning(
               $"[MeasureTools] Tested against {TestedGameVersion}, current is {gameVersion}. " +
               "Some features may not work correctly.");
   ```

3. **Do not gate the patch on the version alone.** Builds that change nothing relevant would lose the mod, and a
   build that does change the target would still get a blind patch if the version happened to match. Pair the warning
   with structural checks (KSAero's transpiler refuses rather than guesses; MeasureTools checks reflected field types).
   See `isolate-patch-failures-and-fault-proof-patch-bodies`.
4. **Make the constant the single source of truth for releases.** MeasureTools' SpaceDock workflow extracts it with a
   `grep` so code, guard and the listing's game-version field cannot disagree:[.github/workflows/spacedock-publish.yml](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/.github/workflows/spacedock-publish.yml#L80-L97)

   ```bash
   # .github/workflows/spacedock-publish.yml L84-L94
             # Find the first Mod.cs that defines TestedGameVersion. The
             # project folder is normally <ModName>/Mod.cs from repo root.
             # ... grep -rEh --include='Mod.cs' for the `private const string TestedGameVersion = "` line ...
             if [ -z "$line" ]; then
               echo "::error::Could not find TestedGameVersion in any Mod.cs"
               exit 1
             fi
             # Strip everything except the quoted value, then strip leading 'v'
             # ... sed extracts the quoted value into $version ...
   ```

   (the full release flow is in [Workflow reading the tested game version from Mod.cs](../../build/package-and-publish-a-mod-release/package-and-publish-a-mod-release-4.md)).
5. **Bump the constant when you re-test**, and keep the README's stated build in sync.

## Complete example

[KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs) (minimal: constant, check, patch with stock fallback) and [MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs) (Info line plus warning, then
per-class patch application).

## Variations and gotchas

- **Exact match warns on every update.** That is intended; for a hard gate, compare the numeric `Build`/`Revision`
  or use `IComparable<VersionInfo>` (note its field order above).
- **KSAero's constant is `v2026.10.7.5541`**, so on build 2026.10.10.5554 it logs the warning, as designed.
- **`VersionInfo.Master`** is filled from a master-server check when the game's update check runs; do not use it
  for a tested-version test.
- **The warning is advisory**: nothing is disabled on mismatch.

## See also

- [Stamp the targeted build into assembly metadata](./check-the-game-version-2.md), [Warn when the running game build differs from the one a mod was tested on](./overview.md)
- [Game version check](../../../../mods/ksaero/features/game-version-check.md), [Per-patch Harmony application, drift checks, once-only logging and debug perf scopes](../../../../mods/ksa-measuretools/features/resilient-patching-and-diagnostics.md)
- [Log from a mod: per-mod log file, game log category and log-once](../write-a-per-mod-log-file/overview.md), [Package and publish a mod release (zip, CI publishing, installer)](../../build/package-and-publish-a-mod-release/overview.md)
