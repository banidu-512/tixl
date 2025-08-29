#nullable enable
using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using T3.Core.Logging;
using T3.Core.Operator;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Slots;

namespace Lib.io.http;

/// <summary>
/// A simple HTTP server operator for T3 to serve a user-provided HTML string.
/// </summary>
[Guid("AABBCCDD-EEFF-0011-2233-4455667788AA")]
internal sealed class WebServer : Instance<WebServer>, IStatusProvider, IDisposable
{
    #region Outputs
    [Output(Guid = "11112222-3333-4444-5555-666677778888")]
    public readonly Slot<bool> IsRunning = new();

    [Output(Guid = "22223333-4444-5555-6666-777788889999")]
    public readonly Slot<int> PortOutput = new(); // Renamed to avoid potential conflict
    #endregion

    #region Constructor
    public WebServer()
    {
        IsRunning.UpdateAction = Update;
        PortOutput.UpdateAction = Update;
    }
    #endregion

    #region State and Fields
    private HttpListener? _listener;
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _lastListenState;
    private int _lastPort;
    private string? _lastHtmlContent; // Store last HTML content
    private bool _disposed = false;

    private string _statusMessage = "Not running";
    private IStatusProvider.StatusLevel _statusLevel = IStatusProvider.StatusLevel.Notice;
    #endregion

    #region Main Update Loop
    private void Update(EvaluationContext context)
    {
        if (_disposed) return;

        var shouldListen = Listen.GetValue(context);
        var port = Port.GetValue(context);
        var htmlContent = HtmlContent.GetValue(context); // Get HTML content from input

        // Check if settings have changed and require a restart or content update
        // Note: For simplicity, we only restart if Listen/Port change. Content changes are handled on request.
        var settingsChanged = shouldListen != _lastListenState || port != _lastPort;

        if (settingsChanged)
        {
            StopServer();
            if (shouldListen)
            {
                StartServer(port);
            }
            _lastListenState = shouldListen;
            _lastPort = port;
        }

        // Always update the stored HTML content
        _lastHtmlContent = htmlContent;

        // Update output slots
        IsRunning.Value = _listener?.IsListening == true;
        PortOutput.Value = _lastPort;
    }
    #endregion

    #region Server Control Logic
    private void StartServer(int port)
    {
        // Use localhost to avoid needing admin rights for most scenarios
        var prefix = $"http://localhost:{port}/";

        _listener = new HttpListener();
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            _listener.Prefixes.Add(prefix);
            _listener.Start();
            SetStatus($"Running on {prefix}", IStatusProvider.StatusLevel.Success);
            Log.Debug($"WebServer started on {prefix}", this);

            // Start the listening loop on a background task
            _ = Task.Run(ListenLoop, _cancellationTokenSource.Token);
        }
        catch (HttpListenerException hlex) when (hlex.ErrorCode == 5) // Access Denied
        {
            SetStatus($"Access Denied on port {port}. Try port >1024.", IStatusProvider.StatusLevel.Error);
            Log.Error($"Access Denied starting WebServer on {prefix}: {hlex.Message}", this);
            CleanupListener();
        }
        catch (Exception e)
        {
            SetStatus($"Failed to start: {e.Message}", IStatusProvider.StatusLevel.Error);
            Log.Error($"Error starting WebServer: {e.Message}", this);
            CleanupListener();
        }
    }

    private async Task ListenLoop()
    {
        try
        {
            while (_listener?.IsListening == true && !_cancellationTokenSource!.Token.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (HttpListenerException) when (_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning($"WebServer error getting request context: {ex.Message}", this);
                    continue;
                }

                // Handle the request asynchronously
                _ = Task.Run(() => HandleRequestAsync(context), _cancellationTokenSource.Token);
            }
        }
        catch (Exception e) when (e is OperationCanceledException || e is ObjectDisposedException)
        {
            // Expected during shutdown
        }
        catch (Exception e)
        {
            Log.Warning($"Unexpected error in WebServer ListenLoop: {e.Message}", this);
        }
        finally
        {
            Log.Debug("WebServer ListenLoop finished.", this);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;

        try
        {
            Log.Debug($"WebServer received request for '{request.Url?.AbsolutePath}'", this);

            string? urlPath = request.Url?.AbsolutePath ?? "/";
            // Serve the HTML content for the root path
            if (string.IsNullOrEmpty(urlPath) || urlPath == "/")
            {
                // Get the current HTML content from the stored variable
                string htmlToServe = _lastHtmlContent ?? "<html><body><h1>No HTML content provided.</h1></body></html>";

                byte[] buffer = Encoding.UTF8.GetBytes(htmlToServe);
                response.ContentType = "text/html";
                response.ContentLength64 = buffer.Length;
                response.StatusCode = (int)HttpStatusCode.OK;

                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                Log.Debug("WebServer served HTML content.", this);
            }
            else
            {
                // For any other path, return 404
                Log.Debug($"WebServer path not found: '{urlPath}'", this);
                SendErrorResponse(response, 404, "Not Found");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"WebServer error handling request for '{request.Url?.AbsolutePath}': {ex.Message}", this);
            SendErrorResponse(response, 500, "Internal Server Error");
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception ex)
            {
                Log.Debug($"WebServer error closing response: {ex.Message}", this);
            }
        }
    }

    private static void SendErrorResponse(HttpListenerResponse response, int statusCode, string description)
    {
        try
        {
            byte[] buffer = Encoding.UTF8.GetBytes($"<html><body><h1>{statusCode} {description}</h1></body></html>");
            response.ContentType = "text/html";
            response.ContentLength64 = buffer.Length;
            response.StatusCode = statusCode;

            response.OutputStream.Write(buffer, 0, buffer.Length);
        }
        catch (Exception ex)
        {
            Log.Debug($"WebServer error sending error response ({statusCode}): {ex.Message}", typeof(WebServer));
        }
    }

    private void StopServer()
    {
        try
        {
            _cancellationTokenSource?.Cancel();
        }
        catch (Exception ex)
        {
            Log.Debug($"WebServer error during cancellation: {ex.Message}", this);
        }

        CleanupListener();

        if (!_lastListenState)
            SetStatus("Not running", IStatusProvider.StatusLevel.Notice);

        IsRunning.DirtyFlag.Invalidate();
        PortOutput.DirtyFlag.Invalidate();
    }

    private void CleanupListener()
    {
        try
        {
            if (_listener != null)
            {
                if (_listener.IsListening)
                {
                    _listener.Stop();
                }
                _listener.Close();
                _listener = null;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"WebServer error stopping HttpListener: {ex.Message}", this);
        }
    }
    #endregion

    #region IStatusProvider Implementation
    public IStatusProvider.StatusLevel GetStatusLevel() => _statusLevel;
    public string GetStatusMessage() => _statusMessage;

    private void SetStatus(string message, IStatusProvider.StatusLevel level)
    {
        _statusMessage = message;
        _statusLevel = level;
    }
    #endregion

    #region IDisposable Implementation
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopServer();
        _cancellationTokenSource?.Dispose();
    }
    #endregion

    #region Input Slots
    [Input(Guid = "33334444-5555-6666-7777-88889999AAAA")]
    public readonly InputSlot<bool> Listen = new();

    [Input(Guid = "44445555-6666-7777-8888-9999AAAABBBB")]
    public readonly InputSlot<int> Port = new(8080);

    /// <summary>
    /// The HTML content to serve when the root path (/) is requested.
    /// </summary>
    [Input(Guid = "55556666-7777-8888-9999-AAAABBBBCCCC")]
    public readonly InputSlot<string> HtmlContent = new(@"<!DOCTYPE html>
<html>
<head>
    <title>Default T3 WebServer Page</title>
</head>
<body>
    <h1>Welcome to the T3 WebServer</h1>
    <p>Provide your HTML content via the 'HtmlContent' input slot.</p>
    <p>Example interaction with a T3 WebSocket:</p>
    <div>
        <label for='slider1'>Slider 1:</label>
        <input type='range' id='slider1' min='0' max='100' value='50'>
        <span id='value1'>50</span>
    </div>
    <div>
        <button id='button1'>Send Message</button>
    </div>
    <div id='status'>Connecting...</div>
    <script>
        // --- Basic WebSocket Interaction Example ---
        // Make sure to change the port to match your T3 WebSocketServer port
        const WS_PORT = 8081;
        let ws;
        const statusEl = document.getElementById('status');
        const slider1 = document.getElementById('slider1');
        const value1 = document.getElementById('value1');
        const button1 = document.getElementById('button1');

        function connect() {
            ws = new WebSocket(`ws://localhost:${WS_PORT}`);
            ws.onopen = () => {
                statusEl.textContent = 'Connected';
                statusEl.style.color = 'green';
            };
            ws.onmessage = (event) => {
                console.log('Received:', event.data);
                statusEl.textContent = `Received: ${event.data}`;
                statusEl.style.color = 'blue';
                // Example: Update slider if T3 sends 'SET_SLIDER1:75'
                if (event.data.startsWith('SET_SLIDER1:')) {
                     const val = event.data.split(':')[1];
                     slider1.value = val;
                     value1.textContent = val;
                }
            };
            ws.onclose = () => {
                statusEl.textContent = 'Disconnected. Reconnecting...';
                statusEl.style.color = 'orange';
                setTimeout(connect, 3000);
            };
            ws.onerror = (err) => {
                statusEl.textContent = 'Error';
                statusEl.style.color = 'red';
                console.error('WebSocket error:', err);
            };
        }

        slider1.addEventListener('input', function() {
            value1.textContent = slider1.value;
            if (ws && ws.readyState === WebSocket.OPEN) {
                ws.send(`SLIDER1:${slider1.value}`);
            }
        });

        button1.addEventListener('click', function() {
             if (ws && ws.readyState === WebSocket.OPEN) {
                 ws.send('BUTTON1_CLICKED');
             }
        });

        window.addEventListener('load', connect);
    </script>
</body>
</html>");
    #endregion
}