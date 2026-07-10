using System.Collections.Generic;

namespace MultipleHorseMod;

internal sealed class ModSaveData
{
    public List<SavedHorseData> Horses { get; set; } = new();
}

internal sealed class SavedHorseData
{
    public string HorseId { get; set; } = "";

    public string Name { get; set; } = "";

    public string Skin { get; set; } = "Default";

    public string Accessory { get; set; } = "None";

    public string Saddle { get; set; } = "None";

    public string CabinId { get; set; } = "";

    public long OwnerId { get; set; }

    public int CreatedDay { get; set; }

    public bool AppearanceOnly { get; set; }

    public string LocationName { get; set; } = "Farm";

    public int TileX { get; set; }

    public int TileY { get; set; }
}
