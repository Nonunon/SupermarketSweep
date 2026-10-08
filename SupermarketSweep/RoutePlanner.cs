using ECommons.ExcelServices;
using SupermarketSweep.Models;

namespace SupermarketSweep;

/// <summary>One listing to buy on a stop.</summary>
public record Purchase(ShoppingListItem Item, MarketDataListing Listing);

/// <summary>One world to visit and what to buy there.</summary>
public class WorldStop(string world, string dataCenter)
{
    public string World { get; } = world;
    public string DataCenter { get; } = dataCenter;
    public List<Purchase> Purchases { get; } = [];
    public long Subtotal => Purchases.Sum(p => p.Listing.Cost);
}

public class RoutePlan
{
    public List<WorldStop> Stops { get; init; } = [];
    public long Total => Stops.Sum(s => s.Subtotal);

    /// <summary>Cost of the cheapest plan with no limit on worlds, for comparison.</summary>
    public long CheapestTotal { get; init; }

    public int CheapestWorldCount { get; init; }

    /// <summary>Items the route can't fully cover (not enough listings, or exact amounts impossible without overbuying).</summary>
    public List<(ShoppingListItem Item, long Missing)> Unfilled { get; init; } = [];

    /// <summary>Items that still need buying but have no price data (or data for other settings).</summary>
    public List<ShoppingListItem> NeedsPrices { get; init; } = [];

    public bool IsEmpty => Stops.Count == 0;
}

/// <summary>
/// Plans which worlds to visit to buy everything still needed.
///
/// 1. Find the cheapest way to buy each item using every world (greedy by price per unit, whole stacks only).
/// 2. Then repeatedly try dropping one world: re-plan without it, and keep the drop if every item is still
///    covered as well as before and the total stays within <c>maxExtraPercent</c> of the cheapest total.
///    Of the valid drops each round, the one that leaves the lowest total wins. Stops when nothing can be dropped.
///
/// Without overbuy, stacks bigger than what's still needed are skipped, so an item can end up partly covered.
/// With overbuy, the cheapest single stack that finishes the item may be bought even if it's more than needed.
///
/// HQ follows each item's <see cref="ShoppingListItem.EffectiveQuality"/>: listings are split into tiers (HQ, then NQ
/// for Prefer HQ; HQ alone for HQ only; one mixed tier for Any) and filled tier by tier. Dropping a world must not
/// lower how much HQ an item gets, so "fewer trips" never quietly swaps HQ for NQ.
/// </summary>
public static class RoutePlanner
{
    /// <param name="Tiers">Listing groups to buy from in order, each sorted by price per unit.</param>
    private record Need(ShoppingListItem Item, long Quantity, List<List<MarketDataListing>> Tiers)
    {
        public IEnumerable<MarketDataListing> AllListings => Tiers.SelectMany(t => t);
    }

    private record Fill(Dictionary<ShoppingListItem, List<MarketDataListing>> Bought, long Total)
    {
        public long Covered(Need need) => Bought.TryGetValue(need.Item, out var listings)
            ? Math.Min(need.Quantity, listings.Sum(l => l.Quantity))
            : 0;

        public long HqCovered(Need need) => Bought.TryGetValue(need.Item, out var listings)
            ? Math.Min(need.Quantity, listings.Where(l => l.Hq).Sum(l => l.Quantity))
            : 0;

        public HashSet<string> Worlds => Bought.Values.SelectMany(l => l).Select(l => l.WorldName).ToHashSet();
    }

    /// <param name="wanted">Each item with how many are still needed. Computed by the caller on the framework
    /// thread, since owned counts come from Allagan Tools IPC; the planning itself is safe to run off-thread.</param>
    /// <param name="currentWorld">Where the player is now (null if unknown), used to order the stops. Also read by the
    /// caller on the framework thread: Dalamud only allows touching the local player there.</param>
    public static RoutePlan Plan(IReadOnlyList<(ShoppingListItem Item, long StillNeeded)> wanted,
        float maxExtraPercent, bool allowOverbuy, (string World, string DataCenter)? currentWorld = null)
    {
        var buying = wanted.Where(w => w.Item.IsMarketable && w.StillNeeded > 0).ToList();
        var needsPrices = buying.Where(w => w.Item.MarketDataResponse is null || !w.Item.PricesMatchCurrentScope)
            .Select(w => w.Item).ToList();
        var needs = buying.Where(w => !needsPrices.Contains(w.Item))
            .Select(w => new Need(w.Item, w.StillNeeded, BuildTiers(w.Item)))
            .ToList();

        var cheapest = FillAll(needs, null, allowOverbuy);
        var budget = cheapest.Total * (1 + Math.Max(0, maxExtraPercent) / 100.0);

        // Ban one used world per round; its purchases can move to ANY world still allowed, including ones the
        // cheapest plan never touched. Keep the ban if it doesn't add worlds, covers as much, and fits the budget.
        // The allowed set only shrinks, so this always ends.
        var allowedWorlds = needs.SelectMany(n => n.AllListings).Select(l => l.WorldName).ToHashSet();
        var current = cheapest;
        var bestSeen = cheapest;
        while (true)
        {
            (Fill Fill, string Banned)? best = null;
            foreach (var world in current.Worlds)
            {
                var allowed = allowedWorlds.Where(w => w != world).ToHashSet();
                var trial = FillAll(needs, allowed, allowOverbuy);
                if (trial.Total > budget
                    || trial.Worlds.Count > current.Worlds.Count
                    || needs.Any(n => trial.Covered(n) < cheapest.Covered(n)
                                      || trial.HqCovered(n) < cheapest.HqCovered(n)))
                    continue;
                if (best is null
                    || trial.Worlds.Count < best.Value.Fill.Worlds.Count
                    || (trial.Worlds.Count == best.Value.Fill.Worlds.Count && trial.Total < best.Value.Fill.Total))
                    best = (trial, world);
            }

            if (best is null)
                break;
            allowedWorlds.Remove(best.Value.Banned);
            current = best.Value.Fill;
            if (current.Worlds.Count < bestSeen.Worlds.Count
                || (current.Worlds.Count == bestSeen.Worlds.Count && current.Total < bestSeen.Total))
                bestSeen = current;
        }

        current = bestSeen;

        return new RoutePlan
        {
            Stops = BuildStops(current, currentWorld),
            CheapestTotal = cheapest.Total,
            CheapestWorldCount = cheapest.Worlds.Count,
            Unfilled = needs.Where(n => current.Covered(n) < n.Quantity)
                .Select(n => (n.Item, n.Quantity - current.Covered(n)))
                .ToList(),
            NeedsPrices = needsPrices,
        };
    }

    private static Fill FillAll(List<Need> needs, HashSet<string>? allowedWorlds, bool allowOverbuy)
    {
        var bought = new Dictionary<ShoppingListItem, List<MarketDataListing>>();
        long total = 0;
        foreach (var need in needs)
        {
            var taken = FillOne(need, allowedWorlds, allowOverbuy);
            if (taken.Count == 0)
                continue;
            bought[need.Item] = taken;
            total += taken.Sum(l => l.Cost);
        }

        return new Fill(bought, total);
    }

    private static List<List<MarketDataListing>> BuildTiers(ShoppingListItem item)
    {
        var usable = item.MarketDataResponse!.Listings
            .Where(l => l.Quantity > 0 && !string.IsNullOrEmpty(l.WorldName))
            .OrderBy(l => (double)l.Cost / l.Quantity)
            .ToList();
        return item.EffectiveQuality switch
        {
            QualityPreference.HqOnly => [usable.Where(l => l.Hq).ToList()],
            QualityPreference.PreferHq => [usable.Where(l => l.Hq).ToList(), usable.Where(l => !l.Hq).ToList()],
            _ => [usable],
        };
    }

    // Fills from the first tier, then whatever's left from the next, and so on.
    private static List<MarketDataListing> FillOne(Need need, HashSet<string>? allowedWorlds, bool allowOverbuy)
    {
        var taken = new List<MarketDataListing>();
        var remaining = need.Quantity;
        foreach (var tier in need.Tiers)
        {
            if (remaining <= 0)
                break;
            var fromTier = FillFromTier(tier, remaining, allowedWorlds, allowOverbuy);
            taken.AddRange(fromTier);
            remaining -= fromTier.Sum(l => l.Quantity);
        }

        return taken;
    }

    internal static List<MarketDataListing> FillFromTier(List<MarketDataListing> byUnitPrice, long quantity,
        HashSet<string>? allowedWorlds, bool allowOverbuy)
    {
        var taken = new List<MarketDataListing>();
        var remaining = quantity;
        MarketDataListing? finisher = null;

        foreach (var listing in byUnitPrice)
        {
            if (remaining <= 0)
                break;
            if (allowedWorlds is not null && !allowedWorlds.Contains(listing.WorldName))
                continue;

            if (listing.Quantity <= remaining)
            {
                taken.Add(listing);
                remaining -= listing.Quantity;
            }
            else if (allowOverbuy && (finisher is null || listing.Cost < finisher.Cost))
            {
                // Too big for what's left; remember the cheapest one in case nothing smaller finishes the job.
                finisher = listing;
            }
        }

        if (remaining > 0 && finisher is not null)
            taken.Add(finisher);

        if (!allowOverbuy)
            return taken;

        // A single stack covering the whole need can beat a pile of small ones plus a finisher.
        var oneStack = byUnitPrice
            .Where(l => l.Quantity >= quantity && (allowedWorlds is null || allowedWorlds.Contains(l.WorldName)))
            .MinBy(l => l.Cost);
        var takenCovers = taken.Sum(l => l.Quantity) >= quantity;
        if (oneStack is not null && (!takenCovers || oneStack.Cost < taken.Sum(l => l.Cost)))
            return [oneStack];
        return taken;
    }

    // Current world first, then the rest of its data center, then other data centers; biggest spends first within each.
    private static List<WorldStop> BuildStops(Fill fill, (string World, string DataCenter)? here)
    {
        var currentWorld = here?.World;
        var currentDc = here?.DataCenter;

        var stops = new Dictionary<string, WorldStop>();
        foreach (var (item, listings) in fill.Bought)
        {
            foreach (var listing in listings)
            {
                if (!stops.TryGetValue(listing.WorldName, out var stop))
                {
                    var dc = ExcelWorldHelper.Get(listing.WorldName)?.DataCenter.ValueNullable?.Name.ToString() ?? "?";
                    stops[listing.WorldName] = stop = new WorldStop(listing.WorldName, dc);
                }

                stop.Purchases.Add(new Purchase(item, listing));
            }
        }

        return stops.Values
            .OrderBy(s => s.World == currentWorld ? 0 : s.DataCenter == currentDc ? 1 : 2)
            .ThenBy(s => s.DataCenter)
            .ThenByDescending(s => s.Subtotal)
            .ToList();
    }
}
