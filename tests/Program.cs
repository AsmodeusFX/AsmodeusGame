using IdleSword.Core;
using IdleSword.Features;

string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("idle-sword/Config/Tables");
var source = GameConfig.Files.ToDictionary(f => f, f => File.ReadAllText(Path.Combine(root, f)));
var config = GameConfig.Load(f => source[f]);
int passed = 0;
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
void Check(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
// 普攻默认关掉：受控用例要的是确定的伤害预算，而普攻每秒都在加伤害、也在消耗随机数。
// 需要验证真实循环的用例显式传 basic: true。
GameSession New(bool basic = false) { var g = new GameSession(config, seed: 42) { BasicAttackEnabled = basic }; return g; }
void Step(GameSession g, double seconds) { for (int i = 0; i < (int)Math.Ceiling(seconds / .05); i++) g.Step(.05); }
void ToBoss(GameSession g)
{
    g.Battle.PlayerX = (g.Level.Cells - 1) * config.Setting("cell_width") + 80;
    g.Battle.Enemies.Clear(); g.Battle.Spawns.Clear(); g.Battle.PlayerHp = g.MaxHp; g.Step(.05);
}
EnemyState Boss(GameSession g) => g.Battle.Enemies.Single(e => e.Kind == "boss");
void Reject(Action test) { try { test(); } catch (InvalidDataException) { return; } throw new Exception("Expected validation rejection"); }
// 按表头列名改写某一行的单元格：不再硬编码行尾字符串（列一多、值一改就会失效）。
string Cell(string text, string id, string field, string value)
{
    var lines = text.TrimStart('﻿').Split('\n');
    int at = Array.IndexOf(lines[0].TrimEnd('\r').Split(','), field);
    if (at < 0) throw new Exception("no such column: " + field);
    for (int i = 1; i < lines.Length; i++)
    {
        var cells = lines[i].TrimEnd('\r').Split(',');
        if (cells[0] != id) continue;
        if (cells.Length != lines[0].TrimEnd('\r').Split(',').Length) throw new Exception("column count mismatch on " + id);
        cells[at] = value; lines[i] = string.Join(',', cells);
        return string.Join('\n', lines);
    }
    throw new Exception("no such row: " + id);
}
// 单一剑诀 + 三个高血量靶子：靶子定身摆在 offset 处（不定身的话怪物会朝玩家走，弹道到达时机随之漂移）。
GameSession SalvoOn(GameConfig cfg, string skillId, double offset = 90)
{
    // 靶场一律关掉普攻：否则每秒多出一支飞行效果，既有"数得到几支剑"的断言全会被污染，
    // 而且普攻还要抽随机数，同 seed 的可复现序列也会跟着漂。
    var s = new GameSession(cfg, seed: 42) { BasicAttackEnabled = false }; s.Step(.05);
    foreach (var e in s.Battle.Enemies) { e.Atk = 0; e.Hp = e.MaxHp = 1e8; e.X = s.Battle.PlayerX + offset; e.StunUntil = 1e9; }
    s.State.Skills.Clear(); s.State.Skills[skillId] = 1; s.Battle.Cooldowns.Clear(); s.Effects.Clear();
    s.Step(.05);
    return s;
}
GameSession Salvo(string skillId, double offset = 90) => SalvoOn(config, skillId, offset);

Check("all tables / 100 stages / 15 skills / 60 intent upgrades", () => Assert(config.Levels.Count == 100 && config.Skills.Count == 15 && config.Rows("SwordUpgrade").Count == 60, "table counts"));
Check("CSV BOM, quotes, multiline and write roundtrip", () => {
    var csv = CsvTable.Write(new[] { new[] { "id", "name" }, new[] { "1", "a,\"b\"\n中文" } });
    Assert(CsvTable.Parse("roundtrip", "\uFEFF" + csv)[0].Text("name") == "a,\"b\"\n中文", "roundtrip");
    Reject(() => CsvTable.Parse("bad", "id,name\n1,\"bad"));
});
Check("reject duplicate IDs and dangling references", () => {
    Reject(() => GameConfig.Load(f => f == "item.csv" ? source[f] + "gold,重复,currency,重复\n" : source[f]));
    // 波次刷什么怪现由子表 wave_unit 决定，悬空引用要在那里拦。
    Reject(() => GameConfig.Load(f => f == "wave_unit.csv" ? source[f].Replace("wave_1_slime,wave_1,slime", "wave_1_slime,wave_1,missing") : source[f]));
});
Check("reject repeat core and non-deterministic first-core quantity", () => {
    Reject(() => GameConfig.Load(f => f == "drop.csv" ? source[f].Replace("boss_repeat,gold,60", "boss_repeat,core,1") : source[f]));
    Reject(() => GameConfig.Load(f => f == "drop.csv" ? source[f].Replace("boss_first,core,1", "boss_first,core,2") : source[f]));
});
Check("reject talent cycles", () => Reject(() => GameConfig.Load(f => f == "TalentLink.csv" ? source[f] + "cycle,t_end,t_root\n" : source[f])));
Check("reject unknown secondary effect", () => Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_03", "secondary", "bogus") : source[f])));
Check("reject invalid flight shape, count and arc band", () => {
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_11", "trajectory", "warp") : source[f]));
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_07", "trajectory", "arc_homing") : source[f]));   // 非 projectile 不得带形态
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_11", "projectile_count", "0") : source[f]));     // 弹数至少 1
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_07", "projectile_count", "3") : source[f]));     // 非 projectile 只能单发
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_11", "arc_min", "200") : source[f]));            // 弧区间倒挂
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_11", "arc_max", "400") : source[f]));            // 弧高越出战斗画面
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_06", "aoe_radius", "0") : source[f]));           // 天降必须有落点半径
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_11", "arc_min", "-80") : source[f]));           // 下弧越出地面
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_11", "speed", "10") : source[f]));              // 慢到几乎不动的弹道
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_01", "pierce_chance", "1.5") : source[f]));     // 概率不能超过 1
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_04", "secondary_extra", "-1") : source[f]));    // 附加参数不能为负
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_02", "trigger_chance", "1.5") : source[f]));     // 触发概率不能超过 1
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_02", "trigger_chance", "-0.1") : source[f]));    // 触发概率不能为负
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_02", "secondary_duration", "0") : source[f]));   // 天降火海需要正的残留时长
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_12", "secondary", "freeze") : source[f]));       // chill 之外的名字仍然被拒
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_05", "targeting", "weakest") : source[f]));      // 未知的选敌方式
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_05", "cast_root", "-1") : source[f]));           // 定身时长不能为负
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_05", "knockback", "-1") : source[f]));           // 击退距离不能为负
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_02", "aoe_all", "1") : source[f]));              // 天降火海与全体命中互斥
    Reject(() => GameConfig.Load(f => f == "monster.csv" ? Cell(source[f], "slime", "layer", "sky") : source[f]));                    // 未知层级
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_03", "hits", "sky") : source[f]));               // 未知技能定位
    Reject(() => GameConfig.Load(f => f == "wave_unit.csv" ? Cell(source[f], "wave_3_hawk", "wave_id", "wave_9") : source[f]));       // 悬空波次引用
    Reject(() => GameConfig.Load(f => f == "wave_unit.csv" ? Cell(source[f], "wave_1_slime", "max_count", "1") : source[f]));         // 数量上限低于基线
    // 某一波没有任何 wave_unit：那一波会刷不出怪、关卡直接空转，必须在加载期被拒。
    Reject(() => GameConfig.Load(f => f == "wave_unit.csv"
        ? Cell(Cell(Cell(source[f], "wave_3_bat", "wave_id", "wave_1"), "wave_3_hawk", "wave_id", "wave_1"), "wave_3_slime", "wave_id", "wave_1")
        : source[f]));
});
Check("only current cell activates; uncleared waves accumulate", () => {
    var g = New(); g.Step(.05);
    Assert(g.Battle.Spawns.Count == 1 && g.Battle.Enemies.Count == 3, "initial wave");
    foreach (var e in g.Battle.Enemies) { e.Hp = e.MaxHp = 1e8; e.Atk = 0; }
    Step(g, 6.2);
    Assert(g.Battle.Spawns.Count == 1 && g.Battle.Spawns[0].Wave >= 2 && g.Battle.Enemies.Count >= 6, "stack wave");
});
Check("walking can trigger a fresh wave before passing spawn", () => {
    var g = New(); g.Step(.05); g.Battle.Enemies.Clear();
    g.Battle.PlayerX = 1200; g.Battle.Spawns[0].Timer = .01; g.Step(.05);
    Assert(g.Moving && g.Battle.Enemies.Count == 3 && !g.Battle.Spawns[0].Passed, "walking spawn");
});
Check("passing stops spawn without removing existing enemies", () => {
    var g = New(); g.Step(.05); g.Battle.PlayerX = 1449;
    foreach (var e in g.Battle.Enemies) e.X = 3000;
    g.Battle.Spawns[0].Timer = .01; g.Step(.05);
    Assert(g.Battle.Spawns[0].Passed && g.Battle.Enemies.Count == 3, "pass lifecycle");
});
Check("waves mix several monster templates, spread across distinct spawn points", () => {
    var g = New(); g.Step(.05);
    Assert(config.WaveUnits["wave_1"].Count == 2, "wave_1 is configured from two templates");
    Assert(g.Battle.Enemies.Count == 3, $"the wave still spawns three units in total: {g.Battle.Enemies.Count}");
    var kinds = g.Battle.Enemies.Select(e => e.MonsterId).OrderBy(id => id).ToArray();
    Assert(kinds.SequenceEqual(["slime", "slime", "tank"]), "the mix matches the table: " + string.Join(",", kinds));
    Assert(g.Battle.Enemies.Select(e => e.X).Distinct().Count() == 3, "no two units share a spawn point");
});
Check("wave size grows with the stage and stops at the per-template ceiling", () => {
    int Ceiling() => config.Rows("wave_unit")
        .Where(u => u.Text("wave_id") == config.Levels.Single(l => l.Id == "level_100").Wave).Sum(u => u.Int("max_count"));
    var late = New();
    late.State.UnlockedLevels.Add("level_100"); late.SelectLevel("level_100");
    late.Step(.05);
    Assert(late.Battle.Enemies.Count > 3, $"a late stage spawns more than the 3-unit baseline: {late.Battle.Enemies.Count}");
    Assert(late.Battle.Enemies.Count <= Ceiling(), $"and never past the ceiling: {late.Battle.Enemies.Count} vs {Ceiling()}");
    // 把倍率拉到很大，数量照样止步在 max_count 之和——上限是硬约束，不是"大致如此"。
    var huge = GameConfig.Load(f => f == "game_settings.csv" ? Cell(source[f], "wave_growth", "value", "9") : source[f]);
    var capped = new GameSession(huge, seed: 42) { BasicAttackEnabled = false };
    capped.State.UnlockedLevels.Add("level_100"); capped.SelectLevel("level_100");
    capped.Step(.05);
    Assert(capped.Battle.Enemies.Count == Ceiling(), $"the ceiling holds: {capped.Battle.Enemies.Count} vs {Ceiling()}");
});
Check("in-range targets stop movement", () => {
    var g = New(); g.Step(.05); g.Battle.Enemies[0].X = g.Battle.PlayerX + 300;
    double x = g.Battle.PlayerX; g.Step(.05); Assert(g.Battle.PlayerX == x && !g.Moving, "stop");
});
Check("normal death resets progress and keeps resources", () => {
    var g = New(); g.Battle.PlayerX = 5000; g.State.Wallet["gold"] = 999; g.Battle.PlayerHp = 0; g.Step(.05);
    Assert(g.Battle.Cell == 0 && g.Battle.PlayerX == 80 && g.State.Amount("gold") == 999 && g.Battle.RespawnTimer > 0, "normal death");
});
Check("boss-cell respawn preserves wounds and freezes encounter during respawn", () => {
    var g = New(); ToBoss(g); Boss(g).Hp = 123; g.Battle.PlayerHp = 0; g.Step(.05);
    int waves = g.Battle.Spawns[24].Wave; Step(g, 1);
    Assert(Boss(g).Hp == 123 && g.Battle.Cell == 24 && g.Battle.Spawns[24].Wave == waves, "boss persistence");
    Step(g, 1.05); Assert(g.Battle.PlayerHp == g.MaxHp, "full hp respawn");
});
Check("rift immune until every non-rift enemy dies", () => {
    var g = New(); ToBoss(g); var rift = g.Battle.Enemies.Single(e => e.Kind == "rift"); double hp = rift.Hp;
    g.HurtEnemy(rift, 1e9); Assert(rift.Hp == hp, "rift immune");
    g.HurtEnemy(Boss(g), 1e9); g.HurtEnemy(rift, 1e9); Assert(rift.Hp == hp, "adds gate");
    foreach (var e in g.Battle.Enemies.Where(e => e.Kind != "rift").ToArray()) g.HurtEnemy(e, 1e9);
    Assert(g.RiftUnlocked, "unlock"); g.HurtEnemy(rift, 1e9); g.Step(.05);
    Assert(g.Level.Id == "level_002", "next stage");
});
Check("rift unlocked beyond attack range still advances and clears the stage", () => {
    var g = New(); ToBoss(g); g.HurtEnemy(Boss(g), 1e9);
    foreach (var e in g.Battle.Enemies.Where(e => e.Kind != "rift").ToArray()) g.HurtEnemy(e, 1e9);
    // BOSS 格阵亡后的重生点距裂隙 1730，远超任一剑诀射程 950；旧逻辑在解锁瞬间冻结移动，挂机永久中断。
    g.Battle.PlayerX = (g.Level.Cells - 1) * config.Setting("cell_width") + 80;
    double rift = g.Battle.Enemies.Single(e => e.Kind == "rift").X;
    g.Step(.05);
    Assert(g.RiftUnlocked && rift - g.Battle.PlayerX > g.AttackRange, "unlocked while out of range");
    Assert(g.Moving, "advance instead of freezing out of range");
    Step(g, 3);
    Assert(rift - g.Battle.PlayerX <= g.AttackRange && !g.Moving, "closed to attack range");
    Step(g, 30);
    Assert(g.Level.Id == "level_002", "stage cleared");
});
Check("boss death stops adds; death keeps boss dead and first reward", () => {
    var g = New(); ToBoss(g); g.HurtEnemy(Boss(g), 1e9); g.Battle.PlayerHp = 0; g.Step(.05); Step(g, 2.1);
    Assert(g.Battle.BossDefeated && !g.Battle.Enemies.Any(e => e.Kind == "boss") && g.Battle.Spawns[24].Wave == 1 && g.State.Amount("core") == 1, "boss death state");
});
Check("repeat kill grants zero core; same template in another stage grants one", () => {
    var g = New(); ToBoss(g); g.HurtEnemy(Boss(g), 1e9);
    g.SelectLevel("level_001"); ToBoss(g); g.HurtEnemy(Boss(g), 1e9); Assert(g.State.Amount("core") == 1, "repeat");
    g.State.UnlockedLevels.Add("level_002"); g.SelectLevel("level_002"); ToBoss(g); g.HurtEnemy(Boss(g), 1e9);
    Assert(g.State.Amount("core") == 2 && g.State.FirstKills.Count == 2, "per-stage ledger");
});
Check("all 100 unique first kills issue exactly 100 cores", () => {
    var g = New(); foreach (var level in config.Levels) { g.State.UnlockedLevels.Add(level.Id); g.SelectLevel(level.Id); ToBoss(g); g.HurtEnemy(Boss(g), 1e9); }
    Assert(g.State.Amount("core") == 100 && g.State.FirstKills.Count == 100, "finite supply");
});
Check("old-stage loop and final-stage loop", () => {
    var g = New(); g.SelectLevel("level_001"); ToBoss(g);
    foreach (var e in g.Battle.Enemies.Where(e => e.Kind != "rift").ToArray()) g.HurtEnemy(e, 1e9);
    g.HurtEnemy(g.Battle.Enemies.Single(e => e.Kind == "rift"), 1e9); g.Step(.05);
    Assert(g.Level.Id == "level_001" && g.Battle.Cell == 0 && g.State.UnlockedLevels.Contains("level_002"), "old loop");
    g.State.UnlockedLevels.Add("level_100"); g.SelectLevel("level_100"); g.State.LoopLevel = false; ToBoss(g);
    foreach (var e in g.Battle.Enemies.Where(e => e.Kind != "rift").ToArray()) g.HurtEnemy(e, 1e9);
    g.HurtEnemy(g.Battle.Enemies.Single(e => e.Kind == "rift"), 1e9); g.Step(.05); Assert(g.Level.Id == "level_100", "last loop");
});
Check("no valid target consumes no skill cooldown", () => {
    var g = New(); g.Step(.05); Assert(g.Battle.Cooldowns.Count == 0, "empty range cooldown");
});
Check("intent auto-production, capacity and hover collection contract", () => {
    var g = New(); g.State.Talents["t_auto"] = 1; Step(g, 3.05);
    Assert(g.State.PendingIntent["intent_0"] == 1 && g.State.Amount("intent_0") == 0, "pending");
    for (int i = 0; i < 250; i++) g.ClickOre("ore_0");
    Assert(g.State.PendingIntent["intent_0"] == 200, "capacity");
    g.CollectIntent("intent_0"); g.CollectIntent("intent_0"); Assert(g.State.Amount("intent_0") == 200, "collect once");
});
Check("talent visibility, atomic costs and finite core spending", () => {
    var g = New(); Assert(!g.TalentVisible("t_auto") && !g.BuyTalent("t_auto"), "hidden node");
    g.State.Wallet["gold"] = 1000; Assert(g.BuyTalent("t_root") && g.TalentVisible("t_hp"), "adjacent");
    g.BuyTalent("t_hp"); double gold = g.State.Amount("gold"); Assert(!g.BuyTalent("t_auto") && g.State.Amount("gold") == gold, "atomic cost");
});
Check("all live effect types execute without invalid targets", () => {
    var g = New(); foreach (var realm in config.Rows("SwordLevel")) g.State.Realms.Add(realm.Text("id"));
    foreach (var id in config.Skills.Keys) g.State.Skills[id] = 1;
    g.Step(.05); foreach (var e in g.Battle.Enemies) { e.Hp = e.MaxHp = 1e8; e.Atk = 0; e.X = g.Battle.PlayerX + 200; }
    // 三个真诀只由普攻概率触发，不会在冷却到点自动释放；这里显式各放一次，保证四种在役 kind 都能被看到。
    foreach (var id in config.Skills.Keys.Where(id => config.Skills[id].TriggerChance > 0))
        Assert(g.ForceRelease(id), "trigger skill released: " + id);
    g.Step(.05);
    // 在役剑诀只剩 projectile / ground / target / buff 四种（buff 不产生 CombatEffect）。
    // summon 随化神档三个召唤技能一起退役，覆盖搬到了"退休路径"那条用例里（用内存改配置造一个召唤剑诀）。
    Assert(g.Effects.Any(e => e.Kind == "projectile") && g.Effects.Any(e => e.Kind == "ground") && g.Effects.Any(e => e.Kind == "target"), "effect kinds");
    Step(g, 15); Assert(g.Battle.Enemies.Any(e => e.Hp < e.MaxHp), "effects damage");
});
Check("secondary: vulnerable amplifies, stun freezes, slow halves, dot ticks", () => {
    var g = New(); g.Step(.05); var e = g.Battle.Enemies[0]; e.X = 5000; e.Atk = 0;
    e.Hp = e.MaxHp = 1000; e.VulnerableUntil = 1; e.VulnerableFactor = 1.5;
    g.HurtEnemy(e, 100); Assert(e.Hp == 850, "vulnerable");
    e.StunUntil = 1; double sx = e.X; g.Step(.05); Assert(e.X == sx, "stun");
    e.StunUntil = 0; e.SlowUntil = 1; e.SlowFactor = .5; sx = e.X; g.Step(.05);
    Assert(e.X < sx && e.X > sx - 6, "slow");
    e.Hp = e.MaxHp = 1000; e.VulnerableUntil = 0; e.VulnerableFactor = 1; e.DotUntil = 1; e.DotDps = 50; double hp = e.Hp; g.Step(1);
    Assert(e.Hp < hp - 40 && e.Hp > hp - 60, "dot");
});
// hover_homing 已无在役技能使用（御剑术改走 line_pierce），但形态留在词汇表里供日后配，故用改过的配置保住覆盖。
var hoverForm = GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_01", "trajectory", "hover_homing") : source[f]);
Check("hover_homing hovers above the caster, then homes and hits", () => {
    var g = SalvoOn(hoverForm, "skill_01");
    var blade = g.Effects.First(e => e.Trajectory == "hover_homing");   // 御剑术默认 3 支，各支行为一致
    Assert(blade.Timer > 0 && blade.X == g.Battle.PlayerX, "hovers above the caster before flying");
    double hp = g.Battle.Enemies[0].Hp;
    Step(g, 1);                                     // 悬浮 0.12s + 飞行
    Assert(g.Battle.Enemies.Any(e => e.Hp < hp), "hits the target after hovering");
});
Check("sky_drop builds a fixed formation of distinct landing points", () => {
    var g = Salvo("skill_06");                      // 万剑决：5 支、间距 40、落点半径 60
    var blades = g.Effects.Where(e => e.Trajectory == "sky_drop").ToArray();
    Assert(blades.Length == 5, $"five blades: {blades.Length}");
    double center = g.Battle.PlayerX + 90;          // 三个靶子重叠在阵心
    var lands = blades.Select(b => b.X).OrderBy(x => x).ToArray();
    Assert(lands.Distinct().Count() == 5, "landing points are spread out, not stacked: " + string.Join(",", lands));
    Assert(lands[2] == center, "the middle blade lands on the formation centre");
    // 间距固定：只有落在半径 60 之内的三支（-40 / 0 / +40）够得到阵心的敌人，与"只有一个敌人"无关地铺开。
    Assert(blades.Count(b => Math.Abs(b.X - center) < 60) == 3, "exactly three landing points reach the centre");
    Assert(blades.Select(b => b.Timer).Distinct().Count() > 1, "blades appear staggered, not all at once");
    var xs = blades.Select(b => b.X).ToArray();
    Step(g, .1);                                    // 不追踪：位置必须冻结
    Assert(g.Effects.Where(e => e.Trajectory == "sky_drop").Select(b => b.X).OrderBy(x => x).SequenceEqual(xs.OrderBy(x => x)), "x stays frozen while falling");
    Step(g, .7);                                    // 停留 0.25s + 下落 0.5s 后落地：三个重叠单位都要挨打
    Assert(g.Battle.Enemies.Count(e => e.Hp < e.MaxHp) == 3, "every overlapping unit was hit");
});
Check("arc_homing fires a configurable salvo with distinct targets and reproducible arcs", () => {
    // 靶子放到 160：贴着 90 摆时剑芒会在同一个 Step 内就飞到并结算移除，观察不到编排信息。
    var g = Salvo("skill_11", 160);                 // 青元剑芒：3 支
    var arcs = g.Effects.Where(e => e.Trajectory == "arc_homing").Select(e => e.Arc).ToArray();
    Assert(arcs.Length == 3, $"three blades: {arcs.Length}");
    Assert(arcs.All(a => a >= -50 && a <= 140), "arc inside the configured band: " + string.Join(",", arcs));
    Assert(arcs.Distinct().Count() > 1, "arcs are randomised, not constant");
    Assert(g.Effects.Select(e => e.Target).Distinct().Count() == 3, "each blade picks its own target");
    Assert(g.Effects.All(e => e.Speed == 1000), "slower configured speed is used");
    Assert(g.Effects.Select(e => e.Timer).Distinct().Count() > 1, "blades are launched with a time gap");
    var again = Salvo("skill_11", 160);
    Assert(again.Effects.Where(e => e.Trajectory == "arc_homing").Select(e => e.Arc).SequenceEqual(arcs), "same seed reproduces the arcs");
});
Check("arc_homing can bend downwards, keeping both sides of the lane in play", () => {
    // 单个靶子 + 反复出手，收集足够多的弧度：区间带负值，应当既有上弧也有下弧（上方多、下方少）。
    var g = Salvo("skill_11", 160);
    var arcs = new List<double>();
    for (int cast = 0; cast < 60; cast++)
    {
        g.Battle.Cooldowns.Clear(); g.Effects.Clear(); g.Step(.05);
        arcs.AddRange(g.Effects.Where(e => e.Trajectory == "arc_homing").Select(e => e.Arc));
    }
    Assert(arcs.Count >= 60, "collected enough arcs to judge the band");
    Assert(arcs.Any(a => a > 0) && arcs.Any(a => a < 0), $"both up and down arcs occur: min={arcs.Min():0.#} max={arcs.Max():0.#}");
    Assert(arcs.Count(a => a < 0) < arcs.Count(a => a > 0), "downward arcs are the minority");
});
Check("line_shot strikes the first enemy on its path and is destroyed", () => {
    // 御剑术：肩侧横射、命中即散、不穿透。清空技能保证伤害只可能来自这些剑。
    var g = New(); g.Step(.05); g.State.Skills.Clear(); g.Battle.Cooldowns.Clear();
    var line = g.Battle.Enemies.ToArray();
    for (int i = 0; i < line.Length; i++) { line[i].Atk = 0; line[i].Hp = line[i].MaxHp = 1e8; line[i].X = g.Battle.PlayerX + 200 + i * 400; line[i].StunUntil = 1e9; }
    g.Effects.Clear();
    g.State.Skills["skill_01"] = 1; g.Battle.Cooldowns.Clear();
    g.Step(.05);
    var blades = g.Effects.Where(e => e.Trajectory == "line_shot").ToArray();
    Assert(blades.Length == 3, $"three blades by default: {blades.Length}");
    Assert(blades.All(b => b.X == g.Battle.PlayerX && b.Speed == 1500), "start at the caster with the configured speed");
    Assert(blades.All(b => b.Timer > 0), "blades surface before being launched");
    Step(g, 1);
    // 三个敌人相隔 400（远超剑的命中半径），每支剑只可能打到最靠前的那个：最前面的挨打，后面两个毫发无损。
    Assert(line[0].Hp < line[0].MaxHp, "the nearest enemy was struck");
    Assert(line[1].Hp == line[1].MaxHp && line[2].Hp == line[2].MaxHp, "the shot is destroyed on impact and never pierces");
});
Check("line_shot pierces only on the first hit, and only when the chance allows", () => {
    // 概率穿透：一次判定机会。pierce_chance = 1 时第一击必穿，穿完标记下来，第二击照样销毁。
    var piercing = GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_01", "pierce_chance", "1") : source[f]);
    var g = SalvoOn(piercing, "skill_01", 200);
    var victim = g.Battle.Enemies[0];
    // 只留一个身前靶子：SalvoOn 把三个靶子放在同一个 X，剑穿过后会立刻撞上第二个同位置的靶子就销毁。
    foreach (var extra in g.Battle.Enemies.Skip(1).ToArray()) g.Battle.Enemies.Remove(extra);
    var behind = new EnemyState { Id = g.Battle.NextEnemyId++, MonsterId = victim.MonsterId, Kind = victim.Kind, X = victim.X + 300, Hp = 1e8, MaxHp = 1e8, Atk = 0, AttackTimer = 999, StunUntil = 1e9 };
    g.Battle.Enemies.Add(behind);
    g.Effects.Clear(); g.Battle.Cooldowns.Clear(); g.Step(.05);
    Step(g, .5);
    Assert(victim.Hp < victim.MaxHp && behind.Hp < behind.MaxHp, "the pierce carried the blade into the enemy behind");
    behind.Hp = behind.MaxHp; victim.Hp = victim.MaxHp;
    g.Effects.Clear(); g.Battle.Cooldowns.Clear(); g.Step(.05);
    var blade = g.Effects.First(e => e.Trajectory == "line_shot");
    Assert(!blade.Pierced, "a fresh blade has not spent its pierce yet");
    for (int i = 0; i < 12 && !blade.Pierced; i++) g.Step(.05);
    Assert(blade.Pierced, "the first hit marked the blade as already pierced, so it can never pierce again");
});
Check("line_pierce sweeps every enemy along the line", () => {
    // 恒穿透形态现在由空明虚空剑与剑气流云壁使用；这里仍用改过的配置单独跑一遍，作为形态自身的对照
    // （两个在役技能各自还叠了击退与寒冷，混在一起就看不清形态本身的行为）。
    var sweeping = GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(Cell(source[f], "skill_01", "trajectory", "line_pierce"), "skill_01", "projectile_count", "1") : source[f]);
    var g = SalvoOn(sweeping, "skill_01", 200);
    var line = g.Battle.Enemies.ToArray();
    for (int i = 0; i < line.Length; i++) { line[i].X = g.Battle.PlayerX + 200 + i * 100; line[i].Hp = line[i].MaxHp = 1e8; }
    g.Effects.Clear(); g.Battle.Cooldowns.Clear(); g.Step(.05);
    Step(g, .5);
    Assert(line.All(e => e.Hp < e.MaxHp), "an always-piercing blade sweeps through every enemy along the line");
});
Check("basic attack fires one flat shot per interval, and stays silent without a legal target", () => {
    // 关掉暴击：普攻伤害要能被精确断言，暴击会把数字乘 1.5，抽签结果还随 seed 漂。
    var plain = GameConfig.Load(f => f == "fightattr.csv" ? Cell(source[f], "crit", "base_value", "0") : source[f]);
    var g = new GameSession(plain, seed: 42) { BasicAttackEnabled = true };
    g.State.Skills.Clear(); g.Battle.Cooldowns.Clear();
    g.Step(.05);
    // 初始波在普攻射程 950 之外：不空放，也不写下间隔。
    Assert(g.Effects.Count == 0 && !g.Battle.Cooldowns.ContainsKey(GameSession.BasicAttackKey), "out of range means no basic attack");
    var target = g.Battle.Enemies[0];
    foreach (var e in g.Battle.Enemies.Skip(1).ToArray()) g.Battle.Enemies.Remove(e);
    target.X = g.Battle.PlayerX + 500; target.Hp = target.MaxHp = 1e6;
    target.Atk = 0; target.AttackTimer = 999; target.StunUntil = 1e9;
    g.Step(.05);
    var shot = g.Effects.Single(e => e.Skill == "");
    Assert(shot.Kind == "projectile" && Math.Abs(shot.Damage - g.Attack * plain.Attr("basic_power")) < 1e-9,
        $"the shot carries attack x basic_power: {shot.Damage}");
    Assert(Math.Abs(g.Battle.Cooldowns[GameSession.BasicAttackKey] - plain.Attr("basic_interval")) < 1e-9, "the interval starts ticking");
    Step(g, 1);
    Assert(target.Hp < target.MaxHp, "the shot lands on the target");
    // 节奏：冷却从 0 起跑 2.55 秒正好三手（0.05 / 1.05 / 2.05），间隔由配置决定。
    g.Battle.Cooldowns[GameSession.BasicAttackKey] = 0;
    int fired = 0; double previous = 0;
    for (int i = 0; i < 51; i++)
    {
        g.Step(.05);
        double now = g.Battle.Cooldowns.GetValueOrDefault(GameSession.BasicAttackKey);
        if (now > previous + .5) fired++;   // 间隔被顶回满值 = 出了一手
        previous = now;
    }
    Assert(fired == 3, $"one shot per configured interval: {fired} in 2.55s");
});
Check("trigger skills never auto-cast, only fire on a basic attack, and stay gated by cooldown", () => {
    // 御雷真诀（触发概率 4%）：靶场关了普攻，冷却到点也绝不会自己放出来。
    var idle = Salvo("skill_07");
    Step(idle, 30);
    Assert(!idle.Effects.Any(e => e.Skill == "skill_07"), "a trigger skill never auto-casts on cooldown");
    // 概率拉到 1：出手的普攻必定带出这一手。
    var certain = GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_07", "trigger_chance", "1") : source[f]);
    // 靶子放远（>99）：贴脸摆的话普攻飞剑会在同一个 Step 内就命中并被移除，看不到它还留在场上。
    var g = SalvoOn(certain, "skill_07", 200); g.BasicAttackEnabled = true;
    g.Battle.Cooldowns.Clear(); g.Effects.Clear();
    g.Step(.05);
    Assert(g.Effects.Any(e => e.Kind == "target" && e.Skill == "skill_07"), "the basic attack is what pulls the trigger");
    // 冷却仍是硬门槛：把它顶满，概率再高也不出手（同时验证这一手普攻确实出去了，不是空断言）。
    g.Effects.Clear(); g.Battle.Cooldowns["skill_07"] = 30; g.Battle.Cooldowns[GameSession.BasicAttackKey] = 0;
    g.Step(.05);
    Assert(g.Effects.Any(e => e.Skill == ""), "the basic attack itself still happens");
    Assert(!g.Effects.Any(e => e.Skill == "skill_07"), "a cooling trigger skill cannot fire");
});
Check("焚天剑诀 lands a flame sword that leaves a lingering fire sea refreshing the burn", () => {
    var g = Salvo("skill_02", 90);                  // 三个靶子重叠在阵心
    Assert(g.ForceRelease("skill_02"), "the flame sword is released");
    var blade = g.Effects.Single(e => e.Trajectory == "sky_drop");
    Assert(blade.Secondary == "dot" && Math.Abs(blade.AoeRadius - 120) < 1e-9, "the sword carries dot and the landing radius");
    Assert(!g.Effects.Any(e => e.Kind == "ground"), "the sea does not exist before the sword lands");
    Step(g, .8);                                    // 停留 0.25s + 下落 0.5s
    var sea = g.Effects.SingleOrDefault(e => e.Kind == "ground" && e.Skill == "skill_02");
    Assert(sea is not null, "landing leaves a fire sea");
    Assert(Math.Abs(sea!.MaxLife - 3) < 1e-9, $"the sea burns for the configured duration: {sea.MaxLife}");
    Assert(Math.Abs(sea.AoeRadius - 120) < 1e-9, "the sea keeps the landing radius");
    Step(g, .1);                                    // 火海下一步才第一次结算
    Assert(g.Battle.Enemies.All(e => e.DotUntil > 0), "everything inside the sea is set alight");
    double hp = g.Battle.Enemies[0].Hp;
    Step(g, 1);
    Assert(g.Battle.Enemies[0].Hp < hp && g.Battle.Enemies[0].DotUntil > 2.5, "the sea keeps damaging and refreshing the burn");
    Step(g, 3);
    Assert(!g.Effects.Any(e => e.Kind == "ground" && e.Skill == "skill_02"), "the sea burns out after its duration");
});
Check("剑气流云壁 pushes a tornado forward, hitting everything and chilling it", () => {
    var g = Salvo("skill_12", 200);                 // 剑气流云壁：line_pierce + chill
    Assert(g.ForceRelease("skill_12"), "the tornado is released");
    var tornado = g.Effects.Single();
    Assert(tornado.Trajectory == "line_pierce" && tornado.Speed == 420 && Math.Abs(tornado.Life - 3) < 1e-9,
        $"speed and duration come from config: spd={tornado.Speed} life={tornado.Life}");
    Step(g, 1);
    Assert(g.Battle.Enemies.All(e => e.Hp < e.MaxHp), "everything along the path is struck");
    var e0 = g.Battle.Enemies[0];
    Assert(e0.ChillUntil > 0 && Math.Abs(e0.SlowFactor - .75) < 1e-9, $"chill slows by 25%: {e0.SlowFactor}");
    Assert(e0.SlowUntil > 0, "chill drives the shared movement slow");
    Assert(g.Effects.Any(x => x.Skill == "skill_12"), "the tornado is still travelling");
    Step(g, 3);
    Assert(!g.Effects.Any(x => x.Skill == "skill_12"), "the tornado dies once its configured duration runs out");
    // 再次命中是刷新而不是叠加。
    e0.ChillUntil = .1; e0.SlowUntil = .1;
    g.Effects.Clear(); g.Battle.Cooldowns.Clear();
    Assert(g.ForceRelease("skill_12"), "second release");
    Step(g, 1);
    Assert(e0.ChillUntil > 4, $"a second hit refreshes the chill: {e0.ChillUntil}");
});
Check("空明虚空剑 roots the whole field at cast, then a ground wave shoves each enemy exactly once", () => {
    // 定身：射程 950 之外的敌人也要被定住——"全屏"不看射程。
    var rooted = Salvo("skill_05", 200);
    var line = rooted.Battle.Enemies.ToArray();
    for (int i = 0; i < line.Length; i++) { line[i].X = rooted.Battle.PlayerX + 200 + i * 900; line[i].StunUntil = 0; }
    rooted.Effects.Clear(); rooted.Battle.Cooldowns.Clear();
    Assert(rooted.ForceRelease("skill_05"), "the wave is released");
    Assert(line.All(e => Math.Abs(e.StunUntil - 1) < 1e-9), "every enemy is rooted at cast, range ignored");
    var wave = rooted.Effects.Single(e => e.Skill == "skill_05");
    Assert(wave.Timer > 0 && wave.X == rooted.Battle.PlayerX, "the wave waits out its wind-up at the caster");
    Step(rooted, .4);                                   // 前摇只有 0.5 秒：这里还在里面
    Assert(wave.X == rooted.Battle.PlayerX, "still winding up before it launches");
    // 前摇期间角色还在前进：发射点必须跟着人走。冻结在施放那一刻的话，起飞时剑气已经被甩到身后，
    // 表现为"冲击波从画面左边冒出来"——这条是那个 bug 的回归护栏。
    rooted.Battle.PlayerX += 200;
    Step(rooted, .05);
    Assert(wave.X == rooted.Battle.PlayerX, "the launch point follows the caster during the wind-up");
    Step(rooted, .3);                                   // 前摇 0.5 秒走完，剑气开始推进
    Assert(wave.X > rooted.Battle.PlayerX, "the wave is on its way once the wind-up ends");
    // 击退：靶子被定死（不还手、不移动），位置变化只可能来自剑气。
    var pushed = Salvo("skill_05", 200);
    foreach (var e in pushed.Battle.Enemies) e.X = pushed.Battle.PlayerX + 600;
    var victim = pushed.Battle.Enemies[0];
    pushed.Effects.Clear(); pushed.Battle.Cooldowns.Clear();
    Assert(pushed.ForceRelease("skill_05"), "second release");
    double x0 = victim.X, hp0 = victim.Hp;
    Step(pushed, 2.5);                                  // 0.5s 前摇 + 600/700 ≈ 0.86s 推进
    Assert(victim.Hp < hp0, "the wave damaged it");
    // 正好推开一个 knockback 的距离：多挨一次就会是两倍，这一条同时证明了"每个敌人只算一次"。
    Assert(Math.Abs(victim.X - (x0 + 160)) < 1e-9, $"pushed exactly once: {x0} -> {victim.X}");
    Step(pushed, 3.5);                                  // duration 4 + 前摇 0.5 = 寿命 4.5 秒
    Assert(!pushed.Effects.Any(e => e.Skill == "skill_05"), "the wave dies after its configured duration");
});
Check("斩鬼神 strikes the field's healthiest enemy however far away, and only after the wind-up", () => {
    var g = Salvo("skill_10", 200);
    var line = g.Battle.Enemies.ToArray();
    foreach (var e in line) { e.X = g.Battle.PlayerX + 200; e.Hp = e.MaxHp = 1e6; }
    var near = line[0]; near.Hp = near.MaxHp = 1e3;               // 身前残血
    var far = line[1]; far.X = g.Battle.PlayerX + 4000; far.Hp = far.MaxHp = 1e9;   // 射程外满血
    g.Battle.Enemies.Remove(line[2]);
    g.Effects.Clear(); g.Battle.Cooldowns.Clear();
    Assert(g.ForceRelease("skill_10"), "the strike is released");
    var strike = g.Effects.Single(e => e.Skill == "skill_10");
    Assert(strike.Target == far.Id, "the healthiest enemy is picked, range ignored");
    Assert(far.Hp == far.MaxHp && near.Hp == near.MaxHp, "nothing has landed yet — the sword is still winding up");
    Step(g, 1);                                                    // duration 0.9s
    Assert(far.Hp < far.MaxHp, "the chosen enemy was struck");
    Assert(near.Hp == near.MaxHp, "and the one in front was left alone");
});
Check("诛仙剑阵 drops four swords in sequence, each one striking every enemy on the field", () => {
    var g = Salvo("skill_15", 400);
    var line = g.Battle.Enemies.ToArray();
    // 一个在身前、一个在身后、一个远到任何落点半径都够不着：aoe_all 必须三个全打。
    line[0].X = g.Battle.PlayerX + 400;
    line[1].X = g.Battle.PlayerX - 500;
    line[2].X = g.Battle.PlayerX + 9000;
    g.Effects.Clear(); g.Battle.Cooldowns.Clear();
    Assert(g.ForceRelease("skill_15"), "the array is released");
    var swords = g.Effects.Where(e => e.Trajectory == "sky_drop").ToArray();
    Assert(swords.Length == 4, $"four swords: {swords.Length}");
    Assert(swords.Select(s => s.X).Distinct().Count() == 4, "four distinct landing points: " + string.Join(",", swords.Select(s => s.X)));
    // 四柄同时浮现、同时落地（volley_interval 为 0）：同一批的起飞前停留必须完全一致。
    // 初始高度的差别是表现层的事（SkyDropTop 分层 + spawn_jitter），Core 不参与。
    Assert(swords.Select(s => s.Timer).Distinct().Count() == 1, "the swords appear and land together: " + string.Join(",", swords.Select(s => s.Timer)));
    Assert(swords.All(s => s.AoeAll), "the array is configured to strike the whole field");
    Step(g, 2);                                                    // 浮现停顿 1 + 下落 0.5，四柄同时
    Assert(line.All(e => e.Hp < e.MaxHp), "every enemy was struck, wherever it stands");
});
Check("ground-only skills cannot touch flying monsters, while everything else still reaches them", () => {
    var g = New(); g.Step(.05);
    g.State.Skills.Clear(); g.Battle.Enemies.Clear();
    EnemyState Add(string monster, double x)
    {
        var e = new EnemyState { Id = g.Battle.NextEnemyId++, MonsterId = monster, Kind = "normal", X = x, Hp = 1e6, MaxHp = 1e6, Atk = 0, AttackTimer = 999, StunUntil = 1e9 };
        g.Battle.Enemies.Add(e); return e;
    }
    var ground = Add("slime", g.Battle.PlayerX + 200);
    var flyer = Add("bat", g.Battle.PlayerX + 260);   // 就在地面靶旁边：没有层判定的话它一定会被波及
    // 青莲剑阵定位是 ground：地面靶掉血，空中靶毫发无损。
    g.State.Skills["skill_03"] = 1; g.Battle.Cooldowns.Clear(); g.Effects.Clear();
    g.Step(.05);
    Step(g, 1);
    Assert(ground.Hp < ground.MaxHp, "the ground target was struck");
    Assert(flyer.Hp == flyer.MaxHp, $"the flying target was left alone: {flyer.Hp}");
    // 不限层的技能（御剑术 / 普攻）照样打得到它——这是"只练地面招也不会卡死"的护栏。
    g.Battle.Enemies.Remove(ground);
    g.State.Skills.Clear(); g.State.Skills["skill_01"] = 1; g.Battle.Cooldowns.Clear();
    flyer.Hp = flyer.MaxHp; flyer.X = g.Battle.PlayerX + 300;
    Step(g, 3);
    Assert(flyer.Hp < flyer.MaxHp, "a layer-agnostic skill still reaches the flyer");
});
Check("a skill aimed only at the air never picks a ground target", () => {
    // hits = air 目前没有在役技能，用改过的配置保住这条路径的覆盖（与 pierce / hover_homing 同一做法）。
    var airOnly = GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_03", "hits", "air") : source[f]);
    var g = SalvoOn(airOnly, "skill_03", 200);         // 靶子全是青苔妖（地面）
    g.Effects.Clear(); g.Battle.Cooldowns.Clear();
    Step(g, 2);
    Assert(g.Battle.Enemies.All(e => e.Hp == e.MaxHp), "an air-only skill left the ground targets alone");
    Assert(g.Effects.Count == 0, "and it never even placed its field");
    Assert(g.Battle.Cooldowns.GetValueOrDefault("skill_03") == 0, "no legal target means no cooldown consumed");
});
Check("haste speeds up attack cooldowns but never buff cooldowns", () => {
    // 攻速若连增益类剑诀一起加速，仙风云体术（15s 冷却 / 6s 持续）会在持续期内转好，变成 100% 常驻。
    var g = Salvo("skill_04");                       // 仙风云体术：以自身为目标的增益，交战即可放
    Assert(g.HasteRemaining > 0 && Math.Abs(g.HasteFactor - 4) < 1e-9, "haste 300% means cooldowns tick 4x");
    g.Battle.Cooldowns.Clear();
    g.Battle.Cooldowns["skill_01"] = 10;              // 输出类剑诀：应被加速
    g.Battle.Cooldowns["skill_04"] = 10;              // 增益类剑诀：不该被加速
    g.Step(.05);
    Assert(Math.Abs(g.Battle.Cooldowns["skill_01"] - (10 - .2)) < 1e-9, $"attack cooldown ticked 4x: {g.Battle.Cooldowns["skill_01"]}");
    Assert(Math.Abs(g.Battle.Cooldowns["skill_04"] - (10 - .05)) < 1e-9, $"buff cooldown ticked 1x: {g.Battle.Cooldowns["skill_04"]}");
});
Check("a power-1 buff leaves the shared damage window alone", () => {
    // 万剑归心（power 1.5，5 秒）生效中再放仙风云体术（power 1，6 秒）：
    // 若纯功能向的增益也占用共享窗口，剩余时间会被顶成 6 秒。
    var g = Salvo("skill_14");                       // 靶子在身前 90，增益才有合法目标可放
    Assert(g.BuffRemaining == 5, $"damage buff window is 5s: {g.BuffRemaining}");
    g.State.Skills["skill_04"] = 1; g.Battle.Cooldowns.Clear();
    g.Step(.05);
    Assert(g.HasteRemaining > 0, "haste came up");
    Assert(g.BuffRemaining <= 5, $"the power-1 buff did not reset the damage window: {g.BuffRemaining}");
});
Check("crit_reduce raises the crit rate while it lasts", () => {
    // 固定 seed 下确定性可比：叠伤害合计，加成期间应明显高于基础暴击率。
    // 每轮清冷却 → 该轮必定出手；每轮跑 0.4 秒，够肩侧的三支剑飞到 200 处的靶子。
    // 清冷却顺带让醉仙望月步每轮重新上，保证整个统计窗口内加成都生效。
    double Harvest(GameSession s, EnemyState target, int rounds)
    {
        double total = 0;
        for (int i = 0; i < rounds; i++) { s.Battle.Cooldowns.Clear(); double before = target.Hp; Step(s, .4); total += before - target.Hp; }
        return total;
    }
    var plain = Salvo("skill_01", 200);
    var plainTarget = plain.Battle.Enemies[0];
    var blessed = Salvo("skill_01", 200);
    var blessedTarget = blessed.Battle.Enemies[0];
    blessed.State.Skills["skill_09"] = 1; blessed.Battle.Cooldowns.Clear();
    blessed.Step(.05);
    Assert(blessed.CritBonusRemaining > 0 && blessed.CritBonus > .29, "crit buff is up");
    double without = Harvest(plain, plainTarget, 60), with = Harvest(blessed, blessedTarget, 60);
    Assert(with > without * 1.05, $"crit bonus raised damage: {without:0} -> {with:0}");
});
Check("a crit shortens one cooling skill's cooldown", () => {
    // 两次跑同一个 seed，唯一差别是增益是否在生效：多余的那部分冷却缩减只能来自暴击抽签。
    GameSession Run(bool withBuff)
    {
        var s = Salvo("skill_01", 200);
        s.Battle.Enemies[0].Hp = s.Battle.Enemies[0].MaxHp = 1e9;
        s.State.Skills["skill_06"] = 1;                       // sink：习得但冷却很长，不会出手
        if (withBuff) s.State.Skills["skill_09"] = 1;         // 醉仙望月步
        s.Battle.Cooldowns.Clear();
        s.Battle.Cooldowns["skill_06"] = 30;
        s.Battle.Cooldowns["skill_09"] = 0;
        // 窗口要够长：缩冷却改成"每次施法最多一次"之后，触发密度降到 1/3（御剑术一轮 3 支只由第一支触发），
        // 3 秒里等不到暴击就会变成假失败。10 秒足够，增益自身 6 秒持续也会在这段时间里覆盖大部分。
        Step(s, 10);
        return s;
    }
    double with = Run(true).Battle.Cooldowns["skill_06"], without = Run(false).Battle.Cooldowns["skill_06"];
    Assert(with < without, $"crits shortened a cooling skill: {without:0.##} -> {with:0.##}");
});
Check("crits never shorten buff cooldowns, and only fire once per cast", () => {
    // 两条口径合起来看：一个增益冷却 + 一个非增益"沉槽"冷却都不出手，30 秒后自然衰减 30。
    // 增益必须正好剩 30（暴击碰不到它）；沉槽必须低于 30（暴击确实在缩短它，否则这条是空断言）。
    var g = Salvo("skill_01", 200);
    foreach (var id in new[] { "skill_04", "skill_06", "skill_09" }) g.State.Skills[id] = 1;
    g.State.Skills["skill_01"] = 1;
    g.Battle.Cooldowns.Clear();
    g.Battle.Cooldowns["skill_04"] = 60; g.Battle.Cooldowns["skill_06"] = 60;
    Step(g, 30);
    Assert(Math.Abs(g.Battle.Cooldowns["skill_04"] - 30) < 1e-6,
        $"a buff cooldown only decays naturally: {g.Battle.Cooldowns["skill_04"]:0.##}");
    Assert(g.Battle.Cooldowns["skill_06"] < 29, $"crits did shorten a non-buff cooldown: {g.Battle.Cooldowns["skill_06"]:0.##}");

    // 每次施法最多一次：把暴击率拉满，御剑术一轮 3 支，旧写法会一轮触发 3 次。
    var alwaysCrit = GameConfig.Load(f => f == "fightattr.csv" ? Cell(source[f], "crit", "base_value", "1") : source[f]);
    var b = SalvoOn(alwaysCrit, "skill_01", 200);
    b.State.Skills["skill_06"] = 1; b.State.Skills["skill_09"] = 1;
    b.Battle.Cooldowns.Clear();
    b.Battle.Cooldowns["skill_06"] = 300;      // 沉槽：冷却远长于观察窗口，不会被自然衰减清空
    double prev = 0; int volleys = 0;
    for (int i = 0; i < 600; i++)              // 30 秒
    {
        b.Step(.05);
        double now = b.Battle.Cooldowns.GetValueOrDefault("skill_01");
        if (now > prev + .5) volleys++;        // 冷却被顶回满值 = 出了一轮手
        prev = now;
    }
    double extra = (300 - 30) - b.Battle.Cooldowns["skill_06"];   // 超出自然衰减的部分全是缩冷却
    Assert(extra >= 5, $"crits did shorten the cooling skill: {extra:0.##}s");
    Assert(extra <= volleys / 2 + 2, $"at most one shortening per cast: {extra:0.##}s over {volleys} casts");
});
Check("homing blade flies out its remaining life when the target dies mid-flight", () => {
    var g = SalvoOn(hoverForm, "skill_01", 900);    // 放远些，飞行 0.45 秒时还在半路
    var blade = g.Effects.First(e => e.Trajectory == "hover_homing");
    Step(g, .45);                                   // 越过悬浮期，进入飞行
    g.Battle.Enemies.Clear();                       // 目标中途死亡
    Step(g, .1);
    Assert(g.Effects.Contains(blade) && blade.Life > 0, "blade survives instead of vanishing");
    Assert(blade.X > g.Battle.PlayerX, "keeps flying along its launch direction");
    Step(g, 3);
    Assert(!g.Effects.Contains(blade), "gone once its remaining life runs out");
});
Check("summon and pet bolts keep the default speed", () => {
    // speed 列只作用于剑诀：召唤弹与宠物弹走同一条默认路径，不能连带被配置改动（回归护栏）。
    Assert(new CombatEffect().Speed == 1500, "default bullet speed is unchanged");
    // summon 已无在役技能（化神档三个召唤随本轮替换退役），用内存改配置把一个剑诀改回召唤，保住这条护栏。
    // 改 kind 必须同时清掉天降形态并把弹数压回 1，否则过不了校验。
    var summoning = GameConfig.Load(f => f == "SwordSkill.csv"
        ? Cell(Cell(Cell(Cell(source[f], "skill_06", "kind", "summon"), "skill_06", "trajectory", ""),
            "skill_06", "projectile_count", "1"), "skill_06", "duration", "7")
        : source[f]);
    // 靶子必须放远：召唤物的锚点在玩家身前 110，贴脸摆的话弹丸生成时就已经贴着目标、同一步内命中并被移除。
    var g = SalvoOn(summoning, "skill_06", 900);    // 召唤物每秒发射一枚弹丸（索敌距离 1100）
    Step(g, 2.2);
    var bolt = g.Effects.FirstOrDefault(e => e.Kind == "projectile");
    Assert(bolt is not null && bolt.Speed == 1500,
        "summon-fired bolt keeps the default speed: " + string.Join(" | ", g.Effects.Select(e => $"{e.Kind}/life={e.Life:0.##}/spd={e.Speed}")));
});
Check("retired pierce / multi paths stay covered by config-driven effects", () => {
    // 两个次级效果已无在役技能使用（原御气飞剑/疾风剑），退休配置日后可能复活，故用改过的配置保住覆盖。
    // skill_02 现在是带天降形态与触发概率的真诀，要把它改成"普通的多发弹"必须一并清掉这两项，
    // 否则它既不会自动释放、也不会从玩家身前飞出，这条用例就变成空断言。
    var multi = GameConfig.Load(f => f == "SwordSkill.csv"
        ? Cell(Cell(Cell(Cell(Cell(source[f],
            "skill_02", "kind", "projectile"),
            "skill_02", "secondary", "multi"),
            "skill_02", "secondary_value", "3"),
            "skill_02", "trajectory", ""),
            "skill_02", "trigger_chance", "0")
        : source[f]);
    var g = new GameSession(multi, seed: 42) { BasicAttackEnabled = false }; g.Step(.05);
    // 靶子放远并定身：贴脸摆的话多发弹丸会在同一个 Step 内命中并被移除，数不到编排。
    foreach (var e in g.Battle.Enemies) { e.Atk = 0; e.Hp = e.MaxHp = 1e8; e.X = g.Battle.PlayerX + 160; e.StunUntil = 1e9; }
    g.State.Skills.Clear(); g.State.Skills["skill_02"] = 1; g.Battle.Cooldowns.Clear(); g.Effects.Clear();
    g.Step(.05);
    Assert(g.Effects.Count(e => e.Kind == "projectile" && !e.Hostile) >= 3, "multi still fires several projectiles");
    // pierce：直接构造一枚穿透弹，验证沿途每个敌人各挨一次；清空技能以保证伤害只可能来自它。
    var p = New(); p.Step(.05); p.State.Skills.Clear(); p.Battle.Cooldowns.Clear();
    var targets = p.Battle.Enemies.ToArray();
    for (int i = 0; i < targets.Length; i++) { targets[i].Atk = 0; targets[i].Hp = targets[i].MaxHp = 1e8; targets[i].X = p.Battle.PlayerX + 200 + i * 100; }
    p.Effects.Clear();
    p.Effects.Add(new() { Kind = "projectile", X = p.Battle.PlayerX, Damage = 1000, Life = 4, MaxLife = 4, Secondary = "pierce" });
    Step(p, .5);
    Assert(targets.All(e => e.Hp < e.MaxHp), "pierce hits every enemy along the line");
});
Check("retired shield / regen effects stay covered", () => {
    // 护心剑罡 / 归元护法 已搬进退役配置，shield / regen 当前无技能使用，故用改过的配置保住消费路径的覆盖。
    GameConfig BuffCfg(string secondary, string value) => GameConfig.Load(f => f == "SwordSkill.csv"
        ? Cell(Cell(Cell(Cell(source[f], "skill_04", "secondary", secondary), "skill_04", "secondary_value", value), "skill_04", "secondary_duration", "5"), "skill_04", "power", "1.3")
        : source[f]);
    // 护盾：厚盾扛住近战攻击，掉的是盾不是血。靶子解除定身并调成低伤害，便于观察。
    var guarded = SalvoOn(BuffCfg("shield", "100"), "skill_04");
    Assert(guarded.ShieldRemaining > 0, "the shield branch set a shield");
    foreach (var e in guarded.Battle.Enemies) { e.StunUntil = 0; e.Atk = 5; e.AttackTimer = .1; }
    double hp = guarded.Battle.PlayerHp;
    Step(guarded, 3);
    Assert(guarded.Battle.PlayerHp == hp, $"the shield absorbed the damage instead of hp: {guarded.Battle.PlayerHp:0}/{hp:0}");
    // 回血：气血被压低后靠增益回升（靶子伤害为 0，排除干扰）。
    var healing = SalvoOn(BuffCfg("regen", "0.05"), "skill_04");
    Assert(healing.RegenRemaining > 0, "the regen branch set a regen");
    healing.Battle.PlayerHp = healing.MaxHp * .5;
    double low = healing.Battle.PlayerHp;
    Step(healing, 1);
    Assert(healing.Battle.PlayerHp > low, $"regen restored hp: {low:0} -> {healing.Battle.PlayerHp:0}");
});
Check("GM grant covers every configured currency without touching the first-kill ledger", () => {
    var g = New();
    var ids = config.Rows("item").Where(r => r.Text("kind") == "currency").Select(r => r.Text("id")).ToList();
    Assert(ids.Count >= 2, "currency count");
    foreach (var id in ids) g.State.Wallet[id] = 0;
    Assert(g.GrantAllCurrencies(), "gm grant");
    Assert(ids.All(id => g.State.Amount(id) == 10000), "every currency");
    // 妖核来自 GM 而非首杀，故首杀账本必须保持为空，定量投放口径不被污染。
    Assert(g.State.FirstKills.Count == 0, "ledger untouched");
    g.GrantAllCurrencies(5); Assert(g.State.Amount("gold") == 10005, "accumulate");
    Assert(g.State.DebugGranted["core"] == 10005, "debug grant recorded");
    SaveStore.Validate(g.State, config); // 调试发放被校验承认，不再报「妖核数量与首杀账本不符」
    g.State.DebugGranted["core"] = 0; Reject(() => SaveStore.Validate(g.State, config)); // 抹掉记录后仍判为不一致
});
Check("legacy save with untracked GM cores is adopted instead of rejected", () => {
    var dir = Path.GetFullPath("artifacts/checks/" + Guid.NewGuid().ToString("N"));
    var path = Path.Combine(dir, "save.json"); var store = new SaveStore(path); var g = New();
    g.State.Wallet["core"] = 500; store.Save(g.State); // 旧版存档：有妖核余额、无调试记录、无首杀
    var state = store.Load(config)!;
    Assert(state.Amount("core") == 500 && state.DebugGranted["core"] == 500, "adopted as debug grant");
    store.Save(state); Assert(store.Load(config)!.Amount("core") == 500 && store.Warning is null, "stable after adoption");
});
Check("effects carry their source skill so the view can tell the 15 skills apart", () => {
    // 三个真诀只由普攻概率触发：用触发概率为 1 的改过配置跑，让"非增益剑诀一律留痕"这条断言是确定的，
    // 不必依赖 5%/4%/1% 在有限步数里摇中。
    var certain = GameConfig.Load(f => f == "SwordSkill.csv"
        ? Cell(Cell(Cell(source[f], "skill_02", "trigger_chance", "1"), "skill_07", "trigger_chance", "1"), "skill_12", "trigger_chance", "1")
        : source[f]);
    var g = new GameSession(certain, seed: 42) { BasicAttackEnabled = true };
    foreach (var realm in config.Rows("SwordLevel")) g.State.Realms.Add(realm.Text("id"));
    foreach (var id in certain.Skills.Keys) g.State.Skills[id] = 1;
    g.Step(.05); foreach (var e in g.Battle.Enemies) { e.Hp = e.MaxHp = 1e8; e.Atk = 0; }
    var fired = new HashSet<string>();
    for (int i = 0; i < 400; i++) { g.Step(.05); foreach (var effect in g.Effects) if (effect.Skill != "") fired.Add(effect.Skill); }
    // 3 个 buff 技能不产生飞行/地面效果（普攻的 Skill 为空，也在这里被排除），其余 12 个都该带着来源标记出手。
    Assert(fired.Count >= 12, $"distinct skills fired: {string.Join(",", fired.Order())}");
    Assert(fired.All(certain.Skills.ContainsKey), "skill ids resolve to SwordSkill rows");
    Assert(g.Effects.All(e => e.MaxLife > 0), "max life present for fading");
});
Check("three pet slots and unique buff categories", () => {
    var g = New(); g.State.Wallet["gold"] = 10000;
    foreach (var r in config.Rows("Pet")) { g.State.Pets.Add(r.Text("id")); g.TogglePet(r.Text("id")); }
    foreach (var r in config.Rows("PetEquip")) Assert(g.EquipPetBuff("pet_azure", r.Text("id")), "equip");
    Assert(g.State.EquippedPets.Count == 3 && !g.EquipPetBuff("pet_azure", "pet_edge"), "slots");
});
Check("atomic save, reload without offline gains, and corrupt-primary backup recovery", () => {
    var dir = Path.GetFullPath("artifacts/checks/" + Guid.NewGuid().ToString("N"));
    var path = Path.Combine(dir, "save.json"); var store = new SaveStore(path); var g = New();
    ToBoss(g); g.HurtEnemy(Boss(g), 1e9); g.ClickOre("ore_0"); store.Save(g.State); store.Save(g.State);
    var state = store.Load(config)!; Assert(state.Amount("core") == 1 && state.PendingIntent["intent_0"] == 1 && state.Battle.BossDefeated, "reload");
    var loaded = new GameSession(config, state); loaded.SelectLevel("level_001"); ToBoss(loaded); loaded.HurtEnemy(Boss(loaded), 1e9); Assert(loaded.State.Amount("core") == 1, "no duplicate reload");
    File.WriteAllText(path, "{broken"); Assert(store.Load(config)!.Amount("core") == 1 && store.Warning is not null, "backup recovery");
});
Check("base character can reach boss and obtain first core in a sustained run", () => {
    // 升级御剑术（初始技能，属默认已解锁的境界）。以前写死 skill_02，而它已随技能书序重排离开
    // 默认境界，UpgradeSkill 会因为境界未解锁而失败——用初始技能即可，也不必再跟着境界表改。
    var g = New(basic: true); g.BuyTalent("t_root"); g.UpgradeSkill("skill_01");
    for (int i = 0; i < 24000 && g.State.FirstKills.Count == 0; i++) g.Step(.05);
    Assert(g.State.FirstKills.Count > 0, $"stalled at {g.Battle.Cell + 1} hp={g.Battle.PlayerHp}");
});
Console.WriteLine($"ALL {passed} CHECKS PASSED");

// CSV roundtrip export is explicit and writes to a separate output directory.
int export = Array.IndexOf(args, "--export");
if (export >= 0 && export + 1 < args.Length)
{
    string output = Path.GetFullPath(args[export + 1]); Directory.CreateDirectory(output);
    foreach (string file in GameConfig.Files)
    {
        var header = source[file].TrimStart('\uFEFF').Split('\n')[0].TrimEnd('\r').Split(',');
        var records = new List<IEnumerable<string>> { header }; records.AddRange(config.Tables[file].Select(r => header.Select(r.Text)));
        File.WriteAllText(Path.Combine(output, file), CsvTable.Write(records), new System.Text.UTF8Encoding(false));
    }
    GameConfig.Load(file => File.ReadAllText(Path.Combine(output, file)));
    Console.WriteLine("CSV EXPORT VERIFIED " + output);
}
