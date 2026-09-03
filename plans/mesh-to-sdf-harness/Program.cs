// Headless GPU verification harness for the TiXL MeshToSDF operator bake pipeline.
// Compiles the operator's bake compute shader, runs the dispatch sequences the
// operator uses for ALL conversion modes (JumpFlood signed/unsigned + exact
// closest point) on a procedural sphere, reads the volumes back and checks
// distance/sign correctness.
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.D3DCompiler;
using T3.Core.Resource;
using T3.Core.DataTypes.Vector;
using T3.Core.Rendering;
using Format = SharpDX.DXGI.Format;
using Device = SharpDX.Direct3D11.Device;
using Buffer = SharpDX.Direct3D11.Buffer;
using MapFlags = SharpDX.Direct3D11.MapFlags;

const string BakeShaderPath = @"C:\Users\Artonurban\AITEST\tixl-mtsd\Operators\Lib\Assets\shaders\cs\mesh-to-sdf-bake.hlsl";
string[] entryPoints =
[
    "ClearVoxels", "ClearSdf", "MeshToVoxel", "ClassifyInsideX", "ClassifyInsideY", "ClassifyInsideZ",
    "Preprocess", "Jfa", "Postprocess", "ClosestPointBrute", "ApplyInsideSign",
    "InitSdfScatter", "BakeSDFScatter", "FinalizeSdfScatter",
    "SmoothFirstPass", "SmoothPass", "SmoothCopyBack"
];

var failures = new List<string>();

// ---------------------------------------------------------------- device init
Device device;
{
    using var factory = new SharpDX.DXGI.Factory1();
    Adapter best = null;
    for (var i = 0; i < factory.GetAdapterCount1(); i++)
    {
        var a = factory.GetAdapter1(i);
        Console.WriteLine($"adapter #{i}: {a.Description.Description} mem={(long)a.Description.DedicatedVideoMemory / (1024 * 1024)}MB");
        if (best == null || (long)a.Description.DedicatedVideoMemory > (long)best.Description.DedicatedVideoMemory)
            best = a;
    }
    device = new Device(best, DeviceCreationFlags.BgraSupport, FeatureLevel.Level_11_1);
}
ResourceManager.Init(device);
Console.WriteLine($"Device feature level {device.FeatureLevel}");
{
    var dxgiDevice = device.QueryInterface<SharpDX.DXGI.Device1>();
    Console.WriteLine($"running on adapter: {dxgiDevice.Adapter.Description.Description}");
}

// ---------------------------------------------------------- 1. compile checks
var kernels = new Dictionary<string, ComputeShader>();
foreach (var ep in entryPoints)
{
    try
    {
        var source = File.ReadAllText(BakeShaderPath);
        var bytecode = ShaderBytecode.Compile(source, ep, "cs_5_0", ShaderFlags.None, EffectFlags.None, null, null);
        kernels[ep] = new ComputeShader(device, bytecode, null);
        Console.WriteLine($"compiled ok: {ep}");
    }
    catch (Exception e)
    {
        failures.Add($"compile {ep}: {e.Message}");
        Console.WriteLine($"COMPILE FAILED {ep}: {e.Message}");
    }
}

if (failures.Count == 0)
{
    // ------------------------------------------------------- 2. sphere mesh
    var (vertices, faces) = MakeSphere(radius: 0.7f, segments: 48, rings: 24);
    Console.WriteLine($"sphere: {vertices.Length} vertices {faces.Length} faces");

    Buffer vertexBuffer = null!;
    ResourceManager.SetupStructuredBuffer(vertices, PbrVertex.Stride * vertices.Length, PbrVertex.Stride, ref vertexBuffer);
    ShaderResourceView vertexSrv = null!;
    ResourceManager.CreateStructuredBufferSrv(vertexBuffer, ref vertexSrv);
    const int faceStride = 12;
    Buffer faceBuffer = null!;
    ResourceManager.SetupStructuredBuffer(faces, faceStride * faces.Length, faceStride, ref faceBuffer);
    ShaderResourceView faceSrv = null!;
    ResourceManager.CreateStructuredBufferSrv(faceBuffer, ref faceSrv);

    // ------------------------------------------------------- 3. bake volume
    int resolution = 64;
    var center = new System.Numerics.Vector3(0, 0, 0);
    var size = new System.Numerics.Vector3(2, 2, 2);
    const int insideVoteThreshold = 3;
    const float emptyDistance = 10f;
    string[] modeNames = ["JumpFlood_Signed", "ExactClosestPoint", "ExactClosestPointScatter", "JumpFlood_Unsigned"];

    var maxAxisSize = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
    var voxelScale = resolution / maxAxisSize;
    var dims = new Int3((int)MathF.Ceiling(size.X * voxelScale), (int)MathF.Ceiling(size.Y * voxelScale), (int)MathF.Ceiling(size.Z * voxelScale));
    Console.WriteLine($"volume: {dims.X}x{dims.Y}x{dims.Z}");

    var voxelsA = CreateVolume(device, dims, Format.R16G16B16A16_Float);
    var voxelsB = CreateVolume(device, dims, Format.R16G16B16A16_Float);
    var insideMask = CreateVolume(device, dims, Format.R32_Float);
    var sdf = CreateVolume(device, dims, Format.R16_Float);
    var scratch = CreateVolume(device, dims, Format.R32_UInt);

    var bakeParams = new BakeParams
    {
        VoxelSize = dims,
        KernelParam = (uint)faces.Length,
        VoxelOrigin = center - size / 2f,
        VoxelScale = voxelScale,
        SampleDensity = 4f,
        DistanceNormalization = MathF.Max(1, resolution),
        InsideVoteThreshold = insideVoteThreshold,
        MaxSamples = 8192,
        ClearValue = 0,
        Signed = 1f,
        SliceZ = 0,
    };
    var paramBuffer = CreateParamBuffer(device, bakeParams);

    var context = device.ImmediateContext;
    var csStage = context.ComputeShader;
    const int texVoxelRead = 2, texInsideMaskRead = 3, uavVoxelWrite = 0, uavInsideMask = 1, uavSdfWrite = 2;

    void UpdateParams(uint kernelParam, float clearValue, float signed)
    {
        bakeParams.KernelParam = kernelParam;
        bakeParams.ClearValue = clearValue;
        bakeParams.Signed = signed;
        context.UpdateSubresource(ref bakeParams, paramBuffer);
    }

    void DispatchVolume() => context.Dispatch((dims.X + 7) / 8, (dims.Y + 7) / 8, (dims.Z + 7) / 8);


    // --------------------------------------------------- 4. run all 3 modes
    var runs = new[]
    {
        (mode: 0, smoothing: 0f,  radius: 1f,   label: "JumpFlood_Signed"),
        (mode: 0, smoothing: 0.7f, radius: 1.5f, label: "JumpFlood_Signed+Smooth"),
        (mode: 1, smoothing: 0f,  radius: 1f,   label: "ExactClosestPoint"),
        (mode: 2, smoothing: 0f,  radius: 1f,   label: "ExactClosestPointScatter"),
        (mode: 3, smoothing: 0f,  radius: 1f,   label: "JumpFlood_Unsigned"),
    };
    double rawTv = 0;
    var hasRawTv = false;

    foreach (var run in runs)
    {
        var mode = run.mode;
        var runLabel = run.label;
        var signed = mode == 3 ? 0f : 1f;
        csStage.SetConstantBuffer(0, paramBuffer);
        Console.WriteLine($"\n=== {runLabel} ===");

        // clear sdf volume to empty state
        UpdateParams(0, emptyDistance, signed);
        csStage.Set(kernels["ClearSdf"]);
        csStage.SetUnorderedAccessView(uavSdfWrite, sdf.Uav);
        DispatchVolume();
        csStage.SetUnorderedAccessView(uavSdfWrite, null);

        // clear voxels + mask
        UpdateParams((uint)faces.Length, 0, signed);
        csStage.Set(kernels["ClearVoxels"]);
        csStage.SetUnorderedAccessView(0, voxelsA.Uav);
        csStage.SetUnorderedAccessView(1, insideMask.Uav);
        DispatchVolume();
        csStage.SetUnorderedAccessView(0, null);
        csStage.SetUnorderedAccessView(1, null);

        // voxelize
        csStage.Set(kernels["MeshToVoxel"]);
        csStage.SetShaderResources(0, new[] { vertexSrv, faceSrv });
        csStage.SetUnorderedAccessView(0, voxelsA.Uav);
        context.Dispatch((faces.Length + 127) / 128, 1, 1);
        csStage.SetShaderResource(0, null);
        csStage.SetShaderResource(1, null);
        csStage.SetUnorderedAccessView(0, null);

        // inside mask by parity voting (not needed for scatter and unsigned modes)
        if (mode is 0 or 1)
        {
            csStage.Set(kernels["ClassifyInsideX"]);
            csStage.SetShaderResource(texVoxelRead, voxelsA.Srv);
            csStage.SetUnorderedAccessView(uavInsideMask, insideMask.Uav);
            context.Dispatch(1, (dims.Y + 7) / 8, (dims.Z + 7) / 8);
            csStage.Set(kernels["ClassifyInsideY"]);
            context.Dispatch((dims.X + 7) / 8, 1, (dims.Z + 7) / 8);
            csStage.Set(kernels["ClassifyInsideZ"]);
            context.Dispatch((dims.X + 7) / 8, (dims.Y + 7) / 8, 1);
            csStage.SetShaderResource(texVoxelRead, null);
            csStage.SetUnorderedAccessView(uavInsideMask, null);
        }

        if (mode == 2)
        {
            // exact scatter (TressFX / vvvv VL.Fuse DomainExtensions method)
            UpdateParams((uint)faces.Length, emptyDistance, signed); // far value for the shell-less cells
            csStage.Set(kernels["InitSdfScatter"]);
            csStage.SetUnorderedAccessView(3, scratch.Uav);
            DispatchVolume();
            csStage.SetUnorderedAccessView(3, null);

            {
                var sc = ReadBackUint(device, scratch.Texture, dims);
                var flip10 = (0x41200000u << 1) | (0x41200000u >> 31);
                long init = 0;
                foreach (var v in sc) if (v == flip10) init++;
                Console.WriteLine($"DEBUG scatter after init: flipped(10.0)={flip10:X8} cells at init value: {init}");
            }

            csStage.Set(kernels["BakeSDFScatter"]);
            csStage.SetShaderResources(0, new[] { vertexSrv, faceSrv });
            csStage.SetUnorderedAccessView(3, scratch.Uav);
            context.Dispatch((faces.Length + 63) / 64, 1, 1);
            csStage.SetShaderResource(0, null);
            csStage.SetShaderResource(1, null);
            csStage.SetUnorderedAccessView(3, null);

            {
                var sc = ReadBackUint(device, scratch.Texture, dims);
                var c = dims.X / 2;
                uint vCenter = sc[c + dims.X * (c + dims.Y * c)];
                uint vCorner = sc[2 + dims.X * (2 + dims.Y * 2)];
                var vSurface = sc[c + 10 + dims.X * (c + dims.Y * c)];
                static float Unflip(uint x) { var r = (x >> 1) | (x << 31); return BitConverter.ToSingle(BitConverter.GetBytes(r)); }
                Console.WriteLine($"DEBUG scratch center={vCenter:X8} -> {Unflip(vCenter):0.000}  corner={vCorner:X8} -> {Unflip(vCorner):0.000}  nearSurface={vSurface:X8} -> {Unflip(vSurface):0.000}");
            }

            csStage.Set(kernels["FinalizeSdfScatter"]);
            csStage.SetShaderResource(4, scratch.Srv);
            csStage.SetUnorderedAccessView(uavSdfWrite, sdf.Uav);
            DispatchVolume();
            csStage.SetShaderResource(4, null);
            csStage.SetUnorderedAccessView(uavSdfWrite, null);
        }
        else if (mode == 1)
        {
            // exact closest point, one z slice per dispatch
            csStage.Set(kernels["ClosestPointBrute"]);
            csStage.SetShaderResources(0, new[] { vertexSrv, faceSrv });
            csStage.SetUnorderedAccessView(uavVoxelWrite, voxelsB.Uav);
            for (var z = 0; z < dims.Z; z++)
            {
                bakeParams.SliceZ = z;
                UpdateParams((uint)faces.Length, 0, signed);
                context.Dispatch((dims.X + 7) / 8, (dims.Y + 7) / 8, 1);
            }
            csStage.SetShaderResource(0, null);
            csStage.SetShaderResource(1, null);
            csStage.SetUnorderedAccessView(uavVoxelWrite, null);

            // apply inside sign
            bakeParams.SliceZ = 0;
            UpdateParams(0, 0, signed);
            csStage.Set(kernels["ApplyInsideSign"]);
            csStage.SetShaderResource(texVoxelRead, voxelsB.Srv);
            csStage.SetShaderResource(texInsideMaskRead, insideMask.Srv);
            csStage.SetUnorderedAccessView(uavSdfWrite, sdf.Uav);
            DispatchVolume();
            csStage.SetShaderResource(texVoxelRead, null);
            csStage.SetShaderResource(texInsideMaskRead, null);
            csStage.SetUnorderedAccessView(uavSdfWrite, null);
        }
        else
        {
            // jump flooding
            UpdateParams(0, 0, signed);
            csStage.Set(kernels["Preprocess"]);
            csStage.SetShaderResource(texVoxelRead, voxelsA.Srv);
            csStage.SetUnorderedAccessView(uavVoxelWrite, voxelsB.Uav);
            DispatchVolume();
            csStage.SetShaderResource(texVoxelRead, null);
            csStage.SetUnorderedAccessView(uavVoxelWrite, null);

            var read = voxelsB;
            var write = voxelsA;
            var maxSide = Math.Max(dims.X, Math.Max(dims.Y, dims.Z));
            csStage.Set(kernels["Jfa"]);
            for (var offset = Math.Max(1, NextPowerOfTwo(maxSide) / 2); offset >= 1; offset /= 2)
            {
                UpdateParams((uint)offset, 0, signed);
                csStage.SetShaderResource(texVoxelRead, read.Srv);
                csStage.SetUnorderedAccessView(uavVoxelWrite, write.Uav);
                DispatchVolume();
                csStage.SetShaderResource(texVoxelRead, null);
                csStage.SetUnorderedAccessView(uavVoxelWrite, null);
                (read, write) = (write, read);
            }

            UpdateParams(0, 0, signed);
            csStage.Set(kernels["Postprocess"]);
            csStage.SetShaderResource(texVoxelRead, read.Srv);
            csStage.SetShaderResource(texInsideMaskRead, insideMask.Srv);
            csStage.SetUnorderedAccessView(uavSdfWrite, sdf.Uav);
            DispatchVolume();
            csStage.SetShaderResource(texVoxelRead, null);
            csStage.SetShaderResource(texInsideMaskRead, null);
            csStage.SetUnorderedAccessView(uavSdfWrite, null);
        }

        if (run.smoothing > 0)
        {
            bakeParams.Smoothing = run.smoothing;
            bakeParams.SmoothingRadius = run.radius;
            UpdateParams((uint)faces.Length, 0, signed);
            csStage.SetSamplers(0, new[] { ResourceManager.DefaultSamplerState });

            csStage.Set(kernels["SmoothFirstPass"]);
            csStage.SetShaderResource(5, sdf.Srv);
            csStage.SetUnorderedAccessView(0, voxelsA.Uav);
            DispatchVolume();
            csStage.SetShaderResource(5, null);
            csStage.SetUnorderedAccessView(0, null);

            csStage.Set(kernels["SmoothPass"]);
            csStage.SetShaderResource(texVoxelRead, voxelsA.Srv);
            csStage.SetUnorderedAccessView(uavVoxelWrite, voxelsB.Uav);
            DispatchVolume();
            csStage.SetShaderResource(texVoxelRead, null);
            csStage.SetUnorderedAccessView(uavVoxelWrite, null);

            csStage.Set(kernels["SmoothCopyBack"]);
            csStage.SetShaderResource(texVoxelRead, voxelsB.Srv);
            csStage.SetUnorderedAccessView(uavSdfWrite, sdf.Uav);
            DispatchVolume();
            csStage.SetShaderResource(texVoxelRead, null);
            csStage.SetUnorderedAccessView(uavSdfWrite, null);

            csStage.SetSampler(0, null);
        }

        csStage.SetConstantBuffer(0, null);
        csStage.Set(null);

        // --------------------------------------------------- 5. checks
        var data = ReadBack(device, sdf.Texture, dims);
        Console.WriteLine($"read back {data.Length} floats");

        float Sample(float x, float y, float z)
        {
            var xi = Math.Clamp((int)(x * dims.X), 0, dims.X - 1);
            var yi = Math.Clamp((int)(y * dims.Y), 0, dims.Y - 1);
            var zi = Math.Clamp((int)(z * dims.Z), 0, dims.Z - 1);
            return data[xi + dims.X * (yi + dims.Y * zi)];
        }

        float AtWorldX(float x) => Sample(x / 2f + 0.5f, 0.5f, 0.5f);

        var scanline = new float[dims.X];
        for (var i = 0; i < dims.X; i++)
            scanline[i] = data[i + dims.X * (dims.Y / 2 + dims.Y * (dims.Z / 2))];
        var tv = 0.0;
        for (var i = 1; i < dims.X; i++)
            tv += Math.Abs(scanline[i] - scanline[i - 1]);

        if (mode == 0 && run.smoothing == 0)
        {
            rawTv = tv;
            hasRawTv = true;
        }

        void Check(string name, float actual, float expected, float tolerance)
        {
            var ok = MathF.Abs(actual - expected) <= tolerance;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  [{runLabel}] {name}: {actual:0.000} (expected {expected:0.000} ±{tolerance:0.000})");
            if (!ok)
                failures.Add($"[{runLabel}] {name}");
        }

        void CheckSign(string name, float actual, bool negative)
        {
            var ok = negative ? actual < 0 : actual > 0;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  [{runLabel}] {name}: {actual:0.000} (expected {(negative ? "negative" : "positive")})");
            if (!ok)
                failures.Add($"[{runLabel}] {name}");
        }

        if (mode == 0 && run.smoothing > 0)
        {
            Console.WriteLine($"DEBUG total variation: raw {rawTv:0.000} -> smoothed {tv:0.000}");
            Check("smoothing reduces blockiness", (float)(tv / Math.Max(rawTv, 1e-6)), 0.45f, 0.35f);
            Check("smoothing shrinks interior (has effect)", Sample(0.5f, 0.5f, 0.5f), -0.15f, 0.15f);
            Check("smoothing keeps surface", Sample(0.5f, 0.85f, 0.5f), 0f, 0.12f);
            CheckSign("smoothing keeps sign", Sample(0.5f, 0.5f, 0.5f), negative: true);
        }

        // sphere radius 0.7 world, volume size 2 -> normalized distance = worldDelta / 2
        var scatter = mode == 2;
        CheckSign("center inside", Sample(0.5f, 0.5f, 0.5f), negative: mode is 0 or 1 or 2);
        Check("center distance", Sample(0.5f, 0.5f, 0.5f),
            mode == 3 ? 0.35f : run.smoothing > 0 ? -0.1f : -0.35f, run.smoothing > 0 ? 0.12f : 0.05f);
        CheckSign("outside corner", Sample(0.02f, 0.02f, 0.02f), negative: false);
        // scatter mode keeps the max distance beyond the ~15 cell shell around the mesh
        Check("outside distance @corner", Sample(0.02f, 0.02f, 0.02f),
            scatter ? 10f : (1.6628f - 0.7f) / 2f, 0.6f);

        if (mode == 2)
        {
            // scatter: exact within the ~15 cell shell around the surface, far cells stay at the max distance
            Check("on-surface +Y", Sample(0.5f, 0.85f, 0.5f), 0f, 0.02f);
            Check("on-surface +Z", Sample(0.5f, 0.5f, 0.85f), 0f, 0.02f);
            Check("near-surface inside @world0.6", AtWorldX(0.6f), -0.05f, 0.03f);
            Check("near-surface outside @world0.9", AtWorldX(0.9f), 0.1f, 0.03f);
            Check("near-surface inside @world-0.6", AtWorldX(-0.6f), -0.05f, 0.03f);
            CheckSign("-Y shell inside", Sample(0.5f, 0.4f, 0.5f), negative: true);
        }
        else if (mode == 1)
        {
            // exact mode: distances to the true sphere surface are tighter
            Check("inside distance @world0.3", AtWorldX(0.3f), -0.2f, 0.02f);
            Check("inside distance @world-0.4", AtWorldX(-0.4f), -0.15f, 0.02f);
            Check("distance ramp world0.2", AtWorldX(0.2f), -0.25f, 0.02f);
            Check("distance ramp world0.9", AtWorldX(0.9f), 0.1f, 0.02f);
            Check("on-surface +Y", Sample(0.5f, 0.85f, 0.5f), 0f, 0.02f);
            Check("on-surface +Z", Sample(0.5f, 0.5f, 0.85f), 0f, 0.02f);
            CheckSign("-Y inside", Sample(0.5f, 0.35f, 0.5f), negative: true);
            CheckSign("-Z inside", Sample(0.5f, 0.5f, 0.35f), negative: true);
        }
        else
        {
            var sign = mode == 3 ? 1f : -1f; // unsigned mode: inside distances are positive
            if (run.smoothing == 0)
            {
            Check("inside distance @world0.3", AtWorldX(0.3f), sign * 0.2f, 0.05f);
            Check("inside distance @world-0.4", AtWorldX(-0.4f), sign * 0.15f, 0.05f);
            CheckSign("outside @world-0.8", AtWorldX(-0.8f), negative: false);
            Check("outside distance @world-0.8", AtWorldX(-0.8f), 0.05f, 0.04f);
            Check("distance ramp world0.2", AtWorldX(0.2f), sign * 0.25f, 0.05f);
            Check("distance ramp world0.9", AtWorldX(0.9f), 0.1f, 0.05f);
            Check("on-surface +Y", Sample(0.5f, 0.85f, 0.5f), 0f, 0.06f);
            Check("on-surface +Z", Sample(0.5f, 0.5f, 0.85f), 0f, 0.06f);
            CheckSign("-Y magnitude flips", Sample(0.5f, 0.35f, 0.5f), negative: mode != 3);
            CheckSign("-Z magnitude flips", Sample(0.5f, 0.5f, 0.35f), negative: mode != 3);
            }
        }
    }
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("ALL CHECKS PASSED");
    return 0;
}

Console.WriteLine($"FAILED: {failures.Count} checks:");
foreach (var f in failures)
    Console.WriteLine("  - " + f);
return 1;

// ---------------------------------------------------------------- helpers
static (PbrVertex[] vertices, Int3[] faces) MakeSphere(float radius, int segments, int rings)
{
    var vertexList = new List<PbrVertex>();
    for (var y = 0; y <= rings; y++)
    {
        var v = (float)y / rings;
        var phi = v * MathF.PI;
        for (var x = 0; x <= segments; x++)
        {
            var u = (float)x / segments;
            var theta = u * MathF.Tau;
            var nx = MathF.Sin(phi) * MathF.Cos(theta);
            var ny = MathF.Cos(phi);
            var nz = MathF.Sin(phi) * MathF.Sin(theta);
            vertexList.Add(new PbrVertex
            {
                Position = new System.Numerics.Vector3(nx, ny, nz) * radius,
                Normal = new System.Numerics.Vector3(nx, ny, nz),
                Selection = 1,
                ColorRgb = System.Numerics.Vector3.One,
            });
        }
    }

    var idx = (int ix, int iy) => iy * (segments + 1) + ix;
    var faceList = new List<Int3>();
    for (var y = 0; y < rings; y++)
    {
        for (var x = 0; x < segments; x++)
        {
            var a = idx(x, y);
            var b = idx(x + 1, y);
            var c = idx(x, y + 1);
            var d = idx(x + 1, y + 1);
            faceList.Add(new Int3(a, c, b));
            faceList.Add(new Int3(b, c, d));
        }
    }

    return (vertexList.ToArray(), faceList.ToArray());
}

static Volume CreateVolume(Device device, Int3 dims, Format format)
{
    var texture = new Texture3D(device, new Texture3DDescription
    {
        Width = dims.X,
        Height = dims.Y,
        Depth = dims.Z,
        MipLevels = 1,
        Format = format,
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
        CpuAccessFlags = CpuAccessFlags.None,
        OptionFlags = ResourceOptionFlags.None,
    });
    var srv = new ShaderResourceView(device, texture);
    var uav = new UnorderedAccessView(device, texture);
    return new Volume(texture, srv, uav);
}

static Buffer CreateParamBuffer(Device device, BakeParams p)
{
    var buffer = new Buffer(device, new BufferDescription
    {
        Usage = ResourceUsage.Default,
        SizeInBytes = 80,
        BindFlags = BindFlags.ConstantBuffer,
        CpuAccessFlags = CpuAccessFlags.None,
    });
    device.ImmediateContext.UpdateSubresource(ref p, buffer);
    return buffer;
}

static uint[] ReadBackUint(Device device, Texture3D texture, Int3 dims)
{
    var stagingDesc = new Texture3DDescription
    {
        Width = dims.X, Height = dims.Y, Depth = dims.Z, MipLevels = 1,
        Format = Format.R32_UInt,
        Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
        CpuAccessFlags = CpuAccessFlags.Read, OptionFlags = ResourceOptionFlags.None,
    };
    using var staging = new Texture3D(device, stagingDesc);
    var context = device.ImmediateContext;
    context.CopyResource(texture, staging);
    var box = context.MapSubresource(staging, 0, MapMode.Read, MapFlags.None);
    var result = new uint[dims.X * dims.Y * dims.Z];
    unsafe
    {
        var s = (uint*)box.DataPointer;
        for (var i = 0; i < result.Length; i++) result[i] = s[i];
    }
    context.UnmapSubresource(staging, 0);
    return result;
}

static float[] ReadBack(Device device, Texture3D texture, Int3 dims)
{
    var stagingDesc = new Texture3DDescription
    {
        Width = dims.X,
        Height = dims.Y,
        Depth = dims.Z,
        MipLevels = 1,
        Format = texture.Description.Format,
        Usage = ResourceUsage.Staging,
        BindFlags = BindFlags.None,
        CpuAccessFlags = CpuAccessFlags.Read,
        OptionFlags = ResourceOptionFlags.None,
    };
    using var staging = new Texture3D(device, stagingDesc);
    var context = device.ImmediateContext;
    context.CopyResource(texture, staging); // SharpDX-on-net10 quirk: (source, destination)
    var box = context.MapSubresource(staging, 0, MapMode.Read, MapFlags.None);
    var result = new float[dims.X * dims.Y * dims.Z];
    unsafe
    {
        var src = (Half*)box.DataPointer;
        fixed (float* dst = result)
        {
            for (var z = 0; z < dims.Z; z++)
            {
                var srcSlice = (Half*)((byte*)src + z * box.SlicePitch);
                for (var y = 0; y < dims.Y; y++)
                {
                    var srcRow = (Half*)((byte*)srcSlice + y * box.RowPitch);
                    for (var x = 0; x < dims.X; x++)
                        dst[x + dims.X * (y + dims.Y * z)] = (float)srcRow[x];
                }
            }
        }
    }
    context.UnmapSubresource(staging, 0);
    return result;
}

static int NextPowerOfTwo(int value)
{
    var p = 1;
    while (p < value)
        p <<= 1;
    return p;
}

internal struct BakeParams
{
    public Int3 VoxelSize;
    public uint KernelParam;
    public System.Numerics.Vector3 VoxelOrigin;
    public float VoxelScale;
    public float SampleDensity;
    public float DistanceNormalization;
    public int InsideVoteThreshold;
    public int MaxSamples;
    public float ClearValue;
    public float Signed;
    public float SliceZ;
    public float Smoothing;
    public float SmoothingRadius;
    public float Padding3;
    public float Padding4;
    public float Padding5;
}

internal sealed record Volume(Texture3D Texture, ShaderResourceView Srv, UnorderedAccessView Uav) : IDisposable
{
    public void Dispose()
    {
        Srv.Dispose();
        Uav.Dispose();
        Texture.Dispose();
    }
}
