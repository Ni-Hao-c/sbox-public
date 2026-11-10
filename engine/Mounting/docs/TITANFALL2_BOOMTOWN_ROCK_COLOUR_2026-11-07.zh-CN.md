# sp_boomtown_start 岩石颜色修复（2026-11-07）

后续排查找到世界 SkyB 上传格式和 mip 顺序的独立错误，见 [2026-11-08 光照格式修复](TITANFALL2_BOOMTOWN_LIGHTMAP_FORMAT_2026-11-08.zh-CN.md)。本页保留 detail color 修复的证据，不能据本页单独判定用户反馈的整面岩壁反差已修复。

## 问题

用户在上一轮部署后指出岩石颜色没有改变。上一轮修复了 detail normal 缺失和宿主 AgX 与 retail display 叠加，但没有验证细节颜色调制是否正确；不能据这些修复宣称岩石配色已还原。

本轮直接检查运行中岩壁。相机射线命中静态道具 `models/rocks/rock_horizontal_01.mdl`，其 GPU 材质源包含 `models/rocks/rock_horizontal_03`。该材质启用了 detail color 与 detail normal，底色 tint 为 `(1,1,1)`。

## 已确认的原因

当前地图使用的两张细节颜色图：

- `models/rocks/rock_horizontal_01_detail_col`
- `models/rocks/rock_jagged_granite_detail_01_col`

其 RPAK 格式编号都是 0，即 `BC1_UNORM`；主要 albedo 图的格式编号为 1，即 `BC1_UNORM_SRGB`。编号映射也与 [LegionPlus 的原始格式表](https://github.com/r-ex/LegionPlus/blob/main/Legion/rtech.h#L35) 一致。

挂载的 default、cutout 和 world-lightmapped shader 却给 detail color 统一配置了 `SrgbRead(true)`，材质表生成器相应使用 `Bindless::GetTexture2DSrgb`。这会将灰值 128 从约 0.502 解码成约 0.216，再乘 2 得到约 0.432，把有细节贴图的岩石压暗，并放大它与邻接灰色石壁的反差。

原版 `uberDet` 的 DXBC 明确执行 `2 * saturate(detail - 0.5 / 255)`。按原始线性字节读取时，灰值 128 恰好对应倍率 1。

## 排除的候选

虽然全局挂载当前选中的纹理来自 `mp_colony02(03).rpak`，本轮对比了它与 `sp_boomtown_start.rpak` 中 4 张相关贴图的解码 BC 数据，SHA256 均一致。没有证据表明该岩壁的差异来自拿错这 4 张贴图的版本。

两层岩石的遮罩混合公式此前已经与原版指令核对。没有反转遮罩、修改顶点颜色或将材质强行染棕，也没有更改地图 LUT、光照或雾参数。

## 实现

- default、cutout 与 world-lightmapped 的 detail 输入改为 Linear。
- 新增小型共享 helper `titanfall2_detail.hlsl`，retail 路径按原始字节域执行半字节偏移与乘 2。
- 旧 VMT 路径在 shader 中显式执行此前的 sRGB 解码，以保留其原有表现。
- 重新生成材质表，detail 改用 `Bindless::GetTexture2D`；C# 字段与结构 stride **736 字节均未改变**。
- 未修改引擎生产代码。

## 像素验证

先用旧编译 shader 运行新 GPU 读回测试，4/4 失败，均复现中灰错误压暗：

| 路径 | 不使用细节图时的 RGB | 使用灰值 128 后的 RGB |
| --- | --- | --- |
| default | 0.1200, 0.2400, 0.3599 | 0.0518, 0.1036, 0.1553 |
| cutout | 0.1200, 0.2400, 0.3599 | 0.0518, 0.1036, 0.1553 |
| world ordinary | 0.1218, 0.2437, 0.3655 | 0.0526, 0.1052, 0.1577 |
| world material table | 0.1218, 0.2437, 0.3655 | 0.0526, 0.1052, 0.1577 |

修复后同一 fixture 的 4 个 retail 用例通过：中灰保留底色，彩色细节图 `(192,128,64)` 按原版公式调制三个通道，且有正控制证明图像确实受 detail 影响。新增的 legacy VMT 用例也通过。

再加上世界材质表的片段保留、颜色与 depth normals 回归，共 **8/8 Integration 用例通过**。Developer 构建、`build-shaders` 和新增 C# 格式验证均通过。

## 运行验收与边界

证据目录：`.tmp/tf2-rock-colour-20261107/`。包含原贴图与格式元数据、包版本哈希对比、旧版失败与新版通过日志、部署前后固定曝光截图、原文件备份和部署清单。

受控截图采用相同保存视角与 retail forced exposure 1；临时曝光设置会在采样结束后恢复。场景的瀑布/蒸汽仍在运动，重启也会重建玩家物品，因此这些截图不是全帧逐像素差值测试。原版色温、局部光照、雾与粒子的全部一致性仍需分别验收。

已沿用用户本轮“部署 并重启”的授权完成 shader 成套部署并恢复地图：

- 进程 PID 29072；地图 `mount://titanfall2/maps/sp_boomtown_start.bsp.scene`。
- 部署 29 个有效 shader，启动后逐个哈希核对无差异。
- 新 default shader SHA256：`C1607B87CA1C6CBE86E0B11D2B490661342A2362BBECC8FC5A7B077752BCD92F`。
- 本次无生产 C# 语义/ABI 改动，构建产物 DLL 与主目录 DLL 哈希一致，均为 `91F02944435DAFC36702DB72CD4A52EDD92669B942DAE4DFF789F6CD8E900FBD`。运行中挂载 MVID 仍为 `c1b51d1d-c881-4232-b1be-f41277f1eff8`。
- 引擎 MVID 仍为 `f8177a3c-2c38-46a5-9042-923d08a71894`。
- 保存的玩家位置 `(12027.547,-8760.884,9728.224)` 与 EyeAngles 已恢复。对照截图旋转和 FOV 相同，角色接地使相机 Z 相差约 0.063 单位。
- forced exposure 在受控截图后已恢复到原值 0；原生相机 AgX 与原生 auto-exposure 继续关闭，沿用上一轮修复的地图 Linear 基线与 retail display。
- 临时测试对象为 0；游戏保持打开。

本轮修正的是 detail color 采样导致的错误压暗，灰白石壁材质没有被统一改色；不将其描述为整面岩壁配色与原版已经完全一致。
