using System.Text.RegularExpressions;
using ECommons;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using Dalamud.Bindings.ImGui;

namespace SupermarketSweep;

/// <summary>Turns clipboard text and MakePlace list files into shopping list entries.</summary>
public static class ListImporter
{
    private static readonly Regex ClipboardLine = new(@"\b(\d+)x\s(.+)\b");
    private static readonly Regex MakePlaceLine = new(@"^\s*(.+?):\s*(\d+)\s*$");

    /// <summary>Lines like "10x Ipe Log". Unknown names are logged and skipped.</summary>
    public static void ImportClipboard(SupermarketSweep manager)
    {
        var text = ImGui.GetClipboardText();
        if (string.IsNullOrWhiteSpace(text))
        {
            Svc.Chat.PrintError("[Supermarket Sweep] Clipboard is empty.");
            return;
        }

        var added = 0;
        foreach (Match match in ClipboardLine.Matches(text))
        {
            var name = match.Groups[2].Value.Trim();
            var item = FindByName(name);
            if (item is null)
            {
                Svc.Log.Warning($"Clipboard import: item '{name}' not found");
                continue;
            }

            manager.AddItem(item.Value, long.Parse(match.Groups[1].Value), save: false);
            added++;
        }

        if (added > 0)
            manager.SaveList();
        Svc.Chat.Print($"[Supermarket Sweep] Imported {added} item(s) from the clipboard.");
    }

    /// <summary>Reads the "Furniture" and "Dyes" sections of a MakePlace shopping list file.</summary>
    public static void ImportMakePlace(SupermarketSweep manager, string text)
    {
        ImportMakePlaceSection(manager, text, "Furniture", "Dyes");
        ImportMakePlaceSection(manager, text, "Dyes", "Furniture (With Dye)");
        manager.SaveList();
    }

    private static void ImportMakePlaceSection(SupermarketSweep manager, string text, string startSection,
        string endSection)
    {
        var startIndex = text.IndexOf(startSection, StringComparison.Ordinal);
        if (startIndex == -1)
        {
            Svc.Log.Error($"Section '{startSection}' not found.");
            return;
        }

        var endIndex = text.IndexOf(endSection, startIndex, StringComparison.Ordinal);
        if (endIndex == -1)
            endIndex = text.Length;

        var isDyes = string.Equals(startSection, "Dyes", StringComparison.OrdinalIgnoreCase);
        var lines = text[startIndex..endIndex].Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var match = MakePlaceLine.Match(line);
            if (!match.Success)
                continue;

            var name = match.Groups[1].Value.Trim();
            if (isDyes)
                name += " Dye";

            // Some dyes only exist as "General-purpose X Dye".
            var item = FindByName(name) ?? (isDyes ? FindByName($"General-purpose {name}") : null);
            if (item is null)
            {
                Svc.Log.Warning($"Item '{name}' does not exist.");
                continue;
            }

            manager.AddItem(item.Value, int.Parse(match.Groups[2].Value.Trim()), save: false);
        }
    }

    private static Item? FindByName(string name) => SupermarketSweep.AllItems.FirstOrNull(i =>
        string.Equals(i.Name.ToString(), name, StringComparison.OrdinalIgnoreCase));
}
