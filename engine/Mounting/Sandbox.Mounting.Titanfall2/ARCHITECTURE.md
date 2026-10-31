# Titanfall 2 挂载项目 · 架构与性能手册

当前全文件审阅入口：[FULL_REVIEW_2026_10_30.md](FULL_REVIEW_2026_10_30.md)。逐文件结构与覆盖边界见 [FULL_REVIEW_FILE_INDEX.md](FULL_REVIEW_FILE_INDEX.md)；下文按日期保留的性能和画面记录属于各自阶段。

本文件是 `Sandbox.Mounting.Titanfall2` 的**结构地图 + 性能开关索引**。
数据与踩坑过程见 `D:\sbox\titanfall2-perf-findings.md`，跨项目方法论见 `D:\sbox\mount-perf-playbook.md`。

---

## 1. 目录结构（按职责分层）

| 路径 | 职责 | 说明 |
|---|---|---|
| `GameMount.cs` | 挂载入口 | 资源源、模型/材质/纹理加载、挂载生命周期；`Titanfall2Mount : BaseGameMount` |
| `Titanfall2StreamingSettings.cs` | **唯一配置面** | 所有阈值/预算/开关都带 `SBOX_TITANFALL2_*` 环境变量覆盖，默认值 = 当前验证过的状态 |
| `Titanfall2Log.cs` | 日志预算 | 警告限流、spike 上报；避免大体量载入刷爆日志 |
| `Formats/` | 纯二进制解析 | rBSP / MDL53 / VTX / VVD / VPHY / PCF / VMT；不接触引擎资源 |
| `Pak/` | 容器与贴图解码 | RPAK / VPK / STARPAK / VTF / DXBC 反射 / Miles 音频 |
| `Resource/` | 引擎资源与场景组件 | 见下面的分解 |
| `PerfKit/` | **可复用性能件** | 零游戏依赖，整文件可拷到下一个 `Sandbox.Mounting.*` 项目 |
| `Perf/` | TF2 → PerfKit 适配 | 只放 TF2 特有逻辑（材质解析、排序、分段计时） |
| `Native/` | 原生桥 | LZHAM / Miles 的 C++ 桥与构建脚本 |

### Resource/ 分解

| 文件 | 职责 |
|---|---|
| `Map.cs` | `BuildScene`：把解析结果装成场景（world/decals/godrays/collision/spawn/props） |
| `Map.World.cs` | BSP 世界几何分组、材质、逐 cell 模型、聚合入口 |
| `Map.Spawns.cs` | 出生点、粒子系统、动态道具/旋转 mover、3D skybox |
| `Map.Environment.cs` | 雾、光照、环境实体解析 |
| `Map.Collision.cs` | 世界碰撞模型与分块 |
| `MapAudio.cs` | 地图音频：`_snd.ent` 的 `ambient_generic` 点声源 + `trigger_soundscape` 声景区（运行时数据编码进可序列化字段） |
| `MapWorldTypes.cs` / `MapRuntimeTypes.cs` | 世界几何与运行时数据类型 |
| `Titanfall2Map.cs` | **宿主组件**：把 `mount://titanfall2/maps/<name>.bsp.scene` 作为 `NotSaved` 子对象加载进自己的场景，开局在 `SpawnPoint` 上生成观察者相机；`GetAvailableMaps()` 列出可用地图 |
| `Titanfall2SpectatorCamera.cs` | 观察者相机控制（WASD/Shift/Space/Ctrl + 鼠标），用于还没有玩家控制器的阶段 |
| `StaticPropStreamer.cs` | 静态道具流送主流程（tick、队列、预算、spawn） |
| `StaticPropStreamer.Loading.cs` | PVS/道具编码、cell 初始化、预载、日志 |
| `StaticPropStreamer.Visibility.cs` | PVS 查询、cell/cluster 可见性、阴影状态 |
| `StaticPropStreamer.Lod.cs` | LOD 模式、换级队列、模型加载/重试 |
| `StaticPropStreamer.Batches.cs` | instanced 批次创建/销毁（重建入口在 `.Lod.cs`） |
| `StaticPropStreamer.Collision.cs` | 道具碰撞体按距离激活/回收 |
| `StaticPropRuntimeTypes.cs` | `StreamedProp` / `PropInstance` / `PendingBatch` / cell-cluster 坐标 |
| `Model.cs` / `Material*.cs` / `Texture.cs` / `Particle*.cs` / `Skybox3D.cs` … | 各类 `ResourceLoader<T>` 与场景组件 |

> 约定：**解析（Formats/Pak）→ 资源（Resource）→ 场景（Map/Streamer）→ 性能件（PerfKit）** 单向依赖，
> PerfKit 不允许反向引用 TF2 任何类型。

---

## 2. 载入数据流

```text
rBSP bytes
  └─ Titanfall2BspReader.TryRead            (Formats/)
       ├─ ParsedBsp.World.Meshes            → Map.World: 按 (cell, material) 分组成 MapMeshGroup
       │      ├─ WorldAggregate=1 → Perf.Titanfall2WorldAggregator
       │      │      ├─ 后台线程按材质拼接顶点/索引（纯托管，有界 chunk + 队列）
       │      │      └─ 主线程 Material/Mesh/Model 创建（PerfKit.PrepareGeometry/CreateAggregate）
       │      └─ WorldAggregate=0 → Map.World.BuildRenderChunks（每 cell 一个 Model）
       ├─ ParsedBsp.StaticProps             → StaticPropStreamer（编码后进组件，跨热重载保留）
       ├─ ParsedBsp.Entities/Partitions     → BSP 自带 ENTITIES（worldspawn/envmap_volume…）+ 分区表
       ├─ ParsedBsp.Visibility              → PVS（CellBSP + portal）
       ├─ ParsedBsp.StaticProps.LightProbe… → `parseStaticPropLightProbes`（默认开，随 `BSP_LIGHTMAPS`）
       └─ ParsedBsp.Cubemaps/Lightmaps      → `parseLightmaps`（默认开：`BSP_LIGHTMAPS=1`；服务器不读）
```

载入日志的**分段计时**（`Titanfall 2 world aggregates: ... materials Xs, vertex/index prepare Ys, mesh build Zs, total Ws`）
用于把"材质/贴图解析（IO，方差大）"和"顶点拼接（纯托管，稳定）"分开。`SBOX_TITANFALL2_PARALLEL_AGGREGATE=0`
可退回主线程顺序构建做 A/B。跨次载入差 4× 是已知现象，单次读数不能用来证明回归（见 findings §9.4）。


### 2.1 解析覆盖（实测，2026-10-22）

实测审计见 [`D:\sbox\titanfall2-map-parsing-audit.md`](../../../../titanfall2-map-parsing-audit.md)。
要点：

| 数据 | 状态 |
|---|---|
| 世界几何（全部顶点格式） | 完整：36 张图 rBSP v37 均只用到 LIT_BUMP / UNLIT / UNLIT_TS，无一例未支持格式 |
| PVS | 完整（`mp_angel_city`: 74 cells / 712 portals） |
| 静态道具（sprp v13） | 完整；`Skin`/`Cubemap`/`DiffuseModulation`/`LightingOrigin`/`ForcedFadeScale` 解析后仍无消费者；`LightProbeColor` 有 tint 链路但编码处恒白 |
| 实体 | **5/5 分区已注册（含 `_snd`）**；BSP 自带 ENTITIES（216 个）与 ENTITY_PARTITIONS 已解析，并在分区缺失时作为回退；渲染/玩法语义仍只覆盖其中一部分 |
| 烘焙光照 | lightmap **默认读**（`BSP_LIGHTMAPS=1`），同时解析 `WORLD_LIGHTS`、SH 探针、RTL 页并由场景控制器绑定 compiled local lights。不能据此宣称完整原版光照 |
| 音频 | **已接**：`_snd.ent` → `ambient_generic` 点声源 + `trigger_soundscape` 声景区；另有 Miles 事件运行时会话（`titanfall2_sound_event`）播放 authored event |
| 碰撞 | `ORIGINAL_WORLD_COLLISION=1` 独立选择权威 rBSP CM；默认保留渲染几何回退，专用服务器强制 CM 且按 contents 分组。与 `DEFERRED_NAVMESH` 开关独立 |

72 个非空 lump：默认读 38 个（含 lightmap 3 个与探针 2 个；`BSP_LIGHTMAPS=0` 时读 33 个）。仍未读的主要是 lightprobe 树、CSM/SHADOW_MESH、
TRICOLL_NODES/BEVEL、portal edge、occlusion mesh、bounds 类。

---

## 3. 运行时数据流（静态道具）

```text
每帧 OnUpdate
  └─ RefreshCells(anchor)           按距离选 cell / cluster，切 PVS
       ├─ UpdateClusterVisibilityState
       │     ├─ PVS mask → cell/batch 可见性
       │     └─ PerfKit.MountedInstanceCull.Update  逐实例距离剔除（子集只在数量变化时重建）
       ├─ UpdateClusterLodState（Legacy only）→ 换级队列（有界，饱和即跳过评估）
       └─ UpdateClusterShadowState / Collision 队列
  └─ 已建批次：PerfKit.MountedInstanceBatch
        ├─ 显式 LOD：按"最近实例原点"距离选级（Native 模式）
        ├─ 单级模型：不传级别（Legacy 模式，模型由队列换）
        └─ DrawModelInstanced（一次 draw 画完整批实例）
```

---

## 4. 性能开关（默认值）

| 开关 | 默认 | 作用 |
|---|---:|---|
| `SBOX_TITANFALL2_WORLD_AGGREGATE` | 1 | 世界几何按材质聚合成 AggregateRenderer（−72% draw 提交） |
| `SBOX_TITANFALL2_PARALLEL_AGGREGATE` | 1 | 世界聚合的顶点/索引拼接走后台有界流水线；0 = 主线程顺序（A/B 用） |
| `SBOX_TITANFALL2_ALL_BODYGROUP_CHOICES` | 0 | 0 = 无 body 覆盖时每个 body part 的第 0 个 choice（`Base` 是组合倍率，并非默认下标）；1 = 所有 choice 叠在一起，仅 A/B |
| `SBOX_TITANFALL2_DEFERRED_NAVMESH` | 0 | 1 = 延迟请求场景 NavMesh 生成，不决定 CM 解析或碰撞来源 |
| `SBOX_TITANFALL2_BSP_LIGHTMAPS` | 1 | 读 BSP lightmap 并使用 `titanfall2_world_lightmapped.shader`；这是当前默认路径。解析缓存要求此开关为 0，见 §4.1 |
| `SBOX_TITANFALL2_BAKED_TONEMAPPING` | 1 | 烘焙光照路径使用原版显示曲线/曝光（`BAKED_TONEMAPPING=0` 比较原始 radiance） |
| `SBOX_TITANFALL2_BAKED_COLOR_CORRECTION` | 1 | 应用地图 `color_correction` 的 32³ RGB LUT |
| `SBOX_TITANFALL2_PROP_RENDER_DISTANCE` | 8192 | 逐实例距离剔除半径；0 = 关闭 |
| `SBOX_TITANFALL2_PROP_RENDER_DISTANCE_HYSTERESIS` | 512 | 剔除迟滞，防止边界抖动 |
| `SBOX_TITANFALL2_PROP_PVS` | 1 | portal PVS 剔除 |
| `SBOX_TITANFALL2_PROP_LOD_MODE` | native | `native` = 全 LOD 一个模型、draw 传级别；`legacy` = 每级一个模型、按距离换 |
| `SBOX_TITANFALL2_PROP_LODS` | 1 | 道具 LOD 总开关（0 强制 LOD0） |
| `SBOX_TITANFALL2_PROP_LOD1/2/3_DISTANCE` | 1536 / 4096 / 8192 | 世界距离阈值（内部按 `25·D/(radius·focal)` 转成引擎 metric） |
| `SBOX_TITANFALL2_PROP_LOD_FOCAL_PX` | 960 | 1920px @ 90° 的水平焦距，换算 LOD 阈值用 |
| `SBOX_TITANFALL2_PROP_BATCHABLE` | 1 | 引擎可合并提交 |
| `SBOX_TITANFALL2_PROP_BATCH_CLUSTER_CELLS` | 4 | 每 4×4 个 prop cell 共用批次；1 = 历史逐 cell 粒度（A/B） |
| `SBOX_TITANFALL2_PROP_INSTANCE_FRUSTUM` | 0 | 逐实例 CPU 视锥剔除（默认只依赖原生批次剔除） |
| `SBOX_TITANFALL2_PROP_IN_CUBEMAP` | 0 | 静态道具不进 cubemap pass |
| `SBOX_TITANFALL2_PROP_NEEDS_ENVMAP` | 0 | 静态道具不进环境反射 pass |
| `SBOX_TITANFALL2_PROP_SHADOWS` | 0 | 道具投影；默认关，只保留原生接触阴影 |
| `SBOX_TITANFALL2_MAP_SHADOWS` | 0 | 世界 CSM；默认关（优先帧时间） |
| `SBOX_TITANFALL2_CONTACT_SHADOWS` | 1 | 原生屏幕空间接触阴影 |
| `SBOX_TITANFALL2_MAP_FOG` | 1 | 地图高度/方向雾 |
| `SBOX_TITANFALL2_PROP_COLLISIONS` | 1 | 道具碰撞体 |
| `SBOX_TITANFALL2_PROP_COLLISION_ENABLE/DISABLE_DISTANCE` | 1536 / 2560 | 碰撞激活区间（迟滞 1024） |
| `SBOX_TITANFALL2_PROP_COLLISION_INSTANCES_PER_FRAME` | 2 | 碰撞体 churn 上限 |
| `SBOX_TITANFALL2_WORLD_RENDER_CELL_SIZE` | 2048 | 世界分块尺寸 |
| `SBOX_TITANFALL2_PROP_LOD_SWAP_QUEUE_LIMIT` | 256 | 换级队列上限；饱和跳过评估而不是丢条目 |
| `SBOX_TITANFALL2_MAP_AUDIO` | 1 | 地图音频总开关（`_snd.ent` 点声源 + 声景区） |
| `SBOX_TITANFALL2_AUDIO_VOLUME` | 0.7 | 地图音频全局音量缩放 |
| `SBOX_TITANFALL2_AUDIO_EMITTER_RADIUS` | 1024 | `ambient_generic.radius=0` 时的听觉半径；省略 `triggerNearbyRadius` 的声景区按 ×2 使用 |
| `SBOX_TITANFALL2_AUDIO_MAX_EMITTERS` | 12 | 同时播放的点声源上限（按听众距离取最近） |
| `SBOX_TITANFALL2_2D_SKY` | 0 | 挂载不再自建 2D sky；默认用宿主场景/系统的 2D sky（旧版行为）。设为 1 才创建零售 `skycube`（主世界 + 3D skybox 世界各一个） |
| `SBOX_TITANFALL2_SKY_MATERIAL` | `mount://titanfall2/skies/skycube.vmat` | 2D 天空材质 |
| `SBOX_TITANFALL2_SKY_INDIRECT` | 1 | 用 2D 天空做 IBL（补暗部） |
| `SBOX_TITANFALL2_SKYBOX_MODE` | 1 | 0 = 仅 2D 天空；1 = 引擎 3D 天空盒（零售远景扇区，默认）；2 = 相机跟随天球（实验） |
| `SBOX_TITANFALL2_SKYBOX_TARGET_DISTANCE` | 25000 | 自动 scale 的目标视距：把 vista 内容推到这么远（用 `skyscale` 会把相机放进扇区里） |
| `SBOX_TITANFALL2_SKYBOX_MODEL_SCALE` | 1 | 保持原始尺寸，避免远景被天空视图的远裁剪面截断 |
| `SBOX_TITANFALL2_SKYBOX_SCALE` | 0 | 覆盖天空盒 draw scale（0 = 用地图 `skyscale`） |
| `SBOX_TITANFALL2_AMBIENT_SCALE` | 1.35 | `_ambient` 补光倍率（烘焙光照默认关时的暗部） |
| `SBOX_TITANFALL2_GODRAYS` | 0 | Godray 叠加卡片；关掉消除移动时的竖向亮条 |

完整清单见 `Titanfall2StreamingSettings.cs`（每个字段上方的注释记录了为什么是这个值）。

### 4.1 派生解析缓存的有效条件

`SBOX_TITANFALL2_PROCESSED_CACHE=1` 只是允许缓存；`Map.Cache` 只在**默认渲染导入模式**下生效，
即同时满足 `ORIGINAL_WORLD_COLLISION=0` 与 `BSP_LIGHTMAPS=0`。烘焙光照现在是默认路径
（`BSP_LIGHTMAPS=1`），所以默认启动**不会**命中 `map` 缓存，日志会写
`disabled for this import mode`。要复现 2026-10-24 记录的冷/热加载对比
（46.5 s → 11.3 s），需要显式设 `SBOX_TITANFALL2_BSP_LIGHTMAPS=0`。
缓存格式目前不保存 lightmap/WORLD_LIGHTS 数据；把烘焙数据纳入缓存需要先扩展
`Map.Cache.cs` 的序列化字段并做 round-trip 校验。

---

## 5. 实测基线（mp_angel_city / RTX 4060 Laptop / 1680×1050）

| 阶段 | Obj | Draws | Tris | Render | fps |
|---|---:|---:|---:|---:|---:|
| 原始 | 4468 | 4618 | 2.68M | 17.44ms | 45.3 |
| ＋LOD 三件套 | 3887 | 4155 | 1.86M | 15.63ms | 52.8 |
| ＋提交 flag | 3364 | 3892 | 1.68M | 14.08ms | 55.0 |
| ＋世界聚合 | 2256 | **1091** | 1.33M | 13.06ms | 60.1 |
| ＋实例级剔除@4096 | 2158 | 1721 | — | **5.78ms** | ~150（走位后读数） |

成本模型：`Render ≈ 2.4ms + 2.63µs × Obj` —— **对象数是成本单位**，减对象优先于减三角形。
注意 `DrawCalls` **不统计** `SceneCustomObject` 内的 `DrawModelInstanced`（findings §8.3），
所以"draw 数没变"不等于"没有收益"。

---

## 6. 复用到下一个挂载项目

1. 拷 `PerfKit/MountedScenePerfKit.cs`（整份文件，改 namespace 即可；见 `PerfKit/README.md`）。
2. 世界几何：按 `(空间分区, 材质)` 分组 → 每材质一个 `MountedGeometryGroup<TVertex>`
   → `MountedAggregateBuilder.Build(groups, prefix)` → `Attach(...)`。
   需要载入期流水线时，用 `PrepareGeometry(...)`（纯托管，可后台）→ `CreateAggregate(...)`（主线程）：
   TF2 的 `Perf/Titanfall2WorldAggregator.cs` 就是“后台拼接 + 有界队列 + 主线程建 Mesh”的参考实现。
3. 静态实例：`new MountedInstanceBatch(world, model, transforms, options)`，
   有多级 LOD 时把 `SelectLod` 设成距离→级别的函数。
4. 距离剔除：每个批次持有一个 `MountedInstanceCull`；`Update(all, anchor, keepDistance)`
   返回 true 时才 `SetDrawTransforms(cull.Subset)`。
5. 所有开关做成环境变量，默认值等于已验证的最优；A/B 时保持同一相机与位姿。

---

## 7. 已知边界与未决

- **宿主场景**：挂载场景是只读的，直接 `Scene.LoadFromFile` 后按 Play 会丢掉你在编辑器里加的玩家/相机。
  用 `Titanfall2Map` 宿主组件把地图作为 `NotSaved` 子对象加载进自己的场景；它会生成观察者相机，
  但还没有 TF2 风格的玩家控制器。
- **运行时验证**：已实测同构建载入 `mp_angel_city`，聚合计数与重构前一致
  （12,735 源 mesh → 334 aggregates / 1,742 fragments / 2,320,944 顶点），
  但**同相机帧时间 A/B 尚未重跑**。新的有界聚合流水线（`SBOX_TITANFALL2_PARALLEL_AGGREGATE=1`）
  也只做到编译验证；下一次载入地图后应确认 `Titanfall 2 world aggregates` 的
  `vertex/index prepare` / `mesh build` / `total` 与画面，再和 `=0` 做同相机 A/B。
- **道具对象数仍是主要余量**：日志实测 18467 实例 / 5469 批（3.4 实例/批）+ 988 个 individual rigid。
  候选（需先载入一次读日志再 A/B，不要盲改默认值）：
  `SBOX_TITANFALL2_PROP_BATCH_CLUSTER_CELLS=2` 可减少 500~800 批，但 batch bounds 变大、
  Native 整批选 LOD 会偏精细、阴影 all-or-none 变粗；individual rigid 先查“为什么不能实例化”
  （半透明/水/贴花走 instanced 有绘制顺序风险）。
- **载入期**：源顶点已按三角形展开（无共享顶点），所以“共享/去重顶点”没有收益；聚合会完整拼一次
  顶点/索引（已预分配），碰撞路径已去掉多余的 `ToArray()`。先用 `materials` / `build` 分段计时
  定位，再决定是否做两遍扫描。
- **载入耗时**：聚合路径仍是"每材质完整拼一次顶点/索引"。已做预分配和后台拼接，
  但材质解析（`Material.Load`/贴图解码）仍在主线程；先看 `materials` 分段，再决定是否做并行材质解析。
- **LOD 阈值**：引擎比较的是 `50/screenWidthInPixels`，不是世界距离；换分辨率/视场会影响选级。
- **DrawCalls 指标**：不统计自定义 SceneObject 的实例化 draw。
- **世界聚合的 fragment 上限**：`AggregateRenderer.MaxFragments`（512）；超出自动切片，绝不丢几何。
- **地图音频是近似**：零售 `soundName`/`playlooping` 是**事件名**，不在 MBNK 的 source 表里；当前用令牌
  相似度匹配到 `..._6ch_v1_lp` / `amb_emit_...` 变体（数字精确优先、`emitter`≈`emit`）。声景区用引擎
  `SoundscapeTrigger`（同一时间只有一个生效、带交叉淡入），DSP/`dsp_volume` 未实现。若后续解析
  MBNK 事件表，可以去掉这层启发式。
- **天空必须在独立世界内从 authored sky_camera 观察**：`models/vistas/angel_city_se.mdl`
  的边界为约 4053×4053×2818，并带有滚动 UV 云面。不能通过玩家在主世界中的位置判断它是否应当显示。
  `Titanfall2Skybox3D` 在渲染前抵消主相机平移，使用组合后的 `Camera.View.Position`。
  远景保留原始尺寸（`SKYBOX_MODEL_SCALE=1`），并初始化 bind pose 和完整模型边界。
  之前的 20 倍尺寸会出现随方向变化的圆形远裁剪缺口；点状边界还会错误剔除整个远景。
  不再用额外的 2D 背景掩盖这些问题；2D 天空仍为显式可选项。
- **音频依赖听众**：点声源按 `Sound.Listener`（玩家相机）距离激活；只加载场景、编辑器里没有玩家时，
  听众停在菜单相机处，因此不会听到地图环境音。

---

## 8. 验证

```powershell
dotnet build engine/Mounting/Sandbox.Mounting.Titanfall2/Sandbox.Mounting.Titanfall2.csproj -c Debug -p:SkipCopyOutputDll=true
```

当前结果：**0 警告 / 0 错误**。部署（`game/mount/titanfall2/titanfall2.dll`）需先退出 sbox.exe；
游戏运行中构建请始终带 `-p:SkipCopyOutputDll=true`。

### 2026-10-24 天空与周期回调修复

- 挂载 DLL 直接由 .NET 构建，不能依赖 addon 编译器自动补齐生命周期订阅接口。
  声明 `OnUpdate` / `OnFixedUpdate` / `OnPreRender` 的挂载组件必须分别显式实现
  `Sandbox.Internal.IUpdateSubscriber` / `IFixedUpdateSubscriber` / `IPreRenderSubscriber`。
  否则 `Scene.AddObjectToDirectory` 不会将组件加入对应队列，重写方法不会逐帧执行。
- 天空组件补齐 `IPreRenderSubscriber`，并在启停时创建/清理独立世界；`SceneModel` 创建后调用
  `UpdateToBindPose`，再设置经过完整 Transform 的模型边界。默认移除之前的 20 倍缩放。
- 材质动画、地图音频、旋转物件、地图宿主、观察者相机、世界阴影控制器以及实验流送/天球组件
  补齐更新订阅。地图音频在每次候选筛选前清空 `Keep`，避免离开范围的声音沿用上次的播放选择。
- 默认 PBR shader 在计算环境反射之前设置并归一化表面法线；可选天空材质绑定真实属性
  `g_tSkyTexture`，不依赖旧 API 的 `SkyTexture` 别名。
- 新 DLL 实机检查：周期回调订阅缺失数为 0；两个远景对象边界均非零；没有额外 2D 天空。
  四个机位（包括地图外）的天空相机抵消残差不超过 0.002 世界单位；原始尺寸下四个机位的
  圆形缺口消失，另检查了 `ZFar=10000`。环境音旧选择清理回归通过，同时播放数不超过 12。
- 第三张截图中的零星移动亮点没有取得严格的原机位前后对照；已修正反射法线错误，但不能把
  这个代码修复等同于所有白色亮点均已消除。保留正常泛光，等待后续实机对照。

### 2026-10-24 加载、水体、视距与静态道具聚合

- 用户随后确认异常亮点已解决。本次不再改变其反射修复。
- 世界材质使用 `Titanfall2MaterialPrefetch`，最多 4 个在途材质、128 MB 预读数据。
  后台只读包和准备压缩纹理，所有引擎纹理/材质创建仍在主线程。
  天使城实测 351 个材质任务，预读峰值约 22 MB，结束后占用为 0；另一次聚合测试
  383 个材质任务，10 个实际工作线程、峰值 23 MB。线程数是整个阶段累计使用的线程数，
  不等于同时工作的数量。关闭开关：`SBOX_TITANFALL2_MATERIAL_PREFETCH=0`。
- 默认/透明近似 shader 目前使用顶点法线，不读取声明的法线纹理，材质绑定和预读据此
  跳过无用上传。水、贴花、折射保留法线纹理。预载单次上限从 128/12 ms 调到 512/35 ms。
  世界材质阶段观察到约 16.1 s 到 6.55 s，但不同进程、缓存和纹理上传会影响比较；
  静态道具冷载仍观察到 25–51 s，不能宣称整个加载问题已经彻底解决或 CPU 可以全核满载。
- 水体参考 `D:/sbox/deadlock-sbox-mount-main/Assets/shaders/deadlock_glass.shader`：
  在半透明通道内自行合成背景，不二次 Alpha 混合；所有源 Alpha 相乘后再应用覆盖度下限。
  水面独立分区建模，使用 `Titanfall2WaterRenderer` 明确设置半透明和 framebuffer-copy 标记。
  普通 `SceneAggregateObject` 的水面实测无法正确取得背景，会出现紫色/纯色，故不再把水面
  加入普通世界 aggregate。最终 shader 的 VS/PS 都通过正式编辑器编译。
  最终场景检查 57 个水面分区全部使用该组件，57 个原生对象的三个通道/复制标记均正确。
  材质预读顺序随渲染阶段同步调整：普通世界、贴花、Godray、水，避免晚消费的水材质
  占满预读窗口，导致中间的普通材质退回同步读取。
- 道具默认显示距离 8192（原 4096），保留半径至少 256 的大型地标；PVS/引擎视锥仍生效。
  距离考虑模型半径，移除会覆盖地标保护的整 cell 距离门控；相机刷新总会更新实例距离。
  `MountedInstanceCull` 修复同数量、不同成员时错误重用旧列表的问题。没有实现透明淡出。
- 实例渲染可按各实例选择原始 LOD，并可按当前渲染 pass 的视锥剔除，避免主视角误剔除阴影。
  当前密集机位对照没有明显整帧改善，额外 CPU 视锥测试默认关闭；
  `SBOX_TITANFALL2_PROP_INSTANCE_FRUSTUM=1` 可单独测试。没有修改用户全局帧率和画质配置。
- 新增 `Map.StaticPropAggregation.cs`：静态模型的世界坐标顶点真正写入 BSP 使用的
  `(WorldRenderCell, WorldMaterialKey)` 分组，进入同一材质缓冲。不是只改变场景父子关系。
  默认关闭：`SBOX_TITANFALL2_AGGREGATE_STATIC_PROPS=1` 在下次启动时启用。
  限制为最多 4 份、刚性、没有可减少三角面的 LOD、默认 skin、非透明/水/贴花的模型；
  顶点预算默认 300000，可由 `SBOX_TITANFALL2_AGGREGATE_PROP_VERTICES` 调整。
  BSP lightmap 实验路径开启时跳过，以免混入不兼容的烘焙光照。
- 天使城首次聚合测试：42 种模型、89 个道具、299997 个展开顶点，CPU 准备 3.60 s。
  道具实例批次 5469 → 5398，但世界增加 32 个材质 aggregate，不能只拿减少的 71 批
  宣称总提交量减少 71。尚未证明整帧 GPU 收益，因此保留实验开关。
  序列化后 42 个模型/半径记录完整；运行时 89 个烘焙道具都没有重复可见对象，
  89 个碰撞来源记录保留，起重机原始模型碰撞体实际创建成功。

### 2026-10-24 持久缓存与移除距离投影跳变

- `ProcessedCache.cs` 提供统一的磁盘派生数据缓存，默认开启（当前默认光照路径下 `map` 缓存不命中，
  见 §4.1）。数据保存在
  `%LOCALAPPDATA%/sbox/mount-cache/titanfall2/processed`；默认预算 2048 MB，按最近使用淘汰。
  可用 `SBOX_TITANFALL2_PROCESSED_CACHE=0` 禁用，`SBOX_TITANFALL2_CACHE_MB` 调整预算，
  `SBOX_TITANFALL2_CACHE_PATH` 指定目录。没有把原生 GPU 对象或完整运行场景常驻内存。
- 缓存标识包含挂载 DLL 的模块 ID、游戏目录和 VPK/RPAK/STARPAK 的大小/修改时间；
  松散文件额外检查自身大小和修改时间。更新源内容、转换器或相关导入设置会生成新缓存。
  文件用 GZip（含 CRC）压缩，经独立临时文件原子发布；损坏/截断/格式不符会回退原始导入。
  分配量、数组和字符串长度有上限；旧缓存被替换或按预算淘汰，不影响原游戏包。
- `Map.Cache.cs` 保存默认导入路径的 BSP 顶点、索引、物件信息、PVS、实体及内嵌 cubemap。
  大数组用二进制，元数据用 JSON。实验光照贴图/原始碰撞解析模式保持原解析路径。
- `Model.Cache.cs` 保存静态模型转换后的各级 LOD 顶点、索引、材质名、边界及原始半径；
  GPU 模型仍在新进程主线程创建，LOD 缺级别名和特殊材质回退保持原行为。
- `Material.Cache.cs` 保存原生 RPak 材质的贴图绑定、参数、常量和 UV/动画表达式；
  非通用的旧 VMT 路径保持原处理。材质分类和预读优先用缓存描述，避免重新解压 RPak 头。
  `Texture.Cache.cs` 保存已经整理好的 BC mip 数据，显存资源按正常生命周期创建/释放。
  材质/模型资源和纹理上限变动仍按各自有效标识重建。
- 同一最终构建，两次独立游戏进程加载天使城（`local.tf2_visual_debug`）：

  | 阶段 | 首次生成完整缓存 | 新进程命中缓存 |
  | --- | ---: | ---: |
  | 整图加载（game.load） | 46.508 s | 11.278 s |
  | BSP 读取/解析/缓存 | 2.90 s | 0.48 s |
  | 世界材质阶段 | 17.76 s | 0.62 s |
  | 静态道具预载 | 13.15 s | 4.11 s |

  首次包含缓存生成开销，这是本机单组对照，不是所有地图/硬件的固定承诺。
  第二次：地图 1 次命中，模型 1036 次命中/0 未命中，纹理 2392 次命中/0 未命中，
  原生材质 957 次命中；保留 1 次旧材质路径未命中。没有缓存拒绝或写入失败。
- 数据回归：12735 个世界 mesh 的所有顶点/索引及材质/页号逐项一致；原始 19458 个物件
  （含天空模型）、PVS、实体和 cubemap 元数据一致。起重机、基座、围栏的 4 级模型数据一致；
  6 份 BC7/ATI1N/DXT1 纹理逐字节一致，12 份材质描述序列化后完整一致。
  截断缓存被拒绝并可重建，变更源标识不会命中旧数据。新进程画面对照没有出现错误贴图。
- 挂载道具投影默认由开改为关，地图地形投影继续默认关闭；原本 896 单位的道具开关不再运行。
  场景实测近/远机位阴影批次均为 0，世界 aggregate 和动态挂载模型投影也为 0；
  19455 个世界道具、57 个水面分区保留，基础光照和接触阴影继续启用。
  若显式设置 `SBOX_TITANFALL2_PROP_SHADOWS=1`，距离默认 0 表示不在挂载层按距离开关；
  世界投影同理。原生级联阴影自己的距离和质量限制仍然适用。
  `SBOX_TITANFALL2_CONTACT_SHADOWS=0` 可额外关闭接触阴影。

### 2026-10-24 Traffic 材质对照与黑顶修复

此节为当日历史基线；后续状态见下一节和 [MOUNT_COMPLETENESS.md](MOUNT_COMPLETENESS.md)。

- 详细证据、未决问题和下一步见 [VISUAL_PARITY_PLAN.md](VISUAL_PARITY_PLAN.md)。
- 黑顶为 `mp_lf_fakesky_*` 的全息顶棚，原 VMT 第二层为天空 cubemap；新增材质立方体输入路径。
  同时将旧双纹理系列和水 shader 中应由 `Material.Set` 写入的标量/向量改为材质参数，
  保留 framebuffer copy 等真正的渲染属性。全新进程验证顶棚已恢复天空和网格。
- 水体去除重复的深色乘数，分离岸边覆盖度与水体厚度下限，ATI2N 法线补齐 Z 重建。
  水面不再近黑，源 UV/法线参数开始生效；完整反射、深度吸收及部分岸边黑线仍未解决。
- `layerBlendTexture`、细节法线和遮罩不再归入 Detail RGB；`c_layerBlendRamp` 不再被当作
  细节强度。墙板黑斑仍需真正的双层材质混合，不能宣称本次已经消除。
- C# 构建 0 警告/错误，5 个 shader 的 VS/PS 编译通过；5 组语义、108 个材质参数来源通过运行时检查。

### 2026-10-25 材质与光照路径续作

- `Titanfall2TextureBinding.Layer` 保留原始表面层索引；`UsesLayeredSurface` 只为明确支持的普通不透明双层表面启用第二套颜色/Gloss/Specular/法线采样。LayerBlend 独立于 Detail，缓存和预读都保留层身份。
- `titanfall2_surface.hlsl` 根据零售 DXBC 公式消费顶点 Alpha 和 `c_layerBlendRamp`；`titanfall2_normals.hlsl` 共享 RPak BC5 坡度解码及带符号的 UV 切线基。普通表面与贴花接入实际法线。
- 私有 shader 的材质常量不再误声明为 RenderAttributes；材质环境图使用 cubemap 输入。22 个 shader 正式编译和新进程 Schema 审计通过。真正的 framebuffer、depth 及逐物件属性仍保留。
- `DxbcVariable` 保存 RDEF flags；`MaterialParameters` 忽略 unused 备用字段，避免源纹理又被 `c_perfSpecColor=0.03` 衰减。
- 水体有独立的 `Distortion2` 语义，按原版 UV1/UV2 顺序扰动；RPak 已提供运动参数时禁用猜测流速。水法线同样按原版坡度解码。
- 水体内部处理折射，岸边使用预乘透明合成，并读取实际 framebuffer 视口比例。Traffic 原先全透明仍有的河岸横纹已在同机位消失；受控零覆盖度 GPU 像素差为 0。
- 实验 BSP 光照显式传递第二 UV，转发完整 Lyr/法线材质参数，解码 SKY A 的 RGB/Alpha 和 SKY B 方向；替换间接漫反射并保留原生直射与探针高光。UV 四象限及 Traffic 两页数据检查通过，默认仍关闭，物件 SH、RTL 和 HDR 量纲待完成。
- 可复用 GPU 检查见 `Perf/MaterialVerification/`；完整差异与后续步骤见 [VISUAL_PARITY_PLAN.md](VISUAL_PARITY_PLAN.md)。
