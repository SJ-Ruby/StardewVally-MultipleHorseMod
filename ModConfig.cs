using StardewModdingAPI;

namespace MultipleHorseMod;

/// <summary>User-editable options. The host's price and cap are authoritative in multiplayer.</summary>
internal sealed class ModConfig
{
    public SButton OpenStableMenu { get; set; } = SButton.F8;
    public int HorsePrice { get; set; } = 0;
    public int MaximumExtraHorses { get; set; } = 3;
}
