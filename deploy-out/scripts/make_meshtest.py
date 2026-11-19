import re, os
src = open(r"D:\CrossTixlClaude\Operators\DepthAnything\Symbols\Lib\io\video\depthanything\DepthScan3D.cs", encoding='utf8').read().replace('\r\n', '\n')


def method(sig_start, next_marker):
    i = src.index(sig_start)
    j = src.index(next_marker, i)
    return src[i:j]


# comment block before WriteSphericalMesh starts at its doc comment
start = src.index("    /**\n     * Turns the spherical range map into a closed triangle mesh")
end = src.index("    private void WritePly(string absolutePath, bool ascii)")
body = src[start:end]
body = body.replace("private (int Vertices, int Triangles) WriteSphericalMesh(", "public static (int Vertices, int Triangles) WriteSphericalMesh(")
body = body.replace("private static void", "public static void")
body = body.replace("ToByte(", "ToByte(")
out = '''using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;

public struct ScanPoint { public System.Numerics.Vector3 Position; public System.Numerics.Vector3 Color; public int Count; public float Weight; public float LastSeen; }

public static class MeshCode
{
    const float SphericalCellRadians = 0.25f * MathF.PI / 180f;
    const int SphericalMinObservations = 2;
    const float MeshDepthJumpRatio = 1.6f;
    const float RoomObjectRatio = 0.75f;
    const int MeshMaxHoleEdges = 400;
    static byte ToByte(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
    public static Stopwatch W = Stopwatch.StartNew();
    public static void T(string s) { Console.WriteLine($"{W.Elapsed.TotalSeconds:F2}s {s}"); }
''' + body + '''
}

public static class Program
{
    public static void Main(string[] args)
    {
        var raw = File.ReadAllBytes(args[0]);
        var n = raw.Length / 24;
        var pts = new ScanPoint[n];
        for (int i = 0; i < n; i++)
        {
            var f = new float[6];
            Buffer.BlockCopy(raw, i * 24, f, 0, 24);
            pts[i] = new ScanPoint { Position = new(f[0], f[1], f[2]), Color = new(f[3], f[4], f[5]), Count = 2, Weight = 1 };
        }
        MeshCode.T("loaded " + n);
        var r = MeshCode.WriteSphericalMesh(args[1], pts);
        MeshCode.T($"done {r.Vertices} {r.Triangles}");
    }
}
'''
os.makedirs(r"D:\CrossTixlClaude\deploy-out\meshtest", exist_ok=True)
open(r"D:\CrossTixlClaude\deploy-out\meshtest\Program.cs", "w", encoding='utf8').write(out)
open(r"D:\CrossTixlClaude\deploy-out\meshtest\meshtest.csproj", "w").write('''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable><ImplicitUsings>disable</ImplicitUsings><AllowUnsafeBlocks>true</AllowUnsafeBlocks></PropertyGroup>
</Project>
''')
print("ok")
