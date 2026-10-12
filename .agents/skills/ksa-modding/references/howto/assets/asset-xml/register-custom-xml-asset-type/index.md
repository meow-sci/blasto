# Game Feature

* [Register a custom XML asset type or inject new child elements into game XML](overview.md) - How KittenExtensions teaches the game's XmlSerializers new elements from attributes in a mod assembly, and three ways mods use it to ship shader assets and data profiles

# How-To

* [Add an XML element with KxAsset and inject child elements with KxAssetInject](register-custom-xml-asset-type-1.md) - Copy two attribute classes into your assembly, tag a SerializedId subclass with KxAsset and KittenExtensions teaches the game's XmlSerializers the new element; KxAssetInject adds a child element type to any public list member of game XML
* [Ship a data-only profile type that loads from ordinary asset XML](register-custom-xml-asset-type-3.md) - Subclass SerializedId with public fields as the schema, register it in a static set in OnDataLoad, and trim defaults so an exported profile is compact XML to paste back into an Assets file
* [Ship shader asset elements through KxAsset and a copied injector](register-custom-xml-asset-type-2.md) - Tag four classes with KxAsset so ShaderEx, ImGuiShader, PostProcessingShader and GlobalPostShader become Assets elements, and copy KittenExtensions' injector into your own assembly to add child elements and swap the save serializers
