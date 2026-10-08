using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using SupermarketSweep.Models;

namespace SupermarketSweep.UI;

/// <summary>
/// Shows up while the marketboard is open. Outlines the item to pick in the search results, then the listings to buy
/// in the listings window (checked live against the route's prices), and lists what's left to buy on this world.
/// It only reads and draws: every click and purchase confirmation stays with the player.
/// </summary>
public class BuyAssistWindow : Window
{
    private static readonly Vector4 BuyColor = ImGuiColors.HealerGreen;
    private static readonly Vector4 PickColor = ImGuiColors.DalamudYellow;

    private readonly SupermarketSweep _manager;

    // Advice only changes when the board, the need or the route does; recomputing it every frame would be wasted work.
    private string _adviceKey = string.Empty;
    private BuyAdvice? _advice;

    private static readonly TimeSpan ListingsSettleTime = TimeSpan.FromMilliseconds(500);
    private string _listingsKey = string.Empty;
    private DateTime _listingsChangedAt = DateTime.MinValue;

    public BuyAssistWindow(SupermarketSweep manager)
        : base("Buy Assistant###SupermarketSweepBuyAssist", ImGuiWindowFlags.NoFocusOnAppearing)
    {
        _manager = manager;
        IsOpen = true;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        Size = new Vector2(440, 360);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override bool DrawConditions() => SupermarketSweep.Config.BuyAssistEnabled
                                             && (UiHelpers.IsMarketboardOpen || MarketboardReader.IsListingsOpen);

    public override void Draw()
    {
        var route = _manager.MainWindow.Route;
        route.UpdatePlan();
        var plan = route.Plan;
        var world = Player.Available ? Player.CurrentWorldName : null;

        if (MarketboardReader.IsListingsOpen)
        {
            DrawListings(plan, world);
            ImGui.Separator();
        }
        else
        {
            HighlightSearchResults();
        }

        DrawShoppingHere(plan, world);
    }

    private void DrawListings(RoutePlan? plan, string? world)
    {
        if (MarketboardReader.ReadListings() is not { } read)
        {
            ImGui.TextDisabled("Loading listings...");
            return;
        }

        // Listings arrive a page at a time; only judge them once they've stopped changing for a moment.
        var listingsKey = $"{read.ItemId}|{string.Join(",", read.Listings)}";
        if (listingsKey != _listingsKey)
        {
            _listingsKey = listingsKey;
            _listingsChangedAt = DateTime.Now;
        }

        if (DateTime.Now - _listingsChangedAt < ListingsSettleTime)
        {
            ImGui.TextDisabled($"Loading listings... ({read.Listings.Count})");
            return;
        }

        var item = _manager.WantedItems.FirstOrDefault(i => i.ItemId == read.ItemId);
        if (item is null)
        {
            var name = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(read.ItemId)?.Name.ToString() ?? $"Item #{read.ItemId}";
            ImGui.TextDisabled($"{name} isn't on your shopping list.");
            return;
        }

        var config = SupermarketSweep.Config;
        var stillNeeded = item.StillNeeded;
        var caps = BuyAdvisor.Caps(plan, item, world, config.BuyAssistMaxOverPercent);
        var plannedHere = BuyAdvisor.PlannedHere(plan, item, world);
        // Aim for what the route buys on this world, not the whole need: it found the rest cheaper elsewhere.
        var target = Math.Min(stillNeeded, plannedHere.Sum(l => l.Quantity));
        var key = $"{read.ItemId}|{item.EffectiveQuality}|{target}|{caps}|{config.RouteAllowOverbuy}|" +
                  string.Join(",", plannedHere.Select(l => $"{l.PricePerUnit}x{l.Quantity}{l.Hq}")) + "|" +
                  string.Join(",", read.Listings);
        if (key != _adviceKey || _advice is null)
        {
            _advice = BuyAdvisor.Advise(read.Listings, item.EffectiveQuality, target, caps, plannedHere,
                config.RouteAllowOverbuy);
            _adviceKey = key;
        }

        var advice = _advice;

        ImGui.Text(item.Name);
        ImGui.SameLine();
        ImGui.TextDisabled($"still need {stillNeeded}, {item.EffectiveQuality.ToFriendlyString()}");
        if (stillNeeded > 0 && target < stillNeeded)
        {
            var elsewhere = plan?.Stops.Where(s => s.World != world && s.Purchases.Any(p => p.Item.ItemId == item.ItemId))
                .Select(s => s.World).ToList() ?? [];
            ImGui.TextColored(ImGuiColors.DalamudYellow, target > 0
                ? $"The route buys {target} here; the rest on {WorldList(elsewhere)}."
                : $"The route buys this on {WorldList(elsewhere)}, not here.");
        }

        if (read.Listings.Count == 0)
        {
            ImGui.TextDisabled("No listings on this world.");
            return;
        }

        DrawCaps(caps, target);
        DrawVerdictSummary(advice, caps, stillNeeded, target);
        DrawListingTable(read.Listings, advice);
        HighlightListings(read.Listings, advice);
    }

    private static void DrawCaps(PriceCaps? caps, long stillNeeded)
    {
        if (stillNeeded <= 0)
            return;
        if (caps is null)
        {
            ImGui.TextColored(ImGuiColors.DalamudOrange, "The route doesn't buy this item, so there's no price to check against.");
            ImGuiEx.Tooltip("Pull prices and open the Route tab, or check the item's quality setting.");
            return;
        }

        static string Cap(double? value) => value is { } v ? UiHelpers.Gil((long)Math.Floor(v)) : "not wanted";
        ImGui.TextDisabled(caps.Hq == caps.Nq
            ? $"Max per unit (with tax): {Cap(caps.Hq)}"
            : $"Max per unit (with tax): HQ {Cap(caps.Hq)}, NQ {Cap(caps.Nq)}");
        ImGuiEx.Tooltip($"The route's {(caps.FromThisWorld ? "planned price on this world" : "planned price elsewhere (it planned none here)")} " +
                        $"+{SupermarketSweep.Config.BuyAssistMaxOverPercent:0}% (setting).");
    }

    private static string WorldList(List<string> worlds) => worlds.Count == 0 ? "other worlds" : string.Join(", ", worlds);

    private static unsafe void DrawVerdictSummary(BuyAdvice advice, PriceCaps? caps, long stillNeeded, long target)
    {
        if (stillNeeded <= 0)
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, "Got them all!");
            return;
        }

        if (target <= 0)
            return;

        var inventory = InventoryManager.Instance();
        var gil = inventory == null ? 0 : inventory->GetGil();
        var freeSlots = inventory == null ? 0 : inventory->GetEmptySlotsInBag();

        var buying = advice.Verdicts.Count(v => v == ListingVerdict.Buy);
        if (buying > 0)
            ImGui.TextColored(BuyColor, $"Buy the {buying} outlined listing(s): {advice.BuyQuantity} for {UiHelpers.Gil(advice.BuyCost)} gil");
        else if (caps is not null)
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Nothing here fits the price and quality rules.");

        if (buying > 0 && advice.Short > 0)
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"Still {advice.Short} short after that.");
        if (advice.BuyCost > gil)
            ImGui.TextColored(ImGuiColors.DalamudRed, $"Not enough gil: you have {UiHelpers.Gil(gil)}.");
        if (buying > 0 && freeSlots == 0)
            ImGui.TextColored(ImGuiColors.DalamudOrange, "Your bags are full; purchases only work if they stack onto what you have.");
    }

    private static void DrawListingTable(List<LiveListing> listings, BuyAdvice advice)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY
                                      | ImGuiTableFlags.SizingStretchProp;
        // The game doesn't hand over retainer names with the listings, so only show the column if it ever does.
        var showRetainer = listings.Any(l => l.Retainer.Length > 0);
        var height = Math.Min(listings.Count + 1, 8) * ImGui.GetFrameHeightWithSpacing();
        using var table = ImRaii.Table("##liveListings", showRetainer ? 5 : 4, flags, new Vector2(0, height));
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        if (showRetainer)
            ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthStretch, 0.7f);
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableHeadersRow();

        for (var i = 0; i < listings.Count; i++)
        {
            var listing = listings[i];
            var (color, label) = Describe(advice.Verdicts[i]);
            if (advice.FromRoute[i])
                label = "Buy (route's pick)";
            ImGui.TableNextRow();
            if (showRetainer)
            {
                ImGui.TableNextColumn();
                ImGui.TextColored(color, listing.Retainer);
            }

            // Same columns as the game's window: price before tax, total with tax.
            ImGui.TableNextColumn();
            ImGui.TextColored(color, UiHelpers.Gil(listing.UnitPrice));
            ImGui.TableNextColumn();
            ImGui.TextColored(color, listing.Hq ? $"{listing.Quantity} HQ" : listing.Quantity.ToString());
            ImGui.TableNextColumn();
            ImGui.TextColored(color, UiHelpers.Gil(listing.Cost));
            ImGui.TableNextColumn();
            ImGui.TextColored(color, label);
        }
    }

    private static (Vector4 Color, string Label) Describe(ListingVerdict verdict) => verdict switch
    {
        ListingVerdict.Buy => (BuyColor, "Buy"),
        ListingVerdict.Fine => (ImGuiColors.DalamudWhite, "OK, not needed"),
        ListingVerdict.TooExpensive => (ImGuiColors.DalamudGrey, "Too expensive"),
        ListingVerdict.WrongQuality => (ImGuiColors.DalamudGrey, "Wrong quality"),
        ListingVerdict.TooBig => (ImGuiColors.DalamudGrey, "Too many"),
        ListingVerdict.Yours => (ImGuiColors.DalamudGrey, "Your retainer"),
        _ => (ImGuiColors.DalamudGrey, ""),
    };

    /// <summary>Outlines the game's listing rows that match a recommended listing; says so if some are scrolled away.</summary>
    private static void HighlightListings(List<LiveListing> listings, BuyAdvice advice)
    {
        // Identical listings can exist, so each recommended listing claims at most one row.
        var toFind = listings.Where((_, i) => advice.Verdicts[i] == ListingVerdict.Buy).ToList();
        if (toFind.Count == 0)
            return;

        var (rows, bounds) = MarketboardReader.ReadListingRows();
        var outlined = 0;
        foreach (var row in rows)
        {
            var match = toFind.FindIndex(l => Shows(row, l));
            if (match < 0 || !Outline(row, bounds, BuyColor))
                continue;
            toFind.RemoveAt(match);
            outlined++;
        }

        if (toFind.Count > 0)
            ImGui.TextColored(ImGuiColors.DalamudYellow,
                $"{toFind.Count} more to buy {(outlined > 0 ? "aren't on screen" : "not on screen")}: scroll the listings.");
    }

    /// <summary>
    /// Whether a row on screen is this listing. The board shows price and total with or without tax depending on its
    /// "Display price with fee included" box, so either version counts. Retainer names only count when both sides have one.
    /// </summary>
    private static bool Shows(ListingRow row, LiveListing listing)
    {
        if (row.Quantity != listing.Quantity || row.Hq != listing.Hq)
            return false;
        var priceOk = row.Price == listing.UnitPrice || row.Price == (long)Math.Floor(listing.UnitCost)
                      || row.Price == (long)Math.Ceiling(listing.UnitCost);
        var totalOk = row.Total == listing.UnitPrice * listing.Quantity || row.Total == listing.Cost;
        var retainerOk = row.Retainer.Length == 0 || listing.Retainer.Length == 0 || row.Retainer == listing.Retainer;
        return priceOk && totalOk && retainerOk;
    }

    /// <summary>Outlines search results that are items still needed from the list.</summary>
    private void HighlightSearchResults()
    {
        var needed = _manager.WantedItems.Where(i => i.StillNeeded > 0)
            .Select(i => i.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (rows, bounds) = MarketboardReader.ReadSearchRows();
        foreach (var row in rows.Where(r => needed.Contains(r.Name)))
            Outline(row, bounds, PickColor);
    }

    /// <summary>This world's stop from the route, each item clickable to search the marketboard.</summary>
    private static void DrawShoppingHere(RoutePlan? plan, string? world)
    {
        var stop = plan?.Stops.FirstOrDefault(s => s.World == world);
        if (stop is null)
        {
            ImGui.TextDisabled(plan is null ? "No route planned yet (open the Route tab)." : $"The route buys nothing on {world ?? "this world"}.");
            return;
        }

        ImGui.Text($"To buy on {stop.World}:");
        ImGui.SameLine();
        ImGui.TextDisabled("(click to search)");
        foreach (var group in stop.Purchases.GroupBy(p => p.Item))
        {
            var quantity = group.Sum(p => p.Listing.Quantity);
            var hq = group.Where(p => p.Listing.Hq).Sum(p => p.Listing.Quantity);
            var label = hq == 0 ? $"{quantity}" : hq == quantity ? $"{quantity} HQ" : $"{hq} HQ + {quantity - hq} NQ";
            using var id = ImRaii.PushId((int)group.Key.ItemId);
            if (ImGui.Selectable($"{group.Key.Name}  ({label})") && !UiHelpers.SearchMarketboard(group.Key.Name))
                Svc.Chat.PrintError("[Supermarket Sweep] Open the marketboard search first.");
        }
    }

    /// <summary>
    /// Outlines a game row on ImGui's foreground layer (the background layer didn't show in-game). Rows whose middle
    /// lies outside the list (scrolled half out) are skipped rather than clipped, since the list's own size may read 0.
    /// Returns false if the row was skipped.
    /// </summary>
    private static bool Outline(ScreenRow row, ScreenRow? list, Vector4 color)
    {
        var middle = (row.Min + row.Max) / 2;
        if (list is not null && list.Max.X > list.Min.X && list.Max.Y > list.Min.Y
            && (middle.Y < list.Min.Y || middle.Y > list.Max.Y))
            return false;

        // Inset a little so outlines on neighbouring rows don't merge into one thick line.
        var inset = new Vector2(2, 1.5f) * ImGuiHelpers.GlobalScale;
        var min = row.Min + ImGuiHelpers.MainViewport.Pos + inset;
        var max = row.Max + ImGuiHelpers.MainViewport.Pos - inset;
        var drawList = ImGui.GetForegroundDrawList(ImGuiHelpers.MainViewport);
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(color with { W = 0.15f }), 4);
        drawList.AddRect(min, max, ImGui.GetColorU32(color), 4, ImDrawFlags.None, 2 * ImGuiHelpers.GlobalScale);
        return true;
    }
}
