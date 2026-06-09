using dc.en;
using dc.tool;
using dc.ui;
using HaxeProxy.Runtime;
using ModCore.Modules;
using ModCore.Utilities;

namespace DeadCellsEnhancement;

// “传奇重铸”功能：在小铸造所每件可重铸装备下面追加一个自定义选项。
// 玩家花费固定金币后，把当前装备升级/重随为传奇（金色）品质。
internal sealed class LegendaryForgeFeature : Core.IModFeature
{
    // 每次尝试升级为传奇品质需要消耗的金币数量。
    private const int LegendaryUpgradeCost = 6000;

    // 本功能对应的「形态(Aspect)」物品 id。group 15 即形态；只有玩家在形态界面选了它，
    // 重铸商店才会出现金色重铸选项。这个 id 会写进存档解锁记录，确定后不要改名。
    internal const string AspectId = "ASP_LegendaryForge";

    // 选项文字里需要翻译的部分。金币数量在运行时拼到后面，所以这里只翻译描述本身。
    // 翻译来自 lang/DeadCellsEnhancement.<lang>.mo，英文回退到 msgid 本身。
    private const string LegendaryUpgradeLabelKey = "Reforge to random Legendary quality";

    // 是否已经把本形态解锁到存档里。形态必须先在 itemMeta 里解锁，才会出现在形态选择界面。
    private bool _aspectUnlocked;

    // 初始化功能，挂进小铸造所的装备行创建流程。
    public void Initialize()
    {
        Hook_ForgeUnderground.addItem += Hook_ForgeUnderground_addItem;
        ModEntry.DebugLog($"LegendaryForgeFeature initialized: cost={LegendaryUpgradeCost}, aspect={AspectId}.");
    }

    // 每帧检查一次：进入游戏后尽早把本形态解锁进存档，使其出现在形态选择界面。
    // 解锁是持久化的 meta 数据，成功一次后就不再尝试。
    public void OnHeroUpdate(Hero hero)
    {
        if (_aspectUnlocked) return;
        if (TryUnlockAspect()) _aspectUnlocked = true;
    }

    // 把本形态解锁到玩家存档的 itemMeta 里。已解锁则跳过，避免重复触发解锁提示。
    private static bool TryUnlockAspect()
    {
        try
        {
            var meta = dc.pr.Game.Class.ME?.user?.itemMeta;
            if (meta == null) return false;

            var key = AspectId.AsHaxeString();
            if (!meta.hasUnlockedItem(key))
            {
                meta.unlockItem(key);
                ModEntry.DebugLog($"Unlocked aspect into itemMeta: {AspectId}.");
            }

            return true;
        }
        catch (Exception ex)
        {
            ModEntry.DebugLog($"Unlock aspect failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // 判断当前玩家是否选择了「炼金铸造」形态。形态选中后会作为 InventItem 进入背包，用 hasItem 查询即可。
    private static bool PlayerHasAspect()
    {
        try
        {
            var hero = Game.Instance.HeroInstance;
            return hero?.inventory != null && hero.inventory.hasItem(AspectId.AsHaxeString());
        }
        catch
        {
            return false;
        }
    }

    // 小铸造所每添加一件装备时会调用 addItem。
    // 原版会在这里生成“重铸额外特性”“提升到 ++ 质量”等选项；我们在原版逻辑后追加一项。
    private dc.h2d.Flow Hook_ForgeUnderground_addItem(
        Hook_ForgeUnderground.orig_addItem orig,
        ForgeUnderground self,
        InventItem item,
        dc.h2d.Flow parent)
    {
        var itemFlow = orig(self, item, parent);

        // 形态门控：只有玩家选择了「炼金铸造」形态，才在重铸商店追加金色重铸选项。
        // 没选这个形态时，重铸商店保持原版样子。
        if (itemFlow != null && PlayerHasAspect())
        {
            try
            {
                AddLegendaryUpgradeChoice(self, item, itemFlow);
            }
            catch (Exception ex)
            {
                // UI 构建失败时不能影响原版小铸造所，否则会导致打开 NPC 闪退。
                // 记录完整堆栈，方便定位是哪一步（FlowBox/Text/registerChoice）抛的异常。
                ModEntry.DebugLog($"Legendary forge add choice failed: {ex}");
            }
        }

        // orig 的返回类型标注为非空，但运行时可能为 null；这里原样返回它（含 null），
        // 用 null-forgiving 仅抑制静态告警，行为与原版 addItem 一致。
        return itemFlow!;
    }

    // 在指定装备行里追加“升级为随机金色品质”选项，并注册到小铸造所的选择系统。
    private void AddLegendaryUpgradeChoice(ForgeUnderground forge, InventItem item, dc.h2d.Flow itemFlow)
    {
        // 原版把每件装备的两个选项放在“行内的竖直列容器”里（itemFlow 下那个 isVertical 的 Flow），
        // 而不是直接放在整行上。我们也要挂进同一个竖直列，选项才会作为第 3 行堆叠显示出来。
        // 找不到竖直列时退回整行，至少保证逻辑可用。
        var column = FindVerticalColumn(itemFlow) ?? itemFlow;

        // 关键修复：必须用原版工厂 createBoxMain 创建选项盒子，而不是 new FlowBox。
        // createBoxMain 会正确初始化 UIBox 背景和内部 reflow 结构；之前手搓的 new FlowBox 缺这些，
        // 游戏在布局/onResize 遍历这一行时就会踩到未初始化的内部对象，触发 "Null access .onResize" 崩溃。
        // 参数 (parent, padH=6, padV=7, biomeColor=null) 与原版重铸行保持一致。
        var choiceBox = FlowBox.Class.createBoxMain.Invoke(column, 6, 7, (int?)0);
        choiceBox.set_verticalAlign(new dc.h2d.FlowAlign.Middle());
        choiceBox.box.alpha = 0.2;

        // 左侧名称文字。用游戏自己的 ui.Text（等价于原版 makeText），确保字体/缩放/语言渲染与原界面一致。
        var textScale = 1.0;
        var label = new Text(
            choiceBox,
            true,
            false,
            new Ref<double>(ref textScale),
            new ImageVerticalAlign.Middle(),
            null);
        label.set_text(GetText.Instance.GetString(LegendaryUpgradeLabelKey).AsHaxeString());
        label.set_textColor(0xFFFFFF);
        label.onResize();

        // 右侧价格文字：金额 + 金币图标（{iconCoin@img} 是游戏富文本的金币图标标签）。
        // 通过 getProperties(...).horizontalAlign = Right 让它和原版选项一样贴右边对齐。
        var costText = new Text(
            choiceBox,
            true,
            false,
            new Ref<double>(ref textScale),
            new ImageVerticalAlign.Middle(),
            null);
        costText.set_text($"{LegendaryUpgradeCost}{{iconCoin@img}}".AsHaxeString());
        costText.onResize();
        choiceBox.getProperties(costText).horizontalAlign = new dc.h2d.FlowAlign.Right();

        // canBeUsed 决定选项是否可用：金币够才可用。
        // 它同时驱动两件事——onBeforeReflow 里据此把选项变灰（开界面即判断），
        // 以及 onValidate 里金币不够时自动播放错误音效 + 摇头动画并拒绝执行。
        HlFunc<bool> canBeUsed = () => GetCurrentMoney() >= LegendaryUpgradeCost;

        // cb 是确认选择后的实际执行逻辑。
        HlAction onConfirm = () => TryUpgradeToLegendary(forge, item);

        // onSelect 是光标移动到该选项时触发的逻辑；更新右侧装备描述即可。
        HlAction onSelect = () => forge.setItemDesc(item);

        // registerChoice 返回这个选项在铸造所选择系统里的句柄，onClick/onMove 要用它定位自己的下标。
        var choice = forge.registerChoice(choiceBox, canBeUsed, onConfirm, onSelect);

        // 让盒子可交互：鼠标可点击、控制器光标可定位，和原版重铸选项一致。
        choiceBox.set_enableInteractive(true);
        choiceBox.interactive.set_cursor(new dc.hxd.Cursor.Button());
        choiceBox.interactive.propagateEvents = true;

        // 关键修复：原版的 onClick 不是直接执行动作，而是“把当前选择切到本选项 → select 刷新光标 → onValidate”，
        // 由 onValidate 调用本选项注册的 cb（即 TryUpgradeToLegendary）。这样鼠标和键盘/手柄都能正确选中并确认。
        choiceBox.interactive.onClick = (HlAction<dc.hxd.Event>)(_ =>
        {
            forge.currentIdx = forge.choices.indexOf(choice, (int?)null);
            var scroll = false;
            forge.select(true, new Ref<bool>(ref scroll));
            forge.onValidate();
        });

        // onMove：鼠标悬停到本选项时，把光标移过来并刷新高亮，和原版选项手感一致。
        choiceBox.interactive.onMove = (HlAction<dc.hxd.Event>)(_ =>
        {
            forge.currentIdx = forge.choices.indexOf(choice, (int?)null);
            var scroll = false;
            forge.select(true, new Ref<bool>(ref scroll));
        });

        // 关键：原版每个选项盒子都挂了 onBeforeReflow，在布局阶段对内部文字调用 onResize() 把它排好。
        // 缺这一步时，游戏后续布局会踩到未排版的 Text。这里只对子元素调用 onResize，
        // 绝不能调用 choiceBox 自身的 reflow，否则 reflow→onBeforeReflow→reflow 会无限递归。
        choiceBox.onBeforeReflow = (HlAction)(() =>
        {
            try
            {
                label.onResize();
                costText.onResize();
                // 让本选项盒子的宽度和原版兄弟选项一致（原版用 fb.minWidth 的 0.45 倍），
                // 否则盒子只按文字内容收缩，价格也没法贴右、上下两行宽度也不齐。
                var width = (int)((forge.fb.minWidth ?? 0) * 0.45);
                choiceBox.set_minWidth((int?)width);
                choiceBox.set_maxWidth((int?)width);

                // 完全照原版 onBeforeReflow 的着色：买得起→名称白色、价格金色(GO)；
                // 买不起→整盒调暗、名称灰色、价格红色(LO)。这样灰显程度和官方选项一致。
                var affordable = GetCurrentMoney() >= LegendaryUpgradeCost;
                choiceBox.alpha = affordable ? 1.0 : 0.5;
                label.set_textColor(affordable ? 0xFFFFFF : 0x7F7F7F);
                costText.set_textColor(GetUiColor(affordable ? "GO" : "LO", affordable ? 0xFFCC66 : 0xFF0000));
            }
            catch { /* 个别帧文字或 fb 尚未就绪时忽略，下一帧会再排 */ }
        });

        ModEntry.DebugLog($"Legendary forge choice added: item={SafeItemLabel(item)}, cost={LegendaryUpgradeCost}");
    }

    // 在装备行里找到那个竖直排列的选项列（原版把“重铸/升品质”两个选项放在这里）。
    // 行的结构是 [装备图标 Skill, 竖直列 Flow]，所以取第一个 isVertical 为 true 的 Flow 子节点即可。
    private static dc.h2d.Flow? FindVerticalColumn(dc.h2d.Flow row)
    {
        try
        {
            // children 是 Haxe Array<Object>，用 length/getDyn 逐个访问（和 strikeChain 的读法一致）。
            var children = row.children;
            for (var i = 0; i < children.length; i++)
            {
                if (children.getDyn(i) is dc.h2d.Flow childFlow && childFlow.isVertical)
                {
                    return childFlow;
                }
            }
        }
        catch
        {
            // 子节点结构异常时退回 null，调用方会改用整行作为父级。
        }

        return null;
    }

    // 读取游戏内置的 UI 命名颜色（如 "GO" 金色、"LO" 红色）。读不到时用传入的回退色。
    private static int GetUiColor(string key, int fallback)
    {
        try
        {
            return (int)dc.ui.Text.Class.COLORS.get(key.AsHaxeString());
        }
        catch
        {
            return fallback;
        }
    }

    // 读取玩家当前金币数。用游戏自己的 dc.pr.Game.Class.ME.data.money（原版判断买不起也读这里）。
    // 读不到时返回 int.MaxValue，宁可放行也不误判为买不起；真正扣款仍由 tryToSubstractMoney 兜底。
    private static int GetCurrentMoney()
    {
        try
        {
            return dc.pr.Game.Class.ME.data.money;
        }
        catch
        {
            return int.MaxValue;
        }
    }

    // 尝试花钱把装备升级/重随为传奇品质。
    private void TryUpgradeToLegendary(ForgeUnderground forge, InventItem item)
    {
        try
        {
            var hero = Game.Instance.HeroInstance;
            if (hero == null)
            {
                ModEntry.DebugLog("Legendary forge skipped: hero is null.");
                return;
            }

            // 不能变传奇的物品（受限、本就不可加 Legendary 词条且当前也不是传奇）直接跳过，避免白扣钱。
            var legendaryKey = "Legendary".AsHaxeString();
            if (!item.hasAffix(legendaryKey) && !item.canReceiveAffix(legendaryKey))
            {
                ModEntry.DebugLog($"Legendary forge skipped: item cannot be legendary, item={SafeItemLabel(item)}.");
                return;
            }

            var noStats = false;
            var paid = hero.tryToSubstractMoney(LegendaryUpgradeCost, new Ref<bool>(ref noStats));
            if (paid < LegendaryUpgradeCost)
            {
                ModEntry.DebugLog($"Legendary forge skipped: not enough money, paid={paid}, cost={LegendaryUpgradeCost}.");
                return;
            }

            // 关键修复：真正“变金色”是让物品带上 Legendary 词条后用原版规则重算属性。
            // finalizeLegendary 只加了 Colorless，并不会变传奇。正确做法是：
            // 1) 先给物品加上 Legendary 词条；
            // 2) 调用游戏自己的 forge.reforge(item, 0)，它内部会读到 hasAffix("Legendary")=true，
            //    清空旧词条并按传奇标志重新生成属性和词条，得到一把真正的金色（传奇）武器。
            if (!item.hasAffix(legendaryKey))
            {
                var ignoreChecks = true;
                item.addAffix(legendaryKey, new Ref<bool>(ref ignoreChecks));
            }

            forge.reforge(item, (int?)0);

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
    private string SafeItemLabel(InventItem item)
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
