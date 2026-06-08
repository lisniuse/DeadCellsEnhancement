using dc.en;
using HaxeProxy.Runtime;
using ModCore.Modules;
using System.Runtime.CompilerServices;

namespace DeadCellsEnhancement;

// “细胞活力”功能：选择变异后，每击杀一个敌人就按当前 Boss 细胞数提高最大生命值。
internal static class HealthGrowthFeature
{
    // CDB 里新增变异的 id。这个 id 会写入存档，所以确定后不要随意改名。
    internal const string MutationId = "P_CellVitality";

    // 每 1 个 Boss 细胞对应的最大生命值增量。1 细胞每击杀 +100，5 细胞每击杀 +500。
    private const int LifePerBossCellPerKill = 100;

    // 记录当前帧是否选择了本变异，用于日志和击杀时快速判断。
    private static bool _isSelected;

    // 本轮累计增加的最大生命值。装备/护符刷新后会用它重新套回生命上限。
    private static int _accumulatedMaxLifeBonus;

    // 当前已经实际套到 hero.maxLife 上的那部分加成。重新计算基础生命时要先扣掉它，避免重复叠加。
    private static int _appliedMaxLifeBonus;

    // 最近一次由本功能写出的 maxLife。用来判断游戏是否因为换护符/装备刷新了生命上限。
    private static int? _lastAppliedMaxLife;

    // 换装备/护符前临时保存的当前生命值。
    // 游戏的装备替换流程有时不会走 onEquipedItemsChange，或者会先把 life 压低再进入我们的每帧修复。
    // 所以在 pickItem 入口提前保存，随后几帧发现生命被装备刷新改低时再恢复回来。
    private static int? _pendingEquipmentLife;

    // 上面那份临时生命快照还剩多少帧有效。只保留很短窗口，避免玩家正常受伤后被误治疗。
    private static int _pendingEquipmentLifeFrames;

    // 最近一次看到的玩家生命值。用来在装备刷新窗口中判断是否发生了非伤害来源的降血。
    private static int? _lastSeenLife;

    // 最近一次看到的玩家最大生命值。辅助日志定位 updateMaxLife/overrideMaxLife 的真实影响。
    private static int? _lastSeenMaxLife;

    // 当前是否正在由本功能主动写入生命/最大生命。
    // 这个标记可以避免我们 Hook 到自己的 overrideMaxLife/setLifeAndRally 调用后重复处理。
    private static bool _isWritingLife;

    // 已经处理过的敌人对象身份。防止同一个死亡事件被多个游戏流程重复通知时反复加生命。
    private static readonly System.Collections.Generic.Queue<int> _recentMobKeys = [];

    // 最近处理过的敌人集合。配合队列做上限控制，避免长时间游戏后无限增长。
    private static readonly System.Collections.Generic.HashSet<int> _recentMobKeySet = [];

    // 初始化功能，注册英雄击杀敌人的回调。
    internal static void Initialize()
    {
        Hook_Hero.onMobDeath += Hook_Hero_onMobDeath;
        Hook_Hero.onEquipedItemsChange += Hook_Hero_onEquipedItemsChange;
        Hook_Hero.pickItem += Hook_Hero_pickItem;
        Hook_Hero.onAfterPickItem += Hook_Hero_onAfterPickItem;
        Hook_Hero.setLifeAndRally += Hook_Hero_setLifeAndRally;
        Hook_Hero.updateMaxLife += Hook_Hero_updateMaxLife;
        Hook_Hero.overrideMaxLife += Hook_Hero_overrideMaxLife;
        ModEntry.DebugLog("HealthGrowthFeature initialized.");
    }

    // 每帧更新变异选择状态。只在状态变化时写日志，避免刷屏。
    internal static void OnHeroUpdate(Hero hero)
    {
        var wasSelected = _isSelected;
        _isSelected = ModEntry.HasMutation(hero, MutationId, "Health growth mutation");

        if (_isSelected != wasSelected)
        {
            ModEntry.DebugLog($"Health growth mutation selected: {_isSelected}");
        }

        // 装备护符或换装时，游戏可能重新计算 maxLife，把本功能的加成冲掉。
        // 每帧轻量检查一次，如果发现 maxLife 和上次写出的值不一致，就按当前基础生命重新套回累计加成。
        if (_isSelected && _accumulatedMaxLifeBonus > 0 && _lastAppliedMaxLife != hero.maxLife)
        {
            ReapplyAccumulatedBonus(hero, "hero update");
        }

        // 换护符/装备后，游戏可能在 pickItem 返回后的后续流程里继续改 life。
        // 如果我们还持有换装前的生命快照，就在短窗口内把 life 拉回快照值。
        RestorePendingEquipmentLife(hero, "hero update");

        _lastSeenLife = hero.life;
        _lastSeenMaxLife = hero.maxLife;
    }

    // 原游戏 Hero.onMobDeath 的 Hook。先让原版逻辑完整执行，再追加最大生命值成长。
    private static void Hook_Hero_onMobDeath(Hook_Hero.orig_onMobDeath orig, Hero self, Mob mob)
    {
        orig(self, mob);
        ApplyLifeGrowthOnKill(self, mob);
    }

    // 原游戏 Hero.pickItem 的 Hook。这里是玩家捡起或替换装备的更早入口。
    // 在原版替换逻辑运行之前保存当前生命，防止装备刷新流程稍后把 life 改低。
    private static void Hook_Hero_pickItem(
        Hook_Hero.orig_pickItem orig,
        Hero self,
        dc.Entity from,
        dc.tool.InventItem i,
        HlAction<bool> onComplete)
    {
        BeginEquipmentLifePreservation(self, "pick item");
        orig(self, from, i, onComplete);
        CaptureEquipmentLifeDrop(self, "pick item return");
        RestorePendingEquipmentLife(self, "pick item return");
    }

    // 原游戏 Hero.onAfterPickItem 的 Hook。这个入口在捡起装备后的收尾阶段触发。
    // 再补一次恢复，可以覆盖 pickItem 内部异步回调或延迟 HUD/装备刷新造成的生命变化。
    private static void Hook_Hero_onAfterPickItem(
        Hook_Hero.orig_onAfterPickItem orig,
        Hero self,
        dc.tool.InventItem i)
    {
        BeginEquipmentLifePreservation(self, "after pick item");
        orig(self, i);
        CaptureEquipmentLifeDrop(self, "after pick item return");
        RestorePendingEquipmentLife(self, "after pick item return");
    }

    // 原游戏 Hero.setLifeAndRally 的 Hook。这个是常见的“设置当前生命值”入口。
    // 装备刷新期间如果它试图把生命调低，我们直接把参数改回换装前快照。
    private static void Hook_Hero_setLifeAndRally(
        Hook_Hero.orig_setLifeAndRally orig,
        Hero self,
        int life,
        int rally)
    {
        if (!_isWritingLife && ShouldProtectEquipmentLife(self) && _pendingEquipmentLife.HasValue)
        {
            var protectedLife = System.Math.Min(self.maxLife, _pendingEquipmentLife.Value);
            if (life < protectedLife)
            {
                ModEntry.DebugLog(
                    $"Health growth intercepted setLifeAndRally during equipment refresh: requestedLife={life}, protectedLife={protectedLife}, currentLife={self.life}, maxLife={self.maxLife}, rally={rally}");
                life = protectedLife;
            }
        }

        orig(self, life, rally);
    }

    // 原游戏 Hero.updateMaxLife 的 Hook。护符/装备属性变化通常会触发它重算最大生命。
    // 原版逻辑结束后立刻重新套本功能的最大生命加成，并恢复装备替换前的当前生命。
    private static void Hook_Hero_updateMaxLife(Hook_Hero.orig_updateMaxLife orig, Hero self)
    {
        var oldLife = self.life;
        var oldMaxLife = self.maxLife;

        orig(self);

        if (_isWritingLife || !ShouldProtectEquipmentLife(self)) return;

        CaptureEquipmentLifeDrop(self, "updateMaxLife");

        if (_accumulatedMaxLifeBonus > 0 && _lastAppliedMaxLife != self.maxLife)
        {
            ReapplyAccumulatedBonus(self, "updateMaxLife");
        }

        RestorePendingEquipmentLife(self, "updateMaxLife");

        if (oldLife != self.life || oldMaxLife != self.maxLife)
        {
            ModEntry.DebugLog(
                $"Health growth observed updateMaxLife: life {oldLife}->{self.life}, maxLife {oldMaxLife}->{self.maxLife}, pendingLife={(_pendingEquipmentLife?.ToString() ?? "none")}");
        }
    }

    // 原游戏 Hero.overrideMaxLife 的 Hook。这个入口会直接覆盖最大生命值。
    // 我们主要用它做定位日志，并在装备窗口中保证当前生命不会被后续流程压低。
    private static void Hook_Hero_overrideMaxLife(
        Hook_Hero.orig_overrideMaxLife orig,
        Hero self,
        int newMaxLife)
    {
        var oldLife = self.life;
        var oldMaxLife = self.maxLife;

        orig(self, newMaxLife);

        if (_isWritingLife || !ShouldProtectEquipmentLife(self)) return;

        CaptureEquipmentLifeDrop(self, "overrideMaxLife");
        RestorePendingEquipmentLife(self, "overrideMaxLife");

        if (oldLife != self.life || oldMaxLife != self.maxLife)
        {
            ModEntry.DebugLog(
                $"Health growth observed overrideMaxLife: requestedMaxLife={newMaxLife}, life {oldLife}->{self.life}, maxLife {oldMaxLife}->{self.maxLife}, pendingLife={(_pendingEquipmentLife?.ToString() ?? "none")}");
        }
    }

    // 原游戏在更换护符/装备后会重算 maxLife 和 life。
    // 这里在原版逻辑前保存当前生命，原版逻辑后再恢复，避免“最大生命没变但当前生命被换装刷新改掉”。
    private static void Hook_Hero_onEquipedItemsChange(
        Hook_Hero.orig_onEquipedItemsChange orig,
        Hero self,
        Ref<bool> updateHUD,
        Ref<bool> duringHeroInit,
        Ref<bool> duringItemTransform)
    {
        var shouldPreserveLife = ShouldProtectEquipmentLife(self);

        var lifeBeforeRefresh = shouldPreserveLife ? self.life : 0;

        orig(self, updateHUD, duringHeroInit, duringItemTransform);

        if (!shouldPreserveLife) return;

        try
        {
            var result = ReapplyAccumulatedBonus(
                self,
                "equipped items change",
                forcedLife: lifeBeforeRefresh);

            ModEntry.DebugLog(
                $"Health growth preserved life after equipment refresh: life {result.OldLife}->{self.life}, maxLife {result.OldMaxLife}->{self.maxLife}");
        }
        catch (Exception ex)
        {
            ModEntry.DebugLog($"Health growth equipment refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // 开始一次装备刷新生命保护。
    // 只有当前玩家、已选择“细胞活力”、并且已经有累计生命加成时才需要保护。
    private static void BeginEquipmentLifePreservation(Hero hero, string reason)
    {
        if (!ShouldProtectEquipmentLife(hero)) return;

        // 如果短时间内连续触发多个装备 Hook，保留较高的生命值作为恢复目标。
        // 这样 pickItem 先保存 201，后续 onAfterPickItem 看到 54 时不会覆盖掉正确快照。
        _pendingEquipmentLife = _pendingEquipmentLife.HasValue
            ? System.Math.Max(_pendingEquipmentLife.Value, hero.life)
            : hero.life;

        _pendingEquipmentLifeFrames = 12;
        ModEntry.DebugLog(
            $"Health growth captured life before equipment refresh: reason={reason}, life={hero.life}, maxLife={hero.maxLife}, pendingLife={_pendingEquipmentLife.Value}");
    }

    // 判断当前英雄是否需要启用装备刷新生命保护。
    private static bool ShouldProtectEquipmentLife(Hero hero)
    {
        return ReferenceEquals(hero, Game.Instance.HeroInstance)
            && _isSelected;
    }

    // 在装备刷新窗口中，如果发现当前生命已经低于上一帧/换装前生命，就把更高值作为恢复目标。
    // 这可以覆盖游戏直接写 hero.life 字段、不走 setLifeAndRally 的情况。
    private static void CaptureEquipmentLifeDrop(Hero hero, string reason)
    {
        if (!ShouldProtectEquipmentLife(hero)) return;

        var baseline = _pendingEquipmentLife;
        if (!baseline.HasValue && _lastSeenLife.HasValue && hero.life < _lastSeenLife.Value)
        {
            baseline = _lastSeenLife.Value;
        }

        if (!baseline.HasValue) return;
        if (hero.life >= baseline.Value) return;

        _pendingEquipmentLife = System.Math.Max(_pendingEquipmentLife ?? 0, baseline.Value);
        _pendingEquipmentLifeFrames = System.Math.Max(_pendingEquipmentLifeFrames, 12);

        ModEntry.DebugLog(
            $"Health growth captured equipment life drop: reason={reason}, life={hero.life}, baseline={baseline.Value}, maxLife={hero.maxLife}, lastSeenLife={(_lastSeenLife?.ToString() ?? "none")}, lastSeenMaxLife={(_lastSeenMaxLife?.ToString() ?? "none")}");
    }

    // 如果存在换装前生命快照，就把当前生命恢复到快照值。
    // 恢复前先重新套一遍最大生命加成，确保目标生命不会超过新的 maxLife。
    private static void RestorePendingEquipmentLife(Hero hero, string reason)
    {
        if (!ShouldProtectEquipmentLife(hero)) return;
        if (!_pendingEquipmentLife.HasValue) return;

        if (_pendingEquipmentLifeFrames <= 0)
        {
            ModEntry.DebugLog(
                $"Health growth discarded expired equipment life snapshot: reason={reason}, pendingLife={_pendingEquipmentLife.Value}, currentLife={hero.life}");
            _pendingEquipmentLife = null;
            return;
        }

        _pendingEquipmentLifeFrames--;

        var targetLife = System.Math.Min(hero.maxLife, _pendingEquipmentLife.Value);
        if (hero.life >= targetLife)
        {
            if (_pendingEquipmentLifeFrames == 0)
            {
                _pendingEquipmentLife = null;
            }

            return;
        }

        var result = ReapplyAccumulatedBonus(
            hero,
            $"equipment life snapshot/{reason}",
            forcedLife: targetLife);

        ModEntry.DebugLog(
            $"Health growth restored equipment life snapshot: reason={reason}, life {result.OldLife}->{hero.life}, maxLife {result.OldMaxLife}->{hero.maxLife}, framesLeft={_pendingEquipmentLifeFrames}");

        if (_pendingEquipmentLifeFrames == 0)
        {
            _pendingEquipmentLife = null;
        }
    }

    // 在玩家击杀敌人后增加最大生命值。
    private static void ApplyLifeGrowthOnKill(Hero hero, Mob mob)
    {
        try
        {
            // 只处理当前玩家英雄，避免训练、分身或特殊流程误触发。
            if (!ReferenceEquals(hero, Game.Instance.HeroInstance)) return;

            // 没选择“细胞活力”变异时完全不生效。
            if (!_isSelected) return;

            // 同一个 Mob 对象只处理一次，避免重复回调导致多次加血。
            if (!RememberMobOnce(mob)) return;

            // 0 细胞时当前难度数为 0，所以击杀不增加最大生命值。
            var bossCells = System.Math.Clamp(ModEntry.GetEffectiveBossCells(hero), 0, 5);
            var addedMaxLife = bossCells * LifePerBossCellPerKill;
            if (addedMaxLife <= 0) return;

            // 累计加成只记“本功能额外给了多少”，不直接把当前 maxLife 当作永久基础值。
            // 这样换护符导致原游戏重算生命时，不会把旧加成再次算进基础生命。
            _accumulatedMaxLifeBonus += addedMaxLife;
            // 击杀只增加“最大生命值”，不治疗“当前生命值”。
            // 例如当前 50/100，击杀后应该变成 50/200，而不是 150/200。
            var lifeBeforeGrowth = hero.life;
            var result = ReapplyAccumulatedBonus(hero, "kill", forcedLife: lifeBeforeGrowth);

            ModEntry.DebugLog(
                $"Health growth applied: bossCells={bossCells}, addMaxLife={addedMaxLife}, totalBonus={_accumulatedMaxLifeBonus}, maxLife {result.OldMaxLife}->{hero.maxLife}, life {result.OldLife}->{hero.life}");
        }
        catch (Exception ex)
        {
            // 击杀回调里不能把异常抛回游戏，否则容易导致死亡结算或关卡流程崩溃。
            ModEntry.DebugLog($"Health growth failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // 把累计生命加成重新套到当前英雄身上。返回旧值用于日志记录。
    private static (int OldMaxLife, int OldLife) ReapplyAccumulatedBonus(
        Hero hero,
        string reason,
        int? forcedLife = null)
    {
        var oldMaxLife = hero.maxLife;
        var oldLife = hero.life;

        // 如果当前 maxLife 等于上次本功能写出的值，说明旧加成还在身上，基础生命要扣掉旧加成。
        // 如果不相等，通常代表游戏因为护符/装备刷新了 maxLife，当前值已经是新的基础生命。
        var baseMaxLife = _lastAppliedMaxLife == hero.maxLife
            ? System.Math.Max(1, hero.maxLife - _appliedMaxLifeBonus)
            : System.Math.Max(1, hero.maxLife);

        var targetMaxLife = baseMaxLife + _accumulatedMaxLifeBonus;
        // 默认保留当前生命值；只有装备刷新保护传入 forcedLife 时，才恢复到指定快照。
        // 这里绝不因为最大生命值增加而额外治疗当前生命。
        var targetLife = System.Math.Min(targetMaxLife, forcedLife ?? hero.life);

        try
        {
            _isWritingLife = true;
            hero.overrideMaxLife(targetMaxLife);
        }
        catch
        {
            hero.maxLife = targetMaxLife;
        }
        finally
        {
            _isWritingLife = false;
        }

        _isWritingLife = true;
        hero.life = targetLife;
        _isWritingLife = false;
        _appliedMaxLifeBonus = _accumulatedMaxLifeBonus;
        _lastAppliedMaxLife = hero.maxLife;

        try
        {
            hero.updateLifeBar();
        }
        catch
        {
            // UI 刷新失败时保持静默，避免影响游戏流程。
        }

        if (reason != "kill")
        {
            ModEntry.DebugLog(
                $"Health growth reapplied: reason={reason}, baseMaxLife={baseMaxLife}, totalBonus={_accumulatedMaxLifeBonus}, maxLife {oldMaxLife}->{hero.maxLife}, life {oldLife}->{hero.life}");
        }

        return (oldMaxLife, oldLife);
    }

    // 记录一个敌人对象是否已经处理过。返回 true 表示本次是第一次见到。
    private static bool RememberMobOnce(Mob mob)
    {
        var mobKey = RuntimeHelpers.GetHashCode(mob);
        if (_recentMobKeySet.Contains(mobKey)) return false;

        _recentMobKeySet.Add(mobKey);
        _recentMobKeys.Enqueue(mobKey);

        // 保留最近 512 个死亡对象已经足够防重复，同时避免集合无限增长。
        while (_recentMobKeys.Count > 512)
        {
            var oldKey = _recentMobKeys.Dequeue();
            _recentMobKeySet.Remove(oldKey);
        }

        return true;
    }
}
