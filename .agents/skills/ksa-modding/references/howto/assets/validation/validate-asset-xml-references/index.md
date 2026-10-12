# Game Feature

* [Validate asset XML ids and paths before launching the game (offline)](overview.md) - Why bad asset XML fails silently in KSA and three levels of validator, from a well-formedness and reference script to extended generated-geometry checks to a game-serializer parse for runtime loaders

# How-To

* [Check asset XML ids and paths offline with check-xml.sh and validate-parts.py](validate-asset-xml-references-1.md) - A well-formedness check and a reference validator that cross-check SubPart InstanceOf, Material and Mesh ids against the mod's own files, the atlas mesh names and Core's library, with case-exact paths and an offline mode, run from the deploy script and CI
* [Extend the validator for generated geometry, ids named in C# and shared planes](validate-asset-xml-references-2.md) - Add checks for XML not listed in mod.toml, ids declared twice including atlas mesh names, launch geometry disagreeing with a generated report, textures shared between materials, ids named as strings in C#, unsized stacking connectors and planes shared between subparts
* [Parse an AssetBundle with the game serializer, without OnDataLoad, and apply pure rules](validate-asset-xml-references-3.md) - Fetch the AssetBundle serializer from XmlHelper.Serializers, parse an XDocument with line info, never call OnDataLoad, and hand the object tree to isolated validation rules so a runtime loader can reject a document set before anything is registered
