# Titanfall 2 挂载环境变量

挂载只保留 20 个 `SBOX_TITANFALL2_*` 环境变量。其中 17 个定义在 [Titanfall2StreamingSettings.cs](Titanfall2StreamingSettings.cs)，另外 3 个分别在 [Titanfall2Log.cs](Titanfall2Log.cs)、[Resource/ProcessedCache.cs](Resource/ProcessedCache.cs) 和 [Resource/ModelExporter.cs](Resource/ModelExporter.cs)。流式加载预算、LOD 距离、粒子上限等调优值都是常量，写在使用它们的类里。

## 怎么设置

- 在启动 `sbox.exe` 之前设置环境变量，再从同一个窗口启动游戏。PowerShell 示例：`$env:SBOX_TITANFALL2_PROP_SHADOWS = "1"`。
- 开关：`1`、`true`、`yes`、`on` 为开，不区分大小写。不设置或留空时用默认值，其它非空值都算关。
- 数值按英文小数点解析。超出范围的值会夹到边界，解析失败时用默认值。
- 所有变量只在挂载第一次读取时生效，改了要重启游戏。

下表省略了 `SBOX_TITANFALL2_` 前缀。

## 缓存与贴图

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `PROCESSED_CACHE` | 开 | 开/关 | 缓存地图、模型、贴图和材质的处理结果 |
| `CACHE_MB` | 2048 | 128–8192 | 处理缓存的磁盘预算（MB） |
| `CACHE_PATH` | `%LOCALAPPDATA%\sbox\mount-cache\titanfall2\processed` | 路径 | 处理缓存目录 |
| `TEXTURE_MAX` | 1024 | 64–2048 | 贴图最大边长。低显存可设 512 |

## 静态 prop

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `PROP_RENDER_DISTANCE` | 8192 | 0–65536 | 逐实例距离剔除半径，大型地标不受影响。0 关闭距离剔除 |
| `PROP_AUTHORED_FADE_SCALE` | 1 | 0.25–16 | 乘到地图为每个 prop 设定的淡出距离上 |
| `PROP_SHADOWS` | 关 | 开/关 | 让半径不小于 48 的静态 prop 投射动态阴影 |

## 光照与画面

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `BSP_LIGHTMAPS` | 开 | 开/关 | 使用地图烘焙的 lightmap、光照探针和本地光。关掉后改用 s&box 原生光照，只用于排查不支持的地图 |
| `MAP_SHADOWS` | 关 | 开/关 | 让导入的世界几何参与方向光级联阴影（CSM） |
| `CONTACT_SHADOWS` | 开 | 开/关 | s&box 屏幕空间接触阴影 |
| `EXPOSURE_EV` | 0 | -5–5 | 在零售曝光标定上再加的曝光补偿（档） |
| `GODRAYS` | 关 | 开/关 | 显示 godray 光束面片。它们是随相机距离淡入的叠加面片，移动时会闪，所以默认隐藏 |

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
