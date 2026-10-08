using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using ECommons.ImGuiMethods;

namespace SupermarketSweep.UI;

/// <summary>
/// A little thank-you page for NostraThomas, who made the original plugin. Opened from the heart in the main
/// window's title bar. Replaces the Ko-fi button NostraLib used to put on every window.
/// </summary>
public class AboutWindow : Window
{
    private const string KofiUrl = "https://ko-fi.com/nostrathomas";

    private static readonly Vector4 Pink = new(1f, 0.56f, 0.76f, 1f);
    private static readonly Vector4 PinkHover = new(1f, 0.66f, 0.83f, 1f);
    private static readonly Vector4 PinkActive = new(0.9f, 0.45f, 0.66f, 1f);
    private static readonly Vector4 Ink = new(0.18f, 0.08f, 0.14f, 1f);

    public AboutWindow() : base("With love, Nostra###SupermarketSweepAbout",
        ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar)
    {
        Size = new Vector2(360, 280);
        SizeCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        ImGui.Spacing();
        DrawHearts();
        ImGui.Spacing();

        Centered("Supermarket Sweep", Pink);
        Centered("was originally made with love by NostraThomas.", null);
        ImGui.Spacing();
        CenteredWrapped("This version is a friend's rework of their plugin. If it ever saved you some gil " +
                        "(or a trip to a fourth data center), consider buying Nostra a coffee.");
        ImGui.Spacing();
        ImGui.Spacing();

        DrawKofiButton();
    }

    private static void DrawHearts()
    {
        var heart = FontAwesomeIcon.Heart.ToIconString();
        var hearts = $"{heart}  {heart}  {heart}";
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        var width = ImGui.CalcTextSize(hearts).X;
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - width) / 2);
        ImGui.TextColored(Pink, hearts);
    }

    private static void DrawKofiButton()
    {
        const string label = "Support Nostra on Ko-fi";
        var size = new Vector2(ImGui.CalcTextSize(label).X + 40 * ImGuiHelpers.GlobalScale,
            ImGui.GetFrameHeight() * 1.4f);
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - size.X) / 2);

        using (ImRaii.PushColor(ImGuiCol.Button, Pink)
                   .Push(ImGuiCol.ButtonHovered, PinkHover)
                   .Push(ImGuiCol.ButtonActive, PinkActive)
                   .Push(ImGuiCol.Text, Ink))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 12 * ImGuiHelpers.GlobalScale))
        {
            if (ImGui.Button(label, size))
                Util.OpenLink(KofiUrl);
        }

        ImGuiEx.Tooltip(KofiUrl);
    }

    private static void Centered(string text, Vector4? color)
    {
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - ImGui.CalcTextSize(text).X) / 2);
        if (color is { } c)
            ImGui.TextColored(c, text);
        else
            ImGui.Text(text);
    }

    // Word-wraps to the window width and centers each line.
    private static void CenteredWrapped(string text)
    {
        var maxWidth = ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X * 4;
        var line = string.Empty;
        foreach (var word in text.Split(' '))
        {
            var candidate = line.Length == 0 ? word : $"{line} {word}";
            if (ImGui.CalcTextSize(candidate).X > maxWidth && line.Length > 0)
            {
                Centered(line, ImGuiColors.DalamudGrey);
                line = word;
            }
            else
            {
                line = candidate;
            }
        }

        if (line.Length > 0)
            Centered(line, ImGuiColors.DalamudGrey);
    }
}
