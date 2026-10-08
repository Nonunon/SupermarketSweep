using System.Numerics;
using ECommons;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SupermarketSweep;

/// <summary>A row on screen in one of the marketboard's lists, in game screen pixels.</summary>
public record ScreenRow(Vector2 Min, Vector2 Max);

/// <summary>
/// A visible row of the listings window and what it shows. <paramref name="Index"/> is its position in the list, which
/// is what the window's callback takes. Price and total may or may not include tax, depending on the window's
/// "Display price with fee included" box.
/// </summary>
public record ListingRow(int Index, Vector2 Min, Vector2 Max, long Price, long Quantity, long Total, bool Hq,
    string Retainer) : ScreenRow(Min, Max)
{
    /// <summary>
    /// Whether this row is <paramref name="listing"/>. Price and total count with or without tax; retainer names only
    /// count when both sides have one (the game's listing data doesn't carry them).
    /// </summary>
    public bool Shows(LiveListing listing)
    {
        if (Quantity != listing.Quantity || Hq != listing.Hq)
            return false;
        var priceOk = Price == listing.UnitPrice || Price == (long)Math.Floor(listing.UnitCost)
                      || Price == (long)Math.Ceiling(listing.UnitCost);
        var totalOk = Total == listing.UnitPrice * listing.Quantity || Total == listing.Cost;
        var retainerOk = Retainer.Length == 0 || listing.Retainer.Length == 0 || Retainer == listing.Retainer;
        return priceOk && totalOk && retainerOk;
    }
}

/// <summary>A visible row of the search results list, its position in the list and its item name.</summary>
public record SearchRow(int Index, Vector2 Min, Vector2 Max, string Name) : ScreenRow(Min, Max);

/// <summary>
/// Read-only access to the open marketboard: the live listings and where rows sit on screen. Main thread only.
/// Nothing here clicks or sends anything (that's <see cref="MarketboardBuyer"/>).
/// </summary>
public static unsafe class MarketboardReader
{
    // Node ids inside a listings row, as used by ECommons' AddonMaster.ItemSearchResult.
    private const uint HqImageNode = 3;
    private const uint PriceTextNode = 5;
    private const uint QuantityTextNode = 6;
    private const uint TotalTextNode = 8;
    private const uint RetainerTextNode = 10;

    public static bool IsListingsOpen => GetReady<AddonItemSearchResult>("ItemSearchResult") != null;

    /// <summary>The named addon if it's open and ready for input, else null.</summary>
    public static AtkUnitBase* GetReadyAddon(string name) => GetReady<AtkUnitBase>(name);

    /// <summary>
    /// The item the listings window is for and the listings received for it so far (null if none is open).
    /// Pages arrive over several frames, so callers should wait for this to settle before trusting it.
    /// <c>InfoProxyItemSearch.WaitingForListings</c> isn't used: in-game it stayed set after everything had arrived.
    /// </summary>
    public static (uint ItemId, List<LiveListing> Listings)? ReadListings()
    {
        var proxy = InfoProxyItemSearch.Instance();
        if (proxy == null || proxy->SearchItemId == 0)
            return null;

        var yours = new HashSet<ulong>();
        var retainers = proxy->PlayerRetainers;
        for (var i = 0; i < Math.Min((int)proxy->PlayerRetainerCount, retainers.Length); i++)
            yours.Add(retainers[i].RetainerId);

        var listings = proxy->Listings;
        var count = Math.Min((int)proxy->ListingCount, listings.Length);
        // Right after switching items the array can still hold the previous item's listings. If the listings carry
        // item ids at all, keep only the open item's (if none do, the field isn't filled and can't help).
        var filterById = false;
        for (var i = 0; i < count && !filterById; i++)
            filterById = listings[i].ItemId != 0;

        var result = new List<LiveListing>(count);
        for (var i = 0; i < count; i++)
        {
            ref var listing = ref listings[i];
            if (listing.Quantity == 0 || (filterById && listing.ItemId != proxy->SearchItemId))
                continue;
            // CharacterName came back empty in-game; it isn't the retainer name. Kept in case it's filled sometimes.
            result.Add(new LiveListing(listing.CharacterName.ToString(), listing.UnitPrice, listing.Quantity,
                listing.TotalTax, listing.IsHqItem, yours.Contains(listing.RetainerId)));
        }

        return (proxy->SearchItemId, result);
    }

    /// <summary>Rows currently visible in the listings window, plus the list's own bounds to clip drawing to.</summary>
    public static (List<ListingRow> Rows, ScreenRow? Bounds) ReadListingRows()
    {
        var addon = GetReady<AddonItemSearchResult>("ItemSearchResult");
        if (addon == null || addon->Results == null)
            return ([], null);

        var rows = new List<ListingRow>();
        var scale = addon->AtkUnitBase.Scale;
        foreach (var (index, renderer, min, max) in VisibleRows(addon->Results, scale))
        {
            var component = ((AtkComponentListItemRenderer*)renderer)->ComponentNode->Component;
            var hqNode = component->GetImageNodeById(HqImageNode);
            rows.Add(new ListingRow(index, min, max,
                Digits(Text(component, PriceTextNode)),
                Digits(Text(component, QuantityTextNode)),
                Digits(Text(component, TotalTextNode)),
                hqNode != null && hqNode->IsVisible(),
                Text(component, RetainerTextNode)));
        }

        return (rows, Bounds(addon->Results, scale));
    }

    /// <summary>Rows currently visible in the search results list (the item names), plus the list's bounds.</summary>
    public static (List<SearchRow> Rows, ScreenRow? Bounds) ReadSearchRows()
    {
        var addon = GetReady<AddonItemSearch>("ItemSearch");
        if (addon == null || addon->ResultsList == null)
            return ([], null);

        var rows = new List<SearchRow>();
        var scale = addon->AtkUnitBase.Scale;
        foreach (var (index, renderer, min, max) in VisibleRows(addon->ResultsList, scale))
        {
            // The name's node id isn't documented, so take the longest text in the row.
            var component = ((AtkComponentListItemRenderer*)renderer)->ComponentNode->Component;
            var name = string.Empty;
            for (var i = 0; i < component->UldManager.NodeListCount; i++)
            {
                var node = component->UldManager.NodeList[i];
                if (node == null || node->Type != NodeType.Text || !node->IsVisible())
                    continue;
                var text = GenericHelpers.ReadSeString(&node->GetAsAtkTextNode()->NodeText).TextValue.Trim();
                if (text.Length > name.Length)
                    name = text;
            }

            rows.Add(new SearchRow(index, min, max, name));
        }

        return (rows, Bounds(addon->ResultsList, scale));
    }

    private static T* GetReady<T>(string name) where T : unmanaged =>
        GenericHelpers.TryGetAddonByName<T>(name, out var addon) && GenericHelpers.IsAddonReady((AtkUnitBase*)addon)
            ? addon
            : null;

    private static List<(int Index, nint Renderer, Vector2 Min, Vector2 Max)> VisibleRows(
        AtkComponentList* list, float scale)
    {
        var rows = new List<(int, nint, Vector2, Vector2)>();
        for (var i = 0; i < list->GetItemCount(); i++)
        {
            if (!list->IsItemVisible(i, true))
                continue;
            var renderer = list->GetItemRenderer(i);
            if (renderer == null || renderer->ComponentNode == null || renderer->ComponentNode->Component == null)
                continue;
            var node = (AtkResNode*)renderer->ComponentNode;
            if (!node->IsVisible())
                continue;
            var (min, max) = Rect(node, scale);
            rows.Add((i, (nint)renderer, min, max));
        }

        return rows;
    }

    private static ScreenRow? Bounds(AtkComponentList* list, float scale)
    {
        if (list->OwnerNode == null)
            return null;
        var (min, max) = Rect((AtkResNode*)list->OwnerNode, scale);
        return new ScreenRow(min, max);
    }

    private static (Vector2 Min, Vector2 Max) Rect(AtkResNode* node, float scale)
    {
        var min = new Vector2(node->ScreenX, node->ScreenY);
        return (min, min + new Vector2(node->Width, node->Height) * scale);
    }

    private static string Text(AtkComponentBase* component, uint nodeId)
    {
        var node = component->GetTextNodeById(nodeId);
        var text = node == null ? null : node->GetAsAtkTextNode();
        return text == null ? string.Empty : GenericHelpers.ReadSeString(&text->NodeText).TextValue.Trim();
    }

    // "1,234 gil" -> 1234. Separators vary by client language, so keep only the digits.
    private static long Digits(string text)
    {
        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        return long.TryParse(digits, out var value) ? value : -1;
    }
}
