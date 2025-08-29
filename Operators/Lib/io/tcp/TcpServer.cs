#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
    [Guid("3C4D5E6F-7A8B-9C0D-1E2F-3A4B5C6D7E8F")]
    public sealed class TcpServer : Instance<TcpServer>
,IStatusProvider,IDisposable
    {
        [Output(Guid = "23456789-ABCD-EF01-2345-6789ABCDEF01")]
        public readonly Slot<Command> Result = new();

        [Output(Guid = "3456789A-BCDE-F012-3456-789ABCDEF012")]
        public readonly Slot<bool> IsListening = new();

        [Output(Guid = "456789AB-CDEF-0123-4567-89ABCDEF0123")]
        public readonly Slot<int> ConnectionCount = new();

        public TcpServer()
        {
            Result.UpdateAction = Update;
            IsListening.UpdateAction = Update;
            ConnectionCount.UpdateAction = Update;
        }

        private bool _lastListenState;
        private int _lastPort;
        private string? _lastSentMessage;
        private TcpListener? _listener;
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly ConcurrentDictionary<Guid, System.Net.Sockets.TcpClient> _clients = new();
        private string _statusMessage = "Not listening";
        private IStatusProvider.StatusLevel _statusLevel = IStatusProvider.StatusLevel.Notice;
        private bool _disposed;

        private void Update(EvaluationContext context)
        {
            if (_disposed)
                return;

            var shouldListen = Listen.GetValue(context);
            var port = Port.GetValue(context);

            var settingsChanged = shouldListen != _lastListenState || port != _lastPort;
            if (settingsChanged)
            {
                StopListening();
                if (shouldListen)
                {
                    StartListening(port);
                }
                _lastListenState = shouldListen;
                _lastPort = port;
            }

            IsListening.Value = _listener != null;
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

        private void StartListening(int port)
        {
            if (_listener != null) return;

            _listener = new TcpListener(IPAddress.Any, port);
            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                _listener.Start();
                SetStatus($"Listening on port {port}", IStatusProvider.StatusLevel.Success);
                _ = Task.Run(AcceptConnectionsLoop, _cancellationTokenSource.Token);
            }
            catch (Exception e)
            {
                SetStatus($"Failed to start: {e.Message}", IStatusProvider.StatusLevel.Error);
                _listener.Stop();
                _listener = null;
            }
        }

        private async Task AcceptConnectionsLoop()
        {
            try
            {
                while (!_cancellationTokenSource!.IsCancellationRequested)
                {
                    var client = await _listener!.AcceptTcpClientAsync(_cancellationTokenSource.Token);
                    var clientId = Guid.NewGuid();
                    _clients[clientId] = client;
                    ConnectionCount.DirtyFlag.Invalidate();
                    _ = HandleClient(clientId, client);
                }
            }
            catch (OperationCanceledException) { /* Expected */ }
            catch (Exception e)
            {
                if (!_cancellationTokenSource!.IsCancellationRequested)
                {
                    Log.Warning($"Listener loop stopped: {e.Message}", this);
                }
            }
        }

        private async Task HandleClient(Guid clientId, System.Net.Sockets.TcpClient client)
        {
            var buffer = new byte[8192];
            try
            {
                using var stream = client.GetStream();
                while (!_cancellationTokenSource!.IsCancellationRequested && client.Connected)
                {
                    var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, _cancellationTokenSource.Token);
                    if (bytesRead == 0) // Connection closed
                    {
                        break;
                    }

                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    Log.Debug($"TCP Server ← '{message}'", this);
                }
            }
            catch (OperationCanceledException) { /* Expected */ }
            catch (Exception) { /* Client disconnected */ }
            finally
            {
                if (_clients.TryRemove(clientId, out var removedClient))
                {
                    removedClient.Dispose();
                    ConnectionCount.DirtyFlag.Invalidate();
                }
            }
        }

        private async Task BroadcastMessageAsync(string message)
        {
            if (_clients.IsEmpty) return;

            var data = Encoding.UTF8.GetBytes(message);
            var clientsToBroadcast = _clients.Values.ToList();

            foreach (var client in clientsToBroadcast)
            {
                if (client.Connected)
                {
                    try
                    {
                        var stream = client.GetStream();
                        await stream.WriteAsync(data, 0, data.Length, _cancellationTokenSource!.Token);
                        Log.Debug($"TCP Server → '{message}'", this);
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
        }

        public IStatusProvider.StatusLevel GetStatusLevel() => _statusLevel;
        public string GetStatusMessage() => _statusMessage;
        private void SetStatus(string message, IStatusProvider.StatusLevel level)
        {
            _statusMessage = message;
            _statusLevel = level;
        }

        [Input(Guid = "56789ABC-DEF0-1234-5678-9ABCDEF01234")]
        public readonly InputSlot<bool> Listen = new();

        [Input(Guid = "6789ABCD-EF01-2345-6789-ABCDEF012345")]
        public readonly InputSlot<int> Port = new(8080);

        [Input(Guid = "789ABCDE-F012-3456-789A-BCDEF0123456")]
        public readonly InputSlot<string> Message = new();

        [Input(Guid = "89ABCDEF-0123-4567-89AB-CDEF01234567")]
        public readonly InputSlot<bool> SendOnChange = new(true);

        [Input(Guid = "9ABCDEF0-1234-5678-9ABC-DEF012345678")]
        public readonly InputSlot<bool> SendTrigger = new();
    }
}