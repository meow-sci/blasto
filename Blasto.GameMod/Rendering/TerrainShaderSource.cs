using System;

namespace Blasto.Rendering;

/// <summary>Pure, anchor-validated terrain shader source transformation, also used by offline validation.</summary>
internal static class TerrainShaderSource
{
    internal static string Transform(string source, string id) => id switch
    {
        "PlanetFrag" => ReplaceOnce(ReplaceOnce(ReplaceOnce(source, "void main()", FragmentDeclarations + "\nvoid main()"),
            "    // Lighting calculations", "    BlastoMaterial(blendedDiffuse, roughness, worldNormal);\n\n    // Lighting calculations"),
            "    outColor = finalColor;", "    BlastoHighlight(finalColor);\n    outColor = finalColor;"),
        "PlanetVert" => ReplaceOnce(ReplaceOnce(source, "void main()", "layout(location = 7) out vec3 blastoSurfacePosition;\n\nvoid main()"),
            "    outWorldPosition = worldPos;", "    outWorldPosition = worldPos;\n    blastoSurfacePosition = worldPos.xyz;"),
        "PlanetTessEvaluation" => ReplaceOnce(ReplaceOnce(ReplaceOnce(source, "void main(void)",
            "layout(location = 7) out vec3 blastoSurfacePosition;\n\nvoid main(void)"),
            "\n        outWorldPosition = vec4(worldPos.xyz, 1.0f);",
            "\n        outWorldPosition = vec4(worldPos.xyz, 1.0f);\n        blastoSurfacePosition = worldPos.xyz;"),
            "\n    outWorldPosition = vec4(worldPos.xyz, 1.0f);",
            "\n    outWorldPosition = vec4(worldPos.xyz, 1.0f);\n    blastoSurfacePosition = displacedPosition;"),
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };

    private static string ReplaceOnce(string source, string anchor, string replacement)
    {
        int index = source.IndexOf(anchor, StringComparison.Ordinal);
        if (index < 0 || source.IndexOf(anchor, index + anchor.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"Terrain shader integration anchor changed: {anchor}");
        return source[..index] + replacement + source[(index + anchor.Length)..];
    }

    // std430: 48-byte header (uvec4 + two tuning vec4s), then five vectors per mark.
    internal const string FragmentDeclarations = """
layout(location = 7) in vec3 blastoSurfacePosition;
struct BlastoMark {
    vec4 centerRadius;
    vec4 tangentUStrength;
    vec4 tangentVAnisotropy;
    vec4 tintRoughness;
    uvec4 meta;
};
layout(std430, set = 2, binding = 13) readonly buffer BlastoMarks {
    uvec4 header;
    vec4 tuning0; // size, visibility, darkness, streak strength (size/visibility applied when packing marks)
    vec4 tuning1; // center strength, edge softness, reserved, reserved
    BlastoMark marks[];
} blasto;
bool blastoProjectedMark = false;

// Projection diagnostics intentionally ignore height tolerance and lighting.
void BlastoHighlight(inout vec4 color) {
    if (blastoProjectedMark) color = vec4(1.0,0.03,0.7,1.0);
}

float BlastoHash(float p) { return fract(sin(p * 127.1 + 311.7) * 43758.5453); }
float BlastoNoise(vec2 p) {
    vec2 i = floor(p), f = fract(p); f = f*f*(3.0-2.0*f);
    return mix(mix(BlastoHash(dot(i,vec2(1,73))), BlastoHash(dot(i+vec2(1,0),vec2(1,73))),f.x),
               mix(BlastoHash(dot(i+vec2(0,1),vec2(1,73))), BlastoHash(dot(i+vec2(1,1),vec2(1,73))),f.x),f.y);
}
void BlastoMaterial(inout vec3 albedo, inout float roughness, inout vec3 normal) {
    for (uint i = 0u; i < min(blasto.header.x, 64u); ++i) {
        BlastoMark mark = blasto.marks[i];
        vec3 delta = blastoSurfacePosition - mark.centerRadius.xyz;
        float radius = mark.centerRadius.w;
        vec3 U = mark.tangentUStrength.xyz, V = mark.tangentVAnisotropy.xyz;
        vec3 N = normalize(cross(U,V));
        vec2 meters = vec2(dot(delta,U), dot(delta,V));
        vec2 p = (meters / radius) / vec2(mark.tangentVAnisotropy.w, 1.0);
        // All fragment lanes evaluate these before the footprint-dependent branch.
        // Differentiating atan itself would also introduce a false spike at its +/-pi seam.
        vec2 dpdx = dFdx(p), dpdy = dFdy(p);
        float radiusSquared = dot(p,p);
        float r = sqrt(radiusSquared);
        vec2 angularGradient = vec2(-p.y,p.x) / max(radiusSquared,1e-8);
        vec2 angularDerivatives = vec2(dot(angularGradient,dpdx),dot(angularGradient,dpdy));
        if (r >= 1.0) continue;
        if ((blasto.header.y & 1u) != 0u) {
            blastoProjectedMark = true;
            continue;
        }
        if (abs(dot(delta,N)) > max(6.0, radius*0.2)) continue;
        float phase = float(mark.meta.x & 65535u) * 0.0000958738;
        float angle = (radiusSquared > 1e-12 ? atan(p.y,p.x) : 0.0) + phase;
        float coarse = sin(angle*17.0 + sin(angle*7.0)*1.7);
        float fine = sin(angle*61.0 + sin(angle*13.0)*2.6 + r*4.0);
        // Filter narrow spokes by their screen footprint to prevent distant shimmer.
        float angularWidth = max((abs(angularDerivatives.x)+abs(angularDerivatives.y))*61.0,0.035);
        float rays = smoothstep(0.65-angularWidth, 0.9+angularWidth, fine);
        rays *= smoothstep(-0.4,0.9,coarse);
        float irregular = BlastoNoise(p*7.0 + vec2(phase*13.0));
        float broken = mix(0.35,1.0,BlastoNoise(p*42.0 + vec2(phase)));
        float center = exp(-r*r*65.0)*(0.55 + 0.3*irregular);
        float fans = rays * broken * exp(-r*1.8) * smoothstep(0.06,0.2,r);
        float broad = 0.15 * irregular * exp(-r*r*3.0);
        // A continuous border fade: larger softness values spread the fade across the footprint.
        float feather = 1.0-exp(-8.0*(1.0-r)*(0.85+0.3*irregular)/blasto.tuning1.y);
        // Factor out the largest weight so large typed controls cannot overflow the shape sum.
        float shapeScale = max(1.0,max(blasto.tuning1.x,blasto.tuning0.w));
        float coverage = (center*(blasto.tuning1.x/shapeScale) +
            fans*0.65*(blasto.tuning0.w/shapeScale) + broad/shapeScale)*feather;
        float strength = mark.tangentUStrength.w; // 1-exp(-saved exposure times live visibility gain)
        // Optical response naturally approaches opaque without imposing a tuning-control ceiling.
        float opacity = 0.0;
        float darkness = blasto.tuning0.z;
        vec3 profileResponse = vec3(0.7)+vec3(1.0)-mark.tintRoughness.rgb;
        // Multiply existing material detail; stock terrain lighting/shadows follow this evaluation.
        // Log products preserve tiny/large combinations and avoid infinity times zero.
        if (coverage > 0.0 && strength > 0.0) {
            float opticalLog = log(coverage)+log(shapeScale)+log(strength);
            opacity = 1.0-exp(-exp(opticalLog));
            if (darkness > 0.0)
                albedo *= exp(-exp(vec3(opticalLog+log(darkness))+log(profileResponse)));
        }
        roughness = mix(roughness,mark.tintRoughness.w,opacity*0.5);
        vec3 radial = U*p.x + V*p.y;
        normal = normalize(normal + radial*(coarse*opacity*0.012));
    }
}
""";
}
