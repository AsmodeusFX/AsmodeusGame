using IdleSword.Core;
using IdleSword.Features;

string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("idle-sword/Config/Tables");
var source = GameConfig.Files.ToDictionary(f => f, f => File.ReadAllText(Path.Combine(root, f)));
var config = GameConfig.Load(f => source[f]);
int passed = 0;
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
void Check(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
GameSession New() => new(config, seed: 42);
void Step(GameSession g, double seconds) { for (int i = 0; i < (int)Math.Ceiling(seconds / .05); i++) g.Step(.05); }
void ToBoss(GameSession g)
{
    g.Battle.PlayerX = (g.Level.Cells - 1) * config.Setting("cell_width") + 80;
    g.Battle.Enemies.Clear(); g.Battle.Spawns.Clear(); g.Battle.PlayerHp = g.MaxHp; g.Step(.05);
}
EnemyState Boss(GameSession g) => g.Battle.Enemies.Single(e => e.Kind == "boss");
void Reject(Action test) { try { test(); } catch (InvalidDataException) { return; } throw new Exception("Expected validation rejection"); }

Check("all tables / 100 stages / 15 skills / 60 intent upgrades", () => Assert(config.Levels.Count == 100 && config.Skills.Count == 15 && config.Rows("SwordUpgrade").Count == 60, "table counts"));
Check("CSV BOM, quotes, multiline and write roundtrip", () => {
    var csv = CsvTable.Write(new[] { new[] { "id", "name" }, new[] { "1", "a,\"b\"\n中文" } });
    Assert(CsvTable.Parse("roundtrip", "\uFEFF" + csv)[0].Text("name") == "a,\"b\"\n中文", "roundtrip");
    Reject(() => CsvTable.Parse("bad", "id,name\n1,\"bad"));
});
Check("reject duplicate IDs and dangling references", () => {
    Reject(() => GameConfig.Load(f => f == "item.csv" ? source[f] + "gold,重复,currency,重复\n" : source[f]));
    Reject(() => GameConfig.Load(f => f == "wave.csv" ? source[f].Replace("wave_1,slime", "wave_1,missing") : source[f]));
});
Check("reject repeat core and non-deterministic first-core quantity", () => {
    Reject(() => GameConfig.Load(f => f == "drop.csv" ? source[f].Replace("boss_repeat,gold,60", "boss_repeat,core,1") : source[f]));
    Reject(() => GameConfig.Load(f => f == "drop.csv" ? source[f].Replace("boss_first,core,1", "boss_first,core,2") : source[f]));
});
Check("reject talent cycles", () => Reject(() => GameConfig.Load(f => f == "TalentLink.csv" ? source[f] + "cycle,t_end,t_root\n" : source[f])));
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
Check("all five effect types execute without invalid targets", () => {
    var g = New(); foreach (var realm in config.Rows("SwordLevel")) g.State.Realms.Add(realm.Text("id"));
    foreach (var id in config.Skills.Keys) g.State.Skills[id] = 1;
    g.Step(.05); foreach (var e in g.Battle.Enemies) { e.Hp = e.MaxHp = 1e8; e.Atk = 0; e.X = g.Battle.PlayerX + 200; }
    Step(g, .1); Assert(g.Effects.Any(e => e.Kind == "ground") && g.Effects.Any(e => e.Kind == "summon") && g.Effects.Any(e => e.Kind == "target"), "effect kinds");
    Step(g, 15); Assert(g.Battle.Enemies.Any(e => e.Hp < e.MaxHp), "effects damage");
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
    var g = New(); g.BuyTalent("t_root"); g.UpgradeSkill("skill_02");
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
