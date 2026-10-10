namespace SupermarketSweep.Models;

/// <summary>How an automated buying run at one world ended (<see cref="MarketboardBuyer.LastOutcome"/>).</summary>
public enum BuyOutcome
{
    /// <summary>Went through every item it set out to buy.</summary>
    Done,

    /// <summary>Didn't start: the route has nothing for this world.</summary>
    NothingToBuy,

    /// <summary>The next purchase would have gone below the gil reserve (or past the gil on hand).</summary>
    OutOfGil,

    /// <summary>Stopped by the player (Stop, /shop stop, or No on the purchase dialog).</summary>
    Stopped,

    /// <summary>Something went wrong (a timeout, a mismatch, the board closing, an error).</summary>
    Problem,
}
