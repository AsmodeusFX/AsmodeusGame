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
    // 攻速与暴击增益：各自持有计时器与参数，互不覆盖（_buffTime/_buffPower 是共享的伤害倍率，与之无关）。
    private double _hasteUntil, _hasteFactor = 1;
    private double _critBonusUntil, _critBonus, _critReduceUntil, _critReduce;
    public bool RiftUnlocked => Battle.BossDefeated && !Battle.Enemies.Any(e => e.Hp > 0 && e.Kind != "rift");
    public double MaxHp => Config.Attr("hp") * (1 + Config.Attr("hp_percent") + TalentBonus("hp"));
    public double Attack => (Config.Attr("atk") + WeaponAttack) * (1 + Config.Attr("atk_percent") + TalentBonus("atk"));
    public double WeaponAttack => State.Weapon == "" ? 0 : Config.Row("Equip", State.Weapon).Number("base_atk") * (1 + .15 * State.WeaponLevel) * State.WeaponRoll;
    /// <summary>角色能打到的最大距离：取已习得剑诀的最大射程（不含 buff 类，它们不造成伤害）。
    /// 已移除普攻概念，接近裂隙的停步判定以剑诀射程为准（原来用的 basic_range 已删除）。
    /// 正常流程下必有御剑术（境界 1 首个剑诀由构造器解锁），故不会出现 0 射程导致走到关底空转。</summary>
    public double AttackRange => State.Skills.Where(kv => kv.Value > 0 && Config.Skills.TryGetValue(kv.Key, out var s) && s.Kind != "buff")
        .Select(kv => Config.Skills[kv.Key].Range).DefaultIfEmpty(0).Max();

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
        // 冷却流逝（唯一的衰减点）：攻速按倍数加速，但**不加速增益类剑诀**。
        // 若连增益一起加速，仙风云体术（15s 冷却 / 6s 持续）会在持续期内就转好，等于自己给自己减冷却，
        // 变成 100% 常驻；两个 15s 增益还会互相锁死。剑灵的键不在 Config.Skills 里，照样加速。
        foreach (var key in Battle.Cooldowns.Keys.ToArray())
        {
            double factor = HasteFactor > 1 && Config.Skills.TryGetValue(key, out var hastened) && hastened.Kind == "buff" ? 1 : HasteFactor;
            Battle.Cooldowns[key] = Math.Max(0, Battle.Cooldowns[key] - dt * factor);
        }
        _buffTime = Math.Max(0, _buffTime - dt);
        TickPlayerBuffs(dt);
        ActivateCell();
        Moving = !Battle.Enemies.Any(e => e.Hp > 0 && e.Kind != "rift" && Math.Abs(e.X - Battle.PlayerX) <= Config.Attr("stop_range"));
        // 裂隙解锁后停步，但前提是已经打得到它。裂隙位于格内 1450+360 处，而 BOSS 格
        // 阵亡重生点在格首 80：两者相距 1730，远超任一剑诀射程（950）。若解锁瞬间直接冻结，
        // 玩家会停在射程外空转，挂机永久中断（取决于最后一个非裂隙敌人倒下时玩家站在哪，
        // 故表现为「有概率」）。因此射程外解锁时必须继续沿格推进。
        if (Battle.Cell == Level.Cells - 1 && RiftUnlocked)
            Moving = !InRange(Battle.Enemies.FirstOrDefault(e => e.Hp > 0 && e.Kind == "rift"), AttackRange);
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
        _hasteUntil = Math.Max(0, _hasteUntil - dt);
        _critBonusUntil = Math.Max(0, _critBonusUntil - dt);
        _critReduceUntil = Math.Max(0, _critReduceUntil - dt);
        if (_hasteUntil == 0) { _hasteFactor = 1; }
        if (_critBonusUntil == 0) { _critBonus = 0; _critReduce = 0; }
        if (_regenUntil > 0 && Battle.PlayerHp > 0) Battle.PlayerHp = Math.Min(MaxHp, Battle.PlayerHp + MaxHp * _regenRate * dt);
    }
    // 表现层读取的增益剩余时间：只读，不参与任何战斗判定，界面据此绘制各增益光环。
    public double BuffRemaining => _buffTime;
    public double ShieldRemaining => _shieldUntil;
    public double RegenRemaining => _regenUntil;
    public double LifestealRemaining => _lifestealUntil;
    public double HasteRemaining => _hasteUntil;
    public double CritBonusRemaining => _critBonusUntil;
    /// <summary>冷却流逝倍数：攻速增益生效时为 1 + secondary_value，否则 1。只有输出类剑诀与剑灵吃这个倍数（见 Step）。</summary>
    public double HasteFactor => _hasteUntil > 0 ? _hasteFactor : 1;
    /// <summary>暴击率加成（绝对值）：醉仙望月步生效期间提高，直接叠在配置的基础暴击率上。</summary>
    public double CritBonus => _critBonusUntil > 0 ? _critBonus : 0;
    /// <summary>清除玩家短时增益（伤害倍率/护盾/回血/吸血/攻速/暴击）。关卡切换、死亡重生与技能预览切换时调用。</summary>
    public void ClearBuffs()
    {
        _buffTime = 0; _shield = 0; _shieldUntil = 0; _regenUntil = 0; _regenRate = 0; _lifestealUntil = 0; _lifestealFactor = 0;
        _hasteUntil = 0; _hasteFactor = 1; _critBonusUntil = 0; _critBonus = 0; _critReduceUntil = 0; _critReduce = 0;
    }
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
                // 历史次级效果：当前已无技能使用（原疾风剑专用），退休配置日后可能复活，勿删。
                var targets = Targets(skill.Range, (int)skill.SecondaryValue);
                if (targets.Count == 0) continue;
                for (int i = 0; i < targets.Count; i++) LaunchSkill(skill, targets[i], Attack * power, i);   // 序号照传，暴击缩冷却才不会被多发放大
            }
            else if (!CastVolley(skill, Attack * power)) continue;   // 没有合法目标时不空放、也不消耗冷却
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
    /// <summary>
    /// 弹群编排：数量、落点/排列间距与错时都在这里决定，返回是否真的出手了（false 表示没有合法目标）。
    /// </summary>
    private bool CastVolley(SkillDef skill, double damage)
    {
        int count = skill.Kind == "projectile" ? skill.ProjectileCount : 1;
        if (skill.Trajectory == "sky_drop")
        {
            // 固定剑阵：阵心取最近的合法敌人，各支按 spread 在阵心两侧铺开，**与敌人数无关**——
            // 只有一个敌人时不会缩成一束。落点由 Core 算进 X，因为它直接参与落地判定。
            var center = Target(skill.Range);
            if (center is null) return false;
            for (int i = 0; i < count; i++) LaunchSkill(skill, center, damage, i, (i - (count - 1) / 2.0) * skill.Spread);
            return true;
        }
        // 其余形态各自选敌、尽量不重复（敌人不足才循环重复）。
        var picks = TargetsWithRepeats(skill.Range, count);
        if (picks.Count == 0) return false;
        for (int i = 0; i < picks.Count; i++) LaunchSkill(skill, picks[i], damage, i);
        return true;
    }
    // ThenBy(Id) 只为同 X 多敌时排序确定：自检常把多个敌人重叠放到同一坐标。
    private List<EnemyState> Targets(double range, int n) => Battle.Enemies.Where(e => Legal(e) && Math.Abs(e.X - Battle.PlayerX) <= range).OrderBy(e => Math.Abs(e.X - Battle.PlayerX)).ThenBy(e => e.Id).Take(n).ToList();
    /// <summary>多发弹群选敌：先取最近的 n 个不同敌人（各自选敌、尽量不重复）；敌人不足时才按序循环重复打同一个。</summary>
    private List<EnemyState> TargetsWithRepeats(double range, int n)
    {
        var distinct = Targets(range, n);
        if (distinct.Count == 0) return [];
        var picks = new List<EnemyState>(n);
        for (int i = 0; i < n; i++) picks.Add(distinct[i % distinct.Count]);
        return picks;
    }
    private void CastBuff(SkillDef skill, double power)
    {
        // 伤害倍率只在配置里 power != 1 时占用：纯功能向的增益（仙风云体术/醉仙望月步）不该把正在
        // 生效的伤害倍率重置掉。判断用配置的基础 power 而不是算上等级与剑意之后的 power——
        // 否则技能一升级，倍率就会被激活，等于偷偷取消了这条规则。
        if (skill.Power != 1) { _buffPower = power; _buffTime = skill.Duration; }
        Battle.PlayerHp = Math.Min(MaxHp, Battle.PlayerHp + MaxHp * .05);
        switch (skill.Secondary)
        {
            case "shield": _shield = Math.Max(_shield, Attack * skill.SecondaryValue); _shieldUntil = skill.SecondaryDuration; break;
            case "regen": _regenUntil = skill.SecondaryDuration; _regenRate = skill.SecondaryValue; break;
            case "lifesteal": _lifestealUntil = skill.SecondaryDuration; _lifestealFactor = skill.SecondaryValue; break;
            case "haste": _hasteFactor = 1 + skill.SecondaryValue; _hasteUntil = skill.SecondaryDuration; break;
            case "crit_reduce": _critBonus = skill.SecondaryValue; _critBonusUntil = skill.SecondaryDuration; _critReduce = skill.SecondaryExtra; _critReduceUntil = skill.SecondaryDuration; break;
        }
    }
    /// <summary>
    /// 暴击后的"缩短一个随机技能的冷却"：只从已习得且**当前冷却 &gt; 0** 的非增益技能里抽
    /// （抽到冷却为 0 的技能等于白给；抽到增益会让攻速覆盖率自涨，见下）。没有候选就什么都不做。
    /// </summary>
    private void CritShortenCooldown()
    {
        if (_critReduceUntil <= 0 || _critReduce <= 0) return;
        // 候选里排除增益类剑诀：否则暴击会不断给仙风云体术减冷却，它的覆盖率从标称的 6/15=40%
        // 实测涨到 50%+，形成"暴击 → 增益来得更勤 → 出手更快 → 更多暴击"的正反馈。
        // 与"攻速不加速增益类剑诀的冷却"是同一条口径：增益之间的冷却不该互相喂。
        var cooling = State.Skills.Where(kv => kv.Value > 0 && Battle.Cooldowns.GetValueOrDefault(kv.Key) > 0
                && Config.Skills.TryGetValue(kv.Key, out var s) && s.Kind != "buff")
            .Select(kv => kv.Key).ToArray();
        if (cooling.Length == 0) return;
        string pick = cooling[_random.Next(cooling.Length)];
        Battle.Cooldowns[pick] = Math.Max(0, Battle.Cooldowns[pick] - _critReduce);
    }
    private void LaunchSkill(SkillDef skill, EnemyState target, double damage, int index, double lane = 0)
    {
        // 随机量在这里一次摇定，表现层只读结果：逐帧重摇会让弧线/高度抖动，也让自检无法复现。
        // 弧度只有弧线形态抽；Jitter 是 0..1 的通用抖动，由表现层按形态解释（天降形态拿它做出生高度）。
        double arc = skill.Trajectory == "arc_homing"
            ? skill.ArcMin + _random.NextDouble() * (skill.ArcMax - skill.ArcMin) : 0;
        double jitter = _random.NextDouble();
        // 弹群错时：第 i 支额外延迟 i × interval，再叠加 ±jitter 的随机；与 hover_time 一起折算成"起飞前停留"。
        double delay = Math.Max(0, index * skill.VolleyInterval + (_random.NextDouble() * 2 - 1) * skill.VolleyJitter);
        Launch(skill.Kind, target, damage, skill.Duration, true, skill.Secondary, skill.SecondaryValue,
            skill.SecondaryDuration, skill.Secondary == "execute" ? skill.SecondaryValue : 0, skill.AoeRadius, skill.Id,
            skill.Trajectory, index, skill.HoverTime + delay, arc, skill.Speed > 0 ? skill.Speed : 1500, lane, jitter,
            skill.PierceChance);
    }
    private void Launch(string kind, EnemyState target, double damage, double duration, bool applyModifiers = true,
        string secondary = "", double secondaryValue = 0, double secondaryDuration = 0, double executeThreshold = 0, double aoeRadius = 0, string skill = "",
        string trajectory = "", int index = 0, double hold = 0, double arc = 0, double speed = 1500, double lane = 0, double jitter = 0,
        double pierceChance = 0)
    {
        if (applyModifiers && _buffTime > 0) damage *= _buffPower;
        // 暴击判定全游戏只有这一处，逐弹丸各摇一次（召唤弹 applyModifiers=false 不参与）。
        // 抽签消耗 _random，会让后续随机序列偏移（同 seed 仍可复现）。
        if (applyModifiers && _random.NextDouble() < Config.Attr("crit") + CritBonus)
        {
            damage *= Config.Attr("crit_damage");
            // "暴击缩短一个随机技能的冷却"按**每次施法**最多触发一次：多发齐射是 3～5 支各摇一次暴击的，
            // 若每支都触发，实际触发密度会放大数倍（实测能把增益冷却吃到覆盖率自涨）。
            // 由该次施法的第一支负责，单发/剑灵弹丸的 index 本就是 0。
            if (index == 0) CritShortenCooldown();
        }
        // 生命期分两种：普通弹道沿用硬编码 4 秒——召唤弹传 2、剑灵弹只传 0.5，
        // 若把 duration 当通用寿命会缩短剑灵弹丸、使其飞不到目标；自定义飞行形态才用 duration（本列对该形态即飞行/下坠时长）。
        // 自定义形态还要加上起飞前停留（hold）：停留与飞行各自计时，落地/命中的时刻才会随错时而变化。
        bool customFlight = trajectory is "hover_homing" or "sky_drop" or "arc_homing" or "line_pierce" or "line_shot";
        double life = kind != "projectile" ? duration
            : customFlight ? duration + hold
            : 4;
        // 起始 X：天降形态生成在目标上空（lane 是剑阵里的落点偏移，落点由 Core 定、不交给表现层），此后冻结；
        // 其余弹道从玩家处出发。
        double startX = kind is "ground" or "target" || trajectory == "sky_drop" ? target.X + lane : Battle.PlayerX;
        Effects.Add(new() { Kind = kind, X = startX, Target = target.Id, Damage = damage, Life = life, MaxLife = life,
            // 自定义形态复用 Timer 作"起飞前停留"倒计时：TickEffects 每步已经 Timer -= dt，停留期只读 Timer > 0。
            Timer = customFlight ? hold : 0,
            Skill = skill, Secondary = secondary, SecondaryValue = secondaryValue, SecondaryDuration = secondaryDuration,
            ExecuteThreshold = executeThreshold, AoeRadius = aoeRadius, Trajectory = trajectory, Index = index, Arc = arc,
            Speed = speed, Jitter = jitter, PierceChance = pierceChance, Dir = Math.Sign(target.X - Battle.PlayerX) });
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
                    // 起飞前停留（悬浮蓄势 / 浮现 / 剑阵各支的错时）：所有自定义形态共用同一个倒计时，
                    // 所以 bolt 以外的形态不必各自实现时序。bolt 与退役的 secondary == "pierce" 传 0，行为不变。
                    if (effect.Timer > 0) break;
                    // 自定义飞行形态。bolt（Trajectory 为空）不走这里，下面两条旧路径原样保留。
                    if (effect.Trajectory == "sky_drop")
                    {
                        // 垂直落下：X 在发射时已冻结（不追踪，"击中谁是谁"），落地时对落点半径内所有敌人结算，可命中重叠单位。
                        if (effect.Life > 0) break;
                        double radius = effect.AoeRadius; // 校验保证 > 0，绝不回退 ground 的 220
                        foreach (var e in Battle.Enemies.Where(e => Legal(e) && Math.Abs(e.X - effect.X) < radius).ToArray())
                            Hit(e, effect.Damage, effect);
                        break;
                    }
                    if (effect.Trajectory == "line_shot")
                    {
                        // 平射、不追踪：命中路径上最先遇到的那个敌人即结算并销毁（与 line_pierce 的恒穿透区分开）。
                        // 用扫过区间拾取，避免一帧跨过多个身位时漏判；只取最靠前的一个，不是全部。
                        double from = effect.X;
                        effect.X += effect.Speed * dt;
                        if (effect.X > Level.Cells * Config.Setting("cell_width")) { effect.Life = 0; break; }
                        var victim = Battle.Enemies.Where(e => Legal(e) && !effect.Hit.Contains(e.Id) && e.X >= from - 30 && e.X <= effect.X + 30)
                            .OrderBy(e => e.X).ThenBy(e => e.Id).FirstOrDefault();
                        if (victim is null) break;
                        effect.Hit.Add(victim.Id);
                        Hit(victim, effect.Damage, effect);
                        // 概率穿透只有第一次击中时判定一次：没穿就销毁，穿了也标记下来，之后命中一律销毁。
                        if (!effect.Pierced && effect.PierceChance > 0 && _random.NextDouble() < effect.PierceChance) effect.Pierced = true;
                        else effect.Life = 0;
                        break;
                    }
                    if (effect.Trajectory is "hover_homing" or "arc_homing")
                    {
                        if (target is null)
                        {
                            // 目标已死：按既定规则不改追别人，沿发射方向把剩余时间飞完，不再造成伤害。
                            effect.X += effect.Dir * effect.Speed * dt;
                            if (effect.X > Level.Cells * Config.Setting("cell_width")) effect.Life = 0;
                            break;
                        }
                        effect.X += Math.Sign(target.X - effect.X) * Math.Min(effect.Speed * dt, Math.Abs(target.X - effect.X));
                        if (Math.Abs(effect.X - target.X) < 24) { Hit(target, effect.Damage, effect); effect.Life = 0; }
                        break;
                    }
                    if (effect.Pierce)
                    {
                        // 穿透：沿前进方向飞行，命中沿途每个敌人一次（用扫过区间避免单帧跳过）。
                        // line_pierce（御剑术平射）与退役配置的 secondary == "pierce" 共用这一段。
                        double from = effect.X;
                        effect.X += effect.Speed * dt;
                        if (effect.X > Level.Cells * Config.Setting("cell_width")) { effect.Life = 0; break; }
                        foreach (var e in Battle.Enemies.Where(e => Legal(e) && !effect.Hit.Contains(e.Id) && e.X >= from - 30 && e.X <= effect.X + 30).ToArray())
                        { effect.Hit.Add(e.Id); Hit(e, effect.Damage, effect); }
                        break;
                    }
                    if (target is null) { effect.Life = 0; break; }
                    effect.X += Math.Sign(target.X - effect.X) * Math.Min(effect.Speed * dt, Math.Abs(target.X - effect.X));
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
