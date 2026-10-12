---
type: How-To
title: Ship a preset craft as VehicleSaveData files in the vehicle library
description: Write the meta.toml and vehicle.xml pair that makes a one-part craft appear in the player's vehicle list, from an install script or from mod code at startup
category: vehicles-and-parts
group: parts
topic: ship-ready-made-craft
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/vehicle-library-presets]
techniques: [xml-asset]
hooks: [file write to <Documents>/Vehicles, starmap StarMapAllModsLoaded]
lifecycle: [startup:asset-load, startup:all-mods-loaded]
game_types:
  - KSA.VehicleSaves
  - KSA.DefaultVehicleSaves
  - KSA.VehicleSave
  - KSA.VehicleSaveData
  - KSA.SaveMetaData
  - KSA.UncompressedVehicleSave
  - KSA.Constants
tags: [vehicle-library, preset, meta-toml, vehicle-xml, install-script]
---
Outcome: a player opens the vehicle list and sees your craft ("Beach Buggy", "Eldorado") ready to launch,
without opening the editor. KSACars does this with an install-time shell script that writes a one-part craft for
each car in exactly the format of Core's shipped default vehicles; this article also shows the same thing as a
few lines of mod code, labelled as adapted.

Prerequisite: a part that is a complete craft (article 2). Demonstrated by [ksacars/vehicle-library-presets](../../../../mods/ksacars/features/vehicle-library-presets.md).

## Background: how the game does it

See the [overview](overview.md) for the libraries. What matters here:

- The user library is `<Documents>/Vehicles` (`My Games/Kitten Space Agency/Vehicles`); each craft is a
  sub-folder named for the craft [current/decomp/KSA/VehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaves.cs#L167)[^ksa-vehiclesaves] [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)[^ksa-constants].
- `Refresh()` clears the list and re-reads every sub-folder; a folder without `meta.toml` or `vehicle.xml` is
  skipped with a warning [current/decomp/KSA/VehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaves.cs#L280-L290) [current/decomp/KSA/UncompressedVehicleSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedVehicleSave.cs#L242-L279)[^ksa-uncompressedvehiclesave].
- A `vehicle.xml` with one `RootPartRef` and no connections is a whole one-part craft. Part Ids inside are
  `InstanceOf` references to `PartTemplate` Ids, so they are API: renaming a part breaks every craft that
  names it (see [Change a shipped part without breaking saves](../evolve-a-shipped-part-without-breaking-saves/overview.md)).
- Core's craft carry `SubPartRef` entries so subparts are fixed at save time; KSACars' files have none, so
  every subpart comes from the current part template on load and the files cannot fall out of step with the
  subpart list.

## Lifecycle and timing

The script has no in-game lifecycle: it runs at install time. KSA reads the library at startup
(`VehicleSaves.OnApplicationStart`, before mods load [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L783-L784)[^ksa-program]) and whenever `Refresh()` runs. A
mod that writes the files itself at `StarMapAllModsLoaded` is therefore *after* the first scan and must call
`VehicleSaves.Refresh()` once it has written. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Pick the folder: the user's `Vehicles` directory. KSA looks for `Vehicles`; on case-insensitive filesystems an
   existing `vehicles` is the same directory, which is why KSACars' script prefers whichever exists.
2. Write `meta.toml` with the keys Core's craft use (`name`, `created`, `updated`, `version`, `systems`), and
   `vehicle.xml` with the part Id as `RootPartRef InstanceOf`. KSACars' script, verbatim:

```sh
# tools/install-testcraft.sh L28-57
NOW="$(date +%Y-%m-%dT%H:%M:%S.0000000)"

# Mirrors Content/Core/defaultvehicles/*/vehicle.xml: a single root part with no connections is the
# whole craft, and each car declares <Control />, so it needs no command pod.
install_car() {
  local name="$1" part="$2"
  mkdir -p "$VEHICLES/$name"
  cat > "$VEHICLES/$name/meta.toml" <<EOF
name = "$name"
created = $NOW
updated = $NOW
version = "KSACars-mod"
systems = [ "Sol", ]
EOF
  cat > "$VEHICLES/$name/vehicle.xml" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<VehicleSaveData Id="$name" ActiveSequence="0">
  <RootPartRef InstanceOf="$part" LocalInstanceId="1" Stage="0">
    <Transform>
      <Position X="0" Y="0" Z="0" />
      <Rotation X="0" Y="0" Z="0" />
      <Scale X="1" Y="1" Z="1" />
    </Transform>
  </RootPartRef>
</VehicleSaveData>
EOF
  echo "installed '$name' to $VEHICLES/$name"
}
install_car "Beach Buggy" KSACars_Prefab_Buggy
install_car "Eldorado" KSACars_Prefab_Eldorado
```

3. Compare with a stock craft: `Content/Core/defaultvehicles/Rocket/meta.toml` has the same five keys
   ([current/Content/Core/defaultvehicles/Rocket/meta.toml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/defaultvehicles/Rocket/meta.toml#L1-L5)[^content-meta]).
4. To write the same files from mod code instead of a script, run this once at `StarMapAllModsLoaded` (adapted
   from `tools/install-testcraft.sh`; compiles against `KSA.VehicleSaves.SaveFolderPath` and
   `VehicleSaves.Refresh`):

```csharp
// adapted from KSACars/tools/install-testcraft.sh
using KSA;

internal static class CraftPresets
{
    public static void Install(string name, string partId)
    {
        string dir = Path.Combine(VehicleSaves.SaveFolderPath, name);
        string vehicleXml = Path.Combine(dir, "vehicle.xml");
        if (File.Exists(vehicleXml)) return;            // never overwrite a player's edited copy

        Directory.CreateDirectory(dir);
        string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.0000000");
        File.WriteAllText(Path.Combine(dir, "meta.toml"),
            $"name = \"{name}\"\ncreated = {now}\nupdated = {now}\nversion = \"MyMod\"\nsystems = [ \"Sol\", ]\n");
        File.WriteAllText(vehicleXml,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            $"<VehicleSaveData Id=\"{name}\" ActiveSequence=\"0\">\n" +
            $"  <RootPartRef InstanceOf=\"{partId}\" LocalInstanceId=\"1\" Stage=\"0\">\n" +
            "    <Transform><Position X=\"0\" Y=\"0\" Z=\"0\" /><Rotation X=\"0\" Y=\"0\" Z=\"0\" /><Scale X=\"1\" Y=\"1\" Z=\"1\" /></Transform>\n" +
            "  </RootPartRef>\n" +
            "</VehicleSaveData>\n");
        VehicleSaves.Refresh();                         // the first scan ran before mods loaded
    }
}
```

5. Check the log for `started loading vehicle '<name>'` when you launch it, and for
   `saved vehicle '<name>' has no vehicle file and was skipped.` if the library ignored it.
6. Document it for players: tell them the craft is under the vehicle list, and that a re-install does not
   overwrite their copy (the script above overwrites; the adapted code does not).

## Complete example

- [tools/install-testcraft.sh](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/install-testcraft.sh)[^ksacars-install-testcraft] - the whole script
- [src/KSACars/Ksa/CraftSpawner.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/CraftSpawner.cs#L54-L85)[^ksacars-craftspawner] - how the same craft are found from code ([Spawn a vehicle from code (from the vehicle library or a hand-built part)](../../vehicles/spawn-a-vehicle-at-runtime/overview.md))

## Variations and gotchas

- **Stock library.** `Content/Core/defaultvehicles` is under the game folder; it is also what
  `VehicleTemplate.OnDataLoad` resolves `<Vehicle>` assets against [current/decomp/KSA/VehicleTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleTemplate.cs#L56-L90)[^ksa-vehicletemplate]. Writing there
  needs write access to the install and survives only until the next game update, so the user library is the
  right place for a mod's presets.
- **Case.** Linux is case-sensitive; create `Vehicles` exactly.
- **The vehicle must be loadable.** If the part Id does not exist when the craft loads, `Load` returns a
  partial or empty tree; keep the part Id stable.
- **Time stamps and versions** are informational; the version string is parsed leniently
  (`VersionInfo.Parse(..., throwExceptions: false)`).

## See also

- [Overview](overview.md), [Article 2](ship-ready-made-craft-2.md), [Article 3](ship-ready-made-craft-3.md)
- [ksacars/vehicle-library-presets](../../../../mods/ksacars/features/vehicle-library-presets.md)
- [Spawn a vehicle from code (from the vehicle library or a hand-built part)](../../vehicles/spawn-a-vehicle-at-runtime/overview.md), [Define a part with its own game data and GLB model](../define-a-part-with-game-data/overview.md)

[^ksa-constants]: current/decomp/KSA/Constants.cs (decompiled, build 2026.10.10.5554)
[^ksa-uncompressedvehiclesave]: current/decomp/KSA/UncompressedVehicleSave.cs (decompiled, build 2026.10.10.5554)
[^ksacars-install-testcraft]: KSACars: tools/install-testcraft.sh
[^ksa-program]: current/decomp/KSA/Program.cs (decompiled, build 2026.10.10.5554)
[^ksacars-craftspawner]: KSACars: src/KSACars/Ksa/CraftSpawner.cs
[^ksa-vehicletemplate]: current/decomp/KSA/VehicleTemplate.cs (decompiled, build 2026.10.10.5554)
[^content-meta]: current/Content/Core/defaultvehicles/Rocket/meta.toml (game content, build 2026.10.10.5554)
[^ksa-vehiclesaves]: current/decomp/KSA/VehicleSaves.cs (decompiled, build 2026.10.10.5554)
