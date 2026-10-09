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
        ImGuiEx.Tooltip("The amount of time in seconds before considering Lifestream to be stuck.");

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

        ImGuiEx.Tooltip(config.BuyAutomation.Description() + "\nEvery buy is checked: the row on screen, the confirmation's item and price, your gil and the limit below.");

        using var disabled = ImRaii.Disabled(config.BuyAutomation == BuyAutomation.OutlineOnly);
        var delay = config.AutoBuyStepDelayMs;
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("Step delay", ref delay, 300, 3000, "%d ms"))
            config.AutoBuyStepDelayMs = delay;
        if (ImGui.IsItemDeactivatedAfterEdit())
            EzConfig.Save();
        ImGuiEx.Tooltip("Pause between automated clicks, give or take 30%.");

        var limit = config.AutoBuyMaxGilPerRun;
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Max gil per run", ref limit, 100_000, 1_000_000))
            config.AutoBuyMaxGilPerRun = Math.Max(0, limit);
        if (ImGui.IsItemDeactivatedAfterEdit())
            EzConfig.Save();
        ImGuiEx.Tooltip("An automated run stops before it would spend more than this in total.");
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
