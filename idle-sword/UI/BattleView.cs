using Godot;
using IdleSword.Features;

namespace IdleSword.UI;

/// <summary>战斗表现适配器。像素占位精灵统一 64×64，底部中心锚点，素材替换不改变战斗判定。</summary>
public partial class BattleView : Control
{
    public GameSession Session { get; set; } = null!;
    private readonly Dictionary<string, Texture2D> _textures = [];
    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore; ClipContents = true;
        TextureFilter = TextureFilterEnum.Nearest;
        var manifest = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(Godot.FileAccess.GetFileAsString("res://Assets/visuals.json"))
            ?? throw new InvalidDataException("美术资源映射为空");
        foreach (var (id, path) in manifest)
            _textures[id] = GD.Load<Texture2D>(path) ?? throw new InvalidDataException("缺少美术资源: " + path);
    }
    private float X(double world) => 330 + (float)(world - Session.Battle.PlayerX);
    public override void _Process(double delta) => QueueRedraw();
    public override void _Draw()
    {
        if (Session is null) return;
        DrawRect(new(0, 0, 1920, 440), new Color("#253f50"));
        DrawCircle(new(1560, 86), 44, new Color("#d9cfad"));
        // 远景使用分层整数坐标多边形，摄像机滚动不影响世界坐标和攻击距离。
        for (int layer = 0; layer < 3; layer++)
        {
            var color = new Color[] { new("#2f5060"), new("#355866"), new("#274550") }[layer];
            float scroll = (float)(Session.Battle.PlayerX * (.025 + layer * .025) % 700);
            for (int i = -1; i < 5; i++)
            {
                float x = i * 700 - scroll;
                DrawColoredPolygon([new(x, 350), new(x + 70, 245 - layer * 8), new(x + 150, 245 - layer * 8), new(x + 300, 80 + layer * 45), new(x + 340, 80 + layer * 45), new(x + 560, 300), new(x + 720, 350)], color);
            }
        }
        for (int i = -1; i < 14; i++)
        {
            float x = i * 180 - (float)(Session.Battle.PlayerX * .32 % 180);
            DrawRect(new(x + 22, 202, 8, 167), new Color("#23444a"));
            for (int j = 0; j < 4; j++)
                DrawColoredPolygon([new(x - 35, 240 + j * 23), new(x + 26, 177 + j * 23), new(x + 92, 240 + j * 23)], new Color("#294e50"));
        }
        DrawRect(new(0, 365, 1920, 75), new Color("#182e37"));
        DrawRect(new(0, 364, 1920, 5), new Color("#748772"));
        for (int i = -1; i < 45; i++)
        {
            float x = i * 52 - (float)(Session.Battle.PlayerX % 52);
            DrawRect(new(x, 379 + i % 3 * 9, 23, 4), new Color("#304647"));
        }
        var font = GetThemeDefaultFont();
        foreach (var (cell, spawn) in Session.Battle.Spawns)
        {
            float x = X(cell * Session.Config.Setting("cell_width") + Session.Config.Rows("spawn_point")[0].Number("offset"));
            if (x < -100 || x > 2000) continue;
            DrawRect(new(x, 292, 5, 73), new Color("#b29668"));
            DrawRect(new(x + 5, 297, 35, 27), spawn.Passed ? new Color("#456963") : new Color("#9d7856"));
            DrawString(font, new(x - 20, 394), spawn.Passed ? "已越过" : $"刷怪点 {cell + 1}", HorizontalAlignment.Left, -1, 18, UiKit.Muted);
        }
        float bob = Session.Moving ? (float)Math.Sin(Session.Elapsed * 14) * 3 : 0;
        DrawEllipseShadow(330, 366, 65);
        if (Session.Battle.RespawnTimer <= 0) Sprite("player", 330, 368 + bob, 136);
        else DrawString(font, new(220, 290), "调息重生…", HorizontalAlignment.Left, -1, 26, UiKit.Gold);
        int pi = 0;
        foreach (var pet in Session.State.EquippedPets)
        {
            float px = 220 + pi * 65, py = 226 + (float)Math.Sin(Session.Elapsed * 2 + pi) * 10;
            Sprite(Session.Config.Row("Pet", pet).Text("visual"), px, py, 70); pi++;
        }
        // 仅限制可见精灵数量，不删除怪物，不改变任何伤害和刷怪逻辑。
        var visible = Session.Battle.Enemies.Where(e => e.Hp > 0 && X(e.X) > -120 && X(e.X) < 2070).OrderBy(e => e.Kind == "boss" || e.Kind == "rift" ? 0 : 1).Take((int)Session.Config.Setting("enemy_visual_limit"));
        foreach (var enemy in visible)
        {
            float x = X(enemy.X), size = enemy.Kind switch { "boss" => 194, "rift" => 174, "elite" => 125, _ => 96 };
            float y = 368 - (enemy.Kind == "rift" ? (float)Math.Sin(Session.Elapsed * 2) * 7 : 0);
            DrawEllipseShadow(x, 368, size * .48f);
            Sprite(Session.Config.Monsters[enemy.MonsterId].Visual, x, y, size, enemy.Kind == "rift" && !Session.RiftUnlocked ? new Color(.6f, .6f, .7f) : Colors.White);
            DrawRect(new(x - 42, y - size - 6, 84, 5), UiKit.Ink);
            DrawRect(new(x - 42, y - size - 6, 84 * (float)(enemy.Hp / enemy.MaxHp), 5), enemy.Kind == "boss" ? new Color("#dc8b80") : UiKit.Jade);
            if (enemy.Kind is "boss" or "rift") DrawString(font, new(x - 65, y - size - 16), enemy.Kind == "rift" && !Session.RiftUnlocked ? "裂隙 · 封印中" : Session.Config.Monsters[enemy.MonsterId].Name, HorizontalAlignment.Left, -1, 20, UiKit.Gold);
        }
        foreach (var effect in Session.Effects)
        {
            float x = X(effect.X); var color = effect.Hostile ? new Color("#e99885") : new Color("#b9e1ca");
            switch (effect.Kind)
            {
                case "projectile": DrawRect(new(x - 26, effect.Hostile ? 289 : 282, 46, 4), color); DrawRect(new(x - 3, 275, 4, 17), UiKit.Gold); break;
                case "ground": DrawRect(new(x - 220, 352, 440, 8), new Color(.5f, .8f, .7f, .5f)); for (int j = 0; j < 7; j++) DrawRect(new(x - 200 + j * 65, 322, 3, 31), color); break;
                case "target": DrawArc(new(effect.Hostile ? 330 : x, 340), 38, 0, Mathf.Tau, 24, color, 3); DrawLine(new(effect.Hostile ? 330 : x, 170), new(effect.Hostile ? 330 : x, 306), color, 2); break;
                case "summon": Sprite("pet_snow", x, 247, 82); break;
            }
        }
        DrawString(font, new(32, 42), "青山不语   ·   剑问长生", HorizontalAlignment.Left, -1, 23, new Color("#c0d2c9"));
        DrawString(font, new(32, 73), Session.Moving ? "前行中 · 尚未越过的刷怪点仍会补怪" : "交战中 · 清空攻击范围后继续前行", HorizontalAlignment.Left, -1, 18, UiKit.Muted);
    }
    private void Sprite(string id, float x, float bottom, float size, Color? color = null)
    {
        if (_textures.TryGetValue(id, out var texture)) DrawTextureRect(texture, new(x - size / 2, bottom - size, size, size), false, color ?? Colors.White);
    }
    private void DrawEllipseShadow(float x, float y, float radius) => DrawRect(new(x - radius, y - 3, radius * 2, 7), new Color(0, 0, 0, .22f));
}
