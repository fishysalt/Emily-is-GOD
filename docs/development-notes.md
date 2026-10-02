# ValleyTalk Memory（艾米丽记忆扩展）

给 ValleyTalk 的 AI 对话加上**长短期记忆**的伴生 SMAPI mod。不改动 ValleyTalk 本体，随时可卸载回退。

---

## 1. 它解决什么问题

ValleyTalk 本身**已经有**一份原始对话历史，存在存档的 `Game1.CustomData` 里：

```
键：smapi/mod-data/dandm1.valleytalk/eventhistory_<npc>
值：{ "EventHistory":[], "OverheardHistory":[], "DialogueHistory":[
        { "Item1": {"season":"Spring","dayOfMonth":1,"timeOfDay":630,"year":1},
          "Item2": {"Dialogues":[{"Text":"..."}]} }, ... ] }
```

但它**会遗忘**。反编译 `Character.cs:746` 的 `EventHistorySample()`：

```csharp
_historyCutoff = source3.OrderBy(x => x.Item1).TakeLast(20).FirstOrDefault()?.Item1;
return source3.Where(x => x.Item1.After(_historyCutoff))...;
```

也就是说：**只取最新 20 条**作为时间下限，再叠加 `Prompts.cs` 里 4000 字符的预算。
**第 21 条往前的记忆，LLM 永远看不到了**——原始记录还躺在存档里，但再也不会进入提示词。

本 mod 补的就是这个洞：

| | 来源 | 时间尺度 | 形式 |
|---|---|---|---|
| 短期记忆 | 本 mod 保留 | 当前游戏内周 | 原文 + 游戏内日期时间戳 |
| 长期记忆 | LLM 每周压缩 | 所有更早的周 | 摘要 + 关键事实 + 承诺与约定 |

星露谷一年 = 4 季 × 28 天 = 16 周，**28 天正好是 4 个整周**，所以周界天然对齐：
D1-7 / D8-14 / D15-21 / D22-28，跨季不产生残周。

## 2. 注入方式（官方 API，不碰本体）

ValleyTalk 对外暴露了 mod API（`ValleyTalk.IValleyTalkInterface`）：

```csharp
void RegisterPromptOverride(string characterName, string promptElement, string overrideText);
```

`EventHistory` 是 24 个可覆盖的提示词元素之一（`Prompts.cs:315`）。`DefaultOrOverride()` 的语义是
**有覆盖则跳过默认实现，直接把覆盖文本逐条追加**。因此本 mod 覆盖 `EventHistory`，
输出「长这样」的一段（并复用 ValleyTalk 自己的多语言标题，所以中文环境下标题也是中文）：

```
## Event history:
<ValleyTalk 原文的 eventHistoryIntro>
History:
### 长期记忆（每周压缩，较早发生的事）
- [Y1 Fall D8 ~ Y1 Fall D14] ……摘要……（关系：……）
  - 关键事实：……；……
  - 承诺与约定：……
### 短期记忆（本周内，带游戏内日期时间）
- [Y1 Fall D19 06:00] Emily: 今天我打算去镇上逛逛，然后去酒吧帮忙。
- [Y1 Fall D19 07:10] 农夫: 我种了苹果树，未来咱们能吃苹果派
- [Y1 Fall D19 08:00] 旁听到 Haley 说：……
以上记忆按时间先后排列……
```

## 3. 关键实现细节（踩过的坑）

1. **存档键是 `emily`，角色名是 `Emily`。**
   ValleyTalk 用 `Helper.Data.WriteSaveData("EventHistory_" + GetSaveName(name))` 落盘，
   而 SMAPI 会把 save data 键**全部小写化**；但 `RegisterPromptOverride` 是按 `character.Name`
   （即 `NPC.Name` = `Emily`）精确匹配的字典键（`ModInteropManager` 用默认大小写敏感比较器）。
   直接用存档键注册会**静默失效**。`NpcNameResolver` 按优先级从四个来源建映射：

   | 来源 | 何时可用 | 说明 |
   |---|---|---|
   | `ValleyTalk.DialogueBuilder._characters`（反射） | 读档后 ValleyTalk 首次 patch NPC 起 | **最权威**：这正是覆盖键所用的同一个集合，连 CustomCompanions 伙伴这类非标准角色也覆盖 |
   | `Game1.characterData` | 标题画面即可 | ValleyTalk `PopulateCharacters()` 迭代的同一集合，实测 212 条 |
   | `Game1.locations[].characters` | 读档后 | 当前地图上真实存在的 NPC |
   | `Data/Characters` 资产 | 标题画面即可 | 兜底（注意它是 `Dictionary<string, CharacterData>`，**不是** `<string,string>`） |

   实测 212 个角色名中 75/77 个历史键可解析；剩余 2 个（`daphne`、`sbvraccoon`）
   在标题画面测量时尚未出现在上述来源中，读档后由 `_characters` / 实时 NPC 扫描补齐（机制自愈）。
   其中 `SBVRaccoon` 是 Sunberry Village 的 **CustomCompanions 伙伴**，压根不在 `Data/Characters` 里。

2. **`MemoryStore` 的大小写陷阱。**
   内部字典用 `StringComparer.OrdinalIgnoreCase`，所以 `emily` 和 `Emily` 是**同一个槽位**，
   但字典里实际存的键字符串仍是**先插入的那个**。若先用 `emily` 写入，之后 `RefreshAll()`
   遍历键注册覆盖时用的还是 `emily` —— 又失效。因此 `NpcMemory.Npc` 单独保存规范名，
   注册与落盘一律用 `memory.Npc`，不用字典键。

3. **读实时历史要靠反射。**
   ValleyTalk 只在 `GameLoop.Saving` 时才把历史写进 `Game1.CustomData`，
   所以整个游戏过程中读存档数据都是**上一次存档的旧值**。
   本 mod 反射读取 `ValleyTalk.EventHistoryReader.Instance._saveCache`（私有字段）拿实时数据，
   存档数据只用于进入存档时的初始化。反射失败时降级为「只有旧数据」，不会崩。

4. **事件按"行"而不是按"条目"存储。**
   ValleyTalk 会把同一句台词同时记进 `DialogueHistory` 和 `ConversationHistory`，
   按条目存会出现大量重复。改成一行一个事件后，去重键 `日|时间|文本` 能自然折叠重复
   （实测 1647 行 → 去重后 1513 行）。

5. **要清洗星露谷对话标记。**
   生成的行会带 `#$b#` 分页符、`$q`/`$r` 应答选项块（一整段 UI 定义）、`$h` 情绪符、
   `[物品id]` 礼物选项。不清洗既污染记忆又浪费字符预算。

6. **压缩必须串行。**
   `vtmemory_backfill all` 可能一次排队几百个周。
   压缩器用单后台工作线程逐个处理，否则会瞬间并发几百个 API 请求。

7. **轮询要有指纹。** ValleyTalk 的实时缓存里每个 NPC 都是**全量历史 JSON**，
   每 2 秒重解析全部 77 个 NPC 纯属浪费。改为对 JSON 取指纹，不变则跳过；
   轮询间隔 300 tick（≈10 秒）。

8. **空壳记录**：`{"ConversationElements":[]}` 这类无内容条目会被跳过（实测 Emily 有 23 条），
   属正常现象，不是解析失败。

## 3.5 已验证到什么程度

**已在真实游戏中启动验证**（启动到标题画面 + 自检，共 5 轮，全程未触碰存档）：

```
[1/4] name resolver: 212 keys (Game1.characterData=212, liveLocations=0, Data/Characters=212)
      'emily' -> 'Emily' OK      'haley' -> 'Haley' OK
      'morris' -> 'Morris' OK    'adelaiderosiervmv' -> 'AdelaideRosierVMV' OK
[2/4] ValleyTalk/Prompts: 862 strings loaded.
[3/4] RegisterPromptOverride: OK   ClearPromptOverride: OK   ClearPromptOverrides: OK
[4/4] Game1.CustomData reachable: True
```

即：mod 能加载、ValleyTalk 的 mod API 本地接口代理三个方法全部连通、
LLM 连接信息能从 ValleyTalk 的 config.json 自动读出、提示词字符串资产能加载、
`Game1.CustomData` 可读、名字解析可用、**零 ERROR**。

**尚未验证**（需要真正读档进入游戏）：读到具体存档的 77 个 NPC 后覆盖是否按预期生效、
以及艾米丽是否真能主动提起一周前的事。启动自检由 `SelfTestOnStartup` 控制（默认开）。

6. **空壳记录**：`{"ConversationElements":[]}` 这类无内容条目会被跳过（实测 Emily 有 23 条），
   属正常现象，不是解析失败。

## 4. 构建方式（本机环境特殊）

本机 **NuGet 被本地代理打断（TLS EOF）**，`Pathoschild.Stardew.ModBuildConfig` 装不上。
因此 `ValleyTalkMemory.csproj` 走**全本地引用**：直接引用游戏目录自带的 .NET 6 程序集
（`System.Private.*.dll`、`netstandard.dll`、`mscorlib.dll`）与 SMAPI/游戏 DLL，
`DisableImplicitFrameworkReferences=true`，不下载任何包。

```powershell
dotnet build E:\SDV_Dev\ValleyTalkMemory\ValleyTalkMemory.csproj
# 构建后自动部署到 <游戏>\Mods\ValleyTalkMemory
```

离线测试台（不需要开游戏，直接读真实存档验证解析与拼装）：

```powershell
dotnet build E:\SDV_Dev\ParserTest\ParserTest.csproj
dotnet E:\SDV_Dev\ParserTest\bin\Debug\net6.0\ParserTest.dll
# 输出：E:\SDV_Dev\ParserTest\result.txt
```

## 5. 游戏内命令

| 命令 | 作用 |
|---|---|
| `vtmemory_names` | 诊断：历史键 → 真实 NPC 名 的映射，标出未解析的键 |
| `vtmemory_status [npc]` | 每个 NPC 的 raw / 本周 / 长期周数 / 压缩进度 |
| `vtmemory_dump <npc> [short]` | 打印已存记忆原文 |
| `vtmemory_show <npc>` | 打印**实际注入提示词**的那段文本（最直观） |
| `vtmemory_reload` | 重读 ValleyTalk 历史并刷新全部覆盖 |
| `vtmemory_compress <npc\|all>` | 强制压缩已完成但未压缩的周 |
| `vtmemory_backfill <npc\|all>` | 把 ValleyTalk 存量历史全部按周回填压缩（**消耗 API 额度**） |
| `vtmemory_reset <npc\|all>` | 清空本 mod 的记忆 |

## 6. 配置

首次启动生成 `Mods\ValleyTalkMemory\config.json`。
LLM 连接信息**留空即复用 ValleyTalk 的 `config.json`**（读取 `ServerAddress` / `ApiKey` / `ModelName`）。

常用项：

```jsonc
{
  "EnableMod": true,
  "DebugLogging": true,
  "DebugDumpMemoryFiles": true,   // 把注入文本写到 Mods\ValleyTalkMemory\debug\memory_<npc>.txt
  "DaysPerWeek": 7,
  "ShortTermBudgetChars": 3000,
  "LongTermBudgetChars": 2600,
  "MaxWeeklySummaries": 20,
  "IncludeOverheard": true,
  "MemoryLanguage": "简体中文",
  "CompressOnWeekEnd": true,
  "MaxCompressionsPerDay": 30,
  "QueryTimeoutSeconds": 90
}
```

## 7. 下一步：让 NPC 落实自己说的行程

`WeeklyMemory.Promises` 已经在按周抽取「该 NPC 说过自己打算做/答应做的事」，
这就是行程功能的原料。落地需要接 SMAPI 的日程系统，要点（待细化）：

- 日程数据资产 `Characters/schedules/<NPC名>`，运行时对象是 `NPC.Schedule`
  （`Dictionary<int, SchedulePathDescription>`），`NPC.TryLoadSchedule()` / `NPC.checkSchedule`。
- 1.6 起支持 `<星期>_<心数>` 形式的日程键（见 Stardew Valley Wiki: Modding:Schedule data）。
- ValleyTalk 自己已经在读当天剩余行程（`locationFuturePlans`：
  "Later today, {{Name}} is planning to go to: {{Locations}}"），可复用它的路径。
- 需要决定的是「临时日程」（只改当天、次日恢复）还是「持久日程」（写回存档）。

---

## 8. 端到端实机验证结果（读档实测）

在不依赖人工点击的前提下，用 `DevAutoLoadSave` 让 mod 在标题画面自己调 `SaveGame.Load(slot)` +
`Game1.exitActiveMenu()`（与 `LoadGameMenu.SaveFileSlot.Activate()` 完全一致的两行），
读入存档 `理塘_415334506`，跑完验证后关闭进程。**读档前后存档 6 个文件的 SHA256 全部一致，零写入。**

真实 SMAPI 日志（节选）：

```
Name mapping: 75/77 ValleyTalk history keys resolved to real NPC names.
Save loaded: memory for 77 NPCs, 77 refreshed from saved ValleyTalk history.

--- post-load verification ---
[A] name resolver after load: 246 keys (ValleyTalk._characters=189, Game1.characterData=212,
                                liveLocations=1869, Data/Characters=212)
[B] memory store: 77 NPCs, 0 with long-term weeks, 1513 raw events total.
      Emily: raw=365, weeks=0, compressedThrough=-1
[C] Emily: memory block is 3815 chars, 58 lines.
      | ## Event history:
      | Emily is aware of the following recent events and conversations with the farmer.
      | ...
      | - [Y1 Fall D19 06:00] Emily: 今天我打算去镇上逛逛，然后去酒吧帮忙。祝你有美好的一天。
      | 以上记忆按时间先后排列，是你亲身经历或亲耳听到的……
[D] ValleyTalk is holding an 'EventHistory' override for 77 characters.
      Emily: override present, will be used for the next prompt
```

`[D]` 是决定性的：它**反射读取 ValleyTalk 自己的私有覆盖表** `ModInteropManager._promptOverrides`，
确认我们的文本确实落在 `["Emily"]["EventHistory"]` 这个 ValleyTalk 构建提示词时会查的**同一个键**上。
整条链路 记忆构建 → 覆盖注册 → 落入 ValleyTalk 存储 已验证贯通。

另外，读档运行期间 `memory_Emily.txt` 在 22:43:43 与 22:44:52 被写了两次，
第二次多出 `[Y1 Fall D20 06:20]` 的新对白 —— 说明**实时历史轮询（反射 `EventHistoryReader._saveCache`）也在工作**。

### 顺带修掉的一个真实缺陷

`[A]` 显示读档后名字表从 212 涨到 246（`ValleyTalk._characters` 从 0 变成 189），
但这个名字表重建发生在覆盖注册**之后**，没人回头补注册。后果是 `daphne` / `sbvraccoon`
两个 NPC 的记忆一直挂在小写键上（debug 目录里只有这两个是 `memory_daphne.txt` 这种小写文件名），
ValleyTalk 永远不会查它们。已加入 `ReconcileNames()` 补救：读档完成后与每天开始时
重新解析已存记忆的名字并重注册覆盖，`_byKey` 是大小写不敏感查找，所以一旦该 NPC 出现即可自愈。

### 仍未验证的唯一环节

**NPC 真正开口那一轮**：需要玩家与艾米丽对话，ValleyTalk 才会构建提示词。
上游全部已验证（覆盖已在表中、且位于正确的键上），下游 `DefaultOrOverride` 的消费逻辑
已从反编译源码确认。这一步只能靠人工。

### 开发开关（默认关闭，勿在正常游玩时开启）

| 配置项 | 默认 | 作用 |
|---|---|---|
| `SelfTestOnStartup` | true | 标题画面打一次启动自检 |
| `DevAutoLoadSave` | `""` | **填存档文件夹名会让 mod 自动读档**，仅用于无人值守验证 |
| `DevVerifyAfterLoad` | true | 读档约 5 秒后输出 `[A]~[D]` 验证报告 |
---

## 9. 手动测试暴露的两个问题（已修）

### 9.1 致命：存档键含 `/`，长期记忆根本存不进去

```
[22:58:50 WARN ValleyTalk Memory] Failed reading memory index:
  The data key is invalid (keys must only contain letters, numbers, underscores, periods, or hyphens).
```

`MemoryStore` 原本用 `vtmemory/_index` 作键。SMAPI 的 save data 键**不允许 `/`**，
于是 `WriteSaveData` / `ReadSaveData` 全部失败——短期记忆不受影响（每次从 ValleyTalk 历史重建），
但 **LLM 压缩出来的长期记忆每次退出即丢**。已改为 `vtmemory_index` / `vtmemory_<npc>`。

这个 bug 只在真正存档时才会暴露，且会被 `try/catch` 吞成一条 WARN，**不会崩、不会报错到表面**，
是典型的"功能静默失效"。所以 `SelfTestOnStartup` 那几条检查值得保留。

### 9.2 同分钟重复行浪费预算

ValleyTalk 会把同一轮对话既按单句存进 `DialogueHistory`，又按整段拼接存进 `ConversationHistory`。
精确匹配去重看不见这种包含关系，于是提示词预算被同一句话重复吃掉。
已加 `MemoryMerge.DropCoveredLines`：同一游戏分钟内，若一行文本被另一行**完整包含**则折叠掉
（只比较同 `Kind` 的行，所以农夫的话不会被 NPC 的话吞掉）。实测 Emily 347 → 340 行。

### 9.3 现状提醒：`短期记忆（本周内）` 目前是名义上的

`compressedThrough = -1` 表示一次压缩都没跑过，因此**全部 340 条都被当作"未压缩"**，
只能靠 3000 字符预算截取最新一段——所以标题写着"本周内"，内容却从 D12 开始。
跑一次 `vtmemory_backfill Emily`（或自然跨过一周）后，更早的周会折叠进长期记忆，
短期块才会变成真正的"本周"。