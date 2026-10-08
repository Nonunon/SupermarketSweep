using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.UI;
using SupermarketSweep.Models;

namespace SupermarketSweep.UI;

/// <summary>Right-hand details for the selected list item: header, quantities, actions, price listings.</summary>
public class ItemPanel(SupermarketSweep manager)
{
    private readonly ListingsTable _listings = new(manager);

    public void Draw(ShoppingListItem? item)
    {
        if (item is null)
        {
            ImGui.TextDisabled("Select an item on the left to see its prices.");
            return;
        }

        DrawHeader(item);
        ImGui.Spacing();
        DrawQuantities(item);
        ImGui.Spacing();

        if (!item.IsMarketable)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "This item can't be bought on the marketboard.");
            return;
        }

        DrawActions(item);
        DrawPriceSummary(item);
        ImGui.Spacing();

        if (item.MarketDataResponse is { } response)
            _listings.Draw(response);
    }

    private static void DrawHeader(ShoppingListItem item)
    {
        var iconSize = new Vector2(40 * ImGuiHelpers.GlobalScale);
        if (item.ItemRecord is { } record)
        {
            var icon = Svc.Texture.GetFromGameIcon(new GameIconLookup(record.Icon)).GetWrapOrEmpty();
            ImGui.Image(icon.Handle, iconSize);
            ImGui.SameLine();
        }

        using (ImRaii.Group())
        {
            if (ImGui.Selectable(item.Name, false, ImGuiSelectableFlags.None, ImGui.CalcTextSize(item.Name)))
            {
                var link = new SeStringBuilder().AddText("[Supermarket Sweep] ").AddItemLink(item.ItemId).BuiltString;
                Svc.Chat.Print(link);
            }

            ImGuiEx.Tooltip("Click to print the item link in chat");
            ImGui.TextDisabled($"Item #{item.ItemId}");
        }
    }

    private void DrawQuantities(ShoppingListItem item)
    {
        var quantity = (int)item.Quantity;
        ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Needed", ref quantity))
        {
            item.Quantity = Math.Max(0, quantity);
            manager.SaveList();
        }

        ImGui.SameLine(0, 20 * ImGuiHelpers.GlobalScale);
        ImGui.AlignTextToFramePadding();
        var owned = item.InventoryCount;
        ImGui.TextColored(owned >= item.Quantity ? ImGuiColors.ParsedBlue : ImGuiColors.DalamudWhite,
            $"Owned: {owned}");
        ImGuiEx.Tooltip((SupermarketSweep.Config.AllCharactersInventory
                            ? "Amount of this item you have across all characters (including retainers and alts)"
                            : "Amount of this item this character has (including its retainers)")
                        + "\nSourced from Allagan Tools");

        ImGui.SameLine(0, 20 * ImGuiHelpers.GlobalScale);
        var stillNeeded = Math.Max(0, item.Quantity - owned);
        ImGui.TextDisabled(stillNeeded == 0 ? "Got enough!" : $"Still need {stillNeeded}");
    }

    private static unsafe void DrawActions(ShoppingListItem item)
    {
        var addon = (AddonItemSearch*)(nint)Svc.GameGui.GetAddonByName("ItemSearch");
        using (ImRaii.Disabled(addon == null))
        {
            if (ImGui.Button("Search Marketboard"))
            {
                addon->SearchTextInput->SetText(item.Name);
                addon->RunSearch();
            }
        }

        ImGuiEx.Tooltip(addon == null
            ? "Open the marketboard first, then this searches it for this item"
            : "Search the open marketboard for this item");

        ImGui.SameLine();
        using (ImRaii.Disabled(item.IsFetchingData))
        {
            if (ImGui.Button("Refresh Prices"))
                item.RefreshMarketData();
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        if (item.IsFetchingData)
            ImGui.TextDisabled(item.Retries > 0 ? $"Fetching... (retry {item.Retries})" : "Fetching...");
        else if (item.MarketDataRegion is { } region && region != SupermarketSweep.Config.ShoppingRegion)
            ImGui.TextColored(ImGuiColors.DalamudOrange,
                $"Prices are for {region.ToFriendlyString()}, refresh for the current region");
        else if (item.MarketDataFetchedAt is { } fetchedAt)
            ImGui.TextDisabled($"Updated {UiHelpers.FormatAge(DateTime.Now - fetchedAt)}");
        else
            ImGui.TextDisabled("No price data yet");
    }

    private static void DrawPriceSummary(ShoppingListItem item)
    {
        if (item.MarketDataResponse is not { } response)
            return;

        var cheapest = response.Listings.MinBy(l => l.PricePerUnit);
        if (cheapest is null)
        {
            ImGui.TextDisabled("Nobody is selling this right now.");
            return;
        }

        ImGui.Text($"Cheapest: {UiHelpers.Gil(cheapest.PricePerUnit)} gil each on {cheapest.WorldName}");
        ImGui.SameLine();
        ImGui.TextDisabled($"| {response.Listings.Count} listings | avg sold {UiHelpers.Gil((long)response.AveragePrice)}");
    }
}
