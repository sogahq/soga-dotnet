using Soga.Realtime;

namespace Soga.Tests.Realtime;

public sealed class SogaBackoffTests
{
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Max = TimeSpan.FromMinutes(5);

    [Fact]
    public void Compute_GrowsExponentiallyWithoutJitter()
    {
        // A sample of 0.5 is the exact midpoint of the jitter range, so the factor is 1.0.
        Assert.Equal(TimeSpan.FromSeconds(2), SogaBackoff.Compute(1, Base, Max, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(4), SogaBackoff.Compute(2, Base, Max, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(8), SogaBackoff.Compute(3, Base, Max, 0.5));
    }

    [Fact]
    public void Compute_NeverExceedsMaximum()
    {
        Assert.Equal(Max, SogaBackoff.Compute(40, Base, Max, 0.999));
        Assert.True(SogaBackoff.Compute(12, Base, Max, 0.999) <= Max);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.999)]
    public void Compute_KeepsJitterWithinTwentyPercent(double sample)
    {
        var delay = SogaBackoff.Compute(3, Base, Max, sample);

        Assert.InRange(delay.TotalSeconds, 8 * 0.8, 8 * 1.2);
    }
}
