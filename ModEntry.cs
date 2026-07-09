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
    private ModConfig Config = null!;
    private readonly Dictionary<long, MountedHorseSnapshot> LastMountedHorses = new();

    public override void Entry(IModHelper helper)
    {
        this.Config = helper.ReadConfig<ModConfig>();

        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.Multiplayer.PeerConnected += this.OnPeerConnected;
        helper.Events.World.BuildingListChanged += this.OnBuildingListChanged;
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.EnsureFarmHasEnoughHorses("save loaded");
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        this.LastMountedHorses.Clear();
        this.EnsureFarmHasEnoughHorses("day started");
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!e.IsMultipleOf(60))
            return;

        this.RememberMountedHorses();
        this.RestoreMissingRecentlyMountedHorses();
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

    private void EnsureFarmHasEnoughHorses(string reason)
    {
        if (!Context.IsWorldReady)
            return;

        if (this.Config.HostOnly && !Context.IsMainPlayer)
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

        List<Farmer> farmers = this.GetOnlineFarmers();
        int wanted = farmers.Count;
        if (wanted <= 1 && !this.Config.AllowExtraHorsesInSinglePlayer)
            return;

        List<Horse> existingHorses = this.GetExistingHorses();
        Queue<Farmer> farmersNeedingHorse = new(
            farmers.Where(farmer => !existingHorses.Any(horse => horse.ownerId.Value == farmer.UniqueMultiplayerID))
        );

        foreach (Horse horse in existingHorses.Where(horse => horse.ownerId.Value == 0))
        {
            if (!farmersNeedingHorse.TryDequeue(out Farmer? owner) || owner is null)
                break;

            horse.ownerId.Value = owner.UniqueMultiplayerID;
        }

        if (farmersNeedingHorse.Count == 0)
            return;

        Stable stable = stables[0];
        Point origin = stable.GetDefaultHorseTile();

        int created = 0;
        while (farmersNeedingHorse.TryDequeue(out Farmer? owner) && owner is not null)
        {
            Vector2 tile = this.FindSpawnTile(farm, origin, created);
            this.AddHorse(farm, Guid.NewGuid(), owner.UniqueMultiplayerID, tile);
            created++;
        }

        this.Monitor.Log($"Created {created} extra horse(s) for {wanted} farmer(s), triggered by {reason}.", LogLevel.Info);
    }

    private void RememberMountedHorses()
    {
        if (!Context.IsWorldReady)
            return;

        foreach (Farmer farmer in this.GetOnlineFarmers())
        {
            if (farmer.mount is not Horse horse)
                continue;

            GameLocation? location = farmer.currentLocation ?? horse.currentLocation;
            if (location is null)
                continue;

            this.LastMountedHorses[farmer.UniqueMultiplayerID] = new MountedHorseSnapshot(
                horse.HorseId,
                farmer.UniqueMultiplayerID,
                location,
                farmer.Tile
            );
        }
    }

    private void RestoreMissingRecentlyMountedHorses()
    {
        if (!Context.IsWorldReady)
            return;

        if (this.Config.HostOnly && !Context.IsMainPlayer)
            return;

        List<Horse> existingHorses = this.GetExistingHorses();
        foreach (MountedHorseSnapshot snapshot in this.LastMountedHorses.Values.ToList())
        {
            if (existingHorses.Any(horse =>
                horse.HorseId == snapshot.HorseId
                || horse.ownerId.Value == snapshot.OwnerId))
            {
                continue;
            }

            Vector2 tile = this.FindSpawnTile(snapshot.Location, snapshot.Tile);
            this.AddHorse(snapshot.Location, snapshot.HorseId, snapshot.OwnerId, tile);
            this.Monitor.Log($"Restored horse for farmer {snapshot.OwnerId} after it disappeared on dismount.", LogLevel.Info);
        }
    }

    private Horse AddHorse(GameLocation location, Guid horseId, long ownerId, Vector2 tile)
    {
        Horse horse = new Horse(horseId, (int)tile.X, (int)tile.Y);
        horse.ownerId.Value = ownerId;
        horse.currentLocation = location;
        location.characters.Add(horse);
        return horse;
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

    private Vector2 FindSpawnTile(GameLocation location, Vector2 origin)
    {
        for (int radius = 0; radius <= 4; radius++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                for (int y = -radius; y <= radius; y++)
                {
                    if (Math.Abs(x) != radius && Math.Abs(y) != radius)
                        continue;

                    Vector2 tile = new(origin.X + x, origin.Y + y);
                    if (location.isTileLocationOpen(tile))
                        return tile;
                }
            }
        }

        return origin;
    }

    private sealed record MountedHorseSnapshot(Guid HorseId, long OwnerId, GameLocation Location, Vector2 Tile);
}
