# Titanfall 2 挂载改造路线

日期：2026-11-06。状态：路线初稿基于当时工作区源码的设计与局部审查。其后的第一批运行期边界重构已实施，结果见[实现与验证记录](TITANFALL2_REFACTOR_2026-11-06.zh-CN.md)；本文中的源码观察描述重构前状态，尚无真实地图性能结论。

关联：[通用框架设计](MOUNT_FRAMEWORK_DESIGN.zh-CN.md)、[研究依据](MOUNT_FRAMEWORK_RESEARCH.zh-CN.md)。

## 1. 目标和现状

保留现有格式读取器、原作材质/光照语义、世界聚合、实例化、派生缓存和碰撞准备能力，通过明确的内容身份、场景计划、后端所有权与运行预算组织它们。所有实现修改限定在 `engine/Mounting/` 与 `game/mount/titanfall2/`。

当前实现已经不是逐物体加载并绘制的简单挂载：

- `Map.World.cs` 使用 `Titanfall2WorldAggregator` 组织世界几何，并有独立阴影几何路径。
- `CompiledPropLayout.cs` 已按 cell 和模型/skin 编译实例排列；`Titanfall2CompiledVisibility.cs` 已提供预编译可见性数据。
- `StaticPropStreamer.cs` 的加载阶段会准备 props；常驻模式还准备碰撞，运行期具备分队列和预算。
- `StaticPropStreamer.GpuScene.cs` 已把兼容静态几何交给 GPU，并支持混合模型的原生残余部分；CPU reference 在显式切换时才补建。
- `StaticPropStreamer.Visibility.cs` 已跳过 `GpuRendered` 批次的 CPU 可见性处理。不能把“全部 GPU 实例还在重复做 CPU LOD”当作已确认问题。
- 已有模型、纹理、材质和地图派生缓存，以及 SceneFile 关联资源释放机制。

本次建议不预设当前瓶颈一定来自 props。世界材质、shadow/depth、lightmap 带宽、透明与粒子都需独立计时。

## 2. 先固定质量基线和身份

将配置分为 `QualityProfile` 与 `BackendProfile`：前者固定纹理尺寸、lightmap 精度、LOD、fade、阴影级联与距离、contact shadows、godrays；后者只决定布局和提交实现。

当前 `FpsPreset >= 120` 同时改变多个质量和布局参数，因此它适合作为用户性能预设，不能单独证明某种架构更快。保留其兼容入口，展开成完整的生效配置并写入报告；后续名称不承诺硬件一定达到某个 FPS。

比较三条实际路径：世界聚合保持相同；静态 props 分别使用现有 CPU 实例化、GPU 纯兼容部分、GPU 加混合 submesh 接管。比较前完成 reference 路径的准备，排除切换时创建对象的开销；内存则另做每条路径独立会话的比较，避免 A/B 会话同时持有两套资源。

每次记录引擎、模块 DLL、shader、地图、画质与 Profile 身份，以及提交对象、CPU batch、GPU instance、native residual、draw、视图输出槽位、GPU buffer 字节和托管内存。

## 3. 优先级 A：给已存在的性能路径补上硬边界

### 3.1 GPU 视图输出池与预算统一

源码事实：`StaticPropGpuScene.Layout.cs` 的 residentBytes 估计包含 8 份输出；`StaticPropGpuScene.cs` 预热 8 个；`GpuSceneCompatibility.cs` 中的 `Titanfall2GpuViewPool.Acquire` 在槽位不足时继续创建。

因此当前估计不是输出池增长的硬上限。建议：

1. 把静态几何字节、单槽输出字节、槽位上限、临时峰值统一交给预算对象；分别报告已承诺和已创建字节。
2. 在加载阶段按已支持的视图/pass 组合预热，运行期申请槽位必须经过预算准入。首次额外视图不应直接造成无界 buffer 分配。
3. 每个 recording 独占尚在使用的输出，不能因为池满就随意复用同一槽位。CPU lock 不能保证 GPU 已完成消费。
4. 超额情况必须有预先验证的保守渲染方案：例如准备好的 reference 路径，或独立的保守输出。不能丢弃该视图，也不能在 draw callback 临时创建全图 reference 对象。
5. 无法提供正确降级时，在准备阶段拒绝启用 GPU 后端，而不是运行中溢出预算。

验收：多相机、反射、阴影与编辑器预览组合下，运行中无未计入预算的增长、无共享输出覆盖、无漏绘；记录槽位高水位。没有测量前，不宣称这是当前帧率瓶颈。

### 3.2 地图回收绑定代次和真实使用者

源码事实：`SceneResourceRelease.cs` 使用挂载级 `_mapSceneLeases`；释放时调用全局 Collect，并在 5.5/11 秒安排额外 Collect。延时任务未按新地图代次重新判断是否适合进行全局回收。

建议先加 session/generation 与取消机制，使旧地图的回收计划不会无条件落进下一张地图的可交互阶段。然后引入 `(ProfileRevision, MapKey, BuildGeneration)` 所有权表：区分临时 build、缓存 SceneFile、活跃场景/克隆、共享资源和专属资源。

保留 `ITitanfall2SceneResource` 的不销毁共享活对象原则；对专属资源使用可证明安全的释放路径。渐进缩小全局清缓存范围，避免任何地图都没关闭时只能全部保留、最后一张关闭时又全部清掉。

不能直接删除现有 GC 补救机制后宣称问题解决。先记录 native 引用保留、最终释放与显存变化；仍有必要的回收安排在明确的加载/退出阶段。GC 是进程级动作，放到 Task.Run 也不能保证不影响游戏线程。

验收：A→B→A、多地图并存、编辑器预览、游戏克隆、卸载中构建和服务器轮图；无错误模型/贴图、无旧代发布、内存峰值受控。延迟 GC 造成实际卡顿与否需要帧时间记录证明。

### 3.3 单次昂贵操作不能靠计时器变成硬预算

`StaticPropStreamer.Collision.cs` 在操作前检查预算，但一次模型加载或 native body 创建本身仍可能超过剩余预算。保留默认常驻碰撞方向，在 Ready 前完成所需碰撞；低内存模式再单独实现解码预取、创建成本估计和有安全边界的驻留。

运行期碰撞需求由玩家/刚体/服务器消费者驱动，不能只依据 `Scene.Camera`。必要时在进入未知区域前等待碰撞就绪。不能降低碰撞覆盖率来制造性能收益。

## 4. 优先级 B：把 streamer 拆成准备结果和运行控制

保留 `Titanfall2StaticPropStreamer` 的组件身份与已有序列化兼容，内部逐步委托给以下拟新增对象：

```text
Titanfall2MapSession
  ContentSnapshot + ProfileRevision
  PreparedMapPlan
    WorldGeometryPlan
    StaticPropPlan
    CollisionPlan
    EnvironmentPlan
  RenderBackend
    NativeWorldAggregates
    CpuPropInstances
    GpuPropScene
    NativeResiduals
  ResourceOwnership
  RuntimeCoordinator
```

`PreparedMapPlan` 建立在现有 CompiledPropLayout/CompiledVisibility 上，不并存另一份相同布局。纯几何/材质描述与运行期 Model/Material 引用分开，缓存只写纯数据。记录稳定 InstanceId、model/skin/bodygroup/LOD、probe、PVS、fade、shadow、碰撞语义和 submesh 路由。

每个 submesh 的主渲染所有者必须唯一；阴影/深度等 pass 按明确规则另行映射。混合 prop 的 GPU 子网格与 native residual 共享实例身份和一致的变换，不重复绘制。

运行状态明确为 Preparing、Ready、Retiring。Ready 且常驻完成后，不再轮询已经永久完成的 populate 工作。GPU 全接管且没有 CPU residual/缺失碰撞时，协调器只更新必要的视图策略；CPU 可见性队列仅处理其真实消费者。保留碰撞销毁后的修复、失败重试和后端切换事件。

不要直接禁用整个 Component：现有所有权、销毁、策略更新或碰撞修复可能仍依赖它。先量化各分支实际耗时，再通过状态和脏标记缩小工作。

现有 GPU 与类型注册依赖非公开方法反射。用户要求“不改引擎源码”本身并未禁止这种方式；迁移期间应集中为明确的兼容层、一次探测、已验证 fallback，并记录支持的引擎身份。新通用框架不能把它描述成稳定 public API。

## 5. 优先级 C：渲染后端各自优化

世界继续使用原生 aggregate，保留透明、水、decal 与独立阴影几何的语义。cell 大小根据 CPU 提交与 GPU 剔除收益共同选择，不能只追求最少 fragment。

GPU props 继续作为候选主路径，CPU 实例化保留为基线与兼容路径。当前每个 recording 执行清计数、逐实例 cull 和生成 draw 参数三个阶段；先记录每个 pass 的 dispatch、candidate、输出字节和 GPU 时间，再决定是否缓存或共享结果。

可共享结果必须有相同视图、frustum、PVS、LOD 策略、shadow/mixed 状态与场景版本，且证明同步和生命周期正确。主摄像机、阴影级联和反射不能因为发生在同一帧就共享可见集。

`RenderSceneObject` 与 `UpdatePolicyCamera` 使用同一锁，候选优化是短锁发布不可变策略快照。是否有跨线程等待先测量；Delete/Dispose 必须继续有正确同步，不能直接删锁。

整图诊断必须覆盖 world forward、depth、shadow、probe/lightmap、透明、粒子和材质动画。外部已经烘焙的光照是否还需要某类实时阴影属于质量/语义决定，不应暗中作为“后端优化”。

## 6. 优先级 D：统一 VFS 与缓存身份

用 `ITitanfall2AssetSource` 包装层接入 `ContentSnapshot`，保留现有 VPK、RPAK、STARPAK、MDL、rBSP 与 Miles 读取器。不要先重写格式。

`GameMount.cs` 的各注册层改为提交候选描述符，统一 resolver 决定 winner。补全路径和 NativeAssetId 的映射；引擎资源路径与原始文件身份分离。Northstar/loose Mod、地图内嵌和同目录伴随文件的读取必须通过同一上下文，不绕回原版物理目录。

当前 `ProcessedCache.cs` 的 ContentFingerprint 包含模块 MVID、原版 VPK/RPAK/STARPAK 文件 size/mtime。它适合现有保守失效，但不是完整 Mod/依赖身份：Mod 目录、搜索顺序、原缺失依赖新增等必须显式纳入新快照和键。不能简单删除 MVID；改用显式 decoder/布局/shader ABI 版本时要有旧缓存拒绝测试。

逐步按实际依赖摘要复用缓存，避免无关包变化使所有资源失效。强内容校验只处理需要的内容和目录身份，不在每次启动无条件 hash 整个游戏。

VFS 改造主要提高 Mod 正确性、可维护性和加载复用，不能独自解释为稳态 FPS 优化。

## 7. 建议变更批次

| 批次 | 范围 | 验收 |
|---|---|---|
| 1 | 生效配置、后端 census、运行身份、固定路线基线 | 三种后端同画质可重复比较，准备开销与稳态分开 |
| 2 | GPU view pool 预算与正确超额处理 | 多视图不越界、不漏绘、输出不互相覆盖 |
| 3 | 地图释放代次与回收计划 | 旧代回收不无条件打断新地图，多次切图不破坏资源 |
| 4 | PreparedMapPlan 与 streamer 状态/职责 | 结果等价；移除已完成工作的轮询；残余路径完整 |
| 5 | 依据基线优化实际瓶颈 pass | p95/p99 与质量通过，不只报告 draw 或局部 FPS |
| 6 | VFS/Mod/Scope 与依赖缓存 | 同名覆盖、GUID、伴随文件、地图隔离和失效通过 |
| 7 | 从已验证的共同部分提炼通用框架 | 第二个挂载接入，不复制 TF2 特例和兼容反射 |

批次 2、3 是已经看到具体代码边界的候选优先项；收益与风险仍需运行验证。批次 5 的具体内容必须由测量决定。每批保留可回退路径，运行验证前核对实际加载的模块与 shader 身份。

路线初稿交付时仅新增本文。后续实施范围与验证结果以实现记录为准。

## 8. GPU 优先级修订：用户提供的性能面板转录

依据：用户提供同一进程四张性能面板截图的文字转录。未直接核验原图，四页来自不同取样窗口；不能把各页的数值拼成一个严格同帧分解，也尚未将该样本与当前部署 DLL/shader 身份对齐。

样本约 47–49 FPS，Frame 20.6–21.4 ms，GPU frame 约 19.7 ms；Rendering 页 span 20.37 ms、busy 13.50 ms、submit-to-done 37.99 ms。

### 8.1 指标解释

- 当前 DevTools 的 GPU 限制标签采用 GPU time 接近 Frame time 的启发式阈值；它支持 GPU 方向排查，不能替代设备利用率或具体瓶颈计数器。
- `DevTools.GpuTimeline.Busy` 计算已记录 graphics 叶通道的区间并集。span 与 busy 的差值可能包含未覆盖工作、等待和其他队列活动，不能直接叫作 GPU 空闲，也不能用 busy/span 当硬件利用率。
- 父通道、子通道及包装层有包含和重叠。RenderToSwapChain、PlatformOverlay、Managed: PlatformOverlay、CommandList 不能简单相加。
- CPU 页的 GPU record/MDI/Dispatch 是 CPU 调用或录制 scope，不能作为 compute/draw 的 GPU 时间。当前 cull 在 MDI 命令播放之前执行，应分别建立 GPU 标记。
- pending presents 的 checked/waited 是累计计数，不能推出当前每帧 Present 等待比例或具体耗时。

### 8.2 本样本的优化顺序

| 对象 | 转录中的 GPU 时间 | 决策 |
|---|---|---|
| Static Opaque Forward | 约 4.01–4.05 ms；其中 MDI 约 1.92 ms | 优先拆分世界、GPU props、原生残余，检查每像素工作与可见几何 |
| Depth Normal Prepass Large / Small | 各约 1.73 / 1.38 ms | 优先检查必要的法线/opacity 路径、重复几何和可复用可见集；保持 SSR/AO 所需输入 |
| Translucent Forward | 约 2.39–2.58 ms，含 overlay/HUD 等 | 先按地图透明、水、粒子、viewmodel、overlay 分来源，不能全部归因 TF2 材质 |
| 平台/UI overlay | PlatformOverlay 约 1.55 ms，UI Root 约 0.89 ms | 属于整帧预算，建立不显示性能面板和外部 overlay 的对照；不改引擎平台层来充当挂载优化 |
| TF2 display / color correction | 约 0.61 / 0.19 ms | 次级候选；检查全屏读写和曝光 meter，融合需保留色域及执行顺序 |
| SSR / AO / Bloom | 各约 0.87 / 0.63 / 0.68 ms | 分别对照；父级 AfterDepthPrepass 等不重复计入 |
| CSM | 可辨识条目约 0.25 / 0.08 ms；计数栏另显示 shadow views 0 | 本转录不足以把阴影排第一；核实 DrawDepthOpaque 归属及取样时刻，不据此宣布阴影关闭 |

以上是候选排序，不是互斥的 GPU 时间相加。Static Opaque 中的 1.92 ms 也不能直接从父级时间扣出一个精确的“世界成本”。

### 8.3 接下来的具体改造

1. 增加挂载范围内的 GPU marker：reset、cull/LOD、args、draw 分开，并标识实际 pass/view；保持 CPU scope 与 GPU marker 名称可区分。
2. 收集材质特性分布和零强度参数，审查 distortion、detail、layer、emissive 等采样是否实际保留在编译结果中。动态代理保留运行期变化；静态特性采用数量受控的精简变体。不能只看源码采样次数判断机器码成本。
3. 对 depth-normal 与 cutout 建立最小必要输出路径；先确认编译器是否已经消除无用材质工作，不先假定现有 depth 完整执行了 forward shader。
4. 在视图、frustum、PVS、LOD、mixed/shadow 资格和时序均相同时，才评估 depth/forward 可见 ID 与 args 复用；反射和阴影保持独立。避免用相机位置猜测视图身份。
5. 检查 `ConservativeRecordings` 是否为零。超额路径增加顶点工作，GPU 受限时应消除持续溢出，同时把增加槽位的缓冲成本纳入预算。

在这个约 21 ms 的样本中，达到 60 FPS 需要整帧降低约 4 ms。即使局部 1.92 ms MDI 绘制减少 20%，局部节省也仅约 0.38 ms，且不保证等量转化为帧时间。因此应覆盖不透明、深度和透明等主要路径，而不是继续只围绕静态 prop 提交数。

Audio 24 ms、GC 11 ms 与 worst frame 58 ms 是另一项尾延迟问题，必须对齐同一帧和调用栈后归因，不能把各类别最大值相加。
