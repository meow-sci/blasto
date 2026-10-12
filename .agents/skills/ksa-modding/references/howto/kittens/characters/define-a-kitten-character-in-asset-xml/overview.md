---
type: Game Feature
title: Read and extend the character asset XML (characters, fur, expressions, attachments)
description: How CharacterAssets.xml composes a Character from shared core, textures, fur, expression, animation and attachment blocks, how the game registers, lists and renders it, and what no reference mod has tested
category: kittens
group: characters
topic: define-a-kitten-character-in-asset-xml
demonstrated_by:
  - mods/unscience/features/doh-kitten-spawning
  - mods/unscience/features/kitten-animations-clip-catalog
  - mods/ksagolf/features/kitten-held-props
lifecycle: [startup:asset-load]
game_types:
  - KSA.CharacterReference
  - KSA.CharacterCoreReference
  - KSA.CharacterAttachmentReference
  - KSA.CharacterTexturesReference
  - KSA.CharacterFurReference
  - KSA.CharacterExpressionsReference
  - KSA.CharacterGroundAnimationsReference
  - KSA.ModLibrary
  - KSA.CharacterAvatar
  - KSA.KittenRosterData
tags: [kitten, character, asset-xml, xml-asset, fur, attachments, expressions]
---
Every kitten the game draws is described by a `Character` element in asset XML. The element does not hold data itself: it points, by `Id`, at a set of shared blocks (the body and skeleton, the textures, the fur, the expression clips, the ground and MMU animation sets, the ear mask, the helmet, visor and MMU attachments) and the game composes a `CharacterAvatar` from them at runtime. This topic is a reference to those elements, grounded in the stock `CharacterAssets.xml` and the loader classes. It is engine-only: **no reference mod declares a new `Character`**, so everything about extending it is read from the code and untested in a running game. The three stock characters (`HunterKitten`, `BanjoKitten`, `PolarisKitten`) differ only in personality and textures.

## Engine background

| Type | Role | Source |
|---|---|---|
| `AssetBundle.Assets` | The list that maps the element names `Character`, `CharacterCore`, `CharacterAttachment`, `CharacterFur`, `CharacterExpressions`, `CharacterMMUAnimations`, `CharacterAnimationMasks`, `CharacterGroundAnimations`, `CharacterTextures` (and `GltfFile`, `PbrMaterial`, `Texture`) to classes | [current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L43-L51) |
| `CharacterReference` | The `Character` element; `OnDataLoad` loads every child block and registers the character | [current/decomp/KSA/CharacterReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterReference.cs#L7-L89) |
| `Character*Reference` blocks | One class per block, each registers itself in a `ModLibrary.AllCharacter*` collection when it is a definition rather than an `Id` reference | [current/decomp/KSA/CharacterCoreReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterCoreReference.cs), [current/decomp/KSA/CharacterAttachmentReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAttachmentReference.cs) |
| `ModLibrary.AllCharacters` | `internal static readonly SerializedCollection<CharacterReference>` that the roster and spawners pick from | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L100-L116) |
| `CharacterAvatar` | Built from a `CharacterReference` by `InitalizeFromCharacterRef`; gives the runtime structs | [current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L344-L445) |
| `KittenRosterData` | The roster: three fixed kittens, the rest filled with a random registered character | [current/decomp/KSA/KittenRosterData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRosterData.cs#L24-L47) |

Two loader rules explain how the XML is written:

- **Definition or reference.** Each block decides what it is from its own content: `CharacterCore` is a reference when it has no `BodySource`, `CharacterFur` when it has no `FurMeshIndex`, `CharacterExpressions` when it has no `ExpressionCenterIndex`, `CharacterAttachment` when it has no `Source`, `CharacterGroundAnimations` when it has no `AnimIdle`, `CharacterTextures` when none of the three materials is set. A reference (`<CharacterCore Id="CharacterCore"/>`) is looked up in the registered collection later by `Get()`; a definition registers itself. `CharacterReference` registers only when its resolved core has a `BodySource` ([current/decomp/KSA/CharacterReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterReference.cs#L65-L89)). Order therefore matters: a block must be loaded before the `Character` that references it ([mod XML load order](../../../assets/asset-xml/mod-xml-load-order/overview.md)).
- **Bones are named.** `BoneReference` (used for eyes, sockets, the expression centre, ear masks) resolves `Name` case-insensitively against the skeleton's `BoneNames` and falls back to `Index`; a name that is not on the skeleton and has no `Index` throws ([current/decomp/KSA/SubAssetReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SubAssetReference.cs)).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. Compose a Character from the shared asset blocks](define-a-kitten-character-in-asset-xml.md) | unscience, KSAGolf (read-only evidence) | `xml-asset` | `startup:asset-load` | Engine XML plus three mods that read the loaded result back; a new `Character` is untested |

## See also

- [Asset XML element reference](../../../assets/asset-xml/asset-xml-element-reference/overview.md), [Mod XML load order](../../../assets/asset-xml/mod-xml-load-order/overview.md)
- [Reach a kitten's avatar, skeleton, bones and animation catalog](../reach-a-kittens-avatar-and-skeleton/overview.md), [Spawn an EVA kitten from code](../spawn-an-eva-kitten-from-code/overview.md)
- [Attach a prop to a kitten's hand or another bone at runtime](../../appearance/attach-a-prop-to-a-kitten-bone/overview.md), [Make a kitten pull a facial expression](../../animation/make-a-kitten-pull-a-facial-expression/overview.md)
- [Declare post-processing shader assets in XML](../../../shaders/post-processing/declare-post-processing-shader-assets-in-xml/overview.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
