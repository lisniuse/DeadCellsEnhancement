// Dead Cells 原始游戏代理命名空间：提供 Game、Hero、Weapon、InventItem 等游戏对象的 C# 代理。
using dc;
using dc.en;
using dc.en.inter;
using dc.tool;
using dc.tool.mod;

// Hashlink 代理对象接口：用于读取/写入游戏对象里没有直接暴露成 C# 属性的动态字段。
using Hashlink.Proxy.Objects;

// HaxeProxy 运行时工具：Ref<T> 用于传递 Haxe 侧的引用参数。
using HaxeProxy.Runtime;

// ModCore 事件接口：让本 Mod 接收“资源加载后”和“英雄每帧更新”等生命周期事件。
using ModCore.Events.Interfaces;
using ModCore.Events.Interfaces.Game;
using ModCore.Events.Interfaces.Game.Hero;

// ModCore 模块：HashlinkHooks 用于挂钩游戏函数，FsPak/GetText 用于加载资源和语言包。
using ModCore.Modules;
using ModCore.Mods;
using ModCore.Utilities;

// .NET 标准库：用于读写热加载配置文件。
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

// 本 Mod 的命名空间，必须和 csproj 里的 ModMain 保持一致。
namespace SpeedTalisman;

// ModEntry 是这个 Mod 的入口类。
// ModBase 提供 Info、Logger 等基础能力。
// IOnAfterLoadingAssets 表示游戏资源加载后会回调 OnAfterLoadingAssets。
// IOnHeroUpdate 表示每帧英雄更新时会回调 OnHeroUpdate。
public class ModEntry(ModInfo info) : ModBase(info),
    IOnAfterLoadingAssets,
    IOnHeroUpdate
{
    // CDB 里新增护符的 id。创建 InventItem 和判断装备时都用这个 id 匹配。
    private const string TalismanId = "SpeedTalisman";

    // 死神镰刀的原版物品 id。数据库里这把武器叫 AdeleScythe，对应游戏里的“Death's Scythe/死神镰刀”。
    private const string TestWeaponId = "AdeleScythe";

    // 正式模式基础攻速加成：0 表示 0 细胞时完全没有加成，直接绕过武器修改。
    private const double DefaultBaseSpeedBonus = 0.00;

    // 正式模式每个已激活 Boss 细胞额外增加的攻速加成：0.10 表示每个 Boss Cell 增加 10%。
    private const double DefaultBonusPerBossCell = 0.10;

    // 默认最大攻速加成上限：0.35 表示最多把相关时间参数缩短 35%，防止手感过快或接近 0CD。
    private const double DefaultMaxSpeedBonus = 0.35;

    // 默认动画速度加成缩放。时间字段已经缩短一次，动画速度再完整叠加会显得过快，所以这里只吃一半加成。
    private const double DefaultAnimationSpeedBonusScale = 0.50;

    // 临时测试开关：模拟 Workshop「Rapid Attack & 0CD」的顶满效果。
    // true 时不按百分比缩放，而是把武器攻击段的冷却、前摇、后摇和命中帧直接清零。
    private const bool DefaultUseWorkshopMaxSpeedTest = false;

    // 默认顶满测试用动画速度下限。Workshop 里常见 animSpd 为 1.5 或 2，这里用 2 作为明显手感测试。
    private const double DefaultWorkshopAnimSpeedFloor = 2.0;

    // 当前运行时基础攻速加成。正式模式为 0；调试配置存在时会按档位覆盖。
    private static double _baseSpeedBonus = DefaultBaseSpeedBonus;

    // 当前运行时每 Boss 细胞攻速加成。正式模式按细胞增加；调试配置存在时会按档位覆盖。
    private static double _bonusPerBossCell = DefaultBonusPerBossCell;

    // 当前运行时最大攻速加成。正式模式和调试模式都会使用这个上限。
    private static double _maxSpeedBonus = DefaultMaxSpeedBonus;

    // 当前运行时动画速度加成缩放。正式模式和调试模式都会使用这个缩放。
    private static double _animationSpeedBonusScale = DefaultAnimationSpeedBonusScale;

    // 当前运行时是否启用顶满测试模式。只有 debug_speed_config.json 存在且 SpeedLevel=5 时才会启用。
    private static bool _useWorkshopMaxSpeedTest = DefaultUseWorkshopMaxSpeedTest;

    // 当前运行时顶满测试动画速度下限。只有 debug_speed_config.json 存在且 SpeedLevel=5 时才会使用。
    private static double _workshopAnimSpeedFloor = DefaultWorkshopAnimSpeedFloor;

    // 当前帧检测到玩家是否装备了迅捷护符。
    private static bool _hasTalismanEquipped;

    // 当前生效的攻速加成，只有装备迅捷护符时才大于 0。
    private static double _currentSpeedBonus;

    // 是否已经在当前游戏进程里成功处理过出生点掉落，避免每帧无限生成。
    private static bool _hasSpawnedTalisman;

    // 生成失败后的重试计时器。游戏刚进图时部分对象可能没准备好，所以允许定时重试。
    private static double _spawnRetryTimer;

    // 调试日志路径。Initialize 中根据 ModRoot 设置，指向 coremod/mods/SpeedTalisman/moddbg.log。
    private static string? _debugLogPath;

    // 调试热加载配置文件路径。正式发布时没有这个文件；存在时优先使用它的 SpeedLevel 档位。
    private static string? _configPath;

    // 配置文件上次读取到的修改时间。文件保存后时间变化，Mod 就会重新读入参数。
    private static DateTime _configLastWriteTimeUtc;

    // 当前是否正在使用 debug_speed_config.json。false 表示正式模式：按 Boss 细胞计算，0 细胞无加成。
    private static bool _usingDebugConfig;

    // 配置文件重载计时器。每帧都读文件太浪费，所以按固定间隔检查一次。
    private static double _configReloadTimer;

    // 记录上一次装备检测使用的来源，避免每帧重复写相同日志。
    private static string? _lastDetectionSource;

    // 武器创建 hook 日志计数器。武器创建可能很频繁，所以只记录前几次，避免日志刷屏。
    private static int _weaponHookLogCount;

    // 武器修改日志计数器。只记录前几把武器，方便确认修改链路是否走通。
    private static int _weaponModifyLogCount;

    // 攻击段字段修改日志计数器。只记录前几个攻击段，方便确认具体字段是否写入。
    private static int _strikeModifyLogCount;

    // 已经经过 Weapon.create 的玩家武器物品。配置热加载或护符装备状态变化后，会尝试给这些武器重新套用参数。
    private static readonly System.Collections.Generic.List<InventItem> _knownPlayerWeaponItems = [];

    // 每个攻击段字段的原始值缓存。动态调参时必须从原始值重新计算，不能在已缩放结果上反复相乘。
    private static readonly System.Collections.Generic.Dictionary<int, System.Collections.Generic.Dictionary<string, object?>> _originalStrikeFieldValues = [];

    // 配置变化后设置为 true，让 OnHeroUpdate 在下一帧尝试把新参数套到已缓存武器上。
    private static bool _pendingReapplyWeapons;

    // ModCore 加载 Mod 时调用的初始化函数。
    public override void Initialize()
    {
        // 记录调试日志文件路径；Info.ModRoot 是当前 Mod 安装目录。
        _debugLogPath = Info.ModRoot?.GetFilePath("moddbg.log");

        // 记录调试热加载配置文件路径；正式发布时不放这个文件，存在时才启用调试档位。
        _configPath = Info.ModRoot?.GetFilePath("debug_speed_config.json");

        // 写入一条轻量日志，方便确认 Mod 是否真的被加载。
        DebugLog("Initialize");

        // 立即读取一次调试配置；文件不存在时会自动进入正式模式。
        ReloadConfigIfChanged(force: true);

        // 注册本 Mod 的语言包名字。GetText 会按 lang/SpeedTalisman.<lang>.mo 查找翻译。
        GetText.Instance.RegisterMod("SpeedTalisman");

        // 写入 Core 的 Serilog 日志，便于在 coremod/logs/log_latest.log 中排查。
        Logger.Information("SpeedTalisman initialized.");

        // 获取 Hashlink hook 管理器实例，用它来拦截游戏里的 Haxe/Hashlink 函数。
        var hooks = HashlinkHooks.Instance;

        // 拦截 tool.$Weapon.create：每次游戏根据 InventItem 创建 Weapon 时，都会先进入 Hook_create。
        // 这样可以在武器对象刚创建出来时修改它的 strikeChain 攻击参数。
        hooks.CreateHook("tool.$Weapon", "create", Hook_WeaponCreate.Hook_create).Enable();
    }

    // 游戏资源加载完成后调用。这里加载本 Mod 打包出的 res.pak。
    void IOnAfterLoadingAssets.OnAfterLoadingAssets()
    {
        // res.pak 位于当前 Mod 安装目录，里面包含 CDB 差异和语言 mo 文件。
        var pakPath = Info.ModRoot!.GetFilePath("res.pak");

        // 把本 Mod 的 pak 加入游戏文件系统，否则 data.cdb_ 和 lang/ 文件不会被游戏看到。
        FsPak.Instance.FileSystem.loadPak(pakPath.AsHaxeString());

        // 写入本地调试日志，确认 pak 的实际加载路径。
        DebugLog($"Loaded res.pak: {pakPath}");

        // 写入 Core 日志，和 DebugLog 互相补充。
        Logger.Information("Loaded SpeedTalisman resources from {0}", pakPath);
    }

    // 英雄每帧更新时调用。dt 是距离上一帧经过的秒数。
    void IOnHeroUpdate.OnHeroUpdate(double dt)
    {
        // 获取当前玩家英雄实例；在主菜单或加载过程中可能为 null。
        var hero = Game.Instance.HeroInstance;

        // 没有英雄对象时什么也不做，避免空引用。
        if (hero == null) return;

        // 每隔一小段时间检查配置文件是否保存过；保存过就热加载新参数。
        _configReloadTimer -= dt;
        if (_configReloadTimer <= 0)
        {
            _configReloadTimer = 1.0;
            ReloadConfigIfChanged(force: false);
        }

        // 递减生成重试计时器。dt 越大，计时器下降越多。
        _spawnRetryTimer -= dt;

        // 如果还没成功处理出生点掉落，并且重试冷却已经结束，就尝试生成一次。
        if (!_hasSpawnedTalisman && _spawnRetryTimer <= 0)
        {
            // 设置 0.5 秒后才允许下一次重试，避免每帧刷日志或重复构造对象。
            _spawnRetryTimer = 0.5;

            // 真正执行出生点掉落物创建逻辑。
            TrySpawnTalisman(hero);
        }

        // 记录上一帧是否装备了护符，用于判断状态是否发生变化。
        var wasEquipped = _hasTalismanEquipped;

        // 记录上一帧的加成数值，用于判断配置文件或细胞数导致的加成变化。
        var previousSpeedBonus = _currentSpeedBonus;

        // 重新检测当前英雄是否装备了 id 为 SpeedTalisman 的护符。
        _hasTalismanEquipped = HasSpeedTalisman(hero);

        // 如果装备了护符，计算攻速加成；否则加成为 0。
        _currentSpeedBonus = _hasTalismanEquipped ? GetSpeedBonus(hero) : 0;

        // 装备状态、加成数值或 debug 配置变化后，尝试把新参数重套到已经见过的玩家武器上。
        if (_pendingReapplyWeapons
            || _hasTalismanEquipped != wasEquipped
            || System.Math.Abs(_currentSpeedBonus - previousSpeedBonus) > 0.0001)
        {
            _pendingReapplyWeapons = false;
            ReapplyKnownWeaponItems(hero, "state/config changed");
        }

        // 只有装备状态发生变化时才写日志，避免每帧刷屏。
        if (_hasTalismanEquipped != wasEquipped)
        {
            // 记录当前装备状态和计算出的百分比加成。
            Logger.Information(
                "SpeedTalisman equipped: {0}, speed bonus: {1:P0}",
                _hasTalismanEquipped,
                _currentSpeedBonus);
        }
    }

    // 尝试在英雄当前位置生成测试掉落物。
    private void TrySpawnTalisman(Hero hero)
    {
        // 已经成功处理过出生点掉落就直接返回，防止重复掉落。
        if (_hasSpawnedTalisman) return;

        try
        {
            // 先检查玩家身上是否已经有迅捷护符。有就不再掉新的护符，避免重复刷护符。
            var alreadyHasTalisman = HasSpeedTalisman(hero);

            // 玩家没有迅捷护符时，才创建一个新的护符掉落在英雄脚下。
            if (!alreadyHasTalisman)
            {
                // 创建一个游戏背包物品 InventItem，类型是 Talisman，id 是 SpeedTalisman。
                var talismanItem = new InventItem(new InventItemKind.Talisman(TalismanId.AsHaxeString()));

                // 在英雄脚下生成迅捷护符。
                SpawnItemDrop(hero, talismanItem, hero.cx, hero.cy);
            }

            // 创建一把原版武器 InventItem，类型是 Weapon，id 是 AdeleScythe。
            var testWeaponItem = new InventItem(new InventItemKind.Weapon(TestWeaponId.AsHaxeString()));

            // 在英雄右侧一格生成死神镰刀，方便继续测试不同武器的攻速手感。
            SpawnItemDrop(hero, testWeaponItem, hero.cx + 1, hero.cy);

            // 到这里说明生成成功，之后不再重复生成。
            _hasSpawnedTalisman = true;

            // 写入调试日志，包含生成位置，方便确认出生点是否触发。
            DebugLog(
                alreadyHasTalisman
                    ? $"Skipped {TalismanId} because hero already has it; spawned {TestWeaponId} at {hero.cx + 1},{hero.cy}"
                    : $"Spawned {TalismanId} at {hero.cx},{hero.cy}; spawned {TestWeaponId} at {hero.cx + 1},{hero.cy}");

            // 写入 Core 日志，便于从 log_latest.log 排查。
            Logger.Information(
                alreadyHasTalisman
                    ? "Skipped SpeedTalisman spawn because hero already has it; spawned {0} at hero position."
                    : "Spawned SpeedTalisman and {0} at hero position.",
                TestWeaponId);
        }
        catch (Exception ex)
        {
            // 生成失败不设置 _hasSpawnedTalisman，让 OnHeroUpdate 之后继续重试。
            DebugLog($"Spawn failed: {ex}");

            // 同时写入 Core 日志，包含异常对象。
            Logger.Warning("Failed to spawn SpeedTalisman: {0}", ex);
        }
    }

    // 在指定格子坐标生成一个可拾取掉落物。
    private static ItemDrop SpawnItemDrop(Hero hero, InventItem item, int cellX, int cellY)
    {
        // ItemDrop 构造函数需要一个 ref bool，游戏内部会用它表示掉落物是否应被销毁。
        var shouldDestroy = false;

        // 在英雄当前关卡和指定格子坐标上创建掉落物。
        // hero._level 是当前关卡对象，cellX/cellY 是希望掉落物出现的格子坐标。
        // true 表示这是一个可拾取掉落物。
        var drop = new ItemDrop(hero._level, cellX, cellY, item, true, new Ref<bool>(ref shouldDestroy));

        // 初始化掉落物。原版示例里也必须调用 init，否则可能崩溃。
        drop.init();

        // 标记为 loot 掉落，让游戏应用掉落物的表现和交互逻辑。
        drop.onDropAsLoot();

        // 让掉落物继承英雄当前 x 方向速度；这是官方示例里的做法，可减少掉落状态异常。
        drop.dx = hero.dx;

        // 返回创建出的掉落物，后续如果要调位置、速度或日志，可以继续使用。
        return drop;
    }

    // 判断当前英雄是否装备了本 Mod 的护符。
    private static bool HasSpeedTalisman(Hero hero)
    {
        // 1. 优先使用游戏公开的 Inventory API。
        // 反射确认 dc.tool.Inventory 有 hasTalisman() / getTalisman() / hasItem()。
        // 这比猜测 Hero 内部字段更稳定，也更接近游戏原本的装备判断。
        if (HasSpeedTalismanInInventory(hero.inventory, out var inventorySource))
        {
            // 第一次或来源变化时写日志，确认当前检测路径已经命中。
            LogDetectionSource(inventorySource);
            return true;
        }

        try
        {
            // 2. 兜底：如果公开 Inventory API 没命中，再读 Hero 底层动态字段。
            // 这段保留是为了兼容某些版本或特殊角色装备字段不同的情况。

            // hero.HashlinkObj 是底层 Hashlink 对象；转成字段对象后可以动态读私有/未代理字段。
            var heroFields = hero.HashlinkObj as IHashlinkFieldObject;

            // 原游戏字段名可能是 _talisman，也可能是 talisman；两个都尝试读取。
            var talisman = heroFields?.GetFieldValue("_talisman")
                        ?? heroFields?.GetFieldValue("talisman");

            // 如果当前没有护符对象，或者对象不能读取字段，就认为未装备。
            if (talisman is not IHashlinkFieldObject talismanFields) return false;

            // 护符对象内部通常会保存 _itemData/itemData，里面包含 id/name 等 CDB 数据。
            var itemData = talismanFields.GetFieldValue("_itemData") as IHashlinkFieldObject
                        ?? talismanFields.GetFieldValue("itemData") as IHashlinkFieldObject;

            // 从 itemData 里读取 id，并转换成 C# 字符串。
            var id = itemData?.GetFieldValue("id")?.ToString();

            // 只有 id 精确等于 SpeedTalisman 时，才认为装备了本 Mod 护符。
            var matched = string.Equals(id, TalismanId, StringComparison.Ordinal);
            if (matched)
            {
                LogDetectionSource("hero dynamic field _talisman/talisman");
            }
            return matched;
        }
        catch
        {
            // 动态字段读取失败时不要影响游戏流程，保守返回 false。
            return false;
        }
    }

    // 使用游戏公开的 Inventory API 检测是否装备迅捷护符。
    private static bool HasSpeedTalismanInInventory(Inventory? inventory, out string source)
    {
        // 默认来源说明，失败时调用方不会使用。
        source = "";

        // 没有 inventory 说明英雄装备栏还没准备好。
        if (inventory == null) return false;

        // 先尝试 getTalisman()，这是最直接的“当前装备护符”读取方式。
        try
        {
            // 如果当前装备了护符，游戏会返回一个 InventItem；否则可能返回 null 或抛异常。
            var talisman = inventory.getTalisman();

            // InventItem._itemData.id 是 CDB 里的物品 id。
            var id = talisman?._itemData.id.ToString();

            // id 命中 SpeedTalisman 就说明装备了我们的护符。
            if (string.Equals(id, TalismanId, StringComparison.Ordinal))
            {
                source = "inventory.getTalisman()._itemData.id";
                return true;
            }
        }
        catch (Exception ex)
        {
            // getTalisman 在没有护符或状态未初始化时可能失败，记录一次轻量原因即可。
            LogDetectionSource($"inventory.getTalisman failed: {ex.GetType().Name}");
        }

        // 再尝试 hasItem(id)。这个函数检查 inventory 是否含有某个 id 的物品。
        try
        {
            if (inventory.hasItem(TalismanId.AsHaxeString()))
            {
                source = "inventory.hasItem(SpeedTalisman)";
                return true;
            }
        }
        catch (Exception ex)
        {
            // hasItem 失败同样不影响游戏，只作为调试线索。
            LogDetectionSource($"inventory.hasItem failed: {ex.GetType().Name}");
        }

        // 最后尝试 hasTalisman()。它只能说明“有护符”，不能说明一定是本 Mod 护符。
        // 因此这里只记录日志，不直接返回 true，避免玩家装备其他护符时误触发攻速加成。
        try
        {
            if (inventory.hasTalisman())
            {
                LogDetectionSource("inventory.hasTalisman true, but id not matched");
            }
        }
        catch
        {
        }

        return false;
    }

    // 装备检测来源日志。只在来源变化时写，避免每帧刷屏。
    private static void LogDetectionSource(string source)
    {
        if (source == _lastDetectionSource) return;
        _lastDetectionSource = source;
        DebugLog($"Talisman detection: {source}");
    }

    // 根据当前 Boss 细胞数量计算最终攻速加成。
    private static double GetSpeedBonus(Hero hero)
    {
        // 读取当前 Boss Cell 数量，并且防止异常值低于 0。
        var bossCells = System.Math.Max(0, GetCurrentBossCells(hero));

        // 正式模式下 0 细胞没有任何加成，直接返回 0，ModifyWeaponStats 会因此完全跳过。
        if (!_usingDebugConfig && bossCells <= 0) return 0;

        // 最终加成 = 基础加成 + Boss Cell 数量 * 每细胞加成，并限制到最大加成。
        return System.Math.Min(_baseSpeedBonus + bossCells * _bonusPerBossCell, _maxSpeedBonus);
    }

    // 从玩家 user 数据中读取当前激活的 Boss 细胞数量。
    private static int GetCurrentBossCells(Hero hero)
    {
        try
        {
            // 读取英雄底层 Hashlink 字段。
            var heroFields = hero.HashlinkObj as IHashlinkFieldObject;

            // _user 保存玩家存档/运行状态数据。
            var user = heroFields?.GetFieldValue("_user") as IHashlinkFieldObject;

            // bossRuneActivated 是原游戏里表示当前 Boss Cell 的字段。
            var bossRuneActivated = user?.GetFieldValue("bossRuneActivated");

            // 根据实际返回类型做兼容转换；不同代理/字段可能返回 int、double 或 float。
            return bossRuneActivated switch
            {
                // 字段已经是 int 时直接返回。
                int value => value,

                // 字段是 double 时取整数部分。
                double value => (int)value,

                // 字段是 float 时取整数部分。
                float value => (int)value,

                // 读不到或类型不认识时，默认 0 细胞。
                _ => 0
            };
        }
        catch
        {
            // 读取失败时默认 0 细胞，不让异常影响游戏。
            return 0;
        }
    }

    // 修改刚创建出来的武器攻击参数。Hook_WeaponCreate 会调用这个函数。
    public static void ModifyWeaponStats(Weapon weapon, Hero hero, InventItem item)
    {
        // 只缓存当前玩家英雄的武器物品，避免调参时误改敌人或其它来源的武器数据。
        if (ReferenceEquals(hero, Game.Instance.HeroInstance))
        {
            RememberPlayerWeaponItem(item);
        }

        // 先记录 hook 是否真的进来。即使当前没装备护符，也能知道 tool.$Weapon.create 有没有被拦截到。
        if (_weaponHookLogCount < 20)
        {
            _weaponHookLogCount++;
            DebugLog(
                $"Weapon.create hook #{_weaponHookLogCount}: equipped={_hasTalismanEquipped}, bonus={_currentSpeedBonus:P0}, weaponType={weapon.GetType().FullName}, heroIsPlayer={ReferenceEquals(hero, Game.Instance.HeroInstance)}");
        }

        // 没装备护符或加成为 0 时，不修改任何武器数据。
        if (!_hasTalismanEquipped || _currentSpeedBonus <= 0) return;

        // 只修改当前玩家英雄的武器，避免影响敌人、分身或其他非玩家来源。
        if (!ReferenceEquals(hero, Game.Instance.HeroInstance)) return;

        // 真正修改武器物品的 CDB 武器数据。
        ApplySpeedBonusToWeaponItem(item, "Weapon.create");
    }

    // 记住玩家已经创建过的武器物品，便于配置变化后不切武器也能尝试重套参数。
    private static void RememberPlayerWeaponItem(InventItem item)
    {
        // 同一个物品引用只保存一次。
        foreach (var knownItem in _knownPlayerWeaponItems)
        {
            if (ReferenceEquals(knownItem, item)) return;
        }

        // 控制缓存长度，避免长时间游戏后列表无限增长。
        if (_knownPlayerWeaponItems.Count >= 24)
        {
            _knownPlayerWeaponItems.RemoveAt(0);
        }

        _knownPlayerWeaponItems.Add(item);
    }

    // 把当前攻速参数重新套到已缓存的玩家武器物品上。
    private static void ReapplyKnownWeaponItems(Hero hero, string reason)
    {
        // 没有护符或没有加成时不改；正式 0 细胞会走到这里并直接跳过。
        if (!_hasTalismanEquipped || _currentSpeedBonus <= 0) return;

        // 只允许玩家英雄触发重套，保持作用范围干净。
        if (!ReferenceEquals(hero, Game.Instance.HeroInstance)) return;

        var reapplied = 0;
        foreach (var item in _knownPlayerWeaponItems.ToArray())
        {
            if (ApplySpeedBonusToWeaponItem(item, reason)) reapplied++;
        }

        // 通知游戏装备数据已经变化，尽量让当前手持武器不需要手动切换也能刷新。
        ForceRefreshEquippedWeapons(hero);

        DebugLog($"Reapplied speed bonus to cached weapons: count={reapplied}, reason={reason}, bonus={_currentSpeedBonus:P0}");
    }

    // 尝试让游戏刷新当前装备武器。配置热加载后仅改 InventItem 数据不一定会影响已创建的 Weapon 实例。
    private static void ForceRefreshEquippedWeapons(Hero hero)
    {
        try
        {
            // updateHUD=false：不强制刷新 HUD。
            var updateHud = false;

            // duringHeroInit=false：不是英雄初始化期间。
            var duringHeroInit = false;

            // duringItemTransform=false：不是物品转化期间。
            var duringItemTransform = false;

            // 这是原版英雄在装备变化时会走的入口，通常会同步库存、武器管理器和武器实例。
            hero.onEquipedItemsChange(
                new Ref<bool>(ref updateHud),
                new Ref<bool>(ref duringHeroInit),
                new Ref<bool>(ref duringItemTransform));

            DebugLog("Requested hero.onEquipedItemsChange after speed config change.");
            return;
        }
        catch (Exception ex)
        {
            DebugLog($"hero.onEquipedItemsChange refresh failed: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            // 兜底：直接通知武器管理器装备更新，并尝试刷新两个主武器槽。
            var duringHeroInit = false;
            hero.weaponsManager.onEquippedItemsUpdated(new Ref<bool>(ref duringHeroInit));
            hero.weaponsManager.updateWeapon(0);
            hero.weaponsManager.updateWeapon(1);
            DebugLog("Requested weaponsManager refresh after speed config change.");
        }
        catch (Exception ex)
        {
            DebugLog($"weaponsManager refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // 修改 InventItem.getWeaponData().strikeChain。返回 true 表示至少成功处理过一次武器数据。
    private static bool ApplySpeedBonusToWeaponItem(InventItem item, string reason)
    {
        try
        {
            // 时间缩放系数。例如 25% 加成时 multiplier = 0.75，表示冷却/硬直缩短到 75%。
            var multiplier = 1.0 - _currentSpeedBonus;

            // 运行时 Weapon 对象没有 strikeChain 字段；真正的攻击段数据在 InventItem.getWeaponData() 返回的 CDB 武器数据里。
            var weaponData = item.getWeaponData();

            // strikeChain 是武器连段数据数组，每一段攻击都有自己的冷却、前摇、后摇、动画速度等。
            var strikeChain = weaponData?.strikeChain;

            if (_weaponModifyLogCount < 20)
            {
                _weaponModifyLogCount++;
                DebugLog(
                    $"Weapon modify #{_weaponModifyLogCount}: reason={reason}, weaponDataType={weaponData?.GetType().FullName ?? "null"}, strikeChainType={strikeChain?.GetType().FullName ?? "null"}, multiplier={multiplier:0.###}, workshopMax={_useWorkshopMaxSpeedTest}");
            }

            if (strikeChain == null)
            {
                DebugLog("Weapon modify skipped: item.getWeaponData().strikeChain is null");
                return false;
            }

            var modifiedStrikes = 0;
            var changedFields = 0;

            // ArrayObj 是 Haxe Array<Dynamic> 的代理类型，length/getDyn 可以稳定访问每个攻击段。
            for (var i = 0; i < strikeChain.length; i++)
            {
                var strike = strikeChain.getDyn(i);
                var changed = ApplySpeedBonusToStrike(strike, multiplier);
                if (changed > 0) modifiedStrikes++;
                changedFields += changed;
            }

            DebugLog(
                $"Weapon modify result: ArrayObj count={strikeChain.length}, modifiedStrikes={modifiedStrikes}, changedFields={changedFields}");

            return modifiedStrikes > 0;
        }
        catch (System.Exception ex)
        {
            // 修改武器参数失败时记录异常，但不继续抛出，避免因为某把武器结构特殊导致游戏崩溃。
            DebugLog($"Weapon modify exception: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // 对单个攻击段 strike 应用攻速加成。
    private static int ApplySpeedBonusToStrike(object? strike, double multiplier)
    {
        // strike 必须能作为 Hashlink 字段对象读取/写入，否则无法修改它的参数。
        if (strike is not IHashlinkFieldObject strikeFields)
        {
            if (_strikeModifyLogCount < 20)
            {
                _strikeModifyLogCount++;
                DebugLog($"Strike modify skipped: strikeType={strike?.GetType().FullName ?? "null"} is not IHashlinkFieldObject");
            }

            return 0;
        }

        // 收集本攻击段实际写入成功的字段名，写到日志里用于确认字段是否存在且可写。
        var changedFields = new System.Collections.Generic.List<string>();

        // 临时顶满模式：参考 Workshop「Rapid Attack & 0CD」的做法，直接清空关键时间字段。
        if (_useWorkshopMaxSpeedTest)
        {
            // coolDown：攻击段冷却直接为 0。
            if (SetNumberField(strikeFields, "coolDown", 0)) changedFields.Add("coolDown");

            // charge：攻击前摇/蓄力直接为 0。
            if (SetNumberField(strikeFields, "charge", 0)) changedFields.Add("charge");

            // dynamicCharge：动态蓄力也直接为 0。
            if (SetNumberField(strikeFields, "dynamicCharge", 0)) changedFields.Add("dynamicCharge");

            // lockCtrlAfter：攻击后锁控制直接为 0。
            if (SetNumberField(strikeFields, "lockCtrlAfter", 0)) changedFields.Add("lockCtrlAfter");

            // hitFrame：命中帧直接为 0，让判定尽早出现。
            if (SetNumberField(strikeFields, "hitFrame", 0)) changedFields.Add("hitFrame");

            // startUp：部分武器使用这个字段表示前摇。
            if (SetNumberField(strikeFields, "startUp", 0)) changedFields.Add("startUp");

            // recovery：部分武器使用这个字段表示后摇。
            if (SetNumberField(strikeFields, "recovery", 0)) changedFields.Add("recovery");

            // animSpd：强制动画速度至少为 2，避免逻辑很快但动画仍显得慢。
            if (EnsureNumberFieldAtLeast(strikeFields, "animSpd", _workshopAnimSpeedFloor)) changedFields.Add("animSpd");

            if (_strikeModifyLogCount < 20)
            {
                _strikeModifyLogCount++;
                DebugLog($"Strike max modify #{_strikeModifyLogCount}: changed={changedFields.Count}, fields={string.Join(",", changedFields)}");
            }

            // 顶满模式已经处理完，不再执行百分比缩放。
            return changedFields.Count;
        }

        // coolDown：攻击段冷却时间，缩短后武器能更快进入下一次攻击。
        if (ScaleDoubleField(strikeFields, "coolDown", multiplier)) changedFields.Add("coolDown");

        // charge：蓄力/前摇相关时间，缩短后出手更快。
        if (ScaleDoubleField(strikeFields, "charge", multiplier)) changedFields.Add("charge");

        // lockCtrlAfter：攻击后锁控制时间，缩短后玩家能更快恢复操作。
        if (ScaleDoubleField(strikeFields, "lockCtrlAfter", multiplier)) changedFields.Add("lockCtrlAfter");

        // hitFrame：命中帧，缩短后攻击判定更早出现。
        if (ScaleDoubleField(strikeFields, "hitFrame", multiplier)) changedFields.Add("hitFrame");

        // startUp：前摇字段，部分武器会使用这个名字。
        if (ScaleDoubleField(strikeFields, "startUp", multiplier)) changedFields.Add("startUp");

        // recovery：后摇字段，部分武器会使用这个名字。
        if (ScaleDoubleField(strikeFields, "recovery", multiplier)) changedFields.Add("recovery");

        // animSpd：动画速度。时间字段缩短的同时小幅提高动画速度，让表现更贴近实际攻速但不至于接近顶满。
        if (ScaleDoubleField(strikeFields, "animSpd", 1.0 + _currentSpeedBonus * _animationSpeedBonusScale)) changedFields.Add("animSpd");

        if (_strikeModifyLogCount < 20)
        {
            _strikeModifyLogCount++;
            DebugLog($"Strike scale modify #{_strikeModifyLogCount}: changed={changedFields.Count}, fields={string.Join(",", changedFields)}");
        }

        return changedFields.Count;
    }

    // 如果字段存在，就按它原来的数值类型写入指定数值。
    private static bool SetNumberField(IHashlinkFieldObject fields, string name, double number)
    {
        try
        {
            // 先读取原字段，用原字段类型决定写回 int/float/double。
            var value = GetOriginalFieldValue(fields, name);
            switch (value)
            {
                case double:
                    fields.SetFieldValue(name, number);
                    return true;
                case float:
                    fields.SetFieldValue(name, (float)number);
                    return true;
                case int:
                    fields.SetFieldValue(name, (int)System.Math.Round(number));
                    return true;
            }
        }
        catch
        {
            // 字段不存在就跳过，兼容不同武器结构。
        }

        return false;
    }

    // 如果字段存在且低于指定下限，就把它提高到下限。
    private static bool EnsureNumberFieldAtLeast(IHashlinkFieldObject fields, string name, double minimum)
    {
        try
        {
            // 读取原字段值。
            var value = GetOriginalFieldValue(fields, name);
            switch (value)
            {
                case double d when d < minimum:
                    fields.SetFieldValue(name, minimum);
                    return true;
                case float f when f < minimum:
                    fields.SetFieldValue(name, (float)minimum);
                    return true;
                case int i when i < minimum:
                    fields.SetFieldValue(name, (int)System.Math.Round(minimum));
                    return true;
            }
        }
        catch
        {
            // 字段不存在就跳过。
        }

        return false;
    }

    // 按 multiplier 缩放某个字段，兼容 double/float/int 三种常见数值类型。
    private static bool ScaleDoubleField(IHashlinkFieldObject fields, string name, double multiplier)
    {
        try
        {
            // 从原始字段值计算新值，避免配置热加载或武器重建时在已缩放结果上反复相乘。
            var value = GetOriginalFieldValue(fields, name);

            // 根据字段实际类型分别处理，避免类型不匹配。
            switch (value)
            {
                // double 字段直接乘以 double 系数。
                case double d:
                    fields.SetFieldValue(name, d * multiplier);
                    return true;

                // float 字段要把 multiplier 转回 float，避免写入类型不兼容。
                case float f:
                    fields.SetFieldValue(name, f * (float)multiplier);
                    return true;

                // int 字段缩放后四舍五入，并且限制最小值为 0，避免负帧数/负冷却。
                case int i:
                    fields.SetFieldValue(name, System.Math.Max(0, (int)System.Math.Round(i * multiplier)));
                    return true;
            }
        }
        catch
        {
            // 字段不存在或不能写入时直接忽略。不同武器的 strike 字段不一定完全一致。
        }

        return false;
    }

    // 读取某个攻击段字段的原始值。第一次读取时缓存，后续调参都从这个原始值重新计算。
    private static object? GetOriginalFieldValue(IHashlinkFieldObject fields, string name)
    {
        // RuntimeHelpers.GetHashCode 基于对象身份，不会因为对象重写 Equals 而改变。
        var objectKey = RuntimeHelpers.GetHashCode(fields);

        // 找到或创建当前攻击段对象的字段原始值表。
        if (!_originalStrikeFieldValues.TryGetValue(objectKey, out var fieldValues))
        {
            fieldValues = [];
            _originalStrikeFieldValues[objectKey] = fieldValues;
        }

        // 同一个字段已经记录过原始值，就直接返回。
        if (fieldValues.TryGetValue(name, out var originalValue))
        {
            return originalValue;
        }

        // 第一次读取字段时，当前值就是原始值。
        originalValue = fields.GetFieldValue(name);
        fieldValues[name] = originalValue;
        return originalValue;
    }

    // 如果 debug 配置文件存在且保存过，就把新档位读进内存；不存在时切回正式模式。
    private static void ReloadConfigIfChanged(bool force)
    {
        try
        {
            // 没有配置路径时直接保持正式模式。
            if (_configPath == null)
            {
                ApplyProductionConfig();
                return;
            }

            // debug_speed_config.json 不存在：正式模式，不做热调参，按 Boss 细胞计算，0 细胞无加成。
            if (!System.IO.File.Exists(_configPath))
            {
                if (force || _usingDebugConfig)
                {
                    ApplyProductionConfig();
                    _configLastWriteTimeUtc = default;
                    _pendingReapplyWeapons = true;
                    DebugLog("Debug config not found; using production boss-cell scaling.");
                }

                return;
            }

            // 读取文件最后修改时间；没变就不用重新解析。
            var lastWriteTimeUtc = System.IO.File.GetLastWriteTimeUtc(_configPath);
            if (!force && lastWriteTimeUtc == _configLastWriteTimeUtc) return;

            // 读取并解析 JSON。解析失败会进入 catch，保留上一组有效配置。
            var json = System.IO.File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<SpeedTalismanConfig>(json);
            if (config == null) return;

            // SpeedLevel 是唯一需要手动编辑的参数；范围固定在 1 到 5。
            var speedLevel = System.Math.Clamp(config.SpeedLevel <= 0 ? 2 : config.SpeedLevel, 1, 5);

            // 根据档位套用内部参数，避免每次测试都要手动改一堆小数。
            ApplySpeedLevel(speedLevel);
            _usingDebugConfig = true;
            _pendingReapplyWeapons = true;

            // 记录成功读取的修改时间，避免下一秒重复读同一个文件。
            _configLastWriteTimeUtc = lastWriteTimeUtc;

            // 写一行摘要，方便你看日志确认游戏已经吃到新参数。
            DebugLog(
                $"Config loaded: level={speedLevel}, base={_baseSpeedBonus:P0}, perCell={_bonusPerBossCell:P0}, max={_maxSpeedBonus:P0}, animScale={_animationSpeedBonusScale:0.##}, workshopMax={_useWorkshopMaxSpeedTest}, workshopAnimFloor={_workshopAnimSpeedFloor:0.##}");
        }
        catch (Exception ex)
        {
            // 配置文件可能正在保存、写了一半或格式错误；保留旧参数即可。
            DebugLog($"Config reload failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // 正式模式：没有 debug_speed_config.json 时使用。0 细胞无加成；之后按 Boss 细胞递增。
    private static void ApplyProductionConfig()
    {
        _baseSpeedBonus = DefaultBaseSpeedBonus;
        _bonusPerBossCell = DefaultBonusPerBossCell;
        _maxSpeedBonus = DefaultMaxSpeedBonus;
        _animationSpeedBonusScale = DefaultAnimationSpeedBonusScale;
        _useWorkshopMaxSpeedTest = DefaultUseWorkshopMaxSpeedTest;
        _workshopAnimSpeedFloor = DefaultWorkshopAnimSpeedFloor;
        _usingDebugConfig = false;
    }

    // 根据 1 到 5 档套用攻速参数。
    private static void ApplySpeedLevel(int speedLevel)
    {
        // 默认不开启顶满测试；只有第 5 档会切到 Workshop 风格 0CD。
        _useWorkshopMaxSpeedTest = false;
        _workshopAnimSpeedFloor = DefaultWorkshopAnimSpeedFloor;

        switch (speedLevel)
        {
            case 1:
                // 轻微加速：适合长期游玩，不容易破坏节奏。
                _baseSpeedBonus = 0.05;
                _bonusPerBossCell = 0.025;
                _maxSpeedBonus = 0.15;
                _animationSpeedBonusScale = 0.30;
                break;

            case 2:
                // 当前推荐档：有提升，但比之前 25% 档温和很多。
                _baseSpeedBonus = 0.10;
                _bonusPerBossCell = 0.05;
                _maxSpeedBonus = 0.35;
                _animationSpeedBonusScale = 0.50;
                break;

            case 3:
                // 明显加速：测试爽感，但还保留一些武器节奏。
                _baseSpeedBonus = 0.15;
                _bonusPerBossCell = 0.075;
                _maxSpeedBonus = 0.50;
                _animationSpeedBonusScale = 0.65;
                break;

            case 4:
                // 高速档：接近强力 Mod 手感，但不直接清零冷却。
                _baseSpeedBonus = 0.20;
                _bonusPerBossCell = 0.10;
                _maxSpeedBonus = 0.65;
                _animationSpeedBonusScale = 0.80;
                break;

            case 5:
                // 顶满测试档：模拟 Workshop 0CD 风格，用来快速验证上限手感。
                _baseSpeedBonus = 0.85;
                _bonusPerBossCell = 0.00;
                _maxSpeedBonus = 0.85;
                _animationSpeedBonusScale = 1.00;
                _useWorkshopMaxSpeedTest = true;
                _workshopAnimSpeedFloor = 2.0;
                break;
        }
    }

    // 写入 Mod 自己的调试日志文件。
    private static void DebugLog(string message)
    {
        try
        {
            // 如果 Initialize 还没设置日志路径，就直接跳过。
            if (_debugLogPath == null) return;

            // 追加一行带时间戳的日志，方便和游戏日志互相对照。
            System.IO.File.AppendAllText(_debugLogPath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不能影响游戏运行，所以吞掉异常。
        }
    }
}

// 热加载配置文件的数据结构。属性名会原样写入 speed_config.json。
internal sealed class SpeedTalismanConfig
{
    // 攻速档位：1=轻微，2=推荐，3=明显，4=高速，5=顶满测试。
    public int SpeedLevel { get; set; } = 2;
}

// 这个静态类专门保存 Weapon.create 的 hook 委托和 hook 方法。
internal static class Hook_WeaponCreate
{
    // 原始 tool.$Weapon.create 的函数签名：传入英雄和背包物品，返回创建出的武器对象。
    public delegate Weapon orig_create(Hero hero, InventItem item);

    // Hook 方法。ModCore 会把原函数 orig 和原始参数传进来。
    public static Weapon Hook_create(orig_create orig, Hero hero, InventItem item)
    {
        // 先调用原版逻辑创建武器，保证游戏自己的武器初始化完整执行。
        var weapon = orig(hero, item);

        // 创建完成后再修改武器攻击参数，这样能拿到完整的 strikeChain。
        ModEntry.ModifyWeaponStats(weapon, hero, item);

        // 返回可能被修改过参数的武器对象给游戏。
        return weapon;
    }
}
