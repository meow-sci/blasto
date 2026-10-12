---
type: Submod
title: jplrepo
description: A tiny development tribute mod that animates cheer phrases on KSA's top menu bar using a call-site transpiler on Program.DrawMenuBar and a prefix on DrawProgramMenusHook
mod: unscience
submod: jplrepo
tags: [menu-bar, imgui, transpiler]
mod_id: jplrepo
---

`jplrepo` cycles three colored "JPLRepo!" cheer phrases as pseudo-menus on the game's top menu bar,
sliding word by word between phrases. It is a development reference that the supermod does not load,
but it demonstrates capturing layout state from inside a game method with a transpiler and drawing
from the public menu hook.

## Hosting

- Manifest id `jplrepo`, entry assembly `MeowSci.JplRepo`; standalone only (not in `unscience`).
- `jplrepo/Mod.cs` patches at `[StarMapAllModsLoaded]` and unpatches at `[StarMapUnload]`; the GUI
  attributes are empty. `Patcher.cs` installs the transpiler, the hook prefix and `HotkeyGuard`.

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [Animated menu-bar text via a call-site transpiler and a hook prefix](../features/jplrepo-menu-bar-animation.md) | ui | `frame:draw-ui` | A transpiler on Program.DrawMenuBar injects a cursor snapshot just before the version-string SetCursorPosY call, and a prefix on DrawProgramMenusHook restores that cursor and draws colored, sliding menu-bar words |

## Scene saves and persistence

None.

## Notes

`jplrepo/README.md` is a renamed copy of the template README and does not describe this mod.

## Further reading

- [jplrepo/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/jplrepo/README.md)
- [scope/FULL_SCOPE.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/FULL_SCOPE.md)

## Entry points

- [jplrepo/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/jplrepo/mod.toml)
- [jplrepo/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/jplrepo/Mod.cs)
- [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs)

## See also

- [unscience overview](../overview.md)
- [Unscience menu-bar entry](../features/unscience-menu-bar-entry.md)
