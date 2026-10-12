# Game Feature

* [Giving inert parts behaviour by recognising their part Id](overview.md) - How a mod finds capability on a part with no module: a data-driven registry of profiles keyed by part Id, a per-craft survey and a roster keyed by Part

# How-To

* [Decide the dispenser kind by which part is fitted, and simulate flares and chaff as decoys](give-inert-parts-behaviour-by-part-id-2.md) - Recognise dispenser parts by Id on any craft, keep a magazine per Part, queue salvos on simulated time and step mod-side decoys that seduce seekers and break radar tracks
* [Feed the registry from a KSArmory folder in every installed mod](give-inert-parts-behaviour-by-part-id-3.md) - Scan a convention folder in every manifest mod for XML definitions, read them with a strict schema-gated reader and audit them against the part library after load
* [Weapon registry keyed by part Id, with profiles in a game-free layer](give-inert-parts-behaviour-by-part-id-1.md) - Describe parts as profile objects keyed by Id, freeze a registry after load, find them on every craft by exact Id and keep each system keyed by its Part reference
