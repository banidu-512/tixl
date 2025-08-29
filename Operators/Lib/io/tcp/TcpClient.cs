#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using T3.Core.Logging;
using T3.Core.Operator;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Slots;
using T3.Core.Utils;

namespace Lib.io.tcp
{
    [Guid("2B1C3D4E-5F6A-7B8C-9D0E-1F2A3B4C5D6E")]
    internal sealed class TcpClient : Instance<TcpClient>
,IStatusProvider,IDisposable
    {
        [Output(Guid = "E7D6C5B4-A987-6543-210F-EDCBA9876543", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
        public readonly Slot<string> ReceivedString = new();

        [Output(Guid = "B2C3D4E5-F6A7-B8C9-D0E1-F2A3B4C5D6E7", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
        public readonly Slot<List<string>> ReceivedLines = new();

        [Output(Guid = "C9D8E7F6-A5B4-3210-FEDC-BA9876543210", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
        public readonly Slot<bool> WasTrigger = new();

        [Output(Guid = "BCDE1234-5678-90AB-CDEF-1234567890AB")]
        public readonly Slot<bool> IsConnected = new();

        public TcpClient()
        {
            ReceivedString.UpdateAction = Update;
            ReceivedLines.UpdateAction = Update;
            WasTrigger.UpdateAction = Update;
            IsConnected.UpdateAction = Update;
        }

        private TcpClientSocket? _socket;
        private CancellationTokenSource? _cts;
        private bool _lastConnectState;
        private string? _lastHost;
        private int _lastPort;
        private string? _lastSentMessage;
        private readonly object _socketLock = new();
        private readonly ConcurrentQueue<string> _receivedQueue = new();
        private readonly List<string> _messageHistory = new();
        private bool _printToLog;
        private bool _disposed;

        private void Update(EvaluationContext context)
        {
            if (_disposed)
                return;

            _printToLog = PrintToLog.GetValue(context);
            var shouldConnect = Connect.GetValue(context);
            var host = Host.GetValue(context);
            var port = Port.GetValue(context);

            var settingsChanged = shouldConnect != _lastConnectState || host != _lastHost || port != _lastPort;
            if (settingsChanged)
            {
                _ = HandleConnectionChange(shouldConnect, host, port);
            }

            HandleMessageSending(context);
            HandleReceivedMessages(context);
            UpdateStatusMessage();
        }

        private async Task HandleConnectionChange(bool shouldConnect, string? host, int port)
        {
            await StopAsync();
            _lastConnectState = shouldConnect;
            _lastHost = host;
            _lastPort = port;

            if (shouldConnect && !string.IsNullOrWhiteSpace(host))
            {
                await StartAsync(host, port);
            }
        }

        private void HandleMessageSending(EvaluationContext context)
        {
            var separator = Separator.GetValue(context) ?? "";
            var messageParts = MessageParts.GetCollectedTypedInputs().Select(p => p.GetValue(context));
            var currentMessage = string.Join(separator, messageParts);
            var hasMessageChanged = currentMessage != _lastSentMessage;
            var manualTrigger = SendTrigger.GetValue(context);
            var sendOnChange = SendOnChange.GetValue(context);
            var shouldSend = manualTrigger || (sendOnChange && hasMessageChanged);

            if (IsConnected.Value && shouldSend && !string.IsNullOrEmpty(currentMessage))
            {
                if (manualTrigger)
                    SendTrigger.SetTypedInputValue(false);

                _ = SendMessageAsync(currentMessage);
                _lastSentMessage = currentMessage;
            }
        }

        private void HandleReceivedMessages(EvaluationContext context)
        {
            var listLength = ListLength.GetValue(context).Clamp(1, 1000);
            var wasTriggered = false;

            while (_receivedQueue.TryDequeue(out var msg))
            {
                ReceivedString.Value = msg;
                _messageHistory.Add(msg);
                wasTriggered = true;
            }

            while (_messageHistory.Count > listLength)
                _messageHistory.RemoveAt(0);

            ReceivedLines.Value = new List<string>(_messageHistory);
            WasTrigger.Value = wasTriggered;
            IsConnected.Value = _socket?.IsConnected ?? false;
        }

        private async Task StartAsync(string host, int port)
        {
            lock (_socketLock)
            {
                _socket?.Dispose();
                _socket = new TcpClientSocket();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
            }

            try
            {
                SetStatus($"Connecting to {host}:{port}...", IStatusProvider.StatusLevel.Notice);
                await _socket!.ConnectAsync(host, port, _cts.Token);
                SetStatus($"Connected to {host}:{port}", IStatusProvider.StatusLevel.Success);

                _ = Task.Run(ReceiveLoop);
            }
            catch (Exception e)
            {
                SetStatus($"Connect failed: {e.Message}", IStatusProvider.StatusLevel.Error);
                lock (_socketLock)
                {
                    _socket?.Dispose();
                    _socket = null;
                }
            }
            finally
            {
                IsConnected.DirtyFlag.Invalidate();
            }
        }

        private async Task StopAsync()
        {
            try
            {
                lock (_socketLock)
                {
                    _cts?.Cancel();
                    _socket?.Dispose();
                    _socket = null;
                    _cts?.Dispose();
                    _cts = null;
                }

                SetStatus("Disconnected", IStatusProvider.StatusLevel.Notice);
                IsConnected.DirtyFlag.Invalidate();
            }
            catch (Exception e)
            {
                Log.Warning($"Stop error: {e.Message}", this);
            }
        }

        private async Task ReceiveLoop()
        {
            var buffer = new byte[8192];
            try
            {
                while (true)
                {
                    lock (_socketLock)
                    {
                        if (_socket == null || !_socket.IsConnected)
                            break;
                    }

                    var bytesRead = await _socket!.ReceiveAsync(buffer, _cts!.Token);
                    if (bytesRead == 0) // Connection closed
                    {
                        await StopAsync();
                        break;
                    }

                    var msg = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    if (_printToLog)
                    {
                        Log.Debug($"TCP Client ← '{msg}'", this);
                    }
                    _receivedQueue.Enqueue(msg);
                    ReceivedString.DirtyFlag.Invalidate();
                }
            }
            catch (OperationCanceledException) { /* Expected */ }
            catch (Exception ex)
            {
                if (!(ex is OperationCanceledException))
                {
                    SetStatus($"Receive error: {ex.Message}", IStatusProvider.StatusLevel.Warning);
                }
            }
            finally
            {
                await StopAsync();
            }
        }

        private async Task SendMessageAsync(string message)
        {
            try
            {
                lock (_socketLock)
                {
                    if (_socket == null || !_socket.IsConnected)
                        return;
                }

                var data = Encoding.UTF8.GetBytes(message);
                await _socket!.SendAsync(data, _cts!.Token);

                if (_printToLog)
                {
                    Log.Debug($"TCP Client → '{message}'", this);
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Send failed: {ex.Message}", IStatusProvider.StatusLevel.Warning);
                await StopAsync();
            }
        }

        private void UpdateStatusMessage()
        {
            if (!_lastConnectState)
            {
                SetStatus("Not connected. Enable 'Connect'.", IStatusProvider.StatusLevel.Notice);
            }
            else if (IsConnected.Value)
            {
                SetStatus($"Connected to {_lastHost}:{_lastPort}", IStatusProvider.StatusLevel.Success);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _ = StopAsync();
            _receivedQueue.Clear();
            _messageHistory.Clear();
        }

        #region IStatusProvider
        private string _statusMessage = "Disconnected";
        private IStatusProvider.StatusLevel _statusLevel = IStatusProvider.StatusLevel.Notice;

        private void SetStatus(string message, IStatusProvider.StatusLevel level)
        {
            _statusMessage = message;
            _statusLevel = level;
        }

        public IStatusProvider.StatusLevel GetStatusLevel() => _statusLevel;
        public string GetStatusMessage() => _statusMessage;
        #endregion

        [Input(Guid = "EDCBA987-6543-210F-EDCB-A9876543210F")]
        public readonly InputSlot<bool> Connect = new();

        [Input(Guid = "BA987654-3210-FEDC-BA98-76543210FEDC")]
        public readonly InputSlot<string> Host = new("localhost");

        [Input(Guid = "3210FEDC-BA98-7654-3210-FEDCBA987654")]
        public readonly InputSlot<int> Port = new(8080);

        [Input(Guid = "6543210F-EDCB-A987-6543-210FEDCBA987")]
        public readonly InputSlot<int> ListLength = new(10);

        [Input(Guid = "210FEDCB-A987-6543-210F-EDCBA9876543")]
        public readonly MultiInputSlot<string> MessageParts = new();

        [Input(Guid = "FEDCBA98-7654-3210-FEDC-BA9876543210")]
        public readonly InputSlot<string> Separator = new(" ");

        [Input(Guid = "CBA98765-4321-0FED-CBA9-876543210FED")]
        public readonly InputSlot<bool> SendOnChange = new(true);

        [Input(Guid = "A9876543-210F-EDCB-A987-6543210FEDCB")]
        public readonly InputSlot<bool> SendTrigger = new();

        [Input(Guid = "87654321-0FED-CBA9-8765-43210FEDCBA9")]
        public readonly InputSlot<bool> PrintToLog = new();

        private class TcpClientSocket : IDisposable
        {
            private System.Net.Sockets.TcpClient? _client;
            private NetworkStream? _stream;
            private readonly object _streamLock = new();

            public bool IsConnected => _client?.Connected ?? false;

            public async Task ConnectAsync(string host, int port, CancellationToken ct)
            {
                _client = new System.Net.Sockets.TcpClient();
                await _client.ConnectAsync(host, port);
                _stream = _client.GetStream();
            }

            public async Task<int> ReceiveAsync(byte[] buffer, CancellationToken ct)
            {
                lock (_streamLock)
                {
                    if (_stream == null) return 0;
                }
                return await _stream!.ReadAsync(buffer, 0, buffer.Length, ct);
            }

            public async Task SendAsync(byte[] data, CancellationToken ct)
            {
                lock (_streamLock)
                {
                    if (_stream == null) return;
                }
                await _stream!.WriteAsync(data, 0, data.Length, ct);
            }

            public void Dispose()
            {
                _stream?.Dispose();
                _client?.Dispose();
            }
        }
    }
}