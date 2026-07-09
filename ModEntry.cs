using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.Locations;

namespace MultipleHorseMod;

public sealed class ModEntry : Mod
{
    private const string SaveDataKey = "multiple-horse-data";

    private ModConfig Config = null!;
    private ModSaveData SaveData = new();
    private readonly HashSet<Guid> ManagedHorseIds = new();

    public override void Entry(IModHelper helper)
    {
        this.Config = helper.ReadConfig<ModConfig>();

        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.GameLoop.Saving += this.OnSaving;
        helper.Events.Multiplayer.PeerConnected += this.OnPeerConnected;
        helper.Events.World.BuildingListChanged += this.OnBuildingListChanged;
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.LoadSaveData();
        this.LoadSavedHorses();
        this.EnsureFarmHasEnoughHorses("save loaded");
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        this.LoadSavedHorses();
        this.EnsureFarmHasEnoughHorses("day started");
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!e.IsMultipleOf(60) || !this.CanManageHorses())
            return;

        this.ReRegisterMissingManagedHorses("runtime check");
        this.ClearHorseOwners();
        this.CaptureManagedHorses();
    }

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        if (!this.CanManageHorses())
            return;

        this.ReRegisterMissingManagedHorses("saving");
        this.ClearHorseOwners();
        this.CaptureManagedHorses();
        this.WriteSaveData();
    }

    private void OnPeerConnected(object? sender, PeerConnectedEventArgs e)
    {
        this.EnsureFarmHasEnoughHorses("peer connected");
    }

    private void OnBuildingListChanged(object? sender, BuildingListChangedEventArgs e)
    {
        if (e.Location is Farm)
            this.EnsureFarmHasEnoughHorses("building list changed");
    }

    private void LoadSaveData()
    {
        this.SaveData = this.Helper.Data.ReadSaveData<ModSaveData>(SaveDataKey) ?? new ModSaveData();
        this.NormalizeSaveData();
    }

    private void LoadSavedHorses()
    {
        if (!this.CanManageHorses())
            return;

        this.ReRegisterMissingManagedHorses("save data load");
        this.ClearHorseOwners();
    }

    private void ReRegisterMissingManagedHorses(string reason)
    {
        List<Horse> existingHorses = this.GetExistingHorses();
        int restored = 0;

        foreach (SavedHorseData savedHorse in this.SaveData.Horses)
        {
            if (!Guid.TryParse(savedHorse.HorseId, out Guid horseId))
                continue;

            Horse? existingHorse = existingHorses.FirstOrDefault(horse => horse.HorseId == horseId);
            if (existingHorse is not null)
            {
                this.ApplySavedHorseData(existingHorse, savedHorse);
                continue;
            }

            GameLocation location = Game1.getLocationFromName(savedHorse.LocationName) ?? Game1.getFarm();
            Vector2 tile = new(savedHorse.TileX, savedHorse.TileY);
            Horse horse = this.AddManagedHorse(location, horseId, tile);
            this.ApplySavedHorseData(horse, savedHorse);
            existingHorses.Add(horse);
            restored++;
        }

        if (restored > 0)
            this.Monitor.Log($"Re-registered {restored} managed horse(s), triggered by {reason}.", LogLevel.Trace);
    }

    private void EnsureFarmHasEnoughHorses(string reason)
    {
        if (!this.CanManageHorses())
            return;

        Farm? farm = Game1.getFarm();
        if (farm is null)
            return;

        List<Stable> stables = farm.buildings
            .OfType<Stable>()
            .Where(stable => stable.daysOfConstructionLeft.Value <= 0)
            .ToList();

        if (stables.Count == 0)
            return;

        this.ReRegisterMissingManagedHorses(reason);
        this.ClearHorseOwners();

        int wanted = this.GetWantedHorseCount();
        if (wanted <= 1 && !this.Config.AllowExtraHorsesInSinglePlayer)
            return;

        List<Horse> existingHorses = this.GetExistingHorses();
        int unmanagedHorseCount = existingHorses.Count(horse => !this.ManagedHorseIds.Contains(horse.HorseId));
        int managedHorseCount = this.ManagedHorseIds.Count;
        int missingCount = Math.Max(0, wanted - unmanagedHorseCount - managedHorseCount);
        if (missingCount == 0)
            return;

        Stable stable = stables[0];
        Point origin = stable.GetDefaultHorseTile();

        for (int index = 0; index < missingCount; index++)
        {
            Vector2 tile = this.FindSpawnTile(farm, origin, existingHorses.Count + index);
            Horse horse = this.AddManagedHorse(farm, Guid.NewGuid(), tile);
            SavedHorseData savedHorse = this.CreateSavedHorseData(horse);
            savedHorse.Name = this.CreateDefaultHorseName(managedHorseCount + index + 1);
            this.ApplySavedHorseData(horse, savedHorse);
            this.SaveData.Horses.Add(savedHorse);
        }

        this.WriteSaveData();
        this.Monitor.Log($"Created {missingCount} shared horse(s) for {wanted} farmer(s), triggered by {reason}.", LogLevel.Info);
    }

    private bool CanManageHorses()
    {
        if (!Context.IsWorldReady)
            return false;

        return !this.Config.HostOnly || Context.IsMainPlayer;
    }

    private int GetWantedHorseCount()
    {
        List<Farmer> farmers = Game1.getAllFarmers()?.ToList() ?? new List<Farmer>();

        if (!farmers.Any(farmer => farmer.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID))
            farmers.Insert(0, Game1.player);

        return farmers
            .GroupBy(farmer => farmer.UniqueMultiplayerID)
            .Count();
    }

    private Horse AddManagedHorse(GameLocation location, Guid horseId, Vector2 tile)
    {
        Horse horse = new(horseId, (int)tile.X, (int)tile.Y);
        horse.ownerId.Value = 0;
        horse.currentLocation = location;
        location.characters.Add(horse);
        this.ManagedHorseIds.Add(horseId);
        return horse;
    }

    private void ApplySavedHorseData(Horse horse, SavedHorseData savedHorse)
    {
        if (!string.IsNullOrWhiteSpace(savedHorse.Name))
            horse.Name = savedHorse.Name;

        if (int.TryParse(savedHorse.Skin, out int skinId) && skinId > 0)
            horse.Manners = skinId;
    }

    private void ClearHorseOwners()
    {
        foreach (Horse horse in this.GetExistingHorses())
            horse.ownerId.Value = 0;
    }

    private void NormalizeSaveData()
    {
        this.ManagedHorseIds.Clear();
        List<SavedHorseData> normalized = new();

        foreach (SavedHorseData savedHorse in this.SaveData.Horses)
        {
            if (!Guid.TryParse(savedHorse.HorseId, out Guid horseId))
                continue;

            if (!this.ManagedHorseIds.Add(horseId))
                continue;

            if (string.IsNullOrWhiteSpace(savedHorse.Name))
                savedHorse.Name = this.CreateDefaultHorseName(normalized.Count + 1);

            savedHorse.Skin ??= "";
            normalized.Add(savedHorse);
        }

        this.SaveData.Horses = normalized;
    }

    private List<Horse> GetExistingHorses()
    {
        IEnumerable<Horse> locationHorses = Game1.locations.SelectMany(location => location.characters.OfType<Horse>());
        IEnumerable<Horse> mountedHorses = this.GetOnlineFarmers()
            .Select(farmer => farmer.mount)
            .OfType<Horse>();

        return locationHorses
            .Concat(mountedHorses)
            .GroupBy(horse => horse.HorseId)
            .Select(group => group.First())
            .ToList();
    }

    private List<Farmer> GetOnlineFarmers()
    {
        List<Farmer> farmers = Game1.getOnlineFarmers()?.ToList() ?? new List<Farmer>();

        if (!farmers.Any(farmer => farmer.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID))
            farmers.Insert(0, Game1.player);

        return farmers
            .GroupBy(farmer => farmer.UniqueMultiplayerID)
            .Select(group => group.First())
            .ToList();
    }

    private void CaptureManagedHorses()
    {
        this.SaveData.Horses = this.GetExistingHorses()
            .Where(horse => this.ManagedHorseIds.Contains(horse.HorseId))
            .Select(this.CreateSavedHorseData)
            .ToList();
    }

    private SavedHorseData CreateSavedHorseData(Horse horse)
    {
        GameLocation location = horse.currentLocation ?? Game1.getFarm();
        Vector2 tile = horse.Tile;
        SavedHorseData? existingData = this.GetSavedHorseData(horse.HorseId);

        return new SavedHorseData
        {
            HorseId = horse.HorseId.ToString(),
            Name = this.GetHorseName(horse, existingData),
            Skin = this.GetHorseSkin(horse, existingData),
            LocationName = location.NameOrUniqueName,
            TileX = (int)tile.X,
            TileY = (int)tile.Y
        };
    }

    private SavedHorseData? GetSavedHorseData(Guid horseId)
    {
        string horseIdText = horseId.ToString();
        return this.SaveData.Horses.FirstOrDefault(horse => horse.HorseId == horseIdText);
    }

    private string GetHorseName(Horse horse, SavedHorseData? existingData)
    {
        if (!string.IsNullOrWhiteSpace(horse.Name))
            return horse.Name;

        if (!string.IsNullOrWhiteSpace(existingData?.Name))
            return existingData.Name;

        return this.CreateDefaultHorseName(this.ManagedHorseIds.Count);
    }

    private string GetHorseSkin(Horse horse, SavedHorseData? existingData)
    {
        if (horse.Manners > 0)
            return horse.Manners.ToString();

        return existingData?.Skin ?? "";
    }

    private string CreateDefaultHorseName(int index)
    {
        return $"Shared Horse {Math.Max(1, index)}";
    }

    private void WriteSaveData()
    {
        this.Helper.Data.WriteSaveData(SaveDataKey, this.SaveData);
    }

    private Vector2 FindSpawnTile(Farm farm, Point origin, int offset)
    {
        for (int radius = 0; radius <= 8; radius++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                for (int y = -radius; y <= radius; y++)
                {
                    if (Math.Abs(x) != radius && Math.Abs(y) != radius)
                        continue;

                    Vector2 tile = new(origin.X + x + offset, origin.Y + y);
                    if (farm.isTileLocationOpen(tile) && farm.isTilePlaceable(tile))
                        return tile;
                }
            }
        }

        return new Vector2(origin.X + offset, origin.Y + 1);
    }
}
