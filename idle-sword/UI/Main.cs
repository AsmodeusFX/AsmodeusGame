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
    private Label _wallet = null!, _hpText = null!, _stage = null!, _notice = null!;
    private ProgressBar _hp = null!;
    private Button _loop = null!;
    private OptionButton _levelSelect = null!;
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
            // QA 总是使用新会话，不读取、不改写正常玩家进度。
            _game = new GameSession(config, _testMode ? null : _store.Load(config));
            _game.PersistRequested += Save;
            if (_store.Warning is not null) _saveStatus = _store.Warning;
            BuildShell(); ShowPage(0); Refresh();
            GD.Print($"Idle-Sword READY | levels={config.Levels.Count} skills={config.Skills.Count}");
        }
        catch (Exception ex)
        {
            _failed = true; GD.PushError(ex.ToString());
            UiKit.PanelAt(this, 80, 120, 1760, 800);
            UiKit.Label(this, "工程加载失败", 120, 145, 1600, 65, 38, UiKit.Gold);
            var error = UiKit.Label(this, ex.Message, 120, 240, 1640, 500, 25); error.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            if (_testMode) GetTree().Quit(1);
        }
    }
    private void BuildShell()
    {
        var bg = new ColorRect { Color = UiKit.Ink, MouseFilter = MouseFilterEnum.Ignore }; UiKit.Place(bg, 0, 0, 1920, 1080); AddChild(bg);
        UiKit.Label(this, "问剑长生", 32, 10, 270, 60, 35, UiKit.Gold);
        UiKit.Label(this, "IDLE SWORD  /  修行初境", 265, 20, 360, 45, 17, UiKit.Muted);
        _wallet = UiKit.Label(this, "", 825, 14, 770, 48, 23);
        UiKit.Button(this, "保存", 1730, 20, 150, 42, Save);
        _hp = new ProgressBar { ShowPercentage = false }; UiKit.Place(_hp, 34, 79, 465, 20);
        _hp.AddThemeStyleboxOverride("background", UiKit.Box(new Color("#263946"), 3)); _hp.AddThemeStyleboxOverride("fill", UiKit.Box(new Color("#89bda8"), 3)); AddChild(_hp);
        _hpText = UiKit.Label(this, "", 35, 104, 500, 32, 18, UiKit.Muted);
        _stage = UiKit.Label(this, "", 565, 76, 760, 54, 23, UiKit.Gold);
        _loop = UiKit.Button(this, "", 1570, 83, 310, 42, () => _game.ToggleLoop());
        var battle = new BattleView { Session = _game }; UiKit.Place(battle, 0, 143, 1920, 400); AddChild(battle);
        UiKit.PanelAt(this, 24, 552, 1872, 64);
        _notice = UiKit.Label(this, "", 46, 559, 1350, 48, 21, UiKit.Jade);
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
        while (_accumulator >= step && iterations++ < 200) { _game.Step(step); _accumulator -= step; }
        _saveClock += delta; _refreshClock += delta;
        if (_saveClock >= _game.Config.Setting("save_interval")) { _saveClock = 0; Save(); }
        if (_refreshClock >= .15) { _refreshClock = 0; Refresh(); }
        if (_testMode && ++_frames == 100) RunUiSmoke();
    }
    private async void RunUiSmoke()
    {
        try
        {
            // 发出真实控件信号，覆盖界面到系统的接线；QA 使用隔离会话。
            IEnumerable<Button> Buttons(Node node) => node.GetChildren().SelectMany(child => (child is Button b ? new[] { b } : Array.Empty<Button>()).Concat(Buttons(child)));
            void Press(string prefix) => Buttons(_page).First(b => b.Text.StartsWith(prefix)).EmitSignal(Button.SignalName.Pressed);
            ShowPage(0); Refresh(); Press("剑心初明");
            if (_game.State.Talents.GetValueOrDefault("t_root") != 1) throw new Exception("Talent UI action failed");
            _intentTab = -1; ShowPage(3); Refresh(); Press("◇");
            var collect = _page.GetChildren().OfType<Button>().First(b => b.Text.StartsWith("移入收取"));
            collect.EmitSignal(Control.SignalName.MouseEntered);
            if (_game.State.Amount("intent_0") != 1) throw new Exception("Hover collection UI action failed");
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
            _game.State.Wallet["gold"] = 10000;
            ShowPage(1); Refresh(); Press("习得");
            if (_game.State.Skills.GetValueOrDefault("skill_02") != 1) throw new Exception("Skill UI action failed");
            ShowPage(2); Refresh(); Press("打造并装备"); Press("淬炼");
            if (_game.State.Weapon != "sword_wood" || _game.State.WeaponLevel != 1) throw new Exception("Forge UI action failed");
            ShowPage(4); Refresh(); Press("召唤");
            if (_game.State.EquippedPets.Count != 1) throw new Exception("Pet UI action failed");
            if (capturePath is not null)
            {
                _game.Battle.Enemies.Clear(); _game.Battle.Spawns.Clear();
                _game.Battle.PlayerX = 24 * _game.Config.Setting("cell_width") + 600;
                _game.Step(.05); ShowPage(1); Refresh(); await Capture("-boss");
            }
            GD.Print("UI SMOKE PASS"); GetTree().Quit();
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }
    private void Refresh()
    {
        var s = _game.State;
        _wallet.Text = $"灵钱  {UiKit.Number(s.Amount("gold"))}       妖核  {s.Amount("core"):0}       攻击  {UiKit.Number(_game.Attack)}";
        _hp.MaxValue = _game.MaxHp; _hp.Value = _game.Battle.PlayerHp;
        _hpText.Text = $"气血  {UiKit.Number(_game.Battle.PlayerHp)} / {UiKit.Number(_game.MaxHp)}     {_saveStatus}";
        _stage.Text = $"{_game.Level.Name}     第 {_game.Battle.Cell + 1:00} / {_game.Level.Cells} 格     在场 {_game.Battle.Enemies.Count} 敌";
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
    private void ShowPage(int tab)
    {
        _selectedTab = tab; _bindings.Clear();
        foreach (var child in _page.GetChildren()) { _page.RemoveChild(child); child.QueueFree(); }
        for (int i = 0; i < _tabs.Count; i++) _tabs[i].AddThemeColorOverride("font_color", i == tab ? UiKit.Gold : UiKit.Muted);
        switch (tab) { case 0: TalentPage(); break; case 1: SkillPage(); break; case 2: ForgePage(); break; case 3: IntentPage(); break; case 4: PetPage(); break; }
    }
    private void Act(Func<bool> action) { action(); ShowPage(_selectedTab); Refresh(); }
}
