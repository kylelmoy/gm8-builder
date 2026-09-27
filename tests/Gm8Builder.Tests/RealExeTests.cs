using Gm8Builder.Exe;
using Gm8Builder.Model;

namespace Gm8Builder.Tests;

/// <summary>
/// Against a real Game Maker build, named by GM8_TEST_EXE. Passes vacuously
/// without it.
/// </summary>
public class RealExeTests
{
    internal static string? ExePath()
    {
        var path = Environment.GetEnvironmentVariable("GM8_TEST_EXE");
        return string.IsNullOrEmpty(path) ? null : path;
    }

    [Fact]
    public void RoundTripIsByteIdentical()
    {
        if (ExePath() is not { } path) return;
        var original = File.ReadAllBytes(path);
        var cache = new BlobCache();
        var exe = ExeFile.Read(original, cache);
        Assert.True(exe.Write(cache).AsSpan().SequenceEqual(original));
    }

    [Fact]
    public void RecompressedBuildReadsBackTheSame()
    {
        if (ExePath() is not { } path) return;
        var exe = ExeFile.Read(File.ReadAllBytes(path));
        var again = ExeFile.Read(exe.Write());
        Assert.True(again.Data.Write().AsSpan().SequenceEqual(exe.Data.Write()));
    }
}
