namespace KeystrokeApp.Services;

/// <summary>
/// RFC 4648 Base32 encoding/decoding for license key serialization.
/// </summary>
internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(byte[] data)
    {
        var chars = new char[(data.Length * 8 + 4) / 5];
        int bitBuffer = 0, bitsInBuffer = 0, index = 0;

        foreach (var b in data)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bitsInBuffer += 8;
            while (bitsInBuffer >= 5)
            {
                bitsInBuffer -= 5;
                chars[index++] = Alphabet[(bitBuffer >> bitsInBuffer) & 0x1F];
            }
        }

        if (bitsInBuffer > 0)
            chars[index++] = Alphabet[(bitBuffer << (5 - bitsInBuffer)) & 0x1F];

        return new string(chars, 0, index);
    }

    public static byte[]? Decode(string encoded)
    {
        var bits = new List<byte>();
        int bitBuffer = 0, bitsInBuffer = 0;

        foreach (var c in encoded)
        {
            int val = CharToValue(c);
            if (val < 0)
                return null;

            bitBuffer = (bitBuffer << 5) | val;
            bitsInBuffer += 5;
            if (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                bits.Add((byte)((bitBuffer >> bitsInBuffer) & 0xFF));
            }
        }

        return bits.ToArray();
    }

    private static int CharToValue(char c) => c switch
    {
        >= 'A' and <= 'Z' => c - 'A',
        >= 'a' and <= 'z' => c - 'a',
        >= '2' and <= '7' => c - '2' + 26,
        _ => -1
    };
}
