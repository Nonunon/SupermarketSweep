using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ECommons.ImGuiMethods;
using SupermarketSweep.Models;

namespace SupermarketSweep.UI;

/// <summary>
/// Sortable table of a response's listings. Click a header to sort, click a world to travel there.
/// The sorted copy is cached and only rebuilt when the data or the sort changes.
/// </summary>
public class ListingsTable(SupermarketSweep manager)
{
    private enum Column
    {
        World,
        Hq,
        Quantity,
        PerItem,
        Total,
    }

    private MarketDataResponse? _sortedFor;
    private Column _sortColumn = Column.PerItem;
    private bool _ascending = true;
    private List<MarketDataListing> _sorted = [];

    public void Draw(MarketDataResponse response)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV
                                      | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable
                                      | ImGuiTableFlags.SizingStretchProp;
        using var table = ImRaii.Table("##listings", 5, flags);
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthStretch, 0.6f);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Per Item", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort, 1.4f);
        ImGui.TableSetupColumn("Total (w/ tax)", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableHeadersRow();

        if (ImGuiEx.TryGetTableSortDirection(out var ascending, out var column, out var dirty) && dirty)
        {
            _sortColumn = (Column)column;
            _ascending = ascending;
            _sortedFor = null;
        }

        if (!ReferenceEquals(_sortedFor, response))
        {
            _sorted = Sort(response.Listings);
            _sortedFor = response;
        }

        foreach (var listing in _sorted)
        {
            using var id = ImRaii.PushId(listing.ListingId ?? listing.GetHashCode().ToString());
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            if (ImGui.Selectable(listing.WorldName, false, ImGuiSelectableFlags.SpanAllColumns))
                manager.TravelToWorld(listing.WorldName);
            ImGuiEx.Tooltip($"Travel to {listing.WorldName} with Lifestream");

            ImGui.TableNextColumn();
            if (listing.Hq)
                ImGui.TextColored(ImGuiColors.DalamudYellow, "HQ");

            ImGui.TableNextColumn();
            ImGui.Text(listing.Quantity.ToString());

            ImGui.TableNextColumn();
            ImGui.Text(UiHelpers.Gil(listing.PricePerUnit));

            ImGui.TableNextColumn();
            ImGui.Text(UiHelpers.Gil(listing.Cost));
        }
    }

    private List<MarketDataListing> Sort(IEnumerable<MarketDataListing> listings)
    {
        Func<MarketDataListing, IComparable> key = _sortColumn switch
        {
            Column.World => l => l.WorldName ?? string.Empty,
            Column.Hq => l => l.Hq,
            Column.Quantity => l => l.Quantity,
            Column.Total => l => l.Cost,
            _ => l => l.PricePerUnit,
        };
        return (_ascending ? listings.OrderBy(key) : listings.OrderByDescending(key))
            .ThenBy(l => l.PricePerUnit)
            .ToList();
    }
}
