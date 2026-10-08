using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;

namespace SupermarketSweep.UI;

/// <summary>
/// Item search box plus its result list. Results stay after a click so several items can be added in a row;
/// clicking something already on the list bumps its quantity instead of adding a duplicate.
/// </summary>
public class SearchPanel(SupermarketSweep manager)
{
    // Drawing tens of thousands of Selectables for a one-letter query tanks the frame rate.
    private const int MaxShownResults = 200;

    private string _searchTerm = string.Empty;

    public void Draw()
    {
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X);
        ImGui.InputTextWithHint("##searchBar", "Search items to add... (words in any order, e.g. 'courtly fending')",
            ref _searchTerm, 100);
        ImGui.SameLine();
        if (ImGuiComponents.IconButton("##clearSearch", FontAwesomeIcon.Times))
            _searchTerm = string.Empty;
        ImGuiEx.Tooltip("Clear search");

        if (string.IsNullOrWhiteSpace(_searchTerm))
            return;

        using (ImRaii.Child("SearchResults",
                   new Vector2(0, SupermarketSweep.Config.SearchListHeight * ImGuiHelpers.GlobalScale), true))
            DrawResults();

        var height = SupermarketSweep.Config.SearchListHeight;
        if (UiHelpers.DrawSplitter("##searchListResize", false, ref height, 80, 1000))
            SupermarketSweep.Config.SearchListHeight = height;
    }

    private void DrawResults()
    {
        var matchingItems = SupermarketSweep.ItemSearch.Search(_searchTerm);
        if (matchingItems.Count == 0)
            ImGui.TextDisabled("No items found.");

        foreach (var item in matchingItems.Take(MaxShownResults))
        {
            using var id = ImRaii.PushId((int)item.RowId);
            var existing = manager.WantedItems.FirstOrDefault(w => w.ItemId == item.RowId);
            var label = existing is null ? item.Name.ToString() : $"{item.Name} (in list: {existing.Quantity})";

            bool clicked;
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen, existing is not null))
                clicked = ImGui.Selectable(label);

            if (clicked)
            {
                manager.AddItem(item, 1);
                Svc.Log.Debug($"Added shopping list item: {item.Name}");
            }
        }

        if (matchingItems.Count > MaxShownResults)
            ImGui.TextDisabled($"...and {matchingItems.Count - MaxShownResults} more. Add more words to narrow it down.");
    }
}
