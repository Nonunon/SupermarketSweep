namespace SupermarketSweep.Models;

/// <summary>How much the buy assistant does by itself at the marketboard.</summary>
public enum BuyAutomation
{
    /// <summary>Outlines what to buy; every click is yours.</summary>
    OutlineOnly = 0,

    /// <summary>Clicks each recommended listing so the game's Yes/No appears; you press Yes.</summary>
    OpenConfirmation = 1,

    /// <summary>One button buys the open item's recommended listings (clicks the listing and Yes).</summary>
    BuyItem = 2,

    /// <summary>One button buys everything the route has for this world, item after item.</summary>
    BuyWorld = 3,
}

public static class BuyAutomationExtensions
{
    public static string ToFriendlyString(this BuyAutomation level) => level switch
    {
        BuyAutomation.OpenConfirmation => "Open confirmation (you press Yes)",
        BuyAutomation.BuyItem => "Buy per item",
        BuyAutomation.BuyWorld => "Buy whole world",
        _ => "Outline only",
    };

    public static string Description(this BuyAutomation level) => level switch
    {
        BuyAutomation.OpenConfirmation => "Clicks each recommended listing so the purchase Yes/No appears. You press Yes; pressing No stops.",
        BuyAutomation.BuyItem => "A button on the open item buys its recommended listings: clicks the listing, checks the dialog, presses Yes.",
        BuyAutomation.BuyWorld => "Also adds a button that buys everything the route has for this world, opening each item in turn.",
        _ => "Only outlines the item to pick and the listings to buy. Nothing is clicked for you.",
    };
}
