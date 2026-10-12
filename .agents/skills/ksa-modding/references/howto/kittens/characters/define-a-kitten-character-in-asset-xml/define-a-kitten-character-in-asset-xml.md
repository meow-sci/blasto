---
type: How-To
title: Compose a Character from the shared asset blocks
description: A walk through CharacterAssets.xml element by element - GltfFile clips, textures, core, fur, expressions, animation sets, masks, attachments and the Character that ties them together - with the code that loads each and the evidence three mods give for what the loaded result looks like
category: kittens
group: characters
topic: define-a-kitten-character-in-asset-xml
difficulty: advanced
prerequisites: [starmap, asset-xml]
demonstrated_by:
  - mods/unscience/features/doh-kitten-spawning
  - mods/unscience/features/kitten-animations-clip-catalog
  - mods/ksagolf/features/kitten-held-props
techniques: [xml-asset]
hooks: [xml-asset Character, reflection KSA.ModLibrary.AllCharacters]
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
tags: [kitten, character, asset-xml, fur, expressions, attachments, reference]
---
This article explains every element a kitten `Character` is built from, in the order the stock file declares them, and how the game turns the result into a `CharacterAvatar`. Everything about the XML comes from the stock file and the loader classes. **No reference mod declares a `Character`**, so the "define your own" section at the end is adapted from the stock XML and the loading code and has not been run in a game. Three mods supply indirect evidence: unscience's doh lists the registered characters by reflection and spawns kittens with them, its clip catalog reads the loaded ground animation set back, and KSAGolf deliberately avoids declaring a `Character` and adds a runtime `CosmeticAttachment` instead (for reasons given at the end). Prerequisites: a StarMap or content mod that ships an asset XML file ([mod.toml reference](../../../getting-started/loader/mod-toml-reference/overview.md)).

## Background: how the game does it

- The asset XML root is `<Assets>`; its children are mapped by name to classes in `AssetBundle.Assets` ([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L43-L51)). All types derive from `SerializedId`, whose `Id` attribute is the key other elements refer to ([current/decomp/KSA/SerializedId.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedId.cs)).
- `CharacterReference.OnDataLoad` loads each child block and registers the character in `ModLibrary.AllCharacters` when its core resolves to a body source ([current/decomp/KSA/CharacterReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterReference.cs#L65-L89)). `ModLibrary.Get<CharacterReference>(id)` is what `KittenEva`, `KittenRenderable` and `CharacterAvatar` call to turn a saved character id into data ([current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L66-L110), [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L136-L141)).
- `CharacterAvatar.InitalizeFromCharacterRef` is the single consumer of the loaded blocks. It dereferences most children without a null check (for example `CharacterTextures.Get().CharacterBodyMaterial`, `CharacterHelmet.Get().Socket`), so treat every child of `Character` below as required ([current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L344-L445)).
- The roster holds a character id per kitten: `KittenRosterData.Create` seeds `Hunter`, `Banjo`, `Polaris` with `HunterKitten`, `BanjoKitten`, `PolarisKitten` and fills the remaining 17 slots with a random entry of `AllCharacters` ([current/decomp/KSA/KittenRosterData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRosterData.cs#L24-L47), [current/decomp/KSA/KittenRosterData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRosterData.cs#L104-L108)). The id string is saved in the roster and in every kitten's vehicle save ([current/decomp/KSA/KittenRosterEntryData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRosterEntryData.cs), [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L1080-L1083)).

## Lifecycle and timing

Character XML is data. It is parsed with the rest of the mod's asset bundles during `ModLibrary.PreloadAssetBundles` / `LoadAll`, on the asset-loading path (`startup:asset-load`), before any scene exists ([load order](../../../assets/asset-xml/mod-xml-load-order/overview.md), [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)). `CharacterAvatar` is built later, per kitten, when a `KittenRenderable` is constructed (`frame:render` / spawn time). Consequences:

- A character that fails to resolve fails at asset load or, worse, at the first kitten spawn that picks it (a throw in the avatar constructor).
- Reading the registered list (doh) is possible from `[StarMapAllModsLoaded]` or later; before `LoadAll` finishes the collection is incomplete.
- `AllCharacters` is `internal`, so a mod reads it by reflection (see the doh evidence below).

## Steps

1. **Declare the glTF assets.** The body, helmet, visor and MMU are `GltfFile` elements with a `Source Path`; every animation clip is a `GltfFile` that names the body as its `Skeleton`, so the clip's channels bind to the body's bones. Two clips carry a `Usage` that marks them as the blink and helmet-ear mask (`Gltf2Usage` has exactly those two values):

```xml
<!-- current/Content/Core/CharacterAssets.xml L3-L17 -->
    <GltfFile Id="KittenGlb">
        <Source Path="Characters/Kitten/KSA_Cat.gltf"/>
    </GltfFile>
    
    <GltfFile Id="KittenHelmetGlb">
        <Source Path="Characters/KittenHelmet/KSA_Cat_Helmet.gltf"/>
    </GltfFile>

    <GltfFile Id="KittenVisorGlb">
        <Source Path="Characters/KittenVisor/KSA_Cat_Visor.gltf"/>
    </GltfFile>

    <GltfFile Id="KittenMMUGlb">
        <Source Path="Characters/KittenMMU/SK_KSA_MMU.glb"/>
    </GltfFile>
```

```xml
<!-- current/Content/Core/CharacterAssets.xml L21-L24, L157-L160, L164-L173 -->
    <GltfFile Id="KittenAnimIdleGlb">
        <Source Path="Characters/KSA_Cat.Anim.Idle.glb"/>
        <Skeleton Id="KittenGlb"/>
    </GltfFile>
    <!-- ... -->
    <GltfFile Id="KSA_MMU_Anim_ArmRetractGlb">
        <Source Path="Characters/KittenMMU/ANI_VEH_KSA_MMU_ArmRetract.glb"/>
        <Skeleton Id="KittenMMUGlb"/>
    </GltfFile>
    <!-- ... -->
    <GltfFile Id="KSA_Cat_Anim_BlinkGlb">
        <Source Path="Characters/KSA_Cat.Anim.Blink.glb"/>
        <Skeleton Id="KittenGlb"/>
        <Usage>BlinkAnimation</Usage>
    </GltfFile>
    <GltfFile Id="KittenAnimHelmetMaskGlb">
        <Source Path="Characters/KSA_Cat.Anim.MaskHelmet.glb"/>
        <Skeleton Id="KittenGlb"/>
        <Usage>HelmetMaskAnimation</Usage>
    </GltfFile>
```

   The MMU arm-retract clip plays on the MMU attachment's own skeleton, not the kitten's. `Gltf2Reference` fields are listed in [current/decomp/KSA/Gltf2Reference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Gltf2Reference.cs#L1-L40); where `Path` is resolved from is covered in [resolve asset ids and mod-relative paths](../../../assets/asset-xml/resolve-asset-ids-and-mod-relative-paths/overview.md).

2. **Declare textures and the textures block.** `PbrMaterial` elements hold `Diffuse`, `Normal` and `AoRoughMetal`; `CharacterTextures` picks a body, head and eye material. The head and eye vary per stock character (Bengal, Siamese, Tuxedo); the body is shared:

```xml
<!-- current/Content/Core/CharacterAssets.xml L218-L228 -->
    <CharacterTextures Id="CharacterTextures">
        <CharacterBodyMaterial Id="CharacterBody"/>
        <CharacterHeadMaterial Id="CharacterHead"/>
        <CharacterEyeMaterial Id="CharacterEyeGreen"/>
    </CharacterTextures>

    <CharacterTextures Id="CharacterTextures_Siamese">
        <CharacterBodyMaterial Id="CharacterBody"/>
        <CharacterHeadMaterial Id="CharacterHead_Siamese"/>
        <CharacterEyeMaterial Id="CharacterEyeBlue"/>
    </CharacterTextures>
```

   `CharacterAvatar` writes the materials into slots of the body glTF's material array by fixed index: eye at 0, head at 1 and 3, body at 2 ([current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L377-L382)). A replacement body glTF must keep that mesh and material order. `CharacterTexturesReference` also has an optional `CharacterFurTexture` ([current/decomp/KSA/CharacterTexturesReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterTexturesReference.cs#L1-L20)).

3. **Declare the core.** `BodySource` names the skinned glTF; `LeftEyeBoneIndex` and `RightEyeBoneIndex` are bone references by name; `MaxBoneCount` sizes the stack buffer `CatPostAnim.MixPoseLocal` uses; `ScleraMeshIndices` get the eye renderer (no shadows, no pre-pass); `HeadMeshIndices` are the meshes hidden when `HideHead` is set ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L170-L178)):

```xml
<!-- current/Content/Core/CharacterAssets.xml L237-L252 -->
    <CharacterCore Id="CharacterCore">
        <BodySource Id="KittenGlb"/>
        <LeftEyeBoneIndex Name="EyeJoint_L"/>
        <RightEyeBoneIndex Name="EyeJoint_R"/>
        <MaxBoneCount Value="256"/>
        <ScleraMeshIndices Value="6"/>
        <ScleraMeshIndices Value="7"/>
        <HeadMeshIndices Value="0"/>
        <HeadMeshIndices Value="1"/>
        <HeadMeshIndices Value="2"/>
        <HeadMeshIndices Value="3"/>
        <HeadMeshIndices Value="5"/>
        <HeadMeshIndices Value="6"/>
        <HeadMeshIndices Value="7"/>
        <HeadMeshIndices Value="8"/>
    </CharacterCore>
```

   `CharacterAvatar.CharacterCore.Scale` is 0.01 (the glTF is in centimetres) and is not an XML field ([current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L30-L51)).

4. **Declare fur.** `FurMeshIndex` is the sub-mesh of the body that gets shell fur; `FurShellDepth` is multiplied by the core scale; `FurMaskTexture` is the mask:

```xml
<!-- current/Content/Core/CharacterAssets.xml L254-L258 -->
    <CharacterFur Id="CharacterFur">
        <FurMeshIndex Value="5"/>
        <FurShellDepth Value="2"/>
        <FurMaskTexture Path="Textures/Characters/KittenFur.ktx2" Category="Default"/>
    </CharacterFur>
```

5. **Declare expressions.** Five lists of faces; `ExpressionCenterIndex` is a bone name. The root bone the expression is mixed from is not here: the avatar takes it from the helmet attachment's `Socket` (`Head_M`) ([current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L435-L446)). A random one of each list is picked for a personality face ([expression topic](../../animation/make-a-kitten-pull-a-facial-expression/overview.md)):

```xml
<!-- current/Content/Core/CharacterAssets.xml L260-L270 -->
    <CharacterExpressions Id="CharacterExpressions">
        <ExpressionCenterIndex Name="EyeLidJoints_M"/>
        <ExpressionScared Id="KittenExpScaredGlb"/>
        <ExpressionHappy Id="KittenExpHappy1Glb"/>
        <ExpressionHappy Id="KittenExpHappy2Glb"/>
        <ExpressionSad Id="KittenExpSadGlb"/>
        <ExpressionAngry Id="KittenExpAngry1Glb"/>
        <ExpressionAngry Id="KittenExpAngry2Glb"/>
        <ExpressionAwe Id="KittenExpAwe1Glb"/>
        <ExpressionAwe Id="KittenExpAwe2Glb"/>
    </CharacterExpressions>
```

6. **Declare the animation sets.** The MMU set is typed fields on `CharacterAvatar.Animations.MmuAnimations`; the ground set is read straight into `KittenRenderable`'s private clip fields (the clip catalog article reads those back). Every ground clip is individually optional (`LoadGroundAnim` returns null for a missing one) but `AnimIdle` must exist: its absence makes the block a reference ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L140-L152), [current/decomp/KSA/CharacterGroundAnimationsReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterGroundAnimationsReference.cs#L1-L50)):

```xml
<!-- current/Content/Core/CharacterAssets.xml L272-L303 -->
    <CharacterMMUAnimations Id="CharacterMMUAnimations">
        <MMUIdleDefaultAnim Id="KSA_Cat_Anim_MMU_Idle1Glb"/>

        <MMUIdleActionsAnim Id="KSA_Cat_Anim_MMU_Idle2Glb"/>
        <MMUIdleActionsAnim Id="KSA_Cat_Anim_MMU_Idle3Glb"/>

        <MMUMoveLeftLoopAnim Id="KSA_Cat_Anim_MMU_MoveLeftLoopGlb"/>
        <MMUMoveRightLoopAnim Id="KSA_Cat_Anim_MMU_MoveRightLoopGlb"/>
        <MMUMoveForwardLoopAnim Id="KSA_Cat_Anim_MMU_MoveForwardLoopGlb"/>
        <MMUMoveBackwardLoopAnim Id="KSA_Cat_Anim_MMU_MoveBackwardLoopGlb"/>
        <MMUMoveUpLoopAnim Id="KSA_Cat_Anim_MMU_MoveUpLoopGlb"/>
        <MMUMoveDownLoopAnim Id="KSA_Cat_Anim_MMU_MoveDownLoopGlb"/>

        <MMUArmRetractAnim Id="KSA_MMU_Anim_ArmRetractGlb"/>
    </CharacterMMUAnimations>

    <CharacterGroundAnimations Id="CharacterGroundAnimations">
        <AnimIdle Id="KittenAnimIdleGlb"/>
        <AnimWalk Id="KittenAnimWalkGlb"/>
        <AnimRun Id="KittenAnimRunGlb"/>
        <AnimLadder Id="KittenAnimLadderGlb"/>
        <AnimJump Id="KittenAnimJumpGlb"/>
        <AnimTumble Id="KittenAnimTumbleGlb"/>
        <AnimJumpLand Id="KittenAnimJumpExtroGlb"/>
        <AnimMoonWalk Id="KittenAnimMoonWalkGlb"/>
        <AnimMoonRun Id="KittenAnimMoonRunGlb"/>
        <AnimSwim Id="KittenAnimSwimGlb"/>
        <AnimSwimIdle Id="KittenAnimTreadingWaterGlb"/>
        <AnimSeatedIdle Id="KittenAnimSeatedIdleGlb"/>

        <AnimSeatedIdleAction Id="KittenAnimSeatedShuffleGlb"/>
    </CharacterGroundAnimations>
```

   The seated entries are loaded only for renderables that an `IVASeat` builds with `seated: true`; an EVA kitten never reads them.

7. **Declare the ear mask and the attachments.** `EarMask` lists ear bones (the `CatEarAnim` processor masks these). An attachment has a `Source`, a `Socket` bone, one `Materials` group per material slot of the mesh, an optional `Transform` (position and Euler rotation in radians) and an `AttachmentType` of `Helmet`, `Visor`, `MMU` or `Cosmetic` ([current/decomp/KSA/CharacterAttachmentType.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAttachmentType.cs)):

```xml
<!-- current/Content/Core/CharacterAssets.xml L305-L321 -->
    <CharacterAnimationMasks Id="CharacterAnimationMasks">
        <EarMask Name="ear1_R"/>
        <EarMask Name="ear2_R"/>
        <EarMask Name="ear1_L"/>
        <EarMask Name="ear2_L"/>
    </CharacterAnimationMasks>

    <CharacterAttachment Id="CharacterHelmetAttachment">
        <Source Id="KittenHelmetGlb"/>
        <Socket Name="Head_M"/>
        <Materials>
            <Diffuse Path="Textures/Characters/Attachments/Kitty_Helmet_A.ktx2" Category="Default"/>
            <Normal Path="Textures/Characters/Attachments/Kitty_Helmet_N.ktx2" Category="Default"/>
            <AoRoughMetal Path="Textures/Characters/Attachments/Kitty_Helmet_ORM.ktx2" Category="Default"/>
        </Materials>
        <AttachmentType>Helmet</AttachmentType>
    </CharacterAttachment>
```

```xml
<!-- current/Content/Core/CharacterAssets.xml L334-L354 -->
    <CharacterAttachment Id="CharacterMMUAttachment">
        <Source Id="KittenMMUGlb"/>
        <Socket Name="Chest_M"/>
        <!-- Material order follows SK_KSA_MMU.glb: body first, labels second -->
        <Materials>
            <Diffuse Path="Textures/Characters/Attachments/KSA_MMU_Color.ktx2" Category="Default"/>
            <Normal Path="Textures/Characters/Attachments/KSA_MMU_Normal.ktx2" Category="Default"/>
            <AoRoughMetal Path="Textures/Characters/Attachments/KSA_MMU_ORM.ktx2" Category="Default"/>
        </Materials>
        <Materials>
            <Diffuse Path="Textures/Characters/Attachments/KSA_MMU_Texts.ktx2" Category="Default"/>
            <Normal Id="EmptyNormal"/>
            <AoRoughMetal Path="Textures/Characters/EmptyAoRoughMetallic.png" Category="Default"/>
        </Materials>
        <!-- Aligns SK_KSA_MMU.glb onto the chest socket; rotation in radians = (90, 163.5, 90) deg -->
        <Transform>
            <Position X="0" Y="1" Z="-1.1"/>
            <Rotation X="1.570796" Y="2.853613" Z="1.570796"/>
        </Transform>
        <AttachmentType>MMU</AttachmentType>
    </CharacterAttachment>
```

   `CharacterReference` also accepts any number of `<CosmeticAttachments Id="..."/>` children (`List<CharacterAttachmentReference>`, element name `CosmeticAttachments`); `SetupAttachmentsFromRenderRef` builds a `StaticMeshRenderable` for each into `Attachments.CosmeticAttachments` with the same socket-plus-transform rule as the helmet ([current/decomp/KSA/CharacterReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterReference.cs#L19-L20), [current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L447-L523)). No stock character uses one, and no reference mod does; KSAGolf adds a runtime entry to the same list instead ([prop topic](../../appearance/attach-a-prop-to-a-kitten-bone/overview.md)).

8. **Tie it together with a `Character`.** Each child is an `Id` reference to a block above, plus a personality string (`Neutral`, `Curious`, `Gruff`, `Woeful`, `Cheerful`; unknown strings become `Neutral`) ([current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L344-L376)):

```xml
<!-- current/Content/Core/CharacterAssets.xml L363-L399 -->
    <Character Id="HunterKitten">
        <Personality Value="Neutral"/>
        <CharacterCore Id="CharacterCore"/>
        <CharacterTextures Id="CharacterTextures"/>

        <CharacterHelmet Id="CharacterHelmetAttachment"/>
        <CharacterVisor Id="CharacterVisorAttachment"/>
        <CharacterMMU Id="CharacterMMUAttachment"/>

        <CharacterFur Id="CharacterFur"/>
        <CharacterExpressions Id="CharacterExpressions"/>
        <CharacterMMUAnimations Id="CharacterMMUAnimations"/>
        <CharacterAnimationMasks Id="CharacterAnimationMasks"/>
        <CharacterGroundAnimations Id="CharacterGroundAnimations"/>

        <AnimHelmetMask Id="KittenAnimHelmetMaskGlb"/>
        <BlinkAnim Id="KSA_Cat_Anim_BlinkGlb"/>
    </Character>

    <Character Id="BanjoKitten">
        <Personality Value="Woeful"/>
        <CharacterCore Id="CharacterCore"/>
        <CharacterTextures Id="CharacterTextures_Siamese"/>

        <CharacterHelmet Id="CharacterHelmetAttachment"/>
        <CharacterVisor Id="CharacterVisorAttachment"/>
        <CharacterMMU Id="CharacterMMUAttachment"/>

        <CharacterFur Id="CharacterFur"/>
        <CharacterExpressions Id="CharacterExpressions"/>
        <CharacterMMUAnimations Id="CharacterMMUAnimations"/>
        <CharacterAnimationMasks Id="CharacterAnimationMasks"/>
        <CharacterGroundAnimations Id="CharacterGroundAnimations"/>

        <AnimHelmetMask Id="KittenAnimHelmetMaskGlb"/>
        <BlinkAnim Id="KSA_Cat_Anim_BlinkGlb"/>
    </Character>
```

9. **Read the result back in code.** doh lists the registered characters through reflection on the internal collection, so it can offer them in a spawn menu and choose one for a new kitten:

```csharp
// doh.lib/Spawning/KittenSpawner.Catalog.cs L85-L107
    /// <summary>Gets all CharacterReference entries via reflection on ModLibrary.AllCharacters (internal).</summary>
    private static List<CharacterReference> GetAllCharacterReferences()
    {
        try
        {
            var field = typeof(ModLibrary).GetField("AllCharacters",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null) return new List<CharacterReference>();

            var collection = field.GetValue(null);
            if (collection == null) return new List<CharacterReference>();

            var getListMethod = collection.GetType().GetMethod("GetList");
            if (getListMethod == null) return new List<CharacterReference>();

            return getListMethod.Invoke(collection, null) as List<CharacterReference> ?? new List<CharacterReference>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"doh: GetAllCharacterReferences error: {ex.Message}");
            return new List<CharacterReference>();
        }
    }
```

## Complete example

A minimal new character that reuses every stock block except the head material. This is **adapted** from the stock XML and untested in a running game; every element and attribute below exists in the loader classes listed above, and the referenced ids exist in `current/Content/Core/CharacterAssets.xml`.

```xml
<!-- adapted from current/Content/Core/CharacterAssets.xml; untested, no reference mod declares a Character -->
<Assets>
    <PbrMaterial Id="MyKittenHead">
        <Diffuse Path="Textures/MyKittenHead_A.ktx2" Category="Default"/>
        <Normal Path="Textures/Characters/KittenHead_N.ktx2" Category="Default"/>
        <AoRoughMetal Path="Textures/Characters/EmptyAoRoughMetallic.png" Category="Default"/>
    </PbrMaterial>

    <CharacterTextures Id="MyKittenTextures">
        <CharacterBodyMaterial Id="CharacterBody"/>
        <CharacterHeadMaterial Id="MyKittenHead"/>
        <CharacterEyeMaterial Id="CharacterEyeBlue"/>
    </CharacterTextures>

    <Character Id="MyKitten">
        <Personality Value="Cheerful"/>
        <CharacterCore Id="CharacterCore"/>
        <CharacterTextures Id="MyKittenTextures"/>
        <CharacterHelmet Id="CharacterHelmetAttachment"/>
        <CharacterVisor Id="CharacterVisorAttachment"/>
        <CharacterMMU Id="CharacterMMUAttachment"/>
        <CharacterFur Id="CharacterFur"/>
        <CharacterExpressions Id="CharacterExpressions"/>
        <CharacterMMUAnimations Id="CharacterMMUAnimations"/>
        <CharacterAnimationMasks Id="CharacterAnimationMasks"/>
        <CharacterGroundAnimations Id="CharacterGroundAnimations"/>
        <AnimHelmetMask Id="KittenAnimHelmetMaskGlb"/>
        <BlinkAnim Id="KSA_Cat_Anim_BlinkGlb"/>
    </Character>
</Assets>
```

Ship it in the mod's asset bundle (listed in `mod.toml`'s `assets`), after Core is loaded so the `Id` references resolve. To spawn it, use the id with the `KittenEva` constructor ([spawn an EVA kitten](../spawn-an-eva-kitten-from-code/overview.md)). Mod files that read the loaded result:

- [doh.lib/Spawning/KittenSpawner.Catalog.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.Catalog.cs#L85-L107) - reads `ModLibrary.AllCharacters`.
- [kitten-animations.lib/KittenAnimationCatalog.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationCatalog.cs#L54-L88) - reads the ground animation set back through the renderable's private fields.
- [docs/KSA-MODDING-NOTES.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/docs/KSA-MODDING-NOTES.md#L505-L526) - why KSAGolf declares no `Character` (roster dressing, removal and manifest order); the claims are summarised under Variations and gotchas.

## Variations and gotchas

- **Modded characters join the roster.** A registered character is eligible for every random roster pick, which the KSAGolf notes put at about a quarter of a roster; removing the mod later leaves saves that name a character id that no longer resolves, which the same notes say makes `KittenEva.CreateKittenFromSaveData` throw on nearly every save (mod claim from [docs/KSA-MODDING-NOTES.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/docs/KSA-MODDING-NOTES.md#L505-L526); `ModLibrary.Get<CharacterReference>` throws a `NullReferenceException` for an unknown id, [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1175-L1183), and `CreateKittenFromSaveData` calls it, [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L100-L107)). That is why KSAGolf uses a runtime cosmetic attachment ([KSAGolf kitten held props](../../../../mods/ksagolf/features/kitten-held-props.md)).
- **Load order.** `CharacterReference.OnDataLoad` resolves Core's `CharacterCore` while the mod's file loads, so a manifest that lists the mod before Core makes the character throw `CharacterCoreReference is null for 'CharacterCore'` at load (same notes, L519-L526). `mod.toml` has no ordering field.
- **One skeleton.** Every clip names the body glTF as its `Skeleton`; the stock files assume one rig (`KSA_Cat`). Other rigs would also need `Core.Scale`, eye bones, ear bones, `HeadMeshIndices`, `ScleraMeshIndices`, the fur sub-mesh and the fixed material slot order to match.
- **Hard-wired names elsewhere.** `CatFurRenderable.GetGravity` reads bone index 66 for the fur sag, and mods look bones up by name (`Head_M`, `Chest_M`); a differently-ordered rig changes both ([current/decomp/KSA/CatFurRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CatFurRenderable.cs#L74-L92)).
- **A bad bone name throws at avatar construction,** not at XML load, because `GetIndex(skeleton)` runs in `InitalizeFromCharacterRef`.
- **Display of the stock list.** The clip catalog reads `CharacterGroundAnimations` through the live renderable; the same data is reachable from `ModLibrary.AllCharacterGroundAnimations`, which is also `internal`.

## See also

- [Read and extend the character asset XML (overview)](overview.md)
- [Reach a kitten's avatar, skeleton, bones and animation catalog](../reach-a-kittens-avatar-and-skeleton/overview.md)
- [Spawn an EVA kitten from code](../spawn-an-eva-kitten-from-code/overview.md), [Attach a prop to a kitten's hand or another bone](../../appearance/attach-a-prop-to-a-kitten-bone/overview.md), [Make a kitten pull a facial expression](../../animation/make-a-kitten-pull-a-facial-expression/overview.md)
- Mod features: [doh kitten spawning](../../../../mods/unscience/features/doh-kitten-spawning.md), [Kitten animation clip catalog](../../../../mods/unscience/features/kitten-animations-clip-catalog.md), [KSAGolf kitten held props](../../../../mods/ksagolf/features/kitten-held-props.md)
- [Declare post-processing shader assets in XML](../../../shaders/post-processing/declare-post-processing-shader-assets-in-xml/overview.md)
