using Gm8Builder.Exe;

namespace Gm8Builder.Tests;

public class CipherTests
{
    [Fact]
    public void SwapCipherRoundTrips()
    {
        var rng = new Random(1);
        var table = SwapCipher.RandomTable(rng);
        foreach (var n in new[] { 0, 1, 2, 3, 255, 256, 257, 10_000 })
        {
            var data = new byte[n];
            rng.NextBytes(data);
            Assert.Equal(data, SwapCipher.Decrypt(SwapCipher.Encrypt(data, table), table));
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(249u)]
    [InlineData(250u)]
    [InlineData(123456789u)]
    [InlineData(0x80000000u)]
    [InlineData(uint.MaxValue)]
    public void ExtensionCipherRoundTrips(uint seed)
    {
        var data = new byte[1000];
        new Random((int)seed).NextBytes(data);
        var encrypted = ExtensionCipher.Encrypt(data, seed);
        Assert.Equal(data[0], encrypted[0]);
        Assert.Equal(data, ExtensionCipher.Decrypt(encrypted, seed));
    }
}
