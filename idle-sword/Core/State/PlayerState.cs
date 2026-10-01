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
    // 表现用来源标记：Skill 为产出该效果的剑诀/剑灵技能 ID，空表示普攻或敌方效果。
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
    public bool Pierce => Secondary == "pierce";
    public List<long> Hit { get; } = [];
}
