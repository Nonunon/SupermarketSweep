namespace SupermarketSweep.Models;

/// <summary>Whether the route (and the buyer) may buy a stack bigger than what's still needed.</summary>
public enum OverbuyMode
{
    /// <summary>Only stacks that fit; an item can come up short.</summary>
    Off = 0,

    /// <summary>A finishing stack may go a little past the need (percent or units, whichever allows more).</summary>
    Limited = 1,

    /// <summary>Any stack size, when that's the cheaper way to finish.</summary>
    Unlimited = 2,
}

public static class OverbuyModeExtensions
{
    public static string ToFriendlyString(this OverbuyMode mode) => mode switch
    {
        OverbuyMode.Off => "Off",
        OverbuyMode.Unlimited => "Unlimited",
        _ => "Limited",
    };

    public static string Description(this OverbuyMode mode) => mode switch
    {
        OverbuyMode.Off => "Only stacks that fit what's still needed. An item can come up short (897/900 when only 99-stacks are left).",
        OverbuyMode.Unlimited => "Any stack bigger than you need (say 99 when you need 3) when that's the cheaper way to finish.",
        _ => "The last stack may go a little past what you need: up to the percent of the need or the number of units,\n" +
             "whichever allows more. So 897/900 can finish with a small stack instead of stopping short.",
    };
}
