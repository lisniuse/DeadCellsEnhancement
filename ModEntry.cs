// Dead Cells 原始游戏代理命名空间：提供 Game、Hero、Weapon、InventItem 等游戏对象的 C# 代理。
using dc;
using dc.en;
using dc.tool;
using dc.tool.mod;
using DcPrGame = dc.pr.Game;
using Hook_PrGame = dc.pr.Hook_Game;

// Hashlink 代理对象接口：用于读取/写入游戏对象里没有直接暴露成 C# 属性的动态字段。
using Hashlink.Proxy.Objects;

// HaxeProxy 运行时工具：Ref<T> 用于调用需要 Haxe 引用参数的游戏函数。
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
namespace DeadCellsEnhancement;

// ModEntry 是这个 Mod 的入口类。
// ModBase 提供 Info、Logger 等基础能力。
// IOnAfterLoadingAssets 表示游戏资源加载后会回调 OnAfterLoadingAssets。
// IOnHeroUpdate 表示每帧英雄更新时会回调 OnHeroUpdate。
public class ModEntry(ModInfo info) : ModBase(info),
    IOnAfterLoadingAssets,
    IOnHeroUpdate
{
    // CDB 里新增变异的 id。判断玩家是否选择本变异时就用这个 id 匹配。
    private const string SpeedMutationId = "P_SpeedInstinct";

    // 正式模式基础攻速加成：0 表示 0 细胞时完全没有加成，直接绕过武器修改。
    private const double DefaultBaseSpeedBonus = 0.00;

    // 旧版线性档位参数保留为日志字段使用；真实加成现在统一走 GetSpeedBonusForBossCells 的明确档位表。
    private const double DefaultBonusPerBossCell = 0.00;

    // 默认最大攻速加成上限：0.50 表示最多把相关时间参数缩短 50%。
    private const double DefaultMaxSpeedBonus = 0.50;

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

    // 当前运行时是否启用顶满测试模式。正式细胞匹配档位不会启用它，保留给以后内部调试使用。
    private static bool _useWorkshopMaxSpeedTest = DefaultUseWorkshopMaxSpeedTest;

    // 当前运行时顶满测试动画速度下限。正式细胞匹配档位不会使用它，保留给以后内部调试使用。
    private static double _workshopAnimSpeedFloor = DefaultWorkshopAnimSpeedFloor;

    // 当前帧检测到玩家是否选择了本 Mod 的迅捷变异。
    private static bool _hasSpeedMutation;

    // 当前生效的攻速加成，只有选择迅捷变异时才大于 0。
    private static double _currentSpeedBonus;

    // 调试日志路径。Initialize 中根据 ModRoot 设置，指向 coremod/mods/DeadCellsEnhancement/moddbg.log。
    private static string? _debugLogPath;

    // 调试热加载配置文件路径。正式发布时没有这个文件；存在时优先使用它的 SpeedLevel 档位。
    private static string? _configPath;

    // 配置文件上次读取到的修改时间。文件保存后时间变化，Mod 就会重新读入参数。
    private static DateTime _configLastWriteTimeUtc;

    // 当前是否正在使用 debug_speed_config.json。false 表示正式模式：按 Boss 细胞计算，0 细胞无加成。
    private static bool _usingDebugConfig;

    // 调试配置模拟的 Boss 细胞数。debug_speed_config.json 存在时，SpeedLevel 会直接写到这里。
    private static int _debugBossCells;

    // 配置文件重载计时器。每帧都读文件太浪费，所以按固定间隔检查一次。
    private static double _configReloadTimer;

    // 记录上一次变异检测使用的来源，避免每帧重复写相同日志。
    private static string? _lastDetectionSource;

    // 记录上一次写入日志的 Boss 细胞读取结果，避免每帧重复刷同样的细胞数。
    private static int? _lastLoggedBossCells;

    // 记录上一次 Boss 细胞读取来源。数值相同但来源变化时也写日志，方便排查正式模式读错字段。
    private static string? _lastLoggedBossCellSource;

    // 当前正在运行的原游戏 dc.pr.Game 实例。Hero 身上不稳定暴露 user，所以从 Game.init 缓存更可靠。
    private static DcPrGame? _currentPrGame;

    // 当前 Boss 细胞数是从哪个字段读到的。LogBossCellsIfChanged 会把它写进日志。
    private static string _currentBossCellSource = "not read yet";

    // 武器创建 hook 日志计数器。武器创建可能很频繁，所以只记录前几次，避免日志刷屏。
    private static int _weaponHookLogCount;

    // 武器修改日志计数器。只记录前几把武器，方便确认修改链路是否走通。
    private static int _weaponModifyLogCount;

    // 攻击段字段修改日志计数器。只记录前几个攻击段，方便确认具体字段是否写入。
    private static int _strikeModifyLogCount;

    // 已经经过 Weapon.create 的玩家武器物品。配置热加载或变异选择状态变化后，会尝试给这些武器重新套用参数。
    private static readonly System.Collections.Generic.List<InventItem> _knownPlayerWeaponItems = [];

    // 每个攻击段字段的原始值缓存。动态调参时必须从原始值重新计算，不能在已缩放结果上反复相乘。
    private static readonly System.Collections.Generic.Dictionary<int, System.Collections.Generic.Dictionary<string, object?>> _originalStrikeFieldValues = [];

    // 配置变化后设置为 true，让 OnHeroUpdate 在下一帧尝试把新参数套到已缓存武器上。
    private static bool _pendingReapplyWeapons;

    // 装备刷新剩余次数。武器实例有时不会在一次 onEquipedItemsChange 后立刻完全重建，所以连续几帧补刷。
    private static int _pendingWeaponRefreshFrames;

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

        // 注册本 Mod 的语言包名字。GetText 会按 lang/DeadCellsEnhancement.<lang>.mo 查找翻译。
        GetText.Instance.RegisterMod("DeadCellsEnhancement");

        // 写入 Core 的 Serilog 日志，便于在 coremod/logs/log_latest.log 中排查。
        Logger.Information("DeadCellsEnhancement initialized.");

        // 获取 Hashlink hook 管理器实例，用它来拦截游戏里的 Haxe/Hashlink 函数。
        var hooks = HashlinkHooks.Instance;

        // 拦截 tool.$Weapon.create：每次游戏根据 InventItem 创建 Weapon 时，都会先进入 Hook_create。
        // 这样可以在武器对象刚创建出来时修改它的 strikeChain 攻击参数。
        hooks.CreateHook("tool.$Weapon", "create", Hook_WeaponCreate.Hook_create).Enable();

        // 记录原游戏 pr.Game 实例。正式模式的 Boss Cell 数在 game.user.bossRuneActivated 上最稳定。
        Hook_PrGame.init += Hook_PrGame_init;
        Hook_PrGame.onDispose += Hook_PrGame_onDispose;
    }

    // 原游戏 dc.pr.Game 初始化时调用；缓存 self，之后读取正式 Boss Cell 难度使用它。
    private static void Hook_PrGame_init(Hook_PrGame.orig_init orig, DcPrGame self)
    {
        _currentPrGame = self;
        DebugLog("Captured dc.pr.Game instance for production boss-cell scaling.");
        orig(self);
    }

    // 原游戏 dc.pr.Game 销毁时调用；如果销毁的是当前缓存实例，就清空引用，避免下局读到旧对象。
    private static void Hook_PrGame_onDispose(Hook_PrGame.orig_onDispose orig, DcPrGame self)
    {
        if (ReferenceEquals(_currentPrGame, self))
        {
            _currentPrGame = null;
            DebugLog("Cleared cached dc.pr.Game instance.");
        }

        orig(self);
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
        Logger.Information("Loaded DeadCellsEnhancement resources from {0}", pakPath);
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

        // 记录上一帧是否选择了迅捷变异，用于判断状态是否发生变化。
        var hadSpeedMutation = _hasSpeedMutation;

        // 记录上一帧的加成数值，用于判断配置文件或细胞数导致的加成变化。
        var previousSpeedBonus = _currentSpeedBonus;

        // 重新检测当前英雄是否已经选择了 id 为 P_SpeedInstinct 的变异。
        _hasSpeedMutation = HasSpeedMutation(hero);

        // 如果选择了变异，计算攻速加成；否则加成为 0。
        _currentSpeedBonus = _hasSpeedMutation ? GetSpeedBonus(hero) : 0;

        // 变异状态、加成数值或 debug 配置变化后，尝试把新参数重套到已经见过的玩家武器上。
        if (_pendingReapplyWeapons
            || _hasSpeedMutation != hadSpeedMutation
            || System.Math.Abs(_currentSpeedBonus - previousSpeedBonus) > 0.0001)
        {
            _pendingReapplyWeapons = false;
            ReapplyKnownWeaponItems(hero, "state/config changed");
        }

        // 如果刚发生过加成变化或恢复，连续几帧要求游戏刷新当前装备武器。
        // 这能处理“数据已经恢复，但当前手持 Weapon 实例还保留旧攻速”的情况。
        if (_pendingWeaponRefreshFrames > 0)
        {
            _pendingWeaponRefreshFrames--;
            ForceRefreshEquippedWeapons(hero, $"pending refresh frames left={_pendingWeaponRefreshFrames}");
        }

        // 只有变异选择状态发生变化时才写日志，避免每帧刷屏。
        if (_hasSpeedMutation != hadSpeedMutation)
        {
            // 记录当前变异状态和计算出的百分比加成。
            Logger.Information(
                "Speed mutation selected: {0}, speed bonus: {1:P0}",
                _hasSpeedMutation,
                _currentSpeedBonus);
        }
    }

    // 判断当前英雄是否已经选择了本 Mod 的迅捷变异。
    private static bool HasSpeedMutation(Hero hero)
    {
        // 变异在游戏内部也是 InventItem，类型是 Perk；选中后通常会被加入英雄 inventory。
        // 因此这里优先走 inventory.hasItem(id)，和原版通过物品 id 查询背包内容的方式一致。
        try
        {
            if (hero.inventory != null && hero.inventory.hasItem(SpeedMutationId.AsHaxeString()))
            {
                LogDetectionSource($"inventory.hasItem({SpeedMutationId})");
                return true;
            }
        }
        catch (Exception ex)
        {
            // 如果当前版本的 hasItem 不支持 Perk，也不要影响游戏；日志会提示我们继续补字段探测。
            LogDetectionSource($"inventory.hasItem({SpeedMutationId}) failed: {ex.GetType().Name}");
        }

        // 公开 API 没命中时，尝试从 hero/inventory 的动态字段里查找变异 id。
        // 这段是兜底兼容：不同游戏版本可能把已选变异放在 perks、_perks 或 inventory 内部数组里。
        return HasSpeedMutationInDynamicFields(hero);
    }

    // 从动态字段兜底查找迅捷变异 id。
    private static bool HasSpeedMutationInDynamicFields(Hero hero)
    {
        try
        {
            // 先检查 hero 自身，再检查 inventory 对象；任意一边找到 P_SpeedInstinct 都认为变异已选。
            return ObjectGraphContainsItemId(hero.HashlinkObj, SpeedMutationId, "hero")
                || ObjectGraphContainsItemId(hero.inventory?.HashlinkObj, SpeedMutationId, "inventory");
        }
        catch (Exception ex)
        {
            // 动态字段读取失败时保守返回 false，并记录一次轻量线索。
            LogDetectionSource($"dynamic perk scan failed: {ex.GetType().Name}");
            return false;
        }
    }

    // 在一个 Hashlink 对象的浅层字段里查找指定物品 id。
    private static bool ObjectGraphContainsItemId(object? root, string itemId, string sourceName)
    {
        // 只做浅层扫描，避免每帧深度遍历整个游戏对象图导致性能和循环引用问题。
        if (root is not IHashlinkFieldObject fields) return false;

        foreach (var fieldName in new[] { "perks", "_perks", "items", "_items", "inventory", "_inventory" })
        {
            var fieldValue = fields.GetFieldValue(fieldName);
            if (ValueContainsItemId(fieldValue, itemId))
            {
                LogDetectionSource($"{sourceName}.{fieldName} contains {itemId}");
                return true;
            }
        }

        return false;
    }

    // 判断某个字段值、数组或物品对象里是否包含指定 id。
    private static bool ValueContainsItemId(object? value, string itemId)
    {
        // 空字段肯定不命中。
        if (value == null) return false;

        // 字段本身就是字符串时直接比较。
        if (string.Equals(value.ToString(), itemId, StringComparison.Ordinal)) return true;

        // InventItem 有 _itemData.id，命中即可。
        if (TryReadItemId(value, out var directId) && string.Equals(directId, itemId, StringComparison.Ordinal)) return true;

        // Haxe 数组在 C# 侧通常实现 IEnumerable；逐个检查其中的元素。
        if (value is System.Collections.IEnumerable enumerable and not string)
        {
            foreach (var element in enumerable)
            {
                if (TryReadItemId(element, out var elementId) && string.Equals(elementId, itemId, StringComparison.Ordinal)) return true;
                if (string.Equals(element?.ToString(), itemId, StringComparison.Ordinal)) return true;
            }
        }

        return false;
    }

    // 从 InventItem 或类似对象里读取 _itemData/itemData.id。
    private static bool TryReadItemId(object? value, out string? id)
    {
        id = null;

        if (value is not IHashlinkFieldObject fields) return false;

        var itemData = fields.GetFieldValue("_itemData") as IHashlinkFieldObject
                    ?? fields.GetFieldValue("itemData") as IHashlinkFieldObject;

        id = itemData?.GetFieldValue("id")?.ToString();
        return !string.IsNullOrEmpty(id);
    }

    // 变异检测来源日志。只在来源变化时写，避免每帧刷屏。
    private static void LogDetectionSource(string source)
    {
        if (source == _lastDetectionSource) return;
        _lastDetectionSource = source;
        DebugLog($"Speed mutation detection: {source}");
    }

    // 根据当前 Boss 细胞数量计算最终攻速加成。
    private static double GetSpeedBonus(Hero hero)
    {
        // 调试模式下，SpeedLevel 直接等于“模拟几细胞”；正式模式下，读取游戏真实 Boss Cell 数。
        var bossCells = _usingDebugConfig
            ? System.Math.Clamp(_debugBossCells, 0, 5)
            : System.Math.Max(0, GetCurrentBossCells(hero));

        // 细胞数来源影响最终加成，写一次变化日志方便确认正式模式是否读到了真实难度。
        LogBossCellsIfChanged(bossCells);

        // 正式模式下 0 细胞没有任何加成，直接返回 0，ModifyWeaponStats 会因此完全跳过。
        // 调试模式下 SpeedLevel = 0 也同样没有任何加成，方便和原版手感做 A/B 对比。
        if (bossCells <= 0) return 0;

        // 最终加成按明确档位表计算，避免线性公式和测试直觉不一致。
        return GetSpeedBonusForBossCells(bossCells);
    }

    // 把 Boss 细胞数或调试 SpeedLevel 映射成真正的攻速加成。
    private static double GetSpeedBonusForBossCells(int bossCells)
    {
        // 先把输入夹在 0-5，防止异常存档值或手写配置值越界。
        var clampedBossCells = System.Math.Clamp(bossCells, 0, 5);

        // 档位表：
        // 0 细胞 = 0%，完全不修改武器。
        // 1 细胞 = 20%，开始有明显但不夸张的加速。
        // 2 细胞 = 30%，比 1 细胞再快一档。
        // 3 细胞 = 40%，这里补齐用户未写的 3 档，让它和 4 细胞同档。
        // 4 细胞 = 40%，保持高难度但不继续膨胀。
        // 5 细胞 = 50%，最高难度给最明显加成。
        var speedBonus = clampedBossCells switch
        {
            0 => 0.00,
            1 => 0.20,
            2 => 0.30,
            3 => 0.40,
            4 => 0.40,
            5 => 0.50,
            _ => 0.00
        };

        // 仍然套一层最大值保护，方便以后只改上限也能整体压住手感。
        return System.Math.Min(speedBonus, _maxSpeedBonus);
    }

    // 从玩家 user 数据中读取当前激活的 Boss 细胞数量。
    private static int GetCurrentBossCells(Hero hero)
    {
        try
        {
            // 最可靠路径：pr.Game.user.bossRuneActivated。
            // core 日志死亡结算里的 "bossRune" 就来自这个用户运行数据。
            if (TryReadBossCellsFromGame(_currentPrGame, out var gameBossCells, out var gameSource))
            {
                _currentBossCellSource = gameSource;
                return gameBossCells;
            }

            // 读取英雄底层 Hashlink 字段。
            var heroFields = hero.HashlinkObj as IHashlinkFieldObject;

            // 某些运行阶段 Hero 的动态字段里也可能挂着 pr.Game；有就作为第二优先级读取。
            foreach (var gameFieldName in new[] { "game", "_game" })
            {
                try
                {
                    if (heroFields?.GetFieldValue(gameFieldName) is DcPrGame heroGame
                        && TryReadBossCellsFromGame(heroGame, out var heroGameBossCells, out var heroGameSource))
                    {
                        _currentPrGame = heroGame;
                        _currentBossCellSource = $"hero.{gameFieldName}->{heroGameSource}";
                        return heroGameBossCells;
                    }
                }
                catch
                {
                    // 字段不存在或当前阶段不可读时，继续尝试其它来源。
                }
            }

            // _user 保存玩家存档/运行状态数据。
            var user = heroFields?.GetFieldValue("_user") as IHashlinkFieldObject;

            // 不同运行阶段/游戏版本里，Boss Cell 字段名字不完全一致。
            // 日志里能看到存档摘要字段叫 bossRune；旧探测用过 bossRuneActivated。
            // 这里按多个候选字段依次读取，读到第一个有效数值就返回。
            foreach (var fieldName in new[] { "bossRuneActivated", "bossRune", "_bossRune", "difficulty" })
            {
                if (TryReadIntField(user, fieldName, out var userValue))
                {
                    _currentBossCellSource = $"hero._user.{fieldName}";
                    return userValue;
                }

                if (TryReadIntField(heroFields, fieldName, out var heroValue))
                {
                    _currentBossCellSource = $"hero.{fieldName}";
                    return heroValue;
                }
            }

            // 读不到任何候选字段时，默认 0 细胞。
            _currentBossCellSource = "not found, fallback 0";
            return 0;
        }
        catch
        {
            // 读取失败时默认 0 细胞，不让异常影响游戏。
            _currentBossCellSource = "read failed, fallback 0";
            return 0;
        }
    }

    // 从原游戏 pr.Game 上读取 Boss Cell 数。直接读强类型属性，比猜 Hashlink 动态字段稳定。
    private static bool TryReadBossCellsFromGame(DcPrGame? game, out int bossCells, out string source)
    {
        bossCells = 0;
        source = "game missing";
        if (game == null) return false;

        try
        {
            var user = game.user;
            if (user != null)
            {
                bossCells = user.bossRuneActivated;
                source = "game.user.bossRuneActivated";
                return true;
            }
        }
        catch
        {
            // 有些加载阶段 user 可能暂时为空或 Hashlink 访问失败，继续尝试 data.sUser。
        }

        try
        {
            var savedUser = game.data?.sUser;
            if (savedUser != null)
            {
                bossCells = savedUser.bossRuneActivated;
                source = "game.data.sUser.bossRuneActivated";
                return true;
            }
        }
        catch
        {
            // 两条强类型路径都失败时，交给调用方走旧动态字段兜底。
        }

        source = "game user fields unavailable";
        return false;
    }

    // 尝试从 Hashlink 字段对象里读取一个整数型配置字段。
    private static bool TryReadIntField(IHashlinkFieldObject? fields, string fieldName, out int value)
    {
        value = 0;
        if (fields == null) return false;

        try
        {
            var rawValue = fields.GetFieldValue(fieldName);
            switch (rawValue)
            {
                case int intValue:
                    value = intValue;
                    return true;

                case double doubleValue:
                    value = (int)doubleValue;
                    return true;

                case float floatValue:
                    value = (int)floatValue;
                    return true;
            }
        }
        catch
        {
            // 字段不存在或类型无法读取时，交给下一个候选字段继续尝试。
        }

        return false;
    }

    // Boss 细胞读取结果变化时写日志。
    private static void LogBossCellsIfChanged(int bossCells)
    {
        if (_lastLoggedBossCells == bossCells && _lastLoggedBossCellSource == _currentBossCellSource) return;
        _lastLoggedBossCells = bossCells;
        _lastLoggedBossCellSource = _currentBossCellSource;
        DebugLog(_usingDebugConfig
            ? $"Boss cells for speed bonus: debug SpeedLevel={bossCells}"
            : $"Boss cells for speed bonus: production bossCells={bossCells}, source={_currentBossCellSource}");
    }

    // 修改刚创建出来的武器攻击参数。Hook_WeaponCreate 会调用这个函数。
    public static void ModifyWeaponStats(Weapon weapon, Hero hero, InventItem item)
    {
        // 只缓存当前玩家英雄的武器物品，避免调参时误改敌人或其它来源的武器数据。
        if (ReferenceEquals(hero, Game.Instance.HeroInstance))
        {
            RememberPlayerWeaponItem(item);
        }

        // 先记录 hook 是否真的进来。即使当前没选择迅捷变异，也能知道 tool.$Weapon.create 有没有被拦截到。
        if (_weaponHookLogCount < 20)
        {
            _weaponHookLogCount++;
            DebugLog(
                $"Weapon.create hook #{_weaponHookLogCount}: mutation={_hasSpeedMutation}, bonus={_currentSpeedBonus:P0}, weaponType={weapon.GetType().FullName}, heroIsPlayer={ReferenceEquals(hero, Game.Instance.HeroInstance)}");
        }

        // 没选择迅捷变异或加成为 0 时，不修改任何武器数据。
        if (!_hasSpeedMutation || _currentSpeedBonus <= 0) return;

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
        // 只允许玩家英雄触发重套，保持作用范围干净。
        if (!ReferenceEquals(hero, Game.Instance.HeroInstance)) return;

        // 没有迅捷变异或加成为 0 时，把之前改过的武器恢复到缓存的原始值。
        // 这样重置变异、切回 0 细胞或 debug 配置改成 0 时，不会留下旧攻速。
        if (!_hasSpeedMutation || _currentSpeedBonus <= 0)
        {
            var restored = 0;
            foreach (var item in _knownPlayerWeaponItems.ToArray())
            {
                if (RestoreWeaponItem(item, reason)) restored++;
            }

            RequestWeaponRefresh(hero, "restore");
            DebugLog($"Restored cached weapons: count={restored}, reason={reason}");
            return;
        }

        var reapplied = 0;
        foreach (var item in _knownPlayerWeaponItems.ToArray())
        {
            if (ApplySpeedBonusToWeaponItem(item, reason)) reapplied++;
        }

        // 通知游戏装备数据已经变化，尽量让当前手持武器不需要手动切换也能刷新。
        RequestWeaponRefresh(hero, "apply");

        DebugLog($"Reapplied speed bonus to cached weapons: count={reapplied}, reason={reason}, bonus={_currentSpeedBonus:P0}");
    }

    // 请求刷新当前装备武器，并安排后续几帧继续补刷。
    private static void RequestWeaponRefresh(Hero hero, string reason)
    {
        // 立即刷一次，尽量让玩家不需要手动切武器。
        ForceRefreshEquippedWeapons(hero, reason);

        // 再安排后续几帧补刷，覆盖游戏内部延迟重建武器实例的情况。
        _pendingWeaponRefreshFrames = 5;
    }

    // 把某个玩家武器物品恢复到最初缓存的攻击段字段值。
    private static bool RestoreWeaponItem(InventItem item, string reason)
    {
        try
        {
            // 武器连段数据仍然存在于 InventItem.getWeaponData().strikeChain。
            var weaponData = item.getWeaponData();
            var strikeChain = weaponData?.strikeChain;
            if (strikeChain == null) return false;

            var restoredStrikes = 0;
            var restoredFields = 0;

            // 逐个攻击段恢复字段；没有缓存过原始值的字段不会被动。
            for (var i = 0; i < strikeChain.length; i++)
            {
                var strike = strikeChain.getDyn(i);
                var changed = RestoreStrikeFields(strike);
                if (changed > 0) restoredStrikes++;
                restoredFields += changed;
            }

            DebugLog(
                $"Weapon restore result: reason={reason}, count={strikeChain.length}, restoredStrikes={restoredStrikes}, restoredFields={restoredFields}");

            return restoredStrikes > 0;
        }
        catch (Exception ex)
        {
            // 恢复失败也不要影响游戏运行，只记录日志便于后续判断是哪类武器结构特殊。
            DebugLog($"Weapon restore exception: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // 恢复单个攻击段被本 Mod 改过的字段。
    private static int RestoreStrikeFields(object? strike)
    {
        if (strike is not IHashlinkFieldObject strikeFields) return 0;

        var objectKey = RuntimeHelpers.GetHashCode(strikeFields);
        if (!_originalStrikeFieldValues.TryGetValue(objectKey, out var fieldValues)) return 0;

        var restored = 0;
        foreach (var pair in fieldValues)
        {
            try
            {
                strikeFields.SetFieldValue(pair.Key, pair.Value);
                restored++;
            }
            catch
            {
                // 某些字段可能在特殊武器上只读或已失效，跳过即可。
            }
        }

        return restored;
    }

    // 尝试让游戏刷新当前装备武器。配置热加载后仅改 InventItem 数据不一定会影响已创建的 Weapon 实例。
    private static void ForceRefreshEquippedWeapons(Hero hero, string reason)
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

            DebugLog($"Requested hero.onEquipedItemsChange after speed config change: {reason}.");
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
            DebugLog($"Requested weaponsManager refresh after speed config change: {reason}.");
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
            var config = JsonSerializer.Deserialize<EnhancementDebugConfig>(json);
            if (config == null) return;

            // SpeedLevel 是唯一需要手动编辑的参数；范围固定在 0 到 5，数值对应“模拟几细胞”。
            var speedLevel = System.Math.Clamp(config.SpeedLevel, 0, 5);

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
        _debugBossCells = 0;
        _usingDebugConfig = false;
    }

    // 根据 0 到 5 档套用攻速参数；SpeedLevel 数值直接对应“模拟几细胞”。
    private static void ApplySpeedLevel(int speedLevel)
    {
        // 调试档位也不开启顶满测试；5 档代表 5 细胞手感，不再代表 0CD。
        _useWorkshopMaxSpeedTest = false;
        _workshopAnimSpeedFloor = DefaultWorkshopAnimSpeedFloor;

        // 调试模式和正式模式共用同一张档位表：
        // 0=0%，1=20%，2=30%，3=40%，4=40%，5=50%。
        _baseSpeedBonus = 0.00;
        _bonusPerBossCell = 0.00;
        _maxSpeedBonus = 0.50;
        _debugBossCells = speedLevel;

        // 0 细胞没有动画加成；其他档位动画速度吃一半加成，避免体感过快。
        _animationSpeedBonusScale = speedLevel <= 0 ? 0.00 : 0.50;
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
internal sealed class EnhancementDebugConfig
{
    // 攻速档位：0-5，对应模拟 0-5 Boss 细胞。
    public int SpeedLevel { get; set; } = 0;
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
