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
    /// <summary>Allow buying a stack bigger than what's still needed when that's the cheaper way to finish.</summary>
    public bool RouteAllowOverbuy { get; set; } = false;
    /// <summary>When shopping North America, also pull Oceania (Materia) prices so the route can go there.</summary>
    public bool RouteIncludeOceania { get; set; } = false;
    /// <summary>HQ rule for items that don't set their own.</summary>
    public QualityPreference RouteDefaultQuality { get; set; } = QualityPreference.PreferHq;
}