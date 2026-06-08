using dc.en;
using dc.tool;
using dc.ui;
using HaxeProxy.Runtime;
using ModCore.Modules;
using ModCore.Utilities;

namespace DeadCellsEnhancement;

// “传奇重铸”功能：在小铸造所每件可重铸装备下面追加一个自定义选项。
// 玩家花费固定金币后，把当前装备升级/重随为传奇（金色）品质。
internal static class LegendaryForgeFeature
{
    // 每次尝试升级为传奇品质需要消耗的金币数量。
    private const int LegendaryUpgradeCost = 6000;

    // 自定义选项在 UI 上显示的文字。先用纯文本验证功能，后续可以继续打磨成带金币图标的原版样式。
    private const string LegendaryUpgradeLabel = "升级为随机金色品质 6000";

    // 初始化功能，挂进小铸造所的装备行创建流程。
    internal static void Initialize()
    {
        Hook_ForgeUnderground.addItem += Hook_ForgeUnderground_addItem;
        ModEntry.DebugLog($"LegendaryForgeFeature initialized: cost={LegendaryUpgradeCost}.");
    }

    // 小铸造所每添加一件装备时会调用 addItem。
    // 原版会在这里生成“重铸额外特性”“提升到 ++ 质量”等选项；我们在原版逻辑后追加一项。
    private static dc.h2d.Flow Hook_ForgeUnderground_addItem(
        Hook_ForgeUnderground.orig_addItem orig,
        ForgeUnderground self,
        InventItem item,
        dc.h2d.Flow parent)
    {
        var itemFlow = orig(self, item, parent);

        try
        {
            AddLegendaryUpgradeChoice(self, item, itemFlow);
        }
        catch (Exception ex)
        {
            // UI 构建失败时不能影响原版小铸造所，否则会导致打开 NPC 闪退。
            ModEntry.DebugLog($"Legendary forge add choice failed: {ex.GetType().Name}: {ex.Message}");
        }

        return itemFlow;
    }

    // 在指定装备行里追加“升级为随机金色品质”选项，并注册到小铸造所的选择系统。
    private static void AddLegendaryUpgradeChoice(ForgeUnderground forge, InventItem item, dc.h2d.Flow itemFlow)
    {
        // FlowBox 是原版 UI 中可被 registerChoice 管理的选项容器。
        var choiceBox = new FlowBox(itemFlow)
        {
            padH = 8,
            padV = 4,
            minWidth = 490,
            isVertical = false,
            horizontalSpacing = 12,
            verticalAlign = new dc.h2d.FlowAlign.Middle()
        };

        // 用游戏自己的 ui.Text 创建文字，确保字体、缩放和语言渲染跟原界面一致。
        var textScale = 1.0;
        var label = new Text(
            choiceBox,
            false,
            false,
            new Ref<double>(ref textScale),
            new ImageVerticalAlign.Middle(),
            null);
        label.text = LegendaryUpgradeLabel.AsHaxeString();

        // canBeUsed 决定选项是否可用。这里保守返回 true，把金币是否足够的判断放进点击回调。
        // 这样即使当前金币字段不好读，也不会因为误判导致按钮消失。
        HlFunc<bool> canBeUsed = () => true;

        // cb 是确认选择后的实际执行逻辑。
        HlAction onConfirm = () => TryUpgradeToLegendary(forge, item);

        // onSelect 是光标移动到该选项时触发的逻辑；更新右侧装备描述即可。
        HlAction onSelect = () => forge.setItemDesc(item);

        forge.registerChoice(choiceBox, canBeUsed, onConfirm, onSelect);
        ModEntry.DebugLog($"Legendary forge choice added: item={SafeItemLabel(item)}, cost={LegendaryUpgradeCost}");
    }

    // 尝试花钱把装备升级/重随为传奇品质。
    private static void TryUpgradeToLegendary(ForgeUnderground forge, InventItem item)
    {
        try
        {
            var hero = Game.Instance.HeroInstance;
            if (hero == null)
            {
                ModEntry.DebugLog("Legendary forge skipped: hero is null.");
                return;
            }

            var noStats = false;
            var paid = hero.tryToSubstractMoney(LegendaryUpgradeCost, new Ref<bool>(ref noStats));
            if (paid < LegendaryUpgradeCost)
            {
                ModEntry.DebugLog($"Legendary forge skipped: not enough money, paid={paid}, cost={LegendaryUpgradeCost}.");
                return;
            }

            // 原游戏 ItemGen.finalizeLegendary 会按原版规则把物品处理成传奇品质，并生成对应传奇词条。
            var itemGen = new dc.level.ItemGen(Environment.TickCount, false);
            itemGen.finalizeLegendary(item);

            // 刷新右侧说明面板，让玩家立即看到金色品质/传奇词条变化。
            forge.setItemDesc(item);

            // 装备已经在身上时，请求英雄刷新装备属性，避免 UI 变化但实际属性未同步。
            var updateHud = true;
            var duringHeroInit = false;
            var duringItemTransform = false;
            hero.onEquipedItemsChange(
                new Ref<bool>(ref updateHud),
                new Ref<bool>(ref duringHeroInit),
                new Ref<bool>(ref duringItemTransform));

            ModEntry.DebugLog($"Legendary forge upgraded item: item={SafeItemLabel(item)}, paid={paid}.");
        }
        catch (Exception ex)
        {
            ModEntry.DebugLog($"Legendary forge upgrade failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // 尽量拿到一个不会抛异常的物品描述，供日志排查使用。
    private static string SafeItemLabel(InventItem item)
    {
        try
        {
            return $"{item.getItemKind()}@level={item.getRawItemLevel()}";
        }
        catch
        {
            return item.GetType().FullName ?? "unknown item";
        }
    }
}
