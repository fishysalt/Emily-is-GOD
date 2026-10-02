# Stardew Valley 1.6 / SMAPI 4.x — NPC 行程（Schedule）运行时 API 技术简报

环境：Stardew Valley 1.6.15（`Stardew Valley.dll` 6,268,416 B）、SMAPI 4.3.2（`StardewModdingAPI.dll` 1,019,392 B）。
验证方法：`Stardew Valley.xml` / `StardewModdingAPI.xml` / `StardewValley.GameData.xml` 成员签名 + 用 `ilspycmd 9.1.0` 反编译 `Stardew Valley.dll`（948 个 .cs）、`StardewModdingAPI.dll`、以及真实存档 XML 交叉验证。所有签名均为**逐字拷贝**，未反编译/未在文档中找到的成员会被明确标注为"不存在/未确认"。

---

## 0. 关键结论速览（先看这个）

| 问题 | 结论 |
| --- | --- |
| 行程运行时类型 | `Dictionary<int, StardewValley.Pathfinding.SchedulePathDescription>` |
| `StardewValley.Schedule` 类 | **1.6 已不存在**（全 948 个反编译文件里没有任何 `class Schedule` / `struct Schedule`；XML 文档中也没有该类型） |
| `NPC.getSchedule` / `NPC.TryGetSchedule` | **不存在**（详见 §1.1） |
| 资产键 | `Characters/schedules/<NPC internal name>`，类型 `Dictionary<string,string>`，**未本地化** |
| 单日覆盖最佳做法 | 编辑该资产 + `helper.GameContent.InvalidateCache(...)`（SMAPI 会自动重载并立即重新触发 `checkSchedule`） |
| `NPC.Schedule` 是否存档 | **否**。`[XmlIgnore]`，且不在 `NetFields` 中；真实存档里 `<Schedule>` 出现 0 次 |
| 什么会被存档 | `NPC.ScheduleKey`（`dayScheduleName` NetString）与 `NPC.followSchedule`。真实存档中 `<dayScheduleName>` 出现 159 次、`<followSchedule>` 出现 219 次，例如 `<dayScheduleName>Mon</dayScheduleName>` |
| 行程何时被重建 | ① 过夜 `NPC.dayUpdate` → `resetForNewDay` → `TryLoadSchedule()`；② 读档 `SaveGame.loadDataToLocations` → `npc.reloadSprite()` → `TryLoadSchedule()`（只在 `Game1.gameMode == 6` 时生效） |
| `<<` / `>>` 分支语法 | **不存在**。解析器只认 `GOTO` / `NOT friendship` / `MAIL` / `a` 时间前缀 |

---

## 1. 运行时行程表示（精确签名）

### 1.1 明确不存在的成员（重要，避免臆造）

我在全量反编译中搜索过、确认**不存在**的成员：

| 搜索项 | 结果 |
| --- | --- |
| `StardewValley.Schedule`（类/结构） | 不存在（`(class\|struct)\s+Schedule(\s\|$\|\{)` 全库 0 命中） |
| `Schedule.FromAsset(...)` | 不存在 |
| `NPC.getSchedule(...)` | 不存在 |
| `NPC.TryGetSchedule(...)` | 不存在 |
| `NPC.checkSchedule(int, GameLocation)` 重载 | 不存在（只有一个 `checkSchedule(int)`） |
| `NPC.Schedule` 的 public setter | 不存在（`{ get; private set; }`，加 `[XmlIgnore]`） |
| `NPC.moveTowardPlayer`（村民走向玩家） | `NPC.moveTowardPlayer(int threshold)` **存在**，但它只是切换"靠近玩家阈值"的怪物式 AI 开关，**不是路径寻路到玩家**：`{ isWalkingTowardPlayer.Value = true; moveTowardPlayerThreshold.Value = threshold; }` |
| XML 文档中的 `NPC.checkSchedule` | `.xml` 中 0 命中 → 它虽为 `public virtual` 但**没有官方文档**（属未文档化的公开 API） |

另外，`StardewValley.GameData.Characters.CharacterData` 的**全部**字段（63 个）中**没有**任何行程相关字段（只有 `Home`、`HomeRegion`、`FestivalVanillaActorIndex` 等）。**行程只存在于 `Characters/schedules/<Name>` 资产**。

### 1.2 `StardewValley.Pathfinding.SchedulePathDescription`

注意：**命名空间是 `StardewValley.Pathfinding`，不是 `StardewValley`**。这是 1.6 的搬迁，很多老教程里的 `StardewValley.SchedulePathDescription` 会编译失败。

```csharp
// 反编译自 Stardew Valley.dll，逐字
namespace StardewValley.Pathfinding;

public class SchedulePathDescription
{
    public Stack<Point> route;
    public int time;
    public int facingDirection;
    public string endOfRouteBehavior;
    public string endOfRouteMessage;
    public string targetLocationName;
    public Point targetTile;

    public SchedulePathDescription(Stack<Point> route, int facingDirection, string endBehavior,
                                   string endMessage, string targetLocationName, Point targetTile)
    {
        endOfRouteMessage = endMessage;
        this.route = route;
        this.facingDirection = facingDirection;
        endOfRouteBehavior = endBehavior;
        this.targetLocationName = targetLocationName;
        this.targetTile = targetTile;
    }
}
```

参数含义：

| 参数 | 含义 |
| --- | --- |
| `route` | 走位栈 `Stack<Point>`（瓦片坐标）。栈顶 = 下一个目标瓦片；`Stack.Peek()` 是"即将踏上的格子" |
| `facingDirection` | 抵达后朝向：0=上, 1=右, 2=下, 3=左。`-1` 表示不改朝向 |
| `endBehavior` | 抵达后播放的动画键，取自 `Data/animationDescriptions`；也可以是 `square_X_Y_facing` / `change_beach` / `change_normal`；**空字符串或 `null` = 无行为** |
| `endMessage` | 抵达后说的台词，格式 `"Strings\\schedules\\Abigail:Sun.000"`（含引号语义，见 §2 注意点） |
| `targetLocationName` | 目标地点**内部名**（如 `"Saloon"`），不是显示名 |
| `targetTile` | 目标瓦片 `Point` |

**⚠ 构造函数不赋值 `time`。** `time` 是公开字段，必须由调用方另外设置（游戏自己在 `parseMasterScheduleImpl` 里用 `schedulePathDescription.time = num5;` 补上）。语义是**出发时间**（除非用了 `a` 前缀，见 §2）。

### 1.3 `StardewValley.NPC` 相关成员

以下签名来自 `Stardew Valley.xml`（有文档的部分逐字拷贝）与反编译代码（无文档的部分）。

```csharp
// 属性（XML 文档原文）
/// <summary>The schedule of this NPC's movements and actions today, if loaded.
///          The key is the time of departure, and the value is a list of directions to reach the new position.</summary>
/// <remarks>You can set the schedule using TryLoadSchedule or one of its overloads.</remarks>
[XmlIgnore]
public Dictionary<int, SchedulePathDescription> Schedule { get; private set; }   // ← private set！外部不能直接赋

/// <summary>The Schedule's key in the original data asset, if loaded.</summary>
[XmlIgnore]
public string ScheduleKey => dayScheduleName.Value;

// 存档字段（NetString，会被序列化）
[XmlElement("dayScheduleName")]
public readonly NetString dayScheduleName = new NetString();
[XmlElement("islandScheduleName")]
public readonly NetString islandScheduleName = new NetString();

// TryLoadSchedule 四个重载（两处 XML 文档未写返回类型，反编译确认为 bool）
public bool TryLoadSchedule();
/// <param name="key">The key for the schedule to load.</param>
public bool TryLoadSchedule(string key);
/// <summary>Try to load a raw schedule script, or disable the schedule if it's invalid.</summary>
/// <param name="key">The schedule's key in the data asset.</param>
/// <param name="rawSchedule">The schedule script to load.</param>
public bool TryLoadSchedule(string key, string rawSchedule);
/// <summary>Try to load raw schedule data, or disable the schedule if it's invalid.</summary>
/// <param name="key">The schedule's key in the data asset.</param>
/// <param name="schedule">The schedule data to load.</param>
public bool TryLoadSchedule(string key, Dictionary<int, SchedulePathDescription> schedule);

/// <summary>Disable the schedule for today.</summary>
public void ClearSchedule();

// —— 以下是 public 但 XML 文档中「没有」的成员（全部来自反编译） ——

public virtual void checkSchedule(int timeOfDay);           // 无 XML 文档
public virtual Dictionary<int, SchedulePathDescription> parseMasterSchedule(string scheduleKey, string rawData);
protected virtual Dictionary<int, SchedulePathDescription> parseMasterScheduleImpl(string scheduleKey, string rawData, List<string> visited);
public static string[] SplitScheduleCommands(string rawScript);   // 按 '/' 切分并 Trim/RemoveEmptyEntries
public virtual void InvalidateMasterSchedule();             // 仅 _hasLoadedMasterScheduleData = false
public Dictionary<string, string> getMasterScheduleRawData();
public string getMasterScheduleEntry(string schedule_key);
public bool hasMasterScheduleEntry(string key);
public virtual SchedulePathDescription pathfindToNextScheduleLocation(
    string scheduleKey, string startingLocation, int startingX, int startingY,
    string endingLocation, int endingX, int endingY,
    int finalFacingDirection, string endBehavior, string endMessage);

// 公开字段（可直接读写，且都是 [XmlIgnore]）
[XmlIgnore] public List<SchedulePathDescription> queuedSchedulePaths = new List<SchedulePathDescription>();
[XmlIgnore] public int lastAttemptedSchedule = -1;
[XmlIgnore] public bool ignoreScheduleToday;    // 有 [XmlIgnore]（NPC.cs:227）→ 不落盘
public bool followSchedule = true;              // ★ 无 [XmlIgnore] → 会落盘（真实存档 219 次）
public float currentScheduleDelay;
public float scheduleDelaySeconds;
public PathFindController temporaryController;                 // 注意：controller 在基类 Character 上
public SchedulePathDescription DirectionsToNewLocation { get; set; }   // [XmlIgnore]，public get/set

// 继承自 StardewValley.Character
public PathFindController controller;
```

`TryLoadSchedule` 各重载的确切行为（反编译）：

```csharp
public bool TryLoadSchedule(string key, Dictionary<int, SchedulePathDescription> schedule)
{
    if (schedule == null) { ClearSchedule(); return false; }
    Schedule = schedule;
    if (Game1.IsMasterGame)
        dayScheduleName.Value = key;      // ← 会写进存档的 <dayScheduleName>
    followSchedule = true;
    return true;
}

public void ClearSchedule()
{
    Schedule = null;
    if (Game1.IsMasterGame)
        dayScheduleName.Value = null;
    followSchedule = false;
}
```

`TryLoadSchedule()`（无参）是**完整的关键字解析链**，顺序严格如下（决定了你该覆盖哪个 key，见 §3）：

```
无原始数据 → ClearSchedule(), false
GreenRain（仅第 1 年绿雨）
islandScheduleName（非空则直接用当前 Schedule，不再解析）
[已婚]  marriage_<被动节日>_<天数> → marriage_<被动节日> → marriage_<季节>_<日> 
        → (Penny: Tue/Wed/Fri | Maru,Harvey: Tue/Thu → marriageJob) → marriage_<星期>（非雨天时）
[未婚]  <被动节日>_<天数> → <被动节日>
        → <季节>_<日> → <日>_<心数>（从高到低）→ <日>
        → (Pam + ccVault → "bus")
        → 若当前地点在下雨: rain2 (50%) → rain
        → <季节>_<星期>_<心数> → <季节>_<星期> → <星期>_<心数> → <星期>
        → <季节> → spring_<星期> → spring
        → ClearSchedule(), false
```

### 1.4 `StardewValley.Pathfinding.PathFindController`

```csharp
namespace StardewValley.Pathfinding;

/// This class finds a path from one point to another using the A* pathfinding algorithm.
/// Can only be used on maps where the tile width and height are each 127 or less.
[InstanceStatics]
public class PathFindController
{
    public delegate bool isAtEnd(PathNode currentNode, Point endPoint, GameLocation location, Character c);
    public delegate void endBehavior(Character c, GameLocation location);

    public const byte impassable = byte.MaxValue;
    public const int timeToWaitBeforeCancelling = 5000;

    public GameLocation location;
    public Stack<Point> pathToEndPoint;
    public Point endPoint;
    public int finalFacingDirection;
    public int pausedTimer;
    public endBehavior endBehaviorFunction;
    public bool nonDestructivePathing;
    public bool allowPlayerPathingInEvent;
    public bool NPCSchedule;
    public int timerSinceLastCheckPoint;

    // 7 个构造函数，全部逐字
    public PathFindController(Character c, GameLocation location, Point endPoint, int finalFacingDirection);
    public PathFindController(Character c, GameLocation location, Point endPoint, int finalFacingDirection, endBehavior endBehaviorFunction);
    public PathFindController(Character c, GameLocation location, Point endPoint, int finalFacingDirection, endBehavior endBehaviorFunction, int limit);
    public PathFindController(Character c, GameLocation location, Point endPoint, int finalFacingDirection, bool clearMarriageDialogues = true);
    public PathFindController(Stack<Point> pathToEndPoint, GameLocation location, Character c, Point endPoint);
    public PathFindController(Stack<Point> pathToEndPoint, Character c, GameLocation l);   // ← 内部 NPCSchedule = true
    public PathFindController(Character c, GameLocation location, isAtEnd endFunction, int finalFacingDirection,
                              endBehavior endBehaviorFunction, int limit, Point endPoint, bool clearMarriageDialogues = true);

    // 静态工具
    public static bool isAtEndPoint(PathNode currentNode, Point endPoint, GameLocation location, Character c);
    public static Stack<Point> findPath(Point startPoint, Point endPoint, isAtEnd endPointFunction,
                                        GameLocation location, Character character, int limit);
    public static Stack<Point> reconstructPath(PathNode finalNode);
    [Obsolete("Use findPathForNPCSchedules overload with 'npc' parameter.")]
    public static Stack<Point> findPathForNPCSchedules(Point startPoint, Point endPoint, GameLocation location, int limit);
    public static Stack<Point> findPathForNPCSchedules(Point startPoint, Point endPoint, GameLocation location, int limit, Character npc);

    // 实例
    public bool isPlayerPresent();
    public virtual bool update(GameTime time);
    public void handleWarps(Rectangle position);       // 跨地图切图的真正实现
    protected virtual void moveCharacter(GameTime time);
    public static int getPreferenceValueForTerrainType(GameLocation l, int x, int y);
}
```

两条实操要点（来自反编译）：

1. **同地图移动**：`new PathFindController(npc, location, tile, facing)` 构造时就会跑单图 A*（`findPath(c.TilePoint, endPoint, isAtEndPoint, location, character, limit)`，`limit` 默认 10000）。注意该构造里有 `if (!(character is NPC) && !isPlayerPresent() && ...) character.Position = ...` 的瞬移分支，NPC 不会走这个分支。
2. **跨地图移动**：必须走 3 参构造 `new PathFindController(Stack<Point> route, Character c, GameLocation l)`，它把 `NPCSchedule = true`；随后 `moveCharacter` 里的 `handleWarps(character.nextPosition(...))` 会在 NPC 踩到 warp/door 时自动切图、更新 `currentLocation`、并处理已婚 NPC 的 FarmHouse↔BusStop 重写。`route` 需要由 `NPC.pathfindToNextScheduleLocation(...)` 生成（它会用 `WarpPathfindingCache.GetLocationRoute` 把多个地图的路径和 warp 点缝成一个栈）。

### 1.5 行程何时被求值（精确调用点）

```csharp
// Game1.performTenMinuteClockUpdate() —— 每 10 游戏分钟，遍历「所有」Game1.locations
foreach (GameLocation location in locations)
    location.performTenMinuteUpdate(timeOfDay);

// GameLocation.performTenMinuteUpdate(int timeOfDay) 内
for (int j = 0; j < characters.Count; j++) {
    NPC nPC = characters[j];
    if (!nPC.IsInvisible) {
        nPC.checkSchedule(timeOfDay);           // ← 主驱动（全地图，与玩家是否在场无关）
        nPC.performTenMinuteUpdate(timeOfDay, this);
    }
}
```

另有：
- `Game1.addMinute()`（调试/加速）对**所有**地点的 NPC 调 `checkSchedule(timeOfDay)`。
- `Game1.addHour()` 会补调 `checkSchedule(timeOfDay - 50/60/70/80/90)`。
- `NPC.update` 里当 `currentScheduleDelay` 归零时调 `checkSchedule(Game1.timeOfDay)`；`temporaryController` 结束后也会调。

`checkSchedule(int timeOfDay)` 的完整逻辑（这段决定了"能不能立刻动"）：

```csharp
public virtual void checkSchedule(int timeOfDay)
{
    if (currentScheduleDelay == 0f && scheduleDelaySeconds > 0f) { currentScheduleDelay = scheduleDelaySeconds; return; } // 首次进入时设延迟
    if (returningToEndPoint) return;
    updatedDialogueYet = false;
    extraDialogueMessageToAddThisMorning = null;
    if (ignoreScheduleToday || Schedule == null) return;                       // ← 两个总闸
    SchedulePathDescription value = null;
    if (lastAttemptedSchedule < timeOfDay) {                                   // ← 关键闸门
        lastAttemptedSchedule = timeOfDay;
        Schedule.TryGetValue(timeOfDay, out value);                            // ← 精确查表，不找"最近的一个"
        if (value != null) queuedSchedulePaths.Add(value);
        value = null;
    }
    if (controller != null && controller.pathToEndPoint?.Count > 0) return;    // 正在走路就别打断
    if (queuedSchedulePaths.Count > 0 && timeOfDay >= queuedSchedulePaths[0].time)
        value = queuedSchedulePaths[0];
    if (value == null) return;
    prepareToDisembarkOnNewSchedulePath();
    if (!returningToEndPoint && temporaryController == null) {
        directionsToNewLocation = value;
        if (queuedSchedulePaths.Count > 0) queuedSchedulePaths.RemoveAt(0);
        controller = new PathFindController(directionsToNewLocation.route, this, Utility.getGameLocationOfCharacter(this)) {
            finalFacingDirection = directionsToNewLocation.facingDirection,
            endBehaviorFunction = getRouteEndBehaviorFunction(
                directionsToNewLocation.endOfRouteBehavior, directionsToNewLocation.endOfRouteMessage)
        };
        if (controller.pathToEndPoint == null || controller.pathToEndPoint.Count == 0) {
            controller.endBehaviorFunction?.Invoke(this, base.currentLocation);
            controller = null;
        }
        if (directionsToNewLocation?.route != null)
            previousEndPoint = directionsToNewLocation.route.LastOrDefault();
    }
}
```

**两个必须记住的闸门**：
- `Schedule.TryGetValue(timeOfDay)` 是**精确查表**。想立刻触发，传入的 `timeOfDay` 必须**正好等于** `Schedule` 里的某个 key。
- `lastAttemptedSchedule < timeOfDay` 只在时间前进时放行。若你在同一个 10 分钟刻度内（或时间没走）注入新行程，**必须先把它重置**（SMAPI 自己就设成 `0`）。

### 1.6 强制立刻重新求值 —— SMAPI 官方配方

`StardewModdingAPI.dll` → `CoreAssetPropagator.UpdateNpcSchedules(IAssetName)` 反编译原文（这是**游戏外最权威的"立刻重载行程"参考实现**）：

```csharp
private bool UpdateNpcSchedules(IAssetName assetName)
{
    string name = Path.GetFileName(assetName.BaseName);                 // "Characters/schedules/Abigail" → "Abigail"
    NPC[] array = GetCharacters().Where(npc => npc.Name == name && npc.IsVillager).ToArray();
    if (!array.Any()) return false;

    foreach (NPC nPC in array)
    {
        // 1) 清掉原始数据缓存（这两个是 protected 字段，SMAPI 用反射；mod 里可用 npc.InvalidateMasterSchedule()）
        Reflection.GetField<bool>(nPC, "_hasLoadedMasterScheduleData").SetValue(false);
        Reflection.GetField<Dictionary<string, string>>(nPC, "_masterScheduleData").SetValue(null);

        // 2) 按关键字链重新解析今天的行程
        nPC.TryLoadSchedule();

        // 3) 若今天已有「已到点」的条目，回到那个时间点重新触发
        if (nPC.Schedule != null)
        {
            int num2 = nPC.Schedule.Keys.Where(p => p <= Game1.timeOfDay).OrderByDescending(p => p).FirstOrDefault();
            if (num2 != 0)
            {
                nPC.queuedSchedulePaths.Clear();
                nPC.lastAttemptedSchedule = 0;
                nPC.checkSchedule(num2);
            }
        }
    }
    return true;
}
```

触发条件（同一个类里）：

```csharp
if (assetName.IsDirectlyUnderPath("Characters/schedules"))
    return flag | UpdateNpcSchedules(assetName);
```

也就是说：**只要 `Characters/schedules/<Name>` 被 invalidate，SMAPI 就会自动重载该 NPC 的行程并重新触发 `checkSchedule`。** 这意味着 §3 的资产方案不需要我们手写任何重新触发代码。

> 在 mod 中，等价的公开调用是 `npc.InvalidateMasterSchedule()`（`public virtual`，只把 `_hasLoadedMasterScheduleData` 置 false；`getMasterScheduleRawData()` 会在下次访问时重新 `Load` 并覆盖 `_masterScheduleData`，所以**不需要**反射去清 `_masterScheduleData`）。

---

## 2. 内容资产：键与格式（已由 wiki + 解析器双重确认）

**资产键**：`Characters/schedules/<NPC 内部名>`（例如 `Characters/schedules/Abigail`），**未本地化**（不是 `Data/...`，不走 `Strings` 本地化路径）。
**类型**：`Dictionary<string, string>`（key = 行程关键字，value = 斜杠分隔的脚本）。
源码佐证：`NPC.getMasterScheduleRawData()` 内 `string text = "Characters\\schedules\\" + base.Name;`，唯一例外是 Leo 且 `DefaultMap != "IslandHut"` 时用 `+"Mainland"`。
另有 `Utility.IsHospitalVisitDay(string character_name)` 同样直接 `Game1.content.Load<Dictionary<string,string>>("Characters\\schedules\\" + character_name)`。

**关键字语法**（顺序即优先级，取自 `NPC.TryLoadSchedule()` 反编译）：

| 语法 | 说明 |
| --- | --- |
| `GreenRain` | 第 1 年绿雨天 |
| `marriage_<festivalId>_<day>` / `marriage_<festivalId>` | 已婚 + 被动节日（夜市等） |
| `marriage_<season>_<dayOfMonth>` | 已婚 + 指定日期，如 `marriage_spring_26` |
| `marriageJob` | 仅 Penny(Tue/Wed/Fri)、Maru(Tue/Thu)、Harvey(Tue/Thu) |
| `marriage_<dayOfWeek>` | 已婚 + 星期（非雨天），如 `marriage_Mon` |
| `<festivalId>_<day>` / `<festivalId>` | 未婚 + 被动节日 |
| `<season>_<dayOfMonth>` | 如 `spring_15` |
| `<dayOfMonth>_<hearts>` | 如 `11_6`，取心数最高者 |
| `<dayOfMonth>` | 如 `16` |
| `bus` | 仅 Pam，且巴士已修复 |
| `rain2` | 雨天 50% 概率 |
| `rain` | 雨天 |
| `<season>_<dayOfWeek>_<hearts>` / `<season>_<dayOfWeek>` | 如 `spring_Mon_6` / `spring_Mon` |
| `<dayOfWeek>_<hearts>` / `<dayOfWeek>` | 如 `Mon_6` / `Mon` |
| `<season>` | 如 `spring` |
| `spring_<dayOfWeek>` | 任意季节的该星期 |
| `spring` | 永远匹配（**不能删**） |
| `default` | 某些回退场景使用，可缺省（缺省则用 `spring`） |

关键字**大小写不敏感**。

**脚本语法**：`SplitScheduleCommands` = `LegacyShims.SplitAndTrim(rawScript, '/', StringSplitOptions.RemoveEmptyEntries)`。
即先按 `/` 切分命令；每条命令再按空格切分：

`<time> [location] <tileX> <tileY> [facingDirection] [animation] [dialogue]`

**首个命令**（在第一个 `/` 之前的整段）可以是：

| 命令 | 解析器行为 |
| --- | --- |
| `GOTO <key>` | 递归加载 `<key>`。`GOTO season` → `Game1.currentSeason`；找不到则退回 `spring` |
| `NOT friendship <npc> <hearts> [<npc> <hearts> ...]` | 若任一玩家对任一列出 NPC 的心数 ≥ 指定值，则整段退回 `spring`；否则 `num++` |
| `MAIL <letterId>` | 未收到该信/世界状态 ID → 从下一条命令继续（`num+1`）；已收到 → 跳过一条（`num+2`） |
| （`NOT` 后面不是 `friendship`） | 忽略 |
| 之后（`array[num]`）若为 `GOTO <key>` | `GOTO no_schedule`（大小写不敏感）→ `followSchedule = false; return null;`；否则递归 |

**时间字段**：

- 正常：军事时间整数，如 `900`、`1900`。
- **`a` 前缀**：`a<time>` 表示"在该时刻**抵达**"。解析器会按路径长度反推出发时间（`num9 = round(walkHalfTime / (realMilliSecondsPerGameTenMinutes/1000*60)) * 10`，再 `max(..., 610)` 夹紧，且不早于上一条命令的时间）。
- **时间 `0`**：特殊语义 —— 这不是一条路径命令，而是"今日起点"。解析器会立刻 `faceDirection`、设置 `previousEndPoint`，并在 **`Game1.IsMasterGame` 时直接 `Game1.warpCharacter(this, text6, new Point(num2, num3))`**（即**瞬移**），然后 `continue`（不写入 `Schedule`）。**这是 `TryLoadSchedule(key, rawString)` 的重大副作用**。

**地点与坐标字段**：

- `location` 可省略。省略/`locationName` 能被 `int.TryParse` 时，表示"沿用上一个地图"，此时后面的字段依次被当成 `tileX`、`tileY`（解析器做 `num4--` 回退）。所以 `900 11 11` = 时间 900 + 当前地图 (11,11)。
- 地点默认值：`startLocation = isMarried() ? "BusStop" : defaultMap.Value`（已婚者从 BusStop 出发）。
- `bed` 特殊值：已婚 → 变成 `BusStop 9 23 3`；未婚 → 取 `default`（否则 `spring`）行程**最后一条**命令的地点/坐标，并尝试挂 `<小写名>_sleep` 动画。
- 不可达地点处理 `changeScheduleForLocationAccessibility`：`JojaMart` / `Railroad` 不可达 → 若存在 `<location>_Replacement` 条目则改用它的 `地点 X Y 朝向`，否则**整段退回 `default`（无则 `spring`）**；`CommunityCenter` 不可达 → 同样整段退回。

**朝向/动画/台词字段**：`facingDirection`（int，默认 `2`=下）；`animation`（`Data/animationDescriptions` 的键，或 `square_X_Y_facing`、`change_beach`、`change_normal`）；`dialogue` 形如 `"Strings\\schedules\\Abigail:Sun.000"`。解析器对引号的处理（反编译原文）：

```csharp
if (num4 < array3.Length) {
    if (array3[num4].Length > 0 && array3[num4][0] == '"')
        endMessage = array[j].Substring(array[j].IndexOf('"'));        // 注意：带引号存进去
    else {
        endBehavior = array3[num4]; num4++;
        if (num4 < array3.Length && array3[num4].Length > 0 && array3[num4][0] == '"')
            endMessage = array[j].Substring(array[j].IndexOf('"')).Replace("\"", "");   // 这里去引号
    }
}
```

然后 `getRouteEndBehaviorFunction` 里：`if (endMessage != null || (behaviorName != null && behaviorName.Length > 0 && behaviorName[0] == '"')) nextEndOfRouteMessage = endMessage.Replace("\"", "");`。
→ **结论：C# 侧构造 `SchedulePathDescription` 时，`endMessage` 不要带引号**；`endBehavior` 传 `""` 或 `null` 就不播放动画。

**`<<` / `>>` 分支**：**不存在**。反编译的 `parseMasterScheduleImpl` 里没有任何 `<<`/`>>` 处理；`NPC.cs` 中搜索 `"<<"` / `">>"` 0 命中；wiki 的 "Schedule script" 章节也只列 `GOTO` / `NOT friendship` / `MAIL` / `a` 前缀。1.6 的唯一"条件"机制就是这三个 + 键名本身（季节/日/星期/心数/节日/雨）。

Wiki 引用：[Modding:Schedule data](https://wiki.stardewvalley.net/Modding:Schedule_data)、[Modding:Migrate to Stardew Valley 1.6](https://wiki.stardewvalley.net/Modding:Migrate_to_Stardew_Valley_1.6)（其中 "Schedule changes" / "Other NPC changes" 小节记录 1.6 的改动，例如命令前后空白会被 trim，可多行书写）。

---

## 3. 单日单人覆盖：可选方案对比

### 方案 A（推荐）：编辑资产 + `InvalidateCache`
**优点**：SMAPI 自动完成"重载 + 重新触发"（§1.6）；资产是唯一真相来源，所以过夜 `resetForNewDay` 会自动重建；读档也会自动重建；不需要碰 `Schedule` 的 private setter。
**缺点**：需要写回资产字符串；必须写对 key。

```csharp
// Entry 中
helper.Events.Content.AssetRequested += this.OnAssetRequested;

private void OnAssetRequested(object sender, AssetRequestedEventArgs e)
{
    // 只处理我们关心的 NPC；注意 e.NameWithoutLocale 是 IAssetName
    if (!this.promisesByNpc.TryGetValue(/* 从 e.NameWithoutLocale.BaseName 解析出的名字 */ ..., out var promise))
        return;

    e.Edit(asset =>
    {
        IDictionary<string, string> data = asset.AsDictionary<string, string>().Data;
        // 关键：覆盖「今天真正会被选中」的那个 key
        string key = promise.ScheduleKeyForToday;          // 见下方说明，通常 = npc.ScheduleKey
        data[key] = BuildMergedScheduleString(data[key], promise);
    }, AssetEditPriority.Late);
}

// 应用（触发 SMAPI 的 CoreAssetPropagator.UpdateNpcSchedules）
helper.GameContent.InvalidateCache($"Characters/schedules/{npc.Name}");
```

`AssetRequestedEventArgs.Edit` 精确签名（XML 文档逐字）：

```csharp
public void Edit(Action<IAssetData> apply, AssetEditPriority priority = AssetEditPriority.Default, string source = null)
```
`AssetEditPriority` = `Early` / `Default` / `Late`（可加偏移如 `Low + 5` 用于 load 优先级）。
`IAssetData.AsDictionary<TKey,TValue>()` 返回 `IAssetDataForDictionary<TKey,TValue> : IAssetData<IDictionary<TKey,TValue>>`，数据通过 `IAssetData<TValue>.Data { get; }` 取。

**⚠ 最容易踩的坑 —— key 必须能被 `TryLoadSchedule()` 选中。**
如果你把覆盖写在 `spring` 上，但今天是 `Tue` 且该 NPC 有 `Tue` 配置，那么 `Tue` 胜出，你的覆盖**完全无效**。
**最省事且正确的做法：覆盖 `npc.ScheduleKey`（即 `dayScheduleName.Value`）** —— 它就是本次解析实际选中的 key，因此必然会被再次选中。

```csharp
string key = npc.ScheduleKey;      // "Tue" / "marriage_Mon" / "spring" / "rain" ...
if (string.IsNullOrEmpty(key)) { /* 今天该 NPC 没有行程 → 只能走方案 B */ }
```

`InvalidateCache` 精确签名（XML 文档逐字）：

```csharp
public void InvalidateCache(string assetName);
public void InvalidateCache(IAssetName assetName);
public void InvalidateCache<T>();
public void InvalidateCache(Func<IAssetInfo, bool> predicate);
public bool DoesAssetExist<T>(IAssetName assetName);   // 另见 Load<T>(string) / Load<T>(IAssetName)
```

### 方案 B：直接运行时注入 `Schedule`（今天立即生效，不落盘）
**优点**：不需要重写资产字符串；可控性最高；能处理"承诺时间已经过了"的场景。
**缺点**：① `NPC.Schedule` 是 `private set`，只能经 `TryLoadSchedule(key, dict)` 写；② 过夜/读档都会丢（§4）；③ 自己生成路径需要处理异常。

```csharp
public static bool SendNpcTo(NPC npc, string targetLocationName, Point targetTile, int facing,
                            int arriveTime, string endBehavior = "", string endMessage = "")
{
    if (npc?.currentLocation == null || !Game1.IsMasterGame) return false;
    if (npc.currentLocation.TilePoint == Point.Zero) { /* 起点 (0,0) 会抛异常 */ }

    // 1) 生成路径（跨地图）
    SchedulePathDescription desc;
    try
    {
        desc = npc.pathfindToNextScheduleLocation(
            npc.ScheduleKey ?? "PromiseMod",              // scheduleKey：只用于异常文本
            npc.currentLocation.Name,                     // startingLocation（内部名）
            npc.TilePoint.X, npc.TilePoint.Y,
            targetLocationName,                           // endingLocation（内部名）
            targetTile.X, targetTile.Y,
            facing is >= 0 and <= 3 ? facing : 2,
            endBehavior ?? string.Empty,
            endMessage ?? string.Empty);
    }
    catch (Exception ex) { /* KeyNotFoundException / warp 缺失 / 起点(0,0) */ return false; }

    if (desc?.route == null || desc.route.Count == 0)
        return false;                                     // 无路可走

    // 2) 选择 key：如果承诺时间已过，就把 key 夹到「当前时间向下取整到 10 分钟」
    int now = Game1.timeOfDay;
    int key = arriveTime;
    if (arriveTime <= now)
        key = Utility.ConvertMinutesToTime(Utility.ConvertTimeToMinutes(now) / 10 * 10);
    desc.time = key;                                      // ★ 构造函数不会设置 time！

    // 3) 合并进现有行程副本（保留今天剩余的原版条目）
    var schedule = npc.Schedule != null
        ? new Dictionary<int, SchedulePathDescription>(npc.Schedule)
        : new Dictionary<int, SchedulePathDescription>();
    schedule[key] = desc;

    // 4) 装载（复用真实 key，避免污染存档里的 <dayScheduleName>）
    npc.ignoreScheduleToday = false;
    npc.TryLoadSchedule(npc.ScheduleKey ?? "spring", schedule);   // 内部 followSchedule = true

    // 5) 立刻求值（精确查表 + 重置闸门）
    npc.queuedSchedulePaths.Clear();
    npc.lastAttemptedSchedule = 0;                                 // ★ 否则 checkSchedule 不查表
    npc.currentScheduleDelay = 0f;                                  // 清掉 scheduleDelaySeconds 造成的延迟
    npc.checkSchedule(key);                                         // key 必须正好是 Schedule 的键
    return true;
}
```

### 方案 C：直接接管 `controller`（最强控制，绕过行程系统）
用于"必须马上动，且不想等 10 分钟刻度"或"目标地点不受行程系统支持"。这是开源实现 LivingNPCs 的做法（§5）：

```csharp
// 同地图
npc.controller = new PathFindController(npc, location, tile, facing);

// 跨地图：自己造 route，然后交给 3 参构造
SchedulePathDescription d = npc.pathfindToNextScheduleLocation(
    "PromiseMod", src.Name, npc.TilePoint.X, npc.TilePoint.Y,
    dest.Name, tile.X, tile.Y, facing, string.Empty, string.Empty);
npc.DirectionsToNewLocation = d;
npc.controller = new PathFindController(d.route, npc, src)
{
    finalFacingDirection = facing,
    NPCSchedule = true            // 3 参构造已设 true，此处是防御性重复
};
```
为了让原版不覆盖你，先 `npc.ignoreScheduleToday = true; npc.followSchedule = false;`（LivingNPCs 的 `SuppressSchedule`），结束时再恢复原值。
跨地图兜底（当路径构建失败时）：`Game1.warpCharacter(npc, targetLocation, new Vector2(tile.X, tile.Y))` —— 见 §6.7。

### 方案 D：Content Patcher
纯内容包，用 `EditData` + `Target: Characters/schedules/<Name>` + `FromFile`/`Entries`，配合 `When` 条件与 `DynamicTokens`。**无法表达"NPC 今天对白里答应了什么"这种运行时状态**，所以只能做静态/日期驱动的覆盖。适合作为"第 2 天起的长期承诺"实现。

### 关于 `npc.Schedule = ...` 直接赋值
**做不到**：`public Dictionary<int, SchedulePathDescription> Schedule { get; private set; }`。只能经 `TryLoadSchedule(key, dict)`。反射可以，但不必要也不推荐。

---

## 4. 跨存档持久化（已用真实存档证明）

### 4.1 事实

| 数据 | 是否落盘 | 证据 |
| --- | --- | --- |
| `NPC.Schedule`（`Dictionary<int, SchedulePathDescription>`） | **否** | 属性带 `[XmlIgnore]`；`NPC.NetFields` 里只有 `dayScheduleName` / `islandScheduleName`（见 `NPC.cs` 的 `.AddField(...)` 列表）；真实存档 47 MB XML 中 `<Schedule>` 出现 **0** 次、`<SchedulePathDescription` **0** 次、`<directionsToNewLocation>` **0** 次、`<queuedSchedulePaths>` **0** 次、`<lastAttemptedSchedule>` **0** 次 |
| `NPC.ScheduleKey`（`dayScheduleName`） | **是** | 真实存档中 `<dayScheduleName>...` 出现 **159** 次，例：`<dayScheduleName>Mon</dayScheduleName>` |
| `previousEndPoint` | 是 | 存档中有 `<previousEndPoint><X>87</X><Y>45</Y></previousEndPoint>` |
| `NPC.followSchedule` | **是** | `public bool followSchedule = true;` **无** `[XmlIgnore]`；真实存档中 `<followSchedule>` 出现 **219** 次 |
| `ignoreScheduleToday` / `lastAttemptedSchedule` / `queuedSchedulePaths` / `DirectionsToNewLocation` / `temporaryController` | 否 | 全部带 `[XmlIgnore]`，且真实存档中 0 次 |

### 4.2 行程被重建的两个（且仅有的）时机

```
① 过夜（存档之前）：
   Game1.newDay 循环 → allCharacter.dayUpdate(dayOfMonth)          [Game1.cs]
   → NPC.resetForNewDay(dayOfMonth)                                [NPC.cs]
     → (IsVillager 时) TryLoadSchedule(); performSpecialScheduleChanges();

② 读档：
   SaveGame.loadDataToLocations(List<GameLocation>)                 [SaveGame.cs]
   → foreach (NPC obj in location.characters) { initializeCharacter(obj, location); obj.reloadSprite(); }
   → NPC.reloadSprite(bool onlyAppearance = false)                  [NPC.cs]
     → if (onlyAppearance || (!Game1.newDay && Game1.gameMode != 6)) return;   // ★ gameMode==6 是读档
     → TryLoadSchedule(); performSpecialScheduleChanges();
   （Game1.gameMode = 6 由 SaveGame.cs:562 设置）
```

其他会清空/重置行程的原版代码：`Game1.prepareSpouseForWedding` → `npc.ClearSchedule()`；`FarmerTeam.OnRequestLeoMoveEvent` → `InvalidateMasterSchedule() + ClearSchedule() + controller = null + temporaryController = null + warpCharacter + Halt() + ignoreScheduleToday = false`（**这是原版"运行时改行程"的官方范式**，值得照抄）；`Event.cs` 中也有 `characterFromName.ClearSchedule()`。

### 4.3 因此要持久化什么

- **单日承诺（"今晚 8 点我在酒馆"）**：不必自己落盘 *行程*，但**必须保证资产形式**（方案 A）或**在 `SaveLoaded` 后重新施加**（方案 B）。因为读档时 `reloadSprite()` 会按 key 链重新解析资产 → 方案 A 自动活下来；方案 B 的纯内存 `Schedule` 会丢。
- **跨日承诺（"后天下午在山上见"）**：需要自己存。用 SMAPI 存档数据（XML 文档逐字签名）：

```csharp
public TModel ReadSaveData<TModel>(string key);            // 读当前存档槽；无存档/非主玩家会抛 InvalidOperationException
public void WriteSaveData<TModel>(string key, TModel data); // 写当前存档槽；玩家不存档退出会丢
public TModel ReadGlobalData<TModel>(string key);           // 本机全局（GOG/Steam 可同步）
public void WriteGlobalData<TModel>(string key, TModel data);
```
（`TModel` 必须是"plain class + public properties"。）

建议的记录模型与生命周期：

```csharp
internal class SchedulePromise
{
    public string NpcName { get; set; }
    public string TargetLocation { get; set; }   // 内部名，如 "Saloon"
    public int TileX { get; set; }
    public int TileY { get; set; }
    public int Facing { get; set; } = 2;
    public int ArriveTime { get; set; }          // 军事时间，如 2000
    public int TotalDays { get; set; }           // Game1.Date.TotalDays，用于判"是不是今天"
    public string EndBehavior { get; set; }
    public string EndMessage { get; set; }
}
```
- `GameLoop.DayEnding`：清掉"今天"的承诺（或保留用于跨日，取决于语义）。
- `GameLoop.DayStarted` / `GameLoop.SaveLoaded`：把 `TotalDays == Game1.Date.TotalDays` 的承诺重新施加（方案 A 的 `AssetRequested` 处理器读这个列表 + `InvalidateCache`；或直接方案 B）。
- `GameLoop.Saving` / `Saved`：`helper.Data.WriteSaveData("promises", list)`。
- `helper.Events.GameLoop.ReturnedToTitle`：清内存缓存。
- **只在 `Context.IsMainPlayer`（主机）执行**（见 §6.4）。

---

## 5. 现有开源实现（含技术手法）

### 5.1 `LivingNPCs` + `ValleyTalk`（最贴近本需求）
- 仓库：[Nyx-Amanises/StardewLivingNPCs](https://github.com/Nyx-Amanises/StardewLivingNPCs)（含 `LivingNPCs/` 与 `ValleyTalk/` 两个 mod，MIT 风格；本地反编译的 ValleyTalk 即出自此处）
- Nexus：[LivingNPCs (47704)](https://www.nexusmods.com/stardewvalley/mods/47704)
- **技术手法（直接读源码确认）**：
  - `LivingNPCs/Behavior/Runtime/NpcTravelRuntime.cs` 的 `TryAssignVanillaScheduleRoute(npc, destination, targetTile, facingDirection, out PathFindController controller)`：**同地图用 4 参 `PathFindController(npc, source, tile, facing)`；跨地图调 `npc.pathfindToNextScheduleLocation(...)` 造 route，再 `new PathFindController(description.route, npc, source) { finalFacingDirection = ..., NPCSchedule = true }`，赋值 `npc.controller` + `npc.DirectionsToNewLocation`**。
  - 出游玩耍期间用 `npc.ignoreScheduleToday = true; npc.followSchedule = false;`（`SuppressSchedule`）压住原版行程，结束用 `RestoreSchedule(npc, 原值, 原值)` 恢复。
  - `NpcScheduleReturnService.TryResolveCurrentScheduleTarget(npc, ...)`：**读 `npc.Schedule` 中 `key <= Game1.timeOfDay` 的最大 key 的 `targetLocationName`/`targetTile`/`facingDirection`**，同地图 `PathFindController`，跨地图则手动 `location.characters.Remove/Add` + `npc.currentLocation = location` + `npc.Position = new Vector2(tile.X * Game1.tileSize, tile.Y * Game1.tileSize)`。
  - `ScheduleReflectionReader` 用反射读 `targetLocationName`/`targetTile`/`facingDirection` —— 说明它也要兼容字段名差异（其实是怕别的 mod 替换了对象类型）。
  - `BehaviorActionExecutor.IsSafeDestinationTile(location, tile, ignoredNpc)`：目标瓦片校验 = `location.Map.Layers[0].LayerWidth/Height` 内 + `location.isTileLocationOpen(v)` + `location.isTilePassable(v)` + 没有别的 NPC 占位。**这段建议直接照抄。**
  - README 原文（中文）：「NPC 使用原版 `pathfindToNextScheduleLocation` 生成跨地图路线，在真实的门和出口处切图；不会再在玩家靠近出口时手动搬运到下一张地图。」「只有明确的 `companion_outing` 会临时让 NPC 跨地图移动，**不会直接改写原版永久日程**。」
  - 持久化：`BehaviorEngine` 用 `helper.Data.ReadSaveData<BehaviorMemorySaveData>("behavior-memory")` / `WriteSaveData(...)`（SMAPI 存档数据），不是文件。
  - **局限**：这是"陪伴出游"（临时抢占 controller + 结束后回归原版行程），**不是**"改写今日剩余行程"，所以与我们的目标互补而非重合。
- `ValleyTalk`（同仓库 `ValleyTalk/`）：**只读**行程。`Prompts.cs` 里 `Dictionary<int, SchedulePathDescription> schedule = Character.StardewNpc.Schedule;` 然后 `schedule.Where(x => x.Key > Game1.timeOfDay).Select(x => x.Value.targetLocationName).Distinct()` 喂给 LLM 做「locationFuturePlans」；另有 `Character.StardewNpc.DirectionsToNewLocation.targetLocationName` 做「locationTravelling」。它的 `ReplaceSchedule` 提示词命令只是**改写要注入 prompt 的原版行程台词文本**（`DialogueContext.ScheduleLine`），**完全不碰游戏行程**。

### 5.2 `RandomNPC`（aedenthorn）—— 运行时动态生成行程
- 源码：[aedenthorn/StardewValleyMods → `RandomNPC/`](https://github.com/aedenthorn/StardewValleyMods/tree/master/RandomNPC)（`RandomNPC/ModEntry.cs`、`RNPCSchedule.cs`、`assets/schedules.json`）
- manifest：`Name: "Random NPCs"`, `UniqueID: "aedenthorn.RandomNPC"`, `Version 0.4.0`, `MinimumApiVersion 3.0.0`
- **技术手法（SMAPI 3.x 旧 API，4.x 需迁移到 `AssetRequested`）**：
  - `public class ModEntry : Mod, IAssetEditor, IAssetLoader`
  - `Edit<T>(IAssetInfo asset)` 中：`if (asset.AssetNameEquals("Characters/schedules/" + npc.nameID)) return (T)(object)MakeSchedule(npc);`
  - `MakeSchedule(RNPC npc)` **每次被请求时随机生成**行程字符串并返回 `Dictionary<string,string>`：`data.Add("spring", schedule.MakeString());`
  - `RNPCSchedule.MakeString()` 拼出真实脚本：
    `"6" + startM + "0 Town " + startX + " " + startY + " " + startFace + "/" + mTime + " " + morningLoc + "/" + aTime + " " + afternoonLoc + "/" + LeaveTime + " BusStop 12 9 0"`
    —— 即 **`<time> <Loc> <x> <y> <face>` 斜杠拼接**，且用 `Game1.random` 在运行时决定地点/时间。
  - 应用：`this.Helper.Content.InvalidateCache("Characters/schedules/" + RNPCs[i].nameID);`（在 `DayEnding` 里对每个 NPC 都做一次 —— **这正是 §1.6 的 SMAPI 自动重载路径**）。
  - 跨日"新行程"靠 `DayEnding` 里 `RNPCSchedules = new List<RNPCSchedule>()` + 重新 invalidate 实现；只有随机字符串池（`assets/schedules.json`）通过 `Helper.Data.ReadJsonFile<ModData>` 落盘。
  - **这是"用资产管线做运行时行程生成"的现存最佳样本**：证明"每帧/每天动态生成 `Characters/schedules/X` + `InvalidateCache`"是可行且被 SMAPI 正式支持的路线。

### 5.3 `Lookup Anything`（Pathoschild）—— 只读行程展示
- 源码：[`LookupAnything/Framework/Fields/ScheduleField.cs`](https://github.com/Pathoschild/StardewMods/blob/develop/LookupAnything/Framework/Fields/ScheduleField.cs)
- `using StardewValley.Pathfinding;` + `Dictionary<int, SchedulePathDescription>? schedule` + `npc.ignoreScheduleToday` / `npc.followSchedule` 判断，UI 里按 key 排序展示 `time` / `entry.targetLocationName` / `entry.targetTile`。
- 它明确处理了农场手（`!Context.IsMainPlayer`）看不到行程的情况：`Npc_Schedule_Farmhand_UnknownSchedule()`。**这印证了 §6.4：非主机无法可靠读取/驱动行程。**

### 5.4 只做静态覆盖的（未逐一验证技术细节）
- [`Content Patcher`](https://github.com/Pathoschild/StardewMods/tree/develop/ContentPatcher)：`EditData` / `Target: Characters/schedules/<Name>`。
- [NPC Schedulers (Nexus 31494)](https://www.nexusmods.com/stardewvalley/mods/31494)、[True Rune - Powerful Schedule Patcher (Nexus 47113)](https://www.nexusmods.com/stardewvalley/mods/47113)、[Spouse Farmhouse Schedules (Nexus 38994)](https://www.nexusmods.com/stardewvalley/mods/38994)：Nexus 对抓取返回 HTTP 403，**我无法确认它们的具体实现**，只能确认它们存在并且主题是"行程补丁"。**未验证，不要据此下结论。**
- `aedenthorn/StardewValleyMods` 仓库里除 `RandomNPC` 外，**没有**其它文件名含 "schedule" 的 mod。

### 5.5 未找到的东西
- **没有任何 LLM/AI 驱动的 Stardew mod 会把 LLM 输出直接转成真实行程**。`ValleyTalk`（LLM 对话）只**读**行程；`LivingNPCs`（LLM 世界动作）只**临时抢占**寻路且明确声明「不会直接改写原版永久日程」。**本任务是空白的。**
- 找不到任何 mod 使用 `queuedSchedulePaths` / `lastAttemptedSchedule` 这套"外科手术式"重触发（只有 SMAPI 自己在 `UpdateNpcSchedules` 里用）。
- GitHub 代码搜索（`grep.app`）被 429/风控拦截，无法做全量代码搜索；上述结论基于可访问的仓库源码。

---

## 6. 已知陷阱（附证据）

### 6.1 第二天必然回到原版行程
`NPC.resetForNewDay` → `TryLoadSchedule()`（`NPC.cs:6215`）从资产重新解析。**任何只写内存 `Schedule` 的改动都会在过夜后失效。** 只有方案 A（资产）才能"自动"跨日，但那样会每天生效 —— 所以必须自己按 `TotalDays` 判日。

### 6.2 读档会丢（同上机制，另一个入口）
见 §4.2 ②。`Schedule` 不落盘；读档走 `reloadSprite()` + `TryLoadSchedule()`。方案 A 的资产改动会活下来；方案 B 的不会。

### 6.3 关键闸门（最容易 debug 到崩溃的两个）
- `checkSchedule` 对 `Schedule` 做**精确** `TryGetValue(timeOfDay)`。传一个不在字典里的时间 → 什么都不发生。
- `lastAttemptedSchedule < timeOfDay` 不放行 → 同一刻度内注入无效。**必须重置**（设 `0` 或 `-1`）。
- `currentScheduleDelay > 0`（由 `scheduleDelaySeconds` 驱动，夜市/岛屿用它做错峰）会让 `checkSchedule` 提前 return。必要时清零。

### 6.4 多人游戏 / 农场手
- `TryLoadSchedule(key, dict)` 只在 `Game1.IsMasterGame` 时写 `dayScheduleName`；`ClearSchedule()` 同理。
- NPC 路径推进只在主机跑（`PathFindController` 的 `update`/`moveCharacter` 由角色的 update 驱动；LivingNPCs 源码注释原文：「Farmhands cannot drive NPC pathfinding (controller.update only runs on the host)」）。
- Lookup Anything 明确对农场手显示 "unknown schedule"。
- **结论：所有行程注入/驱动逻辑都要 `Context.IsMainPlayer` 守卫，并通过 SMAPI 的 net 字段/事件让客户端跟随。**

### 6.5 节日与事件
- `Game1.eventUp`、`Game1.CurrentEvent` 非空时不要动行程。
- 代码里有 `NPC.cs:3392: if (Game1.eventUp || location == null)`、`NPC.cs:5088: if (!Game1.eventUp)` 等守卫；`Event.cs` 中会 `characterFromName.ClearSchedule();`。
- 节日会把 NPC 变成 event actor 放进 festival 用的 `Temp` 地图（`Event.setUpFestivalMainEvent` 等），节日期间游戏时间冻结。
- **`Utility.isFestivalDay()` 系列（XML 文档确认存在 4 个重载）应作为前置跳过条件：**
  ```csharp
  public static bool isFestivalDay();
  public static bool isFestivalDay(string locationContext);
  public static bool isFestivalDay(int day, Season season);
  public static bool isFestivalDay(int day, Season season, string locationContext);
  ```
- **未完全确认**：节日期间 NPC 从原地点移除的确切时序、以及节日结束后 controller 是否被复原。建议实测。

### 6.6 已婚 NPC
`NPC.TryLoadSchedule()` 对已婚者**只查 `marriage_*`**，`spring` 不在链上；已婚者的起始位置被硬编码为 `BusStop 10 23`（`parseMasterScheduleImpl` 里 `isMarried() ? "BusStop" : defaultMap.Value` / `new Point(10,23)`）；`PathFindController.handleWarps` 会把 FarmHouse↔BusStop 的 warp 重写成配偶农舍入口。另外 `NPC.cs:6957: if (isMarried() && (Schedule == null || location is FarmHouse))` 会触发婚姻行为，可能覆盖 controller；`prepareSpouseForWedding` 会 `ClearSchedule()`。
→ **已婚配偶的行程注入是最难的子场景，必须单独实测。**

### 6.7 寻路失败与跨地图约束
`NPC.pathfindToNextScheduleLocation` 会抛异常的情形（反编译逐字）：
- 起点 `Point.Zero`：`throw new Exception($"NPC {Name} has an invalid schedule with key '{scheduleKey}': start position in {startingLocation} is at tile (0, 0), which isn't valid.")`
- 需要的 warp 不存在：`throw new Exception($"NPC {Name} has an invalid schedule with key '{scheduleKey}': it requires a warp from {gameLocation.NameOrUniqueName} to {array[i + 1]}, but none was found.")`
- 路径中的地点不存在：`Game1.RequireLocation(key)` → `throw new KeyNotFoundException($"Required location '{name}' not found.")`
- 返回的 `route` 可能 `Count == 0`（A* 找不到路，`limit` 30000）→ 原版此时直接执行 `endBehaviorFunction` 并把 `controller = null`，NPC 原地不动。

`Game1.warpCharacter` 精确签名（反编译）：

```csharp
public static void warpCharacter(NPC character, string targetLocationName, Point position);
public static void warpCharacter(NPC character, string targetLocationName, Vector2 position);
public static void warpCharacter(NPC character, GameLocation targetLocation, Vector2 position);
```
注意 `Vector2` 版本传的是**瓦片坐标**（`RequireLocation(targetLocationName)` 后 `new Vector2(position.X, position.Y)`），而 `GameLocation` 版本在内部把 `position` 当瓦片用（`LivingNPCs` 也是传 `new Vector2(tile.X, tile.Y)`）。它会自动处理被动节日 `MapReplacements`、`Trailer`→`Trailer_Big`。**这是跨地图兜底的最省事手段**，但它是瞬移（会破坏"看得见他走过去"的观感，且若玩家正在看该地图会有跳帧感）。

另外：`PathFindController` 只能用于**宽高均 ≤ 127** 的地图（类注释原文）；`Farm` 被排除在原版 NPC 行程寻路之外（LivingNPCs 源码注释：「Farm is excluded from vanilla NPC schedule pathfinding, so farm outings use a BusStop/Farm boundary route」）—— 农场相关目标需要 BusStop/Farm 边界绕行或直接 warp。

### 6.8 目标瓦片被占用
`PathFindController.moveCharacter` 里：若另一个正在移动、且名字字典序更小的 NPC 的碰撞盒与自身相交，则 `character.Halt()` 并 return（互相卡死）。同时 `MovePosition` 会被物体/玩家阻挡。**务必先校验目标瓦片**（`isTileLocationOpen` + `isTilePassable` + 无其他 NPC 占位），必要时在附近找空格（照抄 `LivingNPCs.BehaviorActionExecutor.IsSafeDestinationTile` / `TryFindOpenTileNear`）。

### 6.9 `isSleeping`
`NPC.isSleeping` 是 `public readonly NetBool isSleeping = new NetBool(value: false);`（**是字段不是属性**，用 `.Value`）。它**不直接阻止寻路**（`checkAction` 里只是阻止交互 + 播放 emote；`Halt()` 里若 `isSleeping.Value` 会重放睡觉动画）。所以：正要「走」的 NPC 如果已经进入 `*_sleep` 动画状态，控制器仍会移动他，但会被 `Halt()` 的睡觉动画覆盖。**建议：设置新目标前 `npc.isSleeping.Value = false;`，或把承诺时间安排在 sleep 条目之前。**

### 6.10 不要顺手改到 `ScheduleKey`
`TryLoadSchedule(key, dict)` 会把 `key` 写进 `dayScheduleName`（会被存档，真实存档里每个 NPC 都有）。`DefaultPhoneHandler` 会读 `npc.ScheduleKey` 来决定电话对白（例如 `ScheduleKey == "fall_18"`）。**所以方案 B 里请传 `npc.ScheduleKey`（或真实的规范 key 如 `"spring"`），不要传自定义字符串。**

### 6.11 未文档化的 API
`NPC.checkSchedule`、`NPC.parseMasterSchedule`、`NPC.getMasterScheduleRawData`、`NPC.SplitScheduleCommands`、`NPC.pathfindToNextScheduleLocation` 都是 `public` 但在 `Stardew Valley.xml` 里**没有文档**。它们被 SMAPI 自己使用（`checkSchedule`、`queuedSchedulePaths`、`lastAttemptedSchedule`），因此短期内稳定；但属于"官方未承诺"的 API。建议用 `try/catch` 包裹并加 SMAPI 版本守卫。

---

## 7. 推荐实现草图（"NPC 说今晚 8 点去酒馆，就让他真的去"）

### 7.1 分层设计
1. **承诺记录层**（`helper.Data.WriteSaveData`）：对白里解析出 `{NpcName, TargetLocation, Tile, ArriveTime, TotalDays}`。
2. **施加层**（两条腿，互补）：
   - **资产腿（方案 A）**：`AssetRequested` + 覆盖 `npc.ScheduleKey` + `InvalidateCache`。负责"跨过夜/读档后仍然成立"。
   - **即时腿（方案 B/C）**：承诺时间 ≤ 现在，或该 NPC 今天 `ScheduleKey` 为空（无行程）→ 用 `npc.pathfindToNextScheduleLocation` + `TryLoadSchedule(key, dict)` + `lastAttemptedSchedule = 0` + `checkSchedule(key)`；若路径构建失败 → `Game1.warpCharacter` 兜底。
3. **守卫层**：`Context.IsMainPlayer`、`!Game1.eventUp`、`Game1.CurrentEvent == null`、`!Utility.isFestivalDay()`、`npc.currentLocation != null`、`npc.currentLocation.TilePoint != Point.Zero`、`npc.IsVillager`、目标瓦片可通行。

### 7.2 资产腿（保持今日剩余行程、并让承诺条目加入）
```csharp
private void OnAssetRequested(object sender, AssetRequestedEventArgs e)
{
    if (!e.NameWithoutLocale.StartsWith("Characters/schedules/"))   // 或 IsDirectlyUnderPath
        return;

    string npcName = e.NameWithoutLocale.BaseName.Split('/')[^1];
    if (!this.promises.TryGetValue(npcName, out SchedulePromise promise)) return;
    if (promise.TotalDays != Game1.Date.TotalDays) return;

    NPC npc = Game1.getCharacterFromName(npcName);
    string key = npc?.ScheduleKey;                 // ★ 必须覆盖真正被选中的 key
    if (string.IsNullOrEmpty(key)) return;          // 无行程 → 交给即时腿

    DateTime start = DateTime.Now;                 // 便捷日志
    e.Edit(asset =>
    {
        IDictionary<string, string> data = asset.AsDictionary<string, string>().Data;
        string original = data.TryGetValue(key, out string s) ? s : "";
        data[key] = MergePromiseIntoScript(original, promise);   // 见 7.3
    }, AssetEditPriority.Late);

    this.Monitor.Log($"[{npcName}] 覆盖行程 key '{key}'", LogLevel.Trace);
}

/// <summary>把承诺点插入到行程脚本的正确位置（按时间排序，覆盖冲突时间点）。</summary>
private static string MergePromiseIntoScript(string original, SchedulePromise p)
{
    // original 形如 "900 SeedShop 11 5 0/1300 Town 73 54 2/1930 SeedShop 1 9 3 abigail_sleep"
    var commands = original
        .Split('/', StringSplitOptions.RemoveEmptyEntries)
        .Select(c => c.Trim())
        .Where(c => c.Length > 0)
        .ToList();

    // 丢弃首个 GOTO/NOT/MAIL 命令之后的处理略：若含 GOTO，直接放弃合并，改走即时腿
    if (commands.Count > 0 && (commands[0].Contains("GOTO") || commands[0].Contains("NOT") || commands[0].Contains("MAIL")))
        return original;

    string entry = $"{p.ArriveTime} {p.TargetLocation} {p.TileX} {p.TileY} {p.Facing}";
    if (!string.IsNullOrEmpty(p.EndBehavior)) entry += $" {p.EndBehavior}";
    if (!string.IsNullOrEmpty(p.EndMessage))  entry += $" \"{p.EndMessage}\"";

    // 按时间排序插入；时间相同则替换
    var parsed = commands
        .Select(c => (Time: ParseTime(c.Split(' ')[0]), Raw: c))
        .Where(t => t.Time > 0)
        .ToList();
    parsed.RemoveAll(t => t.Time == p.ArriveTime);
    parsed.Add((p.ArriveTime, entry));
    return string.Join("/", parsed.OrderBy(t => t.Time).Select(t => t.Raw));
}
```
（`ParseTime` 需处理 `a` 前缀与 int 解析；含 `GOTO`/`NOT`/`MAIL` 的脚本建议**不要合并**，直接用即时腿，或在 `GOTO` 目标 key 上做覆盖。这是必须实测的边界。）

触发施加：
```csharp
helper.GameContent.InvalidateCache($"Characters/schedules/{npcName}");
// → SMAPI CoreAssetPropagator.UpdateNpcSchedules 自动：InvalidateMasterSchedule 等价操作
//   + npc.TryLoadSchedule() + (latest key <= now → queuedSchedulePaths.Clear(); lastAttemptedSchedule = 0; checkSchedule(latest))
```

### 7.3 即时腿（"现在就去" / 无行程兜底）
即 §3 方案 B 的 `SendNpcTo`。若返回 `false`，兜底：
```csharp
GameLocation dest = Game1.getLocationFromName(promise.TargetLocation);
if (dest != null)                       // getLocationFromName 不存在时会抛，注意 try/catch
{
    npc.currentLocation?.characters.Remove(npc);
    if (!dest.characters.Contains(npc)) dest.characters.Add(npc);
    npc.currentLocation = dest;
    npc.Position = new Vector2(promise.TileX * Game1.tileSize, promise.TileY * Game1.tileSize);
    npc.faceDirection(promise.Facing);
    npc.controller = null;
    npc.Halt();
    // 或者更"官方"的一行：Game1.warpCharacter(npc, dest, new Vector2(promise.TileX, promise.TileY));
}
```
（`LivingNPCs.NpcScheduleReturnService` 就是这么写的，并且会先 `npc.controller = null; npc.Halt();`。）

### 7.4 事件接线
```csharp
helper.Events.Content.AssetRequested        += this.OnAssetRequested;
// 先读盘，再重新施加今天的承诺（顺序很重要）
helper.Events.GameLoop.SaveLoaded           += (_, _) => {
    this.promises = helper.Data.ReadSaveData<List<SchedulePromise>>("promises") ?? new();
    this.ReapplyTodayPromises();
};
helper.Events.GameLoop.DayStarted           += (_, _) => this.ReapplyTodayPromises();
helper.Events.GameLoop.DayEnding            += (_, _) => this.promises.RemoveAll(p => p.TotalDays <= Game1.Date.TotalDays);
helper.Events.GameLoop.Saving               += (_, _) => helper.Data.WriteSaveData("promises", this.promises);
helper.Events.GameLoop.ReturnedToTitle      += (_, _) => this.promises.Clear();
// ⚠ 不要在 GameLaunched 里读存档数据：那时还没载入存档，ReadSaveData 会抛 InvalidOperationException。
```
每个事件体开头：`if (!Context.IsMainPlayer) return;`

### 7.5 多日承诺
同一 `SchedulePromise` 列表；`ReapplyTodayPromises()` 只处理 `TotalDays == Game1.Date.TotalDays` 的项。跨日时什么都不做（承诺自然"明天再生效"）。若承诺是"每天 8 点"，则在 `DayStarted` 里为**今天**生成一条新记录（`TotalDays = 今天`）。

---

## 8. 未能确认 / 需要实测的点（明确标注）

1. **节日与婚礼期间的确切行为**：我确认了 `Game1.eventUp` / `Game1.CurrentEvent` / `Utility.isFestivalDay()` / `Event` 会 `ClearSchedule()`，但**没有**逐行追完"NPC 何时被移出原地点、节日结束后 controller/行程如何复原"。证据：`Event.cs` 的 `clearSchedule` 调用点、`NPC.cs` 的 `eventUp` 守卫。→ 需要实测。
2. **已婚配偶（住在农场）的场景**：我确认了已婚者只走 `marriage_*` key 链、起始点硬编码 `BusStop 10 23`、`handleWarps` 重写农舍 warp、`NPC.cs:6957` 的婚姻行为分支。但**没有**确认"给已婚 NPC 注入非 marriage key 的行程"是否会被 `marriageDuties` / `updateMarriageBehavior` 覆写。→ 需要实测。
3. **`Farm` 作为目标**：有第三方源码注释称农场被排除在原版 NPC 行程寻路之外（LivingNPCs 的 `CompanionOutingRuntime.cs`：「Farm is excluded from vanilla NPC schedule pathfinding」），但我在 `WarpPathfindingCache` 反编译里**没有**看到显式的 Farm 排除（只看 `IgnoreLocationNames` 与"farmhand cellars 会自动加入"，注释来自 XML 文档）。→ 我的 grep 未覆盖到排除逻辑的具体位置，**不确定性存在**。
4. **`followSchedule` 的落盘影响**：`public bool followSchedule = true;` 没有 `[XmlIgnore]`，真实存档里 `<followSchedule>` 出现 **219** 次，**所以它是被持久化的**。`ClearSchedule()` 会把它置 `false` 并存进存档 —— 也就是说，如果你用 `ClearSchedule()` 关掉某 NPC 的行程，这个 `false` 会**跨存档保留**（直到下一次 `resetForNewDay` → `TryLoadSchedule()` 成功时又被设回 `true`）。**建议：不要用 `ClearSchedule()` 做"临时禁用"；改用 `ignoreScheduleToday`（`[XmlIgnore]`，不落盘）+ `followSchedule` 的原值备份/恢复**（LivingNPCs 的 `SuppressSchedule`/`RestoreSchedule` 就是这么做的）。
5. **Nexus 上三个"schedule patch" mod 的实现**：`NPC Schedulers (31494)`、`True Rune - Powerful Schedule Patcher (47113)`、`Spouse Farmhouse Schedules (38994)` —— Nexus 返回 HTTP 403，`grep.app` 返回 429，**我无法验证它们的实现手法**，故只列为"存在但未验证"。
6. **`a` 前缀时间反推的确切公式**：我读到了 `num9 = (int)Math.Round((float)num7 / (float)num8) * 10`（`num7 = 路径像素/2`，`num8 = realMilliSecondsPerGameTenMinutes/1000*60`），但**没有实测**其输出是否符合直觉。→ 建议实际用 `pathfindToNextScheduleLocation` + 手工设 `time`，不要依赖 `a` 前缀。
7. **`NPC.getSchedule` 之类的旧教程 API**：明确不存在（§1.1）。若你看到提到它们的代码，那是 1.5 或更早。
8. **`Game1.Date.TotalDays` / `Game1.tileSize`**：我在第三方源码里看到使用，未在 `Stardew Valley.xml` 中核实签名（`.xml` 对游戏类型覆盖很少）。`Game1.Date.TotalDays` 与 `Game1.tileSize` 是长期稳定的成员，但严格来说**我未逐字核实**。

---

## 9. 参考链接

- [Modding:Schedule data — Stardew Valley Wiki](https://wiki.stardewvalley.net/Modding:Schedule_data)（键语法、脚本格式、`a` 前缀、`bed`、动画、台词、Limitations）
- [Modding:Migrate to Stardew Valley 1.6 — Stardew Valley Wiki](https://wiki.stardewvalley.net/Modding:Migrate_to_Stardew_Valley_1.6)（"Schedule changes" 小节）
- [StardewLivingNPCs（LivingNPCs + ValleyTalk）源码](https://github.com/Nyx-Amanises/StardewLivingNPCs) — 关键文件：[`NpcTravelRuntime.cs`](https://github.com/Nyx-Amanises/StardewLivingNPCs/blob/master/LivingNPCs/Behavior/Runtime/NpcTravelRuntime.cs)、[`NpcScheduleReturnService.cs`](https://github.com/Nyx-Amanises/StardewLivingNPCs/blob/master/LivingNPCs/Behavior/Runtime/NpcScheduleReturnService.cs)、[`BehaviorActionExecutor.cs`](https://github.com/Nyx-Amanises/StardewLivingNPCs/blob/master/LivingNPCs/Behavior/Runtime/BehaviorActionExecutor.cs)、[`ScheduleReflectionReader.cs`](https://github.com/Nyx-Amanises/StardewLivingNPCs/blob/master/LivingNPCs/Behavior/Runtime/ScheduleReflectionReader.cs)
- [LivingNPCs on Nexus](https://www.nexusmods.com/stardewvalley/mods/47704)
- [aedenthorn/StardewValleyMods → RandomNPC](https://github.com/aedenthorn/StardewValleyMods/tree/master/RandomNPC)（运行时随机行程生成，`IAssetEditor` + `InvalidateCache`）
- [Lookup Anything `ScheduleField.cs`](https://github.com/Pathoschild/StardewMods/blob/develop/LookupAnything/Framework/Fields/ScheduleField.cs)（只读行程展示）
- [SMAPI 源码](https://github.com/Pathoschild/SMAPI)（`CoreAssetPropagator.UpdateNpcSchedules`、`IAssetDataForDictionary`、`IAssetData`）
- 本地证据：`Stardew Valley.xml`（`P:StardewValley.NPC.Schedule`、`M:StardewValley.NPC.TryLoadSchedule*`、`T:StardewValley.Pathfinding.PathFindController`、`T:StardewValley.Pathfinding.WarpPathfindingCache`）、`StardewModdingAPI.xml`（`M:StardewModdingAPI.Metadata.CoreAssetPropagator.UpdateNpcSchedules`、`IDataHelper.*`、`AssetRequestedEventArgs.Edit`）
