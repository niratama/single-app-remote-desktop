using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SingleAppRemote.Host;
using SingleAppRemote.Interop;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace SingleAppRemote.Capture;

public sealed class WgcWindowCapturer : IDisposable
{
    private readonly HostConfig _config;
    private readonly Action<byte[]> _onJpegReady;
    private readonly ImageCodecInfo _jpegEncoder;
    private readonly object _encoderLock = new();
    private EncoderParameters _encoderParams;
    private CancellationTokenSource? _loopCts;
    private Task? _captureTask;
    private volatile bool _restartRequested;

    public WgcWindowCapturer(HostConfig config, Action<byte[]> onJpegReady)
    {
        _config = config;
        _onJpegReady = onJpegReady;
        _jpegEncoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        _encoderParams = CreateEncoderParams(_config.JpegQuality);

        _config.TargetChanged += OnTargetChanged;
        _config.QualityChanged += OnQualityChanged;
    }

    private static EncoderParameters CreateEncoderParams(long quality)
    {
        var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        return ep;
    }

    private void OnTargetChanged() => _restartRequested = true;

    private void OnQualityChanged()
    {
        lock (_encoderLock)
        {
            _encoderParams.Dispose();
            _encoderParams = CreateEncoderParams(_config.JpegQuality);
        }
    }

    public void Start()
    {
        _loopCts = new CancellationTokenSource();
        _captureTask = Task.Run(() => CaptureLoopAsync(_loopCts.Token));
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _restartRequested = false;
            var hwnd = _config.TargetHwnd;
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                RunSingleWindowSession(hwnd, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WGC] Capture session ended or restarted: {ex.Message}");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
    }

    private void RunSingleWindowSession(IntPtr hwnd, CancellationToken ct)
    {
        int hr = NativeMethods.D3D11CreateDevice(
            IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7,
            out var d3dDevicePtr, out _, out var d3dContextPtr);
        if (hr < 0) throw new InvalidOperationException($"D3D11CreateDevice failed: 0x{hr:X}");

        IntPtr dxgiDevicePtr = IntPtr.Zero;
        IntPtr inspectablePtr = IntPtr.Zero;
        IntPtr stagingTex = IntPtr.Zero;
        bool sessionClosed = false;

        try
        {
            var dxgiDeviceGuid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.QueryInterface(d3dDevicePtr, ref dxgiDeviceGuid, out dxgiDevicePtr);
            NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevicePtr, out inspectablePtr);
            var winrtDevice = MarshalInspectable<IDirect3DDevice>.FromAbi(inspectablePtr);

            var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
            var itemGuid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            var itemPtr = interop.CreateForWindow(hwnd, ref itemGuid);
            var item = MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
            Marshal.Release(itemPtr);

            item.Closed += (_, _) => sessionClosed = true;

            var size = item.Size;
            if (size.Width <= 0 || size.Height <= 0)
            {
                size = new Windows.Graphics.SizeInt32 { Width = 800, Height = 600 };
            }

            using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            using var session = framePool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = _config.CaptureCursor;

            int texWidth = 0, texHeight = 0;
            object frameSync = new();

            framePool.FrameArrived += (sender, _) =>
            {
                if (sessionClosed || _restartRequested || ct.IsCancellationRequested) return;

                lock (frameSync)
                {
                    using var frame = sender.TryGetNextFrame();
                    if (frame == null) return;

                    if (frame.ContentSize.Width <= 0 || frame.ContentSize.Height <= 0) return;

                    if (frame.ContentSize.Width != size.Width || frame.ContentSize.Height != size.Height)
                    {
                        size = frame.ContentSize;
                        sender.Recreate(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                        if (stagingTex != IntPtr.Zero)
                        {
                            Marshal.Release(stagingTex);
                            stagingTex = IntPtr.Zero;
                        }
                        return;
                    }

                    var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
                    var tex2dGuid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                    var srcTexPtr = access.GetInterface(ref tex2dGuid);

                    try
                    {
                        if (stagingTex == IntPtr.Zero || texWidth != size.Width || texHeight != size.Height)
                        {
                            if (stagingTex != IntPtr.Zero) Marshal.Release(stagingTex);
                            texWidth = size.Width;
                            texHeight = size.Height;
                            stagingTex = D3D11Helper.CreateStagingTexture(d3dDevicePtr, texWidth, texHeight);
                        }

                        byte[]? jpeg;
                        lock (_encoderLock)
                        {
                            jpeg = D3D11Helper.CopyTextureToJpeg(
                                d3dContextPtr, srcTexPtr, stagingTex, texWidth, texHeight, _jpegEncoder, _encoderParams);
                        }
                        if (jpeg != null) _onJpegReady(jpeg);
                    }
                    finally
                    {
                        if (srcTexPtr != IntPtr.Zero) Marshal.Release(srcTexPtr);
                    }
                }
            };

            session.StartCapture();

            while (!ct.IsCancellationRequested && !_restartRequested && !sessionClosed && NativeMethods.IsWindow(hwnd))
            {
                Thread.Sleep(200);
            }
        }
        finally
        {
            if (stagingTex != IntPtr.Zero) Marshal.Release(stagingTex);
            if (inspectablePtr != IntPtr.Zero) Marshal.Release(inspectablePtr);
            if (dxgiDevicePtr != IntPtr.Zero) Marshal.Release(dxgiDevicePtr);
            if (d3dContextPtr != IntPtr.Zero) Marshal.Release(d3dContextPtr);
            if (d3dDevicePtr != IntPtr.Zero) Marshal.Release(d3dDevicePtr);
        }
    }

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _config.TargetChanged -= OnTargetChanged;
        _config.QualityChanged -= OnQualityChanged;
        try { _loopCts?.Cancel(); } catch { }
        try { _captureTask?.Wait(1000); } catch { }
        try { _loopCts?.Dispose(); } catch { }
        _loopCts = null;
        lock (_encoderLock)
        {
            _encoderParams.Dispose();
        }
    }
}
