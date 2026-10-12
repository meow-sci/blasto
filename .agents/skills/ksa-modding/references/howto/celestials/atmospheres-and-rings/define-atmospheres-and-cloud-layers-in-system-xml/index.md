# Game Feature

* [Define atmospheres, scattering and cloud layers for a body in system XML](overview.md) - Promote a body to AtmosphericBody with a complete Atmosphere block, tune Rayleigh, Mie and ozone terms, add a sibling Clouds block with coverage masks, detail tiles, flowmaps and cloud types, and build the textures offline

# How-To

* [Promote a body to AtmosphericBody and splice generated Atmosphere values into the XML](define-atmospheres-and-cloud-layers-in-system-xml-1.md) - Rename a PlanetaryBody or MinorBody element to AtmosphericBody, insert a complete Atmosphere block, and tune Rayleigh, Mie and ozone terms per body with generated values and data-as-key conventions
* [Python asset scripts with encodings matched to the shader that reads them](define-atmospheres-and-cloud-layers-in-system-xml-3.md) - Build cloud coverage, detail and flowmap textures offline with scripts whose channel layouts follow the stock cloud shader, check the scripts in and keep large derived textures out of the repository
* [Templated decks and laminae with absolute asset paths](define-atmospheres-and-cloud-layers-in-system-xml-2.md) - Add a Clouds block with one templated deck per giant planet and thin laminae for haze, pointing textures at absolute paths in a mod folder and deriving light distance and flow speed from layer geometry and measured winds
