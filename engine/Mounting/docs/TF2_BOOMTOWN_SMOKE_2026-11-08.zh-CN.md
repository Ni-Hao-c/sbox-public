# sp_boomtown_start 水雾过度集中问题

日期：2026-11-08。

用户截图显示水面上方的雾形成亮度很高、边界明显的扇形束。本次针对该地图的实际 FX 实体、零售 PCF、VMT 和 VTF 检查，修复两个相关的确定缺口。本文不代表该视角已完成原版游戏与挂载画面的视觉验收。

## 实际资源

`sp_boomtown_start` 的外部实体分区中有 628 个 `info_particle_system`。其中包含 `cenotes_upward_fog`、`cenotes_fall_top_med`、`wildfall_base_mist_lrg_01`、`wildfall_base_mist_med_01` 等水雾效果。

这些效果来自零售 `boomtown_env.pcf` / `wilds_env.pcf` 等库。主要雾纹理为 `particle/smoke/fog_thin_perlin.vtf`，不是需要猜测的替代贴图；已检查实际 VTF、材质属性及原始 DMX 模块。

## 修复 1：形状初始化丢失随机位置偏移

`cenotes_upward_fog` 同时包含：

- `Position Within Sphere Random`，距离默认 0；
- `Position Modify Offset Random`，局部范围 `(-250,-400,-100)` 到 `(250,400,300)`；
- 初始上升速度 200–300，发射率 6/s，生命周期 3 秒。

旧代码优先选择球形发射器，只给发射器设置偏移范围的中心 `(0,0,100)`。球体半径为 0，因此整片 500×800×400 单位的出生体积被压成一个点，容易形成过度集中、叠加过亮的雾柱。

现在保留球/环的形状采样，并独立应用每个粒子的随机偏移。局部偏移随实体旋转，世界偏移保持世界轴向。现有 box 发射器不重复应用随机偏移；已有位置 warp 处理保持在随机偏移之前。

生产创建流程抽出 ConfigureSimulation，供实际系统创建与原生行为回归共用。

## 修复 2：SpriteCard 近距离大雾片没有渐隐

实际材质明确包含 `$startfadesize` / `$endfadesize`：

| 材质 | 开始 / 结束 |
| --- | --- |
| fog_thin_perlin_db | 0.9 / 1.3 |
| fog_thin_perlin_db_nearcull2 | 0.9 / 2.0 |
| fog_thin_perlin_db128_nearcull | 0.35 / 0.65 |
| fog_thin_perlin_add_db128_nearcull | 0.3 / 1.3 |

之前这些值没有传入粒子运行路径。现在对明确配置此参数的材质，按粒子半径与主相机距离计算渐隐，并同时衰减 RGB 和 alpha。公式参考 Valve 官方 SpriteCard 顶点程序中 radius 与 distance×fade-size 的比较，没有擅自引入 FOV 换算。

- [Valve SpriteCard 参数与常量设置](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/materialsystem/stdshaders/spritecard.cpp)
- [Valve SpriteCard 顶点渐隐实现](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/materialsystem/stdshaders/spritecard_vsxx.fxc)

这是主相机上的挂载适配；镜面、额外视图等多视角情况下按每个渲染视图计算渐隐，仍需后续独立验证。

## 验证

验证记录在 `.tmp/tf2-boomtown-smoke-20261108/`：

- `fx-entities.json`：本地图实际粒子实体。
- `raw-definitions.json` / `parsed-definitions.json`：原始 DMX 与当前转换结果。
- `*.vmt` / `fog_thin_perlin.vtf`：用户本地提取的数据，不提交或分发。
- `native-tests.log` / `sbox-build.log` / `verification.json`：最终验证记录。

回归覆盖球形发射器叠加随机偏移的局部/世界两种空间、保留环形半径、真实零售 Boomtown 雾的出生范围，以及镜头远近改变后 RGB/alpha 渐隐与恢复。实际数据测试由 `TF2_SMOKE_AUDIT_ROOT` 指定本地提取目录，CI 不需要持有游戏资源。

最终结果：SboxBuild Developer managed build 成功；原生 Integration 普通测试 49 项及分配测试 1 项全部通过；粒子实体回归 8 项通过。修改的模块文件与 Integration 测试文件的完整格式检查通过，diff 检查通过。

## 部署与视觉边界

用户当前游戏日志记录的模块 MVID 为 `0b94dbc9-5cc9-4f46-86f2-900d21b0de67`，仍是此前部署版本。本次构建与原生测试使用隔离目录；主部署 DLL 未替换，运行中的游戏未重启。

本次没有在用户当前相机位置取得修复后整图画面。已确认并修复资源转换和粒子运行行为，不能据此声称截图中的全部水雾已经与原版一致。PCF 的其他未支持操作与完整材质/光照对照仍可能影响最终外观。
