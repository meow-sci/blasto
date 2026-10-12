# Game Feature

* [Visual regression testing for a KSA mod](overview.md) - Capture frames at the same simulated instant, then diff variants, build contact sheets and animations and compute a temporal standard-deviation map to separate a real change from render noise

# How-To

* [Return contact sheets, GIFs and temporal-noise maps inline through an MCP server](visual-regression-for-mods-2.md) - Wrap the file bridge in a standard-library MCP server that launches the game only if needed and returns captures as base64 JPEG, sheets, GIFs and noise maps an agent can read
* [Same-instant variant diffs, frame series and temporal noise maps with a vis.py toolbox](visual-regression-for-mods-1.md) - Pause the world, capture a base and each variant of one setting at the same instant, diff them with manifest-driven crops, and compute a per-pixel standard deviation over a series to find renderer noise
