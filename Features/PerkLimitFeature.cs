using dc.en.inter.npc;
using dc.ui;
using ModCore.Modules;

namespace DeadCellsEnhancement;

// “变异上限突破”功能：把原版通常 3 个变异的限制提高到指定数量。
internal static class PerkLimitFeature
{
    // 先给一个保守但明显够测试的上限。后续如果 UI 表现正常，可以再改成配置项。
    private const int MaxPerks = 6;

    // 初始化功能，注册变异选择界面和变异 NPC 的上限相关 hook。
    internal static void Initialize()
    {
        Hook_PerkSelect.getMaxPerksHere += Hook_PerkSelect_getMaxPerksHere;
        Hook_PerkMaster.onActivate += Hook_PerkMaster_onActivate;
        Hook_PerkMaster.getAvailablePerks += Hook_PerkMaster_getAvailablePerks;
        ModEntry.DebugLog($"PerkLimitFeature initialized: MaxPerks={MaxPerks}.");
    }

    // 变异选择 UI 会调用这个函数询问“这里最多允许几个变异”。
    private static int Hook_PerkSelect_getMaxPerksHere(Hook_PerkSelect.orig_getMaxPerksHere orig, PerkSelect self)
    {
        try
        {
            var originalMax = orig(self);
            var patchedMax = System.Math.Max(originalMax, MaxPerks);
            if (patchedMax != originalMax)
            {
                ModEntry.DebugLog($"PerkSelect max perks patched: {originalMax}->{patchedMax}");
            }

            return patchedMax;
        }
        catch (Exception ex)
        {
            ModEntry.DebugLog($"PerkSelect max perks hook failed: {ex.GetType().Name}: {ex.Message}");
            return MaxPerks;
        }
    }

    // 和收藏家/变异 NPC 交互时，提前把 NPC 记录的当前上限抬高。
    private static void Hook_PerkMaster_onActivate(Hook_PerkMaster.orig_onActivate orig, PerkMaster self, dc.en.Hero by, bool lp)
    {
        try
        {
            self.maxPerkHere = System.Math.Max(self.maxPerkHere, MaxPerks);
        }
        catch
        {
            // 某些阶段字段可能暂时不可写，继续走原版逻辑即可。
        }

        orig(self, by, lp);

        try
        {
            self.maxPerkHere = System.Math.Max(self.maxPerkHere, MaxPerks);
        }
        catch
        {
            // 原版逻辑后再兜底一次，失败也不影响游戏。
        }
    }

    // 变异 NPC 用这个函数计算“还可以选几个变异”。
    private static int Hook_PerkMaster_getAvailablePerks(Hook_PerkMaster.orig_getAvailablePerks orig, PerkMaster self)
    {
        try
        {
            var originalAvailable = orig(self);
            var selectedCount = GetSelectedPerkCount();
            var patchedAvailable = System.Math.Max(originalAvailable, MaxPerks - selectedCount);

            if (patchedAvailable != originalAvailable)
            {
                ModEntry.DebugLog(
                    $"PerkMaster available perks patched: selected={selectedCount}, available {originalAvailable}->{patchedAvailable}");
            }

            return patchedAvailable;
        }
        catch (Exception ex)
        {
            ModEntry.DebugLog($"PerkMaster available perks hook failed: {ex.GetType().Name}: {ex.Message}");
            return MaxPerks;
        }
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
}
