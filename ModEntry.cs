// Dead Cells 原始游戏代理命名空间：提供 Game、Hero、Weapon、InventItem 等游戏对象的 C# 代理。
using dc;
using dc.en;
using DcPrGame = dc.pr.Game;
using Hook_PrGame = dc.pr.Hook_Game;

// Hashlink 代理对象接口：用于读取/写入游戏对象里没有直接暴露成 C# 属性的动态字段。
using Hashlink.Proxy.Objects;

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
    // 所有功能模块的统一列表。初始化和每帧更新都按这个顺序遍历，新增功能只需在这里追加一项。
    // 注意：LegendaryForgeFeature 暂时停用。它注入小铸造所 UI 时会让游戏在打开重铸 NPC 的瞬间
    // 崩溃（moddbg.log 里 "Legendary forge add choice failed: NullReferenceException"，
    // 随后游戏主循环抛 "Null access .onResize"）。已加入 null 行容器保护，但 onResize 崩溃路径
    // 需要在能实时调试的环境里验证后再重新启用，先从激活列表移除以保证游戏可玩。
    private static readonly Core.IModFeature[] _features =
    [
        new SpeedInstinctFeature(),
        new HealthGrowthFeature(),
        new PerkLimitFeature(),
        new LegendaryForgeFeature(),
    ];

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

    // 记录每个功能上一次变异检测使用的来源，避免多个变异交替检测时每帧刷屏。
    private static readonly System.Collections.Generic.Dictionary<string, string> _lastDetectionSourceByScope = [];

    // 记录上一次写入日志的 Boss 细胞读取结果，避免每帧重复刷同样的细胞数。
    private static int? _lastLoggedBossCells;

    // 记录上一次 Boss 细胞读取来源。数值相同但来源变化时也写日志，方便排查正式模式读错字段。
    private static string? _lastLoggedBossCellSource;

    // 当前正在运行的原游戏 dc.pr.Game 实例。Hero 身上不稳定暴露 user，所以从 Game.init 缓存更可靠。
    private static DcPrGame? _currentPrGame;

    // 当前 Boss 细胞数是从哪个字段读到的。LogBossCellsIfChanged 会把它写进日志。
    private static string _currentBossCellSource = "not read yet";

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

        // 记录原游戏 pr.Game 实例。正式模式的 Boss Cell 数在 game.user.bossRuneActivated 上最稳定。
        Hook_PrGame.init += Hook_PrGame_init;
        Hook_PrGame.onDispose += Hook_PrGame_onDispose;

        // 统一初始化所有功能模块。新增功能时只需放进 Features 目录并加进 _features 列表，不再改动这里。
        foreach (var feature in _features)
        {
            feature.Initialize();
        }
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

        // 统一更新所有功能模块的每帧状态。只关心初始化的功能用接口默认空实现，不会有额外开销。
        foreach (var feature in _features)
        {
            feature.OnHeroUpdate(hero);
        }
    }

    // 判断当前英雄是否已经选择了指定变异。
    internal static bool HasMutation(Hero hero, string mutationId, string logScope)
    {
        // 变异在游戏内部也是 InventItem，类型是 Perk；选中后通常会被加入英雄 inventory。
        // 因此这里优先走 inventory.hasItem(id)，和原版通过物品 id 查询背包内容的方式一致。
        try
        {
            if (hero.inventory != null && hero.inventory.hasItem(mutationId.AsHaxeString()))
            {
                LogDetectionSource($"{logScope}: inventory.hasItem({mutationId})");
                return true;
            }
        }
        catch (Exception ex)
        {
            // 如果当前版本的 hasItem 不支持 Perk，也不要影响游戏；日志会提示我们继续补字段探测。
            LogDetectionSource($"{logScope}: inventory.hasItem({mutationId}) failed: {ex.GetType().Name}");
        }

        // 公开 API 没命中时，尝试从 hero/inventory 的动态字段里查找变异 id。
        // 这段是兜底兼容：不同游戏版本可能把已选变异放在 perks、_perks 或 inventory 内部数组里。
        return HasMutationInDynamicFields(hero, mutationId, logScope);
    }

    // 从动态字段兜底查找指定变异 id。
    private static bool HasMutationInDynamicFields(Hero hero, string mutationId, string logScope)
    {
        try
        {
            // 先检查 hero 自身，再检查 inventory 对象；任意一边找到 P_SpeedInstinct 都认为变异已选。
            return ObjectGraphContainsItemId(hero.HashlinkObj, mutationId, $"{logScope}: hero")
                || ObjectGraphContainsItemId(hero.inventory?.HashlinkObj, mutationId, $"{logScope}: inventory");
        }
        catch (Exception ex)
        {
            // 动态字段读取失败时保守返回 false，并记录一次轻量线索。
            LogDetectionSource($"{logScope}: dynamic perk scan failed: {ex.GetType().Name}");
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
        var separatorIndex = source.IndexOf(": ", StringComparison.Ordinal);
        var scope = separatorIndex > 0 ? source[..separatorIndex] : "default";

        if (_lastDetectionSourceByScope.TryGetValue(scope, out var lastSource) && source == lastSource) return;
        _lastDetectionSourceByScope[scope] = source;
        DebugLog($"Mutation detection: {source}");
    }

    // 读取当前所有功能都应该使用的有效 Boss 细胞数。
    // debug_speed_config.json 存在时优先使用 SpeedLevel；不存在时才读游戏真实难度。
    internal static int GetEffectiveBossCells(Hero hero)
    {
        var bossCells = _usingDebugConfig
            ? System.Math.Clamp(_debugBossCells, 0, 5)
            : System.Math.Max(0, GetCurrentBossCells(hero));

        LogBossCellsIfChanged(bossCells);
        return bossCells;
    }

    // 从玩家 user 数据中读取当前激活的 Boss 细胞数量。
    internal static int GetCurrentBossCells(Hero hero)
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

            // SpeedLevel 现在是全局“模拟 Boss 细胞数”，所有功能都从 GetEffectiveBossCells 读取它。
            ApplyDebugSpeedLevel(speedLevel);
            _usingDebugConfig = true;

            // 记录成功读取的修改时间，避免下一秒重复读同一个文件。
            _configLastWriteTimeUtc = lastWriteTimeUtc;

            // 写一行摘要，方便你看日志确认游戏已经吃到新参数。
            DebugLog($"Config loaded: SpeedLevel={speedLevel}, effectiveBossCells={_debugBossCells}");
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
        _debugBossCells = 0;
        _usingDebugConfig = false;
    }

    // 根据 0 到 5 档套用调试细胞数；SpeedLevel 数值直接对应“模拟几细胞”。
    private static void ApplyDebugSpeedLevel(int speedLevel)
    {
        _debugBossCells = speedLevel;
    }

    // 写入 Mod 自己的调试日志文件。
    internal static void DebugLog(string message)
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

