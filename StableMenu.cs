using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.Menus;

namespace MultipleHorseMod;

/// <summary>A responsive, game-native presentation of the stable's vanilla and mod-owned horses.</summary>
internal sealed class StableMenu : IClickableMenu
{
    private readonly ModEntry mod;
    private readonly Stable stable;
    private ClickableTextureComponent closeButton = null!;
    private ClickableComponent buyButton = null!;
    private ClickableTextureComponent previous = null!;
    private ClickableTextureComponent next = null!;
    private int selected;
    private float scale;
    private double nameScrollTimer;
    private int nameScrollIndex;

    internal StableMenu(ModEntry mod, Stable stable) : base(0, 0, 640, 480, true)
    {
        this.mod = mod;
        this.stable = stable;
        this.Layout();
    }

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds) { base.gameWindowSizeChanged(oldBounds, newBounds); this.Layout(); }
    public override void update(GameTime time)
    {
        base.update(time);
        this.nameScrollTimer += time.ElapsedGameTime.TotalMilliseconds;
        if (this.nameScrollTimer >= 220)
        {
            this.nameScrollTimer = 0;
            this.nameScrollIndex++;
        }
    }
    public override bool readyToClose() => true;
    public override void receiveKeyPress(Keys key) { if (key == Keys.Escape) Game1.exitActiveMenu(); }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (this.closeButton.containsPoint(x, y)) { Game1.exitActiveMenu(); return; }
        IReadOnlyList<Horse> horses = this.mod.GetHorses(this.stable);
        if (this.HasPaging(horses) && this.previous.containsPoint(x, y)) { this.selected = horses.Count > 0 ? (this.selected + horses.Count - 1) % horses.Count : 0; Game1.playSound("shwip"); return; }
        if (this.HasPaging(horses) && this.next.containsPoint(x, y)) { this.selected = horses.Count > 0 ? (this.selected + 1) % horses.Count : 0; Game1.playSound("shwip"); return; }
        if (this.buyButton.containsPoint(x, y))
        {
            if (this.mod.ExtraHorseCount >= this.mod.ExtraHorseLimit) Game1.showRedMessage(this.mod.T("purchase.limit"));
            else this.mod.OpenNamingMenu(this.stable);
        }
    }

    public override void draw(SpriteBatch b)
    {
        drawTextureBox(b, this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, Color.White);
        SpriteText.drawStringHorizontallyCenteredAt(b, this.mod.T("menu.title"), this.xPositionOnScreen + this.width / 2, this.yPositionOnScreen + (int)(60 * this.scale));
        Rectangle card = new(this.xPositionOnScreen + this.width / 2 - (int)(130 * this.scale), this.yPositionOnScreen + (int)(187 * this.scale), (int)(260 * this.scale), (int)(270 * this.scale));
        drawTextureBox(b, card.X, card.Y, card.Width, card.Height, Color.White);
        IReadOnlyList<Horse> horses = this.mod.GetHorses(this.stable);
        if (horses.Count == 0)
        {
            // Content Patcher replaces this game asset too, so Elle's Cuter Horses (and similar packs)
            // supplies the player's selected horse appearance even before an extra horse exists.
            Texture2D horseTexture = Game1.content.Load<Texture2D>("Animals\\Horse");
            b.Draw(horseTexture, GetHorseImageBounds(card), new Rectangle(0, 0, 32, 32), Color.White);
            SpriteText.drawStringHorizontallyCenteredAt(b, this.mod.T("horse.default-label"), card.Center.X, card.Bottom + (int)(24 * this.scale));
        }
        else
        {
            this.selected = Math.Clamp(this.selected, 0, horses.Count - 1);
            Horse horse = horses[this.selected];
            b.Draw(horse.Sprite.Texture, GetHorseImageBounds(card), GetSideSourceRect(horse), Color.White);
            string name = this.GetVisibleName(this.mod.GetHorseMenuName(horse), card.Width);
            SpriteText.drawStringHorizontallyCenteredAt(b, name, card.Center.X, card.Bottom + (int)(24 * this.scale));
        }
        if (this.HasPaging(horses)) { this.previous.draw(b); this.next.draw(b); }
        drawTextureBox(b, this.buyButton.bounds.X, this.buyButton.bounds.Y, this.buyButton.bounds.Width, this.buyButton.bounds.Height, Color.White);
        SpriteText.drawStringHorizontallyCenteredAt(b, this.mod.T("menu.buy"), this.buyButton.bounds.Center.X, this.buyButton.bounds.Center.Y - 16);
        this.closeButton.draw(b); this.drawMouse(b);
    }

    private void Layout()
    {
        // Restore the prior portrait height while retaining the narrower width.
        float preferredScale = Math.Clamp(Math.Min(Game1.uiViewport.Width / 1280f, Game1.uiViewport.Height / 1440f), .65f, 1.5f);
        // The panel remains entirely inside narrow or short UI viewports.
        this.scale = Math.Min(preferredScale, Math.Min(Game1.uiViewport.Width / 640f, Game1.uiViewport.Height / 960f));
        this.width = (int)(640 * this.scale); this.height = (int)(960 * this.scale);
        this.xPositionOnScreen = (Game1.uiViewport.Width - this.width) / 2; this.yPositionOnScreen = (Game1.uiViewport.Height - this.height) / 2;
        // Keep the box visibly wider than the unscaled SpriteText label at every supported UI scale.
        int buttonWidth = (int)(500 * this.scale);
        int buttonHeight = (int)(116 * this.scale);
        int buttonCenterY = this.yPositionOnScreen + this.height * 3 / 4;
        this.buyButton = new ClickableComponent(new Rectangle(this.xPositionOnScreen + this.width / 2 - buttonWidth / 2, buttonCenterY - buttonHeight / 2, buttonWidth, buttonHeight), "buy");

        // The arrow's rendered height is one third of the horse image's inner frame height (234 pixels).
        int arrowHeight = (int)(78 * this.scale);
        int arrowWidth = (int)(85 * this.scale);
        float arrowScale = arrowHeight / 11f;
        int arrowY = this.yPositionOnScreen + (int)(283 * this.scale);
        this.previous = new ClickableTextureComponent(new Rectangle(this.xPositionOnScreen + this.width / 2 - (int)(270 * this.scale), arrowY, arrowWidth, arrowHeight), Game1.mouseCursors, new Rectangle(352, 495, 12, 11), arrowScale);
        this.next = new ClickableTextureComponent(new Rectangle(this.xPositionOnScreen + this.width / 2 + (int)(185 * this.scale), arrowY, arrowWidth, arrowHeight), Game1.mouseCursors, new Rectangle(365, 495, 12, 11), arrowScale);
        this.closeButton = new ClickableTextureComponent(new Rectangle(this.xPositionOnScreen + this.width - (int)(92 * this.scale), this.yPositionOnScreen + (int)(20 * this.scale), (int)(72 * this.scale), (int)(72 * this.scale)), Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 6f * this.scale);
    }

    private static Rectangle GetHorseImageBounds(Rectangle card)
    {
        const int inset = 18;
        return new Rectangle(card.X + inset, card.Y + inset, card.Width - inset * 2, card.Height - inset * 2);
    }

    private bool HasPaging(IReadOnlyList<Horse> horses) => horses.Count > 1 || this.mod.ExtraHorseCount > 0;

    private static Rectangle GetSideSourceRect(Horse horse)
    {
        // The first row is front-facing; the second row is the standard side-facing horse frame.
        // Keep a one-row sheet compatible by falling back to its only available frame.
        Rectangle current = horse.Sprite.SourceRect;
        int y = horse.Sprite.Texture.Height >= current.Height * 2 ? current.Height : 0;
        return new Rectangle(0, y, current.Width, current.Height);
    }

    private string GetVisibleName(string name, int availableWidth)
    {
        // SpriteText has no clipping API. A 16px character budget keeps every marquee frame inside
        // the preview box, while cycling the visible substring for names that exceed that budget.
        int maximumCharacters = Math.Max(1, (availableWidth - 12) / 16);
        if (name.Length <= maximumCharacters)
            return name;
        string loop = name + "   ";
        return string.Concat(Enumerable.Range(0, maximumCharacters).Select(index => loop[(this.nameScrollIndex + index) % loop.Length]));
    }
}
