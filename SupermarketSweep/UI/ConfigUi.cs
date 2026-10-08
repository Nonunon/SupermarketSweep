using System.Numerics;
using ECommons.Configuration;
using Dalamud.Bindings.ImGui;
using NostraLib;
using OtterGui;

namespace SupermarketSweep.UI;

public class ConfigUi : NostraWindow
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
        ImGuiUtil.HoverTooltip("The amount of time in seconds before considering Lifestream to be stuck.");

        var autoPull = SupermarketSweep.Config.AutoPullMarketData;
        DrawBoolConfig("Auto-pull Prices", ref autoPull, x => SupermarketSweep.Config.AutoPullMarketData = x, "If enabled, prices are pulled from Universalis automatically: for the selected item, and for the whole list when the window opens.\nAlso re-pulls when you change Region/Datacenter.");

        var maxAge = SupermarketSweep.Config.MarketDataMaxAgeMinutes;
        if (ImGui.InputInt("Re-pull After (minutes)", ref maxAge))
        {
            SupermarketSweep.Config.MarketDataMaxAgeMinutes = Math.Max(1, maxAge);
            EzConfig.Save();
        }
        ImGuiUtil.HoverTooltip("How old prices can get before Auto-pull fetches them again.");
    }

    private void DrawBoolConfig(string label, ref bool value, Action<bool> setter, string tooltip = "")
    {
        if (ImGui.Checkbox(label, ref value))
        {
            setter(value);
            EzConfig.Save();
        }
        ImGuiUtil.HoverTooltip(tooltip);
    }
}