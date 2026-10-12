# Game Feature

* [Persist ImGui window state and layouts](overview.md) - What Dear ImGui's ini remembers for a window and what it never does, then three ways to keep window state - a plain ImGui.Begin plus a saved open flag, a filtered ini section of your own, and named layouts of every live window applied on request

# How-To

* [Let imgui.ini restore window geometry with a plain ImGui.Begin and persist the open flag yourself](persist-imgui-window-state-1.md) - Bypass the stock window helpers that force NoSavedSettings, set the default size with FirstUseEver so Dear ImGui's ini remembers position and size, and compare the open flag every frame so the ModMenu toggle and the close button both reach your own settings file
* [Persist only your own ImGui window section with SaveIniSettingsToMemory and LoadIniSettingsFromMemory](persist-imgui-window-state-2.md) - Capture the live ImGui ini text, keep just the [Window] section of the mod's own window, store it with open-header state in the mod's data folder, restore it once at AllModsLoaded when the context already exists, and save on a timer, on demand and at unload
* [Save every live window as a named layout in one TOML file and apply it only on request](persist-imgui-window-state-3.md) - Observe each window's last position and size every frame, capture all live windows and in-world panels into one hand-emitted TOML file per layout written atomically, and recreate them when the player asks, skipping name collisions and clamping saved geometry into the current work area
