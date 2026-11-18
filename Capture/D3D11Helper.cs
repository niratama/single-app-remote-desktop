using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SingleAppRemote.Capture;

internal static unsafe class D3D11Helper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public int Width, Height, MipLevels, ArraySize, Format;
        public int SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr pData;
        public int RowPitch, DepthPitch;
    }

    public static IntPtr CreateStagingTexture(IntPtr devicePtr, int width, int height)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = 87, // DXGI_FORMAT_B8G8R8A8_UNORM
            SampleCount = 1,
            Usage = 3, // D3D11_USAGE_STAGING
            CPUAccessFlags = 0x20000 // D3D11_CPU_ACCESS_READ
        };
        var vtable = *(IntPtr**)devicePtr;
        var createTex2D = (delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)vtable[5];
        IntPtr texOut;
        int hr = createTex2D(devicePtr, &desc, IntPtr.Zero, &texOut);
        if (hr < 0) return IntPtr.Zero;
        return texOut;
    }

    public static byte[]? CopyTextureToJpeg(
        IntPtr contextPtr, IntPtr srcTex, IntPtr dstTex, int width, int height,
        ImageCodecInfo encoder, EncoderParameters encoderParams)
    {
        if (width <= 0 || height <= 0 || srcTex == IntPtr.Zero || dstTex == IntPtr.Zero) return null;

        var vtable = *(IntPtr**)contextPtr;
        var copyResource = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)vtable[47];
        copyResource(contextPtr, dstTex, srcTex);

        var map = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, int, D3D11_MAPPED_SUBRESOURCE*, int>)vtable[14];
        D3D11_MAPPED_SUBRESOURCE mapped;
        if (map(contextPtr, dstTex, 0, 1, 0, &mapped) < 0) return null;

        try
        {
            using var bmp = new Bitmap(width, height, mapped.RowPitch, PixelFormat.Format32bppArgb, mapped.pData);
            using var ms = new MemoryStream();
            bmp.Save(ms, encoder, encoderParams);
            return ms.ToArray();
        }
        finally
        {
            var unmap = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, void>)vtable[15];
            unmap(contextPtr, dstTex, 0);
        }
    }
}
