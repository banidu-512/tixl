using ArtNet.Packets;
using ArtNet.Sockets;
// ReSharper disable MemberCanBePrivate.Global

namespace Lib.io.artnet;

[Guid("98efc7c8-cafd-45ee-8746-14f37e9f59f8")]
internal sealed class ArtnetOutput : Instance<ArtnetOutput>,IStatusProvider
{
    [Output(Guid = "499329d0-15e9-410e-9f61-63724dbec937")]
public readonly Slot<Command> Result = new();

    public ArtnetOutput()
    {
        Result.UpdateAction += Update;
    }
        
    private void Update(EvaluationContext context)
    {
        var startUniverse = StartUniverse.GetValue(context);
        var connections = Input.GetCollectedTypedInputs();
        var ipAddressString = IpAddress.GetValue(context).Trim();
        var reconnect = Reconnect.GetValue(context);
        //var broadcast = Broadcast.GetValue(context);

        if (reconnect)
        {
            Reconnect.SetTypedInputValue(false);
        }

        var ipAddressChanged = ipAddressString != _lastIpAddressString;
        if (ipAddressChanged)
        {
            _lastIpAddressString = ipAddressString;
            if (!TryGetValidAddress(ipAddressString, out var error, out _newIpAddress))
            {
                _lastErrorMessage = error;
                return;
            }
        }
        
        if (_connected)
        {
            var targetChanged = ipAddressChanged;
            if (targetChanged || reconnect)
            {
                Console.WriteLine("Reconnecting...");
                _sender.Close();
                _connected = TryConnectArtnet(_newIpAddress);
            }
        }
        else if (_newIpAddress != null)
        {
            _connected = TryConnectArtnet(_newIpAddress);
        }

        // Send DMX data across universes
        const int chunkSize = 512;
        int universeIndex = startUniverse;

        foreach (var input in connections)
        {
            var buffer = input.GetValue(context);
            int bufferCount = buffer.Count;

            // Process in chunks
            for (int i = 0; i < bufferCount; i += chunkSize)
            {
                int currentChunkSize = Math.Min(chunkSize, bufferCount - i);
                var dmxData = buffer.Skip(i).Take(currentChunkSize).ToList();

                var dmxPacket = new ArtNetDmxPacket
                {
                    DmxData = ConvertListToByteArray(dmxData),
                    Universe = (short)universeIndex
                };

                // Sending DMX data
                _sender.Send(dmxPacket);

                universeIndex++;
            }
        }
    }
    

    protected override void Dispose(bool isDisposing)
    {
        if (isDisposing && _connected) _sender.Dispose();
        Console.WriteLine(@"dispose");
    }

    private static bool TryGetValidAddress(string ipAddressString, out string error, out IPAddress ipAddress)
    {
        if (IPAddress.TryParse(ipAddressString, out ipAddress))
        {
            error = null;
            return true;
        }

        error = $"Failed to parse ip: {ipAddressString}";
        return false;
    }

    private bool TryConnectArtnet(IPAddress ipAddress)
    {

        if (ipAddress == null)
            return false;

        try
        {
            _sender = new ArtNetSocket();
            _sender.EnableBroadcast = true;
            //_sender.Open(ipAddress, IPAddress.Parse("255.0.0.0"));
        }
        catch (Exception)
        {
            _lastErrorMessage = $"Failed to connect to {ipAddress}";
        }

        return false;
    }

    static byte[] ConvertListToByteArray(List<float> intList)
    {
        byte[] byteArray = new byte[intList.Count];

        for (int i = 0; i < intList.Count; i++)
        {
            int clampedValue = (int)Math.Clamp(intList[i], 0, 255);
            byteArray[i] = (byte)clampedValue;
        }

        return byteArray;
    }


    private ArtNetSocket _sender;
    private string _lastIpAddressString;
    private bool _connected;
    private IPAddress _newIpAddress;
    //private bool _broadcast;






    public IStatusProvider.StatusLevel GetStatusLevel()
    {
        return string.IsNullOrEmpty(_lastErrorMessage) ? IStatusProvider.StatusLevel.Success : IStatusProvider.StatusLevel.Warning;
    }

    string IStatusProvider.GetStatusMessage() => _lastErrorMessage;
    private string _lastErrorMessage;

        [Input(Guid = "34aeeda5-72b0-4f13-bfd3-4ad5cf42b24f")]
        public readonly InputSlot<int> StartUniverse = new InputSlot<int>();

        [Input(Guid = "fcbfe87b-b8aa-461c-a5ac-b22bb29ad36d")]
        public readonly InputSlot<string> IpAddress = new InputSlot<string>();

        [Input(Guid = "c81d7178-34db-48c9-b0e2-ca7014747bc8")]
        public readonly InputSlot<bool> Broadcast = new InputSlot<bool>();

        [Input(Guid = "ad5cfe4a-f09a-4083-984c-5c60dca3e603")]
        public readonly InputSlot<bool> OnlySendChanges = new InputSlot<bool>();

        [Input(Guid = "168d0023-554f-46cd-9e62-8f3d1f564b8d")]
        public readonly InputSlot<bool> SendTrigger = new InputSlot<bool>();

        [Input(Guid = "73babdb1-f88f-4e4d-aa3f-0536678b0793")]
        public readonly InputSlot<bool> Reconnect = new InputSlot<bool>();

        [Input(Guid = "e2caf182-de22-4769-9b3c-9d75c53972a7")]
        public readonly MultiInputSlot<List<float>> Input = new MultiInputSlot<List<float>>();
}