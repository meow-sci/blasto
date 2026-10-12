# Game Feature

* [Cache and key per-body data so it survives system reloads, repeated ids and extra star systems](overview.md) - Derived body data is constant for a loaded system but a cache must die when the system is reloaded or a body object is replaced; five invalidation and key designs from reference latches to prefix-keyed JSON catalogs

# How-To

* [ConditionalWeakTable of immutable statics per Celestial](cache-and-key-body-data-per-loaded-system-2.md) - Cache each body's constant data in a ConditionalWeakTable keyed by the body instance so a system reload invalidates it automatically, and read only position and velocity per sample
* [Dotted path of Celestial ids from the top-level body down](cache-and-key-body-data-per-loaded-system-4.md) - Key mod data about bodies by a dotted path of Celestial ids such as Jupiter.Io, build it by walking Parent, and resolve the key back to the live object by matching the last segment and its parent depth-first under a root
* [Latch on the Universe.CurrentSystem reference, not its Id](cache-and-key-body-data-per-loaded-system-1.md) - Rebuild a per-system graph and every cache derived from it only when the CelestialSystem object changes, because reloading the same id creates new body objects
* [One-shot attach on the first UI frame with a universe](cache-and-key-body-data-per-loaded-system-3.md) - Match data loaded before any celestial exists to live bodies on the first UI frame where the universe is ready, compute derived numbers once and read only the live values per frame
* [Prefix-keyed JSON files discovered in every installed mod folder](cache-and-key-body-data-per-loaded-system-5.md) - Let any installed mod add or override per-body data by dropping a JSON file with a marker key and system-prefixed body keys beside it, with deterministic precedence and a lookup key order that survives repeated ids
