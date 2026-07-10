using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace MultipleHorseMod;

internal sealed class HorseCustomizationMenu : IClickableMenu
{
    private readonly Func<Texture2D> GetPreview;
    private readonly Func<int, string> ChangeSkin;
    private readonly Func<int, string> ChangeAccessory;
    private readonly Func<int, string> ChangeSaddle;
    private readonly ClickableTextureComponent[] Arrows = new ClickableTextureComponent[6];
    private string SkinName;
    private string AccessoryName;
    private string SaddleName;
    private readonly string HorseName;

    public HorseCustomizationMenu(string horseName, string skin, string accessory, string saddle,
        Func<Texture2D> getPreview, Func<int, string> changeSkin,
        Func<int, string> changeAccessory, Func<int, string> changeSaddle)
        : base(Game1.uiViewport.Width / 2 - 210, Game1.uiViewport.Height / 2 - 150, 420, 300, true)
    {
        this.HorseName = horseName;
        this.SkinName = skin;
        this.AccessoryName = accessory;
        this.SaddleName = saddle;
        this.GetPreview = getPreview;
        this.ChangeSkin = changeSkin;
        this.ChangeAccessory = changeAccessory;
        this.ChangeSaddle = changeSaddle;

        for (int row = 0; row < 3; row++)
        {
            int y = this.yPositionOnScreen + 132 + row * 50;
            this.Arrows[row * 2] = new ClickableTextureComponent(new Rectangle(this.xPositionOnScreen + 42, y, 36, 33), Game1.mouseCursors, new Rectangle(352, 495, 12, 11), 3f);
            this.Arrows[row * 2 + 1] = new ClickableTextureComponent(new Rectangle(this.xPositionOnScreen + 342, y, 36, 33), Game1.mouseCursors, new Rectangle(365, 495, 12, 11), 3f);
        }
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);
        for (int index = 0; index < this.Arrows.Length; index++)
        {
            if (!this.Arrows[index].containsPoint(x, y))
                continue;

            int direction = index % 2 == 0 ? -1 : 1;
            int row = index / 2;
            if (row == 0) this.SkinName = this.ChangeSkin(direction);
            if (row == 1) this.AccessoryName = this.ChangeAccessory(direction);
            if (row == 2) this.SaddleName = this.ChangeSaddle(direction);
            Game1.playSound("shwip");
            return;
        }
    }

    public override void draw(SpriteBatch b)
    {
        IClickableMenu.drawTextureBox(b, this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, Color.White);
        b.DrawString(Game1.smallFont, this.HorseName, new Vector2(this.xPositionOnScreen + 24, this.yPositionOnScreen + 20), Game1.textColor);

        Texture2D preview = this.GetPreview();
        b.Draw(preview, new Vector2(this.xPositionOnScreen + this.width / 2, this.yPositionOnScreen + 88), new Rectangle(0, 32, 32, 32), Color.White, 0f, new Vector2(16, 16), 3f, SpriteEffects.None, 1f);

        this.DrawRow(b, 0, "马匹颜色", this.SkinName);
        this.DrawRow(b, 1, "配饰", this.AccessoryName);
        this.DrawRow(b, 2, "马鞍", this.SaddleName);
        foreach (ClickableTextureComponent arrow in this.Arrows)
            arrow.draw(b);
        this.upperRightCloseButton?.draw(b);
        this.drawMouse(b);
    }

    private void DrawRow(SpriteBatch b, int row, string label, string value)
    {
        int y = this.yPositionOnScreen + 132 + row * 50;
        string text = $"{label}：{value}";
        Vector2 size = Game1.smallFont.MeasureString(text);
        float scale = Math.Min(1f, 230f / Math.Max(1f, size.X));
        b.DrawString(Game1.smallFont, text, new Vector2(this.xPositionOnScreen + (this.width - size.X * scale) / 2, y + 5), Game1.textColor, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
    }
}
