using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ECommons.Configuration;
using ECommons.ImGuiMethods;

namespace SupermarketSweep.UI;

/// <summary>
/// The Route tab's "Trip costs" section: made-up gil the planner charges per extra world and data center, so the
/// route doesn't add a trip for a few gil (<see cref="TripCosts"/>). Off by default.
/// </summary>
public static class TripCostsPanel
{
    public static void Draw(RoutePlan? plan)
    {
        var config = SupermarketSweep.Config;
        using var node = ImRaii.TreeNode(config.RouteTripCostsEnabled ? "Trip costs (on)###tripCosts" : "Trip costs (off)###tripCosts");
        if (!node)
            return;

        var enabled = config.RouteTripCostsEnabled;
        if (ImGui.Checkbox("Weigh trips: only travel when it's worth it", ref enabled))
        {
            config.RouteTripCostsEnabled = enabled;
            EzConfig.Save();
        }

        ImGuiEx.Tooltip("Off: the route buys everything as cheaply as it can, however many worlds that takes\n(\"Max extra to skip a world\" still trims trips that cost little).");

        using var disabled = ImRaii.Disabled(!enabled);
        ImGui.TextDisabled("Made-up costs used only to choose the route; the gil totals don't include them.");

        var world = config.RouteWorldTripCost;
        if (Input("Each extra world counts as", "gil", ref world, 25, 100))
            config.RouteWorldTripCost = Math.Clamp(world, 0, 10_000_000);
        ImGuiEx.Tooltip("Per world other than the one you're on. A world only makes the route if it saves more than this.");

        var dc = config.RouteDataCenterTripCost;
        if (Input("Each extra data center counts as", "gil", ref dc, 500, 5000))
            config.RouteDataCenterTripCost = Math.Clamp(dc, 0, 10_000_000);
        ImGuiEx.Tooltip("Per data center other than yours (a lobby trip, a few minutes), on top of its worlds.\nAnother data center only makes the route if it's this much better.");

        var factor = config.RouteShortUnitFactor;
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Units left short count as");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputFloat("x their price##shortFactor", ref factor, 0.5f, 1f, "%.1f"))
            config.RouteShortUnitFactor = Math.Clamp(factor, 0, 1000);
        if (ImGui.IsItemDeactivatedAfterEdit())
            EzConfig.Save();
        ImGuiEx.Tooltip("A trip may be skipped even if that leaves some units short, when they aren't worth it. Each unit left\n" +
                        "short counts as this many times the dearest price the route would have paid for it.\nHigher = cover more, travel more.");

        if (enabled && plan is { } p)
            ImGui.TextDisabled($"This route: {UiHelpers.Gil(p.TripCost)} for travel, {UiHelpers.Gil(p.ShortCost)} for units left short " +
                               $"(weighed as {UiHelpers.Gil(p.Total + p.TripCost + p.ShortCost)}).");
    }

    // "<label> [value] <unit>" on one line.
    private static bool Input(string label, string unit, ref int value, int step, int stepFast)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
        var changed = ImGui.InputInt($"{unit}##{label}", ref value, step, stepFast);
        if (ImGui.IsItemDeactivatedAfterEdit())
            EzConfig.Save();
        return changed;
    }
}
