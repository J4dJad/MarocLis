using System.Globalization;
using System.Text;

namespace MarocLIS.Instruments.Protocol;

/// <summary>
/// The ASTM E1381 low-level link layer: control characters, frame assembly and the checksum.
/// <para>
/// Analyser vendors implement this layer with small but consequential variations, so it is kept
/// separate from the E1394 record grammar above it. The checksum in particular is where most
/// integrations fail: it covers the bytes <em>after</em> STX up to and including the terminating
/// ETX or ETB, is taken modulo 256, and is transmitted as two upper-case hexadecimal characters.
/// </para>
/// </summary>
public static class AstmControl
{
    public const byte Enq = 0x05; // Enquiry — sender requests the line
    public const byte Ack = 0x06; // Acknowledge
    public const byte Nak = 0x15; // Negative acknowledge — resend
    public const byte Eot = 0x04; // End of transmission — line released
    public const byte Stx = 0x02; // Start of a frame
    public const byte Etx = 0x03; // End of the last frame of a message
    public const byte Etb = 0x17; // End of an intermediate frame
    public const byte Cr = 0x0D;
    public const byte Lf = 0x0A;

    /// <summary>Maximum characters of record text per frame, per E1381.</summary>
    public const int MaxFramePayload = 240;

    /// <summary>Frame numbers cycle 1…7 then back to 0.</summary>
    public static int NextFrameNumber(int current) => (current + 1) % 8;

    /// <summary>
    /// Checksum of a frame body. <paramref name="body"/> must be the bytes between STX and the
    /// terminator, and <paramref name="terminator"/> the ETX or ETB that closed the frame.
    /// </summary>
    public static string Checksum(ReadOnlySpan<byte> body, byte terminator)
    {
        var sum = 0;
        foreach (var b in body) sum += b;
        sum += terminator;

        // Invariant: these two characters go on the wire and the analyser compares them byte for
        // byte. Nothing about a checksum is localisable, and a culture that rendered the digits any
        // other way would make every frame fail its integrity check on that workstation alone.
        return (sum & 0xFF).ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds a complete frame: STX, frame number, text, terminator, checksum, CR LF.
    /// </summary>
    public static byte[] BuildFrame(int frameNumber, string text, bool isLast, Encoding encoding)
    {
        var payload = encoding.GetBytes(text);
        var terminator = isLast ? Etx : Etb;

        var body = new byte[payload.Length + 1];
        body[0] = (byte)('0' + (frameNumber % 8));
        payload.CopyTo(body, 1);

        var checksum = Checksum(body, terminator);

        var frame = new List<byte>(body.Length + 6) { Stx };
        frame.AddRange(body);
        frame.Add(terminator);
        frame.AddRange(encoding.GetBytes(checksum));
        frame.Add(Cr);
        frame.Add(Lf);

        return [.. frame];
    }

    /// <summary>
    /// Splits a message into frames of at most <see cref="MaxFramePayload"/> characters, numbered
    /// from <paramref name="startFrameNumber"/>. Records are not split across frames where that can
    /// be avoided, because several analysers tolerate it poorly.
    /// </summary>
    public static IReadOnlyList<byte[]> BuildFrames(IEnumerable<string> records, Encoding encoding,
                                                    int startFrameNumber = 1)
    {
        var frames = new List<byte[]>();
        var buffer = new StringBuilder();
        var frameNumber = startFrameNumber;
        var pending = new List<string>();

        void Flush(bool isLast)
        {
            if (buffer.Length == 0) return;
            frames.Add(BuildFrame(frameNumber, buffer.ToString(), isLast, encoding));
            frameNumber = NextFrameNumber(frameNumber);
            buffer.Clear();
        }

        pending.AddRange(records);

        for (var i = 0; i < pending.Count; i++)
        {
            var record = pending[i] + "\r";

            if (buffer.Length > 0 && buffer.Length + record.Length > MaxFramePayload)
                Flush(isLast: false);

            if (record.Length > MaxFramePayload)
            {
                // A single oversized record has to be split; do it on the byte boundary E1381 allows.
                var offset = 0;
                while (offset < record.Length)
                {
                    var take = Math.Min(MaxFramePayload, record.Length - offset);
                    buffer.Append(record, offset, take);
                    offset += take;
                    Flush(isLast: offset >= record.Length && i == pending.Count - 1);
                }
                continue;
            }

            buffer.Append(record);
        }

        Flush(isLast: true);
        return frames;
    }
}

/// <summary>A frame received from an analyser, once its integrity has been checked.</summary>
public sealed record AstmFrame(int FrameNumber, string Text, bool IsLast, bool ChecksumValid)
{
    /// <summary>Records carried by the frame, split on CR and with empties dropped.</summary>
    public IReadOnlyList<string> Records =>
        [.. Text.Split('\r', StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.TrimEnd('\n'))
                .Where(r => r.Length > 0)];
}

/// <summary>
/// Reassembles a byte stream from an analyser into frames.
/// <para>
/// Written as an incremental reader rather than a line reader because serial data arrives in
/// arbitrary chunks: a frame is routinely split across two reads, and two frames routinely arrive
/// in one. Feeding bytes in and pulling whole frames out is the only shape that survives that.
/// </para>
/// </summary>
public sealed class AstmFrameReader(Encoding? encoding = null)
{
    private readonly Encoding _encoding = encoding ?? Encoding.Latin1;
    private readonly List<byte> _buffer = [];

    /// <summary>Control characters seen outside a frame, in arrival order, for the driver to act on.</summary>
    private readonly Queue<byte> _controls = new();

    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (var b in data) _buffer.Add(b);
    }

    /// <summary>Takes the next bare control character (ENQ, ACK, NAK, EOT), if one is pending.</summary>
    public byte? TryDequeueControl() => _controls.Count > 0 ? _controls.Dequeue() : null;

    /// <summary>
    /// Extracts the next complete frame. Returns null when more bytes are needed. Control
    /// characters found before a frame are queued rather than discarded.
    /// </summary>
    public AstmFrame? TryReadFrame()
    {
        while (_buffer.Count > 0)
        {
            var start = _buffer.IndexOf(AstmControl.Stx);

            if (start < 0)
            {
                // No frame start in the buffer: everything here is control traffic or noise.
                foreach (var b in _buffer)
                    if (b is AstmControl.Enq or AstmControl.Ack or AstmControl.Nak or AstmControl.Eot)
                        _controls.Enqueue(b);

                _buffer.Clear();
                return null;
            }

            for (var i = 0; i < start; i++)
            {
                var b = _buffer[i];
                if (b is AstmControl.Enq or AstmControl.Ack or AstmControl.Nak or AstmControl.Eot)
                    _controls.Enqueue(b);
            }

            if (start > 0) _buffer.RemoveRange(0, start);

            // Locate the terminator; the checksum's two characters follow it.
            var terminatorIndex = -1;
            for (var i = 1; i < _buffer.Count; i++)
            {
                if (_buffer[i] is AstmControl.Etx or AstmControl.Etb)
                {
                    terminatorIndex = i;
                    break;
                }
            }

            if (terminatorIndex < 0 || _buffer.Count < terminatorIndex + 3)
                return null; // Incomplete — wait for more bytes.

            var terminator = _buffer[terminatorIndex];
            var body = _buffer.GetRange(1, terminatorIndex - 1).ToArray();

            var expected = AstmControl.Checksum(body, terminator);
            var received = _encoding.GetString([_buffer[terminatorIndex + 1], _buffer[terminatorIndex + 2]]);

            var frameNumber = body.Length > 0 && body[0] is >= (byte)'0' and <= (byte)'7'
                ? body[0] - '0'
                : -1;

            var text = body.Length > 1 ? _encoding.GetString(body, 1, body.Length - 1) : string.Empty;

            // Consume the frame plus any trailing CR LF.
            var consumed = terminatorIndex + 3;
            while (consumed < _buffer.Count && _buffer[consumed] is AstmControl.Cr or AstmControl.Lf)
                consumed++;

            _buffer.RemoveRange(0, consumed);

            return new AstmFrame(
                frameNumber,
                text,
                terminator == AstmControl.Etx,
                string.Equals(expected, received, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    public void Reset()
    {
        _buffer.Clear();
        _controls.Clear();
    }
}
