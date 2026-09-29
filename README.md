# Idle-Sword · 问剑长生

Windows / Godot .NET / C# 横版修仙增量放置原型。

## 启动

1. 安装 **Godot 4.7.2 .NET** 和 **.NET 10 SDK**（本机已验证版本：10.0.401）。
2. 在 Godot 项目管理器中导入 `idle-sword/project.godot`。
3. 初次运行需要还原 Godot C# NuGet 包。在仓库根目录执行：

```powershell
dotnet build idle-sword/Idle-Sword.csproj
```

4. 在 Godot 中按 **F5**。默认窗口 1280×720，设计尺寸 1920×1080，保持 16:9 等比缩放；宽高比不同则留边。

## 当前可体验

- 角色地面移动、范围内停步、自动普攻和自动剑诀；近战/远程/法术敌人。
- 每关 25 格、当前格刷怪、超时叠怪、越过刷怪点停刷。
- 普通格死亡重返本关起点；BOSS 格原地重生并保留敌人伤势与死亡状态。
- BOSS 死后停止补怪，清完敌人解封裂隙，击碎后传送。
- 每关 BOSS 首杀固定 1 妖核，重复击杀不掉妖核；旧关和最终关整关循环。
- 剑途节点、5 个境界/15 个样例技能、单武器打造/淬炼/洗练、4 类参悟/强化、3 类剑灵/增强。
- 在线自动参悟通过剑途“静心自悟”解锁；产物移入鼠标收取，切页不影响生产。
- 5 秒自动保存、关键操作保存、正常关闭保存、原子替换及上一份备份。

界面右侧关卡下拉框选择已解锁关卡，选择后进入整关循环；上方按钮切换循环与自动推进。

**这是可运行框架原型，技能、天赋、装备、抽取规则和数值仍是样例。** 武器暂时直接替换，不含多实例背包；剑意强化暂以效果倍率为主。100 关已配置，尚未完成整体平衡与长尾内容。详见 [阶段交付说明](docs/planning/phase_01.md)。

## 验证与配置导出

从仓库根目录执行：

```powershell
dotnet run --project tests/IdleSword.Checks.csproj -- idle-sword/Config/Tables
dotnet run --project tests/IdleSword.Checks.csproj -- idle-sword/Config/Tables --export artifacts/csv-roundtrip
```

检查不需要启动 Godot。第二条先验证，再将配置通过 CSV 写入器导出到独立目录，并重新加载校验。修改源 CSV 后重启游戏生效，不运行时热重载。

Godot 引擎验证（将 `godot` 替换为本机 .NET 编辑器可执行文件）：

```powershell
godot --headless --path idle-sword --editor --import
godot --headless --path idle-sword -- --smoke-test
```

`--smoke-test` 使用独立的新会话、不写正常存档，遍历页面并测试天赋按钮与鼠标收取信号。`--capture <绝对PNG路径>` 在带渲染的同类验证中生成各页面截图，输出目录需已存在。

## 存档与导出

- 游戏使用 Godot `user://save_v1.json` 和 `save_v1.json.bak`。Windows 默认位置为 `%APPDATA%\Godot\app_userdata\Idle-Sword\`。
- 存档不在 Git 仓库中，换电脑不会通过 Git 自动携带试玩进度。
- 无离线收益。重进恢复保存时的关卡、位置、敌人状态与技能冷却；短期弹丸、区域、召唤物与战斗 Buff 会清理。
- `export_presets.cfg` 已配置 Windows x86_64 导出和原始 CSV/资源映射包含规则。导出 EXE 需要另行安装对应版本导出模板；当前交付验证的是编辑器运行版本，未生成发行包。

## 换电脑继续开发

提交并推送整个 `AsmodeusGame` 仓库，包括 `docs/`、`idle-sword/` 和 `tests/`。家中拉取后安装同版本工具，从 [文档索引](docs/README.md) 接续；无需依赖本地会话同步。不要提交 `.godot/`、`bin/`、`obj/`、`artifacts/`。
