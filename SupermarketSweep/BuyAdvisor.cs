using SupermarketSweep.Models;

namespace SupermarketSweep;

/// <summary>A listing as the open marketboard shows it right now (read from game memory, not Universalis).</summary>
public record LiveListing(string Retainer, long UnitPrice, long Quantity, long Tax, bool Hq, bool Yours)
{
    /// <summary>What you actually pay for the whole listing, tax included.</summary>
    public long Cost => UnitPrice * Quantity + Tax;

    public double UnitCost => (double)Cost / Quantity;
}

public enum ListingVerdict
{
    /// <summary>Recommended: buy this one.</summary>
    Buy,

    /// <summary>Within the price cap, but not needed (cheaper ones already cover it).</summary>
    Fine,

    TooExpensive,
    WrongQuality,

    /// <summary>Bigger than what's still needed, and overbuying is off.</summary>
    TooBig,

    /// <summary>One of your own retainers.</summary>
    Yours,

    /// <summary>The route has no price for this item, so there's nothing to judge against.</summary>
    NoReference,
}

/// <summary>Highest acceptable price per unit (tax included) for each quality; null means that quality isn't wanted.</summary>
public record PriceCaps(double? Hq, double? Nq, bool FromThisWorld);

/// <param name="FromRoute">Per listing: recommended because it's exactly what the route planned.</param>
public record BuyAdvice(ListingVerdict[] Verdicts, bool[] FromRoute, long BuyQuantity, long BuyCost, long Short);

/// <summary>
/// Decides which live listings to buy for one item: the Universalis-based route is only a guide, the board is the truth.
/// A listing qualifies if its quality fits the item's rule, its unit price is within <c>maxOverPercent</c> of what the
/// route planned to pay, and (without overbuy) it isn't bigger than what's still needed. The route's own listings are
/// recommended first where they're still on the board; the rest is filled with the route planner's own rules, so the
/// board and the route agree on what "cheapest" means.
/// </summary>
public static class BuyAdvisor
{
    /// <summary>
    /// Price caps from the route: the dearest unit price it planned for this item on <paramref name="world"/>
    /// (or on any world, if none here), plus <paramref name="maxOverPercent"/>. Null if the route doesn't buy the item.
    /// </summary>
    public static PriceCaps? Caps(RoutePlan? plan, ShoppingListItem item, string? world, float maxOverPercent)
    {
        var planned = plan?.Stops.SelectMany(s => s.Purchases)
            .Where(p => p.Item.ItemId == item.ItemId)
            .Select(p => p.Listing)
            .ToList() ?? [];
        if (planned.Count == 0)
            return null;

        var here = planned.Where(l => l.WorldName == world).ToList();
        if (here.Count > 0)
            planned = here;

        var factor = 1 + Math.Max(0, maxOverPercent) / 100.0;
        double? Max(List<MarketDataListing> listings) =>
            listings.Count == 0 ? null : listings.Max(l => (double)l.Cost / l.Quantity) * factor;

        var hq = Max(planned.Where(l => l.Hq).ToList());
        var nq = Max(planned.Where(l => !l.Hq).ToList());
        return item.EffectiveQuality switch
        {
            QualityPreference.HqOnly => new PriceCaps(hq, null, here.Count > 0),
            // HQ at an NQ price is still an upgrade; NQ only if the route itself settled for NQ, so the board
            // never quietly swaps planned HQ for NQ.
            QualityPreference.PreferHq => new PriceCaps(Math.Max(hq ?? 0, nq ?? 0), nq, here.Count > 0),
            _ => new PriceCaps(Max(planned), Max(planned), here.Count > 0),
        };
    }

    /// <summary>What the route planned to buy for this item on <paramref name="world"/>.</summary>
    public static List<MarketDataListing> PlannedHere(RoutePlan? plan, ShoppingListItem item, string? world) =>
        plan?.Stops.Where(s => s.World == world)
            .SelectMany(s => s.Purchases)
            .Where(p => p.Item.ItemId == item.ItemId)
            .Select(p => p.Listing)
            .ToList() ?? [];

    /// <param name="plannedHere">The route's listings for this item on this world (<see cref="PlannedHere"/>).</param>
    public static BuyAdvice Advise(IReadOnlyList<LiveListing> live, QualityPreference quality, long stillNeeded,
        PriceCaps? caps, IReadOnlyList<MarketDataListing> plannedHere, bool allowOverbuy)
    {
        var verdicts = new ListingVerdict[live.Count];
        var index = new Dictionary<MarketDataListing, int>();
        for (var i = 0; i < live.Count; i++)
        {
            var listing = live[i];
            var cap = listing.Hq ? caps?.Hq : caps?.Nq;
            verdicts[i] = listing.Yours ? ListingVerdict.Yours
                : caps is null ? ListingVerdict.NoReference
                : quality == QualityPreference.HqOnly && !listing.Hq ? ListingVerdict.WrongQuality
                : cap is null ? ListingVerdict.WrongQuality
                : listing.UnitCost > cap.Value + 1e-6 ? ListingVerdict.TooExpensive
                : !allowOverbuy && stillNeeded > 0 && listing.Quantity > stillNeeded ? ListingVerdict.TooBig
                : ListingVerdict.Fine;

            if (verdicts[i] == ListingVerdict.Fine)
                index[AsMarketListing(listing)] = i;
        }

        // Two candidates: the route's own picks first (it weighed every item and world, and a plain re-fill can undo
        // that, e.g. a cheap extra HQ crowding out a planned NQ stack), and a plain cheapest fill (Universalis is
        // crowdsourced, so the board can have cheaper listings the route never saw). Keep the better one.
        var acceptable = index.Keys.ToList();
        var routeFirst = Select(acceptable, plannedHere, quality, stillNeeded, allowOverbuy);
        var cheapest = Select(acceptable, [], quality, stillNeeded, allowOverbuy);
        // Units left short have to be bought elsewhere; value them at the most we'd pay per unit here.
        var shortPenalty = Math.Max(caps?.Hq ?? 0, caps?.Nq ?? 0);
        var chosen = Better(cheapest, routeFirst, stillNeeded, quality, shortPenalty);

        // Mark which picks are exactly what the route planned, for the "route's pick" label.
        var fromRoute = new bool[live.Count];
        var unmatched = plannedHere.ToList();
        foreach (var taken in chosen)
        {
            verdicts[index[taken]] = ListingVerdict.Buy;
            var planned = unmatched.FindIndex(p => SameListing(p, taken));
            if (planned < 0)
                continue;
            fromRoute[index[taken]] = true;
            unmatched.RemoveAt(planned);
        }

        var quantity = chosen.Sum(l => l.Quantity);
        return new BuyAdvice(verdicts, fromRoute, quantity, chosen.Sum(l => l.Cost), Math.Max(0, stillNeeded - quantity));
    }

    private static bool SameListing(MarketDataListing a, MarketDataListing b) =>
        a.PricePerUnit == b.PricePerUnit && a.Quantity == b.Quantity && a.Hq == b.Hq;

    /// <summary>
    /// Fills <paramref name="need"/> from <paramref name="acceptable"/>: the <paramref name="planned"/> listings first
    /// where they're on the board, then the rest tier by tier like the planner. Without overbuy that's an exact fill:
    /// the planner's greedy pass can take a cheap small stack that leaves a bigger one no longer fitting, and here
    /// (one item, one world) the exact answer is cheap enough.
    /// </summary>
    private static List<MarketDataListing> Select(List<MarketDataListing> acceptable,
        IReadOnlyList<MarketDataListing> planned, QualityPreference quality, long need, bool allowOverbuy)
    {
        var pool = acceptable.ToList();
        var taken = new List<MarketDataListing>();
        long quantity = 0;
        foreach (var plannedListing in planned)
        {
            if (quantity >= need || (!allowOverbuy && plannedListing.Quantity > need - quantity))
                continue;
            var match = pool.FirstOrDefault(l => SameListing(l, plannedListing));
            if (match is null)
                continue;
            pool.Remove(match);
            taken.Add(match);
            quantity += match.Quantity;
        }

        var sorted = pool.OrderBy(l => (double)l.Cost / l.Quantity).ToList();
        List<List<MarketDataListing>> tiers = quality == QualityPreference.PreferHq
            ? [sorted.Where(l => l.Hq).ToList(), sorted.Where(l => !l.Hq).ToList()]
            : [sorted];
        foreach (var tier in tiers)
        {
            var remaining = need - quantity;
            if (remaining <= 0)
                break;
            var picks = allowOverbuy || remaining > MaxExactFill
                ? RoutePlanner.FillFromTier(tier, remaining, null, allowOverbuy)
                : FillExact(tier, (int)remaining);
            taken.AddRange(picks);
            quantity += picks.Sum(l => l.Quantity);
        }

        return taken;
    }

    /// <summary>
    /// The cheaper of two selections once each unit left short is charged at <paramref name="shortPenalty"/> (it has
    /// to be bought elsewhere). On a tie, more HQ wins for items with an HQ rule, then <paramref name="a"/>.
    /// </summary>
    private static List<MarketDataListing> Better(List<MarketDataListing> a, List<MarketDataListing> b, long need,
        QualityPreference quality, double shortPenalty)
    {
        double Score(List<MarketDataListing> picks) =>
            picks.Sum(l => l.Cost) + Math.Max(0, need - picks.Sum(l => l.Quantity)) * shortPenalty;
        long Hq(List<MarketDataListing> picks) => picks.Where(l => l.Hq).Sum(l => l.Quantity);

        var (aScore, bScore) = (Score(a), Score(b));
        if (Math.Abs(aScore - bScore) > 1e-6)
            return aScore < bScore ? a : b;
        if (quality != QualityPreference.Any && Hq(a) != Hq(b))
            return Hq(a) > Hq(b) ? a : b;
        return a;
    }

    /// <summary>Above this many still needed, fall back to the greedy fill (the exact one's table grows with it).</summary>
    private const int MaxExactFill = 20000;

    /// <summary>
    /// The whole listings, none bigger than needed, that cover as much of <paramref name="need"/> as possible, and
    /// of those the cheapest combination (0/1 knapsack over quantity).
    /// </summary>
    private static List<MarketDataListing> FillExact(List<MarketDataListing> tier, int need)
    {
        // cheapest[q] = lowest cost to buy exactly q; took[i, q] = listing i was part of the best way to reach q
        // when it was considered. Walking the listings backwards then recovers the combination.
        var cheapest = new long[need + 1];
        Array.Fill(cheapest, long.MaxValue);
        cheapest[0] = 0;
        var took = new bool[tier.Count, need + 1];
        for (var i = 0; i < tier.Count; i++)
        {
            var size = (int)Math.Min(tier[i].Quantity, need + 1L);
            for (var q = need; q >= size; q--)
            {
                if (cheapest[q - size] == long.MaxValue || cheapest[q - size] + tier[i].Cost >= cheapest[q])
                    continue;
                cheapest[q] = cheapest[q - size] + tier[i].Cost;
                took[i, q] = true;
            }
        }

        var reached = need;
        while (cheapest[reached] == long.MaxValue)
            reached--;

        var picks = new List<MarketDataListing>();
        for (var i = tier.Count - 1; i >= 0 && reached > 0; i--)
        {
            if (!took[i, reached])
                continue;
            picks.Add(tier[i]);
            reached -= (int)tier[i].Quantity;
        }

        return picks;
    }

    // Same shape the planner works on, so its fill rules can be reused as-is.
    private static MarketDataListing AsMarketListing(LiveListing listing) => new()
    {
        PricePerUnit = listing.UnitPrice,
        Quantity = listing.Quantity,
        Total = listing.UnitPrice * listing.Quantity,
        Tax = listing.Tax,
        Hq = listing.Hq,
        RetainerName = listing.Retainer,
    };
}
