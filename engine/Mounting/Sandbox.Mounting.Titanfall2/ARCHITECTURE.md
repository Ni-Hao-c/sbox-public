# Titanfall 2 挂载：技术参考

本文记录挂载的数据流、零售版文件格式中确认过的结论、对象所有权规则和流式加载预算。功能概览和使用方法见 [README.md](README.md)，环境变量见 [ENVIRONMENT_SWITCHES.md](ENVIRONMENT_SWITCHES.md)。

文中"零售版"指 Steam 上发行的 Titanfall 2 客户端及其资源文件。

## 1. 分层和线程

代码分三层，依赖方向只能从上到下：

1. **Formats**（[Formats/](Formats)）：纯二进制解析，输入字节，输出普通 .NET 对象。这一层不创建任何引擎资源，可以在后台线程运行，也可以在单元测试中直接调用。
2. **Resource**（[Resource/](Resource)）：资源加载器和场景组件。这一层把解析结果转换成 `Texture`、`Material`、`Model`、`SoundFile` 和场景对象。
3. **场景**：`MapLoader.BuildScene` 把组件装配成可序列化的场景。

线程规则：

- 后台线程只读取文件、解压和准备 CPU 数据。
- `Texture`、`Material`、`Mesh`、`Model` 和场景对象只在主线程创建。
- 材质预取（[Resource/MaterialPrefetch.cs](Resource/MaterialPrefetch.cs)）最多同时准备 4 个材质，已准备但未消费的数据上限为 128 MB。

挂载程序集不会经过 s&box 的编译器改写，因此有以下约定：

- 组件需要每帧回调时，显式实现 `Sandbox.Internal.IUpdateSubscriber`、`IFixedUpdateSubscriber` 或 `IPreRenderSubscriber`。
- 挂载程序集不会自动加入游戏的 TypeLibrary。`EnsureRuntimeTypesRegistered` 在初始化、挂载和每次构建场景前，把带 `[Library]` 的类型注册到当前 TypeLibrary，这样挂载场景中的组件才能序列化和还原。
- `[ConCmd]` 不会自动注册。`Titanfall2CollisionDiagnostics.RegisterConsoleCommands` 在挂载时通过反射调用 `ConVarSystem.AddAssembly` 注册整个程序集，卸载时移除。

## 2. 源码地图

| 文件 | 职责 |
|------|------|
| [GameMount.cs](GameMount.cs) | 安装检测、资源注册、卸载清理。 |
| [Titanfall2StreamingSettings.cs](Titanfall2StreamingSettings.cs) | 环境变量读取。 |
| [Formats/Vpk/VpkArchive.cs](Formats/Vpk/VpkArchive.cs) | VPK 目录树和数据块。 |
| [Formats/Rpak/](Formats/Rpak) | RPAK 解压、资源表、StarPak 索引、纹理解码、DXBC 反射。 |
| [Formats/Bsp/](Formats/Bsp) | rBSP lump、CM 碰撞、光照、探针、雾体积、光照环境、实体文本。 |
| [Formats/Mdl/](Formats/Mdl) | MDL v53、VTX、VVD、VPHY、动画。 |
| [Formats/Miles/](Formats/Miles) | Miles 银行索引、BCF 解码、声音事件会话。 |
| [Resource/Map.cs](Resource/Map.cs) 及 `Map.*.cs` | 地图场景构建。 |
| [Resource/StaticPropStreamer.cs](Resource/StaticPropStreamer.cs) 及分部文件 | 静态道具加载、批次、可见性、碰撞流式。 |
| [Resource/MountedScenePerfKit.cs](Resource/MountedScenePerfKit.cs) | 与游戏无关的实例批次、世界聚合和剔除工具，命名空间 `Sandbox.Mounting.PerfKit`。 |
| [Resource/Model.cs](Resource/Model.cs)、[Resource/Material.cs](Resource/Material.cs)、[Resource/Texture.cs](Resource/Texture.cs) | 资源加载器。 |
| [Resource/ProcessedCache.cs](Resource/ProcessedCache.cs) | 磁盘处理缓存。 |

## 3. 资源注册和归档

`Mount` 按以下顺序注册，先注册的名字优先：

1. RPAK 资源。文件名以 `(NN)` 结尾的补丁包按序号从大到小排在最前，同一序号内 `common.rpak` 优先，其余按文件名排序。
2. VPK 资源。只注册 RPAK 没有提供的名字。
3. `skies/skycube.vmat`（挂载生成）。
4. `r2/sound/general.mbnk` 中的 Miles 声音。

### VPK

- 目录树和数据区间都做边界检查，字符串必须有明确的终止符。
- 数据来源由条目的归档索引决定：`0x7FFF` 表示目录文件自身，其他值表示 `_NNN.vpk` 分卷。外部分卷读取失败时报错，不返回空数据。
- 本地化目录 `englishclient_*.vpk` 的数据分卷映射到 `client_*.vpk`。
- 压缩块使用 LZHAM，字典大小为 2^20，由原生桥 `titanfall2_lzham.dll` 解压。
- `depot/` 路径下的镜像条目不注册。

### RPAK 和 StarPak

- 纹理的低分辨率 mip 在 RPAK 中，高分辨率 mip 在 StarPak 中。
- StarPak 索引要求每个条目都位于数据区内。出现重复或越界条目时整个索引作废，并且不缓存这次结果。
- 着色器使用 DXBC 反射得到常量缓冲区和纹理寄存器布局。反射数据中没有 `D3D_SVF_USED` 标志的变量视为未使用。

### 纹理

- 纹理边长上限由 `SBOX_TITANFALL2_TEXTURE_MAX` 决定，超出的高分辨率 mip 不读取。
- BC 压缩纹理只上传到 4×4 的 mip 级别，更小的级别不上传（[Resource/TextureMipUpload.cs](Resource/TextureMipUpload.cs)）。
- VTF 动画纹理额外注册为一张图集（`animatedVtfAtlas`），图集带完整 mip 链。

## 4. 地图构建流程

`MapLoader.BuildScene` 的步骤：

1. 读取 BSP，解析世界几何、CM 碰撞、光照贴图和静态道具光照探针。专用服务器不解析光照。
2. 判断 CM 碰撞是否完整：不为空，且没有被拒绝的图元。专用服务器在不完整时抛出异常。客户端在不完整时，从渲染几何构建备用碰撞。
3. 构建世界渲染几何：
   - 不透明几何聚合成少量大网格。
   - 半透明、贴花、godray、水面和只投射阴影的几何各自生成 `ModelRenderer`，水面使用 `Titanfall2WaterRenderer`。
4. 创建碰撞：
   - 按 contents 分组的 CM 碰撞体，配合 `Titanfall2CollisionManifest` 使用。
   - 或者使用备用碰撞。
5. 添加 `Titanfall2DeferredNavMeshBuilder`。
6. 非专用服务器上继续创建：
   - 环境：太阳、雾、显示后处理、颜色校正
   - 局部光源控制器
   - 立方体贴图探针
   - 3D 天空盒
7. 完整场景创建出生点和 `Titanfall2MapCamera`。纯世界场景不创建。
8. 创建粒子、地图音频（专用服务器除外）和动态道具（纯世界场景除外）。
9. 创建 `Titanfall2StaticPropStreamer`。纯世界场景和专用服务器上 `KeepAllCollisions = true`，并添加 `Titanfall2WorldReady`。

坐标直接沿用 Source 约定：XYZ 不变换，1 单位 = 1 英寸。实体角度按 `new Angles( pitch, yaw, roll )` 转换为旋转。

### 实体来源

零售版把大部分实体放在 BSP 之外的分区文件中：

| 分区文件 | 内容 |
|----------|------|
| `<map>_env.ent` | 环境实体 |
| `<map>_spawn.ent` | 出生点 |
| `<map>_fx.ent` | 粒子 |
| `<map>_script.ent` | 脚本实体 |
| `<map>_snd.ent` | 声音 |

BSP 自身的 ENTITIES lump 很小，但总是包含 `worldspawn`，以及分区文件中没有重复的体积实体（例如 `fog_volume`）。环境分区缺失时，退回到 ENTITIES lump。

### 就绪信号

`Titanfall2WorldReady`（[Resource/WorldReady.cs](Resource/WorldReady.cs)）在 `worldspawn` 上维护标签 `ttf2_map_ready`：

- 标签在 `OnLoad` 和 `OnEnabled` 时清除。
- 发布条件：
  - 道具流式组件已完成常驻碰撞预加载。
  - 世界碰撞体在物理世界中有带形状的物理体。
  - 物理世界中道具碰撞体的数量等于预期数量。
  - 碰撞清单中每个内容分组都有物理体。
- 发布后每帧确认缓存的物理体仍然有效，条件失效时立即撤销标签。
- 遍历物理世界的检查最多每 0.25 秒执行一次，碰撞清单检查也在这个节流之后执行。
- 道具数据损坏时，常驻预加载不会完成，标签也不会发布。

## 5. rBSP 格式要点

### CM 碰撞

代码在 [Formats/Bsp/Titanfall2BspCollisionReader.cs](Formats/Bsp/Titanfall2BspCollisionReader.cs)。

- 每个碰撞图元引用是 32 位：
  - 位 0–7：contents 表索引
  - 位 8–28：21 位图元索引
  - 位 29–31：类型（0 = brush，2 = tricoll，3 = 静态道具）
- 模型 0（世界）使用几何集合的前缀 `[0, model1.first_geo_set)`。模型 N 使用 `CM_GRID_CELLS[grid_w * grid_h + N]`。
- 去重以完整的原始引用为键。
- 损坏的图元会被拒绝并计入 `RejectedPrimitiveCount`。
- 边数超过 128 的 brush 视为损坏。
- tricoll 的三角形绕序会转换为 s&box 的约定。碰撞是单面的。
- 如果所有分组的 contents 都为 0，地图加载失败（`InvalidDataException`），不会生成一个没有固体的世界。

### contents 和检测掩码

`Titanfall2Contents` 和 `Titanfall2TraceMask`（[Resource/CollisionContents.cs](Resource/CollisionContents.cs)）的值取自零售版 client.dll 的脚本常量。常用值：

| 名称 | 值 |
|------|----|
| `PlayerSolid` 掩码 | `0x0201400B` |
| `TitanSolid` 掩码 | `0x0220400B` |
| `Shot` 掩码 | `0x46404033` |
| `PlayerClip` | `0x10000` |
| `MonsterClip` | `0x20000` |
| `NoClimb` | `0x100000` |
| `TitanClip` | `0x200000` |
| `BulletClip` | `0x400000` |
| `NoGrapple` | `0x800` |

每个碰撞对象的 `ModelCollider` 在 contents 与 `PlayerSolid` 掩码没有交集时设为触发器。这样普通 s&box 移动只被玩家固体阻挡，其他掩码通过 `Titanfall2CollisionQueries.Run` 查询。

`Titanfall2CollisionQueries.Run` 的实现细节：

- `PhysicsTrace` 的"任一标签"查询最多使用 8 个标签，因此掩码的置位被拆成每组 8 个标签，每组单独检测，取最近的命中。
- 拆分结果按掩码缓存。
- 空掩码不命中任何导入的碰撞。
- `includeOtherObjects` 为真时，还会检测场景中的普通物体。

### 静态道具 contents

sprp v13 中与碰撞有关的字段：

| 偏移 | 字段 |
|------|------|
| `+0x1e` | solid 类型 |
| `+0x38` | Reserved |
| `+0x3c` | Remove 掩码 |

solid 类型不为 0 时，有效 contents 由 `Titanfall2CollisionSource.StaticPropContents` 按零售版 engine.dll 的初始化逻辑计算，起点是 MDL contents：

1. 为 0 或含 `Solid` 位时：清除位 0，OR `0x00EB0280`。
2. 含 `Window` 位时：清除 `0x00080002`，OR `0x00E30240`。
3. 含 `Grate` 位时：清除 `0x00480008`，OR `0x00A30240`。
4. 最后与 `~Remove` 相与。

结果为 0 的道具不创建碰撞。

可读标签由 contents 推导：

- `playerclip` 和 `titanclip` 只在对象不阻挡子弹时添加。固体道具展开后带有全部 clip 位，不应被当作 clip。
- `nograpple` 对应 `NoGrapple` 位。
- `nowallrun` 对应 `NoClimb` 位。这一对应是推断，没有对照零售版墙跑代码确认。

### 光照贴图和局部光源

- `WORLD_LIGHTS` 有 v1、v2、v3 三种记录，大小分别为 100、108、112 字节。强度是线性 HDR 值，可以直接用于着色，不需要按 LDR 限幅。
- 实时光照页表在 lump 122 中，每页 63 个 `short`。
- 光照贴图的 t35 通道每个像素存 4 个 6 位光源索引和一个 8 位页号，索引 63 表示没有光源。t36 存储可双线性过滤的遮挡值。
- 页表中的索引指向 `flags & 8` 的光源组成的紧凑列表，不是 `WORLD_LIGHTS` 的原始下标（[Formats/Bsp/Titanfall2WorldLight.cs](Formats/Bsp/Titanfall2WorldLight.cs)）。
- 地形按像素选择太阳遮挡通道。静态道具从光照探针获取太阳可见性。
- 每个着色点最多计算 4 盏局部光源。光源数据通过 `Titanfall2LocalLightController` 上传为 GPU 缓冲区，不创建引擎的 `PointLight`。

### 静态道具光照探针

- 系数是带符号的 Q13 定点数（乘以 1/8192），按静态道具索引查找。
- 每个探针上传为 80 字节的 GPU 记录：3 个 `float4` RGB 系数、天空方向和太阳可见性、标志位、4 个字节权重和 4 个 `ushort` 光源 ID。
- 着色公式是 `max( dot( float4( n, 1 ), c ), 0 )` 加天空项。
- 原生探针体积的面顺序是 +X、+Y、+Z、-X、-Y、-Z。

### 光照环境

- `light_environment_volume` 由局部包围盒和一组凸平面定义。
- 不在任何体积内的位置，优先使用没有绑定体积的 `light_environment`。
- `Titanfall2EnvironmentController` 跟随当前相机，通过场景 RenderAttributes 更新太阳和天空颜色。

### 雾

- `fog_volume` 的 `*N` 模型由 CM 数据重建为带优先级的凸体并集（[Formats/Bsp/Titanfall2BspFogVolumes.cs](Formats/Bsp/Titanfall2BspFogVolumes.cs)）。
- 默认雾控制器取第一个 `env_fog_controller`，存在 `master` 控制器时取最后一个 `master`。
- 雾体积按优先级从高到低检查，`fogenable=0` 的体积清除雾。
- 选择雾时使用 `Camera.View.Position`。
- 3D 天空盒在 `useworldfog` 为真时使用同一份雾参数。

### PVS

- rBSP 包含 CellBSP 树和由 portal 连接的可见性单元。挂载最多支持 128 个单元（用 128 位掩码表示）。
- 道具可见性从相机所在单元沿 portal 遍历，最多 16 跳。
- 没有有效单元掩码的道具始终可见；批次中任一成员可见时，整个批次可见。

## 6. 材质和着色器

着色器在 `game/mount/titanfall2/Assets/shaders/` 下。

材质着色器按零售版着色器语义选择，不按材质名猜测，例如水面按语义选择，不看名字中是否有 "water"。

### 层混合

- 层混合（Lyr）权重：`saturate( (ramp + 1) * vertexAlpha + ramp * (mask - 1) )`。
- 半透明层（Lyrtrans）覆盖率：`1 - weight`，再乘以 albedo alpha 和 opacity。
- 层法线的斜率解码：`xy * 2.867661 - 1.439453`，Z 取 1。

### 纹理槽位

- UV 选择字符串按绑定纹理寄存器的顺序对应。
- `Emul` 紧跟在 `Emission` 之后。
- `EmissiveMultiply` 和 `Cavity` 是两个独立语义。`Cavity` 只乘在镜面项上，中性值为 1。
- RPAK 的 RGB 镜面贴图直接作为 F0 使用。

### 渲染通道和 VMT 代理

- 深度/法线预通道在计算颜色和雾之前返回 `DepthNormals::Output`。
- VMT `TextureScroll` 代理应用 `texturescale`，并用 `(scale - 1) / 2` 的偏移补偿缩放中心。
- `Sine` 代理使用 `time - timeoffset`，只把周期 0 改成 1。

### 大气卡片

大气卡片材质的淡出：

- 距离淡出：`saturate( d * scale + bias )`，`scale` 和 `bias` 位于材质常量的 `0x90` 和 `0x94`。
- 视角淡出：`lerp( inner, outer, pow( 1 - |n·v|, exp ) )`。
- 背面剔除跟随材质的 `DoubleSided`。

### 远景（vista）

- 远景着色器按零售版 `c_maxLightingValue` 的方式限制峰值亮度。这个常量是引擎的相机参数，不存储在资源中，挂载默认使用 1。
- HDR 行星和太阳贴图另有固定的强度和峰值限制。

## 7. 显示管线

`Titanfall2Tonemapping`（[Resource/Tonemapping.cs](Resource/Tonemapping.cs)）挂在一个无限范围、优先级 -100 的 `PostProcessVolume` 上，所以游戏自己的后处理体积可以覆盖它。函数定义在 `titanfall2_display.hlsl`。

### 曝光

1. 对颜色缓冲区做 7 级对数亮度降采样。
2. 由平均 log2 亮度计算目标曝光：`L = exp2( meanLog )`，`target = (1.03 - 2 / (ln( L + 1.0002 ) + 2)) / L * 2^EV`。`EV` 是 `SBOX_TITANFALL2_EXPOSURE_EV`，默认 0。
3. 每帧按相对差值向目标逼近，速率 0.1（零售版 `mat_autoexposure_speed`）。
4. 结果限制在 0.5–2。
5. 每个相机有独立的曝光历史。

挂载在渲染时关闭相机自带的自动曝光，并记录原来的设置，组件移除时恢复。

### 显示曲线

零售版 `engine_post` 的默认分支：`x * (10x + 0.3) / (x * (10x + 0.5) + 1.5)`，输入和输出都是线性值。

### 泛光

1. 预过滤：在 1/4 分辨率上计算 `pow( max( rgb - cutoff, 0 ), exponent )`。
2. 模糊：水平和垂直两次，每次 13 个双线性采样点，覆盖 25 像素，中心权重 0.122765。
3. 合成：乘以 `0.5 * BloomStrength` 加到场景颜色上，然后应用曝光和显示曲线。

默认强度 0.15、阈值 1、指数 1，这些是挂载的校准值，不是零售版数值。零售版的 wide 和 streak 分支没有实现。

### 颜色校正

- `color_correction` 实体指向一个 32³ RGB 查找表文件（98,304 字节），例如 `materials/correction/sp_beacon.raw`。
- 只加载全局启用的实体，即没有 `StartDisabled`，且没有 `minfalloff`/`maxfalloff`。
- 权重取 `maxweight`。

## 8. 模型

- 每个 body part 默认选择模型 0。`Mdl53BodyPart.Base` 是打包乘数，不是默认索引。
- VTX strip 的 `IndexOffset` 是组内索引数组的元素下标，不是字节偏移。
- VVD 修正表的区间越界时报错。内嵌 VTX 的解析错误向上传递，不静默生成空模型。
- 碰撞来源的优先级：VPHY 凸包，然后是 hitbox，最后是渲染网格。超过 8192 个三角形的渲染网格用一个包围凸包代替。
- 碰撞来源写入模型数据 `titanfall2_collision`（`Titanfall2ModelCollisionData`），诊断命令从这里读取。
- 每个 hitbox 带标签 `titanfall2_hitgroup_<n>`。critical override 不为 0 的 hitbox 还带 `titanfall2_critical_override`。
- 每个 MDL 还注册一个静态实例模型（`.t2static`），它在一个模型中包含全部 LOD，供实例批次按屏幕尺寸切换。
- 动画来自模型自身和它 include 的模型，按骨骼名称映射到目标骨架。每个序列只取第一个动画，并且总是先添加 `bindpose`。
- 模型缓存键包含挂载程序集的模块 ID（`ContentFingerprint`），重新编译挂载会使旧缓存失效。

## 9. 静态道具批次

- 同一模型、同一批次簇（4×4 个 512 单位单元）的道具组成一个实例批次。
- 索引缓冲区：
  - 恒等索引缓冲区只上传一次，按前缀复用。
  - LOD 重排和可见子集使用动态缓冲区。
  - 批次销毁时释放它的 3 个 GPU 缓冲区。
- 带光照探针的批次（`Titanfall2ProbeInstanceBatch`）在深度预通道中跳过探针绑定（`SkipDepthProbeBindings`），前向和阴影通道绑定全部数据。
- 场景中序列化的道具数据带魔数和版本号，读取时检查版本范围，并按实际字节数限制解码出的数量。

## 10. 粒子

- PCF 库在加载画面之后分帧解析。地图粒子实例在相机附近分帧激活。
- 名称包含 `sun`、`star`、`sky` 或 `weather` 的效果视为全局效果，不受激活半径限制。
- `orientation_type` 1 表示竖直公告板，2 表示世界 XY 平面。
- 支持以下渲染器和功能：
  - 绳索渲染器（`Titanfall2ParticleRopeRenderer`）
  - 速度拖尾
  - 带 `$ramptexture` 的 SpriteCard（`Titanfall2ParticleRampRenderer`）
- 控制点编号范围是 0–63，来自实体键 `cpoint1`..`cpoint63`。游戏代码可以通过 `Titanfall2ParticleControlPoints.SetControlPoint` 更新控制点。
- 每个系统遵守 PCF 中的最大绘制距离和"不可见时休眠"时间。唤醒的循环效果会预热，避免重新出现时是空的。

## 11. 音频

- `general.mbnk` 中的 BCF 声音由 `titanfall2_miles.dll` 调用玩家本机的 `mileswin64.dll` 和 `binkawin64.dll` 解码。
- 声音事件通过 `Titanfall2SoundEventPlayer`（组件名 `titanfall2_sound_event`）或 `CreateSoundEventSession` 播放，使用同一个原生桥中的 Miles 事件 API。
- 原生桥在关闭会话时，如果运行时导出了 `MilesBankUnload`，就先卸载银行；缺少这个导出时跳过。
- 地图音频（[Resource/MapAudio.cs](Resource/MapAudio.cs)）来自 `_snd.ent`：
  - `ambient_generic` 生成点声源。零售地图的 `radius` 都是 0，因此使用默认半径 1024。
  - `trigger_soundscape` 生成球形 `SoundscapeTrigger`。
  - 播放依赖场景中的 `Sound.Listener`。

## 12. 生命周期和所有权

- 缓存的 `SceneFile` 中，生成的世界模型和光照贴图没有磁盘加载器。`MapLoader` 用 `_cachedWorldBuild` 保持这些资源存活，存活时间与 SceneLoader 的场景缓存相同。否则关闭最后一个场景后，GC 会让下一次打开得到错误模型。
- `Titanfall2SceneResourceOwner` 只释放传给它的资源。共享的挂载缓存属于挂载本身，编辑器或运行时克隆的场景被销毁时，这些缓存必须继续存在。
- 共享粒子纹理在挂载卸载时只解除引用，不调用 `Dispose`，因为仍在运行的场景实例可能还在使用它们。
- `Shutdown` 的清理工作：
  - 关闭声音事件会话。
  - 注销控制台命令。
  - 释放内联模型源。
  - 清空所有路径、源和元数据字典。
  - 清空 StarPak 索引。
  - 后台线程也会写入的字典，在它们各自的锁内清空。
- 导航状态按 `Scene` 共享（`ConditionalWeakTable`）。第一张地图记录宿主原来的导航设置，最后一张地图离开时恢复。
- `Titanfall2Map` 只记录并销毁它自己创建的根对象和相机。
  - 加载时深拷贝场景 JSON，在 `Scene.BatchGroup()` 中调用 `SceneUtility.MakeIdGuidsUnique`，使多个根对象之间的引用仍能解析。
  - 部分反序列化失败时，销毁已创建的根对象。
- 专用服务器强制使用 CM 碰撞，并跳过环境、粒子、音频和渲染器。

## 13. 流式加载预算

这些数值是 [Resource/StaticPropStreamer.cs](Resource/StaticPropStreamer.cs) 中的常量：

| 阶段 | 预算 |
|------|------|
| 加载画面预加载 | 每次让出最多 512 个道具、35 ms，最多 4 个碰撞体 |
| 加载后补全 | 每帧最多 16 个道具、3 ms |
| 运行时可见性 | 每帧最多 16 个单元、1.5 ms |
| 碰撞流式 | 每帧最多创建或释放 2 个碰撞体、1 ms；1536 单位内启用，2560 单位外释放 |
| 距离保留带 | 道具进入绘制距离后，再多保留 512 单位 |
| 大型道具 | 半径不小于 256 的道具不做距离剔除 |
| 阴影 | 开启道具阴影时，半径小于 48 的道具不投射阴影 |

预加载阶段没有创建完的道具碰撞，留给运行时的碰撞流式处理。纯世界场景要求全部常驻碰撞都创建完成后才发布就绪标签。

## 14. 处理缓存

- 缓存文件格式：
  - 文件头：魔数 `T2FC`、版本号、32 字节 SHA-256 键。
  - 正文：GZip 压缩的数据。
- 文件写入临时文件后原子替换。
- 读取时校验文件头，并读到 GZip 结尾以检查 CRC 和截断。任何异常都按未命中处理。
- 缓存种类：`model`、`texture`、`material`、`propfade`。地图几何和光照贴图不缓存。
- 缓存键包含两部分：
  - `ContentFingerprint`：挂载程序集的模块 ID，加上游戏文件的大小和修改时间。
  - 资源自身的来源标识。
- 命中时更新文件修改时间，每个文件每次会话只更新一次，供 LRU 淘汰使用。
- 淘汰在每 64 次写入时检查一次，删除最旧的文件，直到总大小不超过 `SBOX_TITANFALL2_CACHE_MB`。
- 缓存只保存派生数据，从不保存引擎或 GPU 对象。
