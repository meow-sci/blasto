# Game Feature

* [List a mod in ModMenu with the ModMenuEntry attribute](overview.md) - How a KSA mod gets an entry in the ModMenu community mod by tagging a static method with ModMenuEntry, and the four ways mods obtain or avoid the attribute type

# How-To

* [Copy the ModMenuEntry attribute into your mod so there is no dependency and no extra DLL](list-a-mod-in-modmenu-3.md) - Declare your own ModMenuEntryAttribute with the same type name and constructor shape, tag a static method with it and guard the method because ModMenu calls it from inside its own menu build
* [Draw a ModMenu page without referencing the game: DynamicMethod wrappers for ImGui.MenuItem and SetItemTooltip](list-a-mod-in-modmenu-4.md) - Resolve Brutal.ImGuiApi.ImGui by name, emit one DynamicMethod per call shape that converts string to the ref struct ImString on the evaluation stack, and keep the page fail-soft with a hand-parsed settings file
* [Reference ModMenu.Attributes, tag a static method and ship the DLL beside your mod](list-a-mod-in-modmenu-1.md) - Add the ModMenu.Attributes assembly to your project, put ModMenuEntry on a static method that draws menu items, toggle your window from it and copy the DLL into the mod folder
* [Vendor ModMenu.Attributes.dll in your repository and let StarMap's per-mod resolver find it](list-a-mod-in-modmenu-2.md) - Commit the attribute DLL, reference it by HintPath without Private=False, copy it to the output folder and understand how StarMap resolves it at run time
