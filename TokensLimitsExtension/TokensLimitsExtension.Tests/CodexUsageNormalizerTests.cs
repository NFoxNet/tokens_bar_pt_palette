using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.Tests;

public sealed class CodexUsageNormalizerTests
{
    [Theory]
    [InlineData(0, "100% осталось")]
    [InlineData(100, "0% осталось")]
    [InlineData(-10, "100% осталось")]
    [InlineData(110, "0% осталось")]
    [InlineData(37.5, "63% осталось")]
    public void FormatRemainingPercentClampsAndRounds(double used, string expected)
    {
        Assert.Equal(expected, CodexUsageNormalizer.FormatRemainingPercent(used));
    }

    [Fact]
    public void FormatTimeUntilResetFormatsHoursAndMinutes()
    {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("через 1ч 40м", CodexUsageNormalizer.FormatTimeUntilReset(now.AddMinutes(100), now));
    }

    [Fact]
    public void FormatTimeUntilResetFormatsDaysAndHours()
    {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("через 3д 4ч", CodexUsageNormalizer.FormatTimeUntilReset(now.AddDays(3).AddHours(4), now));
        Assert.Equal("через 8д 0ч", CodexUsageNormalizer.FormatTimeUntilReset(now.AddDays(8), now));
    }

    [Fact]
    public void FormatTimeUntilResetReportsPastReset()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Equal("сброс уже прошёл", CodexUsageNormalizer.FormatTimeUntilReset(now.AddSeconds(-1), now));
    }
}
