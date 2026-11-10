# Titanfall 2 挂载环境变量

挂载后端与诊断覆盖集中在 [Titanfall2StreamingSettings.cs](Titanfall2StreamingSettings.cs)，玩家画质适配在 [Titanfall2GraphicsSettings.cs](Titanfall2GraphicsSettings.cs)。内容配置在首次挂载时读取；日志、缓存目录和导出目录分别见 [Titanfall2Log.cs](Titanfall2Log.cs)、[Resource/ProcessedCache.cs](Resource/ProcessedCache.cs) 和 [Resource/ModelExporter.cs](Resource/ModelExporter.cs)。

## 玩家画质设置

正常游玩直接使用游戏的画质菜单，无需设置环境变量：

| 玩家设置 | TF2 挂载行为 | 修改后生效时机 |
| --- | --- | --- |
| 纹理低 / 中 / 高 / 极高 | RPAK、VTF 2D 与 VTF cubemap 的单张源纹理上限为 1024 / 2048 / 4096 / 原始尺寸；只有现有 mip，不放大源图 | 保存设置后更新导入策略；已有贴图保持不变，完全离开地图、待资源释放后重新加载。编辑器保留资源缓存时需重新挂载或重启编辑器 |
| 阴影低 / 中 / 高 / 极高 | 地图方向光默认允许 4 级级联，引擎质量配置实际限制为 1 / 2 / 3 / 4 级，并控制分辨率、距离和接触阴影 | 引擎按其画质设置即时应用，不必重建地图 |
| 抗锯齿、渲染比例、超分、原生后处理 | 使用引擎已有实现与质量配置 | 按引擎设置的生效方式 |

优先级：单项环境变量覆盖 > 显式旧 `FPS_PRESET` 的相关限制 > 玩家设置 / 引擎质量配置。纹理只受 `TEXTURE_MAX` 显式覆盖，`FPS_PRESET` 不改变它。阴影覆盖是组件上限，仍受引擎全局质量上限裁剪。菜单中的帧率上限不会自动变成旧 `FPS_PRESET`。

TF2 高度/方向雾、godray 面片和粒子不是引擎体积雾，TF2 自定义折射水面也不使用原生水面质量实现；这次没有把这些内容错误绑定到同名的菜单选项。烘焙光照、曝光标定、碰撞、导航、GPU/CPU 后端、缓存预算及内容优先级保持各自职责。专用服务器或不兼容引擎无法取得画质对象时，纹理回退为 1024，显式覆盖仍有效。

## 怎么设置

- 在启动 `sbox.exe` 之前设置环境变量，再从同一个窗口启动游戏。PowerShell 示例：`$env:SBOX_TITANFALL2_PROP_SHADOWS = "1"`。
- 开关：`1`、`true`、`yes`、`on` 为开，不区分大小写。不设置或留空时用默认值，其它非空值都算关。
- 数值按英文小数点解析。超出范围的值会夹到边界，解析失败时用默认值。
- 环境变量覆盖在首次初始化时读取，修改后要重启游戏；玩家纹理设置监听保存事件，不需要重启进程。缓存目录和模型导出目录在各自的使用路径中读取；为便于复现，仍建议启动前统一设置。

下表省略了 `SBOX_TITANFALL2_` 前缀。

## 缓存与贴图

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `PROCESSED_CACHE` | 开 | 开/关 | 缓存模型、贴图、材质描述和道具淡出距离；不缓存地图几何或光照贴图 |
| `CACHE_MB` | 2048 | 128–8192 | 处理缓存的磁盘预算（MB） |
| `CACHE_PATH` | `%LOCALAPPDATA%\sbox\mount-cache\titanfall2\processed` | 路径 | 处理缓存目录 |
| `TEXTURE_MAX` | 跟随玩家纹理设置 | 64–16384 | 显式覆盖贴图最大边长；未设置、空值或非法数值时跟随玩家设置。低显存可显式设 512 |
| `LIGHTMAP_16BIT` | 开 | 开/关 | 默认单级光照页使用 SkyB（BGR565）与 RealTimeB（BGRA4444）：光照片 17→13 B/px。SkyB 使用原生上传验证过的 BGR565（R 高位、B 低位），不能替换为会返回有效但数据失真的 RGB565。多级 mip 下 RealTimeB 使用 RGBA8888，避开原生 BGRA4444 下级 mip 失真。设 0 全部回到 RGBA8888 |
| `LIGHTMAP_MIPS` | 关（120 档为开） | 开/关 | 给 SkyA / SkyB / RealTimeB 生成 L0–L2 mip 链，生成前先按几何覆盖率把图集 chart 向外 dilate 4 步，避免 mip 把 chart 外的空洞混进来。按原生 API 要求以最小 mip 在前上传；RealTimeB 多级页使用 RGBA8888。代价是远景 chart 边缘略糊和额外显存占用 |

## 静态 prop

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `RESIDENT_WORLD` | 开 | 开/关 | 普通地图在场景加载期间完成静态道具碰撞准备并常驻，减少移动时创建/释放碰撞体；增加加载时间与内存。设 0 恢复普通地图的距离流式碰撞。纯世界和专用服务器始终常驻。此设置写入构建出的场景，旧场景需重新导入 |
| `GPU_STATIC_SCENE` | 开 | 开/关 | 尝试挂载内共享几何、GPU 可见性/LOD 和材质表 MDI 路径。只接收支持的静态 default/cutout 模型；不兼容的模型或初始化失败仍使用 CPU 批次。依赖现有托管 CommandList 播放方法；未修改引擎源码 |
| `GPU_STATIC_SCENE_MB` | 512 | 64–2048 | GPU 场景缓冲预算（MiB），包含共享几何、永久实例/材质表、固定数量的可写视图输出，以及四份只读保守输出；创建前超过预算回退 CPU。该值不包含源模型、绑定纹理、驱动分配与其他引擎资源，不是整个游戏的显存上限 |
| `GPU_VIEW_SLOTS` | 8 | 1–64 | 加载时预分配的可写视图输出槽数，每帧每次 recording 独占一个。超过数量的视图使用只读保守输出，不再扩容或复用在途 UAV；顶点阶段继续按当前视图判定 LOD、距离和 PVS，保留空远景 LOD、混合子网格归属及投影资格。它不做 compute 视锥剔除，也不执行距离阴影门控，可能增加顶点工作与远处阴影。统计中的 `ConservativeRecordings` 可识别此情况；增加槽数会占用更多预算 |
| `PROP_RENDER_DISTANCE` | 8192 | 0–65536 | 逐实例距离剔除半径，大型地标不受影响。0 关闭距离剔除 |
| `PROP_AUTHORED_FADE_SCALE` | 1 | 0.25–16 | 乘到地图为每个 prop 设定的淡出距离上 |
| `PROP_SHADOWS` | 开 | 开/关 | 让半径不小于 48 的静态 prop 投射动态阴影 |

## 帧率与提交粒度

这 4 个值控制每帧要提交多少渲染对象、多少三角形进阴影和深度通道。默认值等于"功能打开但不激进"；想冲更高帧率再往下调。

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `FPS_PRESET` | 0 | 0–240 | 旧性能覆盖预设，不读取玩家帧率上限。0 与 60 不加额外限制；120 改为阴影距离 1024、级联上限 1、LOD 缩放 0.5、cell 4096、批次簇 8、绘制距离 6144，并关掉接触阴影与 Godray。任何一个变量显式设置时都优先于预设 |
| `PROP_SHADOW_DISTANCE` | 0 | 0–65536 | 道具方向光阴影距离。0 关闭距离门控，保留原批次的投影资格。CPU 参照路径按批次门控，每帧最多改 32 次标志；GPU 路径以主相机为锚逐实例门控并保存距离回滞状态，不扫描 CPU 实例、不改原生对象标志 |
| `PROP_LOD_SCALE` | 1 | 0.1–4 | 乘到静态 prop 的 LOD 切换距离（1536/4096/8192）上。小于 1 更早切到低模，减少前向与深度通道三角形 |
| `BATCH_CLUSTER` | 4 | 1/2/4/8/16 | 多少个 prop cell（512 单位）合成一个实例批次。越大提交对象越少，但 PVS 和阴影按批次聚合，粒度变粗 |
| `WORLD_CELL_SIZE` | 2048 | 512–8192 | 世界几何的空间 cell 边长。越大 fragment/draw 越少，视锥剔除越粗 |
| `WORLD_MATERIAL_TABLE` | 开 | 开/关 | 世界材质表：同一 lightmap 页的兼容不透明材质共享原生 aggregate 提交材质，保留原空间片段、边界和三角形范围。动画、cutout 及其他 shader 走原路径；一个 scene 只支持一个使用材质表的世界。可减少 CPU 提交，材质间接读取可能增加 GPU 成本，不能按 draw 降幅推算 FPS 收益。设为 0 恢复原路径 |

冲 60/120 fps 的推荐组合见 [ARCHITECTURE.md](ARCHITECTURE.md) 第 13 节。比较性能时使用同一进程、固定机位、同一相机，并记录 `draws`/`triangles`/`objects`。

## 光照与画面

| 变量 | 默认 | 范围 | 作用 |
| --- | --- | --- | --- |
| `BSP_LIGHTMAPS` | 开 | 开/关 | 使用地图烘焙的 lightmap、光照探针和本地光。关掉后改用 s&box 原生光照，只用于排查不支持的地图 |
| `MAP_SHADOWS` | 开 | 开/关 | 让导入的世界几何参与方向光级联阴影（CSM） |
| `SHADOW_CASCADES` | 4（由引擎按玩家画质裁剪） | 1–4 | 方向光组件的级联上限。`MAP_SHADOWS=0` 时强制为 1；旧 120 fps 预设用 1。显式设置仍不能超过引擎全局质量允许的级联数 |
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

## Northstar 内容会话

| 变量 | 默认 | 作用 |
| --- | --- | --- |
| `NORTHSTAR_PROFILE` | 原游戏安装下存在的 `R2Northstar`，否则仅原版 | Northstar 配置根，包含 `mods/`、`packages/`、`enabledmods.json`。显式空字符串禁用。不是启动器源码目录。 |
| `NORTHSTAR_MAP` | 未指定 | 固定会话地图名，例如 `mp_angel_city`。启用匹配的条件 RPAK/VPK；指定后仅发布该地图场景，避免地图专属覆盖泄漏到其他地图。未指定时，`vpk.json` 声明为 `MapOnly` 的包仍按其声明地图预载（与 `Preload` 等效）并留下诊断。切换需要新进程。 |

例如在启动前设置 `$env:SBOX_TITANFALL2_NORTHSTAR_PROFILE = "D:\northstar"`。源码参考 `D:\northstar\NorthstarLauncher-main`；该目录本身不作为 Mod profile。

诊断命令：`titanfall2_content_stats`；`titanfall2_content_list "models/" 0 100`；`titanfall2_content_explain "models/example.mdl"`。查询 RPAK 材质时使用虚拟资源名并传 `matl`，纹理传 `txtr`。原始目录与引擎目录分离；列出脚本不意味着执行脚本。
