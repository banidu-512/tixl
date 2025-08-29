#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using T3.Core.Logging;
using T3.Core.Operator;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Slots;
using T3.Core.Utils;

namespace Lib.io.websocket
{
    [Guid("1A1B1C1D-2E2F-3A3B-4C4D-5E5F6A6B7C7D")]
    internal sealed class WebSocketClient : Instance<WebSocketClient>, IStatusProvider, IDisposable
    {
        [Output(Guid = "D7C6B5A4-9876-5432-10FE-DCBA98765432", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
        public readonly Slot<string> ReceivedString = new();

        [Output(Guid = "A1B2C3D4-E5F6-A7B8-C9D0-E1F2A3B4C5D6", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
        public readonly Slot<List<string>> ReceivedLines = new();

        [Output(Guid = "F9E8D7C6-B5A4-3210-FEDC-BA9876543210", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
        public readonly Slot<bool> WasTrigger = new();

        [Output(Guid = "ABCDEF01-2345-6789-ABCD-EF0123456789")]
        public readonly Slot<bool> IsConnected = new();

        public WebSocketClient()
        {
            ReceivedString.UpdateAction = Update;
            ReceivedLines.UpdateAction = Update;
            WasTrigger.UpdateAction = Update;
            IsConnected.UpdateAction = Update;
        }

        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _cts;
        private bool _lastConnectState;
        private string? _lastUrl;
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
            var url = Url.GetValue(context);

            var settingsChanged = shouldConnect != _lastConnectState || url != _lastUrl;
            if (settingsChanged)
            {
                _ = HandleConnectionChange(shouldConnect, url);
            }

            HandleMessageSending(context);
            HandleReceivedMessages(context);
            UpdateStatusMessage();
        }

        private async Task HandleConnectionChange(bool shouldConnect, string? url)
        {
            await StopAsync();
            _lastConnectState = shouldConnect;
            _lastUrl = url;

            if (shouldConnect && !string.IsNullOrWhiteSpace(url))
            {
                await StartAsync(url);
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
            IsConnected.Value = _webSocket?.State == WebSocketState.Open;
        }

        private async Task StartAsync(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                SetStatus($"Invalid URL: {url}", IStatusProvider.StatusLevel.Error);
                return;
            }

            lock (_socketLock)
            {
                _webSocket?.Dispose();
                _webSocket = new ClientWebSocket();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
            }

            try
            {
                SetStatus($"Connecting to {url}...", IStatusProvider.StatusLevel.Notice);
                await _webSocket!.ConnectAsync(uri, _cts.Token);
                SetStatus($"Connected to {url}", IStatusProvider.StatusLevel.Success);

                _ = Task.Run(ReceiveLoop);
            }
            catch (Exception e)
            {
                SetStatus($"Connect failed: {e.Message}", IStatusProvider.StatusLevel.Error);
                lock (_socketLock)
                {
                    _webSocket?.Dispose();
                    _webSocket = null;
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

                    if (_webSocket?.State == WebSocketState.Open)
                    {
                        _ = _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None)
                                    .ContinueWith(t =>
                                    {
                                        if (t.IsFaulted)
                                            Log.Warning($"Close error: {t.Exception?.Message}", this);
                                    });
                    }

                    _webSocket?.Dispose();
                    _webSocket = null;
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
            var buffer = new byte[8192]; // Increased buffer size
            try
            {
                while (true)
                {
                    lock (_socketLock)
                    {
                        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
                            break;
                    }

                    var result = await _webSocket!.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await StopAsync();
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        if (_printToLog)
                        {
                            Log.Debug($"WS Client ← '{msg}'", this);
                        }
                        _receivedQueue.Enqueue(msg);
                        ReceivedString.DirtyFlag.Invalidate();
                    }
                }
            }
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
                    if (_webSocket?.State != WebSocketState.Open)
                        return;
                }

                var data = Encoding.UTF8.GetBytes(message);
                await _webSocket!.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None);

                if (_printToLog)
                {
                    Log.Debug($"WS Client → '{message}'", this);
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
                SetStatus($"Connected to {_lastUrl}", IStatusProvider.StatusLevel.Success);
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

        [Input(Guid = "FEDCBA98-7654-3210-FEDC-BA9876543210")]
        public readonly InputSlot<bool> Connect = new();

        [Input(Guid = "54321098-FEDC-BA98-7654-321098FEDCBA")]
        public readonly InputSlot<string> Url = new("ws://localhost:8080");

        [Input(Guid = "ABC12345-DEF6-7890-ABCD-EF1234567890")]
        public readonly InputSlot<int> ListLength = new(10);

        [Input(Guid = "59074D76-1F4F-406A-B512-5813F4E3420E")]
        public readonly MultiInputSlot<string> MessageParts = new();

        [Input(Guid = "82933C40-DA9E-4340-A227-E9BACF6E2063")]
        public readonly InputSlot<string> Separator = new(" ");

        [Input(Guid = "216A0356-EF4A-413A-A656-7497127E31D4")]
        public readonly InputSlot<bool> SendOnChange = new(true);

        [Input(Guid = "C7AC22C0-A31E-41F6-B29D-D40956E6688B")]
        public readonly InputSlot<bool> SendTrigger = new();

        [Input(Guid = "5E725916-4143-4759-8651-E12185C658D3")]
        public readonly InputSlot<bool> PrintToLog = new();
    }
}