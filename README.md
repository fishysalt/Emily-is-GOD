# Emily is GOD

给 [ValleyTalk](https://www.nexusmods.com/stardewvalley/mods/30319)（星露谷 AI 无限对话）加上**记忆**与**自主行程**的伴生 SMAPI mod。

不改动 ValleyTalk 本体，通过它公开的 mod API 注入，随时可卸载回退。

> 当前状态：记忆层已完成并通过实机验证；行程层已完成引擎侧（手动注入可走通）。

---

## 它解决什么问题

### 1. ValleyTalk 会遗忘

ValleyTalk 本身**已经**在每个 NPC 的历史里存了带游戏内时间戳的原始对话，存在存档的
`Game1.CustomData` → `smapi/mod-data/dandm1.valleytalk/eventhistory_<npc>`。

但它取用时只保留**最新 20 条**作为时间下限，再叠加 4000 字符预算
（`Character.cs:746` 的 `EventHistorySample()`）。**第 21 条往前的记忆 LLM 永远看不到**，
尽管原始数据还躺在存档里。

本 mod 补这个洞，分两层：

| | 内容 | 时间尺度 |
|---|---|---|
| **短期记忆** | 本周原始对话，带游戏内日期时间戳 | 当前游戏内周 |
| **长期记忆** | LLM 压缩出的摘要 + 关键事实 + **承诺与约定** | 所有更早的周 |

星露谷一年 = 4 季 × 28 天 = 16 周，**28 天正好是 4 个整周**，所以周界天然对齐
（D1-7 / D8-14 / D15-21 / D22-28），跨季不产生残周。

### 2. NPC 不知道自己要做什么

ValleyTalk 已经把 NPC 剩余行程的**目的地**喂给模型（`locationFuturePlans`：
"Later today, {{Name}} is planning to go to: {{Locations}}"），但**不告诉时间，也不提节日**。
本 mod 补一个「当前情况」块，让她能说"我 8 点有事"、也能以节日为由拒绝邀约。

### 3. NPC 说了要做的事做不到

把对话里的承诺变成**真实行程**——这就是 `NpcScheduler`。

---

## 引擎行为笔记（读反编译源码 + 实测得出）

这部分是本项目最有价值的知识沉淀，开发时踩过的坑都在这里。

### 日程是一串"时间点 → 预计算路线"，不是脚本

- `Schedule` = `Dictionary<int, SchedulePathDescription>`，键是**出发时刻**（军事时间）。
- 每天早晨 `resetForNewDay()` 从资产 `Characters/schedules/<NPC>` **重建一次**；
  之后每 10 游戏分钟被 `checkSchedule(当前时刻)` **轮询**。
- 所以日程**不是消费一次就冻结**，全天可改。早上商量晚上的事**引擎原生支持**。

### 路线是静态预计算的，起点是上一条的终点

`parseMasterSchedule` 链式构建：第 i 条的路线从第 i-1 条的终点算起。
**只插一条会让后面所有条目起点错位**——必须重建整条剩余链。`NpcScheduler` 就是这么做的。

### `checkSchedule` 的精确语义

```csharp
if (ignoreScheduleToday || Schedule == null) return;
if (lastAttemptedSchedule < timeOfDay) {
    lastAttemptedSchedule = timeOfDay;
    Schedule.TryGetValue(timeOfDay, out value);      // 精确查表，没有"找最近一条"
    if (value != null) queuedSchedulePaths.Add(value);  // 只是入队
}
if (controller != null && controller.controller?.pathToEndPoint?.Count > 0) return;
if (queuedSchedulePaths.Count > 0 && timeOfDay >= queuedSchedulePaths[0].time)
    value = queuedSchedulePaths[0];                  // 有空了才执行
```

推论：
- **时间键必须是 10 的倍数**，否则永不触发。
- **已过去的时刻补不了**（单调闸门）。
- 因为入队机制，**即使目标时刻她正在走路也不会丢**，忙完就执行。

### 到达之后引擎只做四件事之一

| `endOfRouteBehavior` | 行为 |
|---|---|
| 空 / null | 原地站着 |
| 含 `square_` | 在 N×N 格内踱步 |
| `Data/AnimationDescriptions` 里的键 | 循环播该动画 |
| 带 `endOfRouteMessage` | 到达时说一句台词 |

**没有"停留 N 小时"**。"6 点到、9 点回家"必须是两条。

### 三个关键限制（实测确认）

1. **农场被排除在 NPC 寻路之外**，进出都不行。连 `Farm → BusStop`（紧邻）都失败。
   SpaceCore 自己的寻路调度器也报同样的错。这就是原版配偶日程把起点硬编码成
   `BusStop 10 23` 的原因——游戏先**传送**配偶到公交站再走。本 mod 照抄这个做法。
2. **已婚 NPC 的键链只查 `marriage_*`**。艾米丽只有 `marriage_Mon` / `marriage_Fri`，
   所以**一周 5 天 `Schedule == null`**，靠配偶行为系统待在家里。
3. **节日当天所有 NPC 被 `ClearSchedule()`**（留下 `Schedule=null` + `dayScheduleName=""`
   + `followSchedule=false` 的三件套签名）。本 mod 在节日当天**拒绝改行程**，
   改为在提示词里告诉她今天有节日，让她在对话中拒绝邀约。

### 其他坑

- `StardewValley.Schedule` 类在 1.6 **已被移除**；`SchedulePathDescription` 搬到了
  `StardewValley.Pathfinding`。老教程的 `Schedule.FromAsset` 编译不过。
- `NPC.Schedule` 是 `{ get; private set; }`，只能经 `TryLoadSchedule(key, dict)` 写入。
- **`Schedule` 不落盘**（`[XmlIgnore]`），只在过夜和读档时从资产重建。跨天承诺必须自己持久化。
- 不要用 `ClearSchedule()` 做临时禁用——它会把 `followSchedule=false` **写进存档**。
  用 `ignoreScheduleToday`（`[XmlIgnore]`，不落盘）。
- SMAPI 的 save data 键**不允许 `/`**，否则 `WriteSaveData` 静默失败。

---

## 安装

1. 需要 **SMAPI 4.x** 和 **ValleyTalk 1.4+**。
2. 把 `ValleyTalkMemory` 文件夹放进 `Stardew Valley/Mods/`。
3. 启动游戏，首次运行会生成 `config.json`。

LLM 连接信息**留空即复用 ValleyTalk 的 `config.json`**。

## 配置要点

```jsonc
{
  "EnabledNpcs": "Emily",        // 只有她启用；"" 或 "*" = 全部 NPC
  "IncludeOverheard": false,     // 不听别人的事——NPC 没理由知道别人的安排
  "DaysPerWeek": 7,
  "ShortTermBudgetChars": 3000,
  "LongTermBudgetChars": 6000,
  "AutoBackfillExistingHistory": true,   // 首次读档回填存量周（一次性 API 开销）
  "InjectSelfKnowledge": true,           // 注入"当前情况"（时间/节日）
  "CompressOnWeekEnd": true
}
```

## 游戏内命令

| 命令 | 作用 |
|---|---|
| `vtmemory_show <npc>` | 打印**实际注入**的那段提示词文本 |
| `vtmemory_status [npc]` | 记忆统计 |
| `vtmemory_names` | 诊断存档键 → 真实 NPC 名 的映射 |
| `vtmemory_schedule <npc>` | 打印她今天剩余的时间表 |
| `vtmemory_goto <npc> <地点> <时刻> [回家时刻] [x y]` | 手动改行程（引擎层测试入口） |
| `vtmemory_backfill <npc\|all>` | 回填存量周 |
| `vtmemory_reset <npc\|all>` | 清空记忆 |

## 构建

本机 NuGet 不可达，因此 csproj 走**全本地引用**：直接引用游戏目录自带的 .NET 6 程序集与
SMAPI / 游戏 DLL，不下载任何包。改动 `GamePath` 后：

```powershell
dotnet build src/ValleyTalkMemory/ValleyTalkMemory.csproj
```

## 目录

- `src/ValleyTalkMemory/` — mod 源码
- `docs/schedule-engine-notes.md` — 行程系统的完整技术调研（含逐字 API 签名）
- `docs/development-notes.md` — 开发过程记录：踩过的坑、验证方法与实测证据

## 已知限制

- 节日当天不改行程（引擎会清空所有 NPC 日程）；改为让她在对话中拒绝。
- 已婚配偶的"回家"止于公交站，之后由游戏/配偶逻辑带她进屋。
- LLM 承诺抽取（工具调用层）尚未完成。

## 致谢

- [ValleyTalk](https://github.com/dandm1/ValleyTalk) by dandm1 — 本 mod 建立在它的 API 之上
- SMAPI 与 Stardew Valley 模组社区

---

## 给另一个角色启用（例如海莉 Haley）

**不需要改任何代码。** 整套逻辑是角色无关的：记忆读取、名字解析、周压缩、承诺抽取、行程注入
都以 NPC 名为参数。让海莉"活起来"只需要动配置。

### 最小步骤

1. **把她加进 `EnabledNpcs`**（逗号分隔；留空或 `*` = 所有 ValleyTalk 覆盖的角色）：

```jsonc
"EnabledNpcs": "Emily,Haley"
```

2. **重启游戏读档。** 启动时会自动完成：

   - `NpcNameResolver` 把存档里 `eventhistory_haley` 这类小写键还原成真实名 `Haley`；
   - 从 ValleyTalk 历史里播种她的原始对话；
   - 派生她的短期记忆（本周带时间戳的原文）；
   - 注册她的 `EventHistory` 提示词覆盖。

   日志应出现：`Name mapping: N/N ValleyTalk history keys resolved`（数字变大），
   `Save loaded: memory for 2 NPCs`，`[D] ValleyTalk is holding an 'EventHistory' override for 2 characters`。

3. **回填她的存量周**（一次性开销）。`AutoBackfillExistingHistory: true` 时首次读档会自动排队，
   日志形如 `Back-filling Haley: 231 lines across 7 finished week(s) — this costs 7 LLM call(s)`。
   想手动控制就把它设为 `false`，再用 `vtmemory_backfill Haley`。

4. **验证**：

```
vtmemory_names                 ← 确认 haley -> Haley 已解析
vtmemory_status Haley          ← 看 raw / 本周 / 长期周数
vtmemory_show Haley            ← 看实际注入的提示词
vtmemory_goto Haley Beach 14:00 ← 手动确认路线走得通
```

做完这四步，她就和艾米丽一样有记忆、有自我认知（知道自己今天的安排）、能通过对话改行程。

### 成本与容量

| 项目 | 每增加一个角色 |
|---|---|
| 回填 | 1 次 LLM 调用 / 每个有对话的已完成周（首次一次性） |
| 每周压缩 | 1 次 LLM 调用 / 周 |
| 承诺抽取 | 仅在她说了带时间承诺的话时触发，闲聊不调用 |
| 提示词长度 | 每个角色独立注入，只有当前对话角色的记忆进入提示词，**不会互相叠加** |
| 存档 | `vtmemory_<name>` 一条；磁盘镜像 `memory-<存档名>.json` 共享一个文件 |

把 `EnabledNpcs` 设成 `*` 会立刻为存档里所有 ValleyTalk 角色排队回填 —— 那是几十到上百次调用，
建议逐个加、确认没问题再扩。

### 各角色会遇到的差异

- **已婚配偶**（艾米丽、海莉等）：键链只查 `marriage_*`，**一周只有 1-2 天有原生日程**，
  其余日子 `Schedule == null`（靠配偶行为系统待在家）。本 mod 会从零给她装一份日程，
  并且**从农场出发的路线走不通**（引擎限制），所以会自动先传送到公交站再走 —— 这是原版行为。
- **未婚角色**：从自己当前位置直接寻路，一般不需要传送。
- **自定义/模组 NPC**：名字解析走 `ValleyTalk._characters` + `Game1.locations`，通常能自动覆盖；
  若 `vtmemory_names` 显示 `UNRESOLVED`，把该 NPC 的名字告诉我加进解析来源即可。
- **地点别名表**在 `PromiseExtractor.cs` 的 `LocationCatalog.Aliases` 里。
  玩家提到的新说法（比如某个模组地图的中文称呼）在这里加一行：
  `["某个说法"] = "内部地图名"`。

### 不需要改的地方

- 短期/长期记忆的分层、周界对齐（28 天 = 4 整周）与预算裁剪
- 行级去重、对话控制符清洗、名字大小写还原
- 承诺抽取的工具契约与节流
- 跨存档隔离（记忆按存档的 `CustomData` 键存储，磁盘镜像按存档文件夹名校验）

---

## 后续路线图

后续设想、依赖顺序、每步的验证标准与风险评估见 [`docs/roadmap.md`](docs/roadmap.md)。

一句话概括顺序：**先把承诺持久化与次日重放做掉**（现在注入的行程不落盘，每天早晨被重建，
所以"明天去某地"到点不会发生），它同时是"自主日程"两个形态的地基；日程动作与人设增量可以并行；
角色联动最危险，建议先只做"公有事实"。
