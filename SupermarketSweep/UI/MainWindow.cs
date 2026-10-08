using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons.Configuration;
using ECommons.ImGuiMethods;
using NostraLib;
using SupermarketSweep.Models;

namespace SupermarketSweep.UI;

/// <summary>
/// Layout only: toolbar, search, then shopping list | divider | tabbed details. Each area lives in its own class.
/// </summary>
public class MainWindow : NostraWindow
{
    private readonly SupermarketSweep _manager;
    private readonly FileDialogManager _fileDialog = new();
    private readonly SearchPanel _search;
    private readonly ShoppingListPanel _list;
    private readonly ItemPanel _itemPanel;
    private readonly RoutePanel _routePanel;

    public MainWindow(SupermarketSweep manager) : base("Supermarket Sweep###SupermarketSweepMain")
    {
        _manager = manager;
        _search = new SearchPanel(manager);
        _list = new ShoppingListPanel(manager);
        _itemPanel = new ItemPanel(manager);
        _routePanel = new RoutePanel(manager);

        Size = new Vector2(900, 650);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(500, 350) };

        TitleBarButtons.Add(new TitleBarButton
        {
            AvailableClickthrough = true,
            Click = _ => manager.OpenConfigUi(),
            Icon = FontAwesomeIcon.Cog,
            Priority = 3,
            ShowTooltip = () => ImGui.SetTooltip("Open Config Menu"),
        });
    }

    public override void Draw()
    {
        DrawToolbar();
        ImGui.Spacing();
        _search.Draw();
        ImGui.Separator();
        ImGui.Spacing();

        var listWidth = SupermarketSweep.Config.ShoppingListWidth * ImGuiHelpers.GlobalScale;
        _list.Draw(listWidth);
        ImGui.SameLine(0, 0);
        var width = SupermarketSweep.Config.ShoppingListWidth;
        if (UiHelpers.DrawSplitter("##shoppingListResize", true, ref width, 140, 800))
            SupermarketSweep.Config.ShoppingListWidth = width;
        ImGui.SameLine(0, 0);

        using (ImRaii.Child("Details", Vector2.Zero))
            DrawTabs();

        _fileDialog.Draw();
    }

    private void DrawToolbar()
    {
        DrawRegionCombo();

        ImGui.SameLine();
        var fetching = _manager.WantedItems.Count(i => i.IsFetchingData);
        using (ImRaii.Disabled(fetching > 0))
        {
            if (ImGui.Button(fetching > 0 ? $"Pulling Prices ({fetching} left)...###pullAll" : "Pull All Prices###pullAll"))
            {
                foreach (var item in _manager.WantedItems)
                    item.RefreshMarketData();
            }
        }

        ImGuiEx.Tooltip("Pull fresh Universalis prices for every item on the list.");

        ImGui.SameLine();
        if (ImGui.Button("Import"))
            ImGui.OpenPopup("importMenu");

        using var popup = ImRaii.Popup("importMenu");
        if (!popup)
            return;

        if (ImGui.MenuItem("MakePlace List..."))
            _fileDialog.OpenFileDialog("Select MakePlace List File", "MakePlace List Files{.txt}", (ok, path) =>
            {
                if (ok && !string.IsNullOrEmpty(path) && File.ReadAllText(path) is { Length: > 0 } text)
                    ListImporter.ImportMakePlace(_manager, text);
            });
        if (ImGui.MenuItem("From Clipboard"))
            ListImporter.ImportClipboard(_manager);
        ImGuiEx.Tooltip("One item per line, like:\n10x Ipe Log\n999x Iron Ore");
    }

    private static void DrawRegionCombo()
    {
        var current = SupermarketSweep.Config.ShoppingRegion;
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        using var combo = ImRaii.Combo("##region", current.ToFriendlyString());
        ImGuiEx.Tooltip("Region/Datacenter to pull prices for");
        if (!combo)
            return;

        foreach (var region in Enum.GetValues<RegionType>())
        {
            if (!ImGui.Selectable(region.ToFriendlyString(), region == current))
                continue;
            SupermarketSweep.Config.ShoppingRegion = region;
            EzConfig.Save();
        }
    }

    private void DrawTabs()
    {
        using var tabs = ImRaii.TabBar("##detailTabs");
        if (!tabs)
            return;

        using (var tab = ImRaii.TabItem("Item"))
        {
            if (tab)
                _itemPanel.Draw(_list.Selected);
        }

        using (var tab = ImRaii.TabItem("Route"))
        {
            if (tab)
                _routePanel.Draw();
        }
    }
}
