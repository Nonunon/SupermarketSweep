using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using ECommons.ImGuiMethods;
using SupermarketSweep.Models;

namespace SupermarketSweep.UI;

/// <summary>
/// The left-hand shopping list. Each row: price status dot, name, [owned/needed]. Rows turn blue once you own
/// enough. Right-click a row for refresh/remove.
/// </summary>
public class ShoppingListPanel(SupermarketSweep manager)
{
    private string _filter = string.Empty;

    public ShoppingListItem? Selected { get; set; }

    public void Draw(float width)
    {
        using var group = ImRaii.Group();

        ImGui.SetNextItemWidth(width);
        ImGui.InputTextWithHint("##listFilter", "Filter list...", ref _filter, 100);

        var footerHeight = ImGui.GetFrameHeightWithSpacing();
        using (ImRaii.Child("ShoppingList", new Vector2(width, -footerHeight), true))
        {
            if (manager.WantedItems.Count == 0)
                ImGui.TextWrapped("Your list is empty. Search above to add items.");

            // Copy: the context menu can remove the row we're iterating over.
            foreach (var item in manager.WantedItems.ToList())
            {
                if (_filter.Length != 0 && !ItemSearch.Matches(item.Name, _filter))
                    continue;
                DrawRow(item);
            }
        }

        DrawFooter(width);
    }

    private void DrawRow(ShoppingListItem item)
    {
        using var id = ImRaii.PushId((int)item.ItemId);

        var (dotColor, dotTooltip) = UiHelpers.Describe(UiHelpers.GetPriceStatus(item));
        UiHelpers.StatusDot(dotColor);
        ImGuiEx.Tooltip(dotTooltip);
        ImGui.SameLine(0, 2);

        var owned = item.InventoryCount;
        var done = owned >= item.Quantity;
        var counter = $"[{owned}/{item.Quantity}]";
        var counterWidth = ImGui.CalcTextSize(counter).X;

        // Empty selectable for the hit box and highlight; text is drawn by hand so a long name gets clipped
        // before it runs under the counter.
        var clicked = ImGui.Selectable("##row", Selected == item);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var counterX = max.X - counterWidth - ImGui.GetStyle().FramePadding.X;
        var textColor = ImGui.GetColorU32(done ? ImGuiColors.ParsedBlue : ImGuiColors.DalamudWhite);

        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(min, new Vector2(counterX - 4, max.Y), true);
        drawList.AddText(min, textColor, item.Name);
        drawList.PopClipRect();
        drawList.AddText(new Vector2(counterX, min.Y),
            ImGui.GetColorU32(done ? ImGuiColors.ParsedBlue : ImGuiColors.DalamudGrey), counter);
        if (ImGui.IsItemHovered() && ImGui.CalcTextSize(item.Name).X > counterX - 4 - min.X)
            ImGui.SetTooltip(item.Name);

        if (clicked)
            Selected = item;

        using var popup = ImRaii.ContextPopupItem("rowMenu");
        if (!popup)
            return;

        if (ImGui.MenuItem("Refresh Prices", string.Empty, false, item.IsMarketable && !item.IsFetchingData))
            item.RefreshMarketData();
        if (ImGui.MenuItem("Remove"))
            Remove(item);
    }

    private void DrawFooter(float width)
    {
        var done = manager.WantedItems.Count(i => i.InventoryCount >= i.Quantity);
        if (ImGuiComponents.IconButton("##removeSelected", FontAwesomeIcon.Trash) && Selected is not null)
            Remove(Selected);
        ImGuiEx.Tooltip("Remove the selected item (or right-click any row)");

        ImGui.SameLine();
        if (ImGuiComponents.IconButton("##removeDone", FontAwesomeIcon.Broom) && done > 0)
        {
            foreach (var item in manager.WantedItems.Where(i => i.InventoryCount >= i.Quantity).ToList())
                Remove(item, save: false);
            manager.SaveList();
        }

        ImGuiEx.Tooltip($"Remove everything you already own enough of ({done})");

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled($"{done}/{manager.WantedItems.Count} done");
    }

    private void Remove(ShoppingListItem item, bool save = true)
    {
        if (Selected == item)
            Selected = null;
        if (save)
            manager.RemoveItem(item);
        else
            manager.WantedItems.Remove(item);
    }
}
