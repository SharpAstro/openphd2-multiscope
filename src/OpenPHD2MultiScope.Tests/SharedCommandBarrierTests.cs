using Shouldly;
using Xunit;

namespace TianWen.OpenPHD2MultiScope.Tests;

public class SharedCommandBarrierTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ASingleClientFiresAtOnce()
    {
        var barrier = new SharedCommandBarrier<string>("dither", timeout: null);

        var decision = barrier.Request(1, "a", [1], T0);

        decision.Outcome.ShouldBe(BarrierOutcome.Fire);
        decision.Args.ShouldBe("a");
        decision.TimedOut.ShouldBeFalse();
        barrier.IsPending.ShouldBeFalse();
    }

    [Fact]
    public void TwoClientsFireOnlyOnTheSecondRequestWithTheFirstArguments()
    {
        var barrier = new SharedCommandBarrier<string>("dither", timeout: null);

        barrier.Request(1, "first", [1, 2], T0).Outcome.ShouldBe(BarrierOutcome.Wait);
        var decision = barrier.Request(2, "second", [1, 2], T0.AddSeconds(30));

        decision.Outcome.ShouldBe(BarrierOutcome.Fire);
        decision.Args.ShouldBe("first");
        decision.Requested.ShouldBe(2);
    }

    [Fact]
    public void AClientAskingTwiceCountsOnce()
    {
        var barrier = new SharedCommandBarrier<string>("dither", timeout: null);

        barrier.Request(1, "a", [1, 2], T0).Outcome.ShouldBe(BarrierOutcome.Wait);
        var again = barrier.Request(1, "a", [1, 2], T0.AddSeconds(1));

        again.Outcome.ShouldBe(BarrierOutcome.Duplicate);
        again.Requested.ShouldBe(1);
        again.Needed.ShouldBe(2);
        barrier.IsPending.ShouldBeTrue();
    }

    [Fact]
    public void ARoundResetsAfterFiring()
    {
        var barrier = new SharedCommandBarrier<string>("dither", timeout: null);
        barrier.Request(1, "a", [1, 2], T0);
        barrier.Request(2, "a", [1, 2], T0).Outcome.ShouldBe(BarrierOutcome.Fire);

        // The old proxy counted dithers forever and fired on the very first request of the next round.
        barrier.Request(1, "b", [1, 2], T0.AddMinutes(5)).Outcome.ShouldBe(BarrierOutcome.Wait);
    }

    [Fact]
    public void TheClientBeingWaitedForLeavingFiresTheRound()
    {
        var barrier = new SharedCommandBarrier<string>("dither", timeout: null);
        barrier.Request(1, "a", [1, 2], T0);

        var decision = barrier.Reevaluate([1], T0.AddSeconds(5));

        decision.ShouldNotBeNull();
        decision.Value.Outcome.ShouldBe(BarrierOutcome.Fire);
        decision.Value.Args.ShouldBe("a");
    }

    [Fact]
    public void TheOnlyRequesterLeavingVoidsTheRound()
    {
        var barrier = new SharedCommandBarrier<string>("dither", timeout: null);
        barrier.Request(1, "a", [1, 2], T0);

        barrier.Reevaluate([2], T0.AddSeconds(5)).ShouldBeNull();
        barrier.IsPending.ShouldBeFalse();
    }

    [Fact]
    public void ANewClientJoiningMidRoundIsWaitedFor()
    {
        var barrier = new SharedCommandBarrier<string>("dither", timeout: null);
        barrier.Request(1, "a", [1, 2], T0);

        barrier.Request(2, "a", [1, 2, 3], T0.AddSeconds(1)).Outcome.ShouldBe(BarrierOutcome.Wait);
        barrier.Request(3, "a", [1, 2, 3], T0.AddSeconds(2)).Outcome.ShouldBe(BarrierOutcome.Fire);
    }

    [Fact]
    public void WithATimeoutAnOverdueRoundFiresOnReevaluation()
    {
        var barrier = new SharedCommandBarrier<string>("dither", TimeSpan.FromSeconds(60));
        barrier.Request(1, "a", [1, 2], T0);

        barrier.Reevaluate([1, 2], T0.AddSeconds(59)).ShouldBeNull();
        var decision = barrier.Reevaluate([1, 2], T0.AddSeconds(60));

        decision.ShouldNotBeNull();
        decision.Value.Outcome.ShouldBe(BarrierOutcome.Fire);
        decision.Value.TimedOut.ShouldBeTrue();
        decision.Value.Requested.ShouldBe(1);
        decision.Value.Needed.ShouldBe(2);
    }

    [Fact]
    public void WithoutATimeoutARoundWaitsIndefinitely()
    {
        var barrier = new SharedCommandBarrier<string>("dither", timeout: null);
        barrier.Request(1, "a", [1, 2], T0);

        barrier.Reevaluate([1, 2], T0.AddDays(1)).ShouldBeNull();
        barrier.IsPending.ShouldBeTrue();
    }

    [Fact]
    public void ConcurrentRequestsFireExactlyOnce()
    {
        const int clients = 16;
        var connected = Enumerable.Range(1, clients).ToArray();
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var barrier = new SharedCommandBarrier<int>("dither", timeout: null);
            var fired = 0;
            Parallel.For(1, clients + 1, id =>
            {
                if (barrier.Request(id, id, connected, T0).Outcome is BarrierOutcome.Fire)
                {
                    Interlocked.Increment(ref fired);
                }
            });
            fired.ShouldBe(1);
        }
    }
}
