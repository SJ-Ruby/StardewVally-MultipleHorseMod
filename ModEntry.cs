using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.GameData.FloorsAndPaths;

namespace MultipleHorseMod;

public sealed class ModEntry : Mod
{
    private const string SaveDataKey = "multiple-horse-data";
    private const string HorseTexturePrefix = "Mods/Mayn.MultipleHorseMod/Horses/";
    private const string HorseColorModDataKey = "Mayn.MultipleHorseMod/Color";
    private static readonly string[] HorseColors = { "Default", "Black", "White", "Gray", "Chestnut", "Golden" };

    private ModConfig Config = null!;
    private ModSaveData SaveData = new();
    private readonly HashSet<Guid> ManagedHorseIds = new();
    private readonly Dictionary<Guid, Horse> ManagedHorseInstances = new();
    private readonly Dictionary<long, Horse> LastMountedHorseByFarmer = new();
    private readonly Dictionary<Guid, string> AppliedHorseColors = new();
    private readonly Dictionary<string, Texture2D> ExternalHorseTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> ExternalSkins = new();
    private readonly List<string> ExternalAccessories = new() { "None" };
    private readonly List<string> ExternalSaddles = new() { "None" };
    private string? ExternalAssetsPath;
    private Texture2D? CrystalFloorTexture;
    private Point CrystalFloorCorner;

    public override void Entry(IModHelper helper)
    {
        this.Config = helper.ReadConfig<ModConfig>();
        this.DiscoverElleAssets();

        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.GameLoop.Saving += this.OnSaving;
        helper.Events.Multiplayer.PeerConnected += this.OnPeerConnected;
        helper.Events.World.BuildingListChanged += this.OnBuildingListChanged;
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
        helper.Events.Content.AssetRequested += this.OnAssetRequested;
        helper.Events.Display.RenderedStep += this.OnRenderedStep;
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.ManagedHorseInstances.Clear();
        this.LastMountedHorseByFarmer.Clear();
        this.AppliedHorseColors.Clear();
        this.LoadSaveData();
        this.LoadSavedHorses();
        this.EnsureFarmHasEnoughHorses("save loaded");
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        this.LoadSavedHorses();
        this.EnsureFarmHasEnoughHorses("day started");
        this.ReturnEligibleCabinHorsesHome();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady)
            return;

        if (e.IsMultipleOf(30))
            this.ApplySyncedHorseColors();

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

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsPlayerFree || !this.CanManageHorses())
            return;

        if (e.Button == SButton.MouseRight && this.TryMountManagedHorse(e.Cursor.GrabTile))
        {
            this.Helper.Input.Suppress(SButton.MouseRight);
            return;
        }

        if (e.Button == SButton.MouseRight && this.TryOpenCustomizationMenu())
        {
            this.Helper.Input.Suppress(SButton.MouseRight);
            return;
        }

        if (e.Button == SButton.F3)
        {
            this.CycleNearbyHorseColor();
            this.Helper.Input.Suppress(SButton.F3);
            return;
        }

        if (e.Button != SButton.F2)
            return;

        Horse? horse = Game1.player.mount as Horse;
        horse ??= Game1.currentLocation.characters
            .OfType<Horse>()
            .Where(candidate => Vector2.Distance(candidate.Tile, Game1.player.Tile) <= 3f)
            .OrderBy(candidate => Vector2.DistanceSquared(candidate.Tile, Game1.player.Tile))
            .FirstOrDefault();

        if (horse is null)
        {
            Game1.addHUDMessage(new HUDMessage("请靠近要改名的马，再按 F2。", HUDMessage.error_type));
            return;
        }

        Horse selectedHorse = horse;
        Game1.activeClickableMenu = new NamingMenu(
            name => this.RenameHorse(selectedHorse, name),
            "给马匹命名",
            this.GetHorseName(selectedHorse, this.GetSavedHorseData(selectedHorse.HorseId)));
        this.Helper.Input.Suppress(SButton.F2);
    }

    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        foreach ((string assetName, Texture2D texture) in this.ExternalHorseTextures)
        {
            if (e.NameWithoutLocale.IsEquivalentTo(assetName))
            {
                Texture2D selectedTexture = texture;
                e.LoadFrom(() => selectedTexture, AssetLoadPriority.Exclusive);
                return;
            }
        }

        foreach (string color in HorseColors.Where(color => color != "Default"))
        {
            if (e.NameWithoutLocale.IsEquivalentTo(HorseTexturePrefix + color))
            {
                string selectedColor = color;
                e.LoadFrom(() => this.CreateHorseTexture(selectedColor), AssetLoadPriority.Exclusive);
                return;
            }
        }
    }

    private void OnRenderedStep(object? sender, RenderedStepEventArgs e)
    {
        if (e.Step != StardewValley.Mods.RenderSteps.World_Background
            || !Context.IsWorldReady
            || Game1.currentLocation is not Farm farm)
            return;

        foreach (Stable stable in farm.buildings.OfType<Stable>().Where(stable => stable.daysOfConstructionLeft.Value <= 0))
        {
            this.LoadCrystalFloorTexture();
            foreach (Vector2 tile in this.GetCustomizationTiles(stable))
            {
                Vector2 screen = Game1.GlobalToLocal(Game1.viewport, tile * Game1.tileSize);
                Rectangle destination = new((int)screen.X, (int)screen.Y, Game1.tileSize, Game1.tileSize);
                Rectangle source = new(this.CrystalFloorCorner.X, this.CrystalFloorCorner.Y, 16, 16);
                if (this.CrystalFloorTexture is not null)
                    e.SpriteBatch.Draw(this.CrystalFloorTexture, destination, source, Color.White);
            }
        }
    }

    private void LoadCrystalFloorTexture()
    {
        if (this.CrystalFloorTexture is not null)
            return;

        Dictionary<string, FloorPathData> floors = Game1.content.Load<Dictionary<string, FloorPathData>>("Data/FloorsAndPaths");
        FloorPathData? crystal = floors.Values.FirstOrDefault(data => data.Id == "3" || data.ItemId == "333");
        if (crystal is null)
            return;
        this.CrystalFloorTexture = Game1.content.Load<Texture2D>(crystal.Texture);
        this.CrystalFloorCorner = crystal.Corner;
    }

    private void CycleNearbyHorseColor()
    {
        if (Game1.currentLocation is not Farm farm || !this.IsPlayerNearStable(farm))
        {
            Game1.addHUDMessage(new HUDMessage("请在马厩附近靠近一匹马，再按 F3。", HUDMessage.error_type));
            return;
        }

        Horse? horse = Game1.player.mount as Horse;
        horse ??= farm.characters
            .OfType<Horse>()
            .Where(candidate => Vector2.Distance(candidate.Tile, Game1.player.Tile) <= 4f)
            .OrderBy(candidate => Vector2.DistanceSquared(candidate.Tile, Game1.player.Tile))
            .FirstOrDefault();

        if (horse is null)
        {
            Game1.addHUDMessage(new HUDMessage("马厩附近没有可换色的马。", HUDMessage.error_type));
            return;
        }

        SavedHorseData? savedHorse = this.GetSavedHorseData(horse.HorseId);
        if (savedHorse is null)
        {
            Game1.addHUDMessage(new HUDMessage("这匹马不由本模组管理，无法保存颜色。", HUDMessage.error_type));
            return;
        }

        int currentIndex = Array.FindIndex(HorseColors, color => color.Equals(savedHorse.Skin, StringComparison.OrdinalIgnoreCase));
        string nextColor = HorseColors[(Math.Max(0, currentIndex) + 1) % HorseColors.Length];
        savedHorse.Skin = nextColor;
        this.ApplyHorseColor(horse, nextColor);
        this.WriteSaveData();
        Game1.addHUDMessage(new HUDMessage($"{this.GetHorseName(horse, savedHorse)} 的颜色：{nextColor}"));
    }

    private bool IsPlayerNearStable(Farm farm)
    {
        return farm.buildings.OfType<Stable>().Any(stable =>
        {
            float centerX = stable.tileX.Value + stable.tilesWide.Value / 2f;
            float centerY = stable.tileY.Value + stable.tilesHigh.Value / 2f;
            return Vector2.Distance(Game1.player.Tile, new Vector2(centerX, centerY)) <= 8f;
        });
    }

    private void DiscoverElleAssets()
    {
        DirectoryInfo? modsDirectory = Directory.GetParent(this.Helper.DirectoryPath);
        if (modsDirectory is null)
            return;

        foreach (DirectoryInfo directory in modsDirectory.EnumerateDirectories("*", SearchOption.AllDirectories))
        {
            string horsePath = Path.Combine(directory.FullName, "assets", "Horse");
            string saddlePath = Path.Combine(directory.FullName, "assets", "Saddles");
            string manifestPath = Path.Combine(directory.FullName, "manifest.json");
            if (!Directory.Exists(horsePath) || !Directory.Exists(saddlePath) || !File.Exists(manifestPath))
                continue;
            if (!File.ReadAllText(manifestPath).Contains("Elle.CuterHorses", StringComparison.OrdinalIgnoreCase))
                continue;

            this.ExternalAssetsPath = Path.Combine(directory.FullName, "assets");
            this.ExternalSkins.AddRange(Directory.EnumerateFiles(horsePath, "*.png")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => name is not null && name != "PrismaticOverlay")!);
            this.ExternalAccessories.AddRange(Directory.EnumerateFiles(saddlePath, "Bridle_*.png").Select(path => Path.GetFileNameWithoutExtension(path)[7..]));
            this.ExternalSaddles.AddRange(Directory.EnumerateFiles(saddlePath, "Saddle_*.png").Select(path => Path.GetFileNameWithoutExtension(path)[7..]));
            this.ExternalSkins.Sort(StringComparer.OrdinalIgnoreCase);
            this.ExternalAccessories.Sort(1, this.ExternalAccessories.Count - 1, StringComparer.OrdinalIgnoreCase);
            this.ExternalSaddles.Sort(1, this.ExternalSaddles.Count - 1, StringComparer.OrdinalIgnoreCase);
            break;
        }
    }

    private bool TryOpenCustomizationMenu()
    {
        if (Game1.currentLocation is not Farm farm)
            return false;

        Stable? stable = farm.buildings.OfType<Stable>()
            .FirstOrDefault(candidate => this.IsPlayerInsideStable(candidate));
        if (stable is null)
            return false;

        if (this.IsHorseInsideStable(farm, stable))
            return false;
        if (this.ExternalAssetsPath is null || this.ExternalSkins.Count == 0)
        {
            Game1.addHUDMessage(new HUDMessage("未找到 Elle's Cuter Horses 资源包。", HUDMessage.error_type));
            return true;
        }

        HashSet<Vector2> customizationTiles = this.GetCustomizationTiles(stable).ToHashSet();
        Horse? horse = farm.characters.OfType<Horse>()
            .Where(candidate => customizationTiles.Contains(candidate.Tile))
            .FirstOrDefault();
        if (horse is null)
        {
            Game1.addHUDMessage(new HUDMessage("请把要换装的马停在马厩右侧的 2×2 地砖上。", HUDMessage.error_type));
            return true;
        }

        SavedHorseData? data = this.GetSavedHorseData(horse.HorseId);
        if (data is null)
        {
            data = this.CreateSavedHorseData(horse);
            data.AppearanceOnly = true;
            data.CabinId = "";
            data.OwnerId = horse.ownerId.Value;
            this.SaveData.Horses.Add(data);
            this.WriteSaveData();
        }
        this.SyncSavedAppearanceFromHorse(horse, data);
        if (!this.ExternalSkins.Contains(data.Skin, StringComparer.OrdinalIgnoreCase)) data.Skin = this.ExternalSkins[0];
        if (!this.ExternalAccessories.Contains(data.Accessory, StringComparer.OrdinalIgnoreCase)) data.Accessory = "None";
        if (!this.ExternalSaddles.Contains(data.Saddle, StringComparer.OrdinalIgnoreCase)) data.Saddle = "None";
        this.ApplyExternalAppearance(horse, data);

        Game1.activeClickableMenu = new HorseCustomizationMenu(
            this.GetHorseName(horse, data), data.Skin, data.Accessory, data.Saddle,
            () => horse.Sprite.Texture,
            direction => this.ChangeAppearance(horse, data, this.ExternalSkins, direction, value => data.Skin = value),
            direction => this.ChangeAppearance(horse, data, this.ExternalAccessories, direction, value => data.Accessory = value),
            direction => this.ChangeAppearance(horse, data, this.ExternalSaddles, direction, value => data.Saddle = value));
        return true;
    }

    private bool TryMountManagedHorse(Vector2 cursorTile)
    {
        if (Game1.player.mount is not null)
            return false;

        Horse? horse = Game1.currentLocation.characters
            .OfType<Horse>()
            .Where(candidate => this.ManagedHorseIds.Contains(candidate.HorseId))
            .Where(candidate => Vector2.Distance(candidate.Tile, Game1.player.Tile) <= 3f)
            .OrderBy(candidate => Vector2.DistanceSquared(candidate.Tile, cursorTile))
            .FirstOrDefault(candidate => Vector2.Distance(candidate.Tile, cursorTile) <= 1.5f);

        return horse is not null && horse.checkAction(Game1.player, Game1.currentLocation);
    }

    private bool IsPlayerInsideStable(Stable stable)
    {
        Vector2 playerTile = Game1.player.Tile;
        Point horseTile = stable.GetDefaultHorseTile();
        bool isOnHorseStall = Math.Abs(playerTile.X - horseTile.X) <= 1f
            && Math.Abs(playerTile.Y - horseTile.Y) <= 1f;
        bool isWithinBuilding = playerTile.X >= stable.tileX.Value
            && playerTile.X < stable.tileX.Value + stable.tilesWide.Value
            && playerTile.Y >= stable.tileY.Value
            && playerTile.Y <= stable.tileY.Value + stable.tilesHigh.Value;
        return isOnHorseStall && isWithinBuilding;
    }

    private bool IsHorseInsideStable(Farm farm, Stable stable)
    {
        Point horseTile = stable.GetDefaultHorseTile();
        IEnumerable<Horse> horses = farm.characters.OfType<Horse>()
            .Concat(this.GetOnlineFarmers().Select(farmer => farmer.mount).OfType<Horse>());
        return horses.Any(horse =>
            Math.Abs(horse.Tile.X - horseTile.X) <= 1f
            && Math.Abs(horse.Tile.Y - horseTile.Y) <= 1f);
    }

    private IEnumerable<Vector2> GetCustomizationTiles(Stable stable)
    {
        int startX = stable.tileX.Value + stable.tilesWide.Value;
        int startY = stable.tileY.Value;
        for (int x = 0; x < 2; x++)
        {
            for (int y = 0; y < 2; y++)
                yield return new Vector2(startX + x, startY + y);
        }
    }

    private void SyncSavedAppearanceFromHorse(Horse horse, SavedHorseData data)
    {
        if (!horse.modData.TryGetValue(HorseColorModDataKey, out string? appearance)
            || !appearance.StartsWith("Elle|", StringComparison.Ordinal))
        {
            return;
        }

        string[] parts = appearance.Split('|');
        if (parts.Length != 4)
            return;
        data.Skin = parts[1];
        data.Accessory = parts[2];
        data.Saddle = parts[3];
    }

    private string ChangeAppearance(Horse horse, SavedHorseData data, List<string> choices, int direction, Action<string> setValue)
    {
        string current = choices == this.ExternalSkins ? data.Skin : choices == this.ExternalAccessories ? data.Accessory : data.Saddle;
        int index = choices.FindIndex(value => value.Equals(current, StringComparison.OrdinalIgnoreCase));
        index = (Math.Max(0, index) + direction + choices.Count) % choices.Count;
        setValue(choices[index]);
        this.ApplyExternalAppearance(horse, data);
        this.WriteSaveData();
        return choices[index];
    }

    private void ApplyExternalAppearance(Horse horse, SavedHorseData data)
    {
        if (this.ExternalAssetsPath is null || !this.ExternalSkins.Contains(data.Skin, StringComparer.OrdinalIgnoreCase))
            return;
        string appearance = $"Elle|{data.Skin}|{data.Accessory}|{data.Saddle}";
        if (this.AppliedHorseColors.TryGetValue(horse.HorseId, out string? applied) && applied == appearance)
            return;
        string assetName = HorseTexturePrefix + $"Elle/{horse.HorseId}/{data.Skin}-{data.Accessory}-{data.Saddle}";
        if (!this.ExternalHorseTextures.ContainsKey(assetName))
            this.ExternalHorseTextures[assetName] = this.ComposeExternalHorseTexture(data);
        horse.modData[HorseColorModDataKey] = appearance;
        horse.Sprite.LoadTexture(assetName);
        this.AppliedHorseColors[horse.HorseId] = appearance;
    }

    private Texture2D ComposeExternalHorseTexture(SavedHorseData data)
    {
        Texture2D baseTexture = Texture2D.FromFile(Game1.graphics.GraphicsDevice, Path.Combine(this.ExternalAssetsPath!, "Horse", data.Skin + ".png"));
        Color[] pixels = new Color[baseTexture.Width * baseTexture.Height];
        baseTexture.GetData(pixels);
        if (data.Saddle != "None")
        {
            this.OverlayTexture(pixels, baseTexture.Width, baseTexture.Height, Path.Combine(this.ExternalAssetsPath!, "Saddles", "Pad_" + data.Saddle + ".png"));
            this.OverlayTexture(pixels, baseTexture.Width, baseTexture.Height, Path.Combine(this.ExternalAssetsPath!, "Saddles", "Saddle_" + data.Saddle + ".png"));
        }
        if (data.Accessory != "None")
            this.OverlayTexture(pixels, baseTexture.Width, baseTexture.Height, Path.Combine(this.ExternalAssetsPath!, "Saddles", "Bridle_" + data.Accessory + ".png"));
        Texture2D result = new(Game1.graphics.GraphicsDevice, baseTexture.Width, baseTexture.Height);
        result.SetData(pixels);
        baseTexture.Dispose();
        return result;
    }

    private void OverlayTexture(Color[] destination, int width, int height, string path)
    {
        if (!File.Exists(path)) return;
        using Texture2D overlay = Texture2D.FromFile(Game1.graphics.GraphicsDevice, path);
        if (overlay.Width != width || overlay.Height != height) return;
        Color[] source = new Color[destination.Length];
        overlay.GetData(source);
        for (int i = 0; i < destination.Length; i++)
        {
            float alpha = source[i].A / 255f;
            if (alpha <= 0f) continue;
            destination[i] = Color.Lerp(destination[i], source[i], alpha);
            destination[i].A = (byte)Math.Max(destination[i].A, source[i].A);
        }
    }

    private void RenameHorse(Horse horse, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        this.SetHorseName(horse, name.Trim());
        SavedHorseData? savedHorse = this.GetSavedHorseData(horse.HorseId);
        if (savedHorse is not null)
        {
            savedHorse.Name = horse.Name;
            this.WriteSaveData();
        }

        Game1.exitActiveMenu();
        Game1.addHUDMessage(new HUDMessage($"马匹已命名为：{horse.Name}"));
    }

    private void SetHorseName(Horse horse, string name)
    {
        horse.Name = name;
        horse.displayName = name;

        Farmer? owner = horse.getOwner();
        if (owner is not null)
            owner.horseName.Value = name;
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
                if (!savedHorse.AppearanceOnly)
                    this.ManagedHorseInstances[horseId] = existingHorse;
                this.ApplySavedHorseData(existingHorse, savedHorse);
                continue;
            }

            if (savedHorse.AppearanceOnly)
                continue;

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
        Point stableOrigin = stables[0].GetDefaultHorseTile();

        foreach (Building cabin in cabins)
        {
            string cabinId = cabin.id.Value.ToString();
            SavedHorseData? savedHorse = this.SaveData.Horses.FirstOrDefault(data => data.CabinId == cabinId);
            if (savedHorse is not null)
                continue;

            Vector2 firstDayTile = this.FindSpawnTile(farm, stableOrigin, created);
            Horse horse = this.AddManagedHorse(farm, Guid.NewGuid(), firstDayTile);
            savedHorse = this.CreateSavedHorseData(horse);
            savedHorse.CabinId = cabinId;
            savedHorse.CreatedDay = Game1.Date.TotalDays;
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
        horse.ownerId.Value = savedHorse.OwnerId;

        if (!string.IsNullOrWhiteSpace(savedHorse.Name))
            this.SetHorseName(horse, savedHorse.Name);

        if (this.ExternalSkins.Contains(savedHorse.Skin, StringComparer.OrdinalIgnoreCase))
            this.ApplyExternalAppearance(horse, savedHorse);
        else
            this.ApplyHorseColor(horse, savedHorse.Skin);

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
            .Where(horse => !horse.AppearanceOnly)
            .Where(horse => !string.IsNullOrWhiteSpace(horse.CabinId))
            .Select(horse => horse.CabinId)
            .ToHashSet();

        Queue<Building> availableCabins = new(cabins.Where(cabin => !assignedCabinIds.Contains(cabin.id.Value.ToString())));

        foreach (SavedHorseData savedHorse in this.SaveData.Horses.Where(horse => !horse.AppearanceOnly && string.IsNullOrWhiteSpace(horse.CabinId)))
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
            .Where(horse => !horse.AppearanceOnly)
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

    private void ReturnEligibleCabinHorsesHome()
    {
        Farm? farm = Game1.getFarm();
        if (farm is null)
            return;

        Dictionary<string, Building> cabins = this.GetCabinBuildings(farm)
            .ToDictionary(cabin => cabin.id.Value.ToString());
        int cabinOffset = 0;

        foreach (SavedHorseData savedHorse in this.SaveData.Horses)
        {
            if (!cabins.TryGetValue(savedHorse.CabinId, out Building? cabin)
                || !Guid.TryParse(savedHorse.HorseId, out Guid horseId)
                || !this.ManagedHorseInstances.TryGetValue(horseId, out Horse? horse)
                || savedHorse.CreatedDay >= Game1.Date.TotalDays)
            {
                continue;
            }

            GameLocation targetLocation = farm;
            Vector2 targetTile = this.GetCabinHorseTile(farm, cabin, cabinOffset++);

            foreach (GameLocation location in Game1.locations)
            {
                if (location.characters.Contains(horse))
                    location.characters.Remove(horse);
            }

            horse.currentLocation = targetLocation;
            horse.setTileLocation(targetTile);
            if (!targetLocation.characters.Contains(horse))
                targetLocation.characters.Add(horse);

            savedHorse.LocationName = targetLocation.NameOrUniqueName;
            savedHorse.TileX = (int)targetTile.X;
            savedHorse.TileY = (int)targetTile.Y;
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
        HashSet<Guid> seenHorseIds = new();

        foreach (SavedHorseData savedHorse in this.SaveData.Horses)
        {
            if (!Guid.TryParse(savedHorse.HorseId, out Guid horseId))
                continue;

            if (!seenHorseIds.Add(horseId))
                continue;

            if (!savedHorse.AppearanceOnly)
                this.ManagedHorseIds.Add(horseId);

            if (string.IsNullOrWhiteSpace(savedHorse.Name))
                savedHorse.Name = this.CreateDefaultHorseName(normalized.Count + 1);

            if (!HorseColors.Contains(savedHorse.Skin, StringComparer.OrdinalIgnoreCase))
            {
                if (!this.ExternalSkins.Contains(savedHorse.Skin, StringComparer.OrdinalIgnoreCase))
                    savedHorse.Skin = "Default";
            }
            savedHorse.Accessory ??= "None";
            savedHorse.Saddle ??= "None";
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
        List<SavedHorseData> appearanceOnly = this.SaveData.Horses
            .Where(data => data.AppearanceOnly)
            .Select(data =>
            {
                Horse? horse = this.GetExistingHorses().FirstOrDefault(candidate => candidate.HorseId.ToString() == data.HorseId);
                return horse is null ? data : this.CreateSavedHorseData(horse);
            })
            .ToList();

        this.SaveData.Horses = this.GetExistingHorses()
            .Where(horse => this.ManagedHorseIds.Contains(horse.HorseId))
            .Select(this.CreateSavedHorseData)
            .Concat(appearanceOnly)
            .ToList();
    }

    private SavedHorseData CreateSavedHorseData(Horse horse)
    {
        GameLocation location = horse.currentLocation ?? Game1.getFarm();
        Vector2 tile = horse.Tile;
        SavedHorseData? existingData = this.GetSavedHorseData(horse.HorseId);
        bool isMounted = this.GetOnlineFarmers().Any(farmer => farmer.mount is Horse mount && mount.HorseId == horse.HorseId);

        // A horse's current position while mounted is the rider's position, not a parked
        // location. Keep the previous values so tomorrow it returns to the last dismount.
        string parkedLocationName = isMounted && !string.IsNullOrWhiteSpace(existingData?.LocationName)
            ? existingData.LocationName
            : location.NameOrUniqueName;
        int parkedTileX = isMounted && existingData is not null ? existingData.TileX : (int)tile.X;
        int parkedTileY = isMounted && existingData is not null ? existingData.TileY : (int)tile.Y;

        return new SavedHorseData
        {
            HorseId = horse.HorseId.ToString(),
            Name = this.GetHorseName(horse, existingData),
            Skin = this.GetHorseSkin(horse, existingData),
            Accessory = existingData?.Accessory ?? "None",
            Saddle = existingData?.Saddle ?? "None",
            CabinId = existingData?.CabinId ?? "",
            OwnerId = existingData?.OwnerId ?? horse.ownerId.Value,
            CreatedDay = existingData?.CreatedDay ?? Game1.Date.TotalDays,
            AppearanceOnly = existingData?.AppearanceOnly ?? false,
            LocationName = parkedLocationName,
            TileX = parkedTileX,
            TileY = parkedTileY
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
        return existingData?.Skin ?? "Default";
    }

    private void ApplyHorseColor(Horse horse, string? color)
    {
        string selectedColor = HorseColors.FirstOrDefault(candidate => candidate.Equals(color, StringComparison.OrdinalIgnoreCase)) ?? "Default";
        horse.modData[HorseColorModDataKey] = selectedColor;
        this.LoadHorseColorTexture(horse, selectedColor);
    }

    private void ApplySyncedHorseColors()
    {
        foreach (Horse horse in this.GetExistingHorses())
        {
            if (!horse.modData.TryGetValue(HorseColorModDataKey, out string? color))
                continue;
            if (color.StartsWith("Elle|", StringComparison.Ordinal))
            {
                string[] parts = color.Split('|');
                if (parts.Length == 4)
                    this.ApplyExternalAppearance(horse, new SavedHorseData { HorseId = horse.HorseId.ToString(), Skin = parts[1], Accessory = parts[2], Saddle = parts[3] });
            }
            else
                this.LoadHorseColorTexture(horse, color);
        }
    }

    private void LoadHorseColorTexture(Horse horse, string color)
    {
        string selectedColor = HorseColors.FirstOrDefault(candidate => candidate.Equals(color, StringComparison.OrdinalIgnoreCase)) ?? "Default";
        if (this.AppliedHorseColors.TryGetValue(horse.HorseId, out string? appliedColor) && appliedColor == selectedColor)
            return;

        string assetName = selectedColor == "Default" ? "Animals/horse" : HorseTexturePrefix + selectedColor;
        horse.Sprite.LoadTexture(assetName);
        this.AppliedHorseColors[horse.HorseId] = selectedColor;
    }

    private Texture2D CreateHorseTexture(string colorName)
    {
        Texture2D source = Game1.content.Load<Texture2D>("Animals/horse");
        Color[] pixels = new Color[source.Width * source.Height];
        source.GetData(pixels);

        for (int index = 0; index < pixels.Length; index++)
            pixels[index] = this.RecolorHorsePixel(pixels[index], colorName);

        Texture2D texture = new(Game1.graphics.GraphicsDevice, source.Width, source.Height);
        texture.SetData(pixels);
        return texture;
    }

    private Color RecolorHorsePixel(Color pixel, string colorName)
    {
        if (pixel.A == 0)
            return pixel;

        float r = pixel.R / 255f;
        float g = pixel.G / 255f;
        float b = pixel.B / 255f;

        // Only replace warm brown body pixels. Neutral outlines, eyes, tack, hats,
        // and highlights remain unchanged.
        bool isHorseCoat = r > g * 1.08f && g > b * 1.05f && r > 0.16f;
        if (!isHorseCoat)
            return pixel;

        float luminance = r * 0.45f + g * 0.4f + b * 0.15f;
        Vector3 target = colorName switch
        {
            "Black" => new Vector3(0.18f, 0.17f, 0.19f),
            "White" => new Vector3(0.92f, 0.90f, 0.84f),
            "Gray" => new Vector3(0.52f, 0.53f, 0.55f),
            "Chestnut" => new Vector3(0.62f, 0.24f, 0.13f),
            "Golden" => new Vector3(0.76f, 0.52f, 0.18f),
            _ => new Vector3(r, g, b)
        };
        float shade = Math.Clamp(luminance / 0.48f, 0.35f, 1.45f);
        return new Color(
            Math.Clamp(target.X * shade, 0f, 1f),
            Math.Clamp(target.Y * shade, 0f, 1f),
            Math.Clamp(target.Z * shade, 0f, 1f),
            pixel.A / 255f);
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
