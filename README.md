# DeadCellsEnhancement

死亡细胞综合功能增强 MOD，基于 Dead Cells Core Modding 开发。

这个仓库会作为后续功能的统一入口，不再只围绕单个攻速功能组织代码。当前已实现功能是“迅捷本能”变异：只有玩家选择这项变异后，武器攻击速度才会根据当前激活的 Boss 细胞数提高。

## 功能列表

- 迅捷本能：按 Boss 细胞数提高武器攻击速度。

## 调试配置

本地测试时，可以在已安装 MOD 的 DLL 同级目录创建 `debug_speed_config.json`：

```json
{
  "SpeedLevel": 2
}
```

`SpeedLevel` 范围是 `0` 到 `5`，含义是“模拟当前为几细胞”。如果这个文件不存在，MOD 会进入正式模式，读取游戏真实 Boss 细胞数；0 细胞时没有任何攻速加成。

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
- 语言包：`lang/DeadCellsEnhancement.zh.mo`、`lang/DeadCellsEnhancement.en.mo`
- 调试日志：已安装 MOD 目录下的 `moddbg.log`
