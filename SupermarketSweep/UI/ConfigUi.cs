using System.Numerics;
using ECommons.Configuration;
using Dalamud.Bindings.ImGui;
using ECommons.ImGuiMethods;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using SupermarketSweep.Models;

namespace SupermarketSweep.UI;

public class ConfigUi : Window
{
    public ConfigUi() : base("Supermarket Sweep Configuration", ImGuiWindowFlags.None, false)
    {
        Size = new Vector2(500, 300);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var inventory = SupermarketSweep.Config.AllCharactersInventory;
        DrawBoolConfig("Search Inventory Across All Characters", ref inventory, x => SupermarketSweep.Config.AllCharactersInventory = x, "If enabled, will search all characters' inventories for items.");

        var removeAutomatically = SupermarketSweep.Config.RemoveQuantityAutomatically;
        DrawBoolConfig("Remove Quantity Automatically", ref removeAutomatically, x => SupermarketSweep.Config.RemoveQuantityAutomatically = x, "If enabled, will remove quantity from items in the shopping list whenever inventory is updated.");

        var useVnavPathing = SupermarketSweep.Config.UseVnavPathing;
        DrawBoolConfig("Use vnavmesh Pathing", ref useVnavPathing, x => SupermarketSweep.Config.UseVnavPathing = x, "If enabled, will use Vnavmesh to move you towards the marketboard after world/DC travelling.");

        var lifeStreamTimeout = SupermarketSweep.Config.LifeStreamTimeout;
        if (ImGui.InputInt("Lifestream Timeout", ref lifeStreamTimeout))
        {
            SupermarketSweep.Config.LifeStreamTimeout = lifeStreamTimeout;
            EzConfig.Save();
        }
        ImGuiEx.Tooltip("The amount of time in seconds before considering Lifestream to be stuck (at least 10).\nApplies from the next travel on.");

        ImGui.Spacing();
        var buyAssist = SupermarketSweep.Config.BuyAssistEnabled;
        DrawBoolConfig("Buy Assistant", ref buyAssist, x => SupermarketSweep.Config.BuyAssistEnabled = x, "While the marketboard is open, outline the item to pick and the listings to buy, checked live against the route.\nIt never clicks or buys anything itself.");

        var maxOver = SupermarketSweep.Config.BuyAssistMaxOverPercent;
        if (ImGui.SliderFloat("Max over route price", ref maxOver, 0, 50, "%.0f%%"))
            SupermarketSweep.Config.BuyAssistMaxOverPercent = maxOver;
        if (ImGui.IsItemDeactivatedAfterEdit())
            EzConfig.Save();
        ImGuiEx.Tooltip("Listings on the board can differ from the pulled prices. Recommend one only if its price per unit\nis at most this much above what the route planned to pay.");

        var opportunistic = SupermarketSweep.Config.BuyAssistOpportunistic;
        DrawBoolConfig("Buy extras when cheaper here", ref opportunistic, x => SupermarketSweep.Config.BuyAssistOpportunistic = x, "Besides what the route plans for this world, also recommend (and buy) listings that cost less than the units\nthe route plans on other worlds. The route then replans, and a stop that's no longer needed drops out.");

        DrawAutomationConfig();

        ImGui.Spacing();
        var logCallbacks = SupermarketSweep.Config.LogAddonCallbacks;
        DrawBoolConfig("Log Marketboard Callbacks (debug)", ref logCallbacks, x => SupermarketSweep.Config.LogAddonCallbacks = x, "Writes every UI callback fired while the marketboard is open to /xllog, prefixed [CallbackLogger].\nOnly watches; nothing is clicked or changed. Leave off unless you're gathering callbacks.");

        var pickStops = SupermarketSweep.Config.RouteRunPickStops;
        DrawBoolConfig("Pick Route Run Stops (debug)", ref pickStops, x => SupermarketSweep.Config.RouteRunPickStops = x, "Puts a checkbox on each stop in the Route tab. Run route skips the unticked worlds,\nso a test can cover just a world or two. The route itself is still planned over every world.");

        var showTrips = SupermarketSweep.Config.RouteShowTripCosts;
        DrawBoolConfig("Show Route Trip Costs (debug)", ref showTrips, x => SupermarketSweep.Config.RouteShowTripCosts = x, "The planner charges made-up gil for each extra world and data center, so it doesn't add a trip to save a\nfew gil. This shows those numbers on the Route tab and lets you tune them here.");
        if (SupermarketSweep.Config.RouteShowTripCosts)
            DrawTripCosts();

    }

    private static void DrawAutomationConfig()
    {
        var config = SupermarketSweep.Config;
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo("Buy automation", config.BuyAutomation.ToFriendlyString()))
        {
            if (combo)
            {
                foreach (var level in Enum.GetValues<BuyAutomation>())
                {
                    if (ImGui.Selectable(level.ToFriendlyString(), level == config.BuyAutomation) && level != config.BuyAutomation)
                    {
                        config.BuyAutomation = level;
                        EzConfig.Save();
                    }

                    ImGuiEx.Tooltip(level.Description());
                }
            }
        }

        ImGuiEx.Tooltip(config.BuyAutomation.Description() + "\nEvery buy is checked: the row on screen, the confirmation's item and price, your gil and the reserve below.");

        using var disabled = ImRaii.Disabled(config.BuyAutomation == BuyAutomation.OutlineOnly);
        var delay = config.AutoBuyStepDelayMs;
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("Step delay", ref delay, 300, 3000, "%d ms"))
            config.AutoBuyStepDelayMs = delay;
        if (ImGui.IsItemDeactivatedAfterEdit())
            EzConfig.Save();
        ImGuiEx.Tooltip("Pause between automated clicks, give or take 30%.");

        var reserve = config.AutoBuyGilReserve;
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Keep at least (gil)", ref reserve, 100_000, 1_000_000))
            config.AutoBuyGilReserve = Math.Clamp(reserve, 0, 999_999_999);
        if (ImGui.IsItemDeactivatedAfterEdit())
            EzConfig.Save();
        ImGuiEx.Tooltip("Gil reserve: automated buying stops before a purchase would leave you with less than this.\n0 = spend everything if needed.");

        var travel = config.RouteTravelAllowancePerStop;
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Travel allowance per stop (gil)", ref travel, 100, 1000))
            config.RouteTravelAllowancePerStop = Math.Clamp(travel, 0, 1_000_000);
        if (ImGui.IsItemDeactivatedAfterEdit())
            EzConfig.Save();
        ImGuiEx.Tooltip("Route runs only: a rough estimate of teleport costs per world, added to the route total in the check\nbefore a run. World visits from a hub aetheryte are free; getting to a hub first isn't.");
    }

    private static void DrawTripCosts()
    {
        var config = SupermarketSweep.Config;
        using var indent = ImRaii.PushIndent();
        var world = config.RouteWorldTripCost;
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Per extra world", ref world, 100, 1000))
            config.RouteWorldTripCost = Math.Clamp(world, 0, 10_000_000);
        if (ImGui.IsItemDeactivatedAfterEdit())
            EzConfig.Save();
        ImGuiEx.Tooltip("Made-up gil per world visited other than the one you're on. A world stays on the route only if it saves more.");

        var dc = config.RouteDataCenterTripCost;
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Per extra data center", ref dc, 1000, 10_000))
            config.RouteDataCenterTripCost = Math.Clamp(dc, 0, 10_000_000);
        if (ImGui.IsItemDeactivatedAfterEdit())
            EzConfig.Save();
        ImGuiEx.Tooltip("Made-up gil per data center other than yours (a lobby trip), on top of the per-world cost.\nAnother data center only makes the route if it's this much better.");

        var factor = config.RouteShortUnitFactor;
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputFloat("Short unit weight", ref factor, 0.5f, 1f, "%.1fx"))
            config.RouteShortUnitFactor = Math.Clamp(factor, 0, 1000);
        if (ImGui.IsItemDeactivatedAfterEdit())
            EzConfig.Save();
        ImGuiEx.Tooltip("A trip may be dropped even if that leaves some units short, when they aren't worth it: each unit left\nshort counts as this many times the dearest price the route would have paid for it. Higher = cover more.");
    }

    private void DrawBoolConfig(string label, ref bool value, Action<bool> setter, string tooltip = "")
    {
        if (ImGui.Checkbox(label, ref value))
        {
            setter(value);
            EzConfig.Save();
        }
        ImGuiEx.Tooltip(tooltip);
    }
}
