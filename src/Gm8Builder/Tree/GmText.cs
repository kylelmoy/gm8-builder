namespace Gm8Builder.Tree;

/// <summary>
/// How text from the tree ends up in a built game. gmksplit reads files as
/// UTF-8 into Java strings; by the time Game Maker has written them out as ANSI,
/// every character above 126 is a single '?' (verified byte for byte against
/// real builds). Strings here are byte-per-char, see <see cref="Latin1"/>.
/// </summary>
public static class GmText
{
    /// <summary>A Unicode string as Game Maker stores it.</summary>
    public static string Store(string text)
    {
        var ascii = true;
        foreach (var c in text)
        {
            if (c > 126)
            {
                ascii = false;
                break;
            }
        }
        if (ascii) return text;

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes()) sb.Append(rune.Value > 126 ? '?' : (char)rune.Value);
        return sb.ToString();
    }

    /// <summary>A file read as UTF-8, as gmksplit reads scripts and the game information.</summary>
    public static string ReadFile(string path) => Store(System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)));

    public static string ToCrlf(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");
}
