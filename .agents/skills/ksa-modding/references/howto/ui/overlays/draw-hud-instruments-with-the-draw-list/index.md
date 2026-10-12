# Game Feature

* [Draw HUD instruments: arc gauges, gradients, scrolling tapes and animated intros](overview.md) - Building broadcast-style and fighter-style instruments from ImGui draw-list primitives, and the draw-list limits that shape the techniques

# How-To

* [HUD instruments: clip rects per region, scrolling tapes and endpoint rotation](draw-hud-instruments-with-the-draw-list-3.md) - Build a fighter-style HUD from draw-list lines - scrolling altitude and speed tapes drawn twice around a readout box with PushClipRect, a pitch ladder rotated by rotating endpoints, and a heading tape
* [HUD instruments: overlapping staged intro and outro animation with easing and edge-in stagger](draw-hud-instruments-with-the-draw-list-2.md) - Animate a draw-list HUD in and out as a pure function of elapsed seconds - five overlapping stages with 1-(1-t)^n easing, an edge-in stagger and a reversible outro
* [HUD instruments: PathArcTo plus PathStroke arcs and vertex-alpha gradients](draw-hud-instruments-with-the-draw-list-1.md) - Build sweeping arc gauges with the draw list's path API and fake gradient polygons by rescaling vertex alpha after AddConvexPolyFilled
