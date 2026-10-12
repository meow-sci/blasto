---
type: How-To
title: Read the AssetBundle element table against three real asset files
description: Reference for the 65 child elements of an Assets XML file that KSA.AssetBundle maps to template types, which are file-backed, how part module elements are registered by reflection, and the Id, Path, Category and HostOnly attributes
category: assets
group: asset-xml
topic: asset-xml-element-reference
difficulty: beginner
prerequisites: []
demonstrated_by:
  - mods/ksagolf/features/prop-and-course-asset-xml
  - mods/ksacars/features/car-part-assets-glb-pbr
  - mods/ksarmory/features/weapon-part-definitions-xml
techniques: [xml-asset]
hooks: [xml Assets root listed in mod.toml assets]
lifecycle: [startup:asset-load]
game_types:
  - KSA.AssetBundle
  - KSA.SerializedId
  - KSA.XmlHelper
  - KSA.FileReference
  - KSA.MeshAtlasFileReference
  - KSA.Gltf2Reference
  - KSA.PbrMaterialReference
  - KSA.PartTemplate
tags: [reference, xml, assetbundle, elements, mod-toml, registry]
---

Every file a mod lists under `assets` in its `mod.toml` has an `<Assets>` root, and the game deserializes it into a
`KSA.AssetBundle`. The bundle is nothing but one list, `Assets`, in which an `[XmlElement]` attribute maps each
child element name to a template or reference class. This article is the lookup table (all 65 element names), the
rules for how each kind registers, and a walk through three real mod files so you can see the table in use.
Demonstrated by [Prop and course asset XML](../../../../mods/ksagolf/features/prop-and-course-asset-xml.md), [Car part assets: GLB atlases, PBR materials and subparts](../../../../mods/ksacars/features/car-part-assets-glb-pbr.md) and
[Weapon parts as inert KSA parts split into an Assets file and a GameData file](../../../../mods/ksarmory/features/weapon-part-definitions-xml.md).

## Background: how the game does it

The element table is the `Assets` list on `AssetBundle`; the root element is `Assets` (the first dozen mappings are shown, the whole table follows in step 1):

```csharp
// current/decomp/KSA/AssetBundle.cs L9-L21
[XmlRoot("Assets")]
public class AssetBundle : SerializedId
{
	[XmlElement("Vehicle", typeof(VehicleTemplate))]
	[XmlElement("Comet", typeof(CometTemplate))]
	[XmlElement("InterstellarComet", typeof(InterstellarCometTemplate))]
	[XmlElement("PeriodicComet", typeof(PeriodicCometTemplate))]
	[XmlElement("AtmosphericBody", typeof(AtmosphericBodyTemplate))]
	[XmlElement("StellarBody", typeof(StellarBodyTemplate))]
	[XmlElement("Barycenter", typeof(BarycenterTemplate))]
	[XmlElement("PlanetaryBody", typeof(PlanetaryBodyTemplate))]
	[XmlElement("MinorBody", typeof(MinorBodyTemplate))]
	[XmlElement("Asteroid", typeof(AsteroidTemplate))]
// ...
```

After deserialization `AssetBundle.OnDataLoad` walks the list and registers what each element is. Celestial
templates are registered only when the selected system requires them, and in the preload pass (see
[Understand when and in which order KSA loads mod XML](../mod-xml-load-order/overview.md)) only celestials, vehicles and situations are processed:

```csharp
// current/decomp/KSA/AssetBundle.cs L84-L109
	public override void OnDataLoad(Mod mod)
	{
		base.OnDataLoad(mod);
		foreach (SerializedId asset in Assets)
		{
			if (asset is AstronomicalTemplate astronomicalTemplate)
			{
				if ((SystemLibrary.Default == null || SystemLibrary.Default.Requires(astronomicalTemplate)) && (!mod.Preload || asset is CelestialTemplate))
				{
					astronomicalTemplate.OnDataLoad(mod);
					ModLibrary.Register(astronomicalTemplate);
				}
			}
			else if (mod.Preload)
			{
				if (asset is VehicleTemplate || asset is SituationTemplate)
				{
					asset.OnDataLoad(mod);
				}
			}
			else
			{
				asset.OnDataLoad(mod);
			}
		}
	}
```

Three base classes explain most of the table:

- **`SerializedId`** carries the `Id` attribute (`[XmlAttribute]`), `IsReferenceable` (an Id was given) and the hash;
  its `OnDataLoad` sets the owning `Mod` ([current/decomp/KSA/SerializedId.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedId.cs#L6-L31)).
- **`FileReference : SerializedId`** adds `Path` (the `LocalPath` attribute) and resolves it with
  `Mod.GetPath`. If no `Id` is given the Id becomes the absolute path. Outside preload an element with an empty
  `Path` is a reference to an already registered file; otherwise it queues a loader. If registration is refused
  because the Id exists, it silently becomes a reference ([current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L10-L72)).
- **Templates** (`PartTemplate`, `StaticObjectTemplate`, `ClutterObjectTemplate`, `SubstanceTemplate` and so on)
  are `SerializedId` subclasses whose `OnDataLoad` registers them in a `ModLibrary` collection
  (`ModLibrary.Register` has about forty overloads, each backed by a `SerializedCollection<T>` such as `AllParts`,
  `AllMaterials`, `AllGltfs`, `AllMeshes`, `AllFiles`; [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L575-L776)). `SerializedCollection.Register`
  is first-wins ([current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L20-L33)).

## Lifecycle and timing

`startup:asset-load`: `Mod.LoadAssetBundles` deserializes each listed file during `ModLibrary.PreloadAssetBundles`
and again during `ModLibrary.LoadAll` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L449-L506)), then `ModLibrary.AttachGameData` merges
`PartGameData` into the matching `Part` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1863-L1880)), after `StarMapAllModsLoaded`. No mod code
runs in this window; code meets the assets later by Id. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Find the element you need in the table.** Names are case-sensitive XML element names. Types in the second
   column are in namespace `KSA` unless shown.

   **Celestials and vehicles**

   | Element | Type | Notes |
   |---|---|---|
   | `StellarBody`, `PlanetaryBody`, `AtmosphericBody`, `MinorBody`, `Asteroid`, `TerrestrialBody` | `StellarBodyTemplate`, `PlanetaryBodyTemplate`, `AtmosphericBodyTemplate`, `MinorBodyTemplate`, `AsteroidTemplate`, `TerrestrialBodyTemplate` | Body library entries; systems pull them in with `<LoadFromLibrary Id=.../>` |
   | `Comet`, `InterstellarComet`, `PeriodicComet`, `Barycenter` | `CometTemplate`, `InterstellarCometTemplate`, `PeriodicCometTemplate`, `BarycenterTemplate` | Registered only if the selected system requires them |
   | `Vehicle` | `VehicleTemplate` | A saved vehicle placed in a system |

   **Files (`FileReference` subclasses: `Path` loads a file)**

   | Element | Type | Notes |
   |---|---|---|
   | `MeshAtlas` | `MeshAtlasFileReference` | A GLB whose every glTF mesh becomes a `MeshReference` with `Id` = mesh name; attribute `HostOnly` |
   | `MeshFile` | `MeshFileReference` | A single mesh file |
   | `Texture` | `TextureReference` | `Category` attribute; optional sidecar `.toml` with the same base name |
   | `Shader`, `GaugeShader` | `ShaderReference`, `GaugeShaderReference` | GLSL or SPIR-V sources |
   | `LanguageFile` | `LanguageFileReference` | Localization strings; loaded early (`PreloadLanguages`) |
   | `SoundFile` | `SoundFileReference` | An audio file |

   **Materials and models**

   | Element | Type | Notes |
   |---|---|---|
   | `PbrMaterial` | `PbrMaterialReference` | Children `Diffuse`, `Normal`, `AoRoughMetal`, `Emissive`, `ThinFilm`, `Alpha` ([Declare glTF meshes and PBR materials in asset XML and author their texture sets](../../models-and-textures/declare-gltf-and-pbr-material-assets/overview.md)) |
   | `GroundClutterMaterial` | `GroundClutterMaterialReference` | Extends `PbrMaterialReference` for clutter |
   | `GltfFile` | `Gltf2Reference` | Child `Source Path=`; registered in `AllGltfs` under its `Id`; used for characters and props |

   **Parts and editor data**

   | Element | Type | Notes |
   |---|---|---|
   | `Part` | `PartTemplate` | Instances `SubPart` children; `DisplayName`, `CrashTolerance` attributes |
   | `SubPart` | `SubPartTemplate` | A reusable body; `IsSubPart` is set |
   | `PartGameData`, `SubPartGameData` | `PartGameDataReference`, `SubPartGameDataReference` | Merged into the part with the same Id by `AttachGameData` ([Define a part with its own game data and GLB model](../../../vehicles-and-parts/parts/define-a-part-with-game-data/overview.md)) |
   | `KeyframeAnimation` | `KeyframeAnimationData.Template` | Animation data for subparts |
   | `EditorTagDef` | `EditorTagDefinition` | A tab or filter in the vehicle editor |

   **Static world content**

   | Element | Type | Notes |
   |---|---|---|
   | `StaticObject`, `StaticSubObject`, `StaticObjectGameData` | `StaticObjectTemplate`, `StaticSubObjectTemplate`, `StaticObjectGameDataReference` | Terrain-placed structures |
   | `ClutterObject`, `ClutterObjectGameData` | `ClutterObjectTemplate`, `ClutterObjectGameDataReference` | Ground clutter |
   | `VolumetricExhaustTemplate`, `PlumeTrailTemplate` | same names | Exhaust visuals |

   **Characters (kittens)**

   | Element | Type |
   |---|---|
   | `Character`, `CharacterCore`, `CharacterAttachment`, `CharacterFur`, `CharacterExpressions`, `CharacterMMUAnimations`, `CharacterAnimationMasks`, `CharacterGroundAnimations`, `CharacterTextures` | `CharacterReference`, `CharacterCoreReference`, `CharacterAttachmentReference`, `CharacterFurReference`, `CharacterExpressionsReference`, `CharacterMMUAnimationsReference`, `CharacterAnimationMasksReference`, `CharacterGroundAnimationsReference`, `CharacterTexturesReference` |

   **Audio**

   | Element | Type |
   |---|---|
   | `Sound`, `MultiSound`, `CompoundSound`, `ClusterSound` | `SoundReference`, `MultiSound`, `CompoundSound`, `ClusterSound` (all `SoundBehavior`) |
   | `SpatialSoundData`, `SoundGroup`, `MusicPlaylist`, `ChannelGroup` | `SpatialSoundData`, `SoundGroupData`, `MusicPlayList`, `ChannelGroupReference` |

   **Gauges**

   | Element | Type |
   |---|---|
   | `GaugeCanvas`, `GaugeComponent` | `GaugeCanvas`, `GaugeComponent` (both `GaugeBase`) |

   **Simulation data and effects**

   | Element | Type |
   |---|---|
   | `Substance`, `MixtureReaction`, `FixedReaction`, `ThermalReaction`, `GrainGeometry`, `Situation` | `SubstanceTemplate`, `MixtureReactionTemplate`, `FixedReactionTemplate`, `ThermalReactionTemplate`, `GrainGeometryTemplate`, `SituationTemplate` |
   | `ParticleEmitter`, `ParticleMesh`, `ExplosionVolume`, `Explosion`, `DistantGlint`, `DistantGlintSettings` | `ParticleEmitterReference`, `ParticleMeshReference`, `ExplosionVolumeTemplate`, `ExplosionReference`, `DistantGlintReference`, `DistantGlintSettingsReference` |

   Sound, character and gauge elements are covered by the audio, kittens and ui categories; this topic only
   shows where they sit in the table.

2. **Use the attributes that matter.**

   | Attribute or child | On | Meaning |
   |---|---|---|
   | `Id` | every `SerializedId` | Global registry key (hash of the string). Duplicate Ids are ignored after the first ([Resolve asset ids and file paths correctly across mods](../resolve-asset-ids-and-mod-relative-paths/overview.md)) |
   | `Path` | every `FileReference`, `Source`, texture children | Joined to the mod folder by `Mod.GetPath`, not to the XML file |
   | `Category` | `TextureReference` | `Default`, `Terrain`, `Vessel`, `TerrainHeight`; picks the graphics-setting size cap |
   | `HostOnly` | `MeshAtlas` | Passed inverted as the `createDeviceMesh` argument of `MeshReference.Load(gltfLoader, i, !HostOnly)`, so `true` skips creating the device (GPU) mesh |
   | `Power` | `Normal` (`TexturePowerReference`) | Normal-map strength, default 1 |
   | `DisplayName` | `Part`, `PbrMaterial` | Shown in the editor or console |
   | An element with no `Path` and an `Id` | file elements | A reference to an existing asset, not a new one |

   ```csharp
   // current/decomp/KSA/MeshAtlasFileReference.cs L17-L19
   	[XmlAttribute("HostOnly")]
   	[DefaultValue(false)]
   	public bool HostOnly { get; set; } = false;
   ```

3. **Know how part module elements are registered.** Elements inside a `Part` or `SubPart` that are not listed
   on `PartTemplate` itself (`PartModel`, `MeshView`, `Collider`, `IVASeat` and so on) are not hard-coded.
   `XmlHelper`'s static constructor reflects every `ModuleBase.TemplateDataBase` subclass in `KSA.dll` and
   registers it under `PartTemplate.Components` using its `[XmlType]` name (or the type name):

   ```csharp
   // current/decomp/KSA/XmlHelper.cs L33-L47
   		// ...
   		IEnumerable<Type> source2 = from t in Assembly.GetExecutingAssembly().GetTypes()
   			where t != typeof(ModuleBase.TemplateDataBase) && typeof(ModuleBase.TemplateDataBase).IsAssignableFrom(t)
   			select t;
   		XmlAttributes xmlAttributes2 = new XmlAttributes();
   		Type[] array3 = source2.ToArray();
   		Type[] array4 = array3;
   		foreach (Type type2 in array4)
   		{
   			XmlElementAttribute attribute2 = new XmlElementAttribute(type2.GetCustomAttribute<XmlTypeAttribute>()?.TypeName ?? type2.Name, type2);
   			xmlAttributes2.XmlElements.Add(attribute2);
   		}
   		AttributeOverrides.Add(typeof(PartTemplate), "Components", xmlAttributes2);
   		XmlSerializer value = new XmlSerializer(typeof(AssetBundle), AttributeOverrides);
   		Serializers.Add(typeof(AssetBundle), value);
   	}
   ```

   For example `PartModelModule.Template` declares `[XmlType(TypeName = "PartModel")]`, with `Mesh`, `Material`,
   `RayTracing`, `ShadowCaster`, `Internal` and `Terrain` children ([current/decomp/KSA/PartModelModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelModule.cs#L17-L42)). Stock
   uses `PartModelDynamic` and `MeshView` the same way. **Consequence:** a mod can use every stock module in XML,
   but cannot invent a new module element name without extending the serializer
   ([Register a custom XML asset type or inject new child elements into game XML](../register-custom-xml-asset-type/overview.md)).

4. **Read three real files against the table.** Start with the lightest, KSAGolf's props file: bare `GltfFile`
   and `PbrMaterial` elements, no parts at all. The code pairs them by Id at runtime:

   ```xml
   <!-- src/KSAGolf/KSAGolfProps.xml L1-L12 -->
   <?xml version="1.0" encoding="utf-8"?>
   <!-- Things a kitten holds or strikes, hung through CosmeticAttachments rather than built as parts,
        so each is a <GltfFile> in centimetres with one mesh, one primitive and one material. -->
   <Assets>
       <GltfFile Id="KSAGolf_Ball_Glb">
           <Source Path="Meshes/KSAGolf_Ball.glb"/>
       </GltfFile>
       <PbrMaterial Id="KSAGolf_Ball_Material">
           <Diffuse Path="Textures/KSAGolf_Ball_Diffuse.png" Category="Default"/>
           <Normal Path="Textures/KSAGolf_Ball_Normal.png" Category="Default"/>
           <AoRoughMetal Path="Textures/KSAGolf_Ball_PBR.png" Category="Default"/>
       </PbrMaterial>
   <!-- ... -->
   ```

   KSACars turns a GLB into a placeable part with `MeshAtlas`, `PbrMaterial`, `SubPart` and `Part`, the same shape
   Core's own `*Assets.xml` files use:

   ```xml
   <!-- src/KSACars/KSACarsAssets.xml L17-L24 -->
     <MeshAtlas Path="Meshes/KSACars_Buggy.glb" />

     <PbrMaterial Id="KSACars_Buggy_Material">
       <Diffuse Path="Textures/KSACars_Buggy_Diffuse.png" Category="Vessel" />
       <Normal Path="Textures/KSACars_Buggy_Normal.png" Category="Vessel" />
       <AoRoughMetal Path="Textures/KSACars_Buggy_PBR.png" Category="Vessel" />
       <Emissive Path="Textures/KSACars_Buggy_Emissive.png" Category="Vessel" />
     </PbrMaterial>
   ```

   ```xml
   <!-- src/KSACars/KSACarsAssets.xml L31-L39 -->
     <SubPart Id="KSACars_BuggyBody_SubPart">
       <PartModel Id="KSACars_Subpart_BuggyBody_Model">
         <Mesh Id="KSACars_Subpart_BuggyBody" />
         <Material Id="KSACars_Buggy_Material" />
       </PartModel>
       <MeshView>
         <Mesh Id="KSACars_Subpart_BuggyBody_VM" />
       </MeshView>
     </SubPart>
   ```

   ```xml
   <!-- src/KSACars/KSACarsAssets.xml L167-L175 -->
     <Part Id="KSACars_Prefab_Buggy" CrashTolerance="1e12">

       <SubPart Id="KSACars_Buggy_Body" InstanceOf="KSACars_BuggyBody_SubPart" />

       <SubPart Id="KSACars_Buggy_WheelFL" InstanceOf="KSACars_BuggyWheelFront_SubPart">
         <Transform>
           <Position X="0.34000" Y="1.15000" Z="0.84000" />
         </Transform>
       </SubPart>
     <!-- ... -->
   ```

   KSArmory splits art from simulation across two files: `Assets` for structure, `GameData` for `EditorTagDef`,
   `PartGameData` and friends. A tab in the part picker is one element:

   ```xml
   <!-- src/KSArmory/KSArmoryGameData.xml L31-L32 -->
     <EditorTagDef Id="Weapons" RootPartWhitelist="true" FaceSnapTargetWhitelist="true"
                   DiameterFilterlist="true" />
   ```

   Core's stock pattern for the same thing (`PartModelDynamic` plus a `MeshView` twin) is in
   [current/Content/Core/CoreFuelTankAAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/CoreFuelTankAAssets.xml#L4-L21).

5. **Register the file in `mod.toml`.** Only files listed in `assets` are read (no globbing). The next topic,
   [Understand when and in which order KSA loads mod XML](../mod-xml-load-order/overview.md), explains when each list is read.

## Complete example

- [src/KSAGolf/KSAGolfProps.xml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolfProps.xml), [src/KSAGolf/KSAGolfCourse.xml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolfCourse.xml)
- [src/KSACars/KSACarsAssets.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsAssets.xml)
- [src/KSArmory/KSArmoryAssets.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryAssets.xml), [src/KSArmory/KSArmoryGameData.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryGameData.xml)
- The engine table: [current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs)

## Variations and gotchas

- **Unknown element names fail quietly.** The serializer has no schema errors for an unknown element inside `Assets`;
  check names against the table above. Unknown attributes are likewise ignored.
- **Malformed XML is reported as missing content.** `XmlLoader` logs a failed deserialize and returns null
  ([current/decomp/KSA/XmlLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlLoader.cs#L20-L52)); the symptom is the assets simply not existing.
- **Plain `new XmlSerializer(typeof(AssetBundle))` lacks the overrides** and silently drops every part module
  component, so tools must use `XmlHelper.Serializers` ([Validate asset XML ids and paths before launching the game (offline)](../../validation/validate-asset-xml-references/overview.md)).
- **Preload does not process parts.** In the first pass only celestial templates, `Vehicle` and `Situation` run
  `OnDataLoad`; everything else is processed in `LoadAll`.
- **Ids from glTF.** A `MeshAtlas` registers each glTF mesh under its name, in the mesh table; ids are unique per registry, not
  globally, so a `SubPart` may share a mesh's name (Core does) but two meshes may not ([Resolve asset ids and file paths correctly across mods](../resolve-asset-ids-and-mod-relative-paths/overview.md)).
- **Removing a `SubPart` can break saves** that already hold the part ([Change a shipped part without breaking saves](../../../vehicles-and-parts/parts/evolve-a-shipped-part-without-breaking-saves/overview.md)).

## See also

- [Understand when and in which order KSA loads mod XML](../mod-xml-load-order/overview.md), [Resolve asset ids and file paths correctly across mods](../resolve-asset-ids-and-mod-relative-paths/overview.md), [Declare glTF meshes and PBR materials in asset XML and author their texture sets](../../models-and-textures/declare-gltf-and-pbr-material-assets/overview.md),
  [Add a part's art with its own GLB model and PBR textures](../../models-and-textures/add-a-part-model-with-glb-and-pbr-textures/overview.md), [Register a custom XML asset type or inject new child elements into game XML](../register-custom-xml-asset-type/overview.md), [Define a part with its own game data and GLB model](../../../vehicles-and-parts/parts/define-a-part-with-game-data/overview.md)
- [Prop and course asset XML](../../../../mods/ksagolf/features/prop-and-course-asset-xml.md), [Car part assets: GLB atlases, PBR materials and subparts](../../../../mods/ksacars/features/car-part-assets-glb-pbr.md), [Weapon parts as inert KSA parts split into an Assets file and a GameData file](../../../../mods/ksarmory/features/weapon-part-definitions-xml.md)
