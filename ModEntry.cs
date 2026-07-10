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
    private readonly Dictionary<Guid, Horse> ManagedHorseInstances = new();
    private readonly Dictionary<long, Horse> LastMountedHorseByFarmer = new();

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
        this.ManagedHorseInstances.Clear();
        this.LastMountedHorseByFarmer.Clear();
        this.LoadSaveData();
        this.LoadSavedHorses();
        this.EnsureFarmHasEnoughHorses("save loaded");
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        this.LoadSavedHorses();
        this.EnsureFarmHasEnoughHorses("day started");
        this.ReturnCabinHorsesHome();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!this.CanManageHorses())
            return;

        this.ReattachDismountedHorses();

        if (!e.IsMultipleOf(60))
            return;

        this.CleanupDeletedCabinHorses();
        this.ApplyCabinOwnership();
        this.CaptureManagedHorses();
    }

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        if (!this.CanManageHorses())
            return;

        this.ApplyCabinOwnership();
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
        this.ApplyCabinOwnership();
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
                this.ManagedHorseInstances[horseId] = existingHorse;
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

        List<Building> cabins = this.GetCabinBuildings(farm);
        this.AssignLegacyHorsesToCabins(cabins);

        HashSet<string> cabinIds = cabins.Select(cabin => cabin.id.Value.ToString()).ToHashSet();
        this.RemoveHorsesForDeletedCabins(cabinIds);

        List<Horse> existingHorses = this.GetExistingHorses();
        int created = 0;

        foreach (Building cabin in cabins)
        {
            string cabinId = cabin.id.Value.ToString();
            SavedHorseData? savedHorse = this.SaveData.Horses.FirstOrDefault(data => data.CabinId == cabinId);
            if (savedHorse is not null)
                continue;

            Vector2 homeTile = this.GetCabinHorseTile(farm, cabin, existingHorses.Count);
            Horse horse = this.AddManagedHorse(farm, Guid.NewGuid(), homeTile);
            savedHorse = this.CreateSavedHorseData(horse);
            savedHorse.CabinId = cabinId;
            savedHorse.Name = this.CreateDefaultHorseName(this.SaveData.Horses.Count + 1);
            this.UpdateCabinBinding(savedHorse, cabin);
            this.ApplySavedHorseData(horse, savedHorse);
            this.SaveData.Horses.Add(savedHorse);
            existingHorses.Add(horse);
            created++;
        }

        this.ApplyCabinOwnership();
        this.WriteSaveData();

        if (created > 0)
            this.Monitor.Log($"Created {created} cabin-bound horse(s), triggered by {reason}.", LogLevel.Info);
    }

    private bool CanManageHorses()
    {
        if (!Context.IsWorldReady)
            return false;

        return !this.Config.HostOnly || Context.IsMainPlayer;
    }

    private Horse AddManagedHorse(GameLocation location, Guid horseId, Vector2 tile)
    {
        Horse horse = new(horseId, (int)tile.X, (int)tile.Y);
        horse.ownerId.Value = 0;
        horse.currentLocation = location;
        location.characters.Add(horse);
        this.ManagedHorseIds.Add(horseId);
        this.ManagedHorseInstances[horseId] = horse;
        return horse;
    }

    private void ApplySavedHorseData(Horse horse, SavedHorseData savedHorse)
    {
        if (!string.IsNullOrWhiteSpace(savedHorse.Name))
            horse.Name = savedHorse.Name;

        if (int.TryParse(savedHorse.Skin, out int skinId) && skinId > 0)
            horse.Manners = skinId;

        horse.ownerId.Value = savedHorse.OwnerId;
    }

    private List<Building> GetCabinBuildings(Farm farm)
    {
        return farm.buildings
            .Where(building => building.isCabin && building.daysOfConstructionLeft.Value <= 0)
            .OrderBy(building => building.id.Value)
            .ToList();
    }

    private void AssignLegacyHorsesToCabins(List<Building> cabins)
    {
        HashSet<string> assignedCabinIds = this.SaveData.Horses
            .Where(horse => !string.IsNullOrWhiteSpace(horse.CabinId))
            .Select(horse => horse.CabinId)
            .ToHashSet();

        Queue<Building> availableCabins = new(cabins.Where(cabin => !assignedCabinIds.Contains(cabin.id.Value.ToString())));

        foreach (SavedHorseData savedHorse in this.SaveData.Horses.Where(horse => string.IsNullOrWhiteSpace(horse.CabinId)))
        {
            if (!availableCabins.TryDequeue(out Building? cabin))
                break;

            savedHorse.CabinId = cabin.id.Value.ToString();
            this.UpdateCabinBinding(savedHorse, cabin);
        }
    }

    private void RemoveHorsesForDeletedCabins(HashSet<string> cabinIds)
    {
        HashSet<Guid> mountedHorseIds = this.GetOnlineFarmers()
            .Select(farmer => farmer.mount)
            .OfType<Horse>()
            .Select(horse => horse.HorseId)
            .ToHashSet();

        List<SavedHorseData> removed = this.SaveData.Horses
            .Where(horse => string.IsNullOrWhiteSpace(horse.CabinId) || !cabinIds.Contains(horse.CabinId))
            .Where(horse => !Guid.TryParse(horse.HorseId, out Guid horseId) || !mountedHorseIds.Contains(horseId))
            .ToList();

        foreach (SavedHorseData savedHorse in removed)
        {
            if (!Guid.TryParse(savedHorse.HorseId, out Guid horseId))
                continue;

            foreach (GameLocation location in Game1.locations)
            {
                Horse? horse = location.characters.OfType<Horse>().FirstOrDefault(candidate => candidate.HorseId == horseId);
                if (horse is not null)
                    location.characters.Remove(horse);
            }

            this.ManagedHorseIds.Remove(horseId);
            this.ManagedHorseInstances.Remove(horseId);
        }

        this.SaveData.Horses.RemoveAll(removed.Contains);
    }

    private void CleanupDeletedCabinHorses()
    {
        Farm? farm = Game1.getFarm();
        if (farm is null)
            return;

        HashSet<string> cabinIds = this.GetCabinBuildings(farm)
            .Select(cabin => cabin.id.Value.ToString())
            .ToHashSet();
        this.RemoveHorsesForDeletedCabins(cabinIds);
    }

    private void ApplyCabinOwnership()
    {
        Farm? farm = Game1.getFarm();
        if (farm is null)
            return;

        Dictionary<string, Building> cabins = this.GetCabinBuildings(farm)
            .ToDictionary(cabin => cabin.id.Value.ToString());

        foreach (SavedHorseData savedHorse in this.SaveData.Horses)
        {
            if (!cabins.TryGetValue(savedHorse.CabinId, out Building? cabin))
                continue;

            this.UpdateCabinBinding(savedHorse, cabin);

            if (Guid.TryParse(savedHorse.HorseId, out Guid horseId)
                && this.ManagedHorseInstances.TryGetValue(horseId, out Horse? horse)
                && horse.ownerId.Value != savedHorse.OwnerId)
            {
                horse.ownerId.Value = savedHorse.OwnerId;
            }
        }
    }

    private void UpdateCabinBinding(SavedHorseData savedHorse, Building cabin)
    {
        Cabin? indoors = cabin.GetIndoors() as Cabin;
        savedHorse.OwnerId = indoors?.owner?.UniqueMultiplayerID ?? 0;
    }

    private Vector2 GetCabinHorseTile(Farm farm, Building cabin, int offset)
    {
        Point origin = new(
            cabin.tileX.Value + Math.Max(1, cabin.tilesWide.Value / 2),
            cabin.tileY.Value + cabin.tilesHigh.Value);

        return this.FindSpawnTile(farm, origin, offset);
    }

    private void ReturnCabinHorsesHome()
    {
        Farm? farm = Game1.getFarm();
        if (farm is null)
            return;

        Dictionary<string, Building> cabins = this.GetCabinBuildings(farm)
            .ToDictionary(cabin => cabin.id.Value.ToString());
        int offset = 0;

        foreach (SavedHorseData savedHorse in this.SaveData.Horses)
        {
            if (!cabins.TryGetValue(savedHorse.CabinId, out Building? cabin)
                || !Guid.TryParse(savedHorse.HorseId, out Guid horseId)
                || !this.ManagedHorseInstances.TryGetValue(horseId, out Horse? horse))
            {
                continue;
            }

            foreach (GameLocation location in Game1.locations)
            {
                if (location != farm && location.characters.Contains(horse))
                    location.characters.Remove(horse);
            }

            Vector2 homeTile = this.GetCabinHorseTile(farm, cabin, offset++);
            horse.currentLocation = farm;
            horse.setTileLocation(homeTile);
            if (!farm.characters.Contains(horse))
                farm.characters.Add(horse);

            savedHorse.LocationName = farm.NameOrUniqueName;
            savedHorse.TileX = (int)homeTile.X;
            savedHorse.TileY = (int)homeTile.Y;
        }
    }

    private void ReattachDismountedHorses()
    {
        List<Farmer> onlineFarmers = this.GetOnlineFarmers();
        HashSet<long> onlineFarmerIds = onlineFarmers
            .Select(farmer => farmer.UniqueMultiplayerID)
            .ToHashSet();

        foreach (long farmerId in this.LastMountedHorseByFarmer.Keys
            .Where(id => !onlineFarmerIds.Contains(id))
            .ToList())
        {
            this.LastMountedHorseByFarmer.Remove(farmerId);
        }

        foreach (Farmer farmer in onlineFarmers)
        {
            long farmerId = farmer.UniqueMultiplayerID;

            if (farmer.mount is Horse mountedHorse)
            {
                if (this.ManagedHorseIds.Contains(mountedHorse.HorseId))
                {
                    this.ManagedHorseInstances[mountedHorse.HorseId] = mountedHorse;
                    this.LastMountedHorseByFarmer[farmerId] = mountedHorse;
                }

                continue;
            }

            if (!this.LastMountedHorseByFarmer.Remove(farmerId, out Horse? dismountedHorse))
                continue;

            if (!this.ManagedHorseIds.Contains(dismountedHorse.HorseId))
                continue;

            // The vanilla game can drop a dynamically-created horse during the handoff
            // from Farmer.mount back to GameLocation.characters. Reattach the same object
            // after that handoff; never create a replacement horse here.
            bool isAlreadyRegistered = Game1.locations.Any(location =>
                location.characters.OfType<Horse>().Any(horse => horse.HorseId == dismountedHorse.HorseId));

            if (isAlreadyRegistered)
                continue;

            GameLocation location = farmer.currentLocation ?? dismountedHorse.currentLocation ?? Game1.getFarm();
            dismountedHorse.currentLocation = location;
            dismountedHorse.Position = farmer.Position;
            location.characters.Add(dismountedHorse);
            this.Monitor.Log($"Reattached managed horse {dismountedHorse.HorseId} after farmer {farmerId} dismounted.", LogLevel.Trace);
        }

        HashSet<Guid> mountedHorseIds = onlineFarmers
            .Select(farmer => farmer.mount)
            .OfType<Horse>()
            .Select(horse => horse.HorseId)
            .ToHashSet();

        HashSet<Guid> registeredHorseIds = Game1.locations
            .SelectMany(location => location.characters.OfType<Horse>())
            .Select(horse => horse.HorseId)
            .ToHashSet();

        foreach ((Guid horseId, Horse horse) in this.ManagedHorseInstances.ToList())
        {
            if (mountedHorseIds.Contains(horseId) || registeredHorseIds.Contains(horseId))
                continue;

            GameLocation location = horse.currentLocation ?? Game1.getFarm();
            horse.currentLocation = location;
            location.characters.Add(horse);
            registeredHorseIds.Add(horseId);
            this.Monitor.Log($"Restored managed horse instance {horseId} after it was removed from its location.", LogLevel.Trace);
        }
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
            savedHorse.CabinId ??= "";
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
            CabinId = existingData?.CabinId ?? "",
            OwnerId = existingData?.OwnerId ?? horse.ownerId.Value,
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
