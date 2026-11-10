# 挂载框架研究依据

调查日期：2026-11-06。主文档：[可复用游戏挂载框架设计](MOUNT_FRAMEWORK_DESIGN.zh-CN.md)。

本文把“查看过的源码行为”和“由此提出的设计判断”分开。外部项目没有在本任务中编译或运行；不能把存在某个类、README 的支持列表或示例截图当作性能和完整性证明。

## 1. 官方入口

从 [s&box 文档索引](https://sbox.game/llms.txt) 开始，核对 [Game Mounts](https://sbox.game/dev/doc/game-mounts/) 与 [Creating Mounts 源文档](https://github.com/Facepunch/sbox-docs/blob/master/docs/game-mounts/creating-mounts.md)。官方文档说明 `BaseGameMount` 负责发现和注册，`ResourceLoader` 负责按需转换，挂载资源使用 `mount://`。设计继续使用这条入口。

官方概览同时指出挂载资产是运行期资源，用户需要原游戏安装；设计不以预先导出并随游戏发布所有原作资产为必要前提。派生缓存是本地可重建的实现细节。

本地源码优先用于确认具体签名、可访问性与限制。当前工作树包含未提交修改，外部文档与当前代码可能不完全一致。

## 2. 公开 GitHub 项目

使用 GitHub 仓库搜索 `sbox mount`，再读取选中项目的目录树和关键文件。搜索范围不是完整生态普查，也不能据此断言没有其他优秀实现。以下提交是本次实际读取时的树身份，用固定链接便于复核。

### 2.1 GameMaker：一个引擎族，多个游戏配置

- 仓库：[chrisspieler/sbox-gamemaker-mount](https://github.com/chrisspieler/sbox-gamemaker-mount)。
- 固定提交：[0a576db0feff7055c198165014ebf0cc08646ecd](https://github.com/chrisspieler/sbox-gamemaker-mount/tree/0a576db0feff7055c198165014ebf0cc08646ecd)。
- 已读：[GameMakerMount.cs](https://github.com/chrisspieler/sbox-gamemaker-mount/blob/0a576db0feff7055c198165014ebf0cc08646ecd/Editor/GameMakerMount/GameMakerMount.cs)、[DeltaruneMount.cs](https://github.com/chrisspieler/sbox-gamemaker-mount/blob/0a576db0feff7055c198165014ebf0cc08646ecd/Editor/GameMakerMount/Games/Deltarune/DeltaruneMount.cs)、[LICENSE](https://github.com/chrisspieler/sbox-gamemaker-mount/blob/0a576db0feff7055c198165014ebf0cc08646ecd/LICENSE)。

观察：公共基类处理 archive、纹理/声音/sprite 记录注册与逻辑路径；Deltarune 子类主要指定 AppId、多 archive 和音乐目录。源码把容器记录映射为资产，不要求一个资产必须等于一个磁盘文件。所查看的 LICENSE 为 MIT。

设计判断：采用“引擎族能力 + 游戏配置”，并支持 LogicalAssetProvider。不能把这些代码直接推广为通用大地图框架：查看的入口在 Mount 中刷新 archives，未证明受预算约束的加载、多场景所有权或 3D 帧时间表现。

### 2.2 Overgrowth：拆分安装发现和资源描述

- 仓库：[chrisspieler/sbox-overgrowth-mount](https://github.com/chrisspieler/sbox-overgrowth-mount)。
- 固定提交：[f4f2b7e3d9f1834f1c519b9d1717e647b22b9f07](https://github.com/chrisspieler/sbox-overgrowth-mount/tree/f4f2b7e3d9f1834f1c519b9d1717e647b22b9f07)。
- 已读：[OvergrowthMount.cs](https://github.com/chrisspieler/sbox-overgrowth-mount/blob/f4f2b7e3d9f1834f1c519b9d1717e647b22b9f07/Editor/OvergrowthMount.cs)、[SteamGameMount.cs](https://github.com/chrisspieler/sbox-overgrowth-mount/blob/f4f2b7e3d9f1834f1c519b9d1717e647b22b9f07/Editor/Utilities/SteamGameMount.cs)。

观察：基类发现 Steam 目录，把资源命令转换为 `MountContext.Add`；游戏层从 DDS 和引用模型/贴图的 XML 对象构造资源命令。

设计判断：内容发现、资产描述和引擎注册应该分离。不能照搬全量 XML 反序列化的时机；大内容库需要目录缓存和按需元数据策略。该项目的 Editor 项目部署方式也不等于当前 `game/mount/<module>/<module>.dll` 的正式模块部署契约。

### 2.3 Bloodlines：明确的补丁与 loose 覆盖

- 仓库：[atrblizzard/vtmb-sbox-mounter](https://github.com/atrblizzard/vtmb-sbox-mounter)。
- 固定提交：[4cd3888079eb8224f67333850b70cb7f2784e5da](https://github.com/atrblizzard/vtmb-sbox-mounter/tree/4cd3888079eb8224f67333850b70cb7f2784e5da)。
- 已读：[VtmbMount.cs](https://github.com/atrblizzard/vtmb-sbox-mounter/blob/4cd3888079eb8224f67333850b70cb7f2784e5da/Editor/Mount/VtmbMount.cs)、[README](https://github.com/atrblizzard/vtmb-sbox-mounter/blob/4cd3888079eb8224f67333850b70cb7f2784e5da/README.md)。

观察：`GameDirs` 将 Unofficial_Patch 排在 vampire 前面，loose 路径以 TryAdd 决定覆盖；读取先查 loose，再查 VPK。资源注册使用合并后的路径集合。读取辅助方法存在全文件 byte[] 和 MemoryStream 路径。

设计判断：Mod 覆盖不需要改引擎；统一虚拟路径、确定顺序与一致读取入口即可实现基础覆盖。通用框架进一步保留来源候选、Scope 和包读取 lease，并避免把全部数据复制成 byte[] 作为唯一访问方式。

README 自述某些材质组/模型能力有限；本调查没有复现，也不将它当作当前引擎的已确认缺陷。

### 2.4 GTA V：大容器依赖需要预建索引

- 仓库：[cmg-divined/gta5-mount-sbox](https://github.com/cmg-divined/gta5-mount-sbox)。
- 固定提交：[ab5ff79cc8b19ce48a04c37ad59c85035e691e98](https://github.com/cmg-divined/gta5-mount-sbox/tree/ab5ff79cc8b19ce48a04c37ad59c85035e691e98)。
- 已读：[GTA5Mount.cs](https://github.com/cmg-divined/gta5-mount-sbox/blob/ab5ff79cc8b19ce48a04c37ad59c85035e691e98/Editor/GTA5Mount.cs)。

观察：入口维护 RPF 文件、模型 hash、YTD、纹理 parent、archetype 等索引，并持有缓存和加载队列。部分查询有预建表；`FindFileByPath` 仍遍历容器和 entries。一些按 hash 获取资源的方法包含尚未完成的返回 null 分支。

设计判断：资源依赖模型不能局限于“文件旁边还有一张纹理”；需要 GUID/hash、字典子资源和父依赖链。可以借鉴索引方向，但不能以“有缓存/队列类”证明它已经达到高性能，也不把扫描 fallback 搬到通用热路径。

### 2.5 共享格式项目：复用解码器而非复制整个挂载

- 仓库：[chrisspieler/sbox-mount-formats](https://github.com/chrisspieler/sbox-mount-formats)。
- 固定提交：[ce1908d637611c7e4a210d896e3d528ed690dbe0](https://github.com/chrisspieler/sbox-mount-formats/tree/ce1908d637611c7e4a210d896e3d528ed690dbe0)。
- 已读：[README](https://github.com/chrisspieler/sbox-mount-formats/blob/ce1908d637611c7e4a210d896e3d528ed690dbe0/README.md) 和目录树。

观察：项目把多个文件格式与压缩工具集中为共享资源，并在 README 记录第三方来源。文档部分能力列表仍未完成。

设计判断：解码库与挂载生命周期分开维护是正确复用方向。第一版可选择适用解码器，不直接整包引入；复制具体第三方代码前核对文件许可证、依赖和当前 API。其历史 Editor DLL 限制说明不能直接当成今天所有模块的限制。

### 2.6 研究选择

没有选择“换成某个外部项目”作为架构结论。以上源码分别说明了引擎族复用、描述符注册、Mod 覆盖、复杂依赖索引和共享解码库的可行做法；它们没有在本任务中证明跨游戏的完整生命周期、视觉、碰撞和运行期性能要求。

本次没有复制外部代码，也没有引入第三方依赖。

## 3. 本地源码索引

路径均相对于仓库根目录。以下文件在本次调查中读取或按符号检查；没有修改。

| 文件 | 对设计的影响 |
|---|---|
| `engine/Mounting/Sandbox.Mounting/MountedGame/BaseGameMount.cs` | 注册、目录构建、Refresh/Shutdown 时序 |
| `engine/Mounting/Sandbox.Mounting/MountedGame/MountContext.cs` | ref struct、Add、未实现 AddError |
| `engine/Mounting/Sandbox.Mounting/MountedAsset/ResourceLoader.cs` | 原有任务/结果缓存、不可重写的 GetOrCreate 外包装边界 |
| `engine/Sandbox.Engine/Game/Mount/Directory.cs` | DLL 文件名发现、模块 Assets、TryLoad/LoadResource |
| `engine/Sandbox.Engine/Game/Mount/SceneLoader.cs` | 临时场景、sealed Load/Shutdown、SceneFile 与缓存 |
| `engine/Sandbox.Engine/Game/Mount/TextureLoader.DDS.cs` | 保留压缩格式和 mip 的既有构建入口 |
| `engine/Sandbox.Engine/Game/Mount/PrefabBuilder.cs` | 既有 prefab 构建与序列化助手 |
| `engine/Sandbox.Reflection/TypeLibrary/TypeLibrary.cs` | AddAssembly 为 internal，自定义组件注册不能作为公共接口假设 |
| `engine/Sandbox.Engine/Systems/Threads/MainThread.cs` | public Wait/Queue 与内部队列边界 |
| `engine/Sandbox.Engine/Systems/SceneSystem/SceneObject.cs` | 原生对象、延迟 Delete、Batchable |
| `engine/Sandbox.Engine/Systems/SceneSystem/SceneCustomObject.cs` | 自定义渲染入口，不能自动解决场景序列化 |
| `engine/Sandbox.Engine/Systems/SceneSystem/SceneAggregateObject.cs` | 单 mesh/材质、fragment 映射、数量限制 |
| `engine/Sandbox.Engine/Scene/Components/Render/AggregateRenderer.cs` | 原有可序列化聚合组件、fragment bounds/transform、整体 tint |
| `engine/Sandbox.Engine/Scene/Components/Render/ModelRenderer.cs` | 原生 SceneObject 与静态标志 |
| `engine/Sandbox.Engine/Resources/Model/Builder/ModelBuilder.cs` | 运行期模型名称、创建、LOD 距离 |
| `engine/Sandbox.Engine/Resources/Model/Builder/ModelBuilder.Mesh.cs` | mesh、LOD、bodygroup 入口 |
| `engine/Sandbox.Engine/Systems/Render/Graphics.Draw.Mesh.cs` | public 显式实例化、间接实例化 |
| `engine/Sandbox.Engine/Systems/Render/Graphics.Draw.Buffer.cs` | 若干底层 indirect 接口的 internal 限制 |
| `engine/Sandbox.Engine/Systems/Render/CommandList/CommandList.cs` | public draw/compute/barrier 能力，使用前仍需合法运行入口 |
| `engine/Sandbox.Engine/Scene/Components/Collider/ModelCollider.cs` | 复用模型碰撞与引擎物理更新 |

这些引擎文件是只读研究对象，不属于方案中的修改清单。

## 4. Source 1 可提炼的内容

当前本地 Source 1 不是简单的“扫描 VPK 后直接注册”实现：

- [BaseMount.Files.cs](../Sandbox.Mounting.Source1/BaseMount.Files.cs)：目录与归档统一映射、first-wins、prefix/header 读取、index 引用计数与 ContentStream。
- [BaseMount.Content.cs](../Sandbox.Mounting.Source1/BaseMount.Content.cs)：资源依赖的作用域查找、父 content root、最终 mount 路径。
- [BaseMount.Maps.cs](../Sandbox.Mounting.Source1/BaseMount.Maps.cs)：地图 pak 虚拟前缀、父作用域、共享地图导入。
- [BaseMount.cs](../Sandbox.Mounting.Source1/BaseMount.cs)：索引替换、资源注册、原始文件类型到 loader 的映射。
- [IContentArchive.cs](../Sandbox.Mounting.Source1/Formats/IContentArchive.cs)：包枚举与读取接口。
- [Map.Loader.cs](../Sandbox.Mounting.Source1/Resource/Map/Map.Loader.cs)：已有组件构建场景。
- [Map.World.cs](../Sandbox.Mounting.Source1/Resource/Map/Geometry/Map.World.cs)：按材质/光照页组织空间片段，构建 AggregateRenderer 所需模型。
- [Map.Physics.cs](../Sandbox.Mounting.Source1/Resource/Map/Geometry/Map.Physics.cs)：原作碰撞到原生物理、surface 和 pass-audio 分组。
- [Map.Props.cs](../Sandbox.Mounting.Source1/Resource/Map/Entities/Map.Props.cs)：模型、皮肤、静态光照和原有 renderer/physics 组件。

需要保留的思想：统一虚拟路径、查找和打开一致、地图资源隔离、读取时持有旧快照、资源按需转换、原生组件承担运行期工作。

不直接复制成通用核心的部分：Source 特定路径命名、VMT/VTF/MDL/BSP 类型、把导入缓存与原始索引放在同一个 FileIndex、扫描所有根目录的策略、具体 archive/loose 顺序。它们属于游戏适配器和资源服务的不同职责。

## 5. Titanfall 2 作为迁移对象的局部观察

- [GameMount.cs](../Sandbox.Mounting.Titanfall2/GameMount.cs)：有按顺序 claim 名称的注册层，但这些表不能替代全格式一致的内容 resolver；同时存在反射调用 TypeLibrary 注册组件的逻辑。
- [AssetSource.cs](../Sandbox.Mounting.Titanfall2/Formats/AssetSource.cs)：已有 deferred source、sibling/path/prefix 读取接口；LooseAssetSource 的注释明确说明它尚未接入挂载实例化路径。
- [ProcessedCache.cs](../Sandbox.Mounting.Titanfall2/Resource/ProcessedCache.cs)：纯派生数据缓存、原子发布与损坏回退方向可借鉴，缓存身份需提升到完整依赖/Scope/Profile 规则。
- [Map.cs](../Sandbox.Mounting.Titanfall2/Resource/Map.cs)、[SceneResourceOwner.cs](../Sandbox.Mounting.Titanfall2/Resource/SceneResourceOwner.cs)：说明临时构建场景、真实场景与缓存 SceneFile 的生命周期问题不能省略。
- [MountedScenePerfKit.cs](../Sandbox.Mounting.Titanfall2/Resource/MountedScenePerfKit.cs)：有可复用聚合/实例化工具与索引基址、fragment 上限、不可变数组规则，可作为实现审查素材。
- [StaticPropGpuScene.cs](../Sandbox.Mounting.Titanfall2/Resource/StaticPropGpuScene.cs)：包含 GPU buffer、视图输出和独立材质/光照等假设；不适合作为整个跨游戏框架的强制底座。

这些是当前代码结构观察，没有完成 TF2 全面审计，也没有给当前实现判定新的运行期缺陷或性能结论。

## 6. 初次设计交付与验证边界

初次设计交付只新增主设计和本附录，没有改动 C#、shader、工程文件或部署 DLL。当时未执行全仓 build/test/format，因为没有实现代码变化；文档检查覆盖链接、代码围栏和工作区差异。随后实施的 TF2 重构见[实现与验证记录](TITANFALL2_REFACTOR_2026-11-06.zh-CN.md)，不改变本附录对外部项目未经运行验证的边界。

后续实现的第一步是能力探针与独立 VFS 夹具。通过后再进入资源桥接、场景、性能与生命周期验收；文档不是这些验收的替代品。
