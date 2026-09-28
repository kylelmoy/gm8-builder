using Gm8Builder.Pe;

namespace Gm8Builder.Tests;

public class Gm8xFixTests
{
    /// <summary>A runner holding only the keyboard_check_direct patch's original bytes.</summary>
    private static byte[] KeyboardRunner()
    {
        var runner = new byte[2_000_000];
        new byte[] { 0x66, 0x85, 0xC0, 0x0F, 0x95, 0xC0 }.CopyTo(runner, 0x1642e9);
        return runner;
    }

    [Fact]
    public void AppliesAPatchWhoseOriginalBytesAreThere()
    {
        var runner = KeyboardRunner();
        Assert.Equal(["GM8.0 keyboard_check_direct lag fix patch"], Gm8xFix.Apply(runner));
        Assert.Equal([0xF6, 0xC4, 0x80], runner[0x1642e9..0x1642ec]);
    }

    [Fact]
    public void SkipsTheKindsItIsTold()
    {
        var runner = KeyboardRunner();
        var before = runner.ToArray();
        Assert.Empty(Gm8xFix.Apply(runner, ["keyboard"]));
        Assert.Equal(before, runner);
    }

    [Fact]
    public void KindsAreTheOnesTheCommandLineLists()
    {
        Assert.Equal(["memory", "joystick", "scheduler", "input-lag", "directplay", "keyboard"], Gm8xFix.Kinds);
    }
}
