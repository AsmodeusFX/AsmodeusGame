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
| duration | number | 持续时间秒数；定点技能为延迟 |
| max_level | integer | 最大等级 |
| cost | number | 基础升级成本；具体成长规则见下方 |
| cost_growth | number | 剑诀升级消耗指数倍率 |
| description | string | 技能说明文字，用于界面展示 |
| secondary | enum（可空） | 次级效果：pierce / multi / slow / stun / dot / vulnerable / lifesteal / execute / shield / regen；空表示无 |
| secondary_value | number | 次级效果强度（multi 为目标数；stun / pierce 可忽略） |
| secondary_duration | number | 次级效果持续时间（秒）；瞬发 / 参数类可为 0 |
| aoe_radius | number | 范围半径（ground 类）；0 表示单体，默认沿用 220 |

次级效果分两类：状态类（slow 减速 / stun 眩晕 / dot 灼烧 / vulnerable 易伤 / lifesteal 吸血 / execute 斩杀 / shield 护盾 / regen 回血）作用于目标或自身；参数类（pierce 穿透 / multi 多重）改变命中方式。详见 `docs/design/sword_skills.md`。

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
- `basic_cooldown`、`basic_range`：普攻冷却和距离。
- `pet_draw_cost`、`pet_duplicate_gold`：召唤成本与重复返还。
- `fixed_step`：模拟固定步长；`save_interval`：自动存档间隔；`enemy_visual_limit`：仅绘制数量上限，不限制怪物存在数量。

## 修改与导出

使用UTF-8保存CSV，避免Excel将ID自动转换为数字或科学计数法。表头不可随意更名。修改后运行根目录README中的验证命令，再重启游戏。导出工具以标准CSV引号规则写出并重新读取验证，不覆盖玩家存档。

