# Game Feature

* [Generate measured atmosphere tables from reference profiles](overview.md) - Build compact altitude tables offline from probe and reference-atmosphere data, apply KSA's own boundary rule, decimate to the nodes that reproduce ln density within tolerance, and emit one generated source file for flight and shaders

# How-To

* [Python generator emitting checked-in C# tables for flight and shaders](generate-measured-atmosphere-tables-offline.md) - Integrate per-body reference profiles hydrostatically, apply KSA's boundary thresholds, decimate to the nodes that reproduce ln density and ln pressure within tolerance, and emit one generated C# file consumed by the flight patch, the XML and the shader tables
