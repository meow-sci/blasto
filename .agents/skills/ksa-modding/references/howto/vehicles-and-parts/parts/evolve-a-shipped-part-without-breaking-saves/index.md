# Game Feature

* [Changing a shipped part without breaking saves](overview.md) - How KSA rebuilds a saved part from its current template and pairs saved subparts by position, why removing or reordering subparts crashes old saves, and the authoring rule plus a startup repair

# How-To

* [Evolve part XML with append-only subpart lists and fixed part Ids](evolve-a-shipped-part-without-breaking-saves-1.md) - The authoring rules that keep old saves and craft loading when a part changes, derived from how PartTree.Deserialize pairs saved subparts by position
* [Repair saves and library craft at StarMapAllModsLoaded](evolve-a-shipped-part-without-breaking-saves-2.md) - Trim surplus SubPartRef entries from universe.xml and vehicle.xml before any load is possible, with a backup, a pure tested repair function and an offline twin script
