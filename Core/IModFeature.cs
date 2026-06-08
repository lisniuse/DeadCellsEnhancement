using dc.en;

namespace DeadCellsEnhancement.Core;

// 功能模块的统一接口。所有 Feature 都实现它，由 ModEntry 放进一个列表里统一初始化和每帧更新。
internal interface IModFeature
{
    // Mod 初始化时调用，用来注册 Hook 或准备运行时状态。
    void Initialize();

    // 英雄每帧更新时调用。只有依赖每帧状态（变异、装备、生命）的功能才需要覆写；默认空实现。
    void OnHeroUpdate(Hero hero) { }
}
