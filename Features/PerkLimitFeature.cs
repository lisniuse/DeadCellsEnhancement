using dc.en.inter.npc;
using dc.ui;
using HaxeProxy.Runtime;
using ModCore.Modules;
using Hook_Hero = dc.en.Hook_Hero;
using Hook_PrGame = dc.pr.Hook_Game;

namespace DeadCellsEnhancement;

// “变异上限突破”功能：把原版通常 3 个变异的限制提高到指定数量。
internal static class PerkLimitFeature
{
    // 先给一个保守但明显够测试的上限。后续如果 UI 表现正常，可以再改成配置项。
    private const int MaxPerks = 6;

    // 每次打开安全屋/变异 NPC 时允许新增的变异数量。
    // 这里保持原版节奏：一次安全屋只能选 1 个，只是总上限从 3 提高到 6。
    private const int MaxPerksPerVisit = 1;

    // 当前这次和变异 NPC 交互时，允许玩家达到的“总变异数量”。
    // 例如进安全屋时已有 3 个，本次只允许达到 4 个；下一次安全屋再允许达到 5 个。
    private static int? _maxPerksAllowedThisVisit;

    // 打开本次变异选择界面时，玩家已经拥有的变异数量。只用于日志和排查。
    private static int _perkCountAtVisitStart;

    // 初始化功能，注册变异选择界面和变异 NPC 的上限相关 hook。
    internal static void Initialize()
    {
        Hook_PrGame.loadMainLevel += Hook_Game_loadMainLevel;
        Hook_Hero.onLevelChanged += Hook_Hero_onLevelChanged;
        Hook_PerkSelect.getMaxPerksHere += Hook_PerkSelect_getMaxPerksHere;
        Hook_PerkSelect.requirementsOk += Hook_PerkSelect_requirementsOk;
        Hook_PerkMaster.onActivate += Hook_PerkMaster_onActivate;
        Hook_PerkMaster.getAvailablePerks += Hook_PerkMaster_getAvailablePerks;
        ModEntry.DebugLog($"PerkLimitFeature initialized: MaxPerks={MaxPerks}.");
    }

    // 原游戏加载新的主关卡时调用。
    // 这里清空“本安全屋已使用过的选择名额”，让下一个安全屋重新允许新增 1 个变异。
    private static void Hook_Game_loadMainLevel(
        Hook_PrGame.orig_loadMainLevel orig,
        dc.pr.Game self,
        dc.cine.LevelTransition cine,
        dc.String id,
        Ref<bool> activate,
        int? forcedSeed)
    {
        ResetPerkVisitLimit($"load main level: {id}");
        orig(self, cine, id, activate, forcedSeed);
    }

    // 英雄切换关卡时也兜底清空一次。
    // 某些过渡流程可能不会完整走 loadMainLevel，挂这里可以避免本关名额带到下一关。
    private static void Hook_Hero_onLevelChanged(Hook_Hero.orig_onLevelChanged orig, dc.en.Hero self, dc.pr.Level oldLevel)
    {
        ResetPerkVisitLimit("hero level changed");
        orig(self, oldLevel);
    }

    // 变异选择 UI 会调用这个函数询问“这里最多允许几个变异”。
    private static int Hook_PerkSelect_getMaxPerksHere(Hook_PerkSelect.orig_getMaxPerksHere orig, PerkSelect self)
    {
        try
        {
            var originalMax = orig(self);
            var patchedMax = GetMaxPerksAllowedThisVisit();
            if (patchedMax != originalMax)
            {
                ModEntry.DebugLog(
                    $"PerkSelect max perks patched: original={originalMax}, visitStart={_perkCountAtVisitStart}, patched={patchedMax}, totalLimit={MaxPerks}");
            }

            return patchedMax;
        }
        catch (Exception ex)
        {
            ModEntry.DebugLog($"PerkSelect max perks hook failed: {ex.GetType().Name}: {ex.Message}");
            return GetMaxPerksAllowedThisVisit();
        }
    }

    // 变异选择 UI 会调用这个函数判断某个变异是否满足选择条件。
    // 本 Mod 的两个变异都依赖 Boss 细胞难度：0 细胞时效果为 0，所以直接禁止选择，避免玩家拿到“空效果”变异。
    private static bool Hook_PerkSelect_requirementsOk(Hook_PerkSelect.orig_requirementsOk orig, PerkSelect self, dc.String k)
    {
        var originalOk = orig(self, k);
        if (!originalOk) return false;

        var mutationId = k.ToString();
        if (!IsBossCellLockedMutation(mutationId)) return true;

        var hero = Game.Instance.HeroInstance;
        if (hero == null) return false;

        var bossCells = ModEntry.GetEffectiveBossCells(hero);
        var allowed = bossCells > 0;
        if (!allowed)
        {
            ModEntry.DebugLog($"Perk requirement blocked at 0 boss cells: mutation={mutationId}, bossCells={bossCells}");
        }

        return allowed;
    }

    // 和收藏家/变异 NPC 交互时，记录“这次安全屋开始时已有几个变异”。
    // 原版一次安全屋只能新增 1 个，所以本次允许达到的总数 = 开始数量 + 1，但不能超过全局上限 6。
    private static void Hook_PerkMaster_onActivate(Hook_PerkMaster.orig_onActivate orig, PerkMaster self, dc.en.Hero by, bool lp)
    {
        EnsurePerkVisitStarted();

        try
        {
            self.maxPerkHere = GetMaxPerksAllowedThisVisit();
        }
        catch
        {
            // 某些阶段字段可能暂时不可写，继续走原版逻辑即可。
        }

        orig(self, by, lp);

        try
        {
            self.maxPerkHere = GetMaxPerksAllowedThisVisit();
        }
        catch
        {
            // 原版逻辑后再兜底一次，失败也不影响游戏。
        }
    }

    // 变异 NPC 用这个函数计算“本次交互还可以选几个变异”。
    // 注意：这里不是总上限。总上限由 getMaxPerksHere/maxPerkHere 控制。
    // 如果这里返回 MaxPerks - selectedCount，就会导致一个安全屋里直接选满 6 个，破坏原版节奏。
    private static int Hook_PerkMaster_getAvailablePerks(Hook_PerkMaster.orig_getAvailablePerks orig, PerkMaster self)
    {
        try
        {
            var originalAvailable = orig(self);
            var selectedCount = GetSelectedPerkCount();
            var remainingThisVisit = GetMaxPerksAllowedThisVisit() - selectedCount;

            // 已经达到总上限时，本次不能再选。
            // 还没达到总上限时，本次最多只允许选 1 个，和原版每个安全屋的选择节奏一致。
            var patchedAvailable = remainingThisVisit > 0 ? System.Math.Min(MaxPerksPerVisit, remainingThisVisit) : 0;

            if (patchedAvailable != originalAvailable)
            {
                ModEntry.DebugLog(
                    $"PerkMaster available perks patched: selected={selectedCount}, visitStart={_perkCountAtVisitStart}, visitMax={GetMaxPerksAllowedThisVisit()}, totalLimit={MaxPerks}, available {originalAvailable}->{patchedAvailable}");
            }

            return patchedAvailable;
        }
        catch (Exception ex)
        {
            ModEntry.DebugLog($"PerkMaster available perks hook failed: {ex.GetType().Name}: {ex.Message}");
            return MaxPerksPerVisit;
        }
    }

    // 开始一次新的变异选择交互，计算这次最多允许达到几个变异。
    private static void EnsurePerkVisitStarted()
    {
        if (_maxPerksAllowedThisVisit.HasValue) return;
        StartNewPerkVisit();
    }

    // 开始当前关卡/安全屋的变异选择额度，计算这次最多允许达到几个变异。
    private static void StartNewPerkVisit()
    {
        _perkCountAtVisitStart = GetSelectedPerkCount();
        _maxPerksAllowedThisVisit = System.Math.Min(MaxPerks, _perkCountAtVisitStart + MaxPerksPerVisit);

        ModEntry.DebugLog(
            $"Perk visit started: selected={_perkCountAtVisitStart}, visitMax={_maxPerksAllowedThisVisit.Value}, totalLimit={MaxPerks}, perVisitLimit={MaxPerksPerVisit}");
    }

    // 清空当前关卡/安全屋的变异选择额度。
    // 这样 ESC 关闭再打开不会刷新额度；只有关卡变化后才会重新给 1 个新增名额。
    private static void ResetPerkVisitLimit(string reason)
    {
        if (_maxPerksAllowedThisVisit.HasValue)
        {
            ModEntry.DebugLog(
                $"Perk visit reset: reason={reason}, visitStart={_perkCountAtVisitStart}, visitMax={_maxPerksAllowedThisVisit.Value}, selectedNow={GetSelectedPerkCount()}");
        }

        _maxPerksAllowedThisVisit = null;
        _perkCountAtVisitStart = 0;
    }

    // 返回本次交互允许达到的总变异数量。
    // 如果 UI 在 onActivate 之前就询问上限，就用当前数量临时初始化一次，避免回退到原版 3 个上限。
    private static int GetMaxPerksAllowedThisVisit()
    {
        if (!_maxPerksAllowedThisVisit.HasValue)
        {
            EnsurePerkVisitStarted();
        }

        return _maxPerksAllowedThisVisit.GetValueOrDefault(MaxPerksPerVisit);
    }

    // 读取当前玩家已经选择了几个变异。
    private static int GetSelectedPerkCount()
    {
        try
        {
            var hero = Game.Instance.HeroInstance;
            var perks = hero?.inventory?.getAllPerks();
            return perks?.length ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    // 判断是否是本 Mod 中需要 1 细胞以上才允许选择的变异。
    private static bool IsBossCellLockedMutation(string mutationId)
    {
        return mutationId == SpeedInstinctFeature.MutationId
            || mutationId == HealthGrowthFeature.MutationId;
    }
}
