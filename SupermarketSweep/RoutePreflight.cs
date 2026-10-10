namespace SupermarketSweep;

public enum Affordability
{
    /// <summary>The route has no stops.</summary>
    NothingToBuy,

    /// <summary>Gil above the reserve covers the whole route plus travel.</summary>
    Fully,

    /// <summary>Some gil above the reserve, but not enough for everything: buy in route order until the reserve.</summary>
    Partly,

    /// <summary>No gil above the reserve.</summary>
    NotAtAll,
}

/// <param name="TravelAllowance">The rough travel estimate included in <see cref="Cost"/>.</param>
/// <param name="StopsCovered">How many stops, in route order, the spendable gil pays for in full.</param>
/// <param name="Warnings">Worth knowing, but the run can go ahead (items without prices, items the route can't cover).</param>
/// <param name="Blockers">Reasons the run can't start (missing plugins, automation off), from the caller.</param>
public record PreflightResult(Affordability Affordability, long RouteTotal, long TravelAllowance, long Gil, long Reserve,
    int StopsCovered, int StopCount, IReadOnlyList<string> Warnings, IReadOnlyList<string> Blockers)
{
    public long Cost => RouteTotal + TravelAllowance;
    public long Spendable => Math.Max(0, Gil - Reserve);
    public bool CanStart => Blockers.Count == 0 && Affordability is Affordability.Fully or Affordability.Partly;
}

/// <summary>
/// The sanity check before a route run: can the gil above the reserve pay for the route? Pure, so it can be tested
/// outside the game; the caller reads the gil and the environment (plugins, settings) on the framework thread.
///
/// The cost is only an estimate: the buyer may pay up to the "max over route price" setting more per unit, and
/// opportunistic extras move spending between worlds. The reserve is what actually stops the buying.
/// </summary>
public static class RoutePreflight
{
    /// <param name="travelPerStop">Rough gil per stop for teleporting to a hub city on the way (world visits from a hub
    /// are free). Not charged for a stop on the world the player is already on.</param>
    /// <param name="blockers">Reasons the run can't start, found by the caller.</param>
    public static PreflightResult Check(RoutePlan plan, long gil, long reserve, long travelPerStop, string? currentWorld,
        IReadOnlyList<string> blockers)
    {
        reserve = Math.Max(0, reserve);
        travelPerStop = Math.Max(0, travelPerStop);
        long Travel(WorldStop stop) => stop.World == currentWorld ? 0 : travelPerStop;

        var spendable = Math.Max(0, gil - reserve);
        var travel = plan.Stops.Sum(Travel);
        var cost = plan.Total + travel;

        var covered = 0;
        long running = 0;
        foreach (var stop in plan.Stops)
        {
            running += stop.Subtotal + Travel(stop);
            if (running > spendable)
                break;
            covered++;
        }

        var affordability = plan.IsEmpty ? Affordability.NothingToBuy
            : spendable >= cost ? Affordability.Fully
            : spendable > 0 ? Affordability.Partly
            : Affordability.NotAtAll;

        var warnings = new List<string>();
        if (plan.NeedsPrices.Count > 0)
            warnings.Add($"{plan.NeedsPrices.Count} item(s) have no prices for these settings and won't be bought: " +
                         string.Join(", ", plan.NeedsPrices.Select(i => i.Name)) + ".");
        foreach (var (item, missing) in plan.Unfilled)
            warnings.Add($"{item.Name}: the route can't cover {missing}.");
        if (affordability == Affordability.Partly)
            warnings.Add(covered == 0
                ? "Not enough gil for the first stop in full: buying stops when it reaches the reserve."
                : $"Enough gil for {covered} of {plan.Stops.Count} stop(s) in full: buying goes in route order and stops at the reserve.");

        return new PreflightResult(affordability, plan.Total, travel, gil, reserve, covered, plan.Stops.Count, warnings,
            blockers);
    }
}
