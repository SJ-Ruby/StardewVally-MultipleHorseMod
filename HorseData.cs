namespace MultipleHorseMod;

internal static class HorseData
{
    internal const string OwnedKey = "Mayn.MultipleHorse/IsExtraHorse";
    internal const string StableKey = "Mayn.MultipleHorse/Stable";
    internal const string IdKey = "Mayn.MultipleHorse/Id";
    internal const string NameKey = "Mayn.MultipleHorse/DisplayName";
    internal const string RiderKey = "Mayn.MultipleHorse/RiderName";
    internal const string DefaultDisplayName = "新马";

    internal static string StableId(int x, int y) => $"{x},{y}";
}
