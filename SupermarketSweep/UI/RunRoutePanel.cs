using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ECommons.ImGuiMethods;
using SupermarketSweep.Models;

namespace SupermarketSweep.UI;

/// <summary>The Route tab's "Run route" controls: the button, the preflight check, and progress while a run goes.</summary>
public class RunRoutePanel(SupermarketSweep manager)
{
    public void Draw(RoutePlan? plan)
    {
        var runner = manager.Runner;
        if (runner.AwaitingConfirm && runner.Preflight is { } preflight)
            DrawPreflight(runner, preflight);
        else if (runner.IsRunning)
            DrawRunning(runner);
        else
            DrawIdle(runner, plan);

        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawIdle(RouteRunner runner, RoutePlan? plan)
    {
        var level = SupermarketSweep.Config.BuyAutomation;
        var why = level == BuyAutomation.OutlineOnly ? "Set Buy automation (settings) to anything but Outline only first."
            : manager.Buyer.IsRunning ? "The buy assistant is buying right now."
            : plan is { IsEmpty: true } ? "Nothing to buy."
            : null;
        using (ImRaii.Disabled(why is not null))
        {
            if (ImGui.Button("Run route"))
                runner.Begin();
        }

        ImGuiEx.Tooltip(why ?? "Pulls fresh prices, replans, and shows a gil check. After Start it goes through every stop by itself:\n" +
            "travel (Lifestream), walk to the board, buy" +
            (level == BuyAutomation.OpenConfirmation ? " (you press Yes on each purchase)" : "") +
            ", then pull prices and replan before the next world.\nStop with the Stop button or /shop stop.");

        if (runner.LastResult is { } result)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"Last run: {result}");
            DrawLog(runner);
            DrawLeftShort(runner);
        }
    }

    private static void DrawLeftShort(RouteRunner runner)
    {
        if (runner.LeftShort.Count == 0)
            return;

        ImGui.TextColored(ImGuiColors.DalamudYellow, $"Left short after the run ({runner.LeftShort.Count} item(s)):");
        ImGuiEx.Tooltip("What was still needed when the run ended, and why. If the run was stopped mid-buy, owned counts\n" +
                        "may lag a purchase or two behind; the list on the left catches up within seconds.");
        using var indent = ImRaii.PushIndent();
        foreach (var (item, units, reason) in runner.LeftShort)
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"{item.Name}: {units} ({reason})");
    }

    private static void DrawPreflight(RouteRunner runner, PreflightResult preflight)
    {
        ImGui.Text("Before the run:");
        ImGui.Text($"Route {UiHelpers.Gil(preflight.RouteTotal)} gil + travel about {UiHelpers.Gil(preflight.TravelAllowance)} = {UiHelpers.Gil(preflight.Cost)} gil.");
        ImGui.Text($"You have {UiHelpers.Gil(preflight.Gil)} gil" +
                   (preflight.Reserve > 0 ? $", keeping {UiHelpers.Gil(preflight.Reserve)}: {UiHelpers.Gil(preflight.Spendable)} to spend." : "."));

        switch (preflight.Affordability)
        {
            case Affordability.Fully:
                ImGui.TextColored(ImGuiColors.HealerGreen, "Enough for the whole route.");
                break;
            case Affordability.Partly:
                ImGui.TextColored(ImGuiColors.DalamudOrange, "Only enough for part of the route.");
                break;
            case Affordability.NotAtAll:
                ImGui.TextColored(ImGuiColors.DalamudRed, "Nothing to spend above the gil reserve.");
                break;
            case Affordability.NothingToBuy:
                ImGui.TextColored(ImGuiColors.HealerGreen, "Nothing to buy.");
                break;
        }

        foreach (var warning in preflight.Warnings)
            ImGui.TextColored(ImGuiColors.DalamudYellow, warning);
        foreach (var blocker in preflight.Blockers)
            ImGui.TextColored(ImGuiColors.DalamudRed, blocker);

        using (ImRaii.Disabled(!preflight.CanStart))
        {
            if (ImGui.Button(preflight.Affordability == Affordability.Partly ? "Start (buy until the reserve)" : "Start"))
                runner.Confirm();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            runner.Stop("Cancelled.");
    }

    private static void DrawRunning(RouteRunner runner)
    {
        using (ImRaii.PushColor(ImGuiCol.Button, ImGuiColors.DalamudRed with { W = 0.8f }))
        {
            if (ImGui.Button("Stop"))
                runner.Stop("Stopped.");
        }

        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudYellow, $"{runner.Status}...");
        if (runner.Spent > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"(spent {UiHelpers.Gil(runner.Spent)} gil)");
        }

        DrawLog(runner);
    }

    private static void DrawLog(RouteRunner runner)
    {
        foreach (var line in runner.Log)
            ImGui.TextDisabled(line);
    }
}
