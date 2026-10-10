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

/// <summary>
/// How far past what's needed a purchase may go. <see cref="Unlimited"/> allows any stack; otherwise the excess may be
/// up to <see cref="MaxPercent"/> of the need or up to <see cref="MaxUnits"/>, whichever allows more.
/// </summary>
public readonly record struct OverbuyRule(bool Unlimited, float MaxPercent, long MaxUnits)
{
    public static OverbuyRule FromConfig(Config config) => config.RouteOverbuyMode switch
    {
        OverbuyMode.Off => new(false, 0, 0),
        OverbuyMode.Unlimited => new(true, 0, 0),
        _ => new(false, config.RouteOverbuyMaxPercent, config.RouteOverbuyMaxUnits),
    };

    /// <summary>The most units past <paramref name="need"/> this rule accepts.</summary>
    public long MaxExcess(long need) => Unlimited
        ? long.MaxValue
        : Math.Max(0, Math.Max((long)Math.Floor(need * Math.Max(0, MaxPercent) / 100.0), MaxUnits));
}

/// <summary>
/// Made-up costs (in gil terms) the planner charges for travelling, so it doesn't add a trip to save a few gil: one
/// per world other than the current one, and one per data center other than the current one (a lobby trip, much
/// slower). Only used to choose the route; never part of the gil totals shown.
///
/// With either cost set, a trip may also be dropped when that leaves units short, if they aren't worth it: each unit
/// left short counts as <see cref="ShortFactor"/> times the dearest unit price the cheapest plan paid for that item.
/// </summary>
public readonly record struct TripCosts(long PerWorld, long PerDataCenter, double ShortFactor = 2)
{
    public bool Enabled => PerWorld > 0 || PerDataCenter > 0;

    public static TripCosts FromConfig(Config config) => !config.RouteTripCostsEnabled
        ? default
        : new(Math.Max(0, config.RouteWorldTripCost), Math.Max(0, config.RouteDataCenterTripCost),
            Math.Max(0, config.RouteShortUnitFactor));
}

public class RoutePlan
{
    public List<WorldStop> Stops { get; init; } = [];
    public long Total => Stops.Sum(s => s.Subtotal);

    /// <summary>Cost of the cheapest plan with no limit on worlds, for comparison.</summary>
    public long CheapestTotal { get; init; }

    public int CheapestWorldCount { get; init; }

    /// <summary>The made-up travel cost the planner charged this route (<see cref="TripCosts"/>), for debugging.</summary>
    public long TripCost { get; init; }

    /// <summary>What the units left short to save trips "cost" the planner (<see cref="TripCosts.ShortFactor"/>), for debugging.</summary>
    public long ShortCost { get; init; }

    /// <summary>Items the route can't fully cover (not enough listings, or exact amounts impossible without overbuying).</summary>
    public List<(ShoppingListItem Item, long Missing)> Unfilled { get; init; } = [];

    /// <summary>
    /// Units the cheapest plan could cover but this route leaves short because the trip wasn't worth them (part of
    /// <see cref="Unfilled"/>).
    /// </summary>
    public List<(ShoppingListItem Item, long Units)> NotWorthTheTrip { get; init; } = [];

    /// <summary>Items that still need buying but have no price data (or data for other settings).</summary>
    public List<ShoppingListItem> NeedsPrices { get; init; } = [];

    public bool IsEmpty => Stops.Count == 0;
}

/// <summary>
/// Plans which worlds to visit to buy everything still needed.
///
/// 1. Find the cheapest way to buy each item using every world (greedy by price per unit, whole stacks only).
/// 2. Then repeatedly try dropping one world: re-plan without it, and keep the drop if either every item is still
///    covered as well as before and the total stays within <c>maxExtraPercent</c> of the cheapest total, or the
///    total plus the made-up <see cref="TripCosts"/> (trips, and units left short) goes down: a trip that saves
///    less than it "costs" isn't worth it, so the route sticks to the current data center unless another one is
///    well worth the hop. Without trip costs no drop may leave anything short.
///    Of the valid drops each round, the one leaving the fewest worlds wins, then the lowest total plus trip costs.
///    Stops when nothing can be dropped.
///
/// Stacks bigger than what's still needed are skipped unless the <see cref="OverbuyRule"/> allows the excess: then the
/// cheapest such stack may finish the item. Without any allowance an item can end up partly covered.
///
/// HQ follows each item's <see cref="ShoppingListItem.EffectiveQuality"/>: listings are split into tiers (HQ, then NQ
/// for Prefer HQ; HQ alone for HQ only; one mixed tier for Any) and filled tier by tier. Dropping a world may only
/// lower how much HQ an item gets by units it drops altogether, so "fewer trips" never quietly swaps HQ for NQ.
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
    /// <param name="excludedWorlds">Worlds whose listings to ignore (a route run's worlds already visited: what's
    /// left there is what the board didn't have, whatever Universalis still says).</param>
    public static RoutePlan Plan(IReadOnlyList<(ShoppingListItem Item, long StillNeeded)> wanted,
        float maxExtraPercent, OverbuyRule overbuy, TripCosts trips = default,
        (string World, string DataCenter)? currentWorld = null, IReadOnlySet<string>? excludedWorlds = null)
    {
        var buying = wanted.Where(w => w.Item.IsMarketable && w.StillNeeded > 0).ToList();
        var needsPrices = buying.Where(w => w.Item.MarketDataResponse is null || !w.Item.PricesMatchCurrentScope)
            .Select(w => w.Item).ToList();
        var needs = buying.Where(w => !needsPrices.Contains(w.Item))
            .Select(w => new Need(w.Item, w.StillNeeded, BuildTiers(w.Item, excludedWorlds)))
            .ToList();

        var cheapest = FillAll(needs, null, overbuy);
        var budget = cheapest.Total * (1 + Math.Max(0, maxExtraPercent) / 100.0);

        var dataCenters = new Dictionary<string, string>();
        string DataCenter(string world)
        {
            if (!dataCenters.TryGetValue(world, out var dc))
                dataCenters[world] = dc = DataCenterOf(world);
            return dc;
        }

        long TripCost(Fill fill)
        {
            if (!trips.Enabled)
                return 0;
            var worlds = fill.Worlds.Where(w => w != currentWorld?.World).ToList();
            var otherDcs = worlds.Select(DataCenter).Where(dc => dc != currentWorld?.DataCenter).Distinct().Count();
            return worlds.Count * trips.PerWorld + otherDcs * trips.PerDataCenter;
        }

        // Units below what the cheapest plan covers, valued at the dearest unit price it paid for the item, times the
        // factor: what leaving them short "costs" when weighing a trip.
        var shortValue = needs.ToDictionary(n => n.Item, n =>
            cheapest.Bought.TryGetValue(n.Item, out var listings) && listings.Count > 0
                ? listings.Max(l => (double)l.Cost / l.Quantity) * trips.ShortFactor
                : 0);
        long Lost(Fill fill, Need n) => Math.Max(0, cheapest.Covered(n) - fill.Covered(n));
        long ShortCost(Fill fill) => trips.Enabled ? (long)Math.Round(needs.Sum(n => Lost(fill, n) * shortValue[n.Item])) : 0;

        long Effective(Fill fill) => fill.Total + TripCost(fill) + ShortCost(fill);
        // Without trip costs: fewest worlds, then cheapest. With them, trips are already priced in (and "fewest worlds"
        // first would prefer buying nothing at all), so the lowest weight wins, fewer worlds on a tie.
        bool Better(Fill a, Fill b) => trips.Enabled
            ? Effective(a) < Effective(b) || (Effective(a) == Effective(b) && a.Worlds.Count < b.Worlds.Count)
            : a.Worlds.Count < b.Worlds.Count || (a.Worlds.Count == b.Worlds.Count && Effective(a) < Effective(b));

        // Ban one used world per round; its purchases can move to ANY world still allowed, including ones the
        // cheapest plan never touched. Keep the ban if it doesn't add worlds and either covers as much and fits the
        // budget, or (with trip costs) weighs less. The allowed set only shrinks, so this always ends.
        //
        // With trip costs, two more kinds of ban are tried each round, because a plain ban re-fills from the cheapest
        // worlds left and tends to scatter the purchases over even more small stacks elsewhere (more worlds, so the
        // ban is thrown out before trips are even weighed): a ban that only re-fills from worlds already on the
        // route, and a ban of a whole data center (both ways).
        var allowedWorlds = needs.SelectMany(n => n.AllListings).Select(l => l.WorldName).ToHashSet();
        var current = cheapest;
        var bestSeen = cheapest;
        while (true)
        {
            // The world the player is on costs no trip, so the extra bans keep it available (otherwise "buy nothing"
            // would compete with "buy it right here"), unless it's the one being banned.
            var home = currentWorld?.World;
            HashSet<string> KeepHome(IEnumerable<string> worlds, string? banned = null)
            {
                var set = worlds.ToHashSet();
                if (home is not null && home != banned && allowedWorlds.Contains(home))
                    set.Add(home);
                return set;
            }

            var candidates = new List<HashSet<string>>();
            foreach (var world in current.Worlds)
            {
                candidates.Add(allowedWorlds.Where(w => w != world).ToHashSet());
                if (trips.Enabled)
                    candidates.Add(KeepHome(current.Worlds.Where(w => w != world), world));
            }

            if (trips.Enabled)
            {
                foreach (var dc in current.Worlds.Select(DataCenter).Distinct().Where(dc => dc != currentWorld?.DataCenter).ToList())
                {
                    candidates.Add(allowedWorlds.Where(w => DataCenter(w) != dc).ToHashSet());
                    candidates.Add(KeepHome(current.Worlds.Where(w => DataCenter(w) != dc)));
                }
            }

            // A candidate must still drop something, or the loop wouldn't end.
            candidates.RemoveAll(c => c.Count >= allowedWorlds.Count);

            (Fill Fill, HashSet<string> Allowed)? best = null;
            foreach (var allowed in candidates)
            {
                var trial = FillAll(needs, allowed, overbuy);
                var losesUnits = needs.Any(n => Lost(trial, n) > 0);
                if (trial.Worlds.Count > current.Worlds.Count
                    || (losesUnits && !trips.Enabled)
                    // HQ may only go with units dropped altogether, never be swapped for NQ.
                    || needs.Any(n => cheapest.HqCovered(n) - trial.HqCovered(n) > Lost(trial, n))
                    || !((trial.Total <= budget && !losesUnits) || Effective(trial) < Effective(current)))
                    continue;
                if (best is null || Better(trial, best.Value.Fill))
                    best = (trial, allowed);
            }

            if (best is null)
                break;
            allowedWorlds = best.Value.Allowed;
            current = best.Value.Fill;
            if (Better(current, bestSeen))
                bestSeen = current;
        }

        current = bestSeen;

        return new RoutePlan
        {
            Stops = BuildStops(current, currentWorld),
            CheapestTotal = cheapest.Total,
            CheapestWorldCount = cheapest.Worlds.Count,
            TripCost = TripCost(current),
            ShortCost = ShortCost(current),
            NotWorthTheTrip = needs.Where(n => Lost(current, n) > 0).Select(n => (n.Item, Lost(current, n))).ToList(),
            Unfilled = needs.Where(n => current.Covered(n) < n.Quantity)
                .Select(n => (n.Item, n.Quantity - current.Covered(n)))
                .ToList(),
            NeedsPrices = needsPrices,
        };
    }

    private static Fill FillAll(List<Need> needs, HashSet<string>? allowedWorlds, OverbuyRule overbuy)
    {
        var bought = new Dictionary<ShoppingListItem, List<MarketDataListing>>();
        long total = 0;
        foreach (var need in needs)
        {
            var taken = FillOne(need, allowedWorlds, overbuy);
            if (taken.Count == 0)
                continue;
            bought[need.Item] = taken;
            total += taken.Sum(l => l.Cost);
        }

        return new Fill(bought, total);
    }

    private static List<List<MarketDataListing>> BuildTiers(ShoppingListItem item, IReadOnlySet<string>? excludedWorlds)
    {
        var usable = item.MarketDataResponse!.Listings
            .Where(l => l.Quantity > 0 && !string.IsNullOrEmpty(l.WorldName)
                        && (excludedWorlds is null || !excludedWorlds.Contains(l.WorldName)))
            .OrderBy(l => (double)l.Cost / l.Quantity)
            .ToList();
        return item.EffectiveQuality switch
        {
            QualityPreference.HqOnly => [usable.Where(l => l.Hq).ToList()],
            QualityPreference.PreferHq => [usable.Where(l => l.Hq).ToList(), usable.Where(l => !l.Hq).ToList()],
            _ => [usable],
        };
    }

    // Fills from the first tier, then whatever's left from the next, and so on. The overbuy allowance is worked out
    // from the item's whole need, so a later tier doesn't get a smaller one just because less is left.
    private static List<MarketDataListing> FillOne(Need need, HashSet<string>? allowedWorlds, OverbuyRule overbuy)
    {
        var taken = new List<MarketDataListing>();
        var remaining = need.Quantity;
        var maxExcess = overbuy.MaxExcess(need.Quantity);
        foreach (var tier in need.Tiers)
        {
            if (remaining <= 0)
                break;
            var fromTier = FillFromTier(tier, remaining, allowedWorlds, maxExcess);
            taken.AddRange(fromTier);
            remaining -= fromTier.Sum(l => l.Quantity);
        }

        return taken;
    }

    /// <param name="maxExcess">How many units past <paramref name="quantity"/> a finishing stack may bring
    /// (0 = only stacks that fit, <see cref="long.MaxValue"/> = any).</param>
    internal static List<MarketDataListing> FillFromTier(List<MarketDataListing> byUnitPrice, long quantity,
        HashSet<string>? allowedWorlds, long maxExcess)
    {
        var taken = new List<MarketDataListing>();
        var remaining = quantity;
        var tooBig = new List<MarketDataListing>();

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
            else if (maxExcess > 0)
            {
                // Too big for what's left; keep it in case nothing smaller finishes the job.
                tooBig.Add(listing);
            }
        }

        // Every skipped stack was bigger than what was left at the time, so it covers what's left now. The cheapest
        // one whose excess is within the allowance finishes the item.
        var finisher = remaining > 0
            ? tooBig.Where(l => l.Quantity - remaining <= maxExcess).MinBy(l => l.Cost)
            : null;
        if (finisher is not null)
            taken.Add(finisher);

        if (maxExcess <= 0)
            return taken;

        // A single stack covering the whole need can beat a pile of small ones plus a finisher.
        var oneStack = byUnitPrice
            .Where(l => l.Quantity >= quantity && l.Quantity - quantity <= maxExcess
                        && (allowedWorlds is null || allowedWorlds.Contains(l.WorldName)))
            .MinBy(l => l.Cost);
        var takenCovers = taken.Sum(l => l.Quantity) >= quantity;
        if (oneStack is not null && (!takenCovers || oneStack.Cost < taken.Sum(l => l.Cost)))
            return [oneStack];
        return taken;
    }

    private static string DataCenterOf(string world) =>
        ExcelWorldHelper.Get(world)?.DataCenter.ValueNullable?.Name.ToString() ?? "?";

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
                    var dc = DataCenterOf(listing.WorldName);
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
