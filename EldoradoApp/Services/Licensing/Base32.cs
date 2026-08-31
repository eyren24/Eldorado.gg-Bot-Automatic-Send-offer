using System.Text;

namespace EldoradoApp.Services.Licensing;

/// <summary>
/// Crockford base32: the alphabet drops I, L, O and U, so a key read aloud on Discord or
/// retyped by hand can't turn a 1 into an I or a 0 into an O. Decoding is case-insensitive
/// and folds those look-alikes back, which is the whole point of not using base64 here.
/// </summary>
internal static class Base32
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;

            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Alphabet[(buffer >> bits) & 31]);
            }
        }

        // Trailing bits of the last byte, left-padded with zeros.
        if (bits > 0)
        {
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Decodes <paramref name="text"/> into exactly <paramref name="expectedBytes"/> bytes.
    /// Anything that isn't an alphabet character is rejected — the caller is expected to have
    /// stripped dashes and spaces already via <see cref="Normalize"/>.
    /// </summary>
    public static bool TryDecode(string text, int expectedBytes, out byte[] data)
    {
        data = new byte[expectedBytes];

        var buffer = 0;
        var bits = 0;
        var written = 0;

        foreach (var c in text)
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0)
            {
                return false;
            }

            buffer = (buffer << 5) | value;
            bits += 5;

            if (bits < 8)
            {
                continue;
            }

            bits -= 8;
            if (written == expectedBytes)
            {
                return false;   // more data than the layout allows
            }

            data[written++] = (byte)((buffer >> bits) & 0xFF);
        }

        return written == expectedBytes;
    }

    /// <summary>
    /// Uppercases, throws away every separator the customer's paste may carry (dashes, spaces,
    /// line breaks from a Discord message) and folds the look-alike letters onto their digits.
    /// </summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);

        foreach (var raw in text)
        {
            var c = char.ToUpperInvariant(raw);
            sb.Append(c switch
            {
                'I' or 'L' => '1',
                'O' => '0',
                _ when Alphabet.IndexOf(c) >= 0 => c,
                _ => '\0'          // dropped: dash, space, newline, quotes, anything else
            });
        }

        return sb.ToString().Replace("\0", "");
    }
}
