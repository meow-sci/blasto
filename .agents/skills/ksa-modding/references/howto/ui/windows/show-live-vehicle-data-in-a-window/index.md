# Game Feature

* [Show live vehicle and editor data in an ImGui window or HUD](overview.md) - Where the controlled vehicle's settled physics, orbit and engine state (or the editor's part tree) can be read safely, and three ways to present it - a StarMap GUI hook, a postfix on Vehicle.OnDrawUi, a postfix on VehicleEditor.OnDrawUi

# How-To

* [Live vehicle data: a plain ImGui window from StarMapAfterGui with guarded Begin and End](show-live-vehicle-data-in-a-window-1.md) - Read the controlled vehicle's settled physics state by ref readonly and show derived aerodynamic numbers in a small auto-sized window that cannot corrupt the ImGui stack when something throws
* [Live vehicle data: Harmony postfix on Vehicle.OnDrawUi filtered to the main viewport](show-live-vehicle-data-in-a-window-2.md) - Hook the game's own per-vehicle UI call instead of a StarMap attribute and build one per-frame data record from orbit, live nozzle thrust and mass for a HUD window
* [Live vehicle data: Harmony postfix on VehicleEditor.OnDrawUi for the part tree being built](show-live-vehicle-data-in-a-window-3.md) - Add a panel to the vehicle editor by postfixing the editor's own UI method and reading the craft's PartTree for dry, propellant and wet mass, since the editor has no StarMap hook and no flight state
