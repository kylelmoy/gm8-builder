namespace Gm8Builder.Exe;

/// <summary>
/// The swap-table cipher over the whole asset stream. Decryption is a byte
/// substitution through the inverse table, subtracting the previous ciphertext
/// byte and the position, then a run of swaps walked backwards. Encryption is
/// exactly that inverted: swaps forwards, then the substitution ascending so each
/// byte sees the ciphertext byte before it.
/// </summary>
public static class SwapCipher
{
    public static byte[] Decrypt(ReadOnlySpan<byte> buf, byte[] table)
    {
        var rev = new byte[256];
        for (var i = 0; i < 256; i++) rev[table[i]] = (byte)i;

        var d = buf.ToArray();
        var n = d.Length;
        for (var i = n; i >= 2; i--) d[i - 1] = (byte)(rev[d[i - 1]] - (d[i - 2] + ((i - 1) & 0xff)));
        for (var i = n - 1; i >= 0; i--)
        {
            var b = Math.Max(i - table[i & 0xff], 0);
            (d[i], d[b]) = (d[b], d[i]);
        }
        return d;
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> buf, byte[] table)
    {
        var d = buf.ToArray();
        var n = d.Length;
        for (var i = 0; i < n; i++)
        {
            var b = Math.Max(i - table[i & 0xff], 0);
            (d[i], d[b]) = (d[b], d[i]);
        }
        for (var i = 2; i <= n; i++) d[i - 1] = table[(d[i - 1] + d[i - 2] + ((i - 1) & 0xff)) & 0xff];
        return d;
    }

    /// <summary>A fresh table: any permutation of 0..255 decrypts, and the runner reads it from the file.</summary>
    public static byte[] RandomTable(Random rng)
    {
        var t = new byte[256];
        for (var i = 0; i < 256; i++) t[i] = (byte)i;
        rng.Shuffle(t);
        return t;
    }
}

/// <summary>
/// The substitution cipher over an extension's file contents: a 256-byte table
/// derived from a seed stored beside the data. The first byte is left in the
/// clear.
/// </summary>
public static class ExtensionCipher
{
    /// <summary>The decryption table: ciphertext byte -> plaintext byte.</summary>
    public static byte[] DecryptTable(uint seed)
    {
        var t = new byte[0x200];
        var seed1 = (int)seed;
        var seed2 = seed1 % 0xFA + 6;
        seed1 /= 0xFA;
        if (seed1 < 0) seed1 += 100;
        if (seed2 < 0) seed2 += 100;
        for (var i = 0; i < t.Length; i++) t[i] = (byte)(i % 256);

        for (uint i = 1; i < 0x2711; i++)
        {
            var idx = (int)((i * (uint)seed2 + (uint)seed1) % 0xFE + 1);
            (t[idx], t[idx + 1]) = (t[idx + 1], t[idx]);
        }
        for (var i = 0; i < 0x100; i++) t[t[i + 1] + 0x100] = (byte)(i + 1);

        return t.AsSpan(0x100, 0x100).ToArray();
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> data, uint seed)
    {
        var table = DecryptTable(seed);
        var d = data.ToArray();
        for (var i = 1; i < d.Length; i++) d[i] = table[d[i]];
        return d;
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> data, uint seed)
    {
        var dec = DecryptTable(seed);
        var enc = new byte[256];
        var seen = new bool[256];
        for (var c = 0; c < 256; c++)
        {
            if (seen[dec[c]]) throw new InvalidOperationException($"extension cipher table for seed {seed} is not a permutation");
            seen[dec[c]] = true;
            enc[dec[c]] = (byte)c;
        }
        var d = data.ToArray();
        for (var i = 1; i < d.Length; i++) d[i] = enc[d[i]];
        return d;
    }
}
