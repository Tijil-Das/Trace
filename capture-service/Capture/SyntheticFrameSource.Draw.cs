namespace ScreenRecall.CaptureService.Capture;

internal sealed partial class SyntheticFrameSource
{
    private void DrawStaticBackground()
    {
        // Flat UI-like chrome: a gradient plus solid bars that never change, i.e. content that should
        // be stored once and then dedupe for the rest of the session.
        for (int y = 0; y < _monitor.Height; y++)
        {
            byte shade = (byte)(16 + (y * 40 / Math.Max(1, _monitor.Height)));
            for (int x = 0; x < _monitor.Width; x++)
            {
                int offset = ((y * _monitor.Width) + x) * 4;
                _pixels[offset] = shade;
                _pixels[offset + 1] = shade;
                _pixels[offset + 2] = (byte)(shade + 24);
                _pixels[offset + 3] = 0xFF;
            }
        }

        FillRect(0, 0, _monitor.Width, 28, Color(0x30, 0x30, 0x30, 0xFF), 3);
        FillRect(0, _monitor.Height - 40, _monitor.Width, 40, Color(0x28, 0x28, 0x28, 0xFF), 4);
        FillRect(24, 120, _monitor.Width - 48, BandHeight, Color(0x10, 0x30, 0x50, 0xFF), 5);
        DrawTypingBlocks();
    }

    private void DrawTypingBlocks()
    {
        for (int i = 0; i < 12; i++)
        {
            bool lit = ((_typingPhase + i) % 12) < 6;
            uint color = lit ? Color(0xF0, 0xF0, 0xF0, 0xFF) : Color(0x20, 0x20, 0x20, 0xFF);
            FillRect(28 + (i * 16), 44, 12, 16, color, 6);
        }
    }

    private void ScrollBand()
    {
        _scrollOffset = (_scrollOffset + 16) % 48;
        for (int x = 24; x < _monitor.Width - 24; x += 16)
        {
            for (int y = 0; y < 48; y++)
            {
                uint color = Color((byte)(_scrollOffset * 4), (byte)(0x40 + y), 0x80, 0xFF);
                FillRect(x, 120 + y, 14, 1, color, 7);
            }
        }
    }

    private void FillRect(int x, int y, int width, int height, uint bgra, uint variant)
    {
        uint color = variant % 7 == 0 ? Rotate(bgra) : bgra;
        int left = Math.Max(0, x);
        int top = Math.Max(0, y);
        int right = Math.Min(_monitor.Width, x + width);
        int bottom = Math.Min(_monitor.Height, y + height);
        for (int py = top; py < bottom; py++)
        {
            for (int px = left; px < right; px++)
            {
                int offset = ((py * _monitor.Width) + px) * 4;
                _pixels[offset] = (byte)(color & 0xFF);
                _pixels[offset + 1] = (byte)((color >> 8) & 0xFF);
                _pixels[offset + 2] = (byte)((color >> 16) & 0xFF);
                _pixels[offset + 3] = (byte)((color >> 24) & 0xFF);
            }
        }
    }

    private uint Color(byte r, byte g, byte b, byte a)
    {
        uint key = (uint)((r << 24) | (g << 16) | (b << 8) | a);
        if (_colors.TryGetValue(key, out uint existing))
        {
            return existing;
        }

        uint value = (uint)((a << 24) | (r << 16) | (g << 8) | b);
        _colors[key] = value;
        return value;
    }

    private static uint Rotate(uint bgra)
    {
        uint b = (bgra >> 24) & 0xFF;
        uint g = (bgra >> 16) & 0xFF;
        uint r = (bgra >> 8) & 0xFF;
        uint a = bgra & 0xFF;
        return (a << 24) | (b << 16) | (g << 8) | r;
    }
}
