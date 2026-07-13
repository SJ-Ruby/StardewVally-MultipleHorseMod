using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Menus;

namespace MultipleHorseMod;

internal sealed class ModEntry : Mod
{
    private ModConfig config = null!;
    private HorseService horses = null!;
    private readonly Dictionary<string, List<StardewValley.Characters.Horse>> syncedStableHorses = new();

    public override void Entry(IModHelper helper)
    {
        this.config = helper.ReadConfig<ModConfig>();
        this.horses = new HorseService(this);
        HorsePatches.Apply(this.ModManifest.UniqueID);
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
        helper.Events.Multiplayer.ModMessageReceived += this.OnModMessageReceived;
    }

    internal int Price => 0;
    internal int ExtraHorseLimit => Math.Max(0, Math.Min(this.config.MaximumExtraHorses, Math.Max(0, Game1.getAllFarmers().Count() - 1)));
    internal int ExtraHorseCount => this.horses.GetExtraHorseCount();
    internal string T(string key, object? tokens = null) => this.Helper.Translation.Get(key, tokens).ToString();
    /// <summary>Returns the stable's vanilla horse first, followed by this mod's marked horses.</summary>
    internal IReadOnlyList<StardewValley.Characters.Horse> GetHorses(Stable stable)
    {
        string stableKey = HorseData.StableId(stable.tileX.Value, stable.tileY.Value);
        if (!Context.IsMainPlayer && this.syncedStableHorses.TryGetValue(stableKey, out List<StardewValley.Characters.Horse>? synced))
            return synced;

        List<StardewValley.Characters.Horse> result = new();
        StardewValley.Characters.Horse? vanillaHorse = stable.getStableHorse();
        // getStableHorse can return null while the horse is mounted or temporarily not resolved.
        // This mod never marks vanilla horses, so this safely restores the normal stable horse in a
        // one-stable setup (the intended use of this mod).
        vanillaHorse ??= Game1.getAllFarmers()
            .Select(p => p.mount)
            .OfType<StardewValley.Characters.Horse>()
            .FirstOrDefault(p => !HorseService.IsExtraHorse(p));
        vanillaHorse ??= Game1.getFarm()?.characters
            .OfType<StardewValley.Characters.Horse>()
            .FirstOrDefault(p => !HorseService.IsExtraHorse(p));
        if (vanillaHorse is not null)
            result.Add(vanillaHorse);
        result.AddRange(this.horses.GetForStable(stable));
        return result;
    }

    internal string GetHorseMenuName(StardewValley.Characters.Horse horse)
    {
        string name = horse.modData.TryGetValue(HorseData.NameKey, out string? savedName) && !string.IsNullOrWhiteSpace(savedName)
            ? savedName
            : horse.displayName;
        if (horse.modData.TryGetValue(HorseData.RiderKey, out string? syncedRider) && !string.IsNullOrWhiteSpace(syncedRider))
            return this.T("horse.ridden", new { name, rider = syncedRider });
        Farmer? rider = Game1.getAllFarmers().FirstOrDefault(p => ReferenceEquals(p.mount, horse));
        return rider is null ? name : this.T("horse.ridden", new { name, rider = rider.Name });
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady || e.Button != this.config.OpenStableMenu || Game1.activeClickableMenu is not null)
            return;
        if (!TryGetNearbyStable(out Stable? stable))
        {
            Game1.showRedMessage(this.T("menu.near-stable"));
            return;
        }
        this.RequestStableSnapshot(stable!);
        Game1.activeClickableMenu = new StableMenu(this, stable!);
    }

    internal void OpenNamingMenu(Stable stable)
    {
        // NamingMenu is the game's own naming prompt and keyboard handling.
        Game1.activeClickableMenu = new NamingMenu(name =>
        {
            this.RequestPurchase(stable, name);
            // The native naming menu doesn't close itself after its callback.
            Game1.exitActiveMenu();
        }, this.T("naming.title"), this.T("naming.default-name"));
    }

    private void RequestPurchase(Stable stable, string name)
    {
        PurchaseRequest request = new(stable.tileX.Value, stable.tileY.Value, name);
        if (Context.IsMainPlayer)
        {
            this.CompletePurchase(Game1.player.UniqueMultiplayerID, request, null);
            return;
        }
        this.Helper.Multiplayer.SendMessage(request, "BuyExtraHorse", new[] { this.ModManifest.UniqueID }, new[] { Game1.MasterPlayer.UniqueMultiplayerID });
    }

    private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        if (e.FromModID != this.ModManifest.UniqueID)
            return;
        if (e.Type == "BuyExtraHorse" && Context.IsMainPlayer)
            this.CompletePurchase(e.FromPlayerID, e.ReadAs<PurchaseRequest>(), e.FromPlayerID);
        else if (e.Type == "RequestStableHorses" && Context.IsMainPlayer)
            this.SendStableSnapshot(e.FromPlayerID, e.ReadAs<StableRequest>());
        else if (e.Type == "StableHorseSnapshot" && e.FromPlayerID == Game1.MasterPlayer.UniqueMultiplayerID)
            this.StoreStableSnapshot(e.ReadAs<StableSnapshot>());
        else if (e.Type == "BuyResult" && e.FromPlayerID == Game1.MasterPlayer.UniqueMultiplayerID)
        {
            PurchaseResult result = e.ReadAs<PurchaseResult>();
            if (result.Success) Game1.showGlobalMessage(result.Message); else Game1.showRedMessage(result.Message);
        }
    }

    private void CompletePurchase(long buyerId, PurchaseRequest request, long? replyTo)
    {
        bool success = this.horses.TryBuy(buyerId, request.StableX, request.StableY, request.Name, out string message);
        if (replyTo is long playerId)
            this.Helper.Multiplayer.SendMessage(new PurchaseResult(success, message), "BuyResult", new[] { this.ModManifest.UniqueID }, new[] { playerId });
        else if (success) Game1.showGlobalMessage(message); else Game1.showRedMessage(message);
    }

    private void RequestStableSnapshot(Stable stable)
    {
        if (!Context.IsMainPlayer)
            this.Helper.Multiplayer.SendMessage(new StableRequest(stable.tileX.Value, stable.tileY.Value), "RequestStableHorses", new[] { this.ModManifest.UniqueID }, new[] { Game1.MasterPlayer.UniqueMultiplayerID });
    }

    private void SendStableSnapshot(long playerId, StableRequest request)
    {
        Stable? stable = Game1.getFarm()?.buildings.OfType<Stable>().FirstOrDefault(p => p.tileX.Value == request.StableX && p.tileY.Value == request.StableY);
        if (stable is null)
            return;
        HorseSnapshot[] horses = this.GetHorses(stable).Select(horse => new HorseSnapshot(
            horse.Name,
            horse.modData.TryGetValue(HorseData.NameKey, out string? savedName) && !string.IsNullOrWhiteSpace(savedName) ? savedName : horse.displayName,
            HorseService.IsExtraHorse(horse),
            Game1.getAllFarmers().FirstOrDefault(player => ReferenceEquals(player.mount, horse))?.Name
        )).ToArray();
        this.Helper.Multiplayer.SendMessage(new StableSnapshot(request.StableX, request.StableY, horses), "StableHorseSnapshot", new[] { this.ModManifest.UniqueID }, new[] { playerId });
    }

    private void StoreStableSnapshot(StableSnapshot snapshot)
    {
        List<StardewValley.Characters.Horse> horses = new();
        foreach (HorseSnapshot horseData in snapshot.Horses)
        {
            StardewValley.Characters.Horse horse = new(Guid.NewGuid(), 0, 0) { Name = horseData.InternalName, displayName = horseData.DisplayName };
            if (horseData.IsExtra)
                horse.modData[HorseData.OwnedKey] = "true";
            horse.modData[HorseData.NameKey] = horseData.DisplayName;
            if (!string.IsNullOrWhiteSpace(horseData.RiderName))
                horse.modData[HorseData.RiderKey] = horseData.RiderName;
            horses.Add(horse);
        }
        this.syncedStableHorses[HorseData.StableId(snapshot.StableX, snapshot.StableY)] = horses;
    }

    private static bool TryGetNearbyStable(out Stable? found)
    {
        found = null;
        Farm? farm = Game1.currentLocation as Farm ?? Game1.getFarm();
        if (farm is null) return false;
        foreach (Building building in farm.buildings)
        {
            if (building is not Stable stable) continue;
            Rectangle bounds = new(stable.tileX.Value - 1, stable.tileY.Value - 1, stable.tilesWide.Value + 2, stable.tilesHigh.Value + 2);
            if (bounds.Contains(Game1.player.TilePoint)) { found = stable; return true; }
        }
        return false;
    }
}

internal sealed record PurchaseRequest(int StableX, int StableY, string Name);
internal sealed record PurchaseResult(bool Success, string Message);
internal sealed record StableRequest(int StableX, int StableY);
internal sealed record StableSnapshot(int StableX, int StableY, HorseSnapshot[] Horses);
internal sealed record HorseSnapshot(string InternalName, string DisplayName, bool IsExtra, string? RiderName);
