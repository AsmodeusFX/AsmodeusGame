namespace IdleSword.Core;

/// <summary>版本化存档 DTO。永久进度与本轮战斗分离，保存未收取参悟产物，无离线时间戳。</summary>
public sealed class PlayerState
{
    public int Version { get; set; } = 1;
    public Dictionary<string, double> Wallet { get; set; } = new() { ["gold"] = 0, ["core"] = 0 };
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
}
