namespace Gm8Builder;

/// <summary>
/// Byte-preserving text. GM8 stores names and code as ANSI bytes with no
/// declared encoding, so a string here is one char per byte: it round-trips
/// whatever the bytes were.
/// </summary>
public static class Latin1
{
    public static string Decode(ReadOnlySpan<byte> bytes) => System.Text.Encoding.Latin1.GetString(bytes);

    /// <summary>The low byte of every char, never '?' for a char above U+00FF.</summary>
    public static byte[] Encode(string text)
    {
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++) bytes[i] = (byte)text[i];
        return bytes;
    }

    public static string Read(string path) => Decode(File.ReadAllBytes(path));
}
