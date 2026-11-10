# sp_boomtown_start 画面一致性检查（2026-11-07）

## 范围与现象

用户提供了原版 Titanfall 2 与 s&box 的管道走廊、瀑布岩壁截图。主要差异是整体冷暖和明暗氛围，以及岩面颜色和细节表现。两组参考图相机位置不同，不能据此进行逐像素误差统计。

本轮仅修改 Titanfall 2 挂载模块、挂载 shader 及验证文件，没有修改引擎生产代码。此前性能工作已经收尾，本轮没有重新加入运行期 A/B 开关。

## 原版数据核对

- 地图：`sp_boomtown_start`。
- 环境分区只有一个启用的全局颜色校正实体，路径为 `materials/correction/mp_wargames.raw`，权重 1。虽然名称带有另一张地图，它确实是该地图的原始实体数据，未擅自替换。
- 已检查的岩石材质 `c_albedoTint` 都为 `(1,1,1)`。
- 分层岩石 `WORLD/STONE/ROCK_HORIZONTAL_TILE_01_TILE_02_BM` 使用 t22 的混合遮罩、t23 的第二层底色与 ramp 10。原始 DXBC 指令与当前混合公式一致：`saturate((ramp + 1) * vertexAlpha + ramp * (mask - 1))`。没有反转混合或额外染棕。
- 主岩石 `models/rocks/rock_horizontal_03` 使用 `uberDetDtnUV2000011Samp222222_skn`，在 BSP 中涉及 195,810 个顶点。原版 t15 为 detail normal，采样 UV1，缩放 12。另有 jagged brown rock 与 harmony flatwall 使用相同类型的 detail normal。
- 原版 detail normal 指令在解码主法线后，将细节法线的 XY 斜率相加，再构造并归一化世界法线。

## 修复

### 岩石细节法线

原导入器已能反射出 DetailNormal，却在 `UsesTexture` 中明确跳过了这个用途。现已补齐材质绑定、UV 通道、斜率编码标识、共享表面采样和世界材质参数复制。

普通材质、GPU 道具材质表和世界材质表使用相同表面逻辑。细节 XY 斜率在切线空间中与主法线相加；没有主法线时也能使用细节法线。

生成器同时修复了材质 buffer 属性宏覆盖丢失的问题，并显式补齐结构末尾的 16 字节对齐。现在有 41 个 float4、17 个纹理索引、12 字节 padding，CPU/HLSL stride 均为 **736 字节**。旧 DLL 的 704 字节布局不能与这些新 shader 混用。

### 原生色调映射与 retail display 的叠加

在用户实际打开的 `facepunch.sandbox` 中，修复前相机原生 `Tonemapping.Mode=AgX`，并依次存在 `Tonemapping`、`Titanfall2Display` 和 `Titanfall2ColorCorrection` 层。相机最终的 native auto-exposure 为 false，但 AgX 曲线仍执行。不同运行时类型的后处理效果不会相互覆盖。

继续读取宿主 `DefaultTonemapping.OnUpdate` 后，确认它每帧会根据是否存在全局原生 tonemapping 来管理默认相机效果。只在挂载中临时禁用默认相机效果会与宿主逻辑发生冲突，因此该方案没有进入最终实现。

最终由地图自己的无限后处理 volume 创建普通原生 `Tonemapping`，设置 `Mode=Linear`、`AutoExposureEnabled=false`，再创建 retail display。此模式采用现有 Source 1 挂载的原生基线做法；宿主的默认相机 AgX 会自行让位。没有直接改写宿主的模式、曝光参数或其他后处理效果。显式保留启用的游戏相机 tonemapping 仍按引擎自身规则覆盖地图基线。

在旧 DLL 的真实游戏里临时加入该基线后，已读到：默认相机 AgX 组件 disabled，地图 Linear 基线 enabled，相机 auto-exposure false；临时组件随后移除。最终源码采用相同的基线配置。

## 验证与边界

- 隔离 Developer 构建成功；shader 已单独编译。主目录与隔离目录的有效 `.shader` / `.hlsl` 源码哈希一致。
- 细节法线 GPU 读回：普通/材质表 × 有/无主法线，4 个用例通过。使用固定 UV2 采样点与第二行材质数据，验证 UV 选择、736 字节 stride 及原版斜率相加结果。
- 同轮先前回归：Integration 11/11，Engine 2/2；挂载纯托管用例 27/27。
- 最终 native 基线验证覆盖实际工厂参数与销毁、地图基线参与原生效果分组、显式相机覆盖；与 4 个细节法线读回用例在同一次 Integration 进程中合计 **7/7 通过**。格式检查与相关文件 `git diff --check` 通过。
- 固定位置、固定 retail exposure 的临时采样证明原生曲线改变实际像素。场景粒子仍在运动，且没有对齐原版相机，因此不将其作为已恢复原版色彩或氛围的结论。
- 未导入的原版脚本曝光控制、局部颜色校正、其他宿主后处理，以及光照/雾/材质能量范围仍可能影响最终画面。本轮没有通过任意调曝光、强改岩石颜色或替换地图 LUT 来掩盖这些差异。

## 证据

本机诊断目录：`.tmp/tf2-boomtown-visual-20261107/`。其中保留原始材质/环境数据、原 shader 反汇编、构建与测试日志、部署前截图及玩家视角、部署文件清单与备份。该临时目录不是正式功能依赖。

## 部署与重启验收

用户明确授权“部署 并重启”后，正常退出原游戏，部署到当前实际使用的 `D:\sbox\sbox-public\game\mount\titanfall2`，成套替换 DLL 和 **29 个有效 shader**。未部署已经退役的参考/A-B shader；引擎文件没有替换。部署前备份在诊断目录的 `deployment-backup/`。

- 新 DLL SHA256：`91F02944435DAFC36702DB72CD4A52EDD92669B942DAE4DFF789F6CD8E900FBD`。
- 实际加载的挂载 MVID：`c1b51d1d-c881-4232-b1be-f41277f1eff8`，与部署文件一致。
- 实际引擎 MVID：`f8177a3c-2c38-46a5-9042-923d08a71894`，与部署前一致。
- 重启进程 PID 7004，`facepunch.sandbox` / `sp_boomtown_start`，`IsLoading=false`；PreparationComplete 与 ResidentCollisionsReady 均为 true。
- 已恢复部署前的玩家位置 `(12160.169, -8761.275, 9728.223)` 与视角。之后用户继续移动，以 `final-live-state.json` 的最终实时状态为准。
- 宿主相机的原生 AgX 组件 disabled；地图原生 Linear 基线 enabled；相机 auto-exposure false，retail display 和原始地图 LUT 层均存在。
- 运行期材质结构 stride 736；世界材质表 90 个源材质，其中 4 个读到有效 detail normal 配置和实际纹理（512、512、256、1024 宽）。
- 此前已决定默认开启的世界材质表随当前源码生效，实际 `WorldMaterialTable=true`；其他质量配置仍使用此前设置。
- 临时测试对象为 0。已保存重启后真实渲染截图 `after-deployment.png`，未发现挂载 shader/材质表布局错误。既有缺失武器 AO 资源、缺失 PrefabSpawner 等宿主日志不属于本轮修复。

本轮确认了修复路径的真实加载与运行；尚未完成与原版相机严格对齐的全场景视觉验收，不将当前截图描述为已经完全还原原版氛围。

随后用户反馈岩石颜色仍未改变，追加调查确认 detail color 的 Linear/sRGB 采样错误。后续 shader 修复、像素验证和第二次部署记录见 [岩石颜色修复记录](TITANFALL2_BOOMTOWN_ROCK_COLOUR_2026-11-07.zh-CN.md)。本文件的首次部署记录予以保留。
