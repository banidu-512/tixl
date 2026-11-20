using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Lib.io.video.depthanything.Utils;
using OpenCvSharp;

var dir = args[0]; var prefix = args[1]; var count = int.Parse(args[2]);
const int W = 640, H = 360;
var fx = (float)(W / 2.0 / Math.Tan(63.5 * Math.PI / 360.0));
ImageRotationTracker tracker;
try { tracker = new ImageRotationTracker(); }
catch (Exception e) { Console.WriteLine(e.ToString().Substring(0, Math.Min(1500, e.ToString().Length))); Console.WriteLine("INNER: " + e.InnerException); return; }
for (var i = 0; i < count; i++)
{
    using var img = Cv2.ImRead(Path.Combine(dir, $"{prefix}{i}.jpg"), ImreadModes.Grayscale);
    img.GetArray(out byte[] data);
    tracker.Update(data, img.Width, img.Height, fx, fx, W / 2f, H / 2f);
    var f = Vector3.Transform(Vector3.UnitZ, tracker.Rotation);
    var yaw = Math.Atan2(f.X, f.Z) * 180 / Math.PI;
    var pitch = Math.Asin(Math.Clamp(-f.Y, -1, 1)) * 180 / Math.PI;
    Console.WriteLine($"{i,2} {tracker.LastSource,-9} inl {tracker.LastInliers,3} yaw {yaw,7:F1} pitch {pitch,7:F1}");
}
