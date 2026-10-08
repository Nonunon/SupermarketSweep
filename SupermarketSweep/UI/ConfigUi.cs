using System.Numerics;
using ECommons.Configuration;
using Dalamud.Bindings.ImGui;
using ECommons.ImGuiMethods;
using Dalamud.Interface.Windowing;

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
