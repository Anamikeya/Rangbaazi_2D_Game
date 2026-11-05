using Rangbaazi.Sim.FixedMath;

namespace Rangbaazi.Sim.Tests;

public class FixTests
{
    [Fact]
    public void IntegerRoundTrip()
    {
        Assert.Equal(7, Fix.FromInt(7).FloorToInt());
        Assert.Equal(-3, Fix.FromInt(-3).FloorToInt());
    }

    [Fact]
    public void FloorRoundsTowardNegativeInfinity()
    {
        Assert.Equal(-1, Fix.FromRatio(-1, 2).FloorToInt());
        Assert.Equal(0, Fix.FromRatio(1, 2).FloorToInt());
    }

    [Fact]
    public void MultiplyAndDivide()
    {
        Fix a = Fix.FromRatio(3, 2);
        Fix b = Fix.FromInt(4);
        Assert.Equal(Fix.FromInt(6), a * b);
        Assert.Equal(Fix.FromRatio(3, 8), a / b);
        Assert.Equal(Fix.FromInt(-6), -a * b);
    }

    [Fact]
    public void LargeValuesDoNotOverflowIntermediate()
    {
        Fix big = Fix.FromInt(1_000_000);
        Assert.Equal(Fix.FromInt(1_000_000), big * Fix.One);
        Assert.Equal(Fix.FromInt(1_000), big / Fix.FromInt(1_000));
    }

    [Fact]
    public void SqrtIsExactForPerfectSquares()
    {
        Assert.Equal(Fix.FromInt(2), Fix.Sqrt(Fix.FromInt(4)));
        Assert.Equal(Fix.FromInt(12), Fix.Sqrt(Fix.FromInt(144)));
        Assert.Equal(Fix.Zero, Fix.Sqrt(Fix.Zero));
    }

    [Fact]
    public void SqrtOfTwoIsWithinOneUlp()
    {
        long raw = Fix.Sqrt(Fix.FromInt(2)).Raw;
        Assert.InRange(raw, 92681, 92682); // sqrt(2) * 65536 = 92681.9
    }

    [Fact]
    public void MoveTowardClampsAtTarget()
    {
        Assert.Equal(Fix.One, Fix.MoveToward(Fix.Zero, Fix.One, Fix.FromInt(5)));
        Assert.Equal(Fix.FromRatio(1, 4), Fix.MoveToward(Fix.Zero, Fix.One, Fix.FromRatio(1, 4)));
        Assert.Equal(-Fix.One, Fix.MoveToward(Fix.Zero, -Fix.One, Fix.FromInt(5)));
    }

    [Fact]
    public void ToStringUsesIntegerFormatting()
    {
        Assert.Equal("1.5000", Fix.FromRatio(3, 2).ToString());
        Assert.Equal("-0.2500", Fix.FromRatio(-1, 4).ToString());
    }
}
