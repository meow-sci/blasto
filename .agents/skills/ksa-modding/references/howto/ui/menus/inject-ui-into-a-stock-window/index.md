# Game Feature

* [Inject UI into the game's own windows and menus, including the orbit right-click menu](overview.md) - How KSA's stock settings, orbit context menu, transfer planner and burn editors are drawn, and four Harmony techniques for adding controls to them at an exact spot

# How-To

* [Add a block under the stock burn editors with Harmony postfixes](inject-ui-into-a-stock-window-4.md) - Postfix Burn.DrawBurnEditorWindowContent and BurnCanvasHost.Draw to draw extra controls after the stock content, choosing a gauge-styled panel when the canvas is docked and a plain ImGui window when it is detached
* [Add sections to the Settings > Mods page with a one-call transpiler](inject-ui-into-a-stock-window-1.md) - Insert a single call before the only ConsoleStyle.PopWidgetStyle in GameSettings.OnDrawUi so registered section delegates draw inside the stock settings body, and run them only while the private Mods tab is open
* [Add shortcuts to the orbit right-click menu with a transpiler anchored on UTF-8 label literals](inject-ui-into-a-stock-window-2.md) - Insert an Advanced Flight Computer submenu before the last EndPopup in BurnContextMenu.Draw and an extra item inside the stock At Periapsis and At Apoapsis submenus by reading the u8 label bytes out of the IL
* [Take over what CREATE does in the Transfer Planner with a same-signature call swap and a click gate](inject-ui-into-a-stock-window-3.md) - Insert controls before DrawCorrectionTransfer and PopWidgetStyle in TransferPlanner.DrawPlanWindow, replace the Burn.Create call with a static method of identical signature and filter the CREATE click with an injected bool-to-bool call
