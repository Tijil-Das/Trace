using System.Runtime.InteropServices;

using ScreenRecall.Storage;
using Vortice.DXGI;

namespace ScreenRecall.CaptureService.Dxgi;

/// <summary>
/// A mouse pointer shape as it can be drawn: straight BGRA with per-pixel alpha, plus the hotspot the shape was
/// authored with.
/// </summary>
internal sealed record PointerShape(ulong Hash, int Width, int Height, int HotspotX, int HotspotY, byte[] Bgra);

/// <summary>
/// Turns the pointer bitmap DXGI reports into pixels a renderer can blend.
/// </summary>
/// <remarks>
/// The duplicated desktop surface never contains the mouse pointer — the compositor draws it separately — so this is
/// the only place the pointer's pixels exist. DXGI offers a shape only when it changed (an arrow becoming an I-beam,
/// a resize handle, a busy ring), so this runs a handful of times per session and never on the steady-state frame
/// path. Three encodings arrive, and all three are normalised to BGRA with straight alpha here, so nothing
/// downstream has to know about masks:
/// <list type="bullet">
/// <item><c>color</c> — 32-bit BGRA with its own alpha.</item>
/// <item><c>masked color</c> — 32-bit BGRA whose alpha byte is an AND mask, not an alpha: it is inverted here.</item>
/// <item><c>monochrome</c> — two 1-bpp masks stacked: the AND mask first, the XOR mask second, each image-height rows
/// of <c>Pitch</c> bytes. <c>Height</c> counts both masks, so the image is half of it. The classic
/// <c>(and=1, xor=0)</c> pixel is transparent, <c>(0,0)</c> is black, <c>(0,1)</c> is white, and <c>(1,1)</c> —
/// which means "invert whatever is underneath" — is drawn as white, because straight alpha cannot express an
/// inverter. A shape whose masks do not fit the buffer is refused rather than guessed at.</item>
/// </list>
/// </remarks>
internal static class PointerShapeReader
{
    /// <summary>
    /// Shape buffer, grown on demand. DXGI writes into it directly, so it is unmanaged, and it is never freed: this
    /// is one cursor bitmap for the lifetime of the process, reallocated only when a shape needs more room.
    /// </summary>
    private static IntPtr _buffer;
    private static int _capacity;

    /// <summary>
    /// Reads the pointer shape DXGI is offering with the current frame, or null when there is nothing usable (no
    /// shape on offer, or a shape this code will not guess at). The caller keeps the last shape it read.
    /// </summary>
    internal static PointerShape? Read(IDXGIOutputDuplication duplication)
    {
        if (_buffer == IntPtr.Zero)
        {
            _capacity = 64 * 1024;
            _buffer = Marshal.AllocHGlobal(_capacity);
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            SharpGen.Runtime.Result result = duplication.GetFramePointerShape(
                (uint)_capacity,
                _buffer,
                out uint required,
                out OutduplPointerShapeInfo info);

            if (result.Success)
            {
                int bytes = (int)Math.Min(required, (uint)_capacity);
                byte[] shape = new byte[bytes];
                Marshal.Copy(_buffer, shape, 0, bytes);
                return Decode(shape, info);
            }

            // MORE_DATA means "the buffer is too small", and the required size arrives with it: grow and retry.
            if (required <= (uint)_capacity)
            {
                return null;
            }

            Marshal.FreeHGlobal(_buffer);
            _capacity = (int)required + 4096;
            _buffer = Marshal.AllocHGlobal(_capacity);
        }

        return null;
    }

    private static PointerShape? Decode(byte[] buffer, OutduplPointerShapeInfo info)
    {
        int width = (int)info.Width;
        int pitch = (int)info.Pitch;
        int rows = (int)info.Height;
        if (width <= 0 || rows <= 0 || pitch <= 0 || width > 256 || rows > 512 || pitch > 4096)
        {
            return null;
        }

        // This wrapper exposes DXGI's shape type as a plain UINT, so the enum's values are the readable form of it
        // (DXGI_OUTDUPL_POINTER_SHAPE_TYPE: 1 monochrome, 2 colour, 3 masked colour).
        uint type = info.Type;
        bool monochrome = type == (uint)PointerShapeType.Monochrome;
        int height = monochrome ? rows / 2 : rows;
        if (height <= 0 || buffer.Length < (long)pitch * rows)
        {
            return null;
        }

        byte[] bgra = new byte[width * height * 4];
        if (type == (uint)PointerShapeType.Color)
        {
            CopyColor(buffer, bgra, width, height, pitch, masked: false);
        }
        else if (type == (uint)PointerShapeType.MaskedColor)
        {
            CopyColor(buffer, bgra, width, height, pitch, masked: true);
        }
        else if (type == (uint)PointerShapeType.Monochrome)
        {
            CopyMonochrome(buffer, bgra, width, height, pitch);
        }
        else
        {
            return null;
        }

        ulong hash = TileHash.Compute(bgra, width, height);
        return new PointerShape(hash, width, height, info.HotSpot.X, info.HotSpot.Y, bgra);
    }

    /// <summary>Copies a 32-bit shape, turning the masked-colour AND mask into an alpha.</summary>
    private static void CopyColor(byte[] buffer, byte[] bgra, int width, int height, int pitch, bool masked)
    {
        for (int y = 0; y < height; y++)
        {
            int sourceRow = y * pitch;
            int targetRow = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int source = sourceRow + (x * 4);
                int target = targetRow + (x * 4);
                bgra[target] = buffer[source];
                bgra[target + 1] = buffer[source + 1];
                bgra[target + 2] = buffer[source + 2];

                // For masked colour the driver puts an AND mask in the fourth byte: 0 means "draw the colour",
                // 0xFF means "keep what is underneath". Inverting it gives the alpha everything else expects.
                bgra[target + 3] = masked ? (byte)(255 - buffer[source + 3]) : buffer[source + 3];
            }
        }
    }

    /// <summary>Expands the two 1-bpp masks of a monochrome pointer into BGRA with alpha.</summary>
    private static void CopyMonochrome(byte[] buffer, byte[] bgra, int width, int height, int pitch)
    {
        int xorBase = height * pitch;
        for (int y = 0; y < height; y++)
        {
            int andRow = y * pitch;
            int xorRow = xorBase + andRow;
            int targetRow = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int byteIndex = x >> 3;
                byte bit = (byte)(1 << (7 - (x & 7)));
                bool and = (buffer[andRow + byteIndex] & bit) != 0;
                bool xor = (buffer[xorRow + byteIndex] & bit) != 0;

                int target = targetRow + (x * 4);
                if (and && !xor)
                {
                    continue; // (1, 0): the desktop shows through.
                }

                byte value = (byte)(xor ? 255 : 0);
                bgra[target] = value;
                bgra[target + 1] = value;
                bgra[target + 2] = value;
                bgra[target + 3] = 255;
            }
        }
    }
}
