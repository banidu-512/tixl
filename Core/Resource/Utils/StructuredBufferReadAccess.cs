#nullable enable

using System;
using System.Collections.Generic;
using SharpDX;
using SharpDX.Direct3D11;
using T3.Core.Logging;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace T3.Core.Resource;

/// <summary>
/// Asynchronously reads back GPU structured buffers over multiple frames using a rotating set of staging buffers.
/// </summary>
public sealed class StructuredBufferReadAccess : IDisposable
{
    public sealed class ReadRequestItem
    {
        internal int RequestIndex;
        public required StructuredBufferReadAccess BufferReadAccess;
        public required OnReadComplete OnSuccess;
        public required Buffer CpuAccessBuffer;
        public required int ElementCount;
        public required int StructureByteStride;

        // A request is mappable once the copy has been queued for a full frame. It stays mappable
        // on the following frames too: with a non-blocking Map the copy is usually not downloaded
        // yet on the first attempt and has to be retried.
        internal bool IsReady => RequestIndex <= BufferReadAccess._frameCounter - 1;

        // Its staging buffer is overwritten by the copy of the request BufferCount frames later,
        // so it must be dropped before that copy is issued.
        internal bool IsObsolete => RequestIndex < BufferReadAccess._frameCounter - BufferCount;

        // Last frame on which this request can still be mapped before its staging slot is reused.
        internal bool IsLastAttempt => RequestIndex == BufferReadAccess._frameCounter - BufferCount;
    }

    public delegate void OnReadComplete(ReadRequestItem readItem, IntPtr dataPointer, DataStream dataStream);

    /// <summary>
    /// Readback will take several frames because it takes a while until the frames are
    /// downloaded from the gpu. <see cref="Update"/> needs to be called once a frame.
    /// </summary>
    public void Update()
    {
        _frameCounter++;

        while (_readRequests.Count > 0 && _readRequests[0].IsObsolete)
        {
            Log.Debug("Removing obsolete buffer read request");
            _readRequests.RemoveAt(0);
        }

        if (_readRequests.Count == 0)
            return;

        var context = ResourceManager.Device.ImmediateContext;

        // Try every matured request, not just the head: a copy that hasn't landed yet must not
        // block the requests behind it, and it has to stay queued so a later Update can retry it.
        // The rotating staging buffers keep it valid until it is about to be overwritten.
        for (var i = 0; i < _readRequests.Count; i++)
        {
            var request = _readRequests[i];
            if (!request.IsReady)
                continue;

            try
            {
                // A blocking Map stalls the CPU until the GPU has drained everything queued so far.
                // Only accept that when the staging slot is about to be reused.
                var flags = request.IsLastAttempt ? MapFlags.None : MapFlags.DoNotWait;
                if (!TryMap(context, request.CpuAccessBuffer, flags, out var dataBox, out var dataStream))
                {
                    if (request.IsLastAttempt)
                    {
                        Log.Warning("Failed to map buffer readback even after waiting; dropping the request.");
                        _readRequests.RemoveAt(i);
                        i--;
                    }

                    continue;
                }

                try
                {
                    request.OnSuccess(request, dataBox.DataPointer, dataStream);
                }
                finally
                {
                    context.UnmapSubresource(request.CpuAccessBuffer, 0);
                    _readRequests.RemoveAt(i);
                    i--;
                }
            }
            catch (Exception e)
            {
                // Lost device, disposed staging buffer or a throwing consumer: drop the request, keep the frame loop alive.
                Log.Warning("Buffer readback failed: " + e.Message);
                if (_readRequests.Remove(request))
                {
                    i--;
                }
            }
        }
    }

    /** Returns false while the GPU has not finished the copy yet (only possible with DoNotWait). */
    private static bool TryMap(DeviceContext context, Buffer buffer, MapFlags flags, out DataBox dataBox, out DataStream dataStream)
    {
        try
        {
            dataBox = context.MapSubresource(buffer, 0, MapMode.Read, flags, out dataStream);
            return dataBox.DataPointer != IntPtr.Zero && dataStream != null;
        }
        catch (SharpDXException e) when (e.Descriptor == SharpDX.DXGI.ResultCode.WasStillDrawing)
        {
            dataBox = default;
            dataStream = null!;
            return false;
        }
    }

    public bool InitiateRead(Buffer sourceBuffer, int elementCount, int structureByteStride, OnReadComplete onSuccess)
    {
        if (sourceBuffer == null! || sourceBuffer.IsDisposed)
            return false;

        var sizeInBytes = elementCount * structureByteStride;
        PrepareCpuAccessBuffers(sizeInBytes, structureByteStride);

        var cpuAccessBuffer = _cpuAccessBuffers[_frameCounter % BufferCount];
        ResourceManager.Device.ImmediateContext.CopyResource(sourceBuffer, cpuAccessBuffer);

        _readRequests.Add(new ReadRequestItem
        {
            BufferReadAccess = this,
            RequestIndex = _frameCounter,
            CpuAccessBuffer = cpuAccessBuffer,
            ElementCount = elementCount,
            StructureByteStride = structureByteStride,
            OnSuccess = onSuccess,
        });

        return true;
    }

    private void PrepareCpuAccessBuffers(int sizeInBytes, int structureByteStride)
    {
        if (_cpuAccessBuffers.Count > 0 &&
            _cpuAccessBuffers[0].Description.SizeInBytes == sizeInBytes &&
            _cpuAccessBuffers[0].Description.StructureByteStride == structureByteStride)
            return;

        DisposeBuffers();
        if (_readRequests.Count > 0)
        {
            Log.Debug($"Discarding {_readRequests.Count} buffer frames with outdated format");
            _readRequests.Clear();
        }

        var desc = new BufferDescription
        {
            Usage = ResourceUsage.Staging, // vs. Default
            SizeInBytes = sizeInBytes, 
            BindFlags = BindFlags.None, // BindFlags.UnorderedAccess | BindFlags.ShaderResource
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = structureByteStride
        };

        for (int i = 0; i < BufferCount; i++)
        {
            try
            {
                var buffer = new Buffer(ResourceManager.Device, desc);
                _cpuAccessBuffers.Add(buffer);
            }
            catch (Exception e)
            {
                Log.Warning("Failed to setup readback buffer " + e.Message);
            }
        }
    }

    public void ClearQueue()
    {
        _readRequests.Clear();
    }

    private void DisposeBuffers()
    {
        foreach (var buffer in _cpuAccessBuffers)
        {
            buffer.Dispose();
        }
        _cpuAccessBuffers.Clear();
    }

    public void Dispose()
    {
        DisposeBuffers();
    }

    // Each request keeps its staging buffer alive for BufferCount Update() calls. This must be
    // generous: a caller can update more than once per rendered frame, and when the GPU is a
    // frame or two behind a DoNotWait map only succeeds once the copy has actually executed.
    // With a short window the last-chance blocking map in Update() would fire every frame.
    private const int BufferCount = 8;
    private readonly List<Buffer> _cpuAccessBuffers = new(BufferCount);
    private readonly List<ReadRequestItem> _readRequests = new(BufferCount);
    private int _frameCounter;
}
