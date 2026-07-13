using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;

namespace MultipleHorseMod;

/// <summary>All mutations run on the host. Extra horses are marked in modData, never attached to Stable.horseId.</summary>
internal sealed class HorseService
{
    private readonly ModEntry mod;

    internal HorseService(ModEntry mod) => this.mod = mod;

    internal IReadOnlyList<Horse> GetForStable(Stable stable)
    {
        string id = HorseData.StableId(stable.tileX.Value, stable.tileY.Value);
        List<Horse> horses = Game1.getFarm()?.characters.OfType<Horse>()
            .Where(IsExtraHorse)
            .Where(p => p.modData.TryGetValue(HorseData.StableKey, out string? stableId) && stableId == id)
            .OrderBy(p => p.modData.TryGetValue(HorseData.IdKey, out string? horseId) ? horseId : string.Empty)
            .ToList()
            ?? new List<Horse>();

        // A mounted horse isn't always present in Farm.characters. Include it explicitly so a
        // rider can never make a mod-owned horse disappear from this menu.
        foreach (Farmer farmer in Game1.getAllFarmers())
        {
            if (farmer.mount is not Horse horse || !IsExtraHorse(horse))
                continue;
            if (horse.modData.TryGetValue(HorseData.StableKey, out string? stableId) && stableId == id && !horses.Contains(horse))
                horses.Add(horse);
        }
        return horses;
    }

    internal int GetExtraHorseCount()
    {
        // Count the same complete set used by the menu. A mounted horse may disappear from
        // Farm.characters briefly, but it must still consume a purchase slot on the host.
        HashSet<Horse> horses = new(Game1.getFarm()?.characters.OfType<Horse>().Where(IsExtraHorse) ?? Enumerable.Empty<Horse>());
        foreach (Farmer farmer in Game1.getAllFarmers())
        {
            if (farmer.mount is Horse horse && IsExtraHorse(horse))
                horses.Add(horse);
        }
        return horses.Count;
    }

    internal bool TryBuy(long buyerId, int stableX, int stableY, string requestedName, out string message)
    {
        message = string.Empty;
        Farm? farm = Game1.getFarm();
        Farmer? buyer = Game1.GetPlayer(buyerId);
        if (!Context.IsMainPlayer || farm is null || buyer is null)
        {
            message = this.mod.T("purchase.invalid");
            return false;
        }

        Stable? stable = farm.buildings.OfType<Stable>().FirstOrDefault(p => p.tileX.Value == stableX && p.tileY.Value == stableY);
        if (stable is null)
        {
            message = this.mod.T("purchase.no-stable");
            return false;
        }
        if (this.GetExtraHorseCount() >= this.mod.ExtraHorseLimit)
        {
            message = this.mod.T("purchase.limit");
            return false;
        }
        string displayName = string.IsNullOrWhiteSpace(requestedName) ? HorseData.DefaultDisplayName : requestedName.Trim();
        displayName = displayName.Length > 12 ? displayName[..12] : displayName;
        int sequence = this.GetExtraHorseCount() + 1;
        Vector2 tile = FindSpawnTile(farm, stable);
        Horse horse = new(Guid.NewGuid(), (int)tile.X, (int)tile.Y)
        {
            // This save name deliberately cannot collide with any vanilla stable horse.
            Name = $"MultipleHorse_child{sequence}",
            displayName = displayName,
            Position = tile * Game1.tileSize,
            currentLocation = farm
        };
        horse.modData[HorseData.OwnedKey] = "true";
        horse.modData[HorseData.StableKey] = HorseData.StableId(stableX, stableY);
        horse.modData[HorseData.IdKey] = Guid.NewGuid().ToString("N");
        horse.modData[HorseData.NameKey] = displayName;

        farm.characters.Add(horse);
        message = this.mod.T("purchase.success", new { name = displayName });
        return true;
    }

    internal static bool IsExtraHorse(Horse horse) => horse.modData.ContainsKey(HorseData.OwnedKey);

    private static Vector2 FindSpawnTile(Farm farm, Stable stable)
    {
        int x = stable.tileX.Value + stable.tilesWide.Value + 1;
        int y = stable.tileY.Value + stable.tilesHigh.Value - 1;
        // Prefer the requested right-hand tile, then a small clear area around it.
        for (int radius = 0; radius <= 3; radius++)
        for (int dx = -radius; dx <= radius; dx++)
        for (int dy = -radius; dy <= radius; dy++)
        {
            Vector2 candidate = new(x + dx, y + dy);
            if (farm.isTileLocationOpen(candidate))
                return candidate;
        }
        return new Vector2(x, y);
    }
}
