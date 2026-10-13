using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace MarocLIS.Infrastructure.Persistence.Demo;

/// <summary>
/// The two binary files the demonstration data needs: a site logo and the PDF content of the
/// controlled documents.
/// <para>
/// Both are produced in code rather than shipped as resources. A resource file is one more thing a
/// build can forget to embed, and the Infrastructure project deliberately carries no imaging or PDF
/// library — the renderers live in MarocLIS.Reporting, which this assembly cannot reference without
/// inverting the layering. What is needed here is small enough to write directly: an RGBA PNG is a
/// header, one zlib stream and a checksum, and a one-page PDF is five objects and a cross-reference
/// table.
/// </para>
/// <para>
/// The files are real, valid files and not placeholder bytes. A logo that is not a decodable PNG
/// makes the report renderer throw on the first release, and a "PDF" that is not a PDF makes the
/// document screen's Open button look broken — which is the opposite of what demonstration data is
/// for.
/// </para>
/// </summary>
internal static class DemoAssets
{
    // ---- Logo ------------------------------------------------------------------------------------

    /// <summary>
    /// A 160 × 160 logo: a teal disc carrying a white laboratory flask. Deliberately generic, with
    /// no lettering, so it cannot be mistaken for a real laboratory's mark. Rendered with 4 × 4
    /// supersampling so the edges are smooth on a report.
    /// </summary>
    public static byte[] CreateLogoPng()
    {
        const int size = 160;
        const int samples = 4;

        var pixels = new byte[size * size * 4];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                double r = 0, g = 0, b = 0, a = 0;

                for (var sy = 0; sy < samples; sy++)
                {
                    for (var sx = 0; sx < samples; sx++)
                    {
                        var px = x + (sx + 0.5) / samples;
                        var py = y + (sy + 0.5) / samples;

                        var (cr, cg, cb, ca) = Shade(px, py);
                        r += cr * ca; g += cg * ca; b += cb * ca; a += ca;
                    }
                }

                var offset = (y * size + x) * 4;
                var count = samples * samples;

                if (a > 0)
                {
                    pixels[offset] = (byte)Math.Round(r / a);
                    pixels[offset + 1] = (byte)Math.Round(g / a);
                    pixels[offset + 2] = (byte)Math.Round(b / a);
                }

                pixels[offset + 3] = (byte)Math.Round(a / count * 255);
            }
        }

        return EncodePng(size, size, pixels);

        // Colour and opacity (0..1) of one sub-sample.
        static (double R, double G, double B, double A) Shade(double x, double y)
        {
            const double cx = 80, cy = 80;

            var dx = x - cx;
            var dy = y - cy;
            var distance = Math.Sqrt(dx * dx + dy * dy);

            if (distance > 76) return (0, 0, 0, 0);

            // A thin white ring inside the disc, so the logo reads on a coloured header too.
            if (distance is > 68 and < 71) return (255, 255, 255, 1);

            if (InFlask(x, y))
            {
                // The liquid: the lower part of the flask body, in a lighter teal.
                return y > 100 ? (126, 214, 204, 1) : (255, 255, 255, 1);
            }

            return (0, 121, 120, 1);
        }

        // An Erlenmeyer flask: a neck from y 34 to 70, then a trapezoid body to y 124.
        static bool InFlask(double x, double y)
        {
            if (y is >= 30 and < 36) return x is >= 64 and <= 96;   // the lip
            if (y is >= 36 and < 70) return x is >= 70 and <= 90;   // the neck

            if (y is >= 70 and <= 124)
            {
                var t = (y - 70) / 54.0;
                var halfWidth = 10 + t * 30;
                return Math.Abs(x - 80) <= halfWidth;
            }

            return false;
        }
    }

    private static byte[] EncodePng(int width, int height, byte[] rgba)
    {
        using var output = new MemoryStream();

        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8;   // bit depth
        header[9] = 6;   // colour type: RGBA
        header[10] = 0;  // compression
        header[11] = 0;  // filter method
        header[12] = 0;  // no interlace
        WriteChunk(output, "IHDR", header);

        // Each scanline is prefixed with its filter type; 0 (none) is enough for a flat logo.
        var raw = new byte[height * (width * 4 + 1)];

        for (var y = 0; y < height; y++)
        {
            raw[y * (width * 4 + 1)] = 0;
            Buffer.BlockCopy(rgba, y * width * 4, raw, y * (width * 4 + 1) + 1, width * 4);
        }

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(raw);

            WriteChunk(output, "IDAT", compressed.ToArray());
        }

        WriteChunk(output, "IEND", []);

        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        output.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crc = Crc32(typeBytes, data);
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, crc);
        output.Write(crcBytes);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];

        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(byte[] first, byte[] second)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var b in first) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in second) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);

        return crc ^ 0xFFFFFFFFu;
    }

    // ---- Documents -----------------------------------------------------------------------------

    /// <summary>
    /// A one-page PDF with a title and a few lines of body text, in Helvetica.
    /// <para>
    /// The text is written in WinAnsi, which shares its accented French letters with Latin-1, so
    /// <c>é</c>, <c>à</c> and <c>ç</c> survive; the few typographic characters outside it are
    /// replaced with their plain equivalents rather than printed as question marks.
    /// </para>
    /// </summary>
    public static byte[] CreateDocumentPdf(string title, IReadOnlyList<string> lines)
    {
        var content = new StringBuilder();
        content.Append("BT\n/F1 16 Tf\n56 780 Td\n(").Append(Escape(title)).Append(") Tj\n");
        content.Append("/F1 10 Tf\n0 -14 Td\n(")
               .Append(Escape("DOCUMENT DE DÉMONSTRATION - NE PAS UTILISER EN PRODUCTION"))
               .Append(") Tj\n");
        content.Append("/F1 11 Tf\n0 -28 Td\n");

        foreach (var line in lines)
            content.Append('(').Append(Escape(line)).Append(") Tj\n0 -16 Td\n");

        content.Append("ET\n");

        var stream = Encoding.Latin1.GetBytes(content.ToString());

        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] " +
                  "/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
        };

        objects.Add([.. Ascii($"<< /Length {stream.Length} >>\nstream\n"), .. stream, .. Ascii("\nendstream")]);

        using var output = new MemoryStream();
        output.Write(Ascii("%PDF-1.4\n"));

        var offsets = new List<long>(objects.Count);

        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            output.Write(Ascii($"{i + 1} 0 obj\n"));
            output.Write(objects[i]);
            output.Write(Ascii("\nendobj\n"));
        }

        var xref = output.Position;

        var table = new StringBuilder();
        table.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n");
        table.Append("0000000000 65535 f \n");

        foreach (var offset in offsets)
            table.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");

        table.Append(CultureInfo.InvariantCulture,
                     $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        output.Write(Ascii(table.ToString()));

        return output.ToArray();

        static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
    }

    private static string Escape(string text)
    {
        var plain = text.Replace('—', '-').Replace('–', '-').Replace('’', '\'')
                        .Replace("œ", "oe", StringComparison.Ordinal)
                        .Replace("Œ", "OE", StringComparison.Ordinal);

        var builder = new StringBuilder(plain.Length);

        foreach (var c in plain)
        {
            if (c is '(' or ')' or '\\') builder.Append('\\');
            builder.Append(c > 0xFF ? '?' : c);
        }

        return builder.ToString();
    }
}
