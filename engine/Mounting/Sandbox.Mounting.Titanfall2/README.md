# Titanfall 2 挂载

`Titanfall2Mount`（[GameMount.cs](GameMount.cs)）以 `titanfall2` 为标识，把本机 Steam 安装的 Titanfall 2（AppId 1237970）中的资源注册成 s&box 资源。安装目录里同时存在 `r2/paks/Win64` 和 `vpk` 时才视为已安装。所有资源都在加载时从原始游戏文件转换，不需要预先解包。

实现细节、数据格式和生命周期规则见 [ARCHITECTURE.md](ARCHITECTURE.md)。全部环境变量见 [ENVIRONMENT_SWITCHES.md](ENVIRONMENT_SWITCHES.md)。

## 挂载的内容

注册顺序决定同名资源的优先级：先注册 RPAK，VPK 只补充 RPAK 中没有的名字。

| 来源 | 注册为 | 说明 |
|------|--------|------|
| `r2/paks/Win64/*.rpak` 和 StarPak | 纹理（`<name>.vtex`）、材质 | RPAK 的 TXTR/MATL 资源，高分辨率 mip 从 StarPak 读取。补丁包优先，然后是 `common.rpak`。 |
| `vpk/*_dir.vpk` | VMT 材质、VTF 纹理、模型、WAV 声音、场景 | `englishclient_` 目录映射到 `client_` 数据包，`depot/` 下的镜像条目被隐藏。 |
| MDL v53（含内嵌 VTX/VVD/VPHY） | `models/<x>.mdl.vmdl` | 默认 bodygroup 取每个 body part 的模型 0。带有附着点、hitbox set、皮肤（`Skin N`），以及模型本身和 include 模型中的动画序列。另注册一个包含全部 LOD 的静态实例模型，供静态道具批次使用。 |
| `r2/sound/general.mbnk` | `sound/<name>.vsnd` | Miles 银行中的 BCF 声音，通过 `titanfall2_miles.dll` 解码。 |
| PCF | 地图粒子系统 | 由地图实体创建，不单独注册为资源。 |
| rBSP 地图 | `maps/<map>.bsp.scene`、`maps/<map>.bsp.world.scene` | 见下一节。 |
| rBSP 内联模型 | `maps/<map>.bsp/brush/<index>.vmdl` | 门、移动平台等 `*N` 模型。 |

`skies/skycube.vmat` 是挂载生成的天空材质，它把零售版的天空立方体贴图包装成原生天空着色器。

## 地图加载

每张地图注册两个场景，都由 [Resource/Map.cs](Resource/Map.cs) 中的 `MapLoader` 构建：

- `mount://titanfall2/maps/<map>.bsp.scene` 是完整场景。它包含世界几何、碰撞、环境、静态道具、粒子、地图音频、出生点、动态道具和一个旁观相机，可以直接打开运行。
- `mount://titanfall2/maps/<map>.bsp.world.scene` 是纯世界场景。它不创建出生点、相机和动态道具，不强制开启导航，并把所有静态道具碰撞常驻在物理世界中。它适合加载进游戏自己的场景。

在自己的场景里使用地图时，添加 `Titanfall2Map` 组件（[Resource/Titanfall2Map.cs](Resource/Titanfall2Map.cs)），用法与 Hammer 地图的 `MapInstance` 相同：

- `MapName` 是地图名，例如 `mp_angel_city`。末尾的 `.bsp`、`.bsp.scene` 会被去掉。
- `PureWorld` 选择加载 `.world.scene`。
- `SpawnSpectatorCamera` 在场景没有主相机时，在第一个出生点上方创建旁观相机。纯世界模式不创建相机。
- `LoadInEditor` 让编辑器视图也加载地图。默认关闭，因为大地图要预加载数万个静态道具。

地图对象作为不保存的子对象加入场景，组件只销毁它自己创建的对象。`Titanfall2Map.GetAvailableMaps()` 返回当前挂载中所有可用的地图名。

纯世界场景加载完成后，`worldspawn` 对象上的 `Titanfall2WorldReady` 组件会在以下条件都满足时添加标签 `ttf2_map_ready`：世界碰撞体已进入物理世界，所有内容分组都有物理体，常驻道具碰撞全部创建完成。任一条件失效时标签会被移除。游戏代码应在看到这个标签后再放置玩家。

## 渲染

以下功能在默认设置（`SBOX_TITANFALL2_BSP_LIGHTMAPS=1`）下启用。

- **光照贴图和探针**：世界几何使用 BSP 中烘焙的 HDR 光照贴图。静态道具使用按道具索引存储的光照探针。
- **局部光源**：`WORLD_LIGHTS` 中的点光源和聚光灯通过实时光照页表在着色器中计算，每个像素最多 4 盏，不为每盏灯创建引擎光源。
- **太阳和天空**：方向光来自 `light_environment`。地图有多个 `light_environment_volume` 时，`Titanfall2EnvironmentController` 按相机位置切换太阳和天空颜色。
- **雾**：`env_fog_controller` 和 `fog_volume` 生成高度雾和方向雾，雾体积按相机位置选择。
- **3D 天空盒**：远景（vista）模型放进独立的天空盒世界，从地图的 `sky_camera` 观察。
- **水面**：按材质着色器语义识别，使用帧缓冲拷贝渲染。
- **贴花**：BSP 贴花几何单独渲染，不投射阴影。
- **显示变换**：`Titanfall2Tonemapping` 实现零售版的自动曝光和显示曲线，替代引擎的自动曝光。地图有全局 `color_correction` 实体时，加载它的 32³ RGB 查找表；该表是一个挂载体纹理资源，场景只引用它的路径。
- **泛光**：在曝光和显示曲线之前合成。

世界和静态道具默认不投射方向光阴影，由屏幕空间接触阴影补充。`SBOX_TITANFALL2_MAP_SHADOWS` 和 `SBOX_TITANFALL2_PROP_SHADOWS` 可以开启阴影。

## 静态道具

`Titanfall2StaticPropStreamer`（[Resource/StaticPropStreamer.cs](Resource/StaticPropStreamer.cs)）在加载画面期间预加载地图中的全部静态道具，之后它们一直保留到场景销毁。

- **实例化**：相同模型的道具按 4×4 个 512 单位的单元分组，合并成一个实例批次绘制。
- **LOD**：静态实例模型包含 MDL/VTX 自带的全部 LOD。实例批次按每个实例到相机的距离选级（1536 / 4096 / 8192 单位切到 LOD1 / 2 / 3），按级分组绘制。
- **PVS**：沿 rBSP 的 cell/portal 图从相机所在单元向外遍历（最多 16 跳），隐藏不可见单元中的道具。没有有效单元的道具始终可见。
- **淡出距离**：每个道具使用 sprp 或 MDL 中设定的淡出距离，可用 `SBOX_TITANFALL2_PROP_AUTHORED_FADE_SCALE` 整体缩放。
- **全局距离**：超过 `SBOX_TITANFALL2_PROP_RENDER_DISTANCE`（默认 8192）的道具不再绘制。半径不小于 256 的大型道具不受这一限制。

## 碰撞

世界碰撞以 rBSP 的 CM 数据（brush 和 tricoll）为准：

- 碰撞几何按原始 contents 值分组，每组生成一个静态 `ModelCollider`。
- 不阻挡玩家（不含 `PLAYERSOLID` 位）的分组设为触发器，例如子弹阻挡和泰坦阻挡。
- CM 数据缺失或有被拒绝的图元时，客户端退回到经过过滤的渲染几何碰撞，并输出警告。专用服务器要求完整的 CM 碰撞，否则加载失败。

静态道具碰撞来自模型的 VPHY 凸包。没有 VPHY 时使用 hitbox；两者都没有时使用渲染网格，超过 8192 个三角形的网格用一个包围凸包代替。道具的有效 contents 由 sprp 的 solid 字段、MDL contents 和 remove 掩码计算，结果为 0 的道具没有碰撞。完整场景中，道具碰撞只在玩家附近启用：1536 单位内创建，超过 2560 单位释放。纯世界场景和专用服务器保留全部道具碰撞。

导入的碰撞对象带有以下标签：

- `ttf2_contents`
- 每个置位的 contents 位对应一个 `ttf2_content_<bit>`
- 可读标签 `playerclip`、`titanclip`、`nograpple`、`nowallrun`

用原始掩码做射线检测时，调用 `Titanfall2CollisionQueries.Run( trace, Titanfall2TraceMask.Shot )`（[Resource/CollisionQueries.cs](Resource/CollisionQueries.cs)）。`Titanfall2Contents` 和 `Titanfall2TraceMask` 枚举（[Resource/CollisionContents.cs](Resource/CollisionContents.cs)）使用零售版脚本中的常量值。

控制台命令 `titanfall2_collision_look [距离=512] [半径=0]` 从当前相机发出射线或球体扫描，以 JSON 输出命中物体的碰撞来源、contents、形状和标签。它只读取，不修改碰撞。

## 导航

`Titanfall2DeferredNavMeshBuilder`（[Resource/DeferredNavMeshBuilder.cs](Resource/DeferredNavMeshBuilder.cs)）接管挂载地图的导航网格生成。

默认的按需模式下：

- 挂载跳过加载时的整图生成。
- 只使用带 `titanfall2_navmesh` 标签的世界碰撞作为输入。
- 玩家、NavMesh 代理和代理目标周围 `SBOX_TITANFALL2_NAVMESH_RADIUS`（默认 2048）单位内的 tile 在需要时才生成。

宿主场景没有开启导航时，挂载不会修改它。`SBOX_TITANFALL2_DEFERRED_NAVMESH=1` 让完整场景主动开启导航。`SBOX_TITANFALL2_NAVMESH_STATIC_PROPS=1` 把静态道具碰撞也加入导航输入，同时让道具碰撞常驻。同一场景中的多张挂载地图共享导航状态，最后一张地图移除时恢复宿主的设置。

## 模型导出

控制台命令 `titanfall2_export_model <挂载路径> [输出目录] [覆盖=true]` 把挂载的模型导出为可编辑的 DMX 和 VMDL 源文件，代码见 [Resource/ModelExporter.cs](Resource/ModelExporter.cs)。

- 输出目录依次取命令参数、`SBOX_TITANFALL2_EXPORT_ROOT`、当前目录下的 `addons/titanfall2_exports/Assets`。
- 材质导出为 `materials/titanfall2_exports/` 下的占位 `.vmat`，需要在材质编辑器中手动指定纹理。
- DMX 只能存储统一缩放，非统一的骨骼缩放会被省略并给出警告。

示例：

```
titanfall2_export_model "mount://titanfall2/models/robots/stalker/robot_stalker.mdl.vmdl" "D:/sbox/sbox-public/game/addons/myaddon/Assets"
```

## 处理缓存

转换后的模型、纹理、材质描述和道具淡出距离会写入磁盘缓存，代码见 [Resource/ProcessedCache.cs](Resource/ProcessedCache.cs)。

- 默认位置是 `%LOCALAPPDATA%\sbox\mount-cache\titanfall2\processed`，可用 `SBOX_TITANFALL2_CACHE_PATH` 修改。
- 总大小上限为 `SBOX_TITANFALL2_CACHE_MB`（默认 2048），超出时按最近使用时间删除旧文件。
- 缓存键包含挂载程序集的模块 ID 和游戏文件的大小与修改时间，重新编译挂载或更新游戏都会使旧条目失效。
- 读取失败或文件损坏时退回到正常转换。
- 缓存可以随时删除。`SBOX_TITANFALL2_PROCESSED_CACHE=0` 关闭缓存。

地图几何和光照贴图不进入磁盘缓存，每次打开地图时重新构建。

## 源码结构

| 目录 | 内容 |
|------|------|
| [Formats/](Formats) | 纯解析代码，不依赖引擎资源：`Bsp`、`Mdl`、`Miles`、`Pcf`、`Rpak`、`Scripts`、`Vmt`、`Vpk`、`Vtf`。 |
| [Resource/](Resource) | 资源加载器和场景组件：地图、模型、材质、纹理、声音、粒子、静态道具、碰撞、导航、环境和后处理。 |
| [Native/](Native) | 原生桥：[LzhamBridge](Native/LzhamBridge/README.md) 解压 VPK 数据块，[MilesBridge](Native/MilesBridge/README.md) 解码 Miles 音频并播放声音事件。 |
| [Titanfall2StreamingSettings.cs](Titanfall2StreamingSettings.cs) | 读取 `SBOX_TITANFALL2_*` 环境变量。 |

着色器在 `game/mount/titanfall2/Assets/shaders/` 下，文件名以 `titanfall2_` 开头。

## 构建和测试

在挂载目录中构建：

```powershell
dotnet build -c Release
```

构建完成后，`CopyOutputDll` 目标会把输出复制到 `game\mount\titanfall2\titanfall2.dll`。设置 `SkipCopyOutputDll=true` 可以跳过复制。两个原生 DLL 由 `Native\*\build.ps1` 单独构建，默认输出到同一目录。

修改着色器后，在 `game\` 目录下运行 `bin\managed\ShaderCompiler.exe` 编译。

测试需要 `FACEPUNCH_ENGINE` 指向 `game\` 目录。在仓库根目录运行：

```powershell
$env:FACEPUNCH_ENGINE = "D:\sbox\sbox-public\game"
dotnet test engine\Tests\Sandbox.Test.Engine --filter FullyQualifiedName~Titanfall2
dotnet test engine\Tests\Sandbox.Test.Integration --filter FullyQualifiedName~Titanfall2
```

集成测试需要本机安装 Titanfall 2。

## 已知限制

- **碰撞**：
  - 只区分 contents 位，不处理 surfaceprop，世界表面没有对应的 s&box `Surface`。
  - `nowallrun` 标签由 `NOCLIMB` 位推断，没有对照零售版的墙跑代码确认。
  - 没有 VPHY 和 hitbox 的模型，其碰撞是渲染网格或包围凸包的近似。
- **游戏逻辑**：门、触发器、脚本和网络同步属于游戏代码，挂载只提供几何、碰撞和实体数据。
- **动画**：每个序列只导入它的第一个动画，混合序列的其余动画会被忽略。
- **粒子**：
  - 只实现了 PCF operator 和 initializer 的一部分。
  - 天空盒世界只承载 vista 模型，不包含粒子。
  - 控制点在加载时从实体数据确定，运行时移动需要游戏代码调用 `SetControlPoint`。
- **音频**：
  - 每个 `trigger_soundscape` 区域只播放一个循环声音，用引擎的球形 `SoundscapeTrigger` 实现，同一时间只有一个区域生效。
  - `ambient_generic` 发声点数量有上限（默认 12 个）。
  - 没有 DSP 效果。
- **雾和后处理**：
  - 雾、光照环境和后处理在每个场景中按单个相机选择，不在多个相机或多张地图之间协调。
  - 不支持脚本或网络驱动的雾渐变，也不支持局部或脚本控制的颜色校正。
  - 泛光只有基础的 prefilter 和模糊，强度、阈值和指数是校准值，不读取地图设置。
  - 地图实体中的曝光控制不会被导入。
- **材质**：VMT 代理只实现了一部分，不支持任意 2D 纹理变换和由实体驱动的输入。
- **显存**：没有跨地图的 GPU 资源 LRU 或显存预算。纹理上限由 `SBOX_TITANFALL2_TEXTURE_MAX`（默认 1024）决定，运行中不会升级到更高的 mip。
