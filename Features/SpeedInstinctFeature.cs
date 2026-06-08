using dc.en;
using dc.tool;
using Hashlink.Proxy.Objects;
using HaxeProxy.Runtime;
using ModCore.Modules;
using System.Runtime.CompilerServices;

namespace DeadCellsEnhancement;

// “迅捷本能”功能：选择变异后，按 Boss 细胞数提高玩家武器攻击速度。
internal static class SpeedInstinctFeature
{
    // CDB 里新增变异的 id。判断玩家是否选择本变异时就用这个 id 匹配。
    internal const string MutationId = "P_SpeedInstinct";

    // 默认最大攻速加成上限：0.50 表示最多把相关时间参数缩短 50%。
    private const double MaxSpeedBonus = 0.50;

    // 默认动画速度加成缩放。时间字段已经缩短一次，动画速度再完整叠加会显得过快，所以这里只吃一半加成。
    private const double AnimationSpeedBonusScale = 0.50;

    // 临时测试开关：模拟 Workshop「Rapid Attack & 0CD」的顶满效果。
    // true 时不按百分比缩放，而是把武器攻击段的冷却、前摇、后摇和命中帧直接清零。
    private static readonly bool UseWorkshopMaxSpeedTest = false;

    // 默认顶满测试用动画速度下限。Workshop 里常见 animSpd 为 1.5 或 2，这里用 2 作为明显手感测试。
    private const double WorkshopAnimSpeedFloor = 2.0;

    // 当前帧检测到玩家是否选择了迅捷变异。
    private static bool _hasMutation;

    // 当前生效的攻速加成，只有选择迅捷变异时才大于 0。
    private static double _currentSpeedBonus;

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

    // 装备刷新剩余次数。武器实例有时不会在一次 onEquipedItemsChange 后立刻完全重建，所以连续几帧补刷。
    private static int _pendingWeaponRefreshFrames;

    // 初始化功能，注册 Weapon.create hook。
    internal static void Initialize(HashlinkHooks hooks)
    {
        // 拦截 tool.$Weapon.create：每次游戏根据 InventItem 创建 Weapon 时，都会先进入 Hook_create。
        // 这样可以在武器对象刚创建出来时修改它的 strikeChain 攻击参数。
        hooks.CreateHook("tool.$Weapon", "create", Hook_WeaponCreate.Hook_create).Enable();
        ModEntry.DebugLog("SpeedInstinctFeature initialized.");
    }

    // 每帧更新变异选择状态、攻速加成和已缓存武器。
    internal static void OnHeroUpdate(Hero hero)
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
    private static double GetSpeedBonus(Hero hero)
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
        return System.Math.Min(speedBonus, MaxSpeedBonus);
    }

    // 修改刚创建出来的武器攻击参数。Hook_WeaponCreate 会调用这个函数。
    private static void ModifyWeaponStats(Weapon weapon, Hero hero, InventItem item)
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
            ModEntry.DebugLog(
                $"Weapon.create hook #{_weaponHookLogCount}: mutation={_hasMutation}, bonus={_currentSpeedBonus:P0}, weaponType={weapon.GetType().FullName}, heroIsPlayer={ReferenceEquals(hero, Game.Instance.HeroInstance)}");
        }

        // 没选择迅捷变异或加成为 0 时，不修改任何武器数据。
        if (!_hasMutation || _currentSpeedBonus <= 0) return;

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
                ModEntry.DebugLog(
                    $"Weapon modify #{_weaponModifyLogCount}: reason={reason}, weaponDataType={weaponData?.GetType().FullName ?? "null"}, strikeChainType={strikeChain?.GetType().FullName ?? "null"}, multiplier={multiplier:0.###}, workshopMax={UseWorkshopMaxSpeedTest}");
            }

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
                var changed = ApplySpeedBonusToStrike(strike, multiplier);
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
    private static int ApplySpeedBonusToStrike(object? strike, double multiplier)
    {
        // strike 必须能作为 Hashlink 字段对象读取/写入，否则无法修改它的参数。
        if (strike is not IHashlinkFieldObject strikeFields)
        {
            if (_strikeModifyLogCount < 20)
            {
                _strikeModifyLogCount++;
                ModEntry.DebugLog($"Strike modify skipped: strikeType={strike?.GetType().FullName ?? "null"} is not IHashlinkFieldObject");
            }

            return 0;
        }

        // 收集本攻击段实际写入成功的字段名，写到日志里用于确认字段是否存在且可写。
        var changedFields = new System.Collections.Generic.List<string>();

        // 临时顶满模式：参考 Workshop「Rapid Attack & 0CD」的做法，直接清空关键时间字段。
        if (UseWorkshopMaxSpeedTest)
        {
            if (SetNumberField(strikeFields, "coolDown", 0)) changedFields.Add("coolDown");
            if (SetNumberField(strikeFields, "charge", 0)) changedFields.Add("charge");
            if (SetNumberField(strikeFields, "dynamicCharge", 0)) changedFields.Add("dynamicCharge");
            if (SetNumberField(strikeFields, "lockCtrlAfter", 0)) changedFields.Add("lockCtrlAfter");
            if (SetNumberField(strikeFields, "hitFrame", 0)) changedFields.Add("hitFrame");
            if (SetNumberField(strikeFields, "startUp", 0)) changedFields.Add("startUp");
            if (SetNumberField(strikeFields, "recovery", 0)) changedFields.Add("recovery");
            if (EnsureNumberFieldAtLeast(strikeFields, "animSpd", WorkshopAnimSpeedFloor)) changedFields.Add("animSpd");

            if (_strikeModifyLogCount < 20)
            {
                _strikeModifyLogCount++;
                ModEntry.DebugLog($"Strike max modify #{_strikeModifyLogCount}: changed={changedFields.Count}, fields={string.Join(",", changedFields)}");
            }

            return changedFields.Count;
        }

        if (ScaleDoubleField(strikeFields, "coolDown", multiplier)) changedFields.Add("coolDown");
        if (ScaleDoubleField(strikeFields, "charge", multiplier)) changedFields.Add("charge");
        if (ScaleDoubleField(strikeFields, "lockCtrlAfter", multiplier)) changedFields.Add("lockCtrlAfter");
        if (ScaleDoubleField(strikeFields, "hitFrame", multiplier)) changedFields.Add("hitFrame");
        if (ScaleDoubleField(strikeFields, "startUp", multiplier)) changedFields.Add("startUp");
        if (ScaleDoubleField(strikeFields, "recovery", multiplier)) changedFields.Add("recovery");
        if (ScaleDoubleField(strikeFields, "animSpd", 1.0 + _currentSpeedBonus * AnimationSpeedBonusScale)) changedFields.Add("animSpd");

        if (_strikeModifyLogCount < 20)
        {
            _strikeModifyLogCount++;
            ModEntry.DebugLog($"Strike scale modify #{_strikeModifyLogCount}: changed={changedFields.Count}, fields={string.Join(",", changedFields)}");
        }

        return changedFields.Count;
    }

    // 如果字段存在，就按它原来的数值类型写入指定数值。
    private static bool SetNumberField(IHashlinkFieldObject fields, string name, double number)
    {
        try
        {
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
            var value = GetOriginalFieldValue(fields, name);
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
    private static object? GetOriginalFieldValue(IHashlinkFieldObject fields, string name)
    {
        var objectKey = RuntimeHelpers.GetHashCode(fields);

        if (!_originalStrikeFieldValues.TryGetValue(objectKey, out var fieldValues))
        {
            fieldValues = [];
            _originalStrikeFieldValues[objectKey] = fieldValues;
        }

        if (fieldValues.TryGetValue(name, out var originalValue))
        {
            return originalValue;
        }

        originalValue = fields.GetFieldValue(name);
        fieldValues[name] = originalValue;
        return originalValue;
    }

    // 这个静态类专门保存 Weapon.create 的 hook 委托和 hook 方法。
    private static class Hook_WeaponCreate
    {
        // 原始 tool.$Weapon.create 的函数签名：传入英雄和背包物品，返回创建出的武器对象。
        public delegate Weapon orig_create(Hero hero, InventItem item);

        // Hook 方法。ModCore 会把原函数 orig 和原始参数传进来。
        public static Weapon Hook_create(orig_create orig, Hero hero, InventItem item)
        {
            var weapon = orig(hero, item);
            ModifyWeaponStats(weapon, hero, item);
            return weapon;
        }
    }
}
