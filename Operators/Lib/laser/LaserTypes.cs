using System;
using System.Runtime.InteropServices;
using T3.Core.DataTypes;

namespace Lib.laser;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct DacPoint
{
    public ushort Control;
    public short X;
    public short Y;
    public ushort R;
    public ushort G;
    public ushort B;
    public ushort I;
    public ushort U1;
    public ushort U2;

    public const int Size = 18;

    public DacPoint(LaserPoint laserPoint)
    {
        Control = (ushort)((laserPoint.I > 0) ? 0 : 0x40);
        X = (short)Math.Clamp(laserPoint.X, -32768, 32767);
        Y = (short)Math.Clamp(laserPoint.Y, -32768, 32767);
        R = (ushort)Math.Clamp(laserPoint.R, 0, 65535);
        G = (ushort)Math.Clamp(laserPoint.G, 0, 65535);
        B = (ushort)Math.Clamp(laserPoint.B, 0, 65535);
        I = (ushort)Math.Clamp(laserPoint.I, 0, 65535);
        U1 = 0;
        U2 = 0;
    }
}
