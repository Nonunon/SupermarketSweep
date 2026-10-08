using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using ECommons.Configuration;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using SupermarketSweep.Models;

namespace SupermarketSweep.UI;

public static class UiHelpers
{
    /// <summary>
    /// A draggable divider line. Horizontal bars (vertical = false) span the available width and drag up/down;
    /// vertical bars fill the available height and drag left/right. <paramref name="size"/> is in unscaled pixels.
    /// Saves the config once the drag ends. Returns true while the value is changing.
    /// </summary>
    public static bool DrawSplitter(string id, bool vertical, ref float size, float min, float max)
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

    /// <summary>True when the marketboard's item search window is open, so <see cref="SearchMarketboard"/> can work.</summary>
    public static bool IsMarketboardOpen => Svc.GameGui.GetAddonByName("ItemSearch") != nint.Zero;

    /// <summary>Types <paramref name="itemName"/> into the open marketboard search and runs it. False if it's closed.</summary>
    public static unsafe bool SearchMarketboard(string itemName)
    {
        var addon = (AddonItemSearch*)(nint)Svc.GameGui.GetAddonByName("ItemSearch");
        if (addon == null)
            return false;

        addon->SearchTextInput->SetText(itemName);
        addon->RunSearch();
        return true;
    }

    public static string Gil(long amount) => amount.ToString("N0", CultureInfo.InvariantCulture);

    public static string FormatAge(TimeSpan age) => age.TotalMinutes < 1 ? "just now"
        : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
        : $"{(int)age.TotalHours}h {age.Minutes}m ago";

    public enum PriceStatus
    {
        None,
        Fetching,
        Fresh,
        OtherRegion,
        Unmarketable,
    }

    public static PriceStatus GetPriceStatus(ShoppingListItem item)
    {
        if (!item.IsMarketable)
            return PriceStatus.Unmarketable;
        if (item.IsFetchingData)
            return PriceStatus.Fetching;
        if (item.MarketDataRegion is null)
            return PriceStatus.None;
        return item.PricesMatchCurrentScope ? PriceStatus.Fresh : PriceStatus.OtherRegion;
    }

    public static (Vector4 Color, string Tooltip) Describe(PriceStatus status) => status switch
    {
        PriceStatus.Fetching => (ImGuiColors.DalamudYellow, "Pulling prices..."),
        PriceStatus.Fresh => (ImGuiColors.HealerGreen, "Has prices"),
        PriceStatus.OtherRegion => (ImGuiColors.DalamudOrange, "Prices were pulled for a different Region/Datacenter or Oceania setting"),
        PriceStatus.Unmarketable => (ImGuiColors.DalamudGrey3, "Can't be bought on the marketboard"),
        _ => (ImGuiColors.DalamudGrey, "No prices pulled yet"),
    };

    /// <summary>Small filled circle on the current line, vertically centered on the text. Advances the cursor.</summary>
    public static void StatusDot(Vector4 color)
    {
        var size = ImGui.GetTextLineHeight();
        var pos = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddCircleFilled(pos + new Vector2(size / 2, size / 2), size * 0.22f,
            ImGui.GetColorU32(color));
        ImGui.Dummy(new Vector2(size, size));
    }
}
