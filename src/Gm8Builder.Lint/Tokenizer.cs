namespace Gm8Builder.Lint;

public enum TokenType { Ident, Number, String, Punct, Unknown, UnterminatedString, UnterminatedComment }

public readonly record struct Token(TokenType Type, string Value, int Line, int Col);

public static class Tokenizer
{
    // Longest match first: every two-character operator precedes its one-character prefix.
    private static readonly string[] Punct =
    [
        "&&", "||", "^^", "<<", ">>", "<=", ">=", "==", "!=", ":=", "+=", "-=", "*=", "/=", "|=", "&=", "^=",
        "{", "}", "(", ")", "[", "]", ";", ",", ".", "+", "-", "*", "/", "=", "<", ">", "!", "~", "?", ":", "&", "|", "^", "@", "$", "#",
    ];

    private static bool IsDigit(char c) => c is >= '0' and <= '9';
    private static bool IsHex(char c) => char.IsAsciiHexDigit(c);
    private static bool IsIdentStart(char c) => char.IsAsciiLetter(c) || c == '_';
    private static bool IsIdentPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    public static List<Token> Tokenize(string src)
    {
        var toks = new List<Token>();
        int i = 0, line = 1, col = 1;
        char At(int k) => k < src.Length ? src[k] : '\0';

        while (i < src.Length)
        {
            var ch = src[i];

            if (ch == '\n') { i++; line++; col = 1; continue; }
            if (ch is ' ' or '\t' or '\r') { i++; col++; continue; }

            // comments
            if (ch == '/' && At(i + 1) == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
                continue;
            }
            if (ch == '/' && At(i + 1) == '*')
            {
                int sl = line, sc = col;
                i += 2; col += 2;
                var closed = false;
                while (i < src.Length)
                {
                    if (src[i] == '*' && At(i + 1) == '/') { i += 2; col += 2; closed = true; break; }
                    if (src[i] == '\n') { line++; col = 1; } else col++;
                    i++;
                }
                if (!closed) toks.Add(new Token(TokenType.UnterminatedComment, "/*", sl, sc));
                continue;
            }

            // strings: GM8 has no escape sequences at all
            if (ch is '"' or '\'')
            {
                int sl = line, sc = col, start = i + 1;
                var quote = ch;
                i++; col++;
                var closed = false;
                while (i < src.Length)
                {
                    if (src[i] == quote) { closed = true; break; }
                    if (src[i] == '\n') { line++; col = 1; } else col++;
                    i++;
                }
                var val = src[start..i];
                if (closed) { i++; col++; }
                toks.Add(closed ? new Token(TokenType.String, val, sl, sc) : new Token(TokenType.UnterminatedString, quote.ToString(), sl, sc));
                continue;
            }

            // numbers (including $ hex)
            if (IsDigit(ch) || (ch == '.' && IsDigit(At(i + 1))))
            {
                int sl = line, sc = col, start = i;
                while (i < src.Length && (IsDigit(src[i]) || src[i] == '.')) { i++; col++; }
                toks.Add(new Token(TokenType.Number, src[start..i], sl, sc));
                continue;
            }
            if (ch == '$' && IsHex(At(i + 1)))
            {
                int sl = line, sc = col, start = i;
                i++; col++;
                while (i < src.Length && IsHex(src[i])) { i++; col++; }
                toks.Add(new Token(TokenType.Number, src[start..i], sl, sc));
                continue;
            }

            // identifiers
            if (IsIdentStart(ch))
            {
                int sl = line, sc = col, start = i;
                while (i < src.Length && IsIdentPart(src[i])) { i++; col++; }
                toks.Add(new Token(TokenType.Ident, src[start..i], sl, sc));
                continue;
            }

            string? matched = null;
            foreach (var p in Punct)
            {
                if (string.CompareOrdinal(src, i, p, 0, p.Length) == 0) { matched = p; break; }
            }
            if (matched != null)
            {
                toks.Add(new Token(TokenType.Punct, matched, line, col));
                i += matched.Length; col += matched.Length;
                continue;
            }

            toks.Add(new Token(TokenType.Unknown, ch.ToString(), line, col));
            i++; col++;
        }
        return toks;
    }
}
