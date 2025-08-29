#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
    [Guid("F0E1D2C3-B4A5-9876-5432-10FEDCBA9876")]
    public sealed class WebSocketServer : Instance<WebSocketServer>, IStatusProvider, IDisposable
    {
        [Output(Guid = "01234567-89AB-CDEF-0123-456789ABCDEF")]
        public readonly Slot<Command> Result = new();

        [Output(Guid = "FEDCBA09-8765-4321-FEDC-BA0987654321")]
        public readonly Slot<bool> IsListening = new();

        [Output(Guid = "11223344-5566-7788-99AA-BBCCDDEEFF00")]
        public readonly Slot<int> ConnectionCount = new();

        public WebSocketServer()
        {
            Result.UpdateAction = Update;
            IsListening.UpdateAction = Update;
            ConnectionCount.UpdateAction = Update;
        }

        private bool _lastListenState;
        private int _lastPort;
        private string? _lastPath;
        private string? _lastSentMessage;
        private HttpListener? _listener;
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
        private string _statusMessage = "Not listening";
        private IStatusProvider.StatusLevel _statusLevel = IStatusProvider.StatusLevel.Notice;
        private bool _disposed;

        private void Update(EvaluationContext context)
        {
            if (_disposed)
                return;

            var shouldListen = Listen.GetValue(context);
            var port = Port.GetValue(context);
            var path = Path.GetValue(context);

            var settingsChanged = shouldListen != _lastListenState || port != _lastPort || path != _lastPath;
            if (settingsChanged)
            {
                StopListening();
                if (shouldListen)
                {
                    var urlPath = path?.TrimStart('/') ?? string.Empty;
                    var prefix = $"http://+:{port}/" + (!string.IsNullOrEmpty(urlPath) ? $"{urlPath}/" : "");
                    StartListening(prefix);
                }
                _lastListenState = shouldListen;
                _lastPort = port;
                _lastPath = path;
            }

            IsListening.Value = _listener is { IsListening: true };
            ConnectionCount.Value = _clients.Count;

            var message = Message.GetValue(context);
            var sendOnChange = SendOnChange.GetValue(context);
            var triggerSend = SendTrigger.GetValue(context);

            var messageChanged = message != _lastSentMessage;

            if (triggerSend || (sendOnChange && messageChanged))
            {
                if (!string.IsNullOrEmpty(message))
                {
                    _ = BroadcastMessageAsync(message);
                    _lastSentMessage = message;
                }

                if (triggerSend)
                    SendTrigger.SetTypedInputValue(false);
            }
        }

        private void StartListening(string prefix)
        {
            if (_listener is { IsListening: true }) return;

            _listener = new HttpListener();
            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                _listener.Prefixes.Add(prefix);
                _listener.Start();
                SetStatus($"Listening on {prefix.Replace("+", "localhost")}", IStatusProvider.StatusLevel.Success);
                _ = Task.Run(AcceptConnectionsLoop, _cancellationTokenSource.Token);
            }
            catch (HttpListenerException hlex) when (hlex.ErrorCode == 5) // Access denied
            {
                SetStatus($"Access denied. Try running T3 as Administrator or: netsh http add urlacl url={prefix} user=Everyone", IStatusProvider.StatusLevel.Error);
                _listener?.Close();
                _listener = null;
            }
            catch (Exception e)
            {
                SetStatus($"Failed to start: {e.Message}", IStatusProvider.StatusLevel.Error);
                _listener?.Close();
                _listener = null;
            }
        }

        private async Task AcceptConnectionsLoop()
        {
            try
            {
                while (_listener?.IsListening == true && !_cancellationTokenSource!.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync();
                    if (context.Request.IsWebSocketRequest)
                    {
                        var webSocketContext = await context.AcceptWebSocketAsync(subProtocol: null);
                        var webSocket = webSocketContext.WebSocket;
                        var clientId = Guid.NewGuid();
                        _clients[clientId] = webSocket;
                        ConnectionCount.DirtyFlag.Invalidate();
                        _ = HandleClient(clientId, webSocket);
                    }
                    else
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                    }
                }
            }
            catch (OperationCanceledException) { /* Expected */ }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                // Expected during shutdown
            }
            catch (Exception e)
            {
                if (_listener?.IsListening == true)
                    Log.Warning($"Listener loop stopped: {e.Message}", this);
            }
        }

        private async Task HandleClient(Guid clientId, WebSocket webSocket)
        {
            var buffer = new byte[8192]; // Larger buffer size
            try
            {
                while (webSocket.State == WebSocketState.Open && !_cancellationTokenSource!.IsCancellationRequested)
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        Log.Debug($"WS Server ← '{message}'", this);
                    }
                }
            }
            catch (OperationCanceledException) { /* Expected */ }
            catch (WebSocketException) { /* Client disconnected unexpectedly */ }
            finally
            {
                if (_clients.TryRemove(clientId, out _))
                {
                    ConnectionCount.DirtyFlag.Invalidate();
                }
                webSocket.Dispose();
            }
        }

        private async Task BroadcastMessageAsync(string message)
        {
            if (_clients.IsEmpty) return;

            var messageBuffer = new ArraySegment<byte>(Encoding.UTF8.GetBytes(message));
            var clientsToBroadcast = _clients.Values.ToList();

            foreach (var client in clientsToBroadcast)
            {
                if (client.State == WebSocketState.Open)
                {
                    try
                    {
                        await client.SendAsync(messageBuffer, WebSocketMessageType.Text, true, CancellationToken.None);
                        Log.Debug($"WS Server → '{message}'", this);
                    }
                    catch (Exception e)
                    {
                        Log.Warning($"Failed to send to a client: {e.Message}", this);
                    }
                }
            }
        }

        private void StopListening()
        {
            try
            {
                _cancellationTokenSource?.Cancel();
                _listener?.Stop();

                foreach (var client in _clients.Values)
                {
                    client.Dispose();
                }
                _clients.Clear();

                if (!_lastListenState)
                    SetStatus("Not listening", IStatusProvider.StatusLevel.Notice);

                IsListening.DirtyFlag.Invalidate();
                ConnectionCount.DirtyFlag.Invalidate();
            }
            catch (Exception e)
            {
                Log.Warning($"Error stopping server: {e.Message}", this);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopListening();
            _cancellationTokenSource?.Dispose();
            _listener?.Close();
        }

        public IStatusProvider.StatusLevel GetStatusLevel() => _statusLevel;
        public string GetStatusMessage() => _statusMessage;
        private void SetStatus(string message, IStatusProvider.StatusLevel level)
        {
            _statusMessage = message;
            _statusLevel = level;
        }

        [Input(Guid = "1234ABCD-5678-90EF-1234-567890ABCDEF")]
        public readonly InputSlot<bool> Listen = new();

        [Input(Guid = "4321DCBA-8765-FE09-4321-DCBA87654321")]
        public readonly InputSlot<int> Port = new(8080);

        [Input(Guid = "AABBCCDD-EEFF-0011-2233-445566778899")]
        public readonly InputSlot<string> Path = new();

        [Input(Guid = "FEDCBA12-3456-7890-FEDC-BA1234567890")]
        public readonly InputSlot<string> Message = new();

        [Input(Guid = "A0E35517-337A-4770-985A-34CAC95B5B5F")]
        public readonly InputSlot<bool> SendOnChange = new(true);

        [Input(Guid = "10293847-56AF-BECD-8901-23456789ABCD")]
        public readonly InputSlot<bool> SendTrigger = new();
    }
}