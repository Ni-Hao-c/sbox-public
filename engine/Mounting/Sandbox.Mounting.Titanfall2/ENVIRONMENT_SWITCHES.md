# Titanfall 2 挂载环境变量开关

日期：2026-10-31。按源码逐条整理，共 **115** 个 `SBOX_TITANFALL2_*` 环境变量。
其中 110 个在 [Titanfall2StreamingSettings.cs](Titanfall2StreamingSettings.cs)，另外 5 个分散在
[Titanfall2Log.cs](Titanfall2Log.cs)、[GameMount.cs](GameMount.cs)、[Resource/ProcessedCache.cs](Resource/ProcessedCache.cs)、
[Resource/ModelExporter.cs](Resource/ModelExporter.cs)。

## 怎么设置

- 在启动 `sbox.exe` 之前设置环境变量，例如 PowerShell：`$env:SBOX_TITANFALL2_NAVMESH_ON_DEMAND = "0"`，再从同一个窗口启动游戏。
- 开关值：`1` / `true` / `yes` / `on` 为开（不区分大小写）。不设置或留空用默认值。**其它任何非空值都算关**。
- 数值：按英文小数点解析，超出范围会被夹到范围边界；解析失败用默认值。
- 绝大多数开关在挂载第一次读取设置时生效，改了要重启游戏。
  只有标 ⚡ 的 8 个能在运行中由代码改写（用于同一位置的 A/B 对比），环境变量本身仍只在启动时读一次。
- `SBOX_TITANFALL2_SELFTEST` 只认 `1`。`SBOX_TITANFALL2_PROP_LOD_MODE` 只认 `native` / `legacy`，写错保持默认。

## 1. 缓存与读取（6）

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `PROCESSED_CACHE` | 开 | 开/关 | 地图、模型、贴图、材质的处理结果缓存 |
| `CACHE_MB` | 2048 | 128–8192 | 处理缓存的磁盘预算（MB） |
| `CACHE_PATH` | `%LOCALAPPDATA%\sbox\mount-cache\titanfall2\processed` | 路径 | 处理缓存目录 |
| `RPAK_BUFFER_MB` | 512 | 64–4096 | RPAK 解压缓冲的内存上限（MB） |
| `TEXTURE_MAX` | 1024 | 64–2048 | 贴图最大边长，低显存可设 512 |
| `MATERIAL_PREFETCH` | 4 | 0–8 | 贴图 CPU 预处理提前量 |

## 2. 世界几何（6）

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `WORLD_AGGREGATE` | 开 | 开/关 | 按材质聚合世界网格，减少提交对象 |
| `PARALLEL_AGGREGATE` | 开 | 开/关 | 聚合数据在后台线程拼接 |
| `WORLD_RENDER_CELL_SIZE` | 2048 | 512–8192 | 世界渲染分块大小 |
| `WORLD_MATERIALS_PER_FRAME` | 1 | 1–16 | 每帧处理的世界材质数 |
| `WORLD_MATERIAL_FRAME_BUDGET_MS` | 2 | 0.25–20 | 世界材质每帧时间预算 |
| `ALL_BODYGROUP_CHOICES` | 关 | 开/关 | 导入每个 bodygroup 的所有选项（会叠画，仅调试） |

## 3. 静态道具：加载、剔除、提交（25）

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `PROP_CELL_SIZE` | 512 | 256–8192 | 道具空间分格大小 |
| `PROP_LOAD_RADIUS` | 1024 | 512–32768 | 优先加载半径 |
| `PROP_BATCH_CLUSTER_CELLS` ⚡ | 4 | 1–8 | 每 N×N 个格合成一个实例批次 |
| `PROP_INSTANCE_FRUSTUM` | 关 | 开/关 | 逐实例视锥剔除 |
| `PROP_RENDER_DISTANCE` ⚡ | 8192 | 0–65536 | 道具绘制距离，0 = 不按距离剔除 |
| `PROP_RENDER_DISTANCE_DRYRUN` ⚡ | 关 | 开/关 | 只统计会被距离剔除的数量，不真的隐藏 |
| `PROP_RENDER_DISTANCE_HYSTERESIS` | 512 | 0–16384 | 距离剔除的回滞带 |
| `PROP_LANDMARK_RADIUS` | 256 | 0–8192 | 半径大于此值的大型道具不做距离剔除 |
| `PROP_AUTHORED_FADE` ⚡ | 开 | 开/关 | 使用原版每个道具的淡出距离 |
| `PROP_AUTHORED_FADE_SCALE` ⚡ | 1 | 0.25–16 | 原版淡出距离倍率（2 = 两倍远） |
| `PROP_PVS` | 开 | 开/关 | 用 BSP 可见性单元剔除道具 |
| `PROP_PVS_PORTAL_DEPTH` ⚡ | 16 | 1–64 | PVS 门户遍历深度 |
| `PROP_BATCHABLE` | 开 | 开/关 | 允许引擎合并道具提交 |
| `PROP_IN_CUBEMAP` | 关 | 开/关 | 道具参与 cubemap 渲染 |
| `PROP_NEEDS_ENVMAP` | 关 | 开/关 | 道具参与环境贴图渲染 |
| `PROP_RUNTIME_CELLS_PER_FRAME` | 16 | 1–256 | 运行时每帧更新的道具格数 |
| `PROP_RUNTIME_CELL_BUDGET_MS` | 1.5 | 0.25–10 | 运行时格更新的每帧时间预算 |
| `PROPS_PER_FRAME` | 16 | 1–256 | 每帧加载的道具数 |
| `PROP_FRAME_BUDGET_MS` | 3 | 0.5–20 | 道具加载每帧时间预算 |
| `PROP_PRELOAD_PER_YIELD` | 512 | 1–2048 | 加载画面每次让出前处理的道具数 |
| `PROP_PRELOAD_BUDGET_MS` | 35 | 1–100 | 加载画面每次让出前的时间预算 |
| `MODEL_FAILURE_RETRY_SECONDS` | 30 | 0.5–900 | 模型加载失败后多久再重试 |
| `AGGREGATE_STATIC_PROPS` | 关 | 开/关 | 实验：把少量无 LOD 道具烘进世界几何 |
| `AGGREGATE_PROP_MAX_COPIES` | 4 | 1–16 | 上一项：同一模型最多几份才烘入 |
| `AGGREGATE_PROP_VERTICES` | 300000 | 0–2000000 | 上一项：烘入的顶点预算 |

## 4. 道具 LOD（11）

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `PROP_LODS` ⚡ | 开 | 开/关 | 使用原版 LOD；关 = 全部用 LOD0 |
| `PROP_LOD_MODE` ⚡ | `native` | `native` / `legacy` | native：一个模型含全部 LOD；legacy：按距离换模型 |
| `PROP_LOD_NOMINAL_RADIUS` | 0 | 0–4096 | 0 = 每个模型用自己的半径；>0 统一半径（A/B 用） |
| `PROP_LOD_FOCAL_PX` | 960 | 64–8192 | 计算 LOD 屏幕尺寸用的焦距（像素） |
| `PROP_LOD1_DISTANCE` | 1536 | 256–65536 | 切到 LOD1 的距离 |
| `PROP_LOD2_DISTANCE` | 4096 | 512–65536 | 切到 LOD2 的距离 |
| `PROP_LOD3_DISTANCE` | 8192 | 1024–131072 | 切到 LOD3 的距离 |
| `PROP_LOD_HYSTERESIS` | 1024 | 0–4096 | LOD 切换回滞 |
| `PROP_LOD_SWAPS_PER_FRAME` | 1 | 1–8 | legacy 模式每帧换模型次数 |
| `PROP_LOD_SWAP_BUDGET_MS` | 1.5 | 0.25–10 | legacy 模式换模型每帧时间预算 |
| `PROP_LOD_SWAP_QUEUE_LIMIT` | 256 | 0–65536 | 换模型队列上限，0 = 不限 |

## 5. 光照、阴影、调色（16）

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `BSP_LIGHTMAPS` | 开 | 开/关 | 使用地图烘焙光照（线性 HDR 路径） |
| `BAKED_TONEMAPPING` | 开 | 开/关 | 原版显示曲线与曝光 |
| `EXPOSURE_EV` | 0 | -5–5 | 曝光补偿（档） |
| `BAKED_COLOR_CORRECTION` | 开 | 开/关 | 地图自带的调色 LUT |
| `AMBIENT_SCALE` | 1.35 | 0.1–4 | light_environment 环境光倍率 |
| `SKY_INDIRECT` | 开 | 开/关 | 天空提供的间接光 |
| `CONTACT_SHADOWS` | 开 | 开/关 | 屏幕空间接触阴影 |
| `MAP_SHADOWS` | 关 | 开/关 | 世界几何参与方向光动态阴影 |
| `PROP_SHADOWS` | 关 | 开/关 | 道具投射动态阴影 |
| `PROP_SHADOW_DISTANCE` | 0 | 0–32768 | 道具阴影距离，0 = 不限 |
| `PROP_SHADOW_MIN_RADIUS` | 48 | 0–2048 | 小于此半径的道具不投影 |
| `WORLD_SHADOW_ENABLE_DISTANCE` | 0 | 0–32768 | 世界块阴影开启距离，0 = 不限 |
| `WORLD_SHADOW_DISABLE_DISTANCE` | 0 | 0–65536 | 世界块阴影关闭距离 |
| `COLOR_SATURATION` | 1.05 | 0–2 | **无效**：已声明，代码中没有读取 |
| `COLOR_BRIGHTNESS` | 0.98 | 0–2 | **无效**：已声明，代码中没有读取 |
| `COLOR_CONTRAST` | 1.08 | 0–2 | **无效**：已声明，代码中没有读取 |

## 6. 天空、雾、大气（11）

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `SKYBOX_MODE` | 1 | 0–2 | 0 = 只用 2D 天空；1 = 引擎 3D 天空盒；2 = 跟随相机的穹顶（实验） |
| `SKYBOX_SCALE` | 0 | 0–4096 | 3D 天空盒缩放，0 = 用地图数据 |
| `SKYBOX_MODEL_SCALE` | 1 | 1–1000 | 远景模型缩放 |
| `SKYBOX_TARGET_DISTANCE` | 25000 | 1000–1000000 | 自动缩放时远景的目标视距 |
| `VISTA_MAX_LIGHTING` | 1 | 0.05–65504 | 远景 HDR 亮度上限（之前修 sp_beacon 光晕时加的） |
| `2D_SKY` | 关 | 开/关 | 远景后面再垫一层 2D 天空 |
| `SKY_MATERIAL` | `mount://titanfall2/skies/skycube.vmat` | 路径 | 2D 天空材质 |
| `MAP_FOG` | 开 | 开/关 | 地图雾 |
| `ATMOSPHERIC_CARD_EFFECTS` | 开 | 开/关 | 大气卡片特效（云层、薄雾） |
| `ATMOSPHERE_OPACITY` | 1 | 0–1 | 大气特效不透明度倍率 |
| `GODRAYS` | 关 | 开/关 | 体积光卡片（移动时容易闪） |

## 7. 粒子（15）

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `PARTICLE_LIBRARIES_PER_FRAME` | 8 | 1–32 | 每帧解析的 PCF 库数 |
| `PARTICLE_LIBRARY_BUDGET_MS` | 2 | 0.25–20 | PCF 解析每帧时间预算 |
| `PARTICLE_INSTANCES_PER_FRAME` | 4 | 1–64 | 每帧创建的地图粒子数 |
| `PARTICLE_INSTANCE_BUDGET_MS` | 2 | 0.25–20 | 粒子创建每帧时间预算 |
| `PARTICLE_ACTIVATION_RADIUS` | 4096 | 256–65536 | 地图粒子激活半径 |
| `PARTICLE_MAX_PER_SYSTEM` | 256 | 1–4096 | 每个系统最多粒子数 |
| `PARTICLE_DRAW_DISTANCE` | 开 | 开/关 | 遵守 PCF 的最大绘制距离 |
| `PARTICLE_SLEEP` | 开 | 开/关 | 看不见一段时间后挂起 |
| `PARTICLE_WAKE_WARMUP` | 1 | 0–8 | 唤醒时预跑的秒数 |
| `PARTICLE_WAKE_WARMUP_BUDGET_MS` | 1.5 | 0–20 | 唤醒预跑的时间预算 |
| `PARTICLE_MAX_LIGHTS` | 4 | 0–32 | 每个系统最多灯光数 |
| `PARTICLE_MAX_RADIUS` | 256 | 8–4096 | 粒子半径上限 |
| `ATMOSPHERE_MAX_RADIUS` | 8192 | 256–32768 | 云层类粒子半径上限 |
| `PARTICLE_MAX_EMITTER_EXTENT` | 2048 | 64–16384 | 发射器范围上限 |
| `PARTICLE_MAX_VELOCITY` | 2048 | 64–32768 | 粒子速度上限 |

## 8. 地图音频（4）

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `MAP_AUDIO` | 开 | 开/关 | 地图环境音 |
| `AUDIO_VOLUME` | 0.7 | 0.05–2 | 环境音总音量 |
| `AUDIO_EMITTER_RADIUS` | 1024 | 64–16384 | 未标半径的发声点的可听半径 |
| `AUDIO_MAX_EMITTERS` | 12 | 1–128 | 同时播放的发声点上限 |

## 9. 碰撞（10）

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `ORIGINAL_WORLD_COLLISION` | 开 | 开/关 | 用原版 brush/tricoll 碰撞；关 = 用渲染网格当碰撞 |
| `COLLISION_CELL_SIZE` | 512 | 256–8192 | 世界碰撞分块大小 |
| `PROP_COLLISIONS` | 开 | 开/关 | 道具碰撞 |
| `STREAM_PROP_COLLISIONS` | 开 | 开/关 | 只保留玩家附近的道具碰撞 |
| `PROP_COLLISION_ENABLE_DISTANCE` | 1536 | 128–32768 | 道具碰撞开启距离 |
| `PROP_COLLISION_DISABLE_DISTANCE` | 2560 | 128–65536 | 道具碰撞关闭距离 |
| `PROP_COLLISION_INSTANCES_PER_FRAME` | 2 | 1–4096 | 每帧创建的道具碰撞数 |
| `PROP_COLLISION_FRAME_BUDGET_MS` | 1 | 0–20 | 道具碰撞创建的每帧时间预算，0 = 用旧的按格预算 |
| `PROP_PRELOAD_COLLISION_BODIES` | 4 | 0–4096 | 加载画面每次让出前创建的碰撞数，0 = 全部延后 |
| `MODEL_COLLISION_MAX_TRIANGLES` | 8192 | 64–131072 | 用渲染网格当碰撞时的三角形上限，超出改用包围盒 |

## 10. 导航网格（5）

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `NAVMESH_ON_DEMAND` | 开 | 开/关 | 宿主开了导航时，跳过加载时整图生成，只在玩家和 NPC 附近生成；关 = 恢复引擎整图生成 |
| `NAVMESH_RADIUS` | 2048 | 512–65536 | 按需生成的半径 |
| `NAVMESH_DELAY_SECONDS` | 3 | 0–120 | 地图加载后多久开始生成 |
| `DEFERRED_NAVMESH` | 关 | 开/关 | 宿主没开导航时也强制开启（仅完整地图场景，不含 `.world.scene`） |
| `NAVMESH_STATIC_PROPS` | 关 | 开/关 | 道具碰撞也参与导航网格 |

## 11. 诊断与工具（6）

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `VERBOSE_LOG` | 关 | 开/关 | 完整诊断日志，不限警告条数 |
| `WARNING_LIMIT` | 32 | 0–1024 | 挂载警告条数上限 |
| `SPIKE_THRESHOLD_MS` | 2 | 0.1–1000 | 单项工作超过此耗时就记一条 `[spike]` |
| `SPIKE_LOG_LIMIT` | 32 | 0–4096 | `[spike]` 日志条数上限 |
| `SELFTEST` | 关 | 只认 `1` | 挂载后运行自检 |
| `EXPORT_ROOT` | 无 | 路径 | 模型导出的默认输出目录（调用时没传目录才用） |

表中变量名省略了 `SBOX_TITANFALL2_` 前缀。
