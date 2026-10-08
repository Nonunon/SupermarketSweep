using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.ImGuiSeStringRenderer;
using Dalamud.Interface.Style;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons;
using ECommons.Automation;
using ECommons.Configuration;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using NostraLib;
using OtterGui;
using OtterGui.Classes;
using SupermarketSweep.Models;
using SupermarketSweep.IPC;
using SupermarketSweep.UI;

namespace SupermarketSweep.UI;

public class MBShoppingList_UI : NostraWindow
{
    private SupermarketSweep _manager;
    private FileDialogManager _fileDialogManager;
    private MbShoppingListUiSelector _selector;
    private ResultsTable _resultsTable;
    private List<MarketDataListing> _marketData = new();

    public MBShoppingList_UI(SupermarketSweep manager) : base("Supermarket Sweep", ImGuiWindowFlags.None, false)
    {
        _manager = manager;
        _fileDialogManager = new FileDialogManager();
        _selector = new MbShoppingListUiSelector(_manager);
        _resultsTable = new ResultsTable(_manager, _marketData);
        TitleBarButtons.Add(new TitleBarButton()
        {
            AvailableClickthrough = true,
            Click = _ => manager.OpenConfigUi(),
            Icon = FontAwesomeIcon.Cog,
            Priority = 3,
            ShowTooltip = () =>
            {
                ImGui.BeginTooltip();
                ImGui.Text("Open Config Menu");
                ImGui.EndTooltip();
            }
        });
    }

    public override void Draw()
    {
        DrawItemAdd();
        if (ImGui.Button("Import MakePlace List"))
        {
            SelectFile();
        }

        ImGui.SameLine();
        if (ImGui.Button("Import from Clipboard"))
        {
            ExtractClipboardText();
        }

        ImGuiEx.Tooltip("Import items from clipboard using the following format:\n'10x Ipe Log'\n'999x Iron Ore");

        if (ImGuiUtil.GenericEnumCombo("Region/Datacenter", 300, SupermarketSweep.Config.ShoppingRegion,
                out RegionType newRegion, r => r.ToFriendlyString()))
        {
            SupermarketSweep.Config.ShoppingRegion = newRegion;
            EzConfig.Save();
        }

        var fetching = _manager.WantedItems.Count(i => i.IsFetchingData);
        var pullAllLabel = fetching > 0 ? $"Pulling Prices ({fetching} left)..." : "Pull All Prices";
        if (ImGuiUtil.DrawDisabledButton($"{pullAllLabel}###pullAll", Vector2.Zero,
                "Pull fresh Universalis prices for every item on the list.", fetching > 0))
        {
            foreach (var item in _manager.WantedItems)
                item.RefreshMarketData();
        }

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.Separator();

        _selector.Draw(SupermarketSweep.Config.ShoppingListWidth * ImGuiHelpers.GlobalScale);
        ImGui.SameLine(0, 0);
        var width = SupermarketSweep.Config.ShoppingListWidth;
        if (DrawSplitter("##shoppingListResize", true, ref width, 120, 800))
            SupermarketSweep.Config.ShoppingListWidth = width;
        ImGui.SameLine(0, 0);
        DrawWantedItem(_selector.Current);

        _fileDialogManager.Draw();
    }

    private void ExtractClipboardText()
    {
        var clipboardText = ImGuiUtil.GetClipboardText();
        if (!string.IsNullOrEmpty(clipboardText))
        {
            try
            {
                Dictionary<string, int> items = new Dictionary<string, int>();

                // Regex pattern
                var pattern = @"\b(\d+)x\s(.+)\b";
                var matches = Regex.Matches(clipboardText, pattern);

                // Loop through matches and add them to dictionary
                foreach (Match match in matches)
                {
                    var quantity = int.Parse(match.Groups[1].Value);
                    var itemName = match.Groups[2].Value;
                    items[itemName] = quantity;
                }

                bool saveNeeded = false;
                foreach (var item in items)
                {
                    var itemObj = SupermarketSweep.AllItems.FirstOrNull(i =>
                        string.Equals(i.Name.ToString(), item.Key, StringComparison.OrdinalIgnoreCase));
                    if (itemObj is null)
                    {
                        Svc.Log.Error($"Item {item.Key} not found");
                        continue;
                    }

                    var shoppingListItem = new ShoppingListItem(itemObj.Value, item.Value);
                    _manager.WantedItems.Add(shoppingListItem);
                    saveNeeded = true;
                }

                if (saveNeeded)
                    _manager.SaveList();
            }
            catch (Exception e)
            {
                Svc.Chat.PrintError("[Supermarket Sweep] Error importing clipboard text. See /xllog for details.");
                Svc.Log.Error($"Error importing from clipboard: {e}");
            }
        }
        else
        {
            Svc.Chat.PrintError($"Clipboard text is empty or invalid");
        }
    }

    private void DrawWantedItem(ShoppingListItem? item)
    {
        ImGui.BeginChild("Wanted Item");
        if (item is null)
        {
            ImGui.Text("No item selected");
            ImGui.EndChild();
            return;
        }

        if (ImGui.Selectable(item.Name))
        {
            var seString = new SeStringBuilder().AddText($"[Supermarket Sweep]").AddItemLink(item.ItemId).BuiltString;
            Svc.Chat.Print(seString);
        }

        ImGuiEx.Tooltip("Click to print item link in chat");


        int quantity = (int)item.Quantity;
        ImGui.PushItemWidth(100);
        if (ImGui.InputInt("Needed Quantity", ref quantity))
        {
            item.Quantity = quantity;
            _manager.SaveList();
        }

        ImGui.PopItemWidth();

        ImGui.Text($"Already Owned: {item.InventoryCount}");
        ImGuiEx.Tooltip(
            (SupermarketSweep.Config.AllCharactersInventory
                ? "Amount of this item you have across all characters (including retainers and alts)"
                : "Amount of this item this character has (including its retainers)")
            + "\nSourced from Allagan Tools\nSee Allagan Tools for detailed information");

        if (item.IsMarketable)
        {
            DrawItemSearch(item);

            if (ImGuiUtil.DrawDisabledButton($"Refresh Prices##{item.ItemId}", Vector2.Zero,
                    "Pull fresh prices for this item from Universalis.", item.IsFetchingData))
                item.RefreshMarketData();

            ImGui.SameLine();
            if (item.IsFetchingData)
                ImGui.TextDisabled(item.Retries > 0 ? $"Fetching... (retry {item.Retries})" : "Fetching...");
            else if (item.MarketDataRegion is { } region && region != SupermarketSweep.Config.ShoppingRegion)
                ImGui.TextColored(ImGuiColors.DalamudOrange, $"Prices are for {region.ToFriendlyString()}, refresh for the current region");
            else if (item.MarketDataFetchedAt is { } fetchedAt)
                ImGui.TextDisabled($"Updated {FormatAge(DateTime.Now - fetchedAt)}");
            else
                ImGui.TextDisabled("No price data yet");
        }
        else
        {
            ImGui.Text("This item cannot be purchased on the Market Board");
        }

        // Older data stays on screen while a refresh is running, so the table doesn't blink out.
        if (item.MarketDataResponse != null)
        {
            var resultsTable = new ResultsTable(_manager, item.MarketDataResponse.Listings);
            resultsTable.Draw(10);
        }

        ImGui.EndChild();
    }

    private static string FormatAge(TimeSpan age) => age.TotalMinutes < 1 ? "just now"
        : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
        : $"{(int)age.TotalHours}h {age.Minutes}m ago";


    private unsafe void DrawItemSearch(ShoppingListItem item)
    {
        AddonItemSearch* addonItemSearch = (AddonItemSearch*)(nint)Svc.GameGui.GetAddonByName("ItemSearch");
        var disabled = addonItemSearch == null;
        var description = disabled
            ? "Automatically search for this item on the Marketboard (MarketBoard window must be open)"
            : "Automatically search for this item on the Marketboard";
        if (ImGuiUtil.DrawDisabledButton($"Search Marketboard for Item##{item.ItemId}", new Vector2(), description,
                disabled))
        {
            addonItemSearch->SearchTextInput->SetText(item.Name);
            addonItemSearch->RunSearch();
        }
    }

    private void SelectFile()
    {
        void Callback(bool finished, string path)
        {
            if (finished && !string.IsNullOrEmpty(path))
            {
                string text = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    ExtractItemsFromMakePlaceList(text, "Furniture", "Dyes");
                    ExtractItemsFromMakePlaceList(text, "Dyes", "Furniture (With Dye)");
                    _manager.SaveList();
                }
            }
        }

        _fileDialogManager.OpenFileDialog("Select MakePlace List File", "MakePlace List Files{.txt}", Callback);
    }

    private string _searchTerm = string.Empty;

    // Drawing tens of thousands of Selectables for a one-letter query tanks the frame rate.
    private const int MaxShownResults = 200;

    private void DrawItemAdd()
    {
        ImGui.Text("Item Search");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("##searchBar", ref _searchTerm, 100);
        ImGuiEx.Tooltip("Words can be in any order and partial, e.g. 'courtly fending' finds\n'Courtly Lover's Gauntlets of Fending'.");
        ImGui.SameLine();
        if (ImGui.Button("Clear"))
            _searchTerm = string.Empty;

        ImGui.BeginChild("ItemList", new Vector2(0, SupermarketSweep.Config.SearchListHeight * ImGuiHelpers.GlobalScale), true);
        if (!string.IsNullOrWhiteSpace(_searchTerm))
        {
            var matchingItems = SupermarketSweep.ItemSearch.Search(_searchTerm);

            if (matchingItems.Count == 0)
                ImGui.TextDisabled("No items found.");

            foreach (var item in matchingItems.Take(MaxShownResults))
            {
                using var id = ImRaii.PushId((int)item.RowId);
                var existing = _manager.WantedItems.FirstOrDefault(w => w.ItemId == item.RowId);
                var label = existing is null ? item.Name.ToString() : $"{item.Name} (in list: {existing.Quantity})";

                bool clicked;
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen, existing is not null))
                    clicked = ImGui.Selectable(label);

                if (clicked)
                {
                    // Search stays put so several results can be added in a row; re-clicking bumps the quantity.
                    if (existing is null)
                    {
                        _manager.WantedItems.Add(new ShoppingListItem(item, 1));
                        Svc.Log.Debug($"Added shopping list item: {item.Name}");
                    }
                    else
                    {
                        existing.Quantity++;
                    }

                    _manager.SaveList();
                }
            }

            if (matchingItems.Count > MaxShownResults)
                ImGui.TextDisabled($"...and {matchingItems.Count - MaxShownResults} more. Add more words to narrow it down.");
        }
        else
        {
            ImGui.Text("Items will appear here when you enter a search term.");
        }

        ImGui.EndChild();
        DrawSearchListResizeHandle();
    }

    // Thin bar under the result list; drag it to resize the list.
    private void DrawSearchListResizeHandle()
    {
        var height = SupermarketSweep.Config.SearchListHeight;
        if (DrawSplitter("##searchListResize", false, ref height, 80, 1000))
            SupermarketSweep.Config.SearchListHeight = height;
    }

    /// <summary>
    /// A draggable divider line. Horizontal bars (vertical = false) span the available width and drag up/down;
    /// vertical bars fill the available height and drag left/right. <paramref name="size"/> is in unscaled pixels.
    /// Saves the config once the drag ends. Returns true while the value is changing.
    /// </summary>
    private static bool DrawSplitter(string id, bool vertical, ref float size, float min, float max)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var thickness = 6 * scale;
        var avail = ImGui.GetContentRegionAvail();
        ImGui.InvisibleButton(id, vertical ? new Vector2(thickness, avail.Y) : new Vector2(avail.X, thickness));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();

        var rMin = ImGui.GetItemRectMin();
        var rMax = ImGui.GetItemRectMax();
        var color = ImGui.GetColorU32(active ? ImGuiCol.SeparatorActive : hovered ? ImGuiCol.SeparatorHovered : ImGuiCol.Separator);
        var mid = (rMin + rMax) / 2;
        var from = vertical ? new Vector2(mid.X, rMin.Y) : new Vector2(rMin.X, mid.Y);
        var to = vertical ? new Vector2(mid.X, rMax.Y) : new Vector2(rMax.X, mid.Y);
        ImGui.GetWindowDrawList().AddLine(from, to, color, 2 * scale);

        if (hovered || active)
            ImGui.SetMouseCursor(vertical ? ImGuiMouseCursor.ResizeEw : ImGuiMouseCursor.ResizeNs);

        if (ImGui.IsItemDeactivated())
            EzConfig.Save();

        if (!active)
            return false;

        var delta = ImGui.GetIO().MouseDelta;
        size = Math.Clamp(size + (vertical ? delta.X : delta.Y) / scale, min, max);
        return true;
    }

    private unsafe void DrawMBButton(ShoppingListItem item)
    {
        var mbAddon = (AddonItemSearch*)(nint)Svc.GameGui.GetAddonByName("ItemSearch");
        if (mbAddon == null)
            return;

        ImGui.SameLine();

        if (ImGui.Button($"Search##{item.ItemId}"))
        {
            mbAddon->SearchTextInput->SetText(item.Name);
            mbAddon->RunSearch();
        }
    }

    void ExtractItemsFromMakePlaceList(string text, string startSection, string endSection)
    {
        // Find the start index of the section
        int startIndex = text.IndexOf(startSection);
        if (startIndex == -1)
        {
            Svc.Log.Error($"Section '{startSection}' not found.");
            return;
        }

        // Find the end index of the section
        int endIndex = text.IndexOf(endSection, startIndex);
        if (endIndex == -1)
        {
            endIndex = text.Length; // If end section not found, read till end
        }

        // Extract the section text
        string sectionText = text.Substring(startIndex, endIndex - startIndex);

        // Split the section into lines
        string[] lines = sectionText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        // Regular expression to match lines with format 'Item Name: Quantity'
        Regex regex = new Regex(@"^\s*(.+?):\s*(\d+)\s*$");

        // Iterate over each line and extract item name and quantity
        foreach (string line in lines)
        {
            Match match = regex.Match(line);
            if (match.Success)
            {
                string itemName = match.Groups[1].Value.Trim();
                if (string.Equals(startSection, "Dyes", StringComparison.OrdinalIgnoreCase))
                    itemName += " Dye";
                int quantity = int.Parse(match.Groups[2].Value.Trim());

                var item = SupermarketSweep.AllItems.FirstOrNull(i =>
                    string.Equals(i.Name.ToString(), itemName, StringComparison.OrdinalIgnoreCase));
                if (item == null && string.Equals(startSection, "Dyes", StringComparison.OrdinalIgnoreCase))
                {
                    item = SupermarketSweep.AllItems.FirstOrNull(i =>
                        string.Equals(i.Name.ToString(), $"General-purpose {itemName}",
                            StringComparison.OrdinalIgnoreCase));
                }

                if (item == null)
                {
                    Svc.Log.Warning($"Item '{itemName}' does not exist.");
                    continue;
                }

                var existingItem = _manager.WantedItems.FirstOrDefault(i => i.ItemId == item.Value.RowId);

                // Add or update the item in the dictionary
                if (existingItem != null)
                {
                    existingItem.Quantity += quantity;
                }
                else
                {
                    _manager.WantedItems.Add(new ShoppingListItem(item.Value, quantity));
                }
            }
        }
    }
}
