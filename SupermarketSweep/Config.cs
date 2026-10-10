using ECommons.Configuration;
using ECommons.DalamudServices;
using Newtonsoft.Json;
using SupermarketSweep.Models;

namespace SupermarketSweep;

public class Config
{
    public bool AllCharactersInventory { get; set; } = false;
    public RegionType ShoppingRegion { get; set; } = RegionType.NorthAmerica;
    public int LifeStreamTimeout { get; set; } = 300;
    public bool RemoveQuantityAutomatically { get; set; }
    public bool UseVnavPathing { get; set; } = true;
    public float SearchListHeight { get; set; } = 260;
    public float ShoppingListWidth { get; set; } = 200;

    // Route planner
    /// <summary>Accept up to this much extra total cost (percent) to skip visiting another world.</summary>
    public float RouteMaxExtraPercent { get; set; } = 5;
    /// <summary>Whether a stack bigger than what's still needed may be bought (see <see cref="OverbuyMode"/>).</summary>
    public OverbuyMode RouteOverbuyMode { get; set; } = OverbuyMode.Limited;
    /// <summary>With <see cref="OverbuyMode.Limited"/>: a finishing stack may go this many percent past the need...</summary>
    public float RouteOverbuyMaxPercent { get; set; } = 5;
    /// <summary>...or this many units past it, whichever allows more.</summary>
    public int RouteOverbuyMaxUnits { get; set; } = 10;
    /// <summary>When shopping North America, also pull Oceania (Materia) prices so the route can go there.</summary>
    public bool RouteIncludeOceania { get; set; } = false;
    /// <summary>HQ rule for items that don't set their own.</summary>
    public QualityPreference RouteDefaultQuality { get; set; } = QualityPreference.PreferHq;

    // Buy assistant
    /// <summary>Show the buy assistant (and its outlines on the game's marketboard windows) while the board is open.</summary>
    public bool BuyAssistEnabled { get; set; } = true;
    /// <summary>Recommend a live listing only if its unit price is at most this much over what the route planned.</summary>
    public float BuyAssistMaxOverPercent { get; set; } = 10;
    /// <summary>Also recommend (and buy) extra listings here that beat what the route pays on other worlds.</summary>
    public bool BuyAssistOpportunistic { get; set; } = true;
    /// <summary>How much the buy assistant clicks by itself.</summary>
    public BuyAutomation BuyAutomation { get; set; } = BuyAutomation.OutlineOnly;
    /// <summary>Pause between automated steps, in milliseconds (randomized by 30% either way).</summary>
    public int AutoBuyStepDelayMs { get; set; } = 700;
    /// <summary>Automated buying stops before a purchase would leave less gil than this.</summary>
    public int AutoBuyGilReserve { get; set; } = 0;
    /// <summary>Route run preflight: rough gil per stop for teleporting to a hub city on the way (world visits are free).</summary>
    public int RouteTravelAllowancePerStop { get; set; } = 1000;

    // Debug
    /// <summary>Log addon callbacks while the marketboard is open (see <see cref="CallbackLogger"/>).</summary>
    public bool LogAddonCallbacks { get; set; } = false;
    /// <summary>Show a checkbox per stop on the Route tab; route runs skip unticked worlds (for testing).</summary>
    public bool RouteRunPickStops { get; set; } = false;
    /// <summary>Weigh trips when planning (<see cref="TripCosts"/>): only travel when it's worth it. Route tab, "Trip costs".</summary>
    public bool RouteTripCostsEnabled { get; set; } = false;
    public int RouteWorldTripCost { get; set; } = 25;
    /// <summary>Made-up gil the planner charges per data center visited other than the current one.</summary>
    public int RouteDataCenterTripCost { get; set; } = 1000;
    /// <summary>With trip costs: each unit a dropped trip leaves short counts as this many times its dearest planned price.</summary>
    public float RouteShortUnitFactor { get; set; } = 2;
}