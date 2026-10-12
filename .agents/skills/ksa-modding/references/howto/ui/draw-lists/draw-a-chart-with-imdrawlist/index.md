# Game Feature

* [Draw charts, scopes and relief maps with the ImGui draw list](overview.md) - Plotting time series, functions of altitude and map-like instruments from ImDrawList primitives inside a KSA mod window, with the game data each one samples

# How-To

* [Chart a time series with nice axes, extrema, a dashed threshold and a hover crosshair](draw-a-chart-with-imdrawlist-1.md) - Draw a self-contained line chart with ImDrawList primitives, including 1-2-5-10 axis ticks, a time axis, area fill, min and max markers, an alert line and a crosshair tooltip
* [Draw a craft-centred polar scope and a cached hill-shaded terrain map](draw-a-chart-with-imdrawlist-3.md) - Build a north-up local frame from a celestial's rotation axis, plot contacts on a polar scope and render a 64x64 height grid as shaded draw-list cells, re-sampling only when needed
* [Plot a game function against altitude with InvisibleButton space reservation](draw-a-chart-with-imdrawlist-2.md) - Sample PhysicalAtmosphereReference at 101 altitudes and draw density and pressure curves in a normal ImGui window using a reserved canvas, grid, labels and a selected-altitude marker
