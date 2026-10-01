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
    // 玩家增益（护盾/回血/吸血）：跨关卡与死亡清除，不随存档持久化。
    private double _shield, _shieldUntil, _regenUntil, _regenRate, _lifestealUntil, _lifestealFactor;
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
        TickPlayerBuffs(dt);
        ActivateCell();
        Moving = !Battle.Enemies.Any(e => e.Hp > 0 && e.Kind != "rift" && Math.Abs(e.X - Battle.PlayerX) <= Config.Attr("stop_range"));
        // 裂隙解锁后停步，但前提是已经打得到它。裂隙位于格内 1450+360 处，而 BOSS 格
        // 阵亡重生点在格首 80：两者相距 1730，远超普攻射程 1000。若解锁瞬间直接冻结，
        // 玩家会停在射程外空转，挂机永久中断（取决于最后一个非裂隙敌人倒下时玩家站在哪，
        // 故表现为「有概率」）。因此射程外解锁时必须继续沿格推进。
        if (Battle.Cell == Level.Cells - 1 && RiftUnlocked)
            Moving = !InRange(Battle.Enemies.FirstOrDefault(e => e.Hp > 0 && e.Kind == "rift"), Config.Setting("basic_range"));
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
    private void TickPlayerBuffs(double dt)
    {
        _shieldUntil = Math.Max(0, _shieldUntil - dt);
        _regenUntil = Math.Max(0, _regenUntil - dt);
        _lifestealUntil = Math.Max(0, _lifestealUntil - dt);
        if (_regenUntil > 0 && Battle.PlayerHp > 0) Battle.PlayerHp = Math.Min(MaxHp, Battle.PlayerHp + MaxHp * _regenRate * dt);
    }
    // 表现层读取的增益剩余时间：只读，不参与任何战斗判定，界面据此绘制护盾/回血/吸血/增益光环。
    public double BuffRemaining => _buffTime;
    public double ShieldRemaining => _shieldUntil;
    public double RegenRemaining => _regenUntil;
    public double LifestealRemaining => _lifestealUntil;
    /// <summary>清除玩家短时增益（伤害倍率/护盾/回血/吸血）。关卡切换、死亡重生与技能预览切换时调用。</summary>
    public void ClearBuffs() { _buffTime = 0; _shield = 0; _shieldUntil = 0; _regenUntil = 0; _regenRate = 0; _lifestealUntil = 0; _lifestealFactor = 0; }
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
            e.SlowUntil = Math.Max(0, e.SlowUntil - dt);
            e.StunUntil = Math.Max(0, e.StunUntil - dt);
            e.VulnerableUntil = Math.Max(0, e.VulnerableUntil - dt);
            if (e.StunUntil > 0) continue; // 眩晕：无法移动与攻击
            double distance = Math.Abs(e.X - Battle.PlayerX);
            double speed = m.Speed * (e.SlowUntil > 0 ? e.SlowFactor : 1);
            if (distance > m.Range)
                e.X += Math.Sign(Battle.PlayerX - e.X) * Math.Min(speed * dt, distance - m.Range);
            e.AttackTimer -= dt;
            if (Math.Abs(e.X - Battle.PlayerX) > m.Range + 1 || e.AttackTimer > 0) continue;
            e.AttackTimer = m.Interval;
            if (m.Attack == "melee") HurtPlayer(e.Atk);
            else Effects.Add(new() { Kind = m.Attack == "magic" ? "target" : "projectile", X = e.X, Life = m.Attack == "magic" ? .7 : 5, Damage = e.Atk, Hostile = true });
        }
        // 灼烧持续伤害：对非裂隙存活敌人按秒结算，不受眩晕影响。
        foreach (var e in Battle.Enemies.Where(e => e.Hp > 0 && e.Kind != "rift" && e.DotUntil > 0).ToArray())
        {
            e.DotUntil = Math.Max(0, e.DotUntil - dt);
            HurtEnemy(e, e.DotDps * dt);
        }
    }
    private bool Legal(EnemyState e) => e.Hp > 0 && (e.Kind != "rift" || RiftUnlocked);
    private bool InRange(EnemyState? e, double range) => e is not null && Math.Abs(e.X - Battle.PlayerX) <= range;
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
            var power = skill.Power * (1 + .15 * (rank - 1) + SkillBonus(id));
            if (skill.Kind == "buff")
            {
                // Buff 以自身为目标，仅在交战时触发，避免无敌人时浪费冷却。
                if (Target(skill.Range) is null) continue;
                CastBuff(skill, power);
            }
            else if (skill.Secondary == "multi")
            {
                var targets = Targets(skill.Range, (int)skill.SecondaryValue);
                if (targets.Count == 0) continue;
                foreach (var t in targets) LaunchSkill(skill.Kind, t, Attack * power, skill.Duration, skill);
            }
            else
            {
                var target = Target(skill.Range);
                if (target is null) continue;
                LaunchSkill(skill.Kind, target, Attack * power, skill.Duration, skill);
            }
            Battle.Cooldowns[id] = skill.Cooldown;
        }
        foreach (var pet in State.EquippedPets)
        {
            var row = Config.Row("PetSkill", Config.Row("Pet", pet).Text("skill_id"));
            var target = Target(row.Number("range"));
            if (target is null || !Ready(pet)) continue;
            var bonus = State.PetBuffs.GetValueOrDefault(pet, []).Sum(id => Config.Row("PetEquip", id).Number("power"));
            Launch(row.Text("kind"), target, Attack * row.Number("power") * (1 + bonus), .5, skill: row.Text("id"));
            Battle.Cooldowns[pet] = row.Number("cooldown");
        }
    }
    private bool Ready(string key) => Battle.Cooldowns.GetValueOrDefault(key) <= 0;
    private List<EnemyState> Targets(double range, int n) => Battle.Enemies.Where(e => Legal(e) && Math.Abs(e.X - Battle.PlayerX) <= range).OrderBy(e => Math.Abs(e.X - Battle.PlayerX)).Take(n).ToList();
    private void CastBuff(SkillDef skill, double power)
    {
        _buffPower = power; _buffTime = skill.Duration;
        Battle.PlayerHp = Math.Min(MaxHp, Battle.PlayerHp + MaxHp * .05);
        switch (skill.Secondary)
        {
            case "shield": _shield = Math.Max(_shield, Attack * skill.SecondaryValue); _shieldUntil = skill.SecondaryDuration; break;
            case "regen": _regenUntil = skill.SecondaryDuration; _regenRate = skill.SecondaryValue; break;
            case "lifesteal": _lifestealUntil = skill.SecondaryDuration; _lifestealFactor = skill.SecondaryValue; break;
        }
    }
    private void LaunchSkill(string kind, EnemyState target, double damage, double duration, SkillDef skill)
        => Launch(kind, target, damage, duration, true, skill.Secondary, skill.SecondaryValue, skill.SecondaryDuration, skill.Secondary == "execute" ? skill.SecondaryValue : 0, skill.AoeRadius, skill.Id);
    private void Launch(string kind, EnemyState target, double damage, double duration, bool applyModifiers = true,
        string secondary = "", double secondaryValue = 0, double secondaryDuration = 0, double executeThreshold = 0, double aoeRadius = 0, string skill = "")
    {
        if (applyModifiers && _buffTime > 0) damage *= _buffPower;
        if (applyModifiers && _random.NextDouble() < Config.Attr("crit")) damage *= Config.Attr("crit_damage");
        double life = kind == "projectile" ? 4 : duration;
        Effects.Add(new() { Kind = kind, X = kind is "ground" or "target" ? target.X : Battle.PlayerX,
            Target = target.Id, Damage = damage, Life = life, MaxLife = life, Skill = skill,
            Secondary = secondary, SecondaryValue = secondaryValue, SecondaryDuration = secondaryDuration,
            ExecuteThreshold = executeThreshold, AoeRadius = aoeRadius });
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
                    if (effect.Pierce)
                    {
                        // 穿透：沿前进方向飞行，命中沿途每个敌人一次（用扫过区间避免单帧跳过）。
                        double from = effect.X;
                        effect.X += 1500 * dt;
                        if (effect.X > Level.Cells * Config.Setting("cell_width")) { effect.Life = 0; break; }
                        foreach (var e in Battle.Enemies.Where(e => Legal(e) && !effect.Hit.Contains(e.Id) && e.X >= from - 30 && e.X <= effect.X + 30).ToArray())
                        { effect.Hit.Add(e.Id); Hit(e, effect.Damage, effect); }
                        break;
                    }
                    if (target is null) { effect.Life = 0; break; }
                    effect.X += Math.Sign(target.X - effect.X) * Math.Min(1500 * dt, Math.Abs(target.X - effect.X));
                    if (Math.Abs(effect.X - target.X) < 24) { Hit(target, effect.Damage, effect); effect.Life = 0; }
                    break;
                case "target":
                    if (effect.Life <= 0 && target is not null) Hit(target, effect.Damage, effect);
                    break;
                case "ground":
                    if (effect.Timer <= 0)
                    {
                        effect.Timer = .6;
                        double radius = effect.AoeRadius > 0 ? effect.AoeRadius : 220;
                        foreach (var e in Battle.Enemies.Where(e => Legal(e) && Math.Abs(e.X - effect.X) < radius).ToArray()) Hit(e, effect.Damage, effect);
                    }
                    break;
                case "summon":
                    effect.X = Battle.PlayerX + 110;
                    if (effect.Timer <= 0) { effect.Timer = 1; var next = Target(1100); if (next is not null) Launch("projectile", next, effect.Damage, 2, false, executeThreshold: effect.ExecuteThreshold, skill: effect.Skill); }
                    break;
            }
        }
        Effects.RemoveAll(e => e.Life <= 0);
    }
    // 命中结算：应用斩杀与吸血，再附带次级效果状态；易伤在 HurtEnemy 内统一处理。
    private void Hit(EnemyState e, double damage, CombatEffect fx)
    {
        if (!Legal(e)) return;
        if (fx.ExecuteThreshold > 0 && e.Hp / e.MaxHp < fx.ExecuteThreshold) damage *= 2; // 斩杀：低于阈值气血伤害翻倍（暂定）
        HurtEnemy(e, damage);
        if (damage > 0 && _lifestealUntil > 0) Battle.PlayerHp = Math.Min(MaxHp, Battle.PlayerHp + damage * _lifestealFactor); // 吸血
        ApplySecondary(e, fx);
    }
    private void ApplySecondary(EnemyState e, CombatEffect fx)
    {
        switch (fx.Secondary)
        {
            case "slow": e.SlowUntil = fx.SecondaryDuration; e.SlowFactor = 1 - fx.SecondaryValue; break;
            case "stun": e.StunUntil = fx.SecondaryDuration; break;
            case "dot": e.DotUntil = fx.SecondaryDuration; e.DotDps = fx.Damage * fx.SecondaryValue; break;
            case "vulnerable": e.VulnerableUntil = fx.SecondaryDuration; e.VulnerableFactor = 1 + fx.SecondaryValue; break;
        }
    }
    private void HurtPlayer(double amount)
    {
        if (Battle.RespawnTimer > 0 || _random.NextDouble() < Config.Attr("dodge")) return;
        if (_shieldUntil > 0 && _shield > 0) { double absorbed = Math.Min(_shield, amount); _shield -= absorbed; amount -= absorbed; if (_shield <= 0) _shieldUntil = 0; }
        Battle.PlayerHp = Math.Max(0, Battle.PlayerHp - amount);
    }
    internal void HurtEnemy(EnemyState e, double damage)
    {
        if (!Legal(e)) return;
        if (e.VulnerableUntil > 0) damage *= e.VulnerableFactor; // 易伤：目标受击伤害提高
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
        Effects.Clear(); ClearBuffs();
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
        Effects.Clear(); ClearBuffs();
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

    /// <summary>
    /// GM 调试入口：为 item.csv 中每一种货币（kind = currency）各增加固定数量。
    /// 遍历配置表而非硬编码名单，因此新增货币后无需改动此处即可自动纳入发放范围。
    /// 这是开发调试通道，不属于五大玩法系统，也不是任何奖励组的产出路径；
    /// 妖核的玩法产出口仍然只有「关卡首杀」一处，此方法不写入首杀账本（FirstKills），
    /// 而是把发放量记入 DebugGranted，使存档校验能区分调试产出与篡改。
    /// </summary>
    public bool GrantAllCurrencies(double amount = 10000)
    {
        var currencies = Config.Rows("item").Where(r => r.Text("kind") == "currency").ToList();
        if (currencies.Count == 0) return Say("配置中没有可发放的货币。");
        foreach (var r in currencies)
        {
            string id = r.Text("id");
            State.Wallet[id] = State.Amount(id) + amount;
            State.DebugGranted[id] = State.DebugGranted.GetValueOrDefault(id) + amount;
        }
        return Changed($"GM 调试 · {currencies.Count} 种货币各 +{amount:0}");
    }
}
