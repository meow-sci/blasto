# Game Feature

* [Identify vessels and parts stably across saves](overview.md) - Which vessel and part identifiers survive a save and load and which do not, and four ways mods key their data - Vehicle.Id with rename and dispose hooks, display-name keys, a window layout that can only anchor on the vehicle id, and a structural part address with a topology hash

# How-To

* [Anchor a saved window layout on Vehicle.Id and accept best-effort part ids](identify-vessels-and-parts-across-saves-3.md) - Why a layout file can only store a vehicle id, a part id and a sub-part id, how the first match is chosen after a restart, and where this approach breaks - the counter-example that motivates structural part addresses
* [Key mod data on Vehicle.Id and follow renames and disposals with postfixes](identify-vessels-and-parts-across-saves-1.md) - Use Vehicle.Id as the per-craft key, move entries when SetName succeeds, drop them when Dispose runs so a recycled id cannot inherit another craft's data, and hook the bool Dispose overload
* [Key settings on the craft display name and warn when two craft share it](identify-vessels-and-parts-across-saves-2.md) - File per-craft settings under the Vehicle.Id display name with a launcher ordinal suffix, and report when a second craft carries a name already in use, which is the limit of name keys
* [Reference a specific part with tree paths and a topology hash, resolving to null instead of guessing](identify-vessels-and-parts-across-saves-4.md) - Capture a vehicle id, the index path through Part.TreeChildren and SubParts, the template id and a SHA-256 signature of the whole ordered tree, and resolve it only when the vehicle is unique, the hash matches and the template matches
