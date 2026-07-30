# Titanfall 2 挂载系统完善计划

## 当前实施进度（2026-07-22）

- 阶段一：已加入模型碰撞分类统计与固定自检样本；纹理到材质的 GUID/路径反向依赖已由统一材质描述层维护并纳入自检统计。
- 阶段二：已完成 VPHY、hitbox、低面数渲染网格和高面数简化包围盒四级碰撞路径。对解包模型均匀抽样 202/1812 个：106 个 VPHY、8 个 hitbox、88 个 fallback，0 个解析错误，213 个 VPHY hull 均通过有限坐标和索引校验。
- 阶段三：396/396 个 PCF 解析成功，共 10437 个定义；天使城与战争游戏的 25 个唯一 `effect_name` 全部命中。已支持 Sprite、Trail 近似、模型粒子、粒子光源、子系统、VTF header 动画帧与 particle-sheet sequence 0；`Graph Scalar` 的 Alpha/Alpha2、Radius、Rotation、Trail 曲线会烘焙为 s&box 生命周期曲线，绝对时间循环会按寿命展开，所有标记为 `mute` 的模块都会跳过。
- 当前明确跳过：需要独立 3D skybox `SceneWorld` 的粒子、控制点 Rope/Beam、屏幕空间粒子。这些项目不能由普通场景组件可靠还原，先避免错误渲染和死循环。
- 阶段四：已建立 `Titanfall2MaterialDescriptor`，统一保存混合、深度、剔除、纹理语义、常量、UV、顶点色/透明、贴花/水/折射/全息、环境反射和运行时表达式。MATL、普通 VMT 与 FX 引用的 VMT 已接入描述层；特殊双层 VMT 的动画 atlas 仍由同一 loader 做最终覆盖。
- 阶段五：已解析 DXBC RDEF resource binding table，并按 MATL handle index 对齐 `t0/t1/...`。对 `common.rpak` 的 4655 个 MATL 实测：21974 个非空纹理 handle 全部得到语义，21482 个来自 shader 反射、492 个使用文件名 fallback、0 个 Unknown。已覆盖 Albedo、Normal、Gloss/Roughness、Specular/Metalness、AO/CAV、Opacity、Emissive、Detail 与 Distortion，并扩充静态常量参数和对应 shader 采样。
- 阶段六：已加入单个集中式 `Titanfall2MaterialAnimator`，支持 CurrentTime、LinearRamp、Sine、Add、Subtract、Multiply、Clamp、RemapValClamped、Equals、UniformNoise 和 EntityRandom，并将 opacity、emissive、fresnel 与 detail blend 映射到运行时材质参数。解包目录的 21/21 个 VMT 解析通过，检测到 UniformNoise、TextureScroll、TextureTransform、AnimatedTexture、LinearRamp 和 Sine。`PlayerProximity` 因共享材质没有可靠世界位置暂时跳过，避免错误的全局距离动画。
- 阶段七：贴花 shader 已禁用深度写入并保留 normal/opacity/emissive；alpha、premultiplied、additive、双面和 vertex color/alpha 继续按原始状态区分。已增加全息与折射/热扭曲专用 shader；折射在当前公共挂载 shader 无可靠场景颜色入口时退化为透明 UV 扭曲。官方 shadercompiler 对本挂载的全部 99 个 shader 项目编译通过。
- 阶段八：已支持 VTF 六面/mip/多帧 cubemap、RPAK TXTR cube/array 和 BC 压缩布局。真实 VPK 审计发现 7 个独立 cubemap 并全部解码；Angel City BSP 的 12 个位置与 12 帧 `cubemaps.hdr.vtf` 全部解码为 256x256、9 mip、BC6H。地图现在创建对应静态 `EnvmapProbe`，缺失数据时在加载完成后延迟创建一次 128px 动态 fallback。通用材质 shader 已加入 cube、Fresnel、roughness mip 与 intensity。
- 阶段九：所有 RPAK 使用轻量磁盘索引，资源数据保持按需解压；真实安装目录 940/940 个包、119578 个目标资产全部索引命中，审计时强解压缓冲为 0。强缓冲 LRU 默认限制为 512 MB，并新增 RPAK、STARPAK、纹理创建/上传/失败/淘汰统计。纹理继续默认截断到 1024 最大 mip，可由环境变量改为 512 或 2048。
- 当前明确跳过：运行时将已缓存的 1024 纹理热替换为 2048 并按可见性回退。公共 `ResourceLoader` 缓存不能原位替换 mip，强行创建第二份 GPU 纹理还需要可靠的 renderer→material→texture 可见性映射；现阶段会重新引入不可驱逐显存和错误释放风险，因此保留稳定的单级按需加载。
- 下一步：完成最终静态回归、Release 部署，再由实机核对 cubemap 面朝向和特殊材质视觉效果。

## 范围与约束

- 只修改 `Sandbox.Mounting.Titanfall2` 挂载项目及其自带资源，不修改 s&box 引擎源码。
- NavMesh 继续保持禁用，本轮不实施烘焙、保存或复用导航数据。
- 模型动画已经完成实机验证，目前播放效果良好，不再作为本轮重点。
- 优先保证资源正确性、地图加载稳定性和显存可控，再提高特殊效果的还原度。

## 可行性结论

| 项目 | 可行性 | 主要风险 |
|---|---:|---|
| 地图环境 FX | 高 | PCF 操作器很多，首版只能覆盖常用集合 |
| 模型真实碰撞 | 高 | 已有 VPHY/hitbox 解析，重点是骨骼空间与回退质量 |
| 更准确解析 RPAK MATL | 高 | 需要扩展 DXBC 资源绑定反射，不能继续主要依靠文件名后缀 |
| 常用 VMT Proxy | 高 | 需要统一的运行时材质更新器，避免每个材质创建一个组件 |
| 贴花、透明、加色 | 高 | 现有基础 shader 已具备，主要补齐深度、混合和排序状态 |
| 折射、热扭曲 | 中高 | 依赖屏幕颜色和深度采样，部分效果可能只能近似 |
| 全息材质 | 高 | Fresnel、扫描线、噪声和 UV 动画均可由自定义 shader 实现 |
| 立方体贴图 | 中高 | 需要补充 RPAK/VTF cubemap 解码并验证面顺序 |
| 环境反射 | 中高 | 可使用导入 cubemap 或运行时 `EnvmapProbe` |
| 当前地图 RPAK 激活 | 高 | 严格只索引当前地图会破坏单模型浏览和跨包共享依赖 |
| 中低 mip 首载 | 高 | RPAK 已经区分 resident mip 与 STARPAK streamed mip |
| 距离升级高 mip | 中高 | 必须创建新纹理并重新绑定依赖材质 |
| 统一 LRU | 高 | GPU、CPU mip、RPAK 和 STARPAK 需要分别管理 |
| 地图退出释放资源 | 高 | 必须使用作用域和引用计数，避免释放仍被公共模型使用的纹理 |

## 技术边界

### 纹理 mip 升级

s&box 当前运行时纹理更新接口不能指定某一级 mip，因此不能在已创建的纹理上逐级追加高 mip。推荐流程如下：

```text
低分辨率纹理
    ↓ 进入视野且距离足够近
读取 STARPAK 高 mip
    ↓
创建新的高分辨率 Texture
    ↓
重新绑定所有依赖 Material
    ↓
旧纹理进入 LRU，必要时 Dispose
```

### RPAK 索引范围

不建议完全取消全局资产目录。推荐采用两级方案：

- 所有 RPAK 只保留轻量磁盘目录索引。
- 挂载时只打开 `common` 和必要核心包。
- 进入地图后只激活、解压当前地图及其依赖包。
- 单独加载模型时根据 GUID 或资源路径懒加载对应 RPAK。

这样可以减少挂载时间和内存占用，同时保留单模型浏览能力。

## 阶段一：基准、统计与依赖记录

1. 为纹理和材质建立稳定资产标识：

   - RPAK 文件名。
   - Asset GUID。
   - Material path。
   - Texture path。
   - STARPAK offset。
   - 所属地图或公共作用域。

2. 建立材质到纹理的反向依赖：

   ```text
   Texture GUID
   ├─ Material A / g_tAlbedo
   ├─ Material B / g_tEmissive
   └─ Material C / g_tOpacity
   ```

3. 增加分类日志：

   - RPAK 索引命中和未命中。
   - MATL 解析失败。
   - Shader reflection 失败。
   - Texture semantic 未识别。
   - STARPAK 范围读取失败。
   - GPU 纹理创建失败。
   - 当前低 mip、高 mip、排队和失败纹理数量。
   - CPU、GPU 和上传字节估算。

4. 建立固定回归样本：

   - `mp_angel_city`。
   - `mp_homestead`。
   - `mp_wargames`。
   - `super_spectre_v1`。
   - 人类、泰坦、载具和带透明贴花的代表模型。

验收标准：白模、紫黑格、透明错误和材质缺失都能在日志中得到明确原因。

## 阶段二：完成模型真实碰撞

当前已有三级回退：Embedded VPHY、骨骼 hitbox、渲染网格 fallback。

1. 批量统计 VPHY、hitbox 和 fallback 的模型覆盖率。
2. 验证 VPHY hull：

   - 点坐标是否处于骨骼局部空间。
   - Bind pose 是否只应用一次。
   - 三角形绕序是否正确。
   - Hull 是否闭合。
   - Mass、solid 和 bone 分组是否正确。

3. 按用途分类：

   - 静态建筑和道具使用 VPHY 静态 collider。
   - 动态机械使用 VPHY body。
   - 角色和泰坦使用骨骼 hitbox。
   - 纯装饰模型默认不创建高面数物理碰撞。

4. 优化 fallback：

   - 超过三角形阈值时不提交完整渲染网格到 physics。
   - 优先生成包围盒、多个 hitbox 或简化凸包。
   - Trace mesh 与 physics mesh 分离。

5. 验证 StaticPropStreamer 创建的 `ModelCollider` 使用模型内真实碰撞。

验收标准：正常标记为 collidable 的静态模型不会被穿过，复杂模型不会生成超高面数物理网格，日志明确显示 `VPHY / hitbox / simplified / none`。

## 阶段三：完成地图环境 FX

1. 解析 PCF 的 DMX binary 5 / PCF 2 格式。
2. 建立 `effect_name → PCF definition` 缓存。
3. 接入 `maps/*_fx.ent`。
4. 第一批实现：

   - `emit_continuously`。
   - `emit_instantaneously`。
   - 生命周期。
   - 随机位置、速度和半径。
   - 颜色、亮度和透明度。
   - 淡入、淡出和旋转。
   - 重力、阻力和基础运动。
   - Sprite renderer。
   - Additive 和 Alpha blend。
   - 动态 VTF 图集。
   - 子粒子系统。

5. 第二批实现：

   - 控制点。
   - Beam 和 Trail。
   - 模型粒子。
   - 光源粒子。
   - 粒子碰撞。
   - 屏幕空间效果。

6. 增加 `Titanfall2FxStreamer`：

   - 相机附近优先。
   - 每帧创建数量限制。
   - 每帧耗时预算。
   - 远距离环境粒子降低更新频率。
   - 地图销毁时统一释放。

验收标准：天使城的太阳、地雾、光晕、烟雾和环境粒子能够分批出现，不造成首帧卡死。

## 阶段四：建立统一材质描述层

在继续扩充 shader 前，将 MATL 和 VMT 转换为统一描述：

```text
Titanfall2MaterialDescriptor
├─ Blend / Depth / Cull
├─ TextureBindings
├─ Constants
├─ UV Channels
├─ VertexColor / VertexAlpha
├─ Decal / Water / Refract / Hologram
├─ EnvironmentReflection
└─ RuntimeExpressions
```

MATL、VMT 和 FX 材质都应通过统一描述创建，不再各自直接调用 `Material.Set`。纹理流送器也通过该描述维护纹理到材质的依赖关系。

## 阶段五：更准确解析 RPAK MATL

1. 扩展 DXBC RDEF 解析器：

   - 读取 shader resource binding 表。
   - 获取纹理名称、register、维度和类型。
   - 保存 `t0/t1/t2... → semantic`。

2. 解析 SHDS/SHDR 的纹理槽位顺序。
3. 将 MATL handle table 与 shader register 对齐。
4. 使用反射语义绑定：

   - Albedo。
   - Normal。
   - Gloss/Roughness。
   - Specular/Metalness。
   - AO/CAV。
   - Opacity。
   - Emissive。
   - Detail。
   - Distortion。
   - Environment/Cubemap。

5. 文件名后缀只作为反射失败时的 fallback。
6. 扩充 constant buffer 参数：

   - Roughness、specular 和 metalness。
   - Emissive strength。
   - Fresnel。
   - Detail blend。
   - Normal scale。
   - UV 动画。
   - Distortion。
   - Opacity 和 alpha test。
   - Tint。
   - Environment intensity。

验收标准：`*_cav` 等纹理不再因为语义不支持导致材质失败，同一模型的主体、贴花和发光层都能正确绑定，MATL 不再依赖第一个纹理就是 albedo。

## 阶段六：继续实现 VMT Proxy

现有基础包括 `TextureScroll`、`AnimatedTexture`、部分 `TextureTransform` 和 `UniformNoise`。

下一批支持：

1. 时间源：

   - `CurrentTime`。
   - `LinearRamp`。
   - `Sine`。

2. 数学操作：

   - `Add`。
   - `Subtract`。
   - `Multiply`。
   - `Clamp`。
   - `RemapValClamped`。
   - `Equals`。

3. UV：

   - `TextureScroll`。
   - `TextureTransform`。
   - 动态旋转。
   - 动态缩放。
   - 多层 UV。

4. 状态和距离：

   - `PlayerProximity`。
   - `EntityRandom`。
   - 控制透明度和发光强度。

实现集中式 `Titanfall2MaterialAnimator`，每帧批量更新活动材质，避免为每个材质创建单独组件。

验收标准：动态水、全息图、滚动光带、动画面板和发光材质能够持续运行，且大量动态材质不会产生明显组件开销。

## 阶段七：完善特殊材质

### 贴花

- 区分模型贴花和世界贴花。
- 使用 polygon offset 或 depth bias。
- 禁止写入深度。
- 正确处理 normal、opacity 和 emissive。
- 根据原始 blend state 区分 cutout、alpha、premultiplied 和 additive。

### 透明与加色

- 精确读取 D3D blend state。
- 支持双面、vertex color 和 vertex alpha。
- 根据材质类型设置深度写入和排序。
- 避免透明材质进入 opaque pass。

### 全息材质

- Fresnel 边缘光。
- 扫描线。
- UV 噪声与闪烁。
- 深度淡化。
- Vertex color 和 vertex alpha。
- 可选 additive 或 alpha blend。

### 折射和热扭曲

- 创建专用 translucent distortion shader。
- 使用 normal/distortion map 扭曲屏幕颜色。
- 使用场景深度处理物体交界。
- 无法获取屏幕颜色时退化为透明加色材质。

折射和热扭曲需要先完成小型 shader 原型，以验证当前 s&box 渲染管线允许的屏幕颜色和深度采样路径。

## 阶段八：立方体贴图和环境反射

1. 支持 VTF cubemap 六个面及 mip 顺序。
2. 支持 RPAK TXTR cube/array：

   - Layer flags。
   - 六面数据布局。
   - 每面 mip 偏移。
   - BC 压缩格式。
   - 使用 `Texture.CreateCube`。

3. 解析地图 cubemap 数据与 `env_cubemap` 实体。
4. 建立材质到最近 cubemap 的关联。
5. 在 shader 中加入：

   - Cube sampler。
   - 法线反射方向。
   - Fresnel。
   - Roughness 选择 mip。
   - Reflection tint 和 intensity。

6. 缺少原始 cubemap 时创建低分辨率 `EnvmapProbe`，只在地图加载完成后捕获一次。

验收标准：金属、湿地面、玻璃和载具能够获得合理环境反射，不再只有直接光照或显示为黑色。

## 阶段九：纹理流送重构

### RPAK 两级目录

挂载时：

- 读取轻量缓存索引。
- 不保留所有 RPAK 解压数据。
- 只激活 `common` 和核心包。

进入地图时：

- 激活地图 RPAK、补丁包和依赖。
- 从 BSP 材质与静态模型材质建立依赖闭包。
- 缺失 GUID 时按全局轻量目录懒加载其他包。

### 纹理状态机

```text
Unloaded
  → ResidentLow
  → HighRequested
  → ResidentHigh
  → Evicted
  → ResidentLow
```

### 首次加载低 mip

- 优先只读取 RPAK resident mip。
- 默认创建最大尺寸为 256 或 512 的纹理。
- 首次加载不读取 STARPAK。
- 世界远景和未进入视野的模型保持低 mip。

### 高 mip 请求

触发条件：

- 渲染器进入视锥。
- 距离低于阈值。
- 屏幕投影尺寸足够大。
- 玩家单独查看模型。

执行流程：

1. 后台读取 STARPAK。
2. 解码目标 mip 链。
3. 进入 GPU 上传预算队列。
4. 创建高分辨率 Texture。
5. 重新绑定全部依赖材质。
6. 旧低分辨率纹理进入 LRU。

### 每帧预算

增加配置项：

- `TextureCreatesPerFrame`。
- `TextureUploadBytesPerFrame`。
- `TextureFrameBudgetMs`。
- `StarpakReadBytesPerFrame`。
- `GpuTextureBudgetMb`。
- `CpuTextureCacheBudgetMb`。
- `RpakBufferBudgetMb`。

### 统一 LRU

分别管理：

- 解压后的 RPAK buffer。
- STARPAK range block。
- CPU mip 数据。
- 低 mip Texture。
- 高 mip Texture。
- Runtime Material。

淘汰优先级：

1. 当前不可见的高 mip。
2. 远距离模型高 mip。
3. 非当前地图资源。
4. 低 mip。
5. `common` 与当前 UI 资源最后淘汰。

### 地图资源作用域

地图加载时创建 `MapResourceScope`，记录：

- 地图激活的 RPAK。
- 地图创建的材质。
- 低、高 mip 纹理引用。
- STARPAK 缓存块。

地图销毁时：

- 降低引用计数。
- 立即释放无引用高 mip。
- 低 mip 进入短期 LRU。
- 释放地图专属 RPAK 强缓存。
- 保留公共资源以及仍被模型浏览器使用的资源。

### 流送日志

定期输出：

```text
Textures:
  low/high/queued/failed
GPU:
  estimated resident MB / budget MB
CPU:
  decoded mip MB / RPAK MB / STARPAK MB
Uploads:
  current frame MB / average MB
Cache:
  hit / miss / eviction
Packages:
  indexed / active / decompressed
Failures:
  missing GUID / missing STARPAK / unsupported format / GPU create
```

验收标准：

- 进入地图时不再立即创建所有高分辨率纹理。
- 靠近模型后纹理清晰度能够提升。
- 离开地图后 GPU 占用明显下降。
- 不再出现数 GB 不可驱逐纹理长期残留。
- 单独查看模型时仍能按需加载完整材质。

## 推荐开发顺序

1. 增加统计、依赖记录和固定回归样本。
2. 完成模型碰撞验证与 fallback 优化。
3. 完成地图环境 FX。
4. 建立统一材质描述层。
5. 完成 MATL shader resource 反射。
6. 重构纹理流送和地图资源作用域。
7. 扩展 VMT Proxy。
8. 完善贴花、透明、加色和全息材质。
9. 实现折射与热扭曲原型。
10. 实现 cubemap 与环境反射。
11. 执行全地图回归和显存压力测试。

第 4 至第 6 步应连续实施。材质描述、纹理依赖和流送调度关系紧密，如果先分散修改材质绑定、随后再加入流送器，会造成重复重构。
