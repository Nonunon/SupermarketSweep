using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SupermarketSweep;

/// <summary>
/// Debug tool for working out the marketboard's callbacks (setting <see cref="Config.LogAddonCallbacks"/>, off by default).
/// While on, every addon callback fired while the marketboard is open, plus click events on its windows, is written to
/// /xllog prefixed <c>[CallbackLogger]</c> (Dalamud adds the plugin name in front). It only watches: the original call always runs unchanged.
/// The hook is created once but only enabled while the setting is on.
/// </summary>
public sealed unsafe class CallbackLogger : IDisposable
{
    private const string Prefix = "[CallbackLogger]";
    private static readonly string[] MarketboardAddons = ["ItemSearch", "ItemSearchResult"];

    private readonly Hook<AtkUnitBase.Delegates.FireCallback>? _fireCallbackHook;
    private bool _enabled;

    public CallbackLogger()
    {
        try
        {
            _fireCallbackHook = Svc.Hook.HookFromAddress<AtkUnitBase.Delegates.FireCallback>(
                (nint)AtkUnitBase.Addresses.FireCallback.Value, FireCallbackDetour);
        }
        catch (Exception ex)
        {
            Svc.Log.Error($"{Prefix} Couldn't create the FireCallback hook: {ex.Message}");
        }

        Svc.Framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var wanted = SupermarketSweep.Config.LogAddonCallbacks;
        if (wanted == _enabled)
            return;
        _enabled = wanted;

        if (wanted)
        {
            _fireCallbackHook?.Enable();
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, MarketboardAddons, OnReceiveEvent);
            Svc.Log.Information($"{Prefix} On. Logging callbacks while the marketboard is open.");
        }
        else
        {
            _fireCallbackHook?.Disable();
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, MarketboardAddons, OnReceiveEvent);
            Svc.Log.Information($"{Prefix} Off.");
        }
    }

    private bool FireCallbackDetour(AtkUnitBase* addon, uint valueCount, AtkValue* values, bool close)
    {
        try
        {
            // Any addon while the board is open, so the purchase confirmation shows up whatever it's called.
            if (addon != null && IsMarketboardOpen())
            {
                var decoded = Enumerable.Range(0, (int)valueCount).Select(i => Callback.DecodeValue(values[i]));
                Svc.Log.Information($"{Prefix} Callback {addon->NameString}: [{string.Join(", ", decoded)}] close={close}");
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error($"{Prefix} Logging failed: {ex.Message}");
        }

        return _fireCallbackHook!.Original(addon, valueCount, values, close);
    }

    private static void OnReceiveEvent(AddonEvent type, AddonArgs args)
    {
        // Clicks only; hover and focus events would drown everything else.
        if (args is not AddonReceiveEventArgs e || !e.AtkEventType.ToString().Contains("Click"))
            return;
        Svc.Log.Information($"{Prefix} Event {e.AddonName}: {e.AtkEventType} param={e.EventParam}");
    }

    private static bool IsMarketboardOpen() =>
        MarketboardAddons.Any(name => Svc.GameGui.GetAddonByName(name) != nint.Zero);

    public void Dispose()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;
        if (_enabled)
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, MarketboardAddons, OnReceiveEvent);
        _fireCallbackHook?.Dispose();
    }
}
