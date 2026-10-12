# Game Feature

* [Tweak layout and animation constants live in the running game](overview.md) - Reflect public static fields into ImGui sliders, persist only the values that differ from the defaults and dump the result as C# source to paste back into the code

# How-To

* [Edit static constants with reflected sliders, keep only the overrides and copy them back as source](live-tweak-constants-in-game.md) - Declare layout and animation numbers as public static fields, reflect them at type initialisation, bind ImGui sliders by ref and persist or dump only the values that differ from the defaults
