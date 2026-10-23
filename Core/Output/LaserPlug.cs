#nullable enable
using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using T3.Serialization;

namespace T3.Core.Output;

/// <summary>
/// A laser device this machine can send to as a plug next to its displays and streams — an EtherDream DAC
/// on the LAN (IP address), a PONK endpoint (MadMapper's laser-over-UDP, usually the 239.255.10.24:5583
/// multicast group) or a CITP laser feed consumer. Part of the <see cref="MachineConfig"/> inventory, so a
/// show rebinds its laser outputs to the devices found at the venue without touching the versioned setup.
/// Per-protocol device settings (scan rate, brightness ceiling, channel order…) live here next to the
/// address — the machine-side counterpart of the output-side laser processing settings.
/// </summary>
public sealed class LaserPlug
{
    public static class Kinds
    {
        public const string EtherDream = "EtherDream";
        public const string Ponk = "PONK";
        public const string Citp = "CITP";
    }

    public Guid Id = Guid.NewGuid();

    /// <summary>Which protocol drives the device; decides which output op can bind here.</summary>
    public string Kind = Kinds.EtherDream;

    /// <summary>The name the binding menu shows.</summary>
    public string Name = string.Empty;

    /// <summary>The DAC's IP address (EtherDream), the PONK host / multicast group, or the CITP capture host.</summary>
    public string Address = string.Empty;

    /// <summary>Remote port; 0 = the protocol's default (PONK 5583; CITP 0 = multicast discovery).</summary>
    public int Port;

    /// <summary>Network interface packets leave on (IP of this machine's NIC); empty = any.</summary>
    public string LocalAddress = string.Empty;

    /// <summary>Per-protocol device settings; null = defaults. Only the block for <see cref="Kind"/> is used.</summary>
    public EtherDreamDeviceSettings? EtherDream;
    public PonkDeviceSettings? Ponk;
    public CitpDeviceSettings? Citp;

    public EtherDreamDeviceSettings EtherDreamSettings => EtherDream ??= new EtherDreamDeviceSettings();
    public PonkDeviceSettings PonkSettings => Ponk ??= new PonkDeviceSettings();
    public CitpDeviceSettings CitpSettings => Citp ??= new CitpDeviceSettings();

    public void WriteToJson(JsonTextWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteObject("Id", Id);
        writer.WriteString("Kind", Kind);
        writer.WriteString("Name", Name);
        writer.WriteString("Address", Address);
        writer.WriteString("LocalAddress", LocalAddress);
        if (Port != 0)
            writer.WriteValue("Port", Port);

        if (EtherDream != null)
        {
            writer.WritePropertyName("EtherDream");
            EtherDream.WriteToJson(writer);
        }

        if (Ponk != null)
        {
            writer.WritePropertyName("Ponk");
            Ponk.WriteToJson(writer);
        }

        if (Citp != null)
        {
            writer.WritePropertyName("Citp");
            Citp.WriteToJson(writer);
        }

        writer.WriteEndObject();
    }

    public static LaserPlug ReadFromJson(JToken token)
    {
        return new LaserPlug
                   {
                       Id = OutputJson.ReadGuid(token["Id"]),
                       Kind = token.ReadValueSafe("Kind", Kinds.EtherDream) ?? Kinds.EtherDream,
                       Name = token.ReadValueSafe("Name", string.Empty) ?? string.Empty,
                       Address = token.ReadValueSafe("Address", string.Empty) ?? string.Empty,
                       LocalAddress = token.ReadValueSafe("LocalAddress", string.Empty) ?? string.Empty,
                       Port = token.ReadValueSafe("Port", 0),
                       EtherDream = token["EtherDream"] is JObject ed ? EtherDreamDeviceSettings.ReadFromJson(ed) : null,
                       Ponk = token["Ponk"] is JObject po ? PonkDeviceSettings.ReadFromJson(po) : null,
                       Citp = token["Citp"] is JObject ci ? CitpDeviceSettings.ReadFromJson(ci) : null,
                   };
    }
}

/// <summary>EtherDream DAC device settings — the knobs of the hardware, not of the show.</summary>
public sealed class EtherDreamDeviceSettings
{
    /// <summary>Points per second requested from the DAC; 0 = the output op's own input applies.</summary>
    public int ScanRate;

    /// <summary>Final intensity ceiling (0..1) applied at encode — the non-bypassable device audit.</summary>
    public float MaxBrightness = 1f;

    /// <summary>Some projectors expect the color channels as BGR.</summary>
    public bool SwapColorChannels;

    public void WriteToJson(JsonTextWriter writer)
    {
        writer.WriteStartObject();
        if (ScanRate != 0)
            writer.WriteValue("ScanRate", ScanRate);

        writer.WriteValue("MaxBrightness", MaxBrightness);
        writer.WriteValue("SwapColorChannels", SwapColorChannels);
        writer.WriteEndObject();
    }

    public static EtherDreamDeviceSettings ReadFromJson(JToken token)
    {
        return new EtherDreamDeviceSettings
                   {
                       ScanRate = token.ReadValueSafe("ScanRate", 0),
                       MaxBrightness = token.ReadValueSafe("MaxBrightness", 1f),
                       SwapColorChannels = token.ReadValueSafe("SwapColorChannels", false),
                   };
    }
}

/// <summary>PONK receiver device settings.</summary>
public sealed class PonkDeviceSettings
{
    public bool UseMulticast = true;

    /// <summary>The sender name receivers see.</summary>
    public string SenderName = "TiXL";

    /// <summary>Sent as MAXSPEED path metadata — requested scanner-speed multiplier.</summary>
    public float MaxScanSpeed = 1f;

    public float MaxBrightness = 1f;
    public bool SwapColorChannels;

    public void WriteToJson(JsonTextWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteValue("UseMulticast", UseMulticast);
        writer.WriteString("SenderName", SenderName);
        writer.WriteValue("MaxScanSpeed", MaxScanSpeed);
        writer.WriteValue("MaxBrightness", MaxBrightness);
        writer.WriteValue("SwapColorChannels", SwapColorChannels);
        writer.WriteEndObject();
    }

    public static PonkDeviceSettings ReadFromJson(JToken token)
    {
        return new PonkDeviceSettings
                   {
                       UseMulticast = token.ReadValueSafe("UseMulticast", true),
                       SenderName = token.ReadValueSafe("SenderName", "TiXL") ?? "TiXL",
                       MaxScanSpeed = token.ReadValueSafe("MaxScanSpeed", 1f),
                       MaxBrightness = token.ReadValueSafe("MaxBrightness", 1f),
                       SwapColorChannels = token.ReadValueSafe("SwapColorChannels", false),
                   };
    }
}

/// <summary>CITP laser feed device settings.</summary>
public sealed class CitpDeviceSettings
{
    /// <summary>Feed names sent to the capture ("FeedName i" per connected feed).</summary>
    public string FeedName = "Laser Feed";

    /// <summary>CITP sender key (uint32); 0 = generate one at random per session.</summary>
    public uint SourceKey;

    public float MaxBrightness = 1f;
    public bool SwapColorChannels;

    public void WriteToJson(JsonTextWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("FeedName", FeedName);
        writer.WriteValue("SourceKey", SourceKey);
        writer.WriteValue("MaxBrightness", MaxBrightness);
        writer.WriteValue("SwapColorChannels", SwapColorChannels);
        writer.WriteEndObject();
    }

    public static CitpDeviceSettings ReadFromJson(JToken token)
    {
        return new CitpDeviceSettings
                   {
                       FeedName = token.ReadValueSafe("FeedName", "Laser Feed") ?? "Laser Feed",
                       SourceKey = token.ReadValueSafe("SourceKey", 0u),
                       MaxBrightness = token.ReadValueSafe("MaxBrightness", 1f),
                       SwapColorChannels = token.ReadValueSafe("SwapColorChannels", false),
                   };
    }
}
