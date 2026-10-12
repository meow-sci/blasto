# Game Feature

* [Build searchable lists, two-pane browsers and file pickers in ImGui](overview.md) - How Brutal's ImInputString behaves in filter boxes, and six ways mods build searchable combos, ranked search lists, two-pane browsers and file pickers

# How-To

* [Searchable lists: a draggable splitter between two panes, a universe guard and a go-to button](build-searchable-lists-and-browsers-5.md) - Build a two-pane browser whose splitter is a thin button moved by MouseDelta, wait for a loaded universe before reading it, and move the camera to the selected body with Universe.MoveCameraTo
* [Searchable lists: a file browser that copies picks into a flat shared catalogue](build-searchable-lists-and-browsers-6.md) - Let players pick files from disk in an ImGui browser window and always copy the chosen file into a flat, non-overwriting catalogue folder before handing back a catalogue name
* [Searchable lists: a filterable colour catalogue list](build-searchable-lists-and-browsers-4.md) - Serve a cached, name-sorted array of colours to a combo with a clearing filter box, so a picker with nearly a thousand entries stays usable
* [Searchable lists: a live substring filter inside a combo, with ImInputString length evaluated](build-searchable-lists-and-browsers-1.md) - Put a text filter at the top of a BeginCombo popup that narrows the items as the player types, and work around Brutal's ImInputString length caching by evaluating the length every frame
* [Searchable lists: InputTextWithHint with ranked results and reveal-on-pick](build-searchable-lists-and-browsers-3.md) - Search every body in every star system as the player types, rank exact then prefix then substring matches, cap the list and act on a pick only after the child region closes
* [Searchable lists: SetKeyboardFocusHere on IsWindowAppearing and SetItemDefaultFocus](build-searchable-lists-and-browsers-2.md) - Make a search-as-you-type star-system combo that focuses its filter box the moment the popup opens and scrolls to the current entry, using a plain InputText whose result is read as a string
