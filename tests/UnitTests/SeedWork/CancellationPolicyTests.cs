using SeedWork.ValueObjects;
namespace UnitTests.SeedWork;

public class CancellationPolicyTests
{
    [Fact]
    public void Create_ShouldSucceed_ForFullyFlexibleSingleTier()
    {
        CancellationPolicy policy = CancellationPolicy.Create([new CancellationTier(0, 100m)]);

        Assert.Equal(100m, policy.ResolveRefundPercent(0));
        Assert.Equal(100m, policy.ResolveRefundPercent(30));
    }

    [Fact]
    public void Create_ShouldSucceed_ForNonRefundableSingleTier()
    {
        CancellationPolicy policy = CancellationPolicy.Create([new CancellationTier(0, 0m)]);

        Assert.Equal(0m, policy.ResolveRefundPercent(0));
        Assert.Equal(0m, policy.ResolveRefundPercent(30));
    }

    [Fact]
    public void Create_ShouldThrow_WhenTiersIsEmpty()
    {
        Assert.ThrowsAny<ArgumentException>(() => CancellationPolicy.Create([]));
    }

    [Fact]
    public void Create_ShouldThrow_WhenNoZeroFloorTierExists()
    {
        Assert.Throws<ArgumentException>(() => CancellationPolicy.Create([new CancellationTier(5, 100m)]));
    }

    [Fact]
    public void Create_ShouldThrow_WhenMultipleZeroFloorTiersExist()
    {
        Assert.Throws<ArgumentException>(() => CancellationPolicy.Create([
            new CancellationTier(0, 100m),
            new CancellationTier(0, 50m)
        ]));
    }

    [Fact]
    public void Create_ShouldThrow_WhenThresholdsAreNotDistinct()
    {
        Assert.Throws<ArgumentException>(() => CancellationPolicy.Create([
            new CancellationTier(5, 100m),
            new CancellationTier(5, 50m),
            new CancellationTier(0, 0m)
        ]));
    }

    [Fact]
    public void Create_ShouldThrow_WhenAThresholdIsNegative()
    {
        Assert.ThrowsAny<ArgumentException>(() => CancellationPolicy.Create([
            new CancellationTier(-1, 100m),
            new CancellationTier(0, 0m)
        ]));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(100.01)]
    public void Create_ShouldThrow_WhenARefundPercentIsOutOfRange(decimal refundPercent)
    {
        Assert.ThrowsAny<ArgumentException>(() => CancellationPolicy.Create([
            new CancellationTier(0, refundPercent)
        ]));
    }

    [Fact]
    public void Create_ShouldThrow_WhenPercentIncreasesCloserToCheckIn()
    {
        // 50% at 5+ days out, but 100% inside that window - refunding more
        // the closer you get to check-in makes no sense as a policy.
        Assert.Throws<ArgumentException>(() => CancellationPolicy.Create([
            new CancellationTier(5, 50m),
            new CancellationTier(0, 100m)
        ]));
    }

    [Fact]
    public void ResolveRefundPercent_ShouldMatchTheModerateDefaultAtEachBoundary()
    {
        // (5, 100), (1, 50), (0, 0)
        CancellationPolicy policy = CancellationPolicy.CreateDefault();

        Assert.Equal(0m, policy.ResolveRefundPercent(0));
        Assert.Equal(50m, policy.ResolveRefundPercent(1));
        Assert.Equal(50m, policy.ResolveRefundPercent(4));
        Assert.Equal(100m, policy.ResolveRefundPercent(5));
        Assert.Equal(100m, policy.ResolveRefundPercent(60));
    }

    [Fact]
    public void ResolveRefundPercent_ShouldThrow_WhenDaysBeforeCheckInIsNegative()
    {
        CancellationPolicy policy = CancellationPolicy.CreateDefault();

        Assert.ThrowsAny<ArgumentException>(() => policy.ResolveRefundPercent(-1));
    }

    [Fact]
    public void Restore_ShouldResolveCorrectly_RegardlessOfTierOrder()
    {
        // Deliberately not pre-sorted, unlike what Create always produces -
        // Restore represents whatever order the jsonb column happened to
        // deserialize in.
        CancellationPolicy policy = CancellationPolicy.Restore([
            new CancellationTier(0, 0m),
            new CancellationTier(5, 100m),
            new CancellationTier(1, 50m)
        ]);

        Assert.Equal(100m, policy.ResolveRefundPercent(10));
        Assert.Equal(50m, policy.ResolveRefundPercent(2));
        Assert.Equal(0m, policy.ResolveRefundPercent(0));
    }

    [Fact]
    public void Equals_ShouldReturnTrue_ForTheSameTiersInTheSameOrder()
    {
        CancellationPolicy a = CancellationPolicy.CreateDefault();
        CancellationPolicy b = CancellationPolicy.CreateDefault();

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equals_ShouldReturnFalse_ForDifferentTiers()
    {
        CancellationPolicy a = CancellationPolicy.Create([new CancellationTier(0, 100m)]);
        CancellationPolicy b = CancellationPolicy.Create([new CancellationTier(0, 0m)]);

        Assert.NotEqual(a, b);
    }
}
