using System.Runtime.Serialization;
using Dalamud.Plugin.Ipc.Exceptions;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using SupermarketSweep.IPC;

namespace SupermarketSweep.Models;

public class ShoppingListItem
{
    [Newtonsoft.Json.JsonIgnore] private Item? _itemRecord;

    public ShoppingListItem(Item item, int quantity)
    {
        ItemId = item.RowId;
        Quantity = quantity;
        _itemRecord = item; // Cache the item here
        IsMarketable = SupermarketSweep.MarketableItems.Contains(ItemRecord!.Value);
    }

    public ShoppingListItem()
    {
    }

    [OnDeserialized]
    internal void OnDeserializedMethod(StreamingContext context)
    {
        // Initialize _itemRecord after deserialization
        _itemRecord = SupermarketSweep.AllItems.FirstOrNull(x => x.RowId == ItemId);
        if (_itemRecord == null)
        {
            // Handle the case where the item is not found
            Svc.Log.Error($"Item with ID {ItemId} not found in AllItems.");
        }
        else
        {
            IsMarketable = SupermarketSweep.MarketableItems.Contains(_itemRecord.Value);
        }
    }

    [Newtonsoft.Json.JsonIgnore] public Item? ItemRecord => _itemRecord;

    [Newtonsoft.Json.JsonIgnore] public string Name => _itemRecord?.Name.ToString() ?? $"Unknown item #{ItemId}";

    public uint ItemId { get; set; }

    public bool IsMarketable { get; private set; }
    public long Quantity { get; set; }

    [Newtonsoft.Json.JsonIgnore]
    private DateTime _inventoryLastUpdated = DateTime.MinValue;

    [Newtonsoft.Json.JsonIgnore]
    private long _inventoryCount = 0;

    [Newtonsoft.Json.JsonIgnore]
    public long InventoryCount
    {
        get
        {
            if (DateTime.Now.Subtract(_inventoryLastUpdated).TotalSeconds > 1)
            {
                try
                {
                    _inventoryCount = AllaganTools_IPCSubscriber.IsInitialized()
                        ? AllaganTools_IPCSubscriber.ItemCountOwned(ItemId,
                            // Allagan Tools' flag is "current character only", the opposite of our setting.
                            !SupermarketSweep.Config.AllCharactersInventory,
                            ValidInventoryTypes.Select(i => (uint)i).ToArray())
                        : 0;
                    _inventoryLastUpdated = DateTime.Now;
                }
                catch (Exception ex)
                {
                    Svc.Log.Error($"Allagan Tools IPC failed: {ex.Message}");
                }
            }

            return _inventoryCount;
        }
    }

    [Newtonsoft.Json.JsonIgnore]
    private List<InventoryType> ValidInventoryTypes = new List<InventoryType>()
    {
        InventoryType.Crystals,
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.Mail,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryRings,
        InventoryType.ArmoryWaist,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
        InventoryType.EquippedItems,
        InventoryType.RetainerCrystals,
        InventoryType.RetainerMarket,
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
        InventoryType.RetainerEquippedItems,
        InventoryType.SaddleBag1,
        InventoryType.SaddleBag2,
        InventoryType.FreeCompanyCrystals,
        InventoryType.FreeCompanyPage1,
        InventoryType.FreeCompanyPage2,
        InventoryType.FreeCompanyPage3,
        InventoryType.FreeCompanyPage4,
        InventoryType.FreeCompanyPage5,
        InventoryType.HousingExteriorAppearance,
        InventoryType.HousingExteriorStoreroom,
        InventoryType.HousingInteriorAppearance,
        InventoryType.HousingInteriorStoreroom1,
        InventoryType.HousingInteriorStoreroom2,
        InventoryType.HousingInteriorStoreroom3,
        InventoryType.HousingInteriorStoreroom4,
        InventoryType.HousingInteriorStoreroom5,
        InventoryType.HousingInteriorStoreroom6,
        InventoryType.HousingInteriorStoreroom7,
        InventoryType.HousingInteriorStoreroom8,
        InventoryType.HousingExteriorPlacedItems,
        InventoryType.HousingInteriorPlacedItems1,
        InventoryType.HousingInteriorPlacedItems2,
        InventoryType.HousingInteriorPlacedItems3,
        InventoryType.HousingInteriorPlacedItems4,
        InventoryType.HousingInteriorPlacedItems5,
        InventoryType.HousingInteriorPlacedItems6,
        InventoryType.HousingInteriorPlacedItems7,
        InventoryType.HousingInteriorPlacedItems8,
        InventoryType.PremiumSaddleBag1,
        InventoryType.PremiumSaddleBag2
    };


    public class ItemDetails
    {
        public uint ItemId { get; set; }
        public uint ItemCount { get; set; }
        public InventoryType InventoryType { get; set; }
    }

    [Newtonsoft.Json.JsonIgnore]
    public MarketDataListing? BestMarketListing => MarketDataResponse?.Listings.OrderBy(l => l.Total).FirstOrDefault();

    [Newtonsoft.Json.JsonIgnore] public MarketDataResponse? MarketDataResponse { get; private set; }

    /// <summary>When <see cref="MarketDataResponse"/> was pulled, and for which region. Null until the first pull.</summary>
    [Newtonsoft.Json.JsonIgnore] public DateTime? MarketDataFetchedAt { get; private set; }

    [Newtonsoft.Json.JsonIgnore] public RegionType? MarketDataRegion { get; private set; }

    // One client for every request, and at most a handful in flight so "Pull All" on a big list stays polite.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly SemaphoreSlim Throttle = new(6);

    // After a pull gives up, automatic pulls leave this item alone for a bit instead of retrying every frame.
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(30);

    [JsonIgnore] private Task? _marketDataTask;

    [JsonIgnore] private int _retries;

    [JsonIgnore] private bool _isFetchingData;

    [JsonIgnore] private DateTime _lastFailedAt = DateTime.MinValue;

    [Newtonsoft.Json.JsonIgnore]
    public bool IsFetchingData

    {
        get
        {
            lock (this)
            {
                return _isFetchingData;
            }
        }
    }

    [Newtonsoft.Json.JsonIgnore]
    public int Retries

    {
        get
        {
            lock (this)
            {
                return _retries;
            }
        }
    }

    [Newtonsoft.Json.JsonIgnore] public List<WorldListing> WorldListings { get; private set; } = [];

    public class WorldListing
    {
        public string WorldName { get; set; }
        public int Count { get; set; }
        public long LowestPrice { get; set; }
        public List<MarketDataListing> Listings { get; set; } = new List<MarketDataListing>();
    }

    /// <summary>
    /// True when an automatic pull should happen: no data yet, data for a different region, or data older than
    /// <paramref name="maxAge"/>. Never true while a pull is running or shortly after one failed.
    /// </summary>
    public bool NeedsMarketData(TimeSpan maxAge)
    {
        if (!IsMarketable || IsFetchingData)
            return false;
        lock (this)
        {
            if (DateTime.Now - _lastFailedAt < FailureCooldown)
                return false;
        }

        return MarketDataFetchedAt is null
               || MarketDataRegion != SupermarketSweep.Config.ShoppingRegion
               || DateTime.Now - MarketDataFetchedAt > maxAge;
    }

    /// <summary>Starts a pull in the background unless one is already running. Old data stays visible until it lands.</summary>
    public void RefreshMarketData() => _ = GetMarketDataResponseAsync();

    public async Task GetMarketDataResponseAsync()
    {
        if (!IsMarketable)
            return;

        Task existingTask;
        lock (this)
        {
            if (_marketDataTask != null && !_marketDataTask.IsCompleted)
            {
                existingTask = _marketDataTask;
            }
            else
            {
                _isFetchingData = true;
                _retries = 0;
                _marketDataTask = Task.Run(FetchMarketDataAsync);
                existingTask = _marketDataTask;
            }
        }

        await existingTask;
    }

    private async Task FetchMarketDataAsync()
    {
        var region = SupermarketSweep.Config.ShoppingRegion;
        var succeeded = false;
        while (Retries < 5)
        {
            Svc.Log.Debug($"GetMarketDataResponseAsync for item {Name}");
            try
            {
                string responseString;
                await Throttle.WaitAsync().ConfigureAwait(false);
                try
                {
                    responseString = await Http
                        .GetStringAsync($"https://universalis.app/api/v2/{region.ToUniversalisString()}/{ItemId}")
                        .ConfigureAwait(false);
                }
                finally
                {
                    Throttle.Release();
                }

                var response = string.IsNullOrEmpty(responseString)
                    ? null
                    : JsonConvert.DeserializeObject<MarketDataResponse>(responseString);
                if (response != null)
                {
                    WorldListings = response.Listings
                        .GroupBy(l => l.WorldName)
                        .Select(g => new WorldListing
                        {
                            WorldName = g.Key,
                            Count = g.Count(),
                            LowestPrice = g.Min(l => l.Total),
                            Listings = g.ToList()
                        })
                        .OrderBy(l => l.LowestPrice)
                        .ToList();
                    MarketDataResponse = response;
                    MarketDataRegion = region;
                    MarketDataFetchedAt = DateTime.Now;
                    succeeded = true;
                    break;
                }

                Svc.Log.Warning($"Empty market data response from Universalis for {Name}");
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"Universalis request for {Name} failed: {ex.Message}");
            }

            lock (this)
            {
                _retries++;
            }

            await Task.Delay(2000).ConfigureAwait(false);
        }

        lock (this)
        {
            _isFetchingData = false;
            if (!succeeded)
                _lastFailedAt = DateTime.Now;
        }
    }
}
