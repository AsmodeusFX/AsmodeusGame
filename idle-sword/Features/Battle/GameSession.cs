using IdleSword.Core;

namespace IdleSword.Features;

/// <summary>固定步长的纯 C# 游戏会话；Godot 仅负责输入、表现和持久化调度。</summary>
public sealed partial class GameSession
{
    public GameConfig Config { get; }
    public PlayerState State { get; }
    public BattleState Battle => State.Battle;
    public LevelDef Level => Config.Levels.Single(l => l.Id == Battle.LevelId);
    public List<CombatEffect> Effects { get; } = [];
    public event Action? PersistRequested;
    public string Message { get; private set; } = "踏入青岚，问道长生。";
    public bool Moving { get; private set; }
    public double Elapsed { get; private set; }
    private readonly Random _random;
    private bool _persist;
    private double _buffTime;
    private double _buffPower = 1;
    public bool RiftUnlocked => Battle.BossDefeated && !Battle.Enemies.Any(e => e.Hp > 0 && e.Kind != "rift");
    public double MaxHp => Config.Attr("hp") * (1 + Config.Attr("hp_percent") + TalentBonus("hp"));
    public double Attack => (Config.Attr("atk") + WeaponAttack) * (1 + Config.Attr("atk_percent") + TalentBonus("atk"));
    public double WeaponAttack => State.Weapon == "" ? 0 : Config.Row("Equip", State.Weapon).Number("base_atk") * (1 + .15 * State.WeaponLevel) * State.WeaponRoll;

    public GameSession(GameConfig config, PlayerState? state = null, int? seed = null)
    {
        Config = config; State = state ?? new(); _random = seed is null ? new Random() : new Random(seed.Value);
        if (state is null)
        {
            State.Wallet["gold"] = config.Setting("starting_gold");
            State.UnlockedLevels.Add(config.Levels[0].Id);
            foreach (var r in config.Rows("SwordLevel").Where(r => r.Flag("default_unlocked"))) State.Realms.Add(r.Text("id"));
            var starter = config.Skills.Values.First(s => State.Realms.Contains(s.Realm));
            State.Skills[starter.Id] = 1;
            EnterLevel(config.Levels[0].Id);
        }
    }

    public void Step(double dt)
    {
        if (dt <= 0 || !double.IsFinite(dt)) return;
        Elapsed += dt;
        TickIntent(dt);
        if (Battle.RespawnTimer > 0)
        {
            Battle.RespawnTimer = Math.Max(0, Battle.RespawnTimer - dt);
            if (Battle.RespawnTimer == 0) { Battle.PlayerHp = MaxHp; _persist = true; }
            FinishStep(); return;
        }
        foreach (var key in Battle.Cooldowns.Keys.ToArray()) Battle.Cooldowns[key] = Math.Max(0, Battle.Cooldowns[key] - dt);
        _buffTime = Math.Max(0, _buffTime - dt);
        ActivateCell();
        Moving = !Battle.Enemies.Any(e => e.Hp > 0 && e.Kind != "rift" && Math.Abs(e.X - Battle.PlayerX) <= Config.Attr("stop_range"));
        if (Battle.Cell == Level.Cells - 1 && RiftUnlocked) Moving = false;
        if (Moving)
        {
            Battle.PlayerX = Math.Min((Level.Cells - 1) * Config.Setting("cell_width") + SpawnOffset - 300,
                Battle.PlayerX + Config.Attr("move_speed") * dt);
            ActivateCell();
        }
        TickSpawns(dt);
        TickEnemies(dt);
        CastSkills();
        TickEffects(dt);
        Battle.Enemies.RemoveAll(e => e.Hp <= 0);
        // 同帧击杀与死亡：奖励已结算；优先执行复活，传送留到存活后。
        if (Battle.PlayerHp <= 0) Die();
        else if (Battle.PortalDestroyed && RiftUnlocked) CompleteLevel();
        FinishStep();
    }

    private void FinishStep() { if (_persist) { _persist = false; PersistRequested?.Invoke(); } }
    private double SpawnOffset => Config.Rows("spawn_point")[0].Number("offset");
    private void ActivateCell()
    {
        Battle.Cell = Math.Min(Level.Cells - 1, (int)(Battle.PlayerX / Config.Setting("cell_width")));
        if (Battle.Spawns.ContainsKey(Battle.Cell)) return;
        Battle.Spawns[Battle.Cell] = new();
        if (Battle.Cell == Level.Cells - 1)
        {
            Spawn(Level.Boss, Battle.Cell, 130);
            Spawn(Level.Rift, Battle.Cell, 360);
            Message = "妖王现身。击败群妖，方可破开裂隙。";
        }
    }
    private void TickSpawns(double dt)
    {
        var wave = Config.Waves[Level.Wave];
        foreach (var (cell, spawn) in Battle.Spawns)
        {
            if (Battle.PlayerX > cell * Config.Setting("cell_width") + SpawnOffset) spawn.Passed = true;
            if (spawn.Passed || Battle.BossDefeated) continue;
            spawn.Timer -= dt;
            if (spawn.Timer > 0) continue;
            spawn.Timer += wave.Interval; spawn.Wave++;
            for (int i = 0; i < wave.Count; i++) Spawn(wave.Monster, cell, 40 + i * 100);
            if (spawn.Wave % wave.EliteEvery == 0) Spawn(wave.Elite, cell, 320);
        }
    }
    private void Spawn(string monsterId, int cell, double offset)
    {
        var m = Config.Monsters[monsterId];
        var (hp, atk) = m.Kind switch { "boss" => (Level.BossHp, Level.BossAtk), "elite" => (Level.EliteHp, Level.EliteAtk), "rift" => (Level.RiftHp, 0d), _ => (Level.HpScale, Level.AtkScale) };
        Battle.Enemies.Add(new() { Id = Battle.NextEnemyId++, MonsterId = m.Id, Kind = m.Kind,
            X = cell * Config.Setting("cell_width") + SpawnOffset + offset,
            Hp = m.Hp * hp, MaxHp = m.Hp * hp, Atk = m.Atk * atk, AttackTimer = m.Interval });
    }
    private void TickEnemies(double dt)
    {
        foreach (var e in Battle.Enemies.Where(e => e.Hp > 0 && e.Kind != "rift"))
        {
            var m = Config.Monsters[e.MonsterId];
            double distance = Math.Abs(e.X - Battle.PlayerX);
            if (distance > m.Range)
                e.X += Math.Sign(Battle.PlayerX - e.X) * Math.Min(m.Speed * dt, distance - m.Range);
            e.AttackTimer -= dt;
            if (Math.Abs(e.X - Battle.PlayerX) > m.Range + 1 || e.AttackTimer > 0) continue;
            e.AttackTimer = m.Interval;
            if (m.Attack == "melee") HurtPlayer(e.Atk);
            else Effects.Add(new() { Kind = m.Attack == "magic" ? "target" : "projectile", X = e.X, Life = m.Attack == "magic" ? .7 : 5, Damage = e.Atk, Hostile = true });
        }
    }
    private bool Legal(EnemyState e) => e.Hp > 0 && (e.Kind != "rift" || RiftUnlocked);
    private EnemyState? Target(double range) => Battle.Enemies.Where(e => Legal(e) && Math.Abs(e.X - Battle.PlayerX) <= range).OrderBy(e => Math.Abs(e.X - Battle.PlayerX)).FirstOrDefault();

    private void CastSkills()
    {
        if (Battle.PlayerHp <= 0) return;
        var basic = Target(Config.Setting("basic_range"));
        if (basic is not null && Ready("basic"))
        {
            Launch("projectile", basic, Attack, 2);
            Battle.Cooldowns["basic"] = Config.Setting("basic_cooldown");
        }
        foreach (var (id, rank) in State.Skills)
        {
            if (rank <= 0 || !Ready(id)) continue;
            var skill = Config.Skills[id];
            var target = Target(skill.Range);
            // Buff 合法目标是自己，但仅在交战时触发，避免无敌人时浪费冷却。
            if (target is null) continue;
            var power = skill.Power * (1 + .15 * (rank - 1) + SkillBonus(id));
            if (skill.Kind == "buff") { _buffPower = power; _buffTime = skill.Duration; Battle.PlayerHp = Math.Min(MaxHp, Battle.PlayerHp + MaxHp * .05); }
            else Launch(skill.Kind, target, Attack * power, skill.Duration);
            Battle.Cooldowns[id] = skill.Cooldown;
        }
        foreach (var pet in State.EquippedPets)
        {
            var row = Config.Row("PetSkill", Config.Row("Pet", pet).Text("skill_id"));
            var target = Target(row.Number("range"));
            if (target is null || !Ready(pet)) continue;
            var bonus = State.PetBuffs.GetValueOrDefault(pet, []).Sum(id => Config.Row("PetEquip", id).Number("power"));
            Launch(row.Text("kind"), target, Attack * row.Number("power") * (1 + bonus), .5);
            Battle.Cooldowns[pet] = row.Number("cooldown");
        }
    }
    private bool Ready(string key) => Battle.Cooldowns.GetValueOrDefault(key) <= 0;
    private void Launch(string kind, EnemyState target, double damage, double duration, bool applyModifiers = true)
    {
        if (applyModifiers && _buffTime > 0) damage *= _buffPower;
        if (applyModifiers && _random.NextDouble() < Config.Attr("crit")) damage *= Config.Attr("crit_damage");
        Effects.Add(new() { Kind = kind, X = kind is "ground" or "target" ? target.X : Battle.PlayerX,
            Target = target.Id, Damage = damage, Life = kind == "projectile" ? 4 : duration });
    }
    private void TickEffects(double dt)
    {
        foreach (var effect in Effects.ToArray())
        {
            effect.Life -= dt; effect.Timer -= dt;
            if (effect.Hostile)
            {
                if (effect.Kind == "projectile") effect.X += Math.Sign(Battle.PlayerX - effect.X) * Math.Min(900 * dt, Math.Abs(Battle.PlayerX - effect.X));
                if ((effect.Kind == "projectile" && Math.Abs(effect.X - Battle.PlayerX) < 20) || (effect.Kind == "target" && effect.Life <= 0))
                { HurtPlayer(effect.Damage); effect.Life = 0; }
                continue;
            }
            var target = Battle.Enemies.FirstOrDefault(e => e.Id == effect.Target && Legal(e));
            switch (effect.Kind)
            {
                case "projectile":
                    if (target is null) { effect.Life = 0; break; }
                    effect.X += Math.Sign(target.X - effect.X) * Math.Min(1500 * dt, Math.Abs(target.X - effect.X));
                    if (Math.Abs(effect.X - target.X) < 24) { HurtEnemy(target, effect.Damage); effect.Life = 0; }
                    break;
                case "target":
                    if (effect.Life <= 0 && target is not null) HurtEnemy(target, effect.Damage);
                    break;
                case "ground":
                    if (effect.Timer <= 0) { effect.Timer = .6; foreach (var e in Battle.Enemies.Where(e => Legal(e) && Math.Abs(e.X - effect.X) < 220).ToArray()) HurtEnemy(e, effect.Damage); }
                    break;
                case "summon":
                    effect.X = Battle.PlayerX + 110;
                    if (effect.Timer <= 0) { effect.Timer = 1; var next = Target(1100); if (next is not null) Launch("projectile", next, effect.Damage, 2, false); }
                    break;
            }
        }
        Effects.RemoveAll(e => e.Life <= 0);
    }
    private void HurtPlayer(double amount)
    {
        if (Battle.RespawnTimer > 0 || _random.NextDouble() < Config.Attr("dodge")) return;
        Battle.PlayerHp = Math.Max(0, Battle.PlayerHp - amount);
    }
    internal void HurtEnemy(EnemyState e, double damage)
    {
        if (!Legal(e)) return;
        e.Hp = Math.Max(0, e.Hp - damage);
        if (e.Hp > 0) return;
        AddCurrency("gold", Config.Monsters[e.MonsterId].Gold);
        if (e.Kind == "boss")
        {
            Battle.BossDefeated = true;
            bool first = State.FirstKills.Add(Level.Id);
            foreach (var reward in Config.Rows("drop").Where(r => r.Text("group_id") == (first ? Level.FirstReward : Level.RepeatReward)))
            {
                if (reward.Text("item_id") != "core") AddCurrency(reward.Text("item_id"), reward.Number("amount"));
            }
            // 妖核只在这一处入账，账本与余额随整个快照原子保存。
            if (first) State.Wallet["core"] = State.Amount("core") + 1;
            Message = first ? "妖王伏诛 · 首杀妖核 +1！" : "妖王伏诛 · 已领取过本关妖核。";
            _persist = true;
        }
        if (e.Kind == "rift") { Battle.PortalDestroyed = true; _persist = true; }
    }
    private void Die()
    {
        bool bossCell = Battle.Cell == Level.Cells - 1;
        Effects.Clear(); _buffTime = 0;
        if (!bossCell) EnterLevel(Level.Id);
        else Battle.PlayerX = (Level.Cells - 1) * Config.Setting("cell_width") + 80;
        Battle.PlayerHp = 0;
        Battle.RespawnTimer = Config.Setting("respawn_seconds");
        Moving = false; _persist = true;
        Message = bossCell ? "气血耗尽 · 本格重生，敌人伤势保留。" : "气血耗尽 · 返回本关起点，资源全部保留。";
    }
    private void CompleteLevel()
    {
        int index = Config.Levels.FindIndex(l => l.Id == Level.Id);
        if (index + 1 < Config.Levels.Count) State.UnlockedLevels.Add(Config.Levels[index + 1].Id);
        var next = !State.LoopLevel && index + 1 < Config.Levels.Count ? Config.Levels[index + 1] : Level;
        EnterLevel(next.Id); Message = "裂隙已破 · 抵达 " + next.Name; _persist = true;
    }
    private void EnterLevel(string id)
    {
        State.Battle = new() { LevelId = id, PlayerHp = MaxHp, PlayerX = 80 };
        Effects.Clear(); _buffTime = 0;
    }
    public bool SelectLevel(string id)
    {
        if (!State.UnlockedLevels.Contains(id)) return Say("尚未解锁此关。");
        State.LoopLevel = true; EnterLevel(id); return Changed("已切换为整关循环。");
    }
    public void ToggleLoop() { State.LoopLevel = !State.LoopLevel; Changed(State.LoopLevel ? "正在循环本关。" : "已开启自动推进。"); }
    private bool Say(string message) { Message = message; return false; }
    private bool Changed(string message) { Message = message; PersistRequested?.Invoke(); return true; }
    private void AddCurrency(string id, double amount)
    {
        if (id == "core") throw new InvalidOperationException("妖核只能由关卡首杀发放");
        State.Wallet[id] = State.Amount(id) + amount;
    }
}
