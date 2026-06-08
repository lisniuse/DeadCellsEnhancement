using dc.en;
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

    // 已经处理过的敌人对象身份。防止同一个死亡事件被多个游戏流程重复通知时反复加生命。
    private static readonly System.Collections.Generic.Queue<int> _recentMobKeys = [];

    // 最近处理过的敌人集合。配合队列做上限控制，避免长时间游戏后无限增长。
    private static readonly System.Collections.Generic.HashSet<int> _recentMobKeySet = [];

    // 初始化功能，注册英雄击杀敌人的回调。
    internal static void Initialize()
    {
        Hook_Hero.onMobDeath += Hook_Hero_onMobDeath;
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
    }

    // 原游戏 Hero.onMobDeath 的 Hook。先让原版逻辑完整执行，再追加最大生命值成长。
    private static void Hook_Hero_onMobDeath(Hook_Hero.orig_onMobDeath orig, Hero self, Mob mob)
    {
        orig(self, mob);
        ApplyLifeGrowthOnKill(self, mob);
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

            // 先记录旧值，便于日志确认。
            var oldMaxLife = hero.maxLife;
            var oldLife = hero.life;
            var newMaxLife = oldMaxLife + addedMaxLife;
            var newLife = System.Math.Min(newMaxLife, oldLife + addedMaxLife);

            // overrideMaxLife 是原游戏公开方法，优先用它让生命上限变化走游戏自己的刷新链路。
            try
            {
                hero.overrideMaxLife(newMaxLife);
            }
            catch
            {
                // 个别阶段 overrideMaxLife 如果不可用，就直接写 maxLife 兜底。
                hero.maxLife = newMaxLife;
            }

            // 同步补充当前生命，让“最大生命增加”立刻有体感。
            hero.life = newLife;

            // 刷新生命条显示。失败也不影响数值本身。
            try
            {
                hero.updateLifeBar();
            }
            catch
            {
                // UI 刷新失败时保持静默，避免影响游戏流程。
            }

            ModEntry.DebugLog(
                $"Health growth applied: bossCells={bossCells}, addMaxLife={addedMaxLife}, maxLife {oldMaxLife}->{hero.maxLife}, life {oldLife}->{hero.life}");
        }
        catch (Exception ex)
        {
            // 击杀回调里不能把异常抛回游戏，否则容易导致死亡结算或关卡流程崩溃。
            ModEntry.DebugLog($"Health growth failed: {ex.GetType().Name}: {ex.Message}");
        }
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
