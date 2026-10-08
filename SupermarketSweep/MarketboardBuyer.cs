using Dalamud.Plugin.Services;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using SupermarketSweep.Models;
using SupermarketSweep.UI;

namespace SupermarketSweep;

/// <summary>
/// Buys at the open marketboard for the buy assistant's automation levels (<see cref="Config.BuyAutomation"/>).
/// A step machine run from Framework.Update, so everything happens on the main thread, one game action per step:
///
/// open the item (search, then click its search result: <c>ItemSearch [5, row]</c>) → wait for its listings to settle
/// → pick the first recommended listing (<see cref="BuyAdvisor"/>) and check its row on screen → click it
/// (<c>ItemSearchResult [2, row]</c>) → check the <c>SelectYesno</c> text names the item and the price → press Yes (or
/// let the player) → wait for the gil to drop → wait for the refreshed listings → repeat, then the next item.
///
/// Safety: targets and price caps are frozen when a run starts and progress is counted from gil actually spent (owned
/// counts from Allagan Tools lag, so trusting them mid-run would buy twice). Anything unexpected (a mismatch, a
/// timeout, the board closing, not enough gil, the per-run gil limit) stops the run. Stop() and /shop stop end it.
/// </summary>
public sealed unsafe class MarketboardBuyer : IDisposable
{
    private enum Step
    {
        OpenItem,
        WaitListings,
        PickListing,
        WaitConfirm,
        PressYes,
        WaitForPlayer,
        WaitPurchase,
    }

    private sealed class ItemRun(ShoppingListItem item, long target, PriceCaps? caps, List<MarketDataListing> planned,
        List<MarketDataListing>? elsewhere)
    {
        public ShoppingListItem Item { get; } = item;
        public long Target { get; } = target;
        public PriceCaps? Caps { get; } = caps;
        public List<MarketDataListing> Planned { get; } = planned;

        /// <summary>What the route plans on other worlds, for opportunistic extras (null if that setting is off).</summary>
        public List<MarketDataListing>? Elsewhere { get; } = elsewhere;

        public long Bought { get; set; }
        public long Remaining => Math.Max(0, Target - Bought);

        /// <summary>Bought beyond the target: those units replaced planned units elsewhere.</summary>
        public long Extra => Math.Max(0, Bought - Target);
        public DateTime? SearchedAt { get; set; }
        public bool ForceReopen { get; set; }
        public DateTime? ClosingSince { get; set; }
        public int ScrollTries { get; set; }
        public int Failures { get; set; }
    }

    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(500);

    private readonly Queue<ItemRun> _queue = new();
    private readonly Random _random = new();
    private ItemRun? _current;
    private Step _step;
    private DateTime _notBefore;
    private DateTime _deadline;
    private bool _playerConfirms;

    private string _listingsKey = string.Empty;
    private DateTime _listingsChangedAt;
    private string? _keyBeforePurchase;

    private LiveListing? _pending;
    private DateTime? _dialogSeenAt;
    private long _gilBefore;
    private long _spent;
    private int _listingsBought;

    public MarketboardBuyer() => Svc.Framework.Update += OnUpdate;

    public bool IsRunning { get; private set; }

    /// <summary>What the current run is doing, for the assistant window.</summary>
    public string Status { get; private set; } = string.Empty;

    /// <summary>How the last run ended (null while running or before the first run).</summary>
    public string? LastResult { get; private set; }

    /// <summary>
    /// Starts buying, on this world, what the route plans here for each item (capped by what's still needed).
    /// Call from the framework thread: it reads owned counts.
    /// </summary>
    public void Start(IEnumerable<ShoppingListItem> items, RoutePlan? plan, string? world)
    {
        var config = SupermarketSweep.Config;
        if (IsRunning || config.BuyAutomation == BuyAutomation.OutlineOnly)
            return;

        _queue.Clear();
        foreach (var item in items)
        {
            var planned = BuyAdvisor.PlannedHere(plan, item, world);
            var target = Math.Min(item.StillNeeded, planned.Sum(l => l.Quantity));
            var elsewhere = config.BuyAssistOpportunistic ? BuyAdvisor.PlannedElsewhere(plan, item, world) : null;
            if (target > 0 || elsewhere is { Count: > 0 })
                _queue.Enqueue(new ItemRun(item, target, BuyAdvisor.Caps(plan, item, world, config.BuyAssistMaxOverPercent),
                    planned, elsewhere));
        }

        if (_queue.Count == 0)
        {
            LastResult = "Nothing to buy here.";
            return;
        }

        _playerConfirms = config.BuyAutomation == BuyAutomation.OpenConfirmation;
        _current = null;
        _step = Step.OpenItem;
        _notBefore = DateTime.MinValue;
        _spent = 0;
        _listingsBought = 0;
        LastResult = null;
        IsRunning = true;
        Chat($"Buying {_queue.Count} item(s) on {world}. Stop with the Stop button or /shop stop.");
    }

    public void Stop(string reason, bool problem = true)
    {
        if (!IsRunning)
            return;
        IsRunning = false;
        _queue.Clear();
        _current = null;
        LastResult = $"{reason} Bought {_listingsBought} listing(s) for {UiHelpers.Gil(_spent)} gil.";
        if (problem)
            Svc.Chat.PrintError($"[Supermarket Sweep] {LastResult}");
        else
            Chat(LastResult);
    }

    private void OnUpdate(IFramework framework)
    {
        if (!IsRunning)
            return;
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[Supermarket Sweep] Buying failed");
            Stop($"Error: {ex.Message}");
        }
    }

    private void Tick()
    {
        if (!UiHelpers.IsMarketboardOpen && !MarketboardReader.IsListingsOpen
                                         && MarketboardReader.GetReadyAddon("SelectYesno") == null)
        {
            Stop("The marketboard closed.");
            return;
        }

        var now = DateTime.Now;
        if (now < _notBefore)
            return;

        switch (_step)
        {
            case Step.OpenItem:
                OpenItem(now);
                break;
            case Step.WaitListings:
                WaitListings(now);
                break;
            case Step.PickListing:
                PickListing(now);
                break;
            case Step.WaitConfirm:
                WaitConfirm(now);
                break;
            case Step.PressYes:
                PressYes(now);
                break;
            case Step.WaitForPlayer:
                WaitForPlayer(now);
                break;
            case Step.WaitPurchase:
                WaitPurchase(now);
                break;
        }
    }

    private void OpenItem(DateTime now)
    {
        if (_current is null)
        {
            if (!_queue.TryDequeue(out var next))
            {
                Stop("Done.", problem: false);
                return;
            }

            _current = next;
        }

        var run = _current;
        Status = $"Opening {run.Item.Name}";
        if (!run.ForceReopen && MarketboardReader.IsListingsOpen
                             && MarketboardReader.ReadListings() is { } open && open.ItemId == run.Item.ItemId)
        {
            BeginWaitListings(now, null);
            return;
        }

        // The game ignores a search-result click while another item's listings are open (seen in-game), so close
        // that window first (its X sends [-1]) and wait for it to go.
        var listings = MarketboardReader.GetReadyAddon("ItemSearchResult");
        if (listings != null || Svc.GameGui.GetAddonByName("ItemSearchResult") != nint.Zero)
        {
            run.ClosingSince ??= now;
            if (now - run.ClosingSince > TimeSpan.FromSeconds(5))
            {
                Stop("Couldn't close the previous item's listings.");
                return;
            }

            if (listings != null)
                Callback.Fire(listings, true, -1);
            Delay(0.5);
            return;
        }

        run.ClosingSince = null;
        var search = MarketboardReader.GetReadyAddon("ItemSearch");
        if (search == null)
        {
            Stop("The marketboard search isn't open.");
            return;
        }

        var (rows, _) = MarketboardReader.ReadSearchRows();
        var row = rows.FirstOrDefault(r => string.Equals(r.Name, run.Item.Name, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
        {
            Callback.Fire(search, true, 5, row.Index);
            run.ForceReopen = false;
            BeginWaitListings(now, null);
            Delay();
            return;
        }

        if (run.SearchedAt is null)
        {
            UiHelpers.SearchMarketboard(run.Item.Name);
            run.SearchedAt = now;
            Delay();
        }
        else if (now - run.SearchedAt > TimeSpan.FromSeconds(6))
        {
            Stop($"Couldn't find {run.Item.Name} in the search results.");
        }
    }

    private void BeginWaitListings(DateTime now, string? mustDifferFrom)
    {
        _step = Step.WaitListings;
        _deadline = now + TimeSpan.FromSeconds(10);
        _keyBeforePurchase = mustDifferFrom;
        _listingsKey = string.Empty;
    }

    private void WaitListings(DateTime now)
    {
        var run = _current!;
        Status = $"Waiting for {run.Item.Name} listings";
        if (now > _deadline)
        {
            // After a purchase the list normally refreshes by itself; if it didn't, reopening the item refreshes it.
            if (_keyBeforePurchase is not null && ++run.Failures < 3)
            {
                run.ForceReopen = true;
                _step = Step.OpenItem;
                return;
            }

            Stop($"Listings for {run.Item.Name} didn't load.");
            return;
        }

        if (MarketboardReader.ReadListings() is not { } read || read.ItemId != run.Item.ItemId)
            return;

        var key = string.Join(",", read.Listings);
        if (key == _keyBeforePurchase)
            return; // still the list from before the purchase
        if (key != _listingsKey)
        {
            _listingsKey = key;
            _listingsChangedAt = now;
            return;
        }

        if (now - _listingsChangedAt >= SettleTime)
            _step = Step.PickListing;
    }

    private void PickListing(DateTime now)
    {
        var run = _current!;
        if (MarketboardReader.ReadListings() is not { } read || read.ItemId != run.Item.ItemId)
        {
            BeginWaitListings(now, null);
            return;
        }

        var config = SupermarketSweep.Config;
        var opportunity = run.Elsewhere is null ? null : new Opportunity(run.Elsewhere, run.Extra);
        var advice = BuyAdvisor.Advise(read.Listings, run.Item.EffectiveQuality, run.Remaining, run.Caps, run.Planned,
            config.RouteAllowOverbuy, opportunity);
        var index = Array.IndexOf(advice.Verdicts, ListingVerdict.Buy);
        if (index < 0)
        {
            if (run.Remaining > 0)
                Chat($"{run.Item.Name}: nothing more here fits the price and quality rules ({run.Remaining} short).");
            NextItem();
            return;
        }

        var listing = read.Listings[index];
        var gil = Gil();
        if (listing.Cost > gil)
        {
            Stop($"Not enough gil for {listing.Quantity} {run.Item.Name} ({UiHelpers.Gil(listing.Cost)}).");
            return;
        }

        if (_spent + listing.Cost > config.AutoBuyMaxGilPerRun)
        {
            Stop($"The next buy would pass the {UiHelpers.Gil(config.AutoBuyMaxGilPerRun)} gil per-run limit (setting).");
            return;
        }

        var row = MarketboardReader.ReadListingRows().Rows.FirstOrDefault(r => r.Shows(listing));
        if (row is null)
        {
            // Probably scrolled out of view; the list is in the same order as the listing data.
            if (++run.ScrollTries > 3)
            {
                Stop($"Couldn't find the {listing.Quantity} x {UiHelpers.Gil(listing.UnitPrice)} listing on screen.");
                return;
            }

            var addon = (AddonItemSearchResult*)MarketboardReader.GetReadyAddon("ItemSearchResult");
            if (addon != null && addon->Results != null)
                addon->Results->ScrollToItem((short)index);
            Delay();
            return;
        }

        run.ScrollTries = 0;
        _pending = listing;
        _gilBefore = gil;
        _keyBeforePurchase = string.Join(",", read.Listings);
        Status = $"Buying {listing.Quantity} {run.Item.Name} for {UiHelpers.Gil(listing.Cost)} gil";
        Callback.Fire(MarketboardReader.GetReadyAddon("ItemSearchResult"), true, 2, row.Index);
        _dialogSeenAt = null;
        _step = Step.WaitConfirm;
        _deadline = now + TimeSpan.FromSeconds(5);
    }

    private void WaitConfirm(DateTime now)
    {
        var run = _current!;
        var addon = MarketboardReader.GetReadyAddon("SelectYesno");
        if (addon == null)
        {
            if (now > _deadline)
                Stop("The purchase confirmation didn't appear.");
            return;
        }

        _dialogSeenAt ??= now;
        var dialog = new AddonMaster.SelectYesno(addon);
        var text = dialog.Text;
        if (!ConfirmationMatches(text, run.Item.Name, _pending!))
        {
            // Its text can take a frame or two to fill in; only call it a mismatch after a moment.
            if (now - _dialogSeenAt < TimeSpan.FromSeconds(1))
                return;
            dialog.No();
            Stop($"The confirmation didn't match what was picked, so pressed No: \"{text}\"");
            return;
        }

        if (_playerConfirms)
        {
            Status = "Press Yes to buy (No stops)";
            _step = Step.WaitForPlayer;
            _deadline = now + TimeSpan.FromMinutes(2);
            return;
        }

        _step = Step.PressYes;
        Delay(0.5);
    }

    private void PressYes(DateTime now)
    {
        var addon = MarketboardReader.GetReadyAddon("SelectYesno");
        if (addon == null)
        {
            Stop("The purchase confirmation closed before Yes.");
            return;
        }

        // Don't force a disabled Yes (ECommons would by default): the game disables it for a reason.
        new AddonMaster.SelectYesno(addon) { RespectDisabledButtons = true }.Yes();
        _step = Step.WaitPurchase;
        _deadline = now + TimeSpan.FromSeconds(8);
    }

    private void WaitForPlayer(DateTime now)
    {
        if (MarketboardReader.GetReadyAddon("SelectYesno") != null)
        {
            if (now > _deadline)
                Stop("No answer to the purchase confirmation.");
            return;
        }

        _step = Step.WaitPurchase;
        _deadline = now + TimeSpan.FromSeconds(4);
    }

    private void WaitPurchase(DateTime now)
    {
        var run = _current!;
        var gil = Gil();
        if (gil < _gilBefore)
        {
            var paid = _gilBefore - gil;
            _spent += paid;
            _listingsBought++;
            run.Bought += _pending!.Quantity;
            run.Failures = 0;
            Chat($"Bought {_pending.Quantity} x {run.Item.Name} for {UiHelpers.Gil(paid)} gil.");
            BeginWaitListings(now, _keyBeforePurchase);
            Delay();
            return;
        }

        if (now <= _deadline)
            return;

        if (_playerConfirms)
        {
            Stop("Not bought (No was pressed, or it didn't go through).", problem: false);
            return;
        }

        if (++run.Failures >= 2)
        {
            Stop($"Purchases of {run.Item.Name} didn't go through twice in a row.");
            return;
        }

        Chat("That purchase didn't go through (sold already?). Rechecking the listings.");
        run.ForceReopen = true;
        _step = Step.OpenItem;
        Delay();
    }

    private void NextItem()
    {
        _current = null;
        _step = Step.OpenItem;
        Delay();
    }

    /// <summary>
    /// The dialog must name the item and show the listing's price: its total or unit price, with or without tax (the
    /// exact wording hasn't been seen yet). Whitespace is ignored because long names wrap; prices are compared as
    /// digits only because separators vary by client language. The row was already checked before the click.
    /// </summary>
    private static bool ConfirmationMatches(string text, string itemName, LiveListing listing)
    {
        static string Squash(string s) => new(s.Where(c => !char.IsWhiteSpace(c) && c != '­').ToArray());
        if (text.Length == 0 || !Squash(text).Contains(Squash(itemName), StringComparison.OrdinalIgnoreCase))
            return false;

        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        long[] prices = [listing.Cost, listing.UnitPrice * listing.Quantity, listing.UnitPrice,
            (long)Math.Floor(listing.UnitCost), (long)Math.Ceiling(listing.UnitCost)];
        return prices.Any(p => digits.Contains(p.ToString()));
    }

    /// <summary>Waits the step delay setting (or a fraction of it), give or take 30%, before the next step.</summary>
    private void Delay(double fraction = 1)
    {
        var ms = SupermarketSweep.Config.AutoBuyStepDelayMs * fraction * (0.7 + _random.NextDouble() * 0.6);
        _notBefore = DateTime.Now + TimeSpan.FromMilliseconds(ms);
    }

    private static long Gil()
    {
        var inventory = InventoryManager.Instance();
        return inventory == null ? 0 : inventory->GetGil();
    }

    private static void Chat(string message) => Svc.Chat.Print($"[Supermarket Sweep] {message}");

    public void Dispose()
    {
        Svc.Framework.Update -= OnUpdate;
        IsRunning = false;
    }
}
