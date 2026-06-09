using dc.en;
using dc.tool;
using Hashlink.Proxy.Objects;
using HaxeProxy.Runtime;
using ModCore.Modules;
using System.Runtime.CompilerServices;

namespace DeadCellsEnhancement;

// “迅捷本能”功能：选择变异后，按 Boss 细胞数提高玩家武器攻击速度。
internal sealed class SpeedInstinctFeature : Core.IModFeature
{
    // CDB 里新增变异的 id。判断玩家是否选择本变异时就用这个 id 匹配。
    internal const string MutationId = "P_SpeedInstinct";

    // 默认最大攻速加成上限：0.50 表示最多把相关时间参数缩短 50%。
    private const double MaxSpeedBonus = 0.50;

    // 默认动画速度加成缩放。时间字段已经缩短一次，动画速度再完整叠加会显得过快，所以这里只吃一半加成。
    private const double AnimationSpeedBonusScale = 0.50;

    // 当前帧检测到玩家是否选择了迅捷变异。
    private bool _hasMutation;

    // 当前生效的攻速加成，只有选择迅捷变异时才大于 0。
    private double _currentSpeedBonus;

    // 已经经过 Weapon.create 的玩家武器物品。配置热加载或变异选择状态变化后，会尝试给这些武器重新套用参数。
    private readonly System.Collections.Generic.List<InventItem> _knownPlayerWeaponItems = [];

    // 每把武器物品、每个攻击段字段的原始值缓存。
    // 这里必须按 InventItem + strikeIndex 缓存，而不能按 strike 对象本身缓存：
    // 游戏刷新装备时可能会重建 strike 对象，新对象里的数值有机会已经是本 Mod 修改后的数值。
    // 如果这时把“已修改值”当成原始值，后续每次刷新都会继续乘倍率，1 细胞也会越打越像 5 细胞。
    private readonly System.Collections.Generic.Dictionary<int, WeaponOriginalFieldCache> _originalWeaponFieldValues = [];

    // 装备刷新剩余次数。武器实例有时不会在一次 onEquipedItemsChange 后立刻完全重建，所以连续几帧补刷。
    private int _pendingWeaponRefreshFrames;

    // 初始化功能，注册 Weapon.create hook。
    public void Initialize()
    {
        // 拦截 tool.$Weapon.create：每次游戏根据 InventItem 创建 Weapon 时，都会先进入 Hook_create。
        // 这样可以在武器对象刚创建出来时修改它的 strikeChain 攻击参数。
        HashlinkHooks.Instance.CreateHook("tool.$Weapon", "create", Hook_create).Enable();
        ModEntry.DebugLog("SpeedInstinctFeature initialized.");
    }

    // 每帧更新变异选择状态、攻速加成和已缓存武器。
    public void OnHeroUpdate(Hero hero)
    {
        // 记录上一帧是否选择了迅捷变异，用于判断状态是否发生变化。
        var hadSpeedMutation = _hasMutation;

        // 记录上一帧的加成数值，用于判断配置文件或细胞数导致的加成变化。
        var previousSpeedBonus = _currentSpeedBonus;

        // 重新检测当前英雄是否已经选择了 id 为 P_SpeedInstinct 的变异。
        _hasMutation = ModEntry.HasMutation(hero, MutationId, "Speed mutation");

        // 如果选择了变异，计算攻速加成；否则加成为 0。
        _currentSpeedBonus = _hasMutation ? GetSpeedBonus(hero) : 0;

        // 变异状态、加成数值或 debug 配置变化后，尝试把新参数重套到已经见过的玩家武器上。
        if (_hasMutation != hadSpeedMutation
            || System.Math.Abs(_currentSpeedBonus - previousSpeedBonus) > 0.0001)
        {
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
        if (_hasMutation != hadSpeedMutation)
        {
            ModEntry.DebugLog($"Speed mutation selected: {_hasMutation}, speed bonus: {_currentSpeedBonus:P0}");
        }
    }

    // 根据当前 Boss 细胞数量计算最终攻速加成。
    private double GetSpeedBonus(Hero hero)
    {
        // 调试模式下，SpeedLevel 直接等于“模拟几细胞”；正式模式下，读取游戏真实 Boss Cell 数。
        var bossCells = ModEntry.GetEffectiveBossCells(hero);

        // 正式模式下 0 细胞没有任何加成，直接返回 0，ModifyWeaponStats 会因此完全跳过。
        // 调试模式下 SpeedLevel = 0 也同样没有任何加成，方便和原版手感做 A/B 对比。
        if (bossCells <= 0) return 0;

        // 最终加成按明确档位表计算，避免线性公式和测试直觉不一致。
        return GetSpeedBonusForBossCells(bossCells);
    }

    // 把 Boss 细胞数或调试 SpeedLevel 映射成真正的攻速加成。
    private double GetSpeedBonusForBossCells(int bossCells)
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
        return System.Math.Min(speedBonus, MaxSpeedBonus);
    }

    // 修改刚创建出来的武器攻击参数。Hook_WeaponCreate 会调用这个函数。
    private void ModifyWeaponStats(Weapon weapon, Hero hero, InventItem item)
    {
        // 只缓存当前玩家英雄的武器物品，避免调参时误改敌人或其它来源的武器数据。
        if (ReferenceEquals(hero, Game.Instance.HeroInstance))
        {
            RememberPlayerWeaponItem(item);
        }

        // 没选择迅捷变异或加成为 0 时，不修改任何武器数据。
        if (!_hasMutation || _currentSpeedBonus <= 0) return;

        // 只修改当前玩家英雄的武器，避免影响敌人、分身或其他非玩家来源。
        if (!ReferenceEquals(hero, Game.Instance.HeroInstance)) return;

        // 真正修改武器物品的 CDB 武器数据。
        ApplySpeedBonusToWeaponItem(item, "Weapon.create");
    }

    // 记住玩家已经创建过的武器物品，便于配置变化后不切武器也能尝试重套参数。
    private void RememberPlayerWeaponItem(InventItem item)
    {
        // 同一个物品引用只保存一次。
        foreach (var knownItem in _knownPlayerWeaponItems)
        {
            if (ReferenceEquals(knownItem, item)) return;
        }

        // 控制缓存长度，避免长时间游戏后列表无限增长。
        if (_knownPlayerWeaponItems.Count >= 24)
        {
            var removedItem = _knownPlayerWeaponItems[0];
            _knownPlayerWeaponItems.RemoveAt(0);

            // 只清掉“已经不在背包里”的物品的原始值缓存。
            // 如果武器仍在背包里（只是被挤出本列表），它的连段数据可能已经是本 Mod 改小后的值；
            // 一旦清掉基准值，下次再装备时会把“已改小值”当成原始值，导致该武器重新开始叠乘。
            // 仍持有的武器对象也不会被 GC，因此保留它的缓存不会有哈希复用问题。
            if (!IsItemStillOwned(removedItem))
            {
                _originalWeaponFieldValues.Remove(GetWeaponItemCacheKey(removedItem));
            }
        }

        _knownPlayerWeaponItems.Add(item);
    }

    // 把当前攻速参数重新套到已缓存的玩家武器物品上。
    private void ReapplyKnownWeaponItems(Hero hero, string reason)
    {
        // 只允许玩家英雄触发重套，保持作用范围干净。
        if (!ReferenceEquals(hero, Game.Instance.HeroInstance)) return;

        // 没有迅捷变异或加成为 0 时，把之前改过的武器恢复到缓存的原始值。
        // 这样重置变异、切回 0 细胞或 debug 配置改成 0 时，不会留下旧攻速。
        if (!_hasMutation || _currentSpeedBonus <= 0)
        {
            var restored = 0;
            foreach (var item in _knownPlayerWeaponItems.ToArray())
            {
                if (RestoreWeaponItem(item, reason)) restored++;
            }

            RequestWeaponRefresh(hero, "restore");
            ModEntry.DebugLog($"Restored cached weapons: count={restored}, reason={reason}");
            return;
        }

        var reapplied = 0;
        foreach (var item in _knownPlayerWeaponItems.ToArray())
        {
            if (ApplySpeedBonusToWeaponItem(item, reason)) reapplied++;
        }

        // 通知游戏装备数据已经变化，尽量让当前手持武器不需要手动切换也能刷新。
        RequestWeaponRefresh(hero, "apply");

        ModEntry.DebugLog($"Reapplied speed bonus to cached weapons: count={reapplied}, reason={reason}, bonus={_currentSpeedBonus:P0}");
    }

    // 请求刷新当前装备武器，并安排后续几帧继续补刷。
    private void RequestWeaponRefresh(Hero hero, string reason)
    {
        // 立即刷一次，尽量让玩家不需要手动切武器。
        ForceRefreshEquippedWeapons(hero, reason);

        // 再安排后续几帧补刷，覆盖游戏内部延迟重建武器实例的情况。
        _pendingWeaponRefreshFrames = 5;
    }

    // 把某个玩家武器物品恢复到最初缓存的攻击段字段值。
    private bool RestoreWeaponItem(InventItem item, string reason)
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
                var changed = RestoreStrikeFields(item, i, strike);
                if (changed > 0) restoredStrikes++;
                restoredFields += changed;
            }

            ModEntry.DebugLog(
                $"Weapon restore result: reason={reason}, count={strikeChain.length}, restoredStrikes={restoredStrikes}, restoredFields={restoredFields}");

            return restoredStrikes > 0;
        }
        catch (Exception ex)
        {
            // 恢复失败也不要影响游戏运行，只记录日志便于后续判断是哪类武器结构特殊。
            ModEntry.DebugLog($"Weapon restore exception: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // 恢复单个攻击段被本 Mod 改过的字段。
    private int RestoreStrikeFields(InventItem item, int strikeIndex, object? strike)
    {
        if (strike is not IHashlinkFieldObject strikeFields) return 0;

        var weaponKey = GetWeaponItemCacheKey(item);
        if (!_originalWeaponFieldValues.TryGetValue(weaponKey, out var weaponCache)) return 0;
        if (!weaponCache.Strikes.TryGetValue(strikeIndex, out var fieldValues)) return 0;

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
    private void ForceRefreshEquippedWeapons(Hero hero, string reason)
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

            ModEntry.DebugLog($"Requested hero.onEquipedItemsChange after speed config change: {reason}.");
        }
        catch (Exception ex)
        {
            ModEntry.DebugLog($"hero.onEquipedItemsChange refresh failed: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            // 兜底：直接通知武器管理器装备更新，并尝试刷新两个主武器槽。
            var duringHeroInit = false;
            hero.weaponsManager.onEquippedItemsUpdated(new Ref<bool>(ref duringHeroInit));
            hero.weaponsManager.updateWeapon(0);
            hero.weaponsManager.updateWeapon(1);
            ModEntry.DebugLog($"Requested weaponsManager refresh after speed config change: {reason}.");
        }
        catch (Exception ex)
        {
            ModEntry.DebugLog($"weaponsManager refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // 修改 InventItem.getWeaponData().strikeChain。返回 true 表示至少成功处理过一次武器数据。
    private bool ApplySpeedBonusToWeaponItem(InventItem item, string reason)
    {
        try
        {
            // 时间缩放系数。例如 25% 加成时 multiplier = 0.75，表示冷却/硬直缩短到 75%。
            var multiplier = 1.0 - _currentSpeedBonus;

            // 运行时 Weapon 对象没有 strikeChain 字段；真正的攻击段数据在 InventItem.getWeaponData() 返回的 CDB 武器数据里。
            var weaponData = item.getWeaponData();

            // strikeChain 是武器连段数据数组，每一段攻击都有自己的冷却、前摇、后摇、动画速度等。
            var strikeChain = weaponData?.strikeChain;

            if (strikeChain == null)
            {
                ModEntry.DebugLog("Weapon modify skipped: item.getWeaponData().strikeChain is null");
                return false;
            }

            var modifiedStrikes = 0;
            var changedFields = 0;

            // ArrayObj 是 Haxe Array<Dynamic> 的代理类型，length/getDyn 可以稳定访问每个攻击段。
            for (var i = 0; i < strikeChain.length; i++)
            {
                var strike = strikeChain.getDyn(i);
                var changed = ApplySpeedBonusToStrike(item, i, strike, multiplier);
                if (changed > 0) modifiedStrikes++;
                changedFields += changed;
            }

            ModEntry.DebugLog(
                $"Weapon modify result: ArrayObj count={strikeChain.length}, modifiedStrikes={modifiedStrikes}, changedFields={changedFields}");

            return modifiedStrikes > 0;
        }
        catch (System.Exception ex)
        {
            // 修改武器参数失败时记录异常，但不继续抛出，避免因为某把武器结构特殊导致游戏崩溃。
            ModEntry.DebugLog($"Weapon modify exception: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // 对单个攻击段 strike 应用攻速加成。
    private int ApplySpeedBonusToStrike(InventItem item, int strikeIndex, object? strike, double multiplier)
    {
        // strike 必须能作为 Hashlink 字段对象读取/写入，否则无法修改它的参数。
        if (strike is not IHashlinkFieldObject strikeFields)
        {
            return 0;
        }

        // 收集本攻击段实际写入成功的字段名，返回数量用于上层统计。
        var changedFields = 0;

        // 攻击段的各时间字段统一按 multiplier 缩短：冷却、蓄力、控制锁定、命中帧、前摇、后摇。
        if (ScaleDoubleField(item, strikeIndex, strikeFields, "coolDown", multiplier)) changedFields++;
        if (ScaleDoubleField(item, strikeIndex, strikeFields, "charge", multiplier)) changedFields++;
        if (ScaleDoubleField(item, strikeIndex, strikeFields, "lockCtrlAfter", multiplier)) changedFields++;
        if (ScaleDoubleField(item, strikeIndex, strikeFields, "hitFrame", multiplier)) changedFields++;
        if (ScaleDoubleField(item, strikeIndex, strikeFields, "startUp", multiplier)) changedFields++;
        if (ScaleDoubleField(item, strikeIndex, strikeFields, "recovery", multiplier)) changedFields++;

        // 动画速度只吃一半加成：时间字段已经缩短一次，动画再完整叠加会显得过快。
        if (ScaleDoubleField(item, strikeIndex, strikeFields, "animSpd", 1.0 + _currentSpeedBonus * AnimationSpeedBonusScale)) changedFields++;

        return changedFields;
    }

    // 按 multiplier 缩放某个字段，兼容 double/float/int 三种常见数值类型。
    private bool ScaleDoubleField(InventItem item, int strikeIndex, IHashlinkFieldObject fields, string name, double multiplier)
    {
        try
        {
            var value = GetOriginalFieldValue(item, strikeIndex, fields, name);
            switch (value)
            {
                case double d:
                    fields.SetFieldValue(name, d * multiplier);
                    return true;
                case float f:
                    fields.SetFieldValue(name, f * (float)multiplier);
                    return true;
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
    private object? GetOriginalFieldValue(InventItem item, int strikeIndex, IHashlinkFieldObject fields, string name)
    {
        var weaponKey = GetWeaponItemCacheKey(item);

        if (!_originalWeaponFieldValues.TryGetValue(weaponKey, out var weaponCache))
        {
            weaponCache = new WeaponOriginalFieldCache();
            _originalWeaponFieldValues[weaponKey] = weaponCache;
        }

        if (!weaponCache.Strikes.TryGetValue(strikeIndex, out var fieldValues))
        {
            fieldValues = [];
            weaponCache.Strikes[strikeIndex] = fieldValues;
        }

        if (fieldValues.TryGetValue(name, out var originalValue))
        {
            return originalValue;
        }

        originalValue = fields.GetFieldValue(name);
        fieldValues[name] = originalValue;
        return originalValue;
    }

    // InventItem 没有稳定公开 id 时，用运行时对象身份作为本局游戏内的缓存 key。
    // 同一个背包物品在装备刷新时仍然是同一个 InventItem，因此比 transient strike 对象稳定。
    private static int GetWeaponItemCacheKey(InventItem item)
    {
        return RuntimeHelpers.GetHashCode(item);
    }

    // 判断某个武器物品是否仍在当前玩家背包里（按对象身份比对，而非按 id）。
    // 用于决定驱逐缓存列表时是否要连原始值缓存一起清掉。
    private static bool IsItemStillOwned(InventItem item)
    {
        try
        {
            var inventory = Game.Instance.HeroInstance?.inventory;
            var items = inventory?.items;
            if (items == null) return false;

            for (var i = 0; i < items.length; i++)
            {
                if (ReferenceEquals(items.getDyn(i), item)) return true;
            }
        }
        catch
        {
            // 读取背包失败时保守认为“仍持有”，宁可少清一次缓存也不要错误地清掉基准值。
            return true;
        }

        return false;
    }

    // 单把武器的原始攻击段字段表。
    // 外层 key 是连段序号，内层 key 是字段名，比如 coolDown/recovery/animSpd。
    private sealed class WeaponOriginalFieldCache
    {
        internal readonly System.Collections.Generic.Dictionary<int, System.Collections.Generic.Dictionary<string, object?>> Strikes = [];
    }

    // 原始 tool.$Weapon.create 的函数签名：传入英雄和背包物品，返回创建出的武器对象。
    private delegate Weapon orig_create(Hero hero, InventItem item);

    // Weapon.create 的 hook 方法。ModCore 会把原函数 orig 和原始参数传进来。
    private Weapon Hook_create(orig_create orig, Hero hero, InventItem item)
    {
        var weapon = orig(hero, item);
        ModifyWeaponStats(weapon, hero, item);
        return weapon;
    }
}
