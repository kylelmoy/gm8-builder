namespace Gm8Builder.Pe;

/// <summary>
/// gm8x_fix's patches for a GM8.0 runner: input lag, joystick polling,
/// scheduler resolution, DirectPlay, keyboard release delay and large-address
/// awareness. Same rule as `gm8x_fix -s`: a patch applies when every byte it
/// touches still holds its original value, all checked before any is written.
/// </summary>
public static partial class Gm8xFix
{
    /// <summary>The kinds of patch there are, for <see cref="Apply"/>'s `skip`.</summary>
    public static IReadOnlyList<string> Kinds => Patches.Select(p => p.Kind).Distinct().ToArray();

    /// <summary>
    /// Patch `runner` in place, except for the kinds in `skip` (like gm8x_fix's
    /// -n options); returns the names of the patches applied.
    /// </summary>
    public static List<string> Apply(byte[] runner, IReadOnlyCollection<string>? skip = null)
    {
        var able = Patches.Where(p => skip?.Contains(p.Kind) != true && Matches(runner, p.Bytes, original: true)).ToList();
        foreach (var (_, _, bytes) in able)
        {
            for (var i = 0; i + 2 < bytes.Length && bytes[i] >= 0; i += 3) runner[bytes[i]] = (byte)bytes[i + 2];
        }
        return able.Select(p => p.Name).ToList();
    }

    private static bool Matches(byte[] runner, int[] bytes, bool original)
    {
        for (var i = 0; i + 2 < bytes.Length && bytes[i] >= 0; i += 3)
        {
            if (bytes[i] >= runner.Length || runner[bytes[i]] != (original ? bytes[i + 1] : bytes[i + 2])) return false;
        }
        return true;
    }
}
