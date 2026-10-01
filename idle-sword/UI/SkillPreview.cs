using Godot;
using IdleSword.Core;
using IdleSword.Features;

namespace IdleSword.UI;

/// <summary>
/// 技能预览模式：用独立会话逐个播放 15 个剑诀，供设计审核对照。
/// 预览会话不写存档、不参与玩家进度，退出后不留痕迹；预览期间主线挂机暂停，
/// 避免"看技能"时后台还在推进关卡。
/// </summary>
public partial class Main
{
    private GameSession? _preview;
    private int _previewSkill;
    private double _previewClock;
    // 靶子距离取 520：落在停步距离 640 之内（角色站定不乱跑），又在全部剑诀射程 950 之内。
    private const double PreviewTargetDistance = 520;
    private const double PreviewTargetHp = 2000;
    // 每 1.8 秒重置所选剑诀冷却，让 12 秒冷却的大招也能快速反复观察。
    private const double PreviewRecast = 1.8;

    private GameSession Active => _preview ?? _game;
    private List<string> PreviewSkillIds => _game.Config.Skills.Keys.OrderBy(id => id).ToList();
    private string PreviewSkillId => PreviewSkillIds[_previewSkill];

    private void TogglePreview()
    {
        if (_preview is null)
        {
            // 固定种子：同一剑诀每次预览的表现一致，便于对照。
            _preview = new GameSession(_game.Config, seed: 1);
            SelectPreviewSkill(0);
        }
        else _preview = null;
        _battle.Session = Active;
        ShowPage(_selectedTab); Refresh();
    }

    private void SelectPreviewSkill(int index)
    {
        if (_preview is null) return;
        var ids = PreviewSkillIds;
        _previewSkill = Math.Clamp(index, 0, ids.Count - 1);
        string current = ids[_previewSkill];
        // 只保留当前剑诀，避免其他技能的特效混进来，失去对照意义。
        foreach (string id in ids) _preview.State.Skills[id] = id == current ? 1 : 0;
        _preview.Battle.Cooldowns.Clear();
        // 清掉上一招的全部残留（飞行效果、玩家增益、飘字）：否则切技能后画面里混着两招，失去逐个对照的意义。
        _preview.Effects.Clear();
        _preview.ClearBuffs();
        _battle.ResetTransient();
        _previewClock = PreviewRecast;
        PreparePreviewField();
    }

    /// <summary>固定靶场：一个不移动、不反击、打不死的目标，并关闭刷怪。</summary>
    private void PreparePreviewField()
    {
        if (_preview is null) return;
        _preview.Battle.Spawns.Clear();
        // 把当前格的刷怪点标记为已越过，使 TickSpawns 不补怪（清空会被 ActivateCell 重新激活）。
        _preview.Battle.Spawns[_preview.Battle.Cell] = new() { Passed = true };
        // 原地清空不经过 BattleView 的换场守卫，不重置的话旧靶子会被当成击杀、多播一次死亡音。
        _preview.Battle.Enemies.Clear();
        _battle.ResetTransient();
        _preview.Battle.PlayerHp = _preview.MaxHp;
        var monster = _preview.Config.Monsters["slime"];
        _preview.Battle.Enemies.Add(new()
        {
            Id = _preview.Battle.NextEnemyId++, MonsterId = monster.Id, Kind = monster.Kind,
            X = _preview.Battle.PlayerX + PreviewTargetDistance,
            Hp = PreviewTargetHp, MaxHp = PreviewTargetHp, Atk = 0, AttackTimer = 999,
        });
    }

    private void TickPreview(double dt)
    {
        if (_preview is null) return;
        _previewClock += dt;
        if (_previewClock >= PreviewRecast) { _previewClock = 0; _preview.Battle.Cooldowns.Clear(); }
        var target = _preview.Battle.Enemies.FirstOrDefault(e => e.Hp > 0);
        if (target is null) { PreparePreviewField(); return; }
        // 每步重新钉住靶子：位置、免伤、不反击，保证 15 个剑诀面对完全相同的对照条件。
        target.X = _preview.Battle.PlayerX + PreviewTargetDistance;
        target.Atk = 0; target.AttackTimer = 999;
        if (target.Hp < target.MaxHp * .25) target.Hp = target.MaxHp;
        _preview.Battle.Spawns.Clear();
        _preview.Battle.Spawns[_preview.Battle.Cell] = new() { Passed = true };
    }

    /// <summary>QA 用：以固定步长推进预览若干帧，使截图不依赖真实帧率。</summary>
    private void AdvancePreview(int frames)
    {
        if (_preview is null) return;
        double step = _game.Config.Setting("fixed_step");
        // 与 _Process 的真实路径保持一致：逐 Step 观测技能冷却，QA 推进也才会触发释放音。
        for (int i = 0; i < frames; i++) { TickPreview(step); _preview.Step(step); TrackSkillCasts(Active); }
    }

    /// <summary>一行摘要：给审核时快速核对数值，界面不复述配置表以外的内容。</summary>
    private string PreviewSummary(SkillDef skill)
    {
        string secondary = skill.Secondary switch
        {
            "" => "无次级效果",
            "pierce" => "穿透",
            "multi" => $"多重 ×{skill.SecondaryValue:0}",
            "slow" => $"减速 {skill.SecondaryValue:P0} / {skill.SecondaryDuration:0.#}s",
            "stun" => $"眩晕 {skill.SecondaryDuration:0.#}s",
            "dot" => $"灼烧 {skill.SecondaryValue:P0}/秒 / {skill.SecondaryDuration:0.#}s",
            "vulnerable" => $"易伤 +{skill.SecondaryValue:P0} / {skill.SecondaryDuration:0.#}s",
            "lifesteal" => $"吸血 {skill.SecondaryValue:P0} / {skill.SecondaryDuration:0.#}s",
            "execute" => $"斩杀 阈值 {skill.SecondaryValue:P0}",
            "shield" => $"护盾 攻击×{skill.SecondaryValue:0.##} / {skill.SecondaryDuration:0.#}s",
            "regen" => $"回血 {skill.SecondaryValue:P0}/秒 / {skill.SecondaryDuration:0.#}s",
            _ => skill.Secondary,
        };
        string realm = _game.Config.Row("SwordLevel", skill.Realm).Text("name");
        return $"{realm} · {skill.Kind} · 冷却 {skill.Cooldown:0.#}s · 射程 {skill.Range:0} · 威力 ×{skill.Power:0.##} · {secondary}";
    }
}
