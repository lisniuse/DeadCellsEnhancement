namespace DeadCellsEnhancement.Core;

// 功能模块的最小接口。后续如果功能变多，可以把各 feature 放进列表里统一初始化和更新。
internal interface ModFeature
{
    // Mod 初始化时调用，用来注册 Hook 或准备运行时状态。
    void Initialize();
}
