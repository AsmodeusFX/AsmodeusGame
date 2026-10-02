namespace IdleSword.Core;

/// <summary>版本化存档 DTO。永久进度与本轮战斗分离，保存未收取参悟产物，无离线时间戳。</summary>
public sealed class PlayerState
{
    public int Version { get; set; } = 1;
    public Dictionary<string, double> Wallet { get; set; } = new() { ["gold"] = 0, ["core"] = 0 };
    // GM 调试发放记录：按货币 id 累计由调试入口发出的数量。存档校验据此区分
    // 「玩法产出」与「调试产出」，否则调试发出的妖核会被当成篡改而拒绝整份存档。
    public Dictionary<string, double> DebugGranted { get; set; } = [];
    public HashSet<string> FirstKills { get; set; } = [];
    public HashSet<string> UnlockedLevels { get; set; } = [];
    public HashSet<string> Realms { get; set; } = [];
    public Dictionary<string, int> Skills { get; set; } = [];
    public Dictionary<string, int> Talents { get; set; } = [];
    public Dictionary<string, int> Upgrades { get; set; } = [];
    public Dictionary<string, double> PendingIntent { get; set; } = [];
    public Dictionary<string, double> IntentTimers { get; set; } = [];
    public HashSet<string> Pets { get; set; } = [];
    public List<string> EquippedPets { get; set; } = [];
    public Dictionary<string, List<string>> PetBuffs { get; set; } = [];
    public string Weapon { get; set; } = "";
    public int WeaponLevel { get; set; }
    public double WeaponRoll { get; set; } = 1;
    public bool LoopLevel { get; set; }
    public BattleState Battle { get; set; } = new();
    public double Amount(string item) => Wallet.GetValueOrDefault(item);
}

public sealed class BattleState
{
    public string LevelId { get; set; } = "";
    public double PlayerX { get; set; }
    public double PlayerHp { get; set; }
    public int Cell { get; set; }
    public double RespawnTimer { get; set; }
    public bool BossDefeated { get; set; }
    public bool PortalDestroyed { get; set; }
    public long NextEnemyId { get; set; } = 1;
    public Dictionary<int, SpawnState> Spawns { get; set; } = [];
    public List<EnemyState> Enemies { get; set; } = [];
    public Dictionary<string, double> Cooldowns { get; set; } = [];
}
public sealed class SpawnState
{
    public bool Passed { get; set; }
    public double Timer { get; set; }
    public int Wave { get; set; }
}
public sealed class EnemyState
{
    public long Id { get; set; }
    public string MonsterId { get; set; } = "";
    public string Kind { get; set; } = "normal";
    public double X { get; set; }
    public double Hp { get; set; }
    public double MaxHp { get; set; }
    public double Atk { get; set; }
    public double AttackTimer { get; set; }
    // 次级效果状态：Until 为剩余秒数，0 表示未生效；Factor 为生效期间的乘数。
    public double SlowUntil { get; set; }
    public double SlowFactor { get; set; } = 1;
    public double StunUntil { get; set; }
    public double DotUntil { get; set; }
    public double DotDps { get; set; }
    public double VulnerableUntil { get; set; }
    public double VulnerableFactor { get; set; } = 1;
}

/// <summary>短期效果不跨关卡；重进游戏清理表现效果，但保留已保存的敌人 HP 和技能冷却。</summary>
public sealed class CombatEffect
{
    public string Kind { get; init; } = "";
    public double X { get; set; }
    public long Target { get; init; }
    public double Damage { get; init; }
    public double Life { get; set; }
    public double Timer { get; set; }
    public bool Hostile { get; init; }
    // 表现用来源标记：Skill 为产出该效果的剑诀/剑灵技能 ID，空表示敌方效果。
    // 仅次级效果无法区分同类（如青莲剑阵与霜华剑域都是 ground+slow），故显式带上来源。
    public string Skill { get; init; } = "";
    // 初始时长，供界面计算渐隐与施法进度；不参与战斗判定。
    public double MaxLife { get; init; }
    // 次级效果与命中参数：Secondary 为空表示无；Pierce 由 Secondary == "pierce" 推导。
    public string Secondary { get; init; } = "";
    public double SecondaryValue { get; init; }
    public double SecondaryDuration { get; init; }
    public double ExecuteThreshold { get; init; }
    public double AoeRadius { get; init; }
    // 飞行形态（空 = 直线弹道 bolt）：决定 X 是否冻结、是否先悬浮蓄势。逻辑与表现都读它。
    public string Trajectory { get; init; } = "";
    // 发射方向（+1/-1），施放时算一次。追踪目标中途死亡后靠它把剩余时间飞完，不改追别人。
    public double Dir { get; init; }
    // 弹群内序号：Core 按选敌顺序编号，表现层据此错开站位与绘制层次（Core 不算显示位置）。
    public int Index { get; init; }
    // 仅表现：弧线形态的弧度，由 Core 用种子随机一次摇定；交给表现层逐帧摇会抖动且不可复现。
    public double Arc { get; init; }
    // 仅表现：0..1 的通用抖动，同样由 Core 摇定一次；天降形态拿它做出生高度的高低差。
    public double Jitter { get; init; }
    // 飞行速度（逻辑单位/秒）。只有剑诀会写入配置值；宠物弹、召唤弹与普攻遗留路径一律用默认 1500，
    // 所以新增 speed 配置列不会连带改动它们。
    public double Speed { get; init; } = 1500;
    // 穿透：既可以是次级效果（退役配置用），也可以由形态自带（line_pierce 平射贯穿），
    // 后者让"形态决定怎么命中"而不必占用次级效果列。这是**恒穿透**，与下面的概率穿透分开。
    public bool Pierce => Secondary == "pierce" || Trajectory == "line_pierce";
    // 概率穿透（line_shot 专用）：首次命中时的一次判定机会，由配置给概率。
    public double PierceChance { get; init; }
    // 是否已经用掉过那次判定机会。用完置真，此后命中一律销毁——"概率穿透只在第一次击中触发"。
    public bool Pierced { get; set; }
    public List<long> Hit { get; } = [];
}
