using System;
using System.Runtime.InteropServices;
using Brutal.Numerics;

namespace Blasto.Rendering;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct BlastGpuMark
{
    public float4 CenterRadius;
    public float4 TangentUStrength;
    public float4 TangentVAnisotropy;
    public float4 TintRoughness;
    public uint Seed, Profile, Reserved0, Reserved1;

    public const int Size = 80;
    public const int HeaderSize = 16;
    public const int MaximumMarks = 64;
    public const int Range = HeaderSize + Size * MaximumMarks;

    public static void AssertLayout()
    {
        if (Marshal.SizeOf<BlastGpuMark>() != Size || Marshal.SizeOf<float4>() != 16 ||
            Marshal.OffsetOf<BlastGpuMark>(nameof(CenterRadius)).ToInt32() != 0 ||
            Marshal.OffsetOf<BlastGpuMark>(nameof(TangentUStrength)).ToInt32() != 16 ||
            Marshal.OffsetOf<BlastGpuMark>(nameof(TangentVAnisotropy)).ToInt32() != 32 ||
            Marshal.OffsetOf<BlastGpuMark>(nameof(TintRoughness)).ToInt32() != 48 ||
            Marshal.OffsetOf<BlastGpuMark>(nameof(Seed)).ToInt32() != 64 ||
            Marshal.OffsetOf<BlastGpuMark>(nameof(Profile)).ToInt32() != 68)
            throw new InvalidOperationException("Blasto std430 mark layout mismatch.");
    }
}
