using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ECommons.Configuration;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.ImGuiMethods;
using SupermarketSweep.Models;

namespace SupermarketSweep.UI;

/// <summary>
/// The "Route" tab: which worlds to visit and what to buy at each, from <see cref="RoutePlanner"/>.
/// Re-plans in the background whenever the list, owned counts, prices or route settings change.
/// </summary>
public class RoutePanel(SupermarketSweep manager)
{
    private string _plannedFor = string.Empty;
    private RoutePlan? _plan;
    private Task<RoutePlan>? _planning;
    private string _planningFor = string.Empty;
    private bool _planFailed;

    /// <summary>The latest finished plan (null until one exists). Kept current by <see cref="UpdatePlan"/>.</summary>
    public RoutePlan? Plan => _plan;

    public void Draw()
    {
        DrawSettings();
        ImGui.Separator();
        ImGui.Spacing();

        UpdatePlan();
        if (_planFailed)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "Route planning hit an error (details in /xllog). It retries when anything changes.");
            ImGui.Spacing();
        }

        if (_plan is null)
        {
            if (!_planFailed)
                ImGui.TextDisabled("Planning...");
            return;
        }

        if (_planning is not null)
        {
            ImGui.TextDisabled("Updating...");
            ImGui.SameLine();
        }

        DrawNeedsPrices(_plan);
        DrawSummary(_plan);
        DrawUnfilled(_plan);
        ImGui.Spacing();

        using var child = ImRaii.Child("RouteStops");
        var number = 1;
        foreach (var stop in _plan.Stops)
            DrawStop(stop, number++);
    }

    private void DrawSettings()
    {
        var config = SupermarketSweep.Config;

        var extra = config.RouteMaxExtraPercent;
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("Max extra to skip a world", ref extra, 0, 50, "%.0f%%"))
            config.RouteMaxExtraPercent = extra;
        if (ImGui.IsItemDeactivatedAfterEdit())
            EzConfig.Save();
        ImGuiEx.Tooltip("Pay up to this much more in total to visit fewer worlds.\n0% = always the cheapest route, however many worlds that takes.");

        ImGui.SameLine();
        var overbuy = config.RouteAllowOverbuy;
        if (ImGui.Checkbox("Allow overbuying", ref overbuy))
        {
            config.RouteAllowOverbuy = overbuy;
            EzConfig.Save();
        }

        ImGuiEx.Tooltip("Allow buying a stack bigger than you need (say 10 when you need 3) when that's cheaper.\nOff: only stacks that fit, which can leave an item short.");

        ImGui.SameLine();
        var oceania = config.RouteIncludeOceania;
        using (ImRaii.Disabled(config.ShoppingRegion != RegionType.NorthAmerica))
        {
            if (ImGui.Checkbox("Include Oceania", ref oceania))
            {
                config.RouteIncludeOceania = oceania;
                EzConfig.Save();
            }
        }

        ImGuiEx.Tooltip("When shopping North America, also pull Materia (Oceania) prices so the route can go there.\nNeeds a fresh Pull All Prices after changing.");

        var quality = config.RouteDefaultQuality;
        if (UiHelpers.QualityCombo("Default quality", ref quality, false, 110))
        {
            config.RouteDefaultQuality = quality;
            EzConfig.Save();
        }

        ImGuiEx.Tooltip("HQ rule for items that don't set their own (Item tab).");
    }

    /// <summary>Re-plans in the background if anything changed. Cheap when nothing did; call from the framework thread.</summary>
    public void UpdatePlan()
    {
        if (_planning is { IsCompleted: true })
        {
            _planFailed = !_planning.IsCompletedSuccessfully;
            if (_planFailed)
                Svc.Log.Error($"Route planning failed: {_planning.Exception}");
            else
                _plan = _planning.Result;
            _plannedFor = _planningFor; // either way, don't re-plan the same inputs every frame

            _planning = null;
        }

        // Owned counts come from IPC, so read them here on the framework thread and pass plain numbers along.
        var wanted = manager.WantedItems.Select(i => (Item: i, StillNeeded: i.StillNeeded)).ToList();
        var key = PlanKey(wanted);
        if (_planning is not null || key == _plannedFor)
            return;

        var config = SupermarketSweep.Config;
        var extra = config.RouteMaxExtraPercent;
        var overbuy = config.RouteAllowOverbuy;
        _planningFor = key;
        (string, string)? here = Player.Available ? (Player.CurrentWorldName, Player.CurrentDataCenterName) : null;
        _planning = Task.Run(() => RoutePlanner.Plan(wanted, extra, overbuy, here));
    }

    private static string PlanKey(List<(ShoppingListItem Item, long StillNeeded)> wanted)
    {
        var config = SupermarketSweep.Config;
        var sb = new StringBuilder();
        sb.Append($"{config.RouteMaxExtraPercent:0}|{config.RouteAllowOverbuy}|{config.ShoppingRegion}|{config.RouteIncludeOceania}|{config.RouteDefaultQuality}");
        foreach (var (item, still) in wanted)
            sb.Append($"|{item.ItemId}:{still}:{item.MarketDataFetchedAt?.Ticks}:{item.IsFetchingData}:{item.Quality}");
        return sb.ToString();
    }

    private void DrawNeedsPrices(RoutePlan plan)
    {
        if (plan.NeedsPrices.Count == 0)
            return;

        ImGui.TextColored(ImGuiColors.DalamudOrange, $"{plan.NeedsPrices.Count} item(s) need prices first.");
        ImGuiEx.Tooltip(string.Join("\n", plan.NeedsPrices.Select(i => i.Name)));
        ImGui.SameLine();
        var fetching = manager.WantedItems.Any(i => i.IsFetchingData);
        using (ImRaii.Disabled(fetching))
        {
            if (ImGui.SmallButton(fetching ? "Pulling..." : "Pull All Prices"))
                foreach (var item in manager.WantedItems)
                    item.RefreshMarketData();
        }
    }

    private static void DrawSummary(RoutePlan plan)
    {
        if (plan.IsEmpty)
        {
            if (plan.NeedsPrices.Count == 0 && plan.Unfilled.Count == 0)
                ImGui.TextColored(ImGuiColors.HealerGreen, "Nothing left to buy!");
            return;
        }

        ImGui.Text($"{plan.Stops.Count} world(s), {UiHelpers.Gil(plan.Total)} gil total");
        if (plan.Stops.Count < plan.CheapestWorldCount)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"(cheapest: {UiHelpers.Gil(plan.CheapestTotal)} gil over {plan.CheapestWorldCount} worlds, " +
                               $"so +{UiHelpers.Gil(plan.Total - plan.CheapestTotal)} gil saves {plan.CheapestWorldCount - plan.Stops.Count} trip(s))");
        }
    }

    private static void DrawUnfilled(RoutePlan plan)
    {
        foreach (var (item, missing) in plan.Unfilled)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"Can't fully cover {item.Name}: {missing} short.");
            ImGuiEx.Tooltip(SupermarketSweep.Config.RouteAllowOverbuy
                ? item.EffectiveQuality == QualityPreference.HqOnly
                    ? "Not enough HQ listings in the region (this item is set to HQ only)."
                    : "Not enough listings in the region."
                : "Not enough listings, or only stacks bigger than what's left. Allow overbuying to use those.");
        }
    }

    private void DrawStop(WorldStop stop, int number)
    {
        using var id = ImRaii.PushId(stop.World);
        if (!ImGui.CollapsingHeader($"{number}. {stop.World} ({stop.DataCenter}): {UiHelpers.Gil(stop.Subtotal)} gil###stop",
                ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (ImGui.Button($"Travel to {stop.World}"))
            manager.TravelToWorld(stop.World);
        ImGuiEx.Tooltip("Lifestream travel, then walk to the marketboard if vnavmesh pathing is on");

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;
        using var table = ImRaii.Table("##stopItems", 4, flags);
        if (!table)
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("Buy", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Stacks", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableHeadersRow();

        foreach (var group in stop.Purchases.GroupBy(p => p.Item))
        {
            var listings = group.Select(p => p.Listing).ToList();
            var quantity = listings.Sum(l => l.Quantity);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var marketboardOpen = UiHelpers.IsMarketboardOpen;
            if (ImGui.Selectable(group.Key.Name) && !UiHelpers.SearchMarketboard(group.Key.Name))
                Svc.Chat.PrintError("[Supermarket Sweep] Open the marketboard first, then click the item to search it.");
            ImGuiEx.Tooltip((marketboardOpen
                                ? "Click to search the marketboard for this item"
                                : "Open the marketboard, then click to search it for this item")
                            + "\n\n" + string.Join("\n", listings.Select(l =>
                                $"{l.Quantity} x {UiHelpers.Gil(l.PricePerUnit)}{(l.Hq ? " HQ" : "")} from {l.RetainerName}")));

            ImGui.TableNextColumn();
            var hq = listings.Where(l => l.Hq).Sum(l => l.Quantity);
            if (hq == 0)
                ImGui.Text(quantity.ToString());
            else if (hq == quantity)
                ImGui.TextColored(ImGuiColors.DalamudYellow, $"{quantity} HQ");
            else
                ImGui.TextColored(ImGuiColors.DalamudOrange, $"{hq} HQ + {quantity - hq} NQ");

            ImGui.TableNextColumn();
            ImGui.Text(listings.Count.ToString());

            ImGui.TableNextColumn();
            ImGui.Text(UiHelpers.Gil(listings.Sum(l => l.Cost)));
        }
    }
}
