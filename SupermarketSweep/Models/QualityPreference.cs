namespace SupermarketSweep.Models;

/// <summary>Which listings the route planner may buy for an item.</summary>
public enum QualityPreference
{
    /// <summary>Per item only: follow the route tab's default.</summary>
    Default = 0,

    /// <summary>Cheapest, HQ or NQ.</summary>
    Any = 1,

    /// <summary>HQ first; NQ only for whatever HQ listings can't cover.</summary>
    PreferHq = 2,

    /// <summary>HQ only; the item comes up short if there isn't enough HQ.</summary>
    HqOnly = 3,
}

public static class QualityPreferenceExtensions
{
    public static string ToFriendlyString(this QualityPreference quality) => quality switch
    {
        QualityPreference.Any => "Any quality",
        QualityPreference.PreferHq => "Prefer HQ",
        QualityPreference.HqOnly => "HQ only",
        _ => "Default",
    };
}
