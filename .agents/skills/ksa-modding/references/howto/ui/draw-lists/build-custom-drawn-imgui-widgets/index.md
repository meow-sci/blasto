# Game Feature

* [Build custom-drawn ImGui widgets, icons, trees and alert tiles on the draw list](overview.md) - Laying out rows, tiles, gauges, toggles, icons, glyphs, trees and rule-driven alert tiles yourself with ImDrawList and InvisibleButton hit-testing instead of stock widgets or textures

# How-To

* [Build a draw-list widget kit: rows, gauges, toggles and sections with a runtime-editable theme](build-custom-drawn-imgui-widgets-1.md) - Write a small PanelKit whose widgets take a draw list, an origin, a width and a running y, hit-test with InvisibleButton, reserve size with Dummy and read colours from public static theme fields
* [Draw icon buttons from primitives, with no textures or icon font](build-custom-drawn-imgui-widgets-2.md) - Keep a stock ImGui Button for hit testing and style, then draw a shield, camera, flag or missile on top of it with the window draw list inside GetItemRectMin and GetItemRectMax
* [Evaluate threshold rules once per frame and render them as banner, tile outline, warning row and graph line](build-custom-drawn-imgui-widgets-5.md) - Keep a fixed table of enable-able value-above-or-below rules, evaluate it once against a shared context, and let every widget ask whether a metric is triggered instead of recomputing
* [Share one glyph function between a canvas and its legend](build-custom-drawn-imgui-widgets-3.md) - Draw state symbols as static draw-list methods that take only a screen point, a radius and two colours, and call the same methods from the map and from a legend of fixed-size Dummy cells
* [Write a generic tree view with static abstract adapters, ChannelsSplit and ImGui storage](build-custom-drawn-imgui-widgets-4.md) - Build an ImGuiTreeView of N and A where adapters are static abstract, selection fills draw on channel 0 beneath labels on channel 1, and open state lives in ImGui storage keyed by node id
