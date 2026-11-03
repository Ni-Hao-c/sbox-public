# Titanfall 2 挂载环境变量

挂载只保留 28 个 `SBOX_TITANFALL2_*` 环境变量。其中 25 个定义在 [Titanfall2StreamingSettings.cs](Titanfall2StreamingSettings.cs)，另外 3 个分别在 [Titanfall2Log.cs](Titanfall2Log.cs)、[Resource/ProcessedCache.cs](Resource/ProcessedCache.cs) 和 [Resource/ModelExporter.cs](Resource/ModelExporter.cs)。流式加载预算、LOD 距离、粒子上限等调优值都是常量，写在使用它们的类里。

## 怎么设置

- 在启动 `sbox.exe` 之前设置环境变量，再从同一个窗口启动游戏。PowerShell 示例：`$env:SBOX_TITANFALL2_PROP_SHADOWS = "1"`。
- 开关：`1`、`true`、`yes`、`on` 为开，不区分大小写。不设置或留空时用默认值，其它非空值都算关。
- 数值按英文小数点解析。超出范围的值会夹到边界，解析失败时用默认值。
- `Titanfall2StreamingSettings` 中的开关和数值在首次初始化时读取，修改后要重启游戏。缓存目录和模型导出目录在各自的使用路径中读取；为便于复现，仍建议启动前统一设置。

下表省略了 `SBOX_TITANFALL2_` 前缀。

## 缓存与贴图

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `PROCESSED_CACHE` | 开 | 开/关 | 缓存模型、贴图、材质描述和道具淡出距离；不缓存地图几何或光照贴图 |
| `CACHE_MB` | 2048 | 128–8192 | 处理缓存的磁盘预算（MB） |
| `CACHE_PATH` | `%LOCALAPPDATA%\sbox\mount-cache\titanfall2\processed` | 路径 | 处理缓存目录 |
| `TEXTURE_MAX` | 1024 | 64–2048 | 贴图最大边长。低显存可设 512 |
| `LIGHTMAP_16BIT` | 开 | 开/关 | 把 SkyB（RGB565）与 RealTimeB（BGRA4444）两张最大的光照片通道改成 16 位：光照片 17→13 B/px（angel_city 70→54 MiB、sewers1 108→83 MiB），世界 shader 每像素那 2 次采样带宽各减半。实测精度：RealTimeB 93.7–96.7% 的 texel 是二值、误差 ≈0.2%，SkyB 天空可见性误差 ≈1%。设 0 回到 RGBA8888 旧路径 |
| `LIGHTMAP_MIPS` | 关（120 档为开） | 开/关 | 给 SkyA / SkyB / RealTimeB 生成 L0–L2 mip 链，生成前先按几何覆盖率把图集 chart 向外 dilate 4 步，避免 mip 把 chart 外的空洞混进来。省的是掠射角/远处的采样带宽，代价是远景 chart 边缘略糊，且显存回到 16 位化之前的水平（angel_city 54→70 MiB） |

## 静态 prop

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `PROP_RENDER_DISTANCE` | 8192 | 0–65536 | 逐实例距离剔除半径，大型地标不受影响。0 关闭距离剔除 |
| `PROP_AUTHORED_FADE_SCALE` | 1 | 0.25–16 | 乘到地图为每个 prop 设定的淡出距离上 |
| `PROP_SHADOWS` | 开 | 开/关 | 让半径不小于 48 的静态 prop 投射动态阴影 |

## 帧率与提交粒度

这 4 个值控制每帧要提交多少渲染对象、多少三角形进阴影和深度通道。默认值等于"功能打开但不激进"；想冲更高帧率再往下调。

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `FPS_PRESET` | 0 | 0–240 | 一档式帧率预设。0 与 60 等于下表默认值；120 改为阴影距离 1024、级联 1、LOD 缩放 0.5、cell 4096、批次簇 8、绘制距离 6144，并关掉接触阴影与 Godray。任何一个变量显式设置时都优先于预设 |
| `PROP_SHADOW_DISTANCE` | 0 | 0–65536 | 距相机多远之外的 prop 批次不再投射方向光阴影。0 = 关闭门控（默认，批次内任一大型实例就让整批投影，与旧行为一致）。开启后标志变更被限制在每帧 32 次以内，避免走动时整片批次在 shadow pass 进出造成卡顿 |
| `PROP_LOD_SCALE` | 1 | 0.1–4 | 乘到静态 prop 的 LOD 切换距离（1536/4096/8192）上。小于 1 更早切到低模，减少前向与深度通道三角形 |
| `BATCH_CLUSTER` | 4 | 1/2/4/8/16 | 多少个 prop cell（512 单位）合成一个实例批次。越大提交对象越少，但 PVS 和阴影按批次聚合，粒度变粗 |
| `WORLD_CELL_SIZE` | 2048 | 512–8192 | 世界几何的空间 cell 边长。越大 fragment/draw 越少，视锥剔除越粗 |

冲 60/120 fps 的推荐组合见 [ARCHITECTURE.md](ARCHITECTURE.md) 第 13 节。比较性能时使用同一进程、固定机位、同一相机，并记录 `draws`/`triangles`/`objects`。

## 光照与画面

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `BSP_LIGHTMAPS` | 开 | 开/关 | 使用地图烘焙的 lightmap、光照探针和本地光。关掉后改用 s&box 原生光照，只用于排查不支持的地图 |
| `MAP_SHADOWS` | 开 | 开/关 | 让导入的世界几何参与方向光级联阴影（CSM） |
| `SHADOW_CASCADES` | 2 | 1–3 | 方向光级联数。每个级联都会把所有投影体再画一遍，所以它直接乘在整条 shadow pass 上：1 = 减半，3 = 适合很长的阴影距离。`MAP_SHADOWS=0` 时强制为 1；120 fps 预设用 1 |
| `CONTACT_SHADOWS` | 开 | 开/关 | s&box 屏幕空间接触阴影 |
| `EXPOSURE_EV` | 0 | -5–5 | 在零售曝光标定上再加的曝光补偿（档） |
| `GODRAYS` | 开 | 开/关 | 显示 godray 光束面片。它们随相机距离淡入，移动时可能闪烁；设为 0 可关闭 |

## 声音

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `MAP_AUDIO` | 开 | 开/关 | 地图环境音发声点和 soundscape |
| `AUDIO_VOLUME` | 0.7 | 0.05–2 | 地图环境音音量 |

## 导航网格

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `NAVMESH_ON_DEMAND` | 开 | 开/关 | 宿主场景已开启 NavMesh 时，跳过加载阶段的整图生成，只用带标签的世界碰撞，按需生成玩家和 NavMeshAgent 附近的 tile。关掉后恢复引擎的整图生成，在 angel_city 这类大图上要多等约 1.5–2 分钟 |
| `NAVMESH_RADIUS` | 2048 | 512–65536 | 按需生成的半径 |
| `DEFERRED_NAVMESH` | 关 | 开/关 | 宿主没有开启 NavMesh 时，由挂载为完整地图场景开启，并在加载后生成 |
| `NAVMESH_STATIC_PROPS` | 关 | 开/关 | 静态 prop 的碰撞也参与导航网格 |

## 诊断与导出

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `VERBOSE_LOG` | 关 | 开/关 | 输出详细加载日志、全部警告和超出预算的耗时记录（spike） |
| `EXPORT_ROOT` | 无 | 路径 | `titanfall2_export_model` 命令的默认导出目录 |
