using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ManeFfmpeg.Core.Mpv;

/// <summary>
/// mpv's software render API: mpv draws the current frame, scaled and letterboxed, into a BGRA buffer.
/// <see cref="Invalidated"/> fires on an mpv thread; render from your own thread, never from the callback.
/// </summary>
public sealed unsafe class MpvSoftwareRenderer : IDisposable
{
    private static readonly byte[] ApiType = "sw\0"u8.ToArray();
    // mpv fills in opaque alpha, so frames go straight into a Bgra8888 bitmap. With "bgr0" the 4th byte is 0
    // and needs a pass over every pixel, which made 1080p60 drop most frames on a 4-core Intel Mac.
    private static readonly byte[] PixelFormat = "bgra\0"u8.ToArray();

    private nint _context;
    private GCHandle _self;

    public MpvSoftwareRenderer(MpvPlayer player)
    {
        nint context;
        fixed (byte* api = ApiType)
        {
            var parameters = stackalloc MpvNative.RenderParam[2];
            parameters[0] = new MpvNative.RenderParam { Type = MpvNative.RenderParamApiType, Data = api };
            parameters[1] = new MpvNative.RenderParam { Type = MpvNative.RenderParamInvalid, Data = null };
            var result = MpvNative.RenderContextCreate(&context, player.Handle, parameters);
            if (result < 0)
                throw new MpvException($"Software rendering isn't available: {MpvNative.Error(result)}");
        }
        _context = context;
        _self = GCHandle.Alloc(this);
        MpvNative.RenderContextSetUpdateCallback(_context, &OnUpdate, GCHandle.ToIntPtr(_self));
    }

    /// <summary>mpv has something new to show (raised on an mpv thread).</summary>
    public event Action? Invalidated;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUpdate(nint context)
    {
        if (GCHandle.FromIntPtr(context).Target is MpvSoftwareRenderer renderer)
            renderer.Invalidated?.Invoke();
    }

    /// <summary>True when mpv wants a new frame drawn (also processes mpv's internal render updates).</summary>
    public bool Update() => _context != 0 && (MpvNative.RenderContextUpdate(_context) & MpvNative.RenderUpdateFrame) != 0;

    /// <summary>Draws the current frame into <paramref name="buffer"/> (BGRA, <paramref name="stride"/> bytes per row).</summary>
    public bool Render(int width, int height, int stride, nint buffer)
    {
        if (_context == 0 || width <= 0 || height <= 0)
            return false;
        var size = stackalloc int[2];
        size[0] = width;
        size[1] = height;
        nuint rowBytes = (nuint)stride;
        fixed (byte* format = PixelFormat)
        {
            var parameters = stackalloc MpvNative.RenderParam[5];
            parameters[0] = new MpvNative.RenderParam { Type = MpvNative.RenderParamSwSize, Data = size };
            parameters[1] = new MpvNative.RenderParam { Type = MpvNative.RenderParamSwFormat, Data = format };
            parameters[2] = new MpvNative.RenderParam { Type = MpvNative.RenderParamSwStride, Data = &rowBytes };
            parameters[3] = new MpvNative.RenderParam { Type = MpvNative.RenderParamSwPointer, Data = (void*)buffer };
            parameters[4] = new MpvNative.RenderParam { Type = MpvNative.RenderParamInvalid, Data = null };
            return MpvNative.RenderContextRender(_context, parameters) >= 0;
        }
    }

    public void Dispose()
    {
        if (_context == 0)
            return;
        MpvNative.RenderContextSetUpdateCallback(_context, null, 0);
        MpvNative.RenderContextFree(_context);
        _context = 0;
        if (_self.IsAllocated)
            _self.Free();
    }
}
