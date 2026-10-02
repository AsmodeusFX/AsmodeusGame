# CSV 字段字典

所有配置均为 UTF-8，首行英文表头，ID 大小写敏感。时间单位秒，1屏=1920逻辑单位，概率与百分比用小数。原始CSV由自定义加载器读取，Godot导入方式为keep。

以下为当前真实字段；并非最终技能/锻造设计。`Config/Schemas/headers.json`保存表头清单，类型与业务约束在`Core/Data/GameConfig.cs`执行。

## monster.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| kind | enum | 类别；对应表的已注册行为/分类 |
| hp | number | 基础生命值 |
| atk | number | 基础攻击力 |
| attack_range | number | 攻击距离（逻辑坐标） |
| attack_interval | number | 攻击间隔（秒，大于0） |
| move_speed | number | 移动速度（逻辑单位/秒） |
| attack_type | enum | melee / ranged / magic / none |
| gold | number | 击杀必得灵钱 |
| visual | string | Assets/visuals.json 中的表现 ID |

## level.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| order | integer | 排序；关卡顺序不得重复 |
| cells | integer | 关卡格数，当前25 |
| normal_hp | number | 普通怪 HP 倍率 |
| normal_atk | number | 普通怪 ATK 倍率 |
| elite_hp | number | 精英 HP 倍率 |
| elite_atk | number | 精英 ATK 倍率 |
| boss_hp | number | BOSS HP 倍率 |
| boss_atk | number | BOSS ATK 倍率 |
| rift_hp | number | 裂隙 HP 倍率 |
| wave_id | reference | wave.id |
| boss_id | reference | monster.id，分类必须boss |
| rift_id | reference | monster.id，分类必须rift |
| first_reward | group | drop.group_id，首杀组；恰好含1个妖核 |
| repeat_reward | group | drop.group_id，重复组；不得有妖核 |

## item.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| kind | enum | 类别；对应表的已注册行为/分类 |
| description | string | 说明 |

## fightattr.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| base_value | number | 属性基础值；百分比用0～1表示 |
| format | enum | 显示提示 integer / percent / decimal |

## SwordLevel.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| default_unlocked | bool | 0或1 |
| cost_gold | number | 灵钱消耗；天赋按当前等级+1乘此值 |
| order | integer | 排序；关卡顺序不得重复 |

## SwordSkill.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| realm_id | reference | SwordLevel.id |
| kind | enum | 类别；对应表的已注册行为/分类 |
| cooldown | number | 独立冷却秒数 |
| range | number | 目标选择距离（逻辑坐标） |
| power | number | 伤害/效果倍率 |
| duration | number | 持续时间秒数；定点技能为延迟；自定义飞行形态下为飞行／下坠时长 |
| max_level | integer | 最大等级 |
| cost | number | 基础升级成本；具体成长规则见下方 |
| cost_growth | number | 剑诀升级消耗指数倍率 |
| description | string | 技能说明文字，用于界面展示 |
| secondary | enum（可空） | 次级效果：pierce / multi / slow / stun / dot / vulnerable / lifesteal / execute / shield / regen / haste / crit_reduce；空表示无。其中 pierce / multi / shield / regen 当前没有技能使用（见下） |
| secondary_value | number | 次级效果强度（multi 为目标数；stun / pierce 可忽略） |
| secondary_duration | number | 次级效果持续时间（秒）；瞬发 / 参数类可为 0 |
| aoe_radius | number | 范围半径（ground 类）；0 表示单体，默认沿用 220。sky_drop 形态下为落点判定半径，此时必须为正（不回退 220） |
| trajectory | enum（可空） | 飞行形态：bolt / line_shot / line_pierce / sky_drop / arc_homing / hover_homing；空表示 bolt（直线弹道）。仅 projectile 可用 |
| projectile_count | integer | 单次出手的弹数，至少 1；非 projectile 只能为 1 |
| hover_time | number | **起飞前在空中停留的秒数**：hover_homing 为悬浮蓄势、sky_drop 为浮现后停留、line_pierce 为浮现；0 表示不停留 |
| arc_min | number | 弧高随机下界（arc_homing 专用），设计坐标的垂直振幅（非角度）；**允许为负，负值表示从下方掠过**，下限 -60 |
| arc_max | number | 弧高随机上界；不得超过 300（弧顶会越出战斗画面） |
| speed | number | 弹道速度（逻辑单位/秒）；0 表示默认 1500。**只作用于剑诀**，宠物弹与召唤弹始终走默认值 |
| spread | number | 弹群排列间距：sky_drop 为落点横向间距（**参与判定**，由 Core 算进 X），其余形态为表现层的排列间距 |
| volley_interval | number | 多发之间的发射间隔（秒），第 i 支额外延迟 i × interval；0 表示同时发射 |
| volley_jitter | number | 在发射间隔之上叠加的 ±随机（秒），让出剑时机不整齐 |
| spawn_jitter | number | 出生高度的随机幅度（像素，仅表现；随机值由 Core 摇定一次） |
| pierce_chance | number | **首次**命中时的穿透概率（0～1，0 表示不穿透）；只对 `line_shot` 有意义，且一次判定机会用完即关闭 |
| secondary_extra | number | 次级效果的附加参数：当前用于 `crit_reduce` 的每次缩短秒数 |

次级效果分两类：状态类（slow 减速 / stun 眩晕 / dot 灼烧 / vulnerable 易伤 / lifesteal 吸血 / execute 斩杀 / shield 护盾 / regen 回血）作用于目标或自身；参数类（pierce 穿透 / multi 多重）改变命中方式。详见 `docs/design/sword_skills.md`。

飞行形态（`trajectory`）决定弹道的运动方式，数量／间距／弧度／速度／出剑节奏都是正交旋钮，组合即可产出新技能而无需改代码：

- `bolt`（空）：直线飞向锁定目标，命中单体。剑灵弹丸等未指定形态的弹道走这条。
- `line_shot`：从角色肩侧**平射**、不追踪，命中路径上**最先遇到**的那个敌人即结算并销毁。配合 `pierce_chance` 可获得一次概率穿透：只在**第一次**命中时判定，穿透后同一支剑不再触发（`Pierced` 标记一次即永久关闭）。
- `line_pierce`：从角色肩侧**平射贯穿**，命中沿途每个敌人一次，越出关卡右界消失（穿透由形态自带，不占用次级效果列）。当前没有技能使用，作为形态词汇保留。
- `sky_drop`：在最近敌人上空生成**固定剑阵**——各支按 `spread` 在阵心两侧铺开，**与敌人数无关**（只有一个敌人时不会缩成一束）；X 生成后**冻结**（不追踪，敌人走开就打空），停留 `hover_time` 秒、下落 `duration` 秒后，对落点 `aoe_radius` 内的所有敌人结算，因此可命中重叠单位。
- `arc_homing`：追踪锁定目标，表现层按每支的 `arc` 画弧光（`arc` 在 `arc_min`～`arc_max` 间随机，由逻辑层一次摇定，不逐帧抖动）；区间带负值即为从下方掠过。
- `hover_homing`：先在施放者位置悬浮 `hover_time` 秒（表现层画在头顶剑阵位次上），再追踪锁定目标。**当前没有技能使用**，作为形态词汇保留。

多发时：追踪与平射形态**各自选敌、尽量不重复**（敌人不足才循环重复），`sky_drop` 则以阵心铺开落点；`volley_interval` + `volley_jitter` 决定出剑的先后节奏，`hover_time` 与它们一起折算成"起飞前停留"，因此命中时刻会随错时变化。没有合法目标时不空放、也不消耗冷却。

形态只影响"怎么飞、怎么命中"，伤害仍走统一的命中结算；`pierce` / `multi` / `shield` / `regen` 四个次级效果当前没有技能使用（分别来自退役的御气飞剑／疾风剑／护心剑罡／归元护法），代码保留在 `GameSession` 里供退休配置日后复用。两个增益类次级效果：

- `haste`：攻击速度提高，`secondary_value` 是提高的比例（3 = +300%，冷却流逝 ×4），`secondary_duration` 是持续秒数。**只加速输出类剑诀与剑灵的冷却，不加速增益类剑诀**——否则 15s 冷却 / 6s 持续的增益会在持续期内转好，变成 100% 常驻。
- `crit_reduce`：暴击率**绝对**提高（0.3 = +30 个百分点），`secondary_extra` 是每次暴击缩短的冷却秒数。缩短目标只从"已习得、当前冷却 > 0、且**不是增益类**"的技能里随机抽取，没有候选就什么都不做；并且**每次施法最多触发一次**（由该次施法的第一支弹丸负责）——暴击是逐弹丸摇的，多发齐射一轮 3～5 支，若每支都触发，触发密度会被放大数倍，增益的冷却也会被不断吃掉。

另外：配置里 `power = 1` 的增益剑诀**不占用伤害倍率窗口**（`_buffPower`/`_buffTime`），这样纯功能向的增益不会把正在生效的伤害增益重置掉。判断用的是配置的基础 `power`，不是算上等级与剑意之后的倍率。

## Talent.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| column | integer | 天赋横向列号，从1开始 |
| row | integer | 天赋纵向行号，1～5 |
| max_level | integer | 最大等级 |
| cost_gold | number | 灵钱消耗；天赋按当前等级+1乘此值 |
| cost_core | integer | 每次天赋升级消耗妖核 |
| effect | enum | 天赋atk/hp/auto_intent；剑意damage_percent |
| value | number | 效果数值，或全局设置的值 |

## TalentLink.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| from_id | reference | Talent.id，前置节点 |
| to_id | reference | Talent.id，后继节点 |

## Equip.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| base_atk | number | 武器基础攻击 |
| craft_cost | number | 打造并替换装备消耗灵钱 |
| upgrade_cost | number | 武器每次淬炼成本基数 |
| refine_cost | number | 洗练灵钱成本 |

## SwordUpgrade.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| tier | integer | 剑意页0～3 |
| skill_id | reference | 剑意引用SwordSkill.id，剑灵引用PetSkill.id |
| currency_id | reference | 消耗item.id |
| max_level | integer | 最大等级 |
| cost | number | 基础升级成本；具体成长规则见下方 |
| value | number | 效果数值，或全局设置的值 |
| effect | enum | 天赋atk/hp/auto_intent；剑意damage_percent |

## Pet.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| skill_id | reference | 剑意引用SwordSkill.id，剑灵引用PetSkill.id |
| weight | number | 抽取权重 |
| visual | string | Assets/visuals.json 中的表现 ID |

## PetSkill.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| cooldown | number | 独立冷却秒数 |
| power | number | 伤害/效果倍率 |
| range | number | 目标选择距离（逻辑坐标） |
| kind | enum | 类别；对应表的已注册行为/分类 |

## PetEquip.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| category | string | 剑灵增强类别，同一剑灵不可重复 |
| power | number | 伤害/效果倍率 |
| cost_gold | number | 灵钱消耗；天赋按当前等级+1乘此值 |

## wave.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| monster_id | reference | monster.id |
| count | integer | 每波普通怪数量 |
| interval | number | 刷怪间隔秒数 |
| elite_every | integer | 每多少波额外刷一个精英 |
| elite_id | reference | 精英monster.id |

## drop.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| group_id | string | 奖励组ID；同组多条一起发放 |
| item_id | reference | item.id |
| amount | number | 必得数量；首杀妖核必须恰好1 |

## spawn_point.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| offset | number | 每格内刷怪点横坐标 |

## contemplation.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| item_id | reference | item.id |
| capacity | number | 每类未收取产物上限 |
| click_amount | number | 单次点击或自动参悟产量 |
| auto_interval | number | 自动参悟秒数 |

## game_settings.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| value | number | 效果数值，或全局设置的值 |

## 当前公式与全局设置

- 剑诀消耗：`ceil(cost × cost_growth ^ 当前等级)`；当前等级0表示尚未习得。
- 天赋消耗：`cost_gold × (当前等级+1)` 灵钱，加 `cost_core` 妖核；所有货币充足才一起扣除。
- 剑意消耗：`cost × (当前等级+1)` 份对应剑意。
- 武器攻击：`base_atk × (1 + 0.15 × 淬炼等级) × 洗练品质`。
- 最终攻击：`(基础攻击 + 武器攻击) × (1 + 基础攻击加成 + 天赋攻击加成)`。
- 技能伤害：`最终攻击 × power × (1 + 0.15 × (技能等级-1) + 剑意加成)`，再应用Buff及暴击。
- `starting_gold`：初始灵钱120；`cell_width`：1920；`respawn_seconds`：复活等待秒数。
- 没有普攻：角色输出全部来自剑诀与剑灵，接近裂隙的停步判定取**已习得剑诀的最大射程**（`GameSession.AttackRange`），原先的 `basic_range` / `basic_cooldown` 已随普攻一并删除。
- `pet_draw_cost`、`pet_duplicate_gold`：召唤成本与重复返还。
- `fixed_step`：模拟固定步长；`save_interval`：自动存档间隔；`enemy_visual_limit`：仅绘制数量上限，不限制怪物存在数量。

## 修改与导出

使用UTF-8保存CSV，避免Excel将ID自动转换为数字或科学计数法。表头不可随意更名。修改后运行根目录README中的验证命令，再重启游戏。导出工具以标准CSV引号规则写出并重新读取验证，不覆盖玩家存档。

