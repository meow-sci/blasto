# Game Feature

* [Show transient alerts and game-styled popups in game](overview.md) - How KSA draws its centred timed alerts and its console-styled Popup modals, and two ways mods use them - TimedAlert from main-thread hooks and a Popup subclass that registers itself

# How-To

* [Alerts: TimedAlert.Create and CreateWarning from main-thread hooks](show-alerts-and-popups-in-game-1.md) - Raise centred, fading messages that look and stack exactly like the game's own alerts by calling TimedAlert from a main-thread hook, while keeping the call off worker threads
* [Popups: a Popup subclass that registers itself and is drawn by Popup.DrawAll](show-alerts-and-popups-in-game-2.md) - Show a console-styled modal with Yes/No or three-way buttons by subclassing the game's abstract Popup and constructing it, since the constructor registers the instance for Popup.DrawAll
