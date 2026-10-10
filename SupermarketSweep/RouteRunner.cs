using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using SupermarketSweep.IPC;
using SupermarketSweep.Models;
using SupermarketSweep.UI;

namespace SupermarketSweep;

/// <summary>
/// Runs the route by itself ("Run route" on the Route tab). A step machine on Framework.Update, like
/// <see cref="MarketboardBuyer"/>, which it reuses for the buying at each world:
///
/// pull prices → replan → preflight (the player presses Start) → pick the next unvisited stop → on another world:
/// close the board, travel (Lifestream) and walk to the board; on this one: walk there if needed (or teleport to
/// Limsa first when outside the world-travel cities) → buy that world's stop → wait for owned counts to catch up →
/// pull prices → replan → next stop. A world is never visited twice in one run.
///
/// Movement is Lifestream's and vnavmesh's job: the runner only calls the plugin's travel/walk helpers and watches.
/// It never enqueues its own tasks into the plugin's TaskManager (the walk queues more tasks as it runs, so anything
/// queued after it would run first).
/// </summary>
public sealed unsafe class RouteRunner : IDisposable
{
    private enum Phase
    {
        Idle,
        PullPrices,
        WaitPrices,
        Plan,
        WaitPlan,
        Confirm,
        PickStop,
        CloseBoard,
        WaitBoard,
        Buy,
        WaitBuy,
        SettleCounts,
    }

    /// <summary>Territories the marketboard walk starts from (the world-travel hubs).</summary>
    private static readonly HashSet<uint> HubTerritories = [129, 130, 132];

    /// <summary>What the runner set going in the plugin's TaskManager, if anything.</summary>
    private enum Move
    {
        None,
        Walk,
        Teleport,
        Travel,
    }

    private readonly SupermarketSweep _manager;
    private readonly HashSet<string> _visited = [];
    private readonly List<string> _log = [];
    private Phase _phase;
    private Phase _loggedPhase;
    private DateTime _notBefore;
    private DateTime _deadline;
    private Task<RoutePlan>? _planning;
    private bool _confirmed;
    private WorldStop? _stop;
    private Move _move;
    private DateTime? _walkEndedAt;
    private long _spent;

    private List<ShoppingListItem> _settleItems = [];
    private string _settleKey = string.Empty;
    private DateTime _settleChangedAt;

    public RouteRunner(SupermarketSweep manager)
    {
        _manager = manager;
        Svc.Framework.Update += OnUpdate;
    }

    /// <summary>True from "Run route" until the run ends, including while the preflight waits for Start.</summary>
    public bool IsRunning => _phase != Phase.Idle;

    /// <summary>The preflight is shown and waits for <see cref="Confirm"/> or <see cref="Stop"/>.</summary>
    public bool AwaitingConfirm => _phase == Phase.Confirm;

    public PreflightResult? Preflight { get; private set; }

    /// <summary>
    /// Worlds unticked on the Route tab while the "pick route run stops" debug setting is on: the run skips them.
    /// Kept by world name for the session (plans change after every world).
    /// </summary>
    public HashSet<string> SkippedWorlds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsSkipped(WorldStop stop) =>
        SupermarketSweep.Config.RouteRunPickStops && (SkippedWorlds.Contains(stop.World) || (_pickedAtStart is { } picked && !picked.Contains(stop.World)));

    /// <summary>
    /// With picking on, the worlds ticked when Start was pressed. A replan can bring in worlds that weren't on the
    /// route then; those were never ticked, so the run leaves them out too.
    /// </summary>
    private HashSet<string>? _pickedAtStart;

    /// <summary>The plan without skipped stops, for the preflight and the checks before a run.</summary>
    private RoutePlan Picked(RoutePlan plan) => new()
    {
        Stops = plan.Stops.Where(s => !IsSkipped(s)).ToList(),
        CheapestTotal = plan.CheapestTotal,
        CheapestWorldCount = plan.CheapestWorldCount,
        Unfilled = plan.Unfilled,
        NeedsPrices = plan.NeedsPrices,
    };

    /// <summary>The plan the run follows (replaced after every world).</summary>
    public RoutePlan? Plan { get; private set; }

    public string Status { get; private set; } = string.Empty;

    /// <summary>The world being bought on (or last bought on).</summary>
    public string? CurrentStop => _stop?.World;

    public long Spent => _spent + (_phase == Phase.WaitBuy ? _manager.Buyer.Spent : 0);

    /// <summary>One line per world (and per notable event), for the Route tab.</summary>
    public IReadOnlyList<string> Log => _log;

    /// <summary>How the last run ended (null while running or before the first run).</summary>
    public string? LastResult { get; private set; }

    /// <summary>"Run route": pulls prices and plans, then shows the preflight. Call from the framework thread.</summary>
    public void Begin()
    {
        if (IsRunning || _manager.Buyer.IsRunning)
            return;
        _visited.Clear();
        _log.Clear();
        LeftShort = [];
        _spent = 0;
        _confirmed = false;
        _pickedAtStart = null;
        _stop = null;
        Preflight = null;
        Plan = null;
        LastResult = null;
        _phase = Phase.PullPrices;
    }

    /// <summary>"Start" on the preflight.</summary>
    public void Confirm()
    {
        if (_phase != Phase.Confirm || Preflight is not { CanStart: true })
            return;
        _confirmed = true;
        _pickedAtStart = SupermarketSweep.Config.RouteRunPickStops
            ? Picked(Plan!).Stops.Select(s => s.World).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        Chat("Running the route. Stop with the Stop button or /shop stop.");
        _phase = Phase.PickStop;
    }

    public void Stop(string reason, bool problem = false)
    {
        if (!IsRunning)
            return;
        var wasConfirmed = _confirmed;
        _phase = Phase.Idle;
        _planning = null;
        if (_manager.Buyer.IsRunning)
        {
            _manager.Buyer.Stop("Route run stopped.", BuyOutcome.Stopped);
            _spent += _manager.Buyer.Spent;
        }

        if (_move != Move.None)
        {
            if (_manager.TaskManager.IsBusy)
                _manager.TaskManager.Abort();
            if (Lifestream_IPCSubscriber.IsEnabled && Lifestream_IPCSubscriber.IsBusy())
                Lifestream_IPCSubscriber.Abort();
            if (VNavmesh_IPCSubscriber.IsEnabled && VNavmesh_IPCSubscriber.Path_IsRunning())
                VNavmesh_IPCSubscriber.Path_Stop();
        }

        _move = Move.None;

        LastResult = wasConfirmed ? $"{reason} Spent {UiHelpers.Gil(_spent)} gil." : reason;
        _loggedPhase = Phase.Idle;
        if (!wasConfirmed)
            return; // cancelled at the preflight: nothing happened worth a chat line
        Svc.Log.Information($"Route run ended: {LastResult}");
        if (problem)
            Svc.Chat.PrintError($"[Supermarket Sweep] {LastResult}");
        else
            Svc.Chat.Print($"[Supermarket Sweep] {LastResult}");

        LeftShort = BuildLeftShort();
        if (LeftShort.Count > 0)
            Chat($"Left short: {string.Join("; ", LeftShort.Select(s => $"{s.Item.Name} {s.Units} ({s.Reason})"))}.");
    }

    /// <summary>What the last run left short, item by item with the reason (empty if everything's covered).</summary>
    public IReadOnlyList<(ShoppingListItem Item, long Units, string Reason)> LeftShort { get; private set; } = [];

    // Everything still needed when the run ends, with the reason from the run's last plan. Owned counts can lag a
    // purchase or two when the run was stopped mid-buy (they settle within seconds); the list is a snapshot.
    private List<(ShoppingListItem Item, long Units, string Reason)> BuildLeftShort()
    {
        var plan = Plan;
        var result = new List<(ShoppingListItem, long, string)>();
        foreach (var item in _manager.WantedItems.Where(i => i.IsMarketable))
        {
            var units = item.StillNeeded;
            if (units <= 0)
                continue;

            var plannedOn = plan?.Stops.Where(s => s.Purchases.Any(p => p.Item == item)).Select(s => s.World).ToList() ?? [];
            var reason = plan is null ? "no plan"
                : plan.NeedsPrices.Contains(item) ? "no prices"
                : plannedOn.Count > 0 ? $"still planned on {string.Join(", ", plannedOn)}"
                : plan.NotWorthTheTrip.Any(n => n.Item == item) ? "only on other worlds, too little to be worth the trip"
                : plan.Unfilled.Any(u => u.Item == item) ? "not enough listings"
                : "nothing fit the price and quality rules";
            result.Add((item, units, reason));
        }

        return result;
    }

    private void OnUpdate(IFramework framework)
    {
        if (!IsRunning || DateTime.Now < _notBefore)
            return;
        try
        {
            Tick(DateTime.Now);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Route run failed");
            Stop($"Error: {ex.Message}", problem: true);
        }
    }

    private void Tick(DateTime now)
    {
        if (_phase != _loggedPhase)
        {
            Svc.Log.Information($"Route run: {_loggedPhase} -> {_phase}" + (_stop is null ? "" : $" ({_stop.World})"));
            _loggedPhase = _phase;
        }

        switch (_phase)
        {
            case Phase.PullPrices:
                PullPrices(now);
                break;
            case Phase.WaitPrices:
                WaitPrices(now);
                break;
            case Phase.Plan:
                StartPlan();
                break;
            case Phase.WaitPlan:
                WaitPlan();
                break;
            case Phase.Confirm:
                Status = "Waiting for Start";
                break;
            case Phase.PickStop:
                PickStop(now);
                break;
            case Phase.CloseBoard:
                CloseBoard(now);
                break;
            case Phase.WaitBoard:
                WaitBoard(now);
                break;
            case Phase.Buy:
                Buy(now);
                break;
            case Phase.WaitBuy:
                WaitBuy(now);
                break;
            case Phase.SettleCounts:
                SettleCounts(now);
                break;
        }
    }

    private void PullPrices(DateTime now)
    {
        Status = "Pulling prices";
        foreach (var item in _manager.WantedItems.Where(i => i.IsMarketable && i.StillNeeded > 0))
            item.RefreshMarketData();
        _phase = Phase.WaitPrices;
        _deadline = now + TimeSpan.FromSeconds(90);
        _notBefore = now + TimeSpan.FromMilliseconds(500);
    }

    private void WaitPrices(DateTime now)
    {
        var fetching = _manager.WantedItems.Count(i => i.IsFetchingData);
        Status = $"Pulling prices ({fetching} left)";
        // A pull that never finishes keeps its old data; the plan flags anything without usable prices.
        if (fetching == 0 || now > _deadline)
            _phase = Phase.Plan;
    }

    private void StartPlan()
    {
        Status = "Planning";
        // Owned counts (IPC) and the player's world are only readable here on the framework thread.
        var wanted = _manager.WantedItems.Select(i => (Item: i, StillNeeded: i.StillNeeded)).ToList();
        var config = SupermarketSweep.Config;
        var extra = config.RouteMaxExtraPercent;
        var overbuy = OverbuyRule.FromConfig(config);
        var trips = TripCosts.FromConfig(config);
        (string, string)? here = Player.Available ? (Player.CurrentWorldName, Player.CurrentDataCenterName) : null;
        // Worlds already done this run are left out: Universalis can still list what the board there didn't have
        // (stale data), and planning on a world the run won't revisit would leave those units bought nowhere.
        var visited = _visited.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _planning = Task.Run(() => RoutePlanner.Plan(wanted, extra, overbuy, trips, here, visited));
        _phase = Phase.WaitPlan;
    }

    private void WaitPlan()
    {
        if (_planning is not { IsCompleted: true })
            return;
        if (!_planning.IsCompletedSuccessfully)
        {
            Svc.Log.Error($"Route planning failed: {_planning.Exception}");
            Stop("Route planning hit an error (details in /xllog).", problem: true);
            return;
        }

        Plan = _planning.Result;
        _planning = null;
        if (_confirmed)
        {
            _phase = Phase.PickStop;
            return;
        }

        var config = SupermarketSweep.Config;
        var picked = Picked(Plan);
        Preflight = RoutePreflight.Check(picked, Gil(), config.AutoBuyGilReserve, config.RouteTravelAllowancePerStop,
            Player.Available ? Player.CurrentWorldName : null, Blockers(picked));
        _phase = Phase.Confirm;
    }

    /// <summary>Reasons the run can't start. Reads plugin and game state, so call on the framework thread.</summary>
    private List<string> Blockers(RoutePlan plan)
    {
        var config = SupermarketSweep.Config;
        var blockers = new List<string>();
        if (config.BuyAutomation == BuyAutomation.OutlineOnly)
            blockers.Add("Buy automation is set to Outline only (settings).");
        if (!AllaganTools_IPCSubscriber.IsEnabled)
            blockers.Add("Allagan Tools isn't loaded: owned counts are needed to replan between worlds.");
        if (_manager.Buyer.IsRunning)
            blockers.Add("The buy assistant is already buying.");

        if (_manager.TaskManager.IsBusy || (Lifestream_IPCSubscriber.IsEnabled && Lifestream_IPCSubscriber.IsBusy()))
            blockers.Add("Travel or a walk is still in progress.");

        var here = Player.Available ? Player.CurrentWorldName : null;
        var travels = plan.Stops.Any(s => s.World != here);
        var firstHere = plan.Stops.FirstOrDefault()?.World == here;
        var needsHub = firstHere && !UiHelpers.IsMarketboardOpen && !HubTerritories.Contains(Svc.ClientState.TerritoryType);
        if ((travels || needsHub) && !Lifestream_IPCSubscriber.IsEnabled)
            blockers.Add("Lifestream isn't loaded: it's needed to travel between worlds" + (needsHub ? " and to get to a city." : "."));
        if (config.UseVnavPathing && !VNavmesh_IPCSubscriber.IsEnabled && (travels || (firstHere && !UiHelpers.IsMarketboardOpen)))
            blockers.Add("vnavmesh isn't loaded, so the walk to the marketboard can't happen (or turn off vnavmesh pathing and open boards yourself).");

        return blockers;
    }

    private void PickStop(DateTime now)
    {
        var plan = Plan!;
        _stop = plan.Stops.FirstOrDefault(s => !_visited.Contains(s.World) && !IsSkipped(s));
        if (_stop is null)
        {
            var skipped = plan.Stops.Where(s => !_visited.Contains(s.World) && IsSkipped(s)).Select(s => s.World).ToList();
            Stop(skipped.Count > 0
                ? $"Done with the picked worlds (skipped: {string.Join(", ", skipped)})."
                : plan.Unfilled.Count > 0 || plan.NeedsPrices.Count > 0
                ? "Route finished; some items couldn't be fully covered (see the Route tab)."
                : "Route finished: everything's bought!");
            return;
        }

        var number = plan.Stops.IndexOf(_stop) + 1;
        Status = $"Stop {_visited.Count + 1}: {_stop.World}";
        AddLog($"{_stop.World}: {_stop.Purchases.Select(p => p.Item).Distinct().Count()} item(s), about {UiHelpers.Gil(_stop.Subtotal)} gil (stop {number} of {plan.Stops.Count} in the plan)");

        var here = Player.Available ? Player.CurrentWorldName : null;
        _move = Move.None;
        _walkEndedAt = null;
        if (_stop.World != here)
        {
            // Close the board before travelling (Lifestream closes it too, but don't rely on that).
            _phase = Phase.CloseBoard;
            _deadline = now + TimeSpan.FromSeconds(10);
            return;
        }

        if (UiHelpers.IsMarketboardOpen)
        {
            _phase = Phase.Buy;
            return;
        }

        var config = SupermarketSweep.Config;
        if (!HubTerritories.Contains(Svc.ClientState.TerritoryType))
        {
            // Not in a city with a walkable board: Lifestream takes us to Limsa, then the walk opens the board.
            if (!_manager.TeleportToLimsaMarketboard())
            {
                Stop("Couldn't teleport to Limsa Lominsa (is Lifestream loaded?). Go to a city marketboard and run again.", problem: true);
                return;
            }

            _move = Move.Teleport;
        }
        else if (config.UseVnavPathing && VNavmesh_IPCSubscriber.IsEnabled)
        {
            _manager.QueueMoveToMarketboardTasks();
            _move = Move.Walk;
        }

        _phase = Phase.WaitBoard;
        _deadline = now + TimeSpan.FromSeconds(_move == Move.None ? 120 : Math.Max(10, config.LifeStreamTimeout) + 90);
    }

    private void CloseBoard(DateTime now)
    {
        Status = "Closing the marketboard";
        var listings = MarketboardReader.GetReadyAddon("ItemSearchResult");
        var search = MarketboardReader.GetReadyAddon("ItemSearch");
        var listingsOpen = Svc.GameGui.GetAddonByName("ItemSearchResult") != nint.Zero;
        var searchOpen = Svc.GameGui.GetAddonByName("ItemSearch") != nint.Zero;
        if (!listingsOpen && !searchOpen && !Svc.Condition[ConditionFlag.OccupiedInEvent])
        {
            Travel(now);
            return;
        }

        if (now > _deadline)
        {
            Stop("Couldn't close the marketboard before travelling.", problem: true);
            return;
        }

        // Seen in-game: the listings' X sends [-1]; closing the whole board sends ItemSearch [-1, 0].
        if (listings != null)
            Callback.Fire(listings, true, -1);
        else if (search != null)
            Callback.Fire(search, true, -1, 0);
        _notBefore = now + TimeSpan.FromMilliseconds(600);
    }

    private void Travel(DateTime now)
    {
        var world = _stop!.World;
        if (!Lifestream_IPCSubscriber.IsEnabled)
        {
            Stop($"Lifestream isn't loaded, so there's no way to travel to {world}.", problem: true);
            return;
        }

        // Lifestream travel, then the walk to the board (queued by the travel itself once it lands). A data center
        // hop goes through the lobby (queues, "please wait" retries), so it gets the longer travel timeout.
        var crossDc = _manager.IsOtherDataCenter(world);
        _manager.TravelToWorld(world);
        _move = Move.Travel;
        _phase = Phase.WaitBoard;
        _deadline = now + TimeSpan.FromSeconds(_manager.TravelTimeoutSeconds(crossDc) + 120);
        AddLog($"Travelling to {world}" + (crossDc ? " (another data center)" : ""));
    }

    private void WaitBoard(DateTime now)
    {
        var target = _stop!.World;
        var here = Player.Available ? Player.CurrentWorldName : null;
        var busy = _manager.TaskManager.IsBusy;
        Status = _move == Move.Travel && here != target ? $"Travelling to {target}"
            : _move == Move.Teleport && !HubTerritories.Contains(Svc.ClientState.TerritoryType) ? "Teleporting to Limsa Lominsa"
            : busy ? "Walking to the marketboard"
            : "Open the marketboard to continue";

        if (here == target && MarketboardReader.GetReadyAddon("ItemSearch") != null)
        {
            _move = Move.None;
            _phase = Phase.Buy;
            _notBefore = now + TimeSpan.FromSeconds(1); // let the board finish opening
            return;
        }

        if (_move != Move.None && !busy)
        {
            // The queue is done (or gave up on a timeout) and the board isn't open yet.
            if (here != target)
            {
                Stop($"Travel to {target} didn't finish (Lifestream timed out or was interrupted).", problem: true);
                return;
            }

            if (!SupermarketSweep.Config.UseVnavPathing || !VNavmesh_IPCSubscriber.IsEnabled)
            {
                // No walk: the player opens the board.
                _move = Move.None;
                _deadline = now + TimeSpan.FromSeconds(120);
                return;
            }

            // The walk ends with opening the board; give the window a moment to appear.
            _walkEndedAt ??= now;
            if (now - _walkEndedAt > TimeSpan.FromSeconds(5))
                Stop("The walk to the marketboard ended without opening it. Open it and run again.", problem: true);
            return;
        }

        if (now > _deadline)
            Stop("Couldn't get to the marketboard. Open it and run again.", problem: true);
    }

    private void Buy(DateTime now)
    {
        var stop = _stop!;
        var items = stop.Purchases.Select(p => p.Item).Distinct().ToList();
        Status = $"Buying on {stop.World}";
        if (!_manager.Buyer.Start(items, Plan, stop.World))
        {
            if (_manager.Buyer.LastOutcome == BuyOutcome.NothingToBuy)
            {
                AddLog($"{stop.World}: nothing left to buy here.");
                FinishWorld(now, []);
                return;
            }

            Stop("Couldn't start buying (is Buy automation set to Outline only?).", problem: true);
            return;
        }

        _phase = Phase.WaitBuy;
    }

    private void WaitBuy(DateTime now)
    {
        var buyer = _manager.Buyer;
        if (buyer.IsRunning)
        {
            Status = $"{_stop!.World}: {buyer.Status}";
            return;
        }

        _spent += buyer.Spent;
        var stop = _stop!;
        AddLog($"{stop.World}: {buyer.LastResult}");
        switch (buyer.LastOutcome)
        {
            case BuyOutcome.Done:
            case BuyOutcome.NothingToBuy:
                FinishWorld(now, stop.Purchases.Select(p => p.Item).Distinct().ToList());
                return;
            case BuyOutcome.OutOfGil:
                Stop("Reached the gil reserve, so the run ends here.");
                return;
            case BuyOutcome.Stopped:
                Stop("Buying was stopped, so the run ends here.");
                return;
            default:
                Stop($"Buying on {stop.World} hit a problem, so the run ends here: {buyer.LastResult}", problem: true);
                return;
        }
    }

    /// <summary>Marks the world done and waits for owned counts of what was bought to catch up before replanning.</summary>
    private void FinishWorld(DateTime now, List<ShoppingListItem> bought)
    {
        _visited.Add(_stop!.World);
        _settleItems = bought;
        _settleKey = string.Empty;
        _settleChangedAt = now;
        _deadline = now + TimeSpan.FromSeconds(10);
        _phase = Phase.SettleCounts;
    }

    // Allagan Tools' counts lag behind purchases (and are cached for a second here), so wait until they stop
    // changing for a moment, with a cap, before replanning from them.
    private void SettleCounts(DateTime now)
    {
        Status = "Waiting for owned counts to update";
        var key = string.Join(",", _settleItems.Select(i => i.StillNeeded));
        if (key != _settleKey)
        {
            _settleKey = key;
            _settleChangedAt = now;
        }
        else if (now - _settleChangedAt >= TimeSpan.FromSeconds(2.5) || now > _deadline)
        {
            _phase = Phase.PullPrices;
            return;
        }

        _notBefore = now + TimeSpan.FromSeconds(1);
    }

    private static long Gil()
    {
        var inventory = InventoryManager.Instance();
        return inventory == null ? 0 : inventory->GetGil();
    }

    // Chat lines also go to the Dalamud log, so a run can be followed afterwards in /xllog or dalamud.log.
    private static void Chat(string message)
    {
        Svc.Chat.Print($"[Supermarket Sweep] {message}");
        Svc.Log.Information(message);
    }

    private void AddLog(string line)
    {
        _log.Add(line);
        Svc.Log.Information($"Route run: {line}");
    }

    public void Dispose()
    {
        Svc.Framework.Update -= OnUpdate;
        _phase = Phase.Idle;
    }
}
