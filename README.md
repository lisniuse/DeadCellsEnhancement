# DeadCellsEnhancement

死亡细胞综合功能增强 MOD，基于 Dead Cells Core Modding 开发。

这个仓库是多个增强功能的统一入口。每个功能都实现 `Core/IModFeature` 接口，由 `ModEntry` 放进一个列表里统一初始化和每帧更新；新增功能只需在 `Features/` 下加一个类并注册进列表。

## 功能列表

- 迅捷本能（`P_SpeedInstinct` 变异）：选择后，武器攻击速度按当前激活的 Boss 细胞数提高（0 细胞不生效）。
- 细胞活力（`P_CellVitality` 变异）：选择后，每击杀一个敌人，最大生命值增加 `当前 Boss 细胞数 x 100`；0 细胞不生效。
- 变异上限突破：把原版通常 3 个变异的选择上限提高到 6 个，但保留“每个安全屋只能新增 1 个”的原版节奏。
- 传奇重铸：在小铸造所每件可重铸装备下追加一个选项，花费 6000 金币把该装备重铸为随机金色（传奇）品质。

## 调试配置

本地测试时，可以在已安装 MOD 的 DLL 同级目录创建 `debug_speed_config.json`：

```json
{
  "SpeedLevel": 2
}
```

`SpeedLevel` 范围是 `0` 到 `5`，含义是“模拟当前为几细胞”。这个值是全局的“模拟 Boss 细胞数”，所有依赖细胞数的功能（迅捷本能、细胞活力、变异选择条件）都会读取它。如果文件不存在，MOD 进入正式模式，读取游戏真实 Boss 细胞数；0 细胞时这些功能都不生效。

迅捷本能的攻速加成档位：

| Boss 细胞 / SpeedLevel | 攻速加成 |
| --- | --- |
| 0 | 0% |
| 1 | 20% |
| 2 | 30% |
| 3 | 40% |
| 4 | 40% |
| 5 | 50% |

## 开发说明

- MOD 名称：`DeadCellsEnhancement`
- 入口类：`DeadCellsEnhancement.ModEntry`
- 功能目录：`Features/`（每个功能实现 `Core/IModFeature`，在 `ModEntry._features` 列表中注册）
- 公共结构目录：`Core/`
- 语言包：`lang/DeadCellsEnhancement.zh.mo`、`lang/DeadCellsEnhancement.en.mo`
- 调试日志：已安装 MOD 目录下的 `moddbg.log`

### 构建

`dotnet build` 需要两个环境变量定位 MDK 和游戏：

```powershell
$env:DCCM_MDK_ROOT     = '<游戏目录>\dev\core\mdk\bin'
$env:DEAD_CELLS_GAME_PATH = '<游戏目录>'
dotnet build -c Debug
```

`AutoInstallMod=true`，构建成功后会自动安装到 `<游戏目录>\coremod\mods\DeadCellsEnhancement\`。

### 修改翻译

编辑 `lang/*.po` 后，用 Python 自带的 `msgfmt.py` 重新生成 `.mo`，再重新构建以打进 `res.pak`：

```powershell
python <Python>/Tools/i18n/msgfmt.py -o lang/DeadCellsEnhancement.zh.mo lang/DeadCellsEnhancement.zh.po
python <Python>/Tools/i18n/msgfmt.py -o lang/DeadCellsEnhancement.en.mo lang/DeadCellsEnhancement.en.po
```
