# Game Feature

* [Read typed text, key chords and the clipboard from ImGui](overview.md) - How text, chords and clipboard reach a mod through ImGui's IO and the GLFW-backed platform clipboard, and two mods that use them

# How-To

* [Copy to the clipboard from ImGui: selectable rows that build text only on click](read-typed-text-and-key-chords-from-imgui-2.md) - Copy a list row to the system clipboard with ImGui.SetClipboardText, building the text only when the row is clicked and showing a hover tooltip
* [Read typed text and key chords from ImGui: character queue versus IsKeyPressed, with clipboard paste chords](read-typed-text-and-key-chords-from-imgui-1.md) - Read printable text from ImGui's character queue, Ctrl and Alt chords from key presses, handle AltGr, and copy or paste through ImGui's GLFW-backed clipboard
