namespace TianWen.OpenPHD2MultiScope;

/// <summary>
/// Decides when a command that acts on the ONE shared guider may run. Several imaging programs
/// share PHD2 through this proxy, so a dither (or a stop) asked for by one of them would disturb
/// the exposures of all the others. A round starts with the first request and FIRES once every
/// connected client has asked, or once <see cref="Timeout"/> has passed since the round started.
/// </summary>
/// <remarks>
/// Pure and clock-free: the caller passes the connected client ids and the time, so every
/// interleaving can be tested. A client asking twice in one round counts once; a client that
/// disconnects stops being waited for (<see cref="Reevaluate"/>), as does one that never asks
/// before the timeout. The first request's arguments are the ones used when the round fires.
/// </remarks>
internal sealed class SharedCommandBarrier<TArgs>(string command, TimeSpan? timeout)
{
    // A lock rather than a CAS swap: a round is a set plus a start time that must change
    // together, it is touched only from network and timer threads (never a render thread), and
    // the critical section is a handful of set operations.
    private readonly Lock _lock = new();
    private Round? _round;

    public string Command { get; } = command;

    /// <summary>How long a round waits for the remaining clients; <see langword="null"/> waits for all of them.</summary>
    public TimeSpan? Timeout { get; } = timeout;

    public BarrierDecision<TArgs> Request(int clientId, TArgs args, IReadOnlyCollection<int> connectedClients, DateTimeOffset now)
    {
        lock (_lock)
        {
            var round = _round ??= new Round(args, now);
            var duplicate = !round.Requested.Add(clientId);

            return Decide(round, connectedClients, now) ?? (duplicate
                ? BarrierDecision<TArgs>.Duplicate(round.Requested.Count, Needed(round, connectedClients))
                : BarrierDecision<TArgs>.Wait(round.Requested.Count, Needed(round, connectedClients)));
        }
    }

    /// <summary>
    /// Called when a client leaves and on a timer: the round may now be complete (the client it
    /// waited for is gone) or overdue. Returns a firing decision, or <see langword="null"/> if
    /// there is nothing to do.
    /// </summary>
    public BarrierDecision<TArgs>? Reevaluate(IReadOnlyCollection<int> connectedClients, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_round is not { } round)
            {
                return null;
            }

            // Forget requests from clients that are gone; a round nobody connected asked for is void.
            round.Requested.IntersectWith(connectedClients);
            if (round.Requested.Count == 0)
            {
                _round = null;
                return null;
            }

            return Decide(round, connectedClients, now);
        }
    }

    public bool IsPending
    {
        get
        {
            lock (_lock)
            {
                return _round is not null;
            }
        }
    }

    private BarrierDecision<TArgs>? Decide(Round round, IReadOnlyCollection<int> connectedClients, DateTimeOffset now)
    {
        var allAsked = connectedClients.All(round.Requested.Contains);
        var overdue = Timeout is { } limit && now - round.StartedAt >= limit;
        if (!allAsked && !overdue)
        {
            return null;
        }

        _round = null;
        return BarrierDecision<TArgs>.Fire(round.Args, round.Requested.Count, connectedClients.Count, timedOut: !allAsked);
    }

    private static int Needed(Round round, IReadOnlyCollection<int> connectedClients)
        => Math.Max(connectedClients.Count, round.Requested.Count);

    private sealed class Round(TArgs args, DateTimeOffset startedAt)
    {
        public TArgs Args { get; } = args;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public HashSet<int> Requested { get; } = [];
    }
}

internal enum BarrierOutcome
{
    /// <summary>The round is complete: run the command now, with <see cref="BarrierDecision{TArgs}.Args"/>.</summary>
    Fire,
    /// <summary>Recorded; other clients have not asked yet.</summary>
    Wait,
    /// <summary>This client already asked in the current round.</summary>
    Duplicate,
}

internal readonly record struct BarrierDecision<TArgs>(BarrierOutcome Outcome, TArgs? Args, int Requested, int Needed, bool TimedOut)
{
    public static BarrierDecision<TArgs> Fire(TArgs args, int requested, int needed, bool timedOut) => new(BarrierOutcome.Fire, args, requested, needed, timedOut);
    public static BarrierDecision<TArgs> Wait(int requested, int needed) => new(BarrierOutcome.Wait, default, requested, needed, false);
    public static BarrierDecision<TArgs> Duplicate(int requested, int needed) => new(BarrierOutcome.Duplicate, default, requested, needed, false);
}
