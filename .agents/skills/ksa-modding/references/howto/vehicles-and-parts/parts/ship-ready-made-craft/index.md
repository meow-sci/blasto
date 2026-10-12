# Game Feature

* [Shipping whole-vehicle presets and single-part craft with a part mod](overview.md) - How KSA's two vehicle libraries store craft as VehicleSaveData files, and how to make a single part a complete craft that players can launch

# How-To

* [Make a furniture part a one-part craft that is never built upon](ship-ready-made-craft-3.md) - Apply the root-part rule to props, with a Control module only where the prop is flown, and place them from code without the editor
* [Make a single part a whole craft with a RootPartWhitelist tag](ship-ready-made-craft-2.md) - Give a part an editor tag with RootPartWhitelist, a node connector with no surface flag and a Control module so it can start and fly a craft by itself
* [Ship a preset craft as VehicleSaveData files in the vehicle library](ship-ready-made-craft-1.md) - Write the meta.toml and vehicle.xml pair that makes a one-part craft appear in the player's vehicle list, from an install script or from mod code at startup
