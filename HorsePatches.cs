using HarmonyLib;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;

namespace MultipleHorseMod;

/// <summary>Lets marked horses resolve their home stable without modifying the vanilla horse stored by that stable.</summary>
internal static class HorsePatches
{
    internal static void Apply(string harmonyId)
    {
        new Harmony(harmonyId).Patch(
            AccessTools.Method(typeof(Horse), nameof(Horse.TryFindStable), new[] { typeof(GameLocation).MakeByRefType(), typeof(Stable).MakeByRefType() }),
            prefix: new HarmonyMethod(typeof(HorsePatches), nameof(TryFindStablePrefix)));
    }

    private static bool TryFindStablePrefix(Horse __instance, ref GameLocation location, ref Stable stable, ref bool __result)
    {
        if (!__instance.modData.TryGetValue(HorseData.StableKey, out string? stableId))
            return true;
        string[] parts = stableId.Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y))
        {
            __result = false;
            return false;
        }
        Farm? farm = Game1.getFarm();
        stable = farm?.buildings.OfType<Stable>().FirstOrDefault(p => p.tileX.Value == x && p.tileY.Value == y)!;
        location = farm!;
        __result = stable is not null;
        return false;
    }
}
