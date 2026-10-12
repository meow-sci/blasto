---
type: Mod Feature
title: Vehicle library presets
description: Install each car as a ready-to-launch whole craft by writing a vehicle.xml with a single RootPartRef and a meta.toml into the player's Vehicles library, mirroring Core's default vehicles
mod: ksacars
category: vehicles-and-parts
techniques: [xml-asset]
hooks: []
lifecycle: []
game_types:
  - KSA.VehicleSaveData
  - KSA.VehicleSaves
  - KSA.DefaultVehicleSaves
  - KSA.PartInstance
  - KSA.VehicleTemplate
tags: [vehicles, craft-files, vehicle-library, presets, tooling, xml]
---

Each KSACars car is a single part that is also a complete craft (it has a node connector, a
`<Control />` command source and seats). To let a player launch one straight from the vehicle list
without opening the editor, the repo's `tools/install-testcraft.sh` writes a one-part craft for each
car into the player's vehicle library, in exactly the format of Core's shipped default
vehicles.[^ksacars-installtestcraft] The README tells players to find the cars under *Vehicles* in
the editor or launch them from the vehicle list.[^ksacars-readme]

## How it works

1. The script locates the KSA user folder (`My Games/Kitten Space Agency`) under `/mnt/c/Users`
   (a WSL developer setup) and picks `vehicles` or `Vehicles`, whichever exists.
2. For each car it creates `<Vehicles>/<Name>/` with two files:
   - `meta.toml`: `name`, `created`, `updated`, `version`, `systems = [ "Sol", ]`, matching the keys
     of Core's `defaultvehicles/*/meta.toml`.[^ksa-banjo-meta]
   - `vehicle.xml`: `<VehicleSaveData Id="<Name>" ActiveSequence="0">` with one
     `<RootPartRef InstanceOf="KSACars_Prefab_Buggy" LocalInstanceId="1" Stage="0">` and an identity
     `<Transform>`.
3. KSA's library (`VehicleSaves.SaveFolderPath = <Documents>/Vehicles`) lists every folder with a
   `vehicle.xml`; `VehicleSaveData` maps `RootPartRef` to a `PartInstance`, the same type the part
   XML uses, so the craft is the root part plus whatever subparts its template declares.[^ksa-vehiclesaves][^ksa-vehiclesavedata]

The script's craft files carry no `<SubPartRef>` (Core's Banjo does, for its tank contents), so
every subpart comes from the current part template on load and the files cannot fall out of step
with the subpart list the way saves can.

The [command bridge](./file-drop-command-bridge.md) reuses the library: `CraftSpawner` parks a
craft from it at a latitude and longitude for automated tests.[^ksacars-craftspawner]

## Lifecycle and timing

None in game: this is an install-time script run by a developer (`lifecycle: []`). KSA reads the
library when the vehicle list is refreshed (`VehicleSaves.Refresh`) and when a craft is launched.

## Key code

| Path | Role |
|---|---|
| [tools/install-testcraft.sh](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/install-testcraft.sh) | Writes both cars' `meta.toml` and `vehicle.xml` |
| [src/KSACars/Ksa/CraftSpawner.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/CraftSpawner.cs) | Bridge-side consumer of library craft |

## Snippets

```bash
# tools/install-testcraft.sh L30-L57
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

## Game APIs touched

| API | Purpose |
|---|---|
| `VehicleSaveData` ([current/decomp/KSA/VehicleSaveData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaveData.cs#L8-L28)) | `RootPartRef` (`PartInstance`), `Character`, `ActiveSequence`, `vehicle.xml` |
| `VehicleSaves.SaveFolderPath` ([current/decomp/KSA/VehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaves.cs#L157-L167)) | `<Documents>/Vehicles`, the player's library |
| `DefaultVehicleSaves.SaveFolderPath` ([current/decomp/KSA/DefaultVehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DefaultVehicleSaves.cs#L12-L24)) | `Content/Core/defaultvehicles`, hard-coded to Core[^ksa-defaultvehiclesaves] |
| Core `defaultvehicles/Banjo/vehicle.xml` ([current/Content/Core/defaultvehicles/Banjo/vehicle.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/defaultvehicles/Banjo/vehicle.xml)) | The template the script mirrors[^ksa-banjo-vehicle] |
| `AssetBundle` `<Vehicle>` element (`VehicleTemplate`) ([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L12)) | A different, system-placement vehicle asset; not used by KSACars[^ksa-assetbundle] |

## Pitfalls and version notes

- **A mod folder cannot add default vehicles.** `DefaultVehicleSaves` reads only
  `Content/Core/defaultvehicles`, so presets go into the player's own library (or would need a
  patch). KSACars does this with a developer script, not at runtime; players launch from the
  editor's *Vehicles* category instead.
- **The script assumes WSL** (`/mnt/c/Users/...`). On native Windows or Linux point it at the KSA
  user directory yourself (`tools/ksa-user-dir.sh` prints it).
- **The craft needs a command source** in the part (`<Control />`), otherwise the one-part craft is
  not controllable.
- `version = "KSACars-mod"` in `meta.toml` is a free string; KSA writes its own build there.
- The `<Vehicle>` asset element (`VehicleTemplate`, with `SituationRef`, `Body2Cce`, `BodyRates`)
  is how systems place vehicles at scenario start (`ConfigOnStartPopup`); it is not a library preset.

## See also

- [Car part game data](./car-part-gamedata.md)
- [File-drop command bridge](./file-drop-command-bridge.md)
- [Spawn a saved craft parked at a latitude and longitude](./spawn-parked-craft.md)
- [Package, deploy and release](./package-deploy-release.md)
- [Ship a preset craft as VehicleSaveData files in the vehicle library](../../../howto/vehicles-and-parts/parts/ship-ready-made-craft/ship-ready-made-craft-1.md) - how-to article demonstrating this feature

[^ksacars-installtestcraft]: tools/install-testcraft.sh
[^ksacars-readme]: README.md
[^ksacars-craftspawner]: src/KSACars/Ksa/CraftSpawner.cs
[^ksa-vehiclesavedata]: KSA.VehicleSaveData (decompiled)
[^ksa-vehiclesaves]: KSA.VehicleSaves (decompiled)
[^ksa-defaultvehiclesaves]: KSA.DefaultVehicleSaves (decompiled)
[^ksa-banjo-vehicle]: Core defaultvehicles/Banjo/vehicle.xml
[^ksa-banjo-meta]: Core defaultvehicles/Banjo/meta.toml
[^ksa-assetbundle]: KSA.AssetBundle (decompiled)
