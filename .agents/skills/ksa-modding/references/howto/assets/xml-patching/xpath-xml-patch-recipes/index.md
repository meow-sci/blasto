# Game Feature

* [Write XPath patch operations for game XML](overview.md) - The ModuleManager-style patch operations KittenExtensions runs over XPath 1.0 paths, with worked recipes for appending a gauge button, adding missing elements to bodies and rewriting values with conditionals

# How-To

* [Add missing mass and sphere-of-influence elements to a kind of body](xpath-xml-patch-recipes-2.md) - Select bodies by element name and Id with an XPath and append inline Mass and SphereOfInfluence elements so code that needs them stops skipping the body
* [Append a button to a stock gauge canvas with Copy and inline XML](xpath-xml-patch-recipes-1.md) - Target a stock gauge canvas by Id with an XPath, append a new Button element with Copy Pos="Append" and inline XML, and pick a canvas that is actually visible when you need it
* [Rewrite many nodes with With, If and SetVar, and combine trees with Merge](xpath-xml-patch-recipes-3.md) - Use With to run operations once per selected node, If to branch on an XPath result, SetVar to keep values, and Merge with _MergeId and _MergePos to combine elements without listing every child
