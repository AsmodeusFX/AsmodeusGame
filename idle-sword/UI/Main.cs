using Godot;
using IdleSword.Core;
using IdleSword.Features;
using FileAccess = Godot.FileAccess;

namespace IdleSword.UI;

public partial class Main : Control
{
    private GameSession _game = null!;
    private SaveStore _store = null!;
    private Control _page = null!;
    private BattleView _battle = null!;
    private Label _wallet = null!, _hpText = null!, _stage = null!, _notice = null!;
    private ProgressBar _hp = null!;
    private Button _loop = null!;
    private OptionButton _levelSelect = null!;
    private Button _previewToggle = null!;
    private readonly List<string> _levelIds = [];
    private readonly List<Action> _bindings = [];
    private readonly List<Button> _tabs = [];
    private int _selectedTab, _intentTab = -1;
    private double _accumulator, _saveClock, _refreshClock;
    private bool _failed, _testMode;
    private int _frames;
    private string _saveStatus = "本地存档";
    private string[] _args = [];

    public override void _Ready()
    {
        Theme = new Theme { DefaultFont = new SystemFont { FontNames = ["Microsoft YaHei", "Noto Sans CJK SC", "sans-serif"] }, DefaultFontSize = 22 };
        GetTree().AutoAcceptQuit = false;
        _args = OS.GetCmdlineUserArgs(); _testMode = _args.Contains("--smoke-test") || _args.Contains("--capture");
        try
        {
            var config = GameConfig.Load(file => FileAccess.GetFileAsString("res://Config/Tables/" + file));
            _store = new SaveStore(ProjectSettings.GlobalizePath(_testMode ? "user://qa/session.json" : "user://save_v1.json"));
            _settingsStore = new SettingsStore(ProjectSettings.GlobalizePath(_testMode ? "user://qa/settings.json" : "user://settings.json"));
            _settings = _settingsStore.Load();
            // 顺序要紧：ApplyAudio 先建好 Music/SFX 两条总线，LoadAudio 再把播放器挂上去。
            ApplyDisplay(); ApplyAudio(); LoadAudio();
            // 玩家拖动窗口改尺寸时要同步进设置，见 OnWindowSizeChanged。
            // 必须在 ApplyDisplay 之后接：启动那一次重设不该被当成玩家拖拽。
            GetWindow().SizeChanged += OnWindowSizeChanged;
            Sfx.Play = PlaySfx;
            // QA 总是使用新会话，不读取、不改写正常玩家进度。
            _game = new GameSession(config, _testMode ? null : _store.Load(config));
            _game.PersistRequested += Save;
            if (_store.Warning is not null) _saveStatus = _store.Warning;
            BuildShell(); ShowPage(0); Refresh(); StartBgm();
            // 缺素材是静默损坏：Godot 吞掉 _Ready 异常，画面会空白但流程照常，必须显式提示。
            var problems = new[] { _battle.LoadError, _audioLoadError }.Where(p => p is not null).ToArray();
            if (problems.Length > 0) { foreach (var problem in problems) GD.PushError(problem!); _gameNotice = string.Join("  /  ", problems); }
            GD.Print($"Idle-Sword READY | levels={config.Levels.Count} skills={config.Skills.Count} audio={_audio.Count} sfx_voices={_sfxPlayers.Length}");
        }
        catch (Exception ex)
        {
            _failed = true; GD.PushError(ex.ToString());
            UiKit.PanelAt(this, 80, 120, 1760, 800);
            UiKit.Label(this, "工程加载失败", 120, 145, 1600, 65, 38, UiKit.Gold);
            UiKit.Wrapped(this, ex.Message, 120, 240, 1640, 500, 25);
            if (_testMode) GetTree().Quit(1);
        }
    }
    private void BuildShell()
    {
        var bg = new ColorRect { Color = UiKit.Ink, MouseFilter = MouseFilterEnum.Ignore }; UiKit.Place(bg, 0, 0, 1920, 1080); AddChild(bg);
        UiKit.Label(this, "问剑长生", 32, 10, 270, 60, 35, UiKit.Gold);
        UiKit.Label(this, "IDLE SWORD  /  修行初境", 265, 20, 360, 45, 17, UiKit.Muted);
        // 顶栏右侧依次为 GM / 设置 / 保存，钱包宽度收窄给设置按钮让位。
        _wallet = UiKit.Label(this, "", 825, 14, 620, 48, 23);
        var gm = UiKit.Button(this, "GM", 1462, 20, 120, 42, () => { _game.GrantAllCurrencies(); Refresh(); });
        gm.TooltipText = "调试专用：item.csv 中每种货币各 +10000，新增货币自动纳入。";
        var settings = UiKit.Button(this, "设置", 1592, 20, 120, 42, ToggleSettings);
        settings.TooltipText = "画面、音频与进度重置。";
        UiKit.Button(this, "保存", 1730, 20, 150, 42, Save);
        _hp = new ProgressBar { ShowPercentage = false }; UiKit.Place(_hp, 34, 79, 465, 20);
        _hp.AddThemeStyleboxOverride("background", UiKit.Box(new Color("#263946"), 3)); _hp.AddThemeStyleboxOverride("fill", UiKit.Box(new Color("#89bda8"), 3)); AddChild(_hp);
        _hpText = UiKit.Label(this, "", 35, 104, 500, 32, 18, UiKit.Muted);
        _stage = UiKit.Label(this, "", 565, 76, 760, 54, 23, UiKit.Gold);
        _loop = UiKit.Button(this, "", 1570, 83, 310, 42, () => _game.ToggleLoop());
        _battle = new BattleView { Session = _game };
        _battle.HitLanded += heavy => PlaySfx(heavy ? "sfx_hit_heavy" : "sfx_hit");
        _battle.EnemyDefeated += () => PlaySfx("sfx_kill");
        UiKit.Place(_battle, 0, 143, 1920, 400); AddChild(_battle);
        UiKit.PanelAt(this, 24, 552, 1872, 64);
        _notice = UiKit.Label(this, "", 46, 559, 1350, 48, 21, UiKit.Jade);
        _previewToggle = UiKit.Button(this, "技能预览", 1404, 563, 120, 42, TogglePreview);
        _previewToggle.TooltipText = "用独立会话逐个播放 15 个剑诀，不写存档；预览期间主线挂机暂停。";
        _levelSelect = new OptionButton(); UiKit.Place(_levelSelect, 1530, 563, 340, 42); AddChild(_levelSelect);
        _levelSelect.ItemSelected += index => { if (index < _levelIds.Count) { _game.SelectLevel(_levelIds[(int)index]); Refresh(); } };
        string[] names = ["01  剑途", "02  剑境 · 剑诀", "03  铸剑", "04  剑意", "05  剑灵"];
        for (int i = 0; i < names.Length; i++) { int tab = i; _tabs.Add(UiKit.Button(this, names[i], 24 + i * 378, 632, 360, 56, () => ShowPage(tab))); }
        _page = new Control(); UiKit.Place(_page, 24, 704, 1872, 316); AddChild(_page);
        UiKit.Label(this, "初版试炼  ·  在线自动战斗  ·  离线不产出", 32, 1040, 1050, 28, 17, UiKit.Muted);
        UiKit.Label(this, "每关首杀妖核 ×1   /   长路无重置", 1460, 1040, 420, 28, 17, UiKit.Gold);
    }
    public override void _Process(double delta)
    {
        if (_failed || _game is null) return;
        _accumulator += delta;
        double step = _game.Config.Setting("fixed_step");
        int iterations = 0;
        // 预览期间只推进预览会话：既让主线挂机暂停，也保证预览状态不回写存档。
        while (_accumulator >= step && iterations++ < 200)
        {
            _accumulator -= step;
            if (_preview is not null) { TickPreview(step); _preview.Step(step); }
            else _game.Step(step);
            // 逐 Step 观测而不是逐帧：一次长帧会合并多个 Step，短冷却技能可能触发又走完而被漏掉。
            TrackSkillCasts(Active);
        }
        TrackFirstKills();
        TickSettings(delta);
        _saveClock += delta; _refreshClock += delta;
        if (_preview is null && _saveClock >= _game.Config.Setting("save_interval")) { _saveClock = 0; Save(); }
        if (_refreshClock >= .15) { _refreshClock = 0; Refresh(); }
        if (_testMode && ++_frames == 100) RunUiSmoke();
    }
    private async void RunUiSmoke()
    {
        try
        {
            if (_battle.LoadError is not null) throw new Exception(_battle.LoadError);
            // 缺音频与缺图同理，是静默损坏，必须显式断言。
            if (_audioLoadError is not null) throw new Exception(_audioLoadError);
            if (_audio.Count == 0 || _sfxPlayers.Length != SfxVoices) throw new Exception("Audio voices were not created");
            // 循环要真的生效：只设 LoopMode 而 loop_end 留在 0 是空区间，会"看着对、实际不循环"。
            if (_audio["bgm_battle"] is not AudioStreamWav bgm) throw new Exception("BGM is not an AudioStreamWav");
            if (bgm.LoopMode != AudioStreamWav.LoopModeEnum.Forward || bgm.LoopBegin != 0 || bgm.LoopEnd <= 0)
                throw new Exception($"BGM loop not applied: mode={bgm.LoopMode} begin={bgm.LoopBegin} end={bgm.LoopEnd}");
            // 发出真实控件信号，覆盖界面到系统的接线；QA 使用隔离会话。
            IEnumerable<Button> Buttons(Node node) => node.GetChildren().SelectMany(child => (child is Button b ? new[] { b } : Array.Empty<Button>()).Concat(Buttons(child)));
            void Press(string prefix) => Buttons(_page).First(b => b.Text.StartsWith(prefix)).EmitSignal(Button.SignalName.Pressed);
            // 外壳（顶栏/页签）上的按钮不在 _page 下，单独按整棵树查找。
            void Tap(string prefix) => Buttons(this).First(b => b.Text.StartsWith(prefix)).EmitSignal(Button.SignalName.Pressed);
            ShowPage(0); Refresh(); Press("剑心初明");
            if (_game.State.Talents.GetValueOrDefault("t_root") != 1) throw new Exception("Talent UI action failed");
            _intentTab = -1; ShowPage(3); Refresh(); Press("◇");
            var collect = _page.GetChildren().OfType<Button>().First(b => b.Text.StartsWith("移入收取"));
            collect.EmitSignal(Control.SignalName.MouseEntered);
            if (_game.State.Amount("intent_0") != 1) throw new Exception("Hover collection UI action failed");
            // 命中音：推进真实战斗，并逐帧放行让 BattleView._Process 里的血量差分真的跑到。
            // 一次跑完再取结果是不行的——那样敌人会在两帧之间生灭，表现层根本观察不到。
            for (int frame = 0; frame < 40; frame++)
            {
                for (int i = 0; i < 5; i++) _game.Step(_game.Config.Setting("fixed_step"));
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            if (SfxCount("sfx_hit") + SfxCount("sfx_hit_heavy") == 0) throw new Exception("Hit SFX never fired");
            // 击杀音：死亡的敌人已在同一次 Step 里被移除，表现层只能靠"从列表消失"判定，这里专门验证那条路径。
            if (SfxCount("sfx_kill") == 0)
            {
                var victim = _game.Battle.Enemies.FirstOrDefault(e => e.Kind != "rift");
                if (victim is null) throw new Exception("No enemy available to verify the kill SFX");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);   // 先让表现层看到它还活着
                victim.Hp = 0;
                _game.Step(_game.Config.Setting("fixed_step"));
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            if (SfxCount("sfx_kill") == 0) throw new Exception("Kill SFX never fired");
            // 普通命中分支要单独验证：1 级角色几乎一击秒杀，伤害占比总是越过重击阈值，
            // 自然战斗只会走到重击分支。这里把血量拉高，让单次伤害落进普通命中区间。
            var slime = _game.Config.Monsters["slime"];
            double tankHp = Math.Max(1, _game.Attack) * 400;
            _game.Battle.Enemies.Add(new()
            {
                Id = _game.Battle.NextEnemyId++, MonsterId = slime.Id, Kind = slime.Kind,
                X = _game.Battle.PlayerX + 200, Hp = tankHp, MaxHp = tankHp, Atk = 0, AttackTimer = 999,
            });
            for (int frame = 0; frame < 20 && SfxCount("sfx_hit") == 0; frame++)
            {
                for (int i = 0; i < 5; i++) _game.Step(_game.Config.Setting("fixed_step"));
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            if (SfxCount("sfx_hit") == 0) throw new Exception("Normal hit SFX never fired");
            // 灼烧跳伤必须不出命中音。跳伤按固定步长成块结算（每步 DotDps*fixed_step），
            // 而真实帧间隔小于固定步长：拿帧间隔当阈值会低于单步跳伤，判据永不成立，每步都播一次命中音。
            double step = _game.Config.Setting("fixed_step");
            var burnProbe = new EnemyState { DotUntil = 1, DotDps = 100 };
            if (!BattleView.IsBurnTick(burnProbe, 100 * step, step)) throw new Exception("Burn tick not classified as burn");
            if (BattleView.IsBurnTick(burnProbe, 100 * step, step * .2)) throw new Exception("Burn judged against the frame clock instead of the simulation clock");
            if (BattleView.IsBurnTick(burnProbe, 100 * 3, step)) throw new Exception("Direct hit misclassified as burn");
            // 端到端放在独立会话里：主线会话仍在挂机走路、可能死亡重生或补怪，
            // 任何一次额外命中都会污染"场上只有跳伤"这个前提。这里只留一个远处着火的靶子。
            // 刷怪点要标记为已越过而不是清空——清空会被 ActivateCell 在下一步重建并刷怪。
            var burnSession = new GameSession(_game.Config, seed: 1);
            for (int cell = 0; cell < burnSession.Level.Cells; cell++) burnSession.Battle.Spawns[cell] = new() { Passed = true };
            var burning = new EnemyState
            {
                Id = burnSession.Battle.NextEnemyId++, MonsterId = slime.Id, Kind = slime.Kind,
                X = burnSession.Battle.PlayerX + 5000, Hp = tankHp, MaxHp = tankHp, Atk = 0, AttackTimer = 999,
                DotUntil = 60, DotDps = Math.Max(1, _game.Attack * .2),
            };
            burnSession.Battle.Enemies.Add(burning);
            _battle.Session = burnSession;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);   // 先让表现层把这个敌人读成基准
            int hitsBeforeBurn = SfxCount("sfx_hit") + SfxCount("sfx_hit_heavy");
            int burnTicksBefore = _battle.BurnTicks;
            // 每帧跑满 30 步：headless 的真实帧间隔比固定步长大，帧数跑少了两种时钟差别不明显，
            // 用错时钟的写法会"碰巧"判对，这条断言就成了空断言。步数拉大后模拟时长必然远大于帧间隔。
            // 每步重新钉住靶距，免得角色一路走近后真的打中它。
            for (int frame = 0; frame < 6; frame++)
            {
                for (int i = 0; i < 30; i++) { burning.X = burnSession.Battle.PlayerX + 5000; burnSession.Step(step); }
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            // 先证明跳伤真的结算过，否则下面两条都是空断言。
            if (burning.Hp >= tankHp) throw new Exception("Burn never ticked, the SFX assertion would be vacuous");
            // 主判据看分类结果而不是听感：抑制机制用的是主线模拟钟，自检里那口钟几乎不走，
            // 误判出来的命中音会被整批吃掉、计数不变，"没出声"就成了能被掩盖的断言。
            if (_battle.BurnTicks - burnTicksBefore < 5) throw new Exception("Burn ticks were not classified as burn");
            if (SfxCount("sfx_hit") + SfxCount("sfx_hit_heavy") != hitsBeforeBurn) throw new Exception("Burn ticks played the hit SFX");
            _battle.Session = _game;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            // 召唤/跟随单位的站位：Core 把召唤物的 X 统一钉在玩家身后，错开完全靠表现层分配位次。
            var fakes = new[] { "skill_05", "skill_10", "skill_15" }
                .Select(id => new CombatEffect { Kind = "summon", Skill = id, Life = 5, MaxLife = 5 }).ToArray();
            foreach (var fake in fakes) _game.Effects.Add(fake);
            for (int frame = 0; frame < 2; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var slots = fakes.Select(e => _battle.CompanionSlots.GetValueOrDefault(e, -1)).ToArray();
            if (slots.Any(s => s < 0) || slots.Distinct().Count() != 3) throw new Exception("Summons shared a slot: " + string.Join(",", slots));
            var summonXs = slots.Select(s => BattleView.FollowerSlot(s).X).ToArray();
            if (summonXs.Distinct().Count() != 3) throw new Exception("Summons overlapped on screen: " + string.Join(",", summonXs));
            if (summonXs.All(x => x > 330) || summonXs.All(x => x < 330)) throw new Exception("Summons all stacked on one side");
            foreach (var fake in fakes) _game.Effects.Remove(fake);
            // 首杀奖励音由 FirstKills 账本增加触发。自检跑不到 BOSS，这里直接改账本触发一次；
            // 测试模式不写存档（Save 会直接返回），不会污染进度。
            _game.State.FirstKills.Add(_game.Level.Id);
            TrackFirstKills();
            if (SfxCount("sfx_reward") == 0) throw new Exception("Reward SFX never fired");
            _game.State.FirstKills.Remove(_game.Level.Id);
            TrackFirstKills();
            // 释放音：预览模式逐个播放剑诀，覆盖全部 5 种 kind。
            // 其中 buff 类不产生 CombatEffect，正是"效果引用差集"会永久漏掉的那一类。
            TogglePreview();
            foreach (string kind in new[] { "projectile", "target", "ground", "buff", "summon" })
            {
                int index = PreviewSkillIds.FindIndex(id => _game.Config.Skills[id].Kind == kind);
                if (index < 0) throw new Exception("No skill of kind " + kind);
                SelectPreviewSkill(index);
                AdvancePreview(60);
                if (SfxCount("sfx_cast_" + kind) == 0) throw new Exception($"Cast SFX for kind '{kind}' never fired");
            }
            TogglePreview();
            string? capturePath = null;
            if (_args.Contains("--capture"))
            {
                int i = Array.IndexOf(_args, "--capture");
                capturePath = i + 1 < _args.Length ? _args[i + 1] : "user://preview.png";
            }
            async Task Capture(string suffix)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (capturePath is null) return;
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                string path = capturePath.Replace(".png", suffix + ".png");
                var err = GetViewport().GetTexture().GetImage().SavePng(path);
                if (err != Error.Ok) throw new Exception("Capture failed: " + err);
                GD.Print("CAPTURE " + path);
            }
            for (int i = 0; i < 5; i++) { ShowPage(i); Refresh(); await Capture("-page" + i); }
            _intentTab = 0; ShowPage(3); Refresh(); await Capture("-intent");
            ShowPage(0); Refresh(); await Capture("");
            // 用 GM 按钮发放后续步骤所需资源：既覆盖 GM→GameSession 接线，也避免界面直接改写钱包。
            Tap("GM");
            if (_game.State.Amount("gold") < 10000 || _game.State.Amount("core") < 10000) throw new Exception("GM UI action failed");
            ShowPage(1); Refresh(); Press("习得");
            if (_game.State.Skills.GetValueOrDefault("skill_02") != 1) throw new Exception("Skill UI action failed");
            ShowPage(2); Refresh(); Press("打造并装备"); Press("淬炼");
            if (_game.State.Weapon != "sword_wood" || _game.State.WeaponLevel != 1) throw new Exception("Forge UI action failed");
            ShowPage(4); Refresh(); Press("召唤");
            if (_game.State.EquippedPets.Count != 1) throw new Exception("Pet UI action failed");
            // 突破音走的是"带专属成功音的 Act"路径；突破要花灵钱，GM 之后才有，所以放在这里。
            ShowPage(1); Refresh(); Press("突破");
            if (SfxCount("sfx_breakthrough") == 0) throw new Exception("Breakthrough SFX never fired");
            if (capturePath is not null)
            {
                // 技能展示场景：学会全部剑诀并把敌人做成耐打靶，便于逐帧核对 15 个技能的表现差异。
                foreach (var id in _game.Config.Skills.Keys) _game.State.Skills[id] = 3;
                // 原地清空不经过换场守卫，不重置的话旧 id 会被当成击杀、刷一波死亡音。
                _game.Battle.Enemies.Clear(); _game.Battle.Spawns.Clear(); _battle.ResetTransient();
                _game.Battle.PlayerX = 24 * _game.Config.Setting("cell_width") + 600;
                _game.Step(.05);
                foreach (var e in _game.Battle.Enemies) { e.Hp = e.MaxHp = 1e9; e.Atk = 0; }
                ShowPage(1); Refresh(); await Capture("-boss");
                for (int frame = 0; frame < 3; frame++)
                {
                    for (int i = 0; i < 12; i++) _game.Step(.05);
                    await Capture("-skills" + frame);
                }
                // 技能预览：15 个剑诀各出一张对照图，逐个核对表现与数值。
                TogglePreview();
                for (int i = 0; i < _game.Config.Skills.Count; i++)
                {
                    SelectPreviewSkill(i); ShowPage(_selectedTab); Refresh(); AdvancePreview(40);
                    await Capture("-skill" + (i + 1).ToString("00"));
                }
                TogglePreview();
            }
            // 设置面板：打开、改音量、改分辨率、二次确认重置，覆盖界面到系统的接线。
            OpenSettings();
            if (!_settingsRoot!.Visible) throw new Exception("Settings panel did not open");
            await Capture("-settings");
            _musicSlider.Value = 35; _soundSlider.Value = 20;
            if (_settings.MusicVolume != 35 || _settings.SoundVolume != 20) throw new Exception("Volume slider wiring failed");
            if (Math.Abs(AudioServer.GetBusVolumeDb(AudioServer.GetBusIndex("Music")) - Mathf.LinearToDb(.35f)) > .01) throw new Exception("Music bus volume not applied");
            _settings.Width = 1600; _settings.Height = 900; PersistSettings();
            var reloaded = new SettingsStore(ProjectSettings.GlobalizePath("user://qa/settings.json")).Load();
            if (reloaded.Width != 1600 || reloaded.MusicVolume != 35) throw new Exception("Settings persistence failed");
            double goldBefore = _game.State.Amount("gold"); int skillsBefore = _game.State.Skills.Count;
            // 预览模式下重置：必须退回主线会话，否则画面读预览、存档写主线，两者对不上。
            TogglePreview();
            if (_preview is null) throw new Exception("Preview mode did not start");
            Tap("重置游戏进度");
            if (_resetArmed <= 0) throw new Exception("Reset did not require confirmation");
            // 第一次点击只进入待确认，进度必须原封不动。
            if (_game.State.Amount("gold") != goldBefore || _game.State.Skills.Count != skillsBefore) throw new Exception("Reset fired on the first click");
            Tap("确认重置");
            if (_game.State.Amount("gold") != _game.Config.Setting("starting_gold") || _game.State.Amount("core") != 0 || _game.State.UnlockedLevels.Count != 1) throw new Exception("Reset did not restore the initial state");
            if (_game.State.FirstKills.Count != 0 || _game.State.Skills.Count != 1) throw new Exception("Reset left progress behind");
            if (_settingsRoot.Visible) throw new Exception("Settings panel should close after reset");
            if (_preview is not null || !ReferenceEquals(_battle.Session, _game)) throw new Exception("Reset must leave preview mode and rebind the battle view");
            // 真实窗口路径：--capture 是有显示的，这里验证分辨率确实落到窗口上。
            if (capturePath is not null)
            {
                async Task WaitFrames(int count)
                {
                    for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }
                // 改窗口尺寸/显示模式要经操作系统一圈，读回值会落后一两帧，所以轮询几帧再判定，
                // 否则断言会随帧率偶发失败。
                async Task ExpectSize(Vector2I size, string what)
                {
                    for (int i = 0; i < 12 && DisplayServer.WindowGetSize() != size; i++) await WaitFrames(1);
                    if (DisplayServer.WindowGetSize() != size)
                        throw new Exception($"{what}: window is {DisplayServer.WindowGetSize()}, expected {size}");
                }
                async Task ExpectMode(DisplayServer.WindowMode mode, string what)
                {
                    for (int i = 0; i < 12 && DisplayServer.WindowGetMode() != mode; i++) await WaitFrames(1);
                    if (DisplayServer.WindowGetMode() != mode)
                        throw new Exception($"{what}: window mode is {DisplayServer.WindowGetMode()}, expected {mode}");
                }
                // 1) 分辨率下拉走真实信号：选中一项后真实窗口必须跟着变。
                _settings.Fullscreen = false; ApplyDisplay(); await ExpectSize(new(1600, 900), "Initial display apply failed");
                OpenSettings();
                int pick = _resolutionOptions.FindIndex(r => r.Width == 1920 && r.Height == 1080);
                if (pick < 0) throw new Exception("Resolution preset missing");
                _resolutionBox.Select(pick);
                _resolutionBox.EmitSignal(OptionButton.SignalName.ItemSelected, pick);
                await ExpectSize(new(1920, 1080), "Resolution dropdown did not resize the window");
                // 2) 全屏勾选框走真实信号：显示模式与还原后的尺寸都要对。
                _fullscreenBox.ButtonPressed = true;
                await ExpectMode(DisplayServer.WindowMode.Fullscreen, "Fullscreen toggle did not apply");
                _fullscreenBox.ButtonPressed = false;
                await ExpectMode(DisplayServer.WindowMode.Windowed, "Leaving fullscreen did not restore windowed mode");
                await ExpectSize(new(1920, 1080), "Leaving fullscreen lost the chosen resolution");
                // 3) 先最大化再调分辨率是很常见的顺序：退出最大化时系统会异步还原旧尺寸，
                //    同帧设的尺寸会被盖掉，必须推迟一帧才套用。
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Maximized);
                await ExpectMode(DisplayServer.WindowMode.Maximized, "Maximize failed");
                _settings.Width = 1280; _settings.Height = 720; ApplyDisplay();
                await ExpectSize(new(1280, 720), "Resolution did not apply from a maximized window");
                // 4) 拖拽改出来的尺寸要被设置采纳，开面板不能反过来把窗口弹回旧预设。
                DisplayServer.WindowSetSize(new Vector2I(1000, 640));
                await ExpectSize(new(1000, 640), "Hand resize did not reach the window");
                if (_settings.Width != 1000 || _settings.Height != 640)
                    throw new Exception($"Hand resize not captured: {_settings.Width}x{_settings.Height}");
                // 故意让配置说"全屏"而窗口停在窗口模式，此时开面板只该如实勾选。
                // ButtonPressed 的 setter 会发出 Toggled，用错就会在这里把窗口顶成全屏。
                _settings.Fullscreen = true;
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                DisplayServer.WindowSetSize(new Vector2I(1000, 640));
                CloseSettings(); OpenSettings(); await WaitFrames(6);
                if (DisplayServer.WindowGetMode() != DisplayServer.WindowMode.Windowed)
                    throw new Exception("Opening settings applied the display mode by itself");
                if (DisplayServer.WindowGetSize() != new Vector2I(1000, 640))
                    throw new Exception($"Opening settings resized the window to {DisplayServer.WindowGetSize()}");
                if (!_fullscreenBox.ButtonPressed) throw new Exception("Fullscreen checkbox does not reflect the saved value");
                if (!_resolutionBox.Disabled) throw new Exception("Resolution dropdown should be disabled while fullscreen");
                // 收尾：回到窗口模式与预设尺寸。
                _settings.Fullscreen = false; _settings.Width = 1280; _settings.Height = 720;
                ApplyDisplay(); CloseSettings(); await WaitFrames(2);
                GD.Print($"DISPLAY {DisplayServer.WindowGetSize()} mode={DisplayServer.WindowGetMode()} screen={DisplayServer.ScreenGetSize()}");
            }
            // 界面音：上面所有按钮都是通过 EmitSignal 触发的真实信号，点击音应已随之播放。
            if (SfxCount("sfx_click") == 0) throw new Exception("Click SFX never fired");
            if (SfxCount("sfx_panel") == 0) throw new Exception("Panel SFX never fired");
            GD.Print($"SFX PLAYS {_sfxPlays} throttled={_sfxThrottled} | click={SfxCount("sfx_click")} hit={SfxCount("sfx_hit")}/{SfxCount("sfx_hit_heavy")} "
                + $"kill={SfxCount("sfx_kill")} buy={SfxCount("sfx_buy")} deny={SfxCount("sfx_deny")} panel={SfxCount("sfx_panel")} "
                + $"reward={SfxCount("sfx_reward")} breakthrough={SfxCount("sfx_breakthrough")}");
            GD.Print("UI SMOKE PASS"); GetTree().Quit();
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }
    private void Refresh()
    {
        var s = _game.State;
        // 战斗读数跟随当前会话：预览时显示预览会话，钱包始终是玩家真实钱包。
        var shown = Active;
        _wallet.Text = $"灵钱  {UiKit.Number(s.Amount("gold"))}       妖核  {s.Amount("core"):0}       攻击  {UiKit.Number(shown.Attack)}";
        _hp.MaxValue = shown.MaxHp; _hp.Value = shown.Battle.PlayerHp;
        _hpText.Text = $"气血  {UiKit.Number(shown.Battle.PlayerHp)} / {UiKit.Number(shown.MaxHp)}     {_saveStatus}";
        _previewToggle.Text = _preview is null ? "技能预览" : "退出预览";
        _stage.Text = _preview is null
            ? $"{_game.Level.Name}     第 {_game.Battle.Cell + 1:00} / {_game.Level.Cells} 格     在场 {_game.Battle.Enemies.Count} 敌"
            : $"技能预览    第 {_previewSkill + 1:00} / {PreviewSkillIds.Count} 个剑诀    {_game.Config.Skills[PreviewSkillId].Name}";
        _notice.Text = _gameNotice ?? _game.Message;
        _loop.Text = s.LoopLevel ? "整关循环  /  点击自动推进" : "自动推进  /  点击循环本关";
        if (_levelIds.Count != s.UnlockedLevels.Count)
        {
            _levelSelect.Clear(); _levelIds.Clear();
            foreach (var level in _game.Config.Levels.Where(l => s.UnlockedLevels.Contains(l.Id))) { _levelIds.Add(level.Id); _levelSelect.AddItem(level.Name); }
        }
        _levelSelect.Select(_levelIds.IndexOf(_game.Level.Id));
        foreach (var update in _bindings) update();
    }
    private void Save()
    {
        if (_failed || _testMode) return;
        try { _store.Save(_game.State); _saveStatus = "已保存 " + DateTime.Now.ToString("HH:mm:ss"); _gameNotice = null; }
        catch (Exception ex) { _saveStatus = "保存失败"; GD.PushError(ex.ToString()); _gameNotice = "保存失败，请检查磁盘空间与存档目录权限。"; }
    }
    private string? _gameNotice;
    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) { if (!_failed) Save(); GetTree().Quit(); }
    }
    /// <summary>
    /// 退出时释放。静态转发持有本实例的方法引用，若不清掉，托管对象会一直活着，
    /// 连带它持有的音频资源也不释放，Godot 退出时会报一堆泄漏。
    /// </summary>
    public override void _ExitTree()
    {
        Sfx.Play = null;
        // 播放器持有 Stream 引用，不放掉的话音频资源要等 .NET GC 才回收，
        // 而 Godot 的退出泄漏检查跑在 GC 之前，于是每次退出都刷一堆"资源仍在使用"的噪声。
        foreach (var player in _sfxPlayers) { player.Stop(); player.Stream = null; }
        if (_music is not null) { _music.Stop(); _music.Stream = null; }
        _audio.Clear();
        _sfxPlayers = [];
        _music = null;
    }
    private void ShowPage(int tab)
    {
        _selectedTab = tab; _bindings.Clear();
        foreach (var child in _page.GetChildren()) { _page.RemoveChild(child); child.QueueFree(); }
        for (int i = 0; i < _tabs.Count; i++)
        {
            // 预览期间页面固定为剑诀列表，禁用页签避免切走后状态与画面不一致。
            _tabs[i].Disabled = _preview is not null;
            _tabs[i].AddThemeColorOverride("font_color", i == tab && _preview is null ? UiKit.Gold : UiKit.Muted);
        }
        if (_preview is not null) { PreviewPage(); return; }
        switch (tab) { case 0: TalentPage(); break; case 1: SkillPage(); break; case 2: ForgePage(); break; case 3: IntentPage(); break; case 4: PetPage(); break; }
    }
    private void Act(Func<bool> action, string? successSfx = null)
    {
        bool ok = action();
        // 失败音覆盖的不止"资源不足"，也包括已满级/已拥有/槽位已满；要精确区分得让 Core 返回失败原因，属于过度设计。
        PlaySfx(ok ? successSfx ?? "sfx_buy" : "sfx_deny");
        ShowPage(_selectedTab); Refresh();
    }
}
