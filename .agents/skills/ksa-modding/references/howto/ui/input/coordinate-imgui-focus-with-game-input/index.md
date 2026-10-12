# Game Feature

* [Share the keyboard and mouse between ImGui, mod tools and the game](overview.md) - How KSA decides who gets a key or click - ImGui, the controller, the vehicle or a hotkey - and five ways mods read input without fighting the game

# How-To

* [Share input with the game: a FixedController subclass swallows OnKey, OnMouseButton and OnScroll](coordinate-imgui-focus-with-game-input-5.md) - Take keys, clicks and the wheel away from the game while a mod tool is active by subclassing the camera controller the engine asks before the vehicle
* [Share input with the game: gate on WantTextInput and WantCaptureMouse in the GUI hook](coordinate-imgui-focus-with-game-input-1.md) - Read keys and mouse through ImGui from a StarMap GUI hook, gated so a text field or a window under the cursor wins, accepting that the tool stops when the HUD is hidden
* [Share input with the game: ignore clicks ImGui owns and keep working with the HUD hidden](coordinate-imgui-focus-with-game-input-2.md) - Make world click tools skip any click ImGui wants, reserve modifier gestures, scope each click to one consumer, and run the same handler from the frame step when the UI pass is skipped
* [Share input with the game: NoNav and NoFocusOnAppearing windows that never take the keyboard](coordinate-imgui-focus-with-game-input-3.md) - Build a mod panel that cannot capture keyboard focus, so held flight keys keep working while the panel is open
* [Share input with the game: WantCaptureKeyboard drops key events; Vehicle.OnKey patches add new key meanings](coordinate-imgui-focus-with-game-input-4.md) - Understand how KSA drops and clears flight input while ImGui has the keyboard, and give a vehicle a key it does not normally hear with OnKey and ProcessInput patches
