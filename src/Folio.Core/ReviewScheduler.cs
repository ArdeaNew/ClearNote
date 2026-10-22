namespace Folio.Core;

/// <summary>
/// FSRS-6 memory equations with fixed public defaults; Folio adds its own
/// three-button learning steps and due-first random selection (see ALGORITHM.md).
/// </summary>
public static class ReviewScheduler
{
    public const double DesiredRetention = .9;
    public const string AlgorithmVersion = "FSRS-6 defaults + Folio learning policy v1";
    private const double MinStability = .001;
    private const double MaxStability = 36500;
    private static readonly double[] W = [.212, 1.2931, 2.3065, 8.2956, 6.4133,
        .8334, 3.0194, .001, 1.8722, .1666, .796, 1.4835, .0614,
        .2629, 1.6483, .6014, 1.8729, .5425, .0912, .0658, .1542];
    private static readonly double Factor = Math.Pow(.9, -1 / W[20]) - 1;

    /// <summary>Records one sufficiently visible browse, using the existing Good equations once.
    /// The caller owns visibility thresholds and duplicate suppression within a session.</summary>
    public static void RecordBrowse(CardDocument card, DateTimeOffset now)
    {
        Apply(card, ReviewRating.Good, now);
        card.Reviews[^1].Automatic = true;
    }

    public static void Apply(CardDocument card, ReviewRating rating, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (card.Collection != NoteCollection.Notes)
            throw new InvalidOperationException("Only Notes participate in spaced repetition.");
        if (!Enum.IsDefined(rating)) throw new ArgumentOutOfRangeException(nameof(rating));
        var memory = card.Memory;
        if (memory.LastReviewAt > now)
            throw new ArgumentOutOfRangeException(nameof(now), "This review is earlier than the previous one. Check your system clock before continuing.");

        var elapsed = memory.LastReviewAt is { } last ? (now - last).TotalDays : 0;
        var recall = RecallProbability(card, now);
        var grade = (int)rating;
        double stability;
        double difficulty;
        if (memory.Repetitions == 0 || memory.LastReviewAt is null || memory.Stability <= 0)
        {
            stability = W[grade - 1];
            difficulty = InitialDifficulty(grade);
        }
        else
        {
            var oldStability = Math.Clamp(memory.Stability, MinStability, MaxStability);
            var oldDifficulty = Math.Clamp(memory.Difficulty, 1, 10);
            difficulty = NextDifficulty(oldDifficulty, grade);
            if (elapsed < 1)
            {
                var increase = Math.Exp(W[17] * (grade - 3 + W[18])) * Math.Pow(oldStability, -W[19]);
                if (grade >= 2) increase = Math.Max(1, increase);
                stability = oldStability * increase;
            }
            else if (rating == ReviewRating.Again)
            {
                var forgotten = W[11] * Math.Pow(oldDifficulty, -W[12]) *
                    (Math.Pow(oldStability + 1, W[13]) - 1) * Math.Exp(W[14] * (1 - recall));
                // Official implementations cap post-lapse S below previous S.
                stability = Math.Min(forgotten, oldStability / Math.Exp(W[17] * W[18]));
            }
            else
            {
                var hardPenalty = rating == ReviewRating.Hard ? W[15] : 1;
                stability = oldStability * (1 + Math.Exp(W[8]) * (11 - oldDifficulty) *
                    Math.Pow(oldStability, -W[9]) * (Math.Exp(W[10] * (1 - recall)) - 1) * hardPenalty);
            }
        }

        memory.Stability = Math.Clamp(stability, MinStability, MaxStability);
        memory.Difficulty = Math.Clamp(difficulty, 1, 10);
        memory.LastReviewAt = now;
        memory.Repetitions++;
        if (rating == ReviewRating.Again) memory.Lapses++;
        // Learning policy is intentionally separate from the memory equations.
        var interval = rating == ReviewRating.Again ? TimeSpan.FromMinutes(10) :
            TimeSpan.FromDays(Math.Clamp(Interval(memory.Stability), 1d / 24, MaxStability));
        memory.DueAt = now.Add(interval);
        card.UpdatedAt = now;
        card.Reviews.Add(new ReviewLog
        {
            ReviewedAt = now,
            Rating = rating,
            ElapsedDays = elapsed,
            RecallBefore = recall,
            StabilityAfter = memory.Stability,
            DueAt = memory.DueAt
        });
    }

    public static double RecallProbability(CardDocument card, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(card);
        var state = card.Memory;
        if (state.LastReviewAt is not { } last || state.Stability <= 0) return 0;
        var elapsed = Math.Max(0, (now - last).TotalDays);
        return Math.Clamp(Math.Pow(1 + Factor * elapsed / Math.Max(MinStability, state.Stability), -W[20]), 0, 1);
    }

    public static CardDocument? Pick(IEnumerable<CardDocument> cards, ISet<string> excluded,
        DateTimeOffset now, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(excluded);
        random ??= Random.Shared;
        var available = cards.Where(c => c.Collection == NoteCollection.Notes && !excluded.Contains(c.Id)).ToList();
        if (available.Count == 0) return null;
        var due = available.Where(c => c.Memory.DueAt <= now).ToList();
        var pool = due.Count > 0 ? due : available;
        var weighted = pool.Select(c => (Card: c, Weight: Weight(c, now))).ToList();
        var threshold = random.NextDouble() * weighted.Sum(x => x.Weight);
        foreach (var entry in weighted)
        {
            threshold -= entry.Weight;
            if (threshold <= 0) return entry.Card;
        }
        return weighted[^1].Card;
    }

    private static double Weight(CardDocument card, DateTimeOffset now)
    {
        var forgetting = 1 - RecallProbability(card, now);
        var overdue = Math.Max(0, (now - card.Memory.DueAt).TotalDays);
        var normalizedOverdue = Math.Min(10, overdue / Math.Max(1, card.Memory.Stability));
        return 1 + 4 * forgetting + normalizedOverdue;
    }

    private static double InitialDifficulty(int grade) => Math.Clamp(W[4] - Math.Exp(W[5] * (grade - 1)) + 1, 1, 10);

    private static double NextDifficulty(double difficulty, int grade)
    {
        var delta = -W[6] * (grade - 3);
        var damped = difficulty + delta * (10 - difficulty) / 9;
        return W[7] * InitialDifficulty(4) + (1 - W[7]) * damped;
    }

    private static double Interval(double stability) => stability / Factor * (Math.Pow(DesiredRetention, -1 / W[20]) - 1);
}
