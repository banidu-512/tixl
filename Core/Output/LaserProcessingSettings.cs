#nullable enable
using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using T3.Core.DataTypes;
using T3.Serialization;

namespace T3.Core.Output;

/// <summary>
/// The laser pipeline configuration carried by a laser output: the optimizer and safety settings
/// that the output operators apply when they stream for this output — the setup-owned replacement
/// for graph-side ops — it is edited in the Output Setup. Safety starts <see cref="SafetySettings.Calibrated"/>
/// = false: until the venue is aimed, only the manual alignment rectangle streams.
/// </summary>
public sealed class LaserProcessingSettings
{
    public OptimizerSettings Optimizer = new();
    public SafetySettings Safety = new();

    public static LaserProcessingSettings Default => new();

    public void WriteToJson(JsonTextWriter writer)
    {
        writer.WriteStartObject();

        writer.WritePropertyName("Optimizer");
        writer.WriteStartObject();
        writer.WriteValue("EnableOptimization", Optimizer.EnableOptimization);
        writer.WriteValue("MaxJumpDistance", Optimizer.MaxJumpDistance);
        writer.WriteValue("BlankingDelayPoints", Optimizer.BlankingDelayPoints);
        writer.WriteValue("CornerAngleThreshold", Optimizer.CornerAngleThreshold);
        writer.WriteValue("CornerAnchorCount", Optimizer.CornerAnchorCount);
        writer.WriteEndObject();

        writer.WritePropertyName("Safety");
        writer.WriteStartObject();
        writer.WriteValue("Calibrated", Safety.Calibrated);
        writer.WriteValue("BeamEnable", Safety.BeamEnable);
        writer.WriteValue("SafeZoneCenterX", Safety.SafeZoneCenterX);
        writer.WriteValue("SafeZoneCenterY", Safety.SafeZoneCenterY);
        writer.WriteValue("SafeZoneWidth", Safety.SafeZoneWidth);
        writer.WriteValue("SafeZoneHeight", Safety.SafeZoneHeight);
        writer.WriteValue("MaxStepPerPoint", Safety.MaxStepPerPoint);
        writer.WriteValue("MaxStaticPoints", Safety.MaxStaticPoints);
        writer.WriteValue("MaxBrightness", Safety.MaxBrightness);
        writer.WriteValue("SetupCenterX", Safety.SetupCenterX);
        writer.WriteValue("SetupCenterY", Safety.SetupCenterY);
        writer.WriteValue("SetupWidth", Safety.SetupWidth);
        writer.WriteValue("SetupHeight", Safety.SetupHeight);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    public static LaserProcessingSettings ReadFromJson(JToken token)
    {
        var settings = new LaserProcessingSettings();
        var optimizer = token["Optimizer"];
        if (optimizer != null)
        {
            settings.Optimizer = new OptimizerSettings
                                     {
                                         EnableOptimization = optimizer.ReadValueSafe("EnableOptimization", true),
                                         MaxJumpDistance = optimizer.ReadValueSafe("MaxJumpDistance", 5000f),
                                         BlankingDelayPoints = optimizer.ReadValueSafe("BlankingDelayPoints", 5),
                                         CornerAngleThreshold = optimizer.ReadValueSafe("CornerAngleThreshold", 30f),
                                         CornerAnchorCount = optimizer.ReadValueSafe("CornerAnchorCount", 3),
                                     };
        }

        var safety = token["Safety"];
        if (safety != null)
        {
            settings.Safety = new SafetySettings
                                  {
                                      // Absent = false: an uncalibrated laser streams only the alignment rectangle.
                                      Calibrated = safety.ReadValueSafe("Calibrated", false),
                                      BeamEnable = safety.ReadValueSafe("BeamEnable", true),
                                      SafeZoneCenterX = safety.ReadValueSafe("SafeZoneCenterX", 0f),
                                      SafeZoneCenterY = safety.ReadValueSafe("SafeZoneCenterY", 0f),
                                      SafeZoneWidth = safety.ReadValueSafe("SafeZoneWidth", 65534f),
                                      SafeZoneHeight = safety.ReadValueSafe("SafeZoneHeight", 65534f),
                                      MaxStepPerPoint = safety.ReadValueSafe("MaxStepPerPoint", 4000f),
                                      MaxStaticPoints = safety.ReadValueSafe("MaxStaticPoints", 64),
                                      MaxBrightness = safety.ReadValueSafe("MaxBrightness", 1f),
                                      SetupCenterX = safety.ReadValueSafe("SetupCenterX", 0f),
                                      SetupCenterY = safety.ReadValueSafe("SetupCenterY", 0f),
                                      SetupWidth = safety.ReadValueSafe("SetupWidth", 0.04f),
                                      SetupHeight = safety.ReadValueSafe("SetupHeight", 0.04f),
                                  };
        }

        return settings;
    }

    /// <summary>Path-ordering and blanking stage.</summary>
    public sealed class OptimizerSettings
    {
        public bool EnableOptimization = true;
        public float MaxJumpDistance = 5000f;
        public int BlankingDelayPoints = 5;
        public float CornerAngleThreshold = 30f;
        public int CornerAnchorCount = 3;

        public LaserOptimizerTransforms.Config ToConfig() => new()
                                                                 {
                                                                     EnableOptimization = EnableOptimization,
                                                                     MaxJumpDistance = MaxJumpDistance,
                                                                     BlankingDelayPoints = BlankingDelayPoints,
                                                                     CornerAngleThreshold = CornerAngleThreshold,
                                                                     CornerAnchorCount = CornerAnchorCount,
                                                                 };
    }

    /// <summary>Safety stage; <see cref="Calibrated"/>
    /// gates content vs. the manual alignment rectangle.</summary>
    public sealed class SafetySettings
    {
        public bool Calibrated;
        public bool BeamEnable = true;

        /// <summary>Safe-zone rect in ILDA units (±32767 field).</summary>
        public float SafeZoneCenterX;
        public float SafeZoneCenterY;
        public float SafeZoneWidth = 65534f;
        public float SafeZoneHeight = 65534f;

        public float MaxStepPerPoint = 4000f;
        public int MaxStaticPoints = 64;

        /// <summary>Show-level intensity ceiling (0..1); the plug's device ceiling stays the final audit.</summary>
        public float MaxBrightness = 1f;

        /// <summary>Manual alignment rectangle, in normalized field units (-1..1 center, 0..1 half-extents).</summary>
        public float SetupCenterX;
        public float SetupCenterY;
        public float SetupWidth = 0.04f;
        public float SetupHeight = 0.04f;

        public LaserSafetyTransforms.Config ToConfig() => new()
                                                              {
                                                                  BeamEnable = BeamEnable,
                                                                  ZoneCenterX = SafeZoneCenterX,
                                                                  ZoneCenterY = SafeZoneCenterY,
                                                                  ZoneHalfWidth = MathF.Max(0f, SafeZoneWidth) * 0.5f,
                                                                  ZoneHalfHeight = MathF.Max(0f, SafeZoneHeight) * 0.5f,
                                                                  MaxStepPerPoint = Math.Clamp(MaxStepPerPoint, 100f, 65535f),
                                                                  MaxStaticPoints = Math.Clamp(MaxStaticPoints, 1, 100000),
                                                                  StaticDistance = 5,
                                                                  MaxBrightness = Math.Clamp(MaxBrightness, 0f, 1f),
                                                              };

        public string DescribeSetup() =>
            $"Safety setup (manual): {SetupWidth:F2} x {SetupHeight:F2} field at ({SetupCenterX:F2}, {SetupCenterY:F2})"
            + " - switch Calibrated on for content";
    }
}
