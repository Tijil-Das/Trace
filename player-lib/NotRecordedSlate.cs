namespace ScreenRecall.Player;

/// <summary>
/// Draws the "not recorded" card that the exporter bakes into frames falling inside a stretch nobody recorded (see
/// <see cref="SessionActivity"/>).
/// </summary>
/// <remarks>
/// Playback says this with a vector overlay in the dashboard, where the DOM can draw crisp text at any size. A video
/// has no DOM: the card has to be pixels, so this rasterises it — background, hatch, camera-with-a-slash icon and
/// text from a built-in 5x7 font. Nothing here is part of capture or of the store: it exists only in exported
/// frames, which is the one place a held frame would otherwise be indistinguishable from current content.
/// </remarks>
internal static class NotRecordedSlate
{
    private const int GlyphWidth = 5;
    private const int GlyphHeight = 7;

    /// <summary>Glyph rows, five bits each, most significant bit leftmost. Upper case only; the caller upper-cases.</summary>
    private static readonly Dictionary<char, byte[]> Font = new()
    {
        [' '] = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
        ['A'] = new byte[] { 0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11 },
        ['B'] = new byte[] { 0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E },
        ['C'] = new byte[] { 0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E },
        ['D'] = new byte[] { 0x1E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1E },
        ['E'] = new byte[] { 0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F },
        ['F'] = new byte[] { 0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10 },
        ['G'] = new byte[] { 0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F },
        ['H'] = new byte[] { 0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11 },
        ['I'] = new byte[] { 0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x1F },
        ['J'] = new byte[] { 0x07, 0x02, 0x02, 0x02, 0x02, 0x12, 0x0C },
        ['K'] = new byte[] { 0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11 },
        ['L'] = new byte[] { 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F },
        ['M'] = new byte[] { 0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11 },
        ['N'] = new byte[] { 0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11 },
        ['O'] = new byte[] { 0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E },
        ['P'] = new byte[] { 0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10 },
        ['Q'] = new byte[] { 0x0E, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0D },
        ['R'] = new byte[] { 0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11 },
        ['S'] = new byte[] { 0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E },
        ['T'] = new byte[] { 0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04 },
        ['U'] = new byte[] { 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E },
        ['V'] = new byte[] { 0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04 },
        ['W'] = new byte[] { 0x11, 0x11, 0x11, 0x15, 0x15, 0x1B, 0x11 },
        ['X'] = new byte[] { 0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11 },
        ['Y'] = new byte[] { 0x11, 0x11, 0x0A, 0x04, 0x04, 0x04, 0x04 },
        ['Z'] = new byte[] { 0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F },
        ['0'] = new byte[] { 0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E },
        ['1'] = new byte[] { 0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E },
        ['2'] = new byte[] { 0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F },
        ['3'] = new byte[] { 0x1F, 0x02, 0x04, 0x02, 0x01, 0x11, 0x0E },
        ['4'] = new byte[] { 0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02 },
        ['5'] = new byte[] { 0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E },
        ['6'] = new byte[] { 0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E },
        ['7'] = new byte[] { 0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08 },
        ['8'] = new byte[] { 0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E },
        ['9'] = new byte[] { 0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C },
        [':'] = new byte[] { 0x00, 0x0C, 0x0C, 0x00, 0x0C, 0x0C, 0x00 },
        ['-'] = new byte[] { 0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00 },
        ['.'] = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x0C },
        [','] = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x0C, 0x04, 0x08 },
        ['('] = new byte[] { 0x02, 0x04, 0x08, 0x08, 0x08, 0x04, 0x02 },
        [')'] = new byte[] { 0x08, 0x04, 0x02, 0x02, 0x02, 0x04, 0x08 },
        ['/'] = new byte[] { 0x01, 0x02, 0x02, 0x04, 0x08, 0x08, 0x10 },
        ['+'] = new byte[] { 0x00, 0x04, 0x04, 0x1F, 0x04, 0x04, 0x00 },
        ['!'] = new byte[] { 0x04, 0x04, 0x04, 0x04, 0x04, 0x00, 0x04 },
        ['<'] = new byte[] { 0x02, 0x04, 0x08, 0x10, 0x08, 0x04, 0x02 },
        ['>'] = new byte[] { 0x08, 0x04, 0x02, 0x01, 0x02, 0x04, 0x08 },
    };

    /// <summary>
    /// Draws the card over the whole frame: hatched background, a bordered panel, the camera-with-a-slash icon and
    /// up to three lines of text. Sized from the frame, so a 640-wide export gets the same card as a 4K one.
    /// </summary>
    internal static void Draw(byte[] bgra, int width, int height, int stride, string title, string detail, string reason)
    {
        if (bgra.Length < stride * height || width <= 0 || height <= 0)
        {
            return;
        }

        uint backgroundTop = Bgra(0x14, 0x18, 0x1E);
        uint backgroundBottom = Bgra(0x0B, 0x0E, 0x12);
        uint hatchLight = Bgra(0x33, 0x16, 0x1D);
        uint hatchDark = Bgra(0x24, 0x0F, 0x15);
        uint panel = Bgra(0x1B, 0x10, 0x15);
        uint border = Bgra(0x7A, 0x36, 0x46);
        uint accent = Bgra(0xC2, 0x5B, 0x72);
        uint titleColour = Bgra(0xF3, 0xDD, 0xE3);
        uint detailColour = Bgra(0xCB, 0xB9, 0xC0);
        uint reasonColour = Bgra(0x9C, 0x84, 0x8C);

        for (int y = 0; y < height; y++)
        {
            uint row = Mix(backgroundTop, backgroundBottom, y / (double)Math.Max(1, height - 1));
            for (int x = 0; x < width; x++)
            {
                SetPixel(bgra, width, height, stride, x, y, row);
            }
        }

        // The hatch is the same visual language as the gap bands on the dashboard timeline.
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (((x + y) / 8) % 2 != 0)
                {
                    continue;
                }

                SetPixel(bgra, width, height, stride, x, y, ((x + y) / 8) % 4 == 0 ? hatchLight : hatchDark);
            }
        }

        int panelWidth = Math.Clamp((int)(width * 0.74), 180, 1120);
        int panelHeight = Math.Clamp((int)(height * 0.46), 120, 460);
        int left = (width - panelWidth) / 2;
        int top = (height - panelHeight) / 2;
        Fill(bgra, width, height, stride, left, top, panelWidth, panelHeight, border);
        Fill(bgra, width, height, stride, left + 3, top + 3, panelWidth - 6, panelHeight - 6, panel);

        int titleScale = Math.Clamp(Math.Min(width / 190, height / 130), 2, 9);
        int detailScale = Math.Max(1, titleScale - 2);
        int iconSize = (GlyphHeight * titleScale) * 3;
        int cursor = top + (int)(panelHeight * 0.10);
        CameraSlash(bgra, width, height, stride, left + ((panelWidth - iconSize) / 2), cursor, iconSize, accent, panel);
        cursor += iconSize + (titleScale * 6);

        DrawTextCentered(bgra, width, height, stride, left, panelWidth, cursor, title, titleScale, titleColour);
        cursor += (GlyphHeight * titleScale) + (titleScale * 5);
        DrawTextCentered(bgra, width, height, stride, left, panelWidth, cursor, detail, detailScale, detailColour);
        cursor += (GlyphHeight * detailScale) + (detailScale * 4);
        DrawTextCentered(bgra, width, height, stride, left, panelWidth, cursor, reason, detailScale, reasonColour);
    }

    /// <summary>Width in pixels a string occupies at a scale.</summary>
    internal static int MeasureText(string text, int scale)
        => text.Length == 0 ? 0 : (((text.Length * (GlyphWidth + 1)) - 1) * scale);

    /// <summary>Draws one line of text, left edge at x.</summary>
    internal static void DrawText(
        byte[] bgra,
        int width,
        int height,
        int stride,
        int x,
        int y,
        string text,
        int scale,
        uint colour)
    {
        for (int index = 0; index < text.Length; index++)
        {
            char character = char.ToUpperInvariant(text[index]);
            if (!Font.TryGetValue(character, out byte[]? glyph))
            {
                glyph = Font[' '];
            }

            int glyphX = x + (index * (GlyphWidth + 1) * scale);
            for (int row = 0; row < GlyphHeight; row++)
            {
                byte bits = glyph[row];
                for (int column = 0; column < GlyphWidth; column++)
                {
                    if ((bits & (1 << (GlyphWidth - 1 - column))) == 0)
                    {
                        continue;
                    }

                    Fill(
                        bgra,
                        width,
                        height,
                        stride,
                        glyphX + (column * scale),
                        y + (row * scale),
                        scale,
                        scale,
                        colour);
                }
            }
        }
    }

    private static void DrawTextCentered(
        byte[] bgra,
        int width,
        int height,
        int stride,
        int panelLeft,
        int panelWidth,
        int y,
        string text,
        int scale,
        uint colour)
    {
        int textWidth = MeasureText(text, scale);
        DrawText(bgra, width, height, stride, panelLeft + ((panelWidth - textWidth) / 2), y, text, scale, colour);
    }

    /// <summary>A camera with a slash through it: the shape says "nothing was recorded" without words.</summary>
    private static void CameraSlash(
        byte[] bgra,
        int width,
        int height,
        int stride,
        int x,
        int y,
        int size,
        uint colour,
        uint cut)
    {
        int unit = Math.Max(2, size / 10);
        int bodyLeft = x + unit;
        int bodyTop = y + (size / 3);
        int bodyWidth = Math.Max(unit * 4, size - (unit * 2) - (size / 8));
        int bodyHeight = Math.Max(unit * 3, (size * 11) / 20);
        Fill(bgra, width, height, stride, bodyLeft, bodyTop, bodyWidth, bodyHeight, colour);

        // The lens is a ring: a filled disc with the inside punched back out in the panel colour.
        int centreX = bodyLeft + (bodyWidth / 2);
        int centreY = bodyTop + (bodyHeight / 2);
        int radius = Math.Max(unit, Math.Min(bodyWidth, bodyHeight) / 3);
        for (int py = centreY - radius - 1; py <= centreY + radius + 1; py++)
        {
            for (int px = centreX - radius - 1; px <= centreX + radius + 1; px++)
            {
                double distance = Math.Sqrt(((px - centreX) * (px - centreX)) + ((py - centreY) * (py - centreY)));
                if (distance <= radius && distance >= radius - unit)
                {
                    SetPixel(bgra, width, height, stride, px, py, cut);
                }
            }
        }

        Fill(bgra, width, height, stride, x + (size / 4), y + (size / 5), bodyWidth / 3, unit, colour);

        // The slash: a thick line stamped in the panel colour first, so it visibly cuts the camera, then in accent.
        int thickness = Math.Max(2, size / 12);
        int cutThickness = thickness + Math.Max(2, size / 14);
        int fromX = x + (size / 7);
        int fromY = y + size - (size / 9);
        int toX = x + size - (size / 9);
        int toY = y + (size / 7);
        int steps = Math.Max(1, Math.Max(Math.Abs(toX - fromX), Math.Abs(toY - fromY)));
        for (int step = 0; step <= steps; step++)
        {
            int px = fromX + (((toX - fromX) * step) / steps);
            int py = fromY + (((toY - fromY) * step) / steps);
            Fill(bgra, width, height, stride, px - (cutThickness / 2), py - (cutThickness / 2), cutThickness, cutThickness, cut);
        }

        for (int step = 0; step <= steps; step++)
        {
            int px = fromX + (((toX - fromX) * step) / steps);
            int py = fromY + (((toY - fromY) * step) / steps);
            Fill(bgra, width, height, stride, px - (thickness / 2), py - (thickness / 2), thickness, thickness, colour);
        }
    }

    private static uint Bgra(byte red, byte green, byte blue) => (uint)((0xFF << 24) | (red << 16) | (green << 8) | blue);

    private static uint Mix(uint from, uint to, double amount)
    {
        double t = Math.Clamp(amount, 0, 1);
        byte fromB = (byte)(from & 0xFF), fromG = (byte)((from >> 8) & 0xFF), fromR = (byte)((from >> 16) & 0xFF);
        byte toB = (byte)(to & 0xFF), toG = (byte)((to >> 8) & 0xFF), toR = (byte)((to >> 16) & 0xFF);
        return Bgra(
            (byte)Math.Round(fromR + ((toR - fromR) * t)),
            (byte)Math.Round(fromG + ((toG - fromG) * t)),
            (byte)Math.Round(fromB + ((toB - fromB) * t)));
    }

    private static void Fill(byte[] bgra, int width, int height, int stride, int x, int y, int w, int h, uint colour)
    {
        for (int py = y; py < y + h; py++)
        {
            for (int px = x; px < x + w; px++)
            {
                SetPixel(bgra, width, height, stride, px, py, colour);
            }
        }
    }

    private static void SetPixel(byte[] bgra, int width, int height, int stride, int x, int y, uint colour)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return;
        }

        int offset = (y * stride) + (x * 4);
        if (offset + 3 >= bgra.Length)
        {
            return;
        }

        bgra[offset] = (byte)(colour & 0xFF);
        bgra[offset + 1] = (byte)((colour >> 8) & 0xFF);
        bgra[offset + 2] = (byte)((colour >> 16) & 0xFF);
        bgra[offset + 3] = 0xFF;
    }
}
