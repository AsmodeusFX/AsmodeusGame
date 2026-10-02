using Godot;
using IdleSword.Core;
using IdleSword.Features;

namespace IdleSword.UI;

/// <summary>
/// 战斗表现适配器。像素占位精灵统一 64×64，底部中心锚点，素材替换不改变战斗判定。
/// 表现层只读不写：命中反馈由血条差分推断，状态图标直接读取 EnemyState 的公开字段，
/// 不在 Core 里添加任何仅供显示的状态。
/// </summary>
public partial class BattleView : Control
{
    public GameSession Session { get; set; } = null!;
    /// <summary>
    /// 资源缺失记录。Godot 会吞掉 _Ready 中抛出的异常（转换为控制台错误而不中断进程），
    /// 因此缺图不能靠抛异常暴露——那会让"资源没导入"表现为"游戏照常跑但画面空白"。
    /// 这里显式记录，交给启动流程提示，并由 QA 自检断言。
    /// </summary>
    public string? LoadError { get; private set; }
    /// <summary>命中信号，参数为是否重击。音频由 Main 订阅后播放，表现层只负责把差分结果报出去。</summary>
    public event Action<bool>? HitLanded;
    /// <summary>敌人死亡信号。死亡的敌人已在同一次 Step 里被移除，血量差分看不到 Hp&lt;=0，只能靠"从列表消失"判定。</summary>
    public event Action? EnemyDefeated;
    // 元素配色：风青碧、雷金电、霜冰蓝、炎赤橙、太虚紫、血赤。与 UiKit 的靛青底/暖金强调共存。
    private static readonly Color Wind = new("#9fdcc4"), Thunder = new("#f5ea9a"), Frost = new("#9ed4ee"),
        Flame = new("#ff8a3d"), Void = new("#b49ae0"), Blood = new("#d96a72"), Hostile = new("#e99885");
    private readonly Dictionary<string, Texture2D> _textures = [];
    // _lastHp 用于逐帧差分出"命中"，_popups 为飘字，_flash 记录受击闪动截止时刻。
    private readonly Dictionary<long, double> _lastHp = [], _flash = [];
    private readonly List<Popup> _popups = [];
    // 上一帧看到的 BattleState 对象。换场判定必须用对象身份，见 TrackHits 的说明。
    private BattleState? _lastBattle;
    private double _clock;
    // 上一次观测到的模拟时钟，用来算"这一帧到底模拟了多少秒"，见 TrackHits 与 AddPopup。
    private double _lastElapsed;
    // 召唤/跟随单位占用的位次。Core 每步把召唤物的 X 统一钉回玩家身后（那只是逻辑锚点），
    // 具体站在哪、怎么错开由表现层分配，见 AssignCompanionSlots。
    private readonly Dictionary<CombatEffect, int> _companionSlots = [];

    private sealed class Popup
    {
        public long Id; public bool Burn; public float X; public double Total, Life, MaxLife;
        public Color Color = Colors.White; public int Size = 22;
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore; ClipContents = true;
        TextureFilter = TextureFilterEnum.Nearest;
        var manifest = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(Godot.FileAccess.GetFileAsString("res://Assets/visuals.json"))
            ?? throw new InvalidDataException("美术资源映射为空");
        var missing = new List<string>();
        foreach (var (id, path) in manifest)
        {
            var texture = GD.Load<Texture2D>(path);
            if (texture is null) missing.Add(path); else _textures[id] = texture;
        }
        if (missing.Count > 0) LoadError = "缺少美术资源（未导入或路径错误）: " + string.Join(", ", missing);
    }
    private float X(double world) => 330 + (float)(world - Session.Battle.PlayerX);
    public override void _Process(double delta)
    {
        TrackHits(delta);
        AssignCompanionSlots();
        QueueRedraw();
    }
    /// <summary>清空飘字与受击记录。切换预览目标时调用，避免上一场的残留浮在画面上。</summary>
    public void ResetTransient()
    {
        _lastHp.Clear(); _flash.Clear(); _popups.Clear(); _companionSlots.Clear();
    }

    /// <summary>
    /// 召唤/跟随单位当前占用的位次。站位完全由表现层决定（Core 只把它们钉在玩家身后的
    /// 逻辑锚点上，站位不影响伤害），公开此只读视图仅供自检核对"多个单位确实错开"。
    /// </summary>
    public IReadOnlyDictionary<CombatEffect, int> CompanionSlots => _companionSlots;

    /// <summary>
    /// 累计判定为灼烧跳伤的次数。公开仅供自检核对判据——跳伤被误判成命中时，
    /// 声音本身可能被音效抑制机制吃掉而看不出来，这个计数不会。
    /// </summary>
    public int BurnTicks { get; private set; }

    /// <summary>
    /// 给每个召唤/跟随单位分配一个稳定位次。新单位补当前最小的空位，已有单位不会因为
    /// 别人到期而整体挪位——那会让画面上的单位无端平移。位次最大不超过并发召唤数。
    /// </summary>
    private void AssignCompanionSlots()
    {
        var live = Session.Effects.Where(e => e.Kind == "summon").ToHashSet();
        foreach (var gone in _companionSlots.Keys.Where(e => !live.Contains(e)).ToArray()) _companionSlots.Remove(gone);
        var used = _companionSlots.Values.ToHashSet();
        foreach (var effect in live)
        {
            if (_companionSlots.ContainsKey(effect)) continue;
            int slot = 0;
            while (used.Contains(slot)) slot++;
            used.Add(slot);
            _companionSlots[effect] = slot;
        }
    }

    /// <summary>
    /// 召唤/跟随单位的站位（屏幕坐标，玩家恒在 x=330）。
    /// 左右交替、逐层向外并抬高，避免所有单位叠在同一点，也避免全堆在同一侧——
    /// 单侧一字排开在单位变多时会甩出画面。slot 越大越靠外，之后的跟随单位沿用这套位次即可。
    /// </summary>
    public static (float X, float Y) FollowerSlot(int slot)
    {
        int ring = slot / 2;
        // 左侧让得近、右侧让得开：玩家朝右，身后更空，身前要避开挥剑与弹丸的主轴。
        float dx = (slot % 2 == 0 ? -96f : 116f) + (slot % 2 == 0 ? -1f : 1f) * ring * 92;
        return (330 + dx, 250 - ring * 46);
    }

    /// <summary>
    /// 头顶剑阵位次（悬浮形态用）：以玩家(x=330)为基准水平错开、中央略高，多支剑不重叠。
    /// 与 FollowerSlot 同样是纯函数：只吃序号与总数，不含随机、不读会话状态，因此可被自检断言。
    /// 站位只是绘制偏移，Core 里每支剑的 X 仍是各自锁定的逻辑锚点，不参与命中判定。
    /// </summary>
    public static (float X, float Y) HoverSlot(int index, int count)
    {
        float spacing = Math.Min(38f, 300f / Math.Max(1, count - 1));
        float dx = Math.Clamp((index - (count - 1) / 2f) * spacing, -300f, 300f);
        // 越靠中央抬得越高：读起来像一列斜张的剑阵，重叠的两支也自然分出层次。
        float lift = 1 - Math.Abs(dx) / 300f;
        return (330 + dx, Math.Clamp(198 - lift * 14 - index % 2 * 6, 10f, 230f));
    }

    /// <summary>天降剑的起飞高度：按序号分层，避免同一落点的多支剑完全重叠。落点 X 由 Core 冻结，表现层不改。</summary>
    public static float SkyDropTop(int index) => 10 + index % 5 * 20;

    /// <summary>
    /// 平射剑的排列位次（御剑术）：以玩家为原点，身前身后交替、同侧逐层上抬，横向间距由 spread 配置。
    /// 纯函数，只吃序号／总数／间距，可被自检断言；y 落在肩部一带（268 上下），不遮挡下方界面。
    /// </summary>
    public static (float X, float Y) VolleySlot(int index, int count, float spread)
    {
        float toward = index % 2 == 0 ? 1f : -1f;          // 偶数位在身前、奇数位在身后
        float rank = index / 2;
        // 身后偏低、身前偏高，同侧再逐层上抬：只按 rank 错高会挤在一条线上，看不出是"排开的一列剑"。
        float y = 258 - rank * 26 + (index % 2 == 0 ? 0f : 18f);
        return (330 + toward * (26 + rank * Math.Max(12f, spread)), y);
    }

    /// <summary>
    /// 剑形多边形：按给定角度手算旋转。本文件刻意不用 DrawSetTransform——
    /// 变换状态会残留给后续的 DrawString / 精灵绘制，且手算版是纯函数、可被自检断言。
    /// 角度 0 为剑尖朝右，+90° 为剑尖朝下。剑格另用 DrawBlade 画一条横档。
    /// </summary>
    public static Vector2[] BladePolygon(float cx, float cy, double angle, float length, float width)
    {
        float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
        Vector2 At(float along, float across) => new(cx + along * cos - across * sin, cy + along * sin + across * cos);
        float tip = length * .5f, tail = -length * .5f, w = width * .5f;
        return [At(tip, 0), At(tip - length * .3f, -w), At(tail, -w * .5f), At(tail, w * .5f), At(tip - length * .3f, w)];
    }

    /// <summary>取出某效果的形态参数（数量／停留／飞行时长／排列间距／出生高度随机）。查不到配置时按单发直线处理。</summary>
    private (int Count, double Hold, double Duration, double Spread, double SpawnJitter) ShapeOf(CombatEffect effect)
        => Session.Config.Skills.TryGetValue(effect.Skill, out var s)
            ? (s.ProjectileCount, s.HoverTime, s.Duration, s.Spread, s.SpawnJitter) : (1, 0d, effect.MaxLife, 0d, 0d);

    /// <summary>
    /// 飞行进度按"逻辑 X 走了多远"算，而不是按寿命：逻辑飞行远快于 duration，
    /// 按寿命算会让剑在命中前看起来还没飞到位（命中当帧 effect 就被移除了）。
    /// </summary>
    private float FlightProgress(CombatEffect effect, float x, float from)
    {
        var target = Session.Battle.Enemies.FirstOrDefault(e => e.Id == effect.Target);
        float to = target is null ? x + 200 : X(target.X);
        return Math.Clamp((x - from) / Math.Max(1, to - from), 0, 1);
    }

    /// <summary>
    /// 判断一次掉血是否属于灼烧跳伤。阈值按"这段时间里模拟了多少秒"算，不能用真实帧间隔——
    /// 跳伤是按固定步长成块结算的（每步 DotDps*fixed_step），而真实帧间隔通常小于固定步长，
    /// 用帧间隔做阈值会低于单步跳伤，判据永不成立，于是每一步都播一次命中音。
    /// </summary>
    public static bool IsBurnTick(EnemyState enemy, double damage, double simSeconds)
        => enemy.DotUntil > 0 && damage <= enemy.DotDps * simSeconds * 1.5;
    /// <summary>模拟层不发命中事件，表现层用血量差分还原命中、飘字与受击闪动。</summary>
    private void TrackHits(double delta)
    {
        _clock += delta;
        // 换场守卫用 BattleState 对象身份，不能用关卡 id：死亡回到起点和整关循环都会回到同一个关卡 id，
        // id 守卫挡不住，会把 _lastHp 里所有旧 id 当成击杀、刷一波死亡音。
        if (!ReferenceEquals(_lastBattle, Session.Battle))
        {
            _lastBattle = Session.Battle;
            ResetTransient();
            _lastElapsed = Session.Elapsed;
        }
        // 本帧实际模拟了多少秒。一帧可能跑 0 个也可能跑很多个固定步，必须按模拟时钟算，
        // 用真实帧间隔会把灼烧判据算错，见 IsBurnTick。
        double simSeconds = Math.Max(0, Session.Elapsed - _lastElapsed);
        _lastElapsed = Session.Elapsed;
        bool hitPlayed = false;
        foreach (var enemy in Session.Battle.Enemies)
        {
            if (!_lastHp.TryGetValue(enemy.Id, out double previous)) { _lastHp[enemy.Id] = enemy.Hp; continue; }
            _lastHp[enemy.Id] = enemy.Hp;
            double damage = previous - enemy.Hp;
            if (damage <= 0) continue;
            _flash[enemy.Id] = _clock + .12;
            bool burn = AddPopup(enemy, damage, simSeconds);
            if (burn) BurnTicks++;
            // 灼烧跳伤不出声：每步只掉一点点，逐帧播会变成机关枪。
            if (burn) continue;
            bool heavy = damage >= enemy.MaxHp * .12;
            // 每帧最多一次普通命中音，重击放行：范围技能同时打多个目标时也不会糊成一片。
            if (!heavy && hitPlayed) continue;
            hitPlayed = true;
            HitLanded?.Invoke(heavy);
        }
        bool announced = false;
        foreach (long id in _lastHp.Keys.Where(id => !Session.Battle.Enemies.Any(e => e.Id == id)).ToArray())
        {
            _lastHp.Remove(id); _flash.Remove(id);
            // 同一帧死多个只报一次，避免叠成一片。
            if (announced) continue;
            announced = true;
            EnemyDefeated?.Invoke();
        }
        foreach (var popup in _popups) popup.Life -= delta;
        _popups.RemoveAll(p => p.Life <= 0);
    }
    /// <summary>返回是否为灼烧跳伤，供调用方决定要不要播命中音。</summary>
    private bool AddPopup(EnemyState enemy, double damage, double simSeconds)
    {
        // 灼烧是每帧连续小额伤害，逐帧出字会刷屏，故同一目标的灼烧伤害合并成一个跳字。
        bool burn = IsBurnTick(enemy, damage, simSeconds);
        if (burn)
        {
            var merged = _popups.FirstOrDefault(p => p.Id == enemy.Id && p.Burn);
            if (merged is not null) { merged.Total += damage; merged.Life = merged.MaxLife; return burn; }
        }
        double ratio = damage / Math.Max(1, enemy.MaxHp);
        _popups.Add(new Popup
        {
            Id = enemy.Id, Burn = burn, X = (float)enemy.X, Total = damage,
            Life = burn ? .7 : .85, MaxLife = burn ? .7 : .85,
            Color = burn ? Flame : ratio >= .12 ? UiKit.Gold : UiKit.Text,
            Size = burn ? 18 : ratio >= .12 ? 30 : 22,
        });
        if (_popups.Count > 60) _popups.RemoveAt(0);
        return burn;
    }
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
        // 地面持续效果（剑阵/领域）画在角色与敌人之下，避免盖住血条。
        foreach (var effect in Session.Effects.Where(e => e.Kind == "ground" && !e.Hostile)) DrawEffect(effect, font);
        float bob = Session.Moving ? (float)Math.Sin(Session.Elapsed * 14) * 3 : 0;
        DrawEllipseShadow(330, 366, 65);
        if (Session.Battle.RespawnTimer <= 0) Sprite("player", 330, 368 + bob, 136);
        else DrawString(font, new(220, 290), "调息重生…", HorizontalAlignment.Left, -1, 26, UiKit.Gold);
        DrawPlayerAuras();
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
            // 受击瞬间放大 12% 并叠加高光，代替无法在 LDR 下实现的白色闪光。
            bool hit = _flash.TryGetValue(enemy.Id, out double until) && until > _clock;
            if (hit) size *= 1.12f;
            Sprite(Session.Config.Monsters[enemy.MonsterId].Visual, x, y, size, enemy.Kind == "rift" && !Session.RiftUnlocked ? new Color(.6f, .6f, .7f) : Colors.White);
            if (hit) DrawCircle(new(x, y - size * .5f), size * .34f, new Color(1, 1, 1, .16f));
            DrawEnemyStatus(enemy, x, y, size);
            DrawRect(new(x - 42, y - size - 6, 84, 5), UiKit.Ink);
            DrawRect(new(x - 42, y - size - 6, 84 * (float)(enemy.Hp / enemy.MaxHp), 5), enemy.Kind == "boss" ? Hostile : UiKit.Jade);
            if (enemy.Kind is "boss" or "rift") DrawString(font, new(x - 65, y - size - 16), enemy.Kind == "rift" && !Session.RiftUnlocked ? "裂隙 · 封印中" : Session.Config.Monsters[enemy.MonsterId].Name, HorizontalAlignment.Left, -1, 20, UiKit.Gold);
        }
        // 召唤物不走通用标签：它们的站位由位次分配，标签要跟着单位走，见 DrawSummons。
        foreach (var effect in Session.Effects.Where(e => e.Kind is not ("ground" or "summon"))) DrawEffect(effect, font);
        DrawSummons(font);
        DrawPopups(font);
        DrawString(font, new(32, 42), "青山不语   ·   剑问长生", HorizontalAlignment.Left, -1, 23, new Color("#c0d2c9"));
        DrawString(font, new(32, 73), Session.Moving ? "前行中 · 尚未越过的刷怪点仍会补怪" : "交战中 · 清空攻击范围后继续前行", HorizontalAlignment.Left, -1, 18, UiKit.Muted);
    }

    /// <summary>按剑诀 ID 分流：每个技能用不同的形状、配色与节奏，使 15 个剑诀在画面上可辨认。</summary>
    private void DrawEffect(CombatEffect effect, Font font)
    {
        float x = X(effect.X);
        if (x < -1200 || x > 3200) return;
        switch (effect.Kind)
        {
            case "projectile": DrawProjectile(effect, x); break;
            case "ground": DrawGround(effect, x); break;
            case "target": DrawTarget(effect, x); break;
        }
        // 技能名标签只由本次施法的第一支负责：多发剑诀每支都画会叠成一串糊字。
        if (effect.Index == 0 && Session.Config.Skills.TryGetValue(effect.Skill, out var skill) && effect.MaxLife > 0 && effect.Life > effect.MaxLife - .7)
        {
            var label = UiKit.Text; label.A = (float)Math.Clamp((effect.MaxLife - effect.Life) / .7, 0, 1) * .92f;
            DrawString(font, new(x - 130, 222), skill.Name, HorizontalAlignment.Center, 260, 18, label);
        }
    }
    private void DrawProjectile(CombatEffect effect, float x)
    {
        if (effect.Hostile) { DrawRect(new(x - 26, 289, 46, 4), Hostile); DrawRect(new(x - 3, 275, 4, 17), Hostile); return; }
        // 先按飞行形态分流，再回落到按剑诀 ID 的绘制：同类形态的新剑诀不需要再加分支。
        switch (effect.Trajectory)
        {
            case "hover_homing": DrawHoverBlade(effect, x); return;
            case "sky_drop": DrawSkyDropBlade(effect, x); return;
            case "arc_homing": DrawArcBlade(effect, x); return;
            case "line_shot": DrawPierceBlade(effect, x, false); return;
            case "line_pierce": DrawPierceBlade(effect, x, true); return;
        }
        switch (effect.Skill)
        {
            default: // 剑灵弹丸等未指定形态的直线弹道
                DrawRect(new(x - 26, 289, 46, 4), UiKit.Jade); DrawRect(new(x - 3, 275, 4, 17), UiKit.Gold); break;
        }
    }

    /// <summary>画一支剑：剑身多边形 + 剑格横档。角度 0 为剑尖朝右、+90° 为朝下。</summary>
    private void DrawBlade(float x, float y, double angle, float length, Color blade, Color guard)
    {
        DrawColoredPolygon(BladePolygon(x, y, angle, length, Math.Max(3f, length * .17f)), blade);
        float gx = (float)Math.Cos(angle), gy = (float)Math.Sin(angle);
        float at = -length * .2f, arm = Math.Max(4f, length * .17f);
        DrawLine(new(x + gx * at - gy * arm, y + gy * at + gx * arm), new(x + gx * at + gy * arm, y + gy * at - gx * arm), guard, Math.Max(2f, length * .08f));
    }

    /// <summary>
    /// 御剑术：肩侧浮现后平射。`line_shot` 命中即散，只拉一小段拖尾；`line_pierce` 是恒穿透形态，
    /// 保留贯穿全屏的剑光读法。两者共用肩侧排列与错时（VolleySlot + volley_interval）。
    /// </summary>
    private void DrawPierceBlade(CombatEffect effect, float x, bool pierce)
    {
        var shape = ShapeOf(effect);
        var (ox, oy) = VolleySlot(effect.Index, shape.Count, (float)shape.Spread);
        if (effect.Timer > 0)   // 浮现期：在各自肩位上逐渐显形，尚未射出
        {
            float fade = (float)Math.Clamp(1 - effect.Timer / Math.Max(.01, shape.Hold), .25f, 1f);
            var glow = Wind; glow.A = fade;
            DrawBlade(ox, oy + (float)Math.Sin(_clock * 6 + effect.Index) * 2, 0, 62, glow, new Color(UiKit.Gold, fade));
            return;
        }
        // 飞行期：y 固定在肩部高度。
        float y = oy + (float)Math.Sin(_clock * 6 + effect.Index) * 2;
        if (pierce) DrawRect(new(x - 900, y - 1, 900, 3), new Color(Wind, .10f));   // 贯穿剑光：恒穿透才有
        DrawLine(new(x - (pierce ? 120 : 54), y), new(x, y), new Color(Wind, pierce ? .45f : .3f), pierce ? 4 : 3);
        DrawBlade(x, y, 0, 68, Wind, UiKit.Gold);
    }

    /// <summary>头顶悬浮后追踪（当前无技能使用，形态保留在词汇表里供日后配置）。</summary>
    private void DrawHoverBlade(CombatEffect effect, float x)
    {
        var shape = ShapeOf(effect);
        var (hx, hy) = HoverSlot(effect.Index, shape.Count);
        float bob = (float)Math.Sin(_clock * 3 + effect.Index) * 3;
        if (effect.Timer > 0)   // 悬浮期：停在各自的剑阵位次上
        {
            DrawBlade(hx, hy + bob, .62, 62, Wind, UiKit.Gold);
            DrawRect(new(hx - 30, hy + 24, 22, 2), new Color(Wind, .3f));
            return;
        }
        float t = FlightProgress(effect, x, 330);
        float px = hx + (x - hx) * t, py = hy + (296 - hy) * t + bob * (1 - t);
        double angle = Math.Atan2(296 - hy, Math.Max(1, x - hx));
        DrawLine(new(px - (float)Math.Cos(angle) * 44, py - (float)Math.Sin(angle) * 44), new(px, py), new Color(Wind, .3f), 3);
        DrawBlade(px, py, angle, 68, Wind, UiKit.Gold);
    }

    /// <summary>
    /// 万剑决：固定剑阵从敌人上空垂直落剑。落点 X 已由 Core 按间距算进 effect.X（表现层不再自己偏移，
    /// 否则会出现"看着没打中却掉血"），这里只负责出生高度、空中停留、下落与落点预警。
    /// </summary>
    private void DrawSkyDropBlade(CombatEffect effect, float x)
    {
        var shape = ShapeOf(effect);
        float radius = (float)(effect.AoeRadius > 0 ? effect.AoeRadius : 60);
        // 出生高度带随机高低差；随机值由 Core 摇定一次，逐帧重摇会抖动。
        float top = SkyDropTop(effect.Index) + (float)(effect.Jitter * shape.SpawnJitter);
        // 停留与下落共用 Timer：Timer 大于 duration 时还没开始落，clamp 到 0 即为"悬停中"。
        float fall = (float)Math.Clamp(1 - effect.Timer / Math.Max(.05, shape.Duration), 0, 1);
        float y = Math.Clamp(top + (356 - top) * fall * fall, 6f, 394f);   // 平方：越接近地面越快
        DrawEllipseFloor(x, radius, new Color(Thunder, .08f + .14f * fall));
        DrawRect(new(x - 1, y, 2, Math.Max(0, 356 - y)), new Color(Thunder, .12f));
        if (fall > .85f) DrawEllipseFloor(x, radius * .7f, new Color("#fffce8", .18f));
        // 悬停期轻微上下浮动，读起来是"浮现在空中停了一下"而不是卡住。
        float bob = effect.Timer > 0 ? (float)Math.Sin(_clock * 4 + effect.Index * 1.7) * 4 : 0;
        DrawBlade(x, y + bob, Math.PI / 2, 72, Thunder, UiKit.Gold);
    }

    /// <summary>
    /// 青元剑芒：一道能量流光从角色前方划弧飞出。弧高取 Core 摇定的 effect.Arc（表现层不重摇，否则逐帧抖动），
    /// 允许为负——负值时从下方掠过（上方空间多、下方少，区间由配置给出）。
    /// 夹取：上到 250（弧顶 = 发射高度 300 − 弧高，再高会顶出战斗区）、下到地面线以上（356），不越界也不压下方界面。
    /// </summary>
    private void DrawArcBlade(CombatEffect effect, float x)
    {
        var shape = ShapeOf(effect);
        // 每支各自扇开（纵向 + 横向）：同打一个目标时逻辑 X 相同，不错开就会叠在同一条弧上、数不出几支。
        // 横向偏移只有 ±spread 上下，仍在敌人身上，因此画面与"打在谁身上"不矛盾。
        float lane = (effect.Index - (shape.Count - 1) / 2f) * (float)shape.Spread;
        float from = 330 + 52 + lane * .5f;
        float origin = 300 + (effect.Index - (shape.Count - 1) / 2f) * 16;
        float arc = Math.Clamp((float)effect.Arc, -50f, 250f);
        if (effect.Timer > 0)   // 蓄势期：起点处一点逐渐变亮的光芒，还没射出去
        {
            float charge = (float)Math.Clamp(1 - effect.Timer / Math.Max(.01, shape.Hold), 0f, 1f);
            DrawCircle(new(from, origin), 3 + charge * 4, new Color(Void, .25f + .45f * charge));
            return;
        }
        float t = FlightProgress(effect, x + lane, from);
        float px = from + (x + lane - from) * t;
        float py = Math.Clamp(origin - arc * (float)Math.Sin(Math.PI * t), 6f, 350f);
        double angle = Math.Atan2(-arc * Math.PI * Math.Cos(Math.PI * t), Math.Max(60, x + lane - from));
        DrawArcLight(effect.Index, from, origin, px, py, angle, arc, t);
    }

    /// <summary>
    /// 画"剑芒"：不是实体剑，而是一道能量流光——沿轨迹拖尾取若干采样点，
    /// 先铺一层半透明外发光、再叠一条亮芯（照抄 DrawLightning 的双描边法，本文件没有 shader/bloom 可用）。
    /// </summary>
    private void DrawArcLight(int index, float from, float origin, float px, float py, double angle, float arc, float t)
    {
        const int Steps = 7;
        var spine = new Vector2[Steps + 1];
        for (int i = 0; i <= Steps; i++)
        {
            float back = Math.Max(0, t - i * .035f);   // 往回采样：拖尾贴着已经飞过的那段弧
            float bx = from + (px - from) * (t <= 0 ? 0 : back / t);
            spine[i] = new(bx, Math.Clamp(origin - arc * (float)Math.Sin(Math.PI * back), 6f, 350f));
        }
        DrawPolyline(spine, new Color(Void, .30f), 9);                    // 外发光
        DrawPolyline(spine, new Color("#e8dcff", .85f), 3);               // 亮芯
        DrawCircle(new(px, py), 7, new Color(Void, .55f));                // 头部光晕
        DrawCircle(new(px, py), 3, new Color("#fdfbff", .95f));
        // 顺着弧线的一点方向感：头部前方再补一小段更淡的流光
        DrawLine(new(px, py), new(px + (float)Math.Cos(angle) * 14, py + (float)Math.Sin(angle) * 14), new Color("#e8dcff", .35f), 2);
    }
    private void DrawGround(CombatEffect effect, float x)
    {
        float radius = (float)(effect.AoeRadius > 0 ? effect.AoeRadius : 220);
        float progress = effect.MaxLife > 0 ? (float)(1 - effect.Life / effect.MaxLife) : 0;
        switch (effect.Skill)
        {
            case "skill_03": // 青莲剑阵：青莲法阵与旋转莲瓣
                DrawEllipseFloor(x, radius, new Color(Wind, .15f));
                DrawArc(new(x, 356), radius * .62f, 0, Mathf.Tau, 32, new Color(Wind, .7f), 2);
                for (int i = 0; i < 6; i++)
                {
                    float angle = (float)(_clock * .9 + i * Mathf.Tau / 6);
                    float px = x + (float)Math.Cos(angle) * radius * .55f, py = 352 + (float)Math.Sin(angle) * radius * .16f;
                    DrawColoredPolygon([new(px, py - 14), new(px + 10, py), new(px, py + 14), new(px - 10, py)], new Color(Wind, .75f));
                }
                break;
            case "skill_08": // 霜华剑域：冰域、霜圈与飘落雪点
                DrawEllipseFloor(x, radius, new Color(Frost, .2f));
                DrawArc(new(x, 356), radius * .72f, 0, Mathf.Tau, 40, new Color(Frost, .8f), 2);
                for (int i = 0; i < 9; i++)
                {
                    float sx = x + (float)Math.Sin(i * 2.1) * radius * .78f;
                    float sy = 296 + (float)((_clock * 44 + i * 39) % 68);
                    DrawRect(new(sx, sy, 3, 3), new Color(Frost, .85f));
                }
                break;
            case "skill_13": // 焚天剑阵：火海与跳动火苗
                DrawEllipseFloor(x, radius, new Color(Flame, .22f));
                for (int i = 0; i < 16; i++)
                {
                    float fx = x - radius + i * (radius * 2 / 15);
                    float h = 18 + (float)Math.Sin(_clock * 9 + i * 1.7) * 10;
                    var spark = i % 3 == 0 ? new Color("#ffd98a") : Flame;
                    DrawColoredPolygon([new(fx - 7, 358), new(fx, 358 - h), new(fx + 7, 358)], new Color(spark, .8f));
                }
                break;
            default:
                DrawEllipseFloor(x, radius, new Color(UiKit.Jade, .18f));
                for (int j = 0; j < 7; j++) DrawRect(new(x - radius * .9f + j * (radius * .3f), 322, 3, 31), new Color(UiKit.Jade, .7f));
                break;
        }
        // 收束环：点数要够多，少于 16 个点会画成明显的多边形。
        DrawArc(new(x, 356), radius * (0.9f + progress * .1f), 0, Mathf.Tau, 40, new Color(Colors.White, .14f), 1);
    }
    private void DrawTarget(CombatEffect effect, float x)
    {
        float progress = effect.MaxLife > 0 ? (float)(1 - effect.Life / effect.MaxLife) : 1;
        if (effect.Hostile) { DrawArc(new(330, 340), 38, 0, Mathf.Tau, 24, Hostile, 3); DrawLine(new(330, 170), new(330, 306), Hostile, 2); return; }
        switch (effect.Skill)
        {
            case "skill_02": // 落星诀：天降星芒与落点冲击环
                DrawRect(new(x - 16, 120, 32, 226), new Color(UiKit.Gold, .16f));
                DrawRect(new(x - 5, 120, 10, 226), new Color(UiKit.Gold, .55f));
                DrawArc(new(x, 350), 26 + progress * 48, 0, Mathf.Tau, 26, new Color(UiKit.Gold, .8f), 3);
                break;
            case "skill_07": // 天雷引：锯齿闪电与爆裂环
                DrawLightning(x, 150, 348, Thunder);
                DrawArc(new(x, 350), 30 + progress * 42, 0, Mathf.Tau, 26, new Color(Thunder, .85f), 3);
                break;
            case "skill_12": // 破云诀：下坠破甲符印
                DrawColoredPolygon([new(x, 148), new(x + 22, 202), new(x, 256), new(x - 22, 202)], new Color(Void, .8f));
                DrawColoredPolygon([new(x, 172), new(x + 11, 202), new(x, 232), new(x - 11, 202)], new Color("#efe6ff", .9f));
                DrawArc(new(x, 256), 34, 0, Mathf.Tau, 24, new Color(Void, .85f), 3);
                break;
            default:
                DrawArc(new(x, 340), 38, 0, Mathf.Tau, 24, UiKit.Jade, 3); DrawLine(new(x, 170), new(x, 306), UiKit.Jade, 2); break;
        }
    }
    /// <summary>
    /// 召唤物：三种剑灵各用一张素材，区别于此前全部复用同一张占位图。
    /// 站位取自位次而不是 effect.X——Core 把召唤物的 X 统一钉在玩家身后，
    /// 直接用会让所有召唤物精确重叠（见 FollowerSlot）。
    /// </summary>
    private void DrawSummons(Font font)
    {
        foreach (var effect in Session.Effects.Where(e => e.Kind == "summon"))
        {
            int slot = _companionSlots.GetValueOrDefault(effect);
            var (x, y) = FollowerSlot(slot);
            if (x < -160 || x > 2080) continue;
            // 各槽错开相位，否则多个单位同步浮动，看着像同一张图。
            y += (float)Math.Sin(_clock * 3 + slot * 1.3) * 7;
            string sprite = effect.Skill switch { "skill_05" => "summon_shadow", "skill_10" => "summon_taixu", "skill_15" => "summon_zhuxie", _ => "pet_snow" };
            var tint = effect.Skill switch { "skill_05" => Wind, "skill_10" => Void, "skill_15" => UiKit.Gold, _ => UiKit.Jade };
            DrawEllipseFloor(x, 56, new Color(tint, .16f));
            Sprite(sprite, x, y, 82);
            if (Session.Config.Skills.TryGetValue(effect.Skill, out var skill) && effect.MaxLife > 0 && effect.Life > effect.MaxLife - .7)
            {
                var label = UiKit.Text; label.A = (float)Math.Clamp((effect.MaxLife - effect.Life) / .7, 0, 1) * .92f;
                DrawString(font, new(x - 130, y - 138), skill.Name, HorizontalAlignment.Center, 260, 18, label);
            }
            if (effect.Skill == "skill_15" && effect.ExecuteThreshold > 0)
                DrawString(font, new(x - 60, y - 96), "斩杀", HorizontalAlignment.Center, 120, 16, UiKit.Gold);
        }
    }
    /// <summary>敌人身上的次级效果图标：减速冰环、眩晕电弧、灼烧火苗、易伤裂痕。</summary>
    private void DrawEnemyStatus(EnemyState enemy, float x, float y, float size)
    {
        if (enemy.SlowUntil > 0) DrawArc(new(x, 360), size * .44f, 0, Mathf.Tau, 26, new Color(Frost, .75f), 3);
        if (enemy.StunUntil > 0)
        {
            DrawLightning(x, y - size - 58, y - size - 6, Thunder);
            for (int i = 0; i < 3; i++)
            {
                float angle = (float)(_clock * 5 + i * Mathf.Tau / 3);
                DrawRect(new(x + (float)Math.Cos(angle) * 24 - 2, y - size - 30 + (float)Math.Sin(angle) * 7, 4, 4), Thunder);
            }
        }
        if (enemy.DotUntil > 0)
            for (int i = 0; i < 4; i++)
            {
                float fx = x - 18 + i * 12, h = 15 + (float)Math.Sin(_clock * 12 + i) * 8;
                DrawColoredPolygon([new(fx - 5, y - 4), new(fx, y - 4 - h), new(fx + 5, y - 4)], new Color(Flame, .8f));
            }
        if (enemy.VulnerableUntil > 0)
        {
            DrawLine(new(x - 16, y - size * .58f), new(x + 8, y - size * .34f), Hostile, 2);
            DrawLine(new(x + 8, y - size * .52f), new(x - 10, y - size * .2f), Hostile, 2);
        }
    }
    /// <summary>玩家增益光环：护盾金罡、归元回气、万剑吸血、疾风攻速、醉仙暴击，以及通用增益环。</summary>
    private void DrawPlayerAuras()
    {
        float t = (float)_clock;
        // 攻速：脚下疾风环 + 向后掠过的速度线，读起来就是"出手变快"。
        if (Session.HasteRemaining > 0)
        {
            DrawArc(new(330, 358), 62, 0, Mathf.Tau, 26, new Color(Wind, .5f), 3);
            // 速度线整条落在角色左侧（精灵占 262..398），不压在人物身上。
            for (int i = 0; i < 6; i++)
            {
                float py = 250 + i * 22 + (float)Math.Sin(t * 6 + i) * 4;
                float span = 90 + i * 8;
                DrawLine(new(330 - span - 40 - (float)((t * 120 + i * 24) % 60), py), new(330 - span, py), new Color(Wind, .35f), 3);
            }
        }
        // 暴击：身周金色星芒闪烁，随机相位让它一直在闪。
        if (Session.CritBonusRemaining > 0)
        {
            for (int i = 0; i < 5; i++)
            {
                float angle = t * 1.2f + i * 1.26f;
                float px = 330 + (float)Math.Cos(angle) * 74, py = 312 + (float)Math.Sin(angle) * 48;
                float size = 5 + (float)Math.Sin(t * 8 + i) * 3f;
                DrawLine(new(px - size, py), new(px + size, py), new Color(Thunder, .9f), 3);
                DrawLine(new(px, py - size), new(px, py + size), new Color(Thunder, .9f), 3);
            }
        }
        if (Session.ShieldRemaining > 0)
        {
            var hex = new Vector2[7];
            for (int i = 0; i <= 6; i++)
            {
                float angle = i % 6 / 6f * Mathf.Tau + t * .6f;
                hex[i] = new(330 + (float)Math.Cos(angle) * 80, 300 + (float)Math.Sin(angle) * 92);
            }
            DrawPolyline(hex, new Color(UiKit.Gold, .75f), 3);
        }
        if (Session.RegenRemaining > 0)
        {
            DrawArc(new(330, 358), 70, 0, Mathf.Tau, 30, new Color(UiKit.Jade, .55f), 3);
            for (int i = 0; i < 5; i++)
            {
                float angle = t * 1.6f + i * 1.26f;
                DrawRect(new(330 + (float)Math.Cos(angle) * 62 - 2, 358 - (float)((t * 70 + i * 32) % 120), 4, 4), new Color(UiKit.Jade, .85f));
            }
        }
        if (Session.LifestealRemaining > 0)
        {
            for (int i = 0; i < 8; i++)
            {
                float angle = t * 2 + i / 8f * Mathf.Tau;
                float px = 330 + (float)Math.Cos(angle) * 88, py = 330 + (float)Math.Sin(angle) * 56;
                DrawLine(new(px, py), new(px + (float)Math.Cos(angle) * 18, py + (float)Math.Sin(angle) * 12), new Color(Blood, .85f), 3);
            }
        }
        if (Session.BuffRemaining > 0) DrawArc(new(330, 366), 54, 0, Mathf.Tau, 28, new Color(UiKit.Gold, .5f), 2);
    }
    private void DrawPopups(Font font)
    {
        foreach (var popup in _popups)
        {
            var enemy = Session.Battle.Enemies.FirstOrDefault(e => e.Id == popup.Id);
            float x = X(enemy?.X ?? popup.X);
            if (x < -80 || x > 2040) continue;
            float t = (float)(1 - popup.Life / popup.MaxLife);
            var color = popup.Color; color.A = 1 - t * t;
            DrawString(font, new(x - 60, 322 - t * 48), UiKit.Number(popup.Total), HorizontalAlignment.Center, 120, popup.Size, color);
        }
    }
    private void DrawLightning(float x, float top, float bottom, Color color)
    {
        int count = 7;
        var points = new Vector2[count];
        for (int i = 0; i < count; i++)
            points[i] = new(x + (float)Math.Sin(i * 2.7 + _clock * 30) * (i == count - 1 ? 0 : 15), top + i * (bottom - top) / (count - 1));
        DrawPolyline(points, color, 5);
        DrawPolyline(points, new Color("#fffce8"), 2);
    }
    private void DrawEllipseFloor(float x, float radius, Color color)
    {
        var points = new Vector2[28];
        for (int i = 0; i < points.Length; i++)
        {
            float angle = i / (float)points.Length * Mathf.Tau;
            points[i] = new(x + (float)Math.Cos(angle) * radius, 356 + (float)Math.Sin(angle) * radius * .13f);
        }
        DrawColoredPolygon(points, color);
    }
    private void Sprite(string id, float x, float bottom, float size, Color? color = null)
    {
        if (_textures.TryGetValue(id, out var texture)) DrawTextureRect(texture, new(x - size / 2, bottom - size, size, size), false, color ?? Colors.White);
    }
    private void DrawEllipseShadow(float x, float y, float radius) => DrawRect(new(x - radius, y - 3, radius * 2, 7), new Color(0, 0, 0, .22f));
}
