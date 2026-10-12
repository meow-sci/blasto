---
type: Mod Feature
title: Game version check
description: At StarMapAllModsLoaded the mod compares VersionInfo.Current.VersionString with the build it was tested on and logs a warning on mismatch, leaving the real go or no-go decision to the transpiler's shape check
mod: ksaero
category: getting-started
techniques: [starmap-lifecycle, version-check]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded]
game_types: [KSA.VersionInfo, Brutal.Logging.DefaultCategory]
tags: [version, compatibility, logging, starmap]
---

KSAero records the game build it was tested against as a constant and, before patching, compares it
with the running game's version string. A mismatch is logged as a warning and nothing else happens:
the mod still installs its patch, because the transpiler's structural check is what actually decides
whether the patch is safe. The two together give players a useful log line ("tested against X, current
is Y") without blocking the mod on every game update.[^ksaero-mod] [^ksaero-readme]

## How it works

- `private const string TestedGameVersion = "v2026.10.7.5541";` in the entry class
  ([KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L11)).
- `VersionInfo.Current.VersionString` is compared with `!=` and a mismatch goes to
  `DefaultCategory.Log.Warning`, tagged `[KSAero]`
  ([KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L19-L21)).
- `VersionInfo.Current` is a `static readonly` instance built from the `KSA` assembly's
  `AssemblyInformationalVersionAttribute`, parsed with the generated regex below, and formatted as
  `v{Major}.{Minor}.{Build}.{Revision}{Suffix}`: a leading `v`, the `+commit` metadata dropped, and a
  pre-release suffix kept with its `-`
  ([current/decomp/KSA/VersionInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VersionInfo.cs#L124-L145),
  [current/decomp/KSA/VersionInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VersionInfo.cs#L159-L178)).[^ksa-versioninfo]
- In build 2026.10.10.5554 the informational version is `2026.10.10.5554+ea506daffa75e919075b7fc042633abdae551d6a`
  ([current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)), so `VersionString` is `v2026.10.10.5554` and
  KSAero logs the warning on that build.[^ksa-dll]

```csharp
// current/decomp/KSA/VersionInfo.cs L178
	[GeneratedRegex("^v?(?<Year>\\d+)\\.(?<Month>\\d+)\\.(?<Build>\\d+)\\.(?<Revision>\\d+)(?:-(?<Suffix>[^+]+))?(?:\\+.*)?$")]
```

## Lifecycle and timing

`startup:all-mods-loaded`: the check is the first statement of `[StarMapAllModsLoaded]`
`OnFullyLoaded`, which StarMap invokes from its postfix on `ModLibrary.LoadAll`
([StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L17-L28)).
`VersionInfo.Current` needs nothing but the loaded `KSA` assembly, so it is valid at any StarMap hook;
putting the check next to the patch means the warning lands in the log right before any
"Patching failed" line, which makes bug reports self-explanatory.[^starmap-modlibrarypatches]

## Key code

| Path | Role |
|---|---|
| [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L11) | Tested-version constant |
| [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L19-L21) | Comparison and warning |

## Snippets

```csharp
// KSAero/Mod.cs L8-L21
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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `VersionInfo.Current` | [current/decomp/KSA/VersionInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VersionInfo.cs#L15-L17) | The running game's version |
| `VersionInfo.VersionString` | [current/decomp/KSA/VersionInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VersionInfo.cs#L115) | `v`-prefixed string compared with the constant |
| `VersionInfo.Major`, `Minor`, `Build`, `Revision`; `IComparable<VersionInfo>` | [current/decomp/KSA/VersionInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VersionInfo.cs#L105-L113) | Alternatives for range checks (not used by KSAero) |
| `DefaultCategory.Log` | [current/decomp/Brutal.Logging/DefaultCategory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/DefaultCategory.cs) | Game log category used for the warning[^ksa-defaultcategory] |

## Pitfalls and version notes

- **Exact match warns on every update.** That is intended here. For a hard gate, compare the numeric
  `Build` and `Revision` (or use `IComparable<VersionInfo>`) instead of strings.
- **Include the `v`.** `VersionString` always starts with `v`; the informational version does not.
- **Do not gate the patch on the version alone.** Builds that change nothing relevant would lose the
  mod, and builds that do change the target would still get a blind patch if the version happened to
  match. Pair it with a structural check like the one in
  [ComputeDrag call-site transpiler](computedrag-call-site-transpiler.md).
- **Keep the constant and the README in sync.** KSAero's README names the same build.

## See also

- [Patch refusal and rollback](patch-refusal-and-rollback.md)
- [ComputeDrag call-site transpiler](computedrag-call-site-transpiler.md)
- [Compare VersionInfo.Current with a tested-version constant at AllModsLoaded](../../../howto/getting-started/lifecycle/check-the-game-version/check-the-game-version-1.md) - how-to article demonstrating this feature
- [StarMapAllModsLoaded: work that needs the full mod set](../../../howto/getting-started/lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-3.md) - how-to article demonstrating this feature

[^ksaero-mod]: KSAero/Mod.cs
[^ksaero-readme]: KSAero/README.md
[^ksa-versioninfo]: KSA.VersionInfo (decompiled, build 2026.10.10.5554)
[^ksa-dll]: KSA.dll (build 2026.10.10.5554), assembly attributes
[^starmap-modlibrarypatches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^ksa-defaultcategory]: Brutal.Logging.DefaultCategory (decompiled, build 2026.10.10.5554)
