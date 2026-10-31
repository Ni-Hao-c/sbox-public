# 全文件审阅与修复记录 — Sandbox.Mounting.Titanfall2（2026-10-30 第二轮）

本文件覆盖 `engine/Mounting/Sandbox.Mounting.Titanfall2` 的**全部源文件**：104 个 C#（约 27,000 行）、
34 个 Markdown、3 个原生桥文件与构建脚本。审阅分两批进行：先对既有实现做全量审阅，再对另一个
Codex 会话并行完成并已停止的增量（专用服务器、contents 分类碰撞、Miles 事件运行时、streamer 重写）
做二次审阅。

## 0. 结论摘要

| 项 | 结果 |
|---|---|
| 已修复 | 45 项（P0 1、P1 11、P2 33） |
| 复核后否定（未改） | 6 项（见 §3，均为“报告看起来成立、实际不成立”） |
| 已确认但按设计/风险保留 | 17 项（见 §2） |
| 构建 | `-c Release`（`SkipCopyOutputDll=true`）0 警告 / 0 错误 |
| 格式 | 模块级 `dotnet format --verify-no-changes` 由**失败**转为**通过** |
| 未运行 | 实机载入、GPU 像素回归、专用服务器联机；本轮结论是静态审阅 + 编译/格式验证 |

**并发说明：** 本轮中途检测到另一个 Codex 会话正在修改同一目录（新增
`CollisionQueries.cs`、`CollisionContents.cs`、`MilesEventSession.cs`、`Model.Metadata.cs` 等）。
当时停止了对代码的进一步修改；该会话结束后，按其最终代码重新审阅增量并继续修复。
期间写入的 17 项修复已逐项确认仍在，且与增量代码一起通过编译。

## 1. 已修复

| 级别 | 位置 | 问题 | 修复 |
|---|---|---|---|
| P0 | `Resource/CubemapController.cs` OnDestroy | 先 `GameObject.Destroy()`（下一帧才生效）再立即 `Dispose` 探针纹理，探针在本帧剩余时间仍引用已释放纹理 | 改为 `DestroyImmediate()` 后再释放纹理，与 `MapAudio` 的同类所有权处理一致 |
| P1 | `Formats/Titanfall2BspReader.cs` `ParseStaticProps` | `propCount` 只用于预分配上限，循环却按它读完整 GAME_LUMP；损坏计数会吞掉后续 child 的数据 | 用 `sprp` child 的 `relativeOffset + Length` 校验 `propCount * 64`，越界直接返回空 |
| P1 | `Formats/Titanfall2BspReader.cs` `BuildModel` | 世界模型（index 0）的 `FirstMesh/NumMeshes` 从不校验，损坏值会驱动数十亿次循环并让 `ParseVisibility` 按它分配掩码 | 内联模型保持抛错；世界模型钳制到 mesh lump 范围，`SourceMeshCount` 使用钳制值 |
| P1 | `Pak/RpakArchive.cs` `ResolveStreamingFilePath` | RPAK 头里的 streaming 文件名直接当主机路径使用，可含根路径或 `..` 越出包目录 | 拒绝根路径、`:` 和 `..` 段，与 `LooseAssetSource.TryReadPath` 一致 |
| P1 | `Resource/WorldShadowController.cs` | 只收集 `ModelRenderer` 子对象；默认聚合路径创建的是 `AggregateRenderer`，距离阴影门控完全空转 | 同时收集两种 renderer，统一 `Apply(bool)`；`AggregateRenderer.CastShadows` 变更会重建原生聚合 |
| P1 | `Resource/Tonemapping.cs` | 每帧把 `Camera.AutoExposure.Enabled` 设为 false，效果销毁后不恢复，宿主相机永久失去自动曝光 | 每相机记录首次渲染前的值，`OnDisabled/OnDestroy` 还原 |
| P1 | `Resource/Texture.cs` `Prepare` | 预算在完整解码**之后**检查；超预算时丢弃已解码数据，`Load()` 又在主线程重解一次 | 超预算仍保留 `_prepared`（字节已产生，丢弃只会重复工作）；预算仍阻止启动新任务 |
| P2 | `Formats/Titanfall2BspCollisionReader.cs` `AppendBrush` | 文件字节 `sideCount` 直接驱动 `planes³` 凸包生成，异常值可造成分钟级卡死 | 拒绝 `sideCount > 128`（远高于零售引擎上限），并保留既有逐面校验 |
| P2 | `Formats/Titanfall2Mdl53AnimationReader.cs` `Decode` | `FrameCount` 无上限即用于 `new BonePose[frameCount][]` | 上限 `1 << 16`，超过抛 `InvalidDataException` |
| P2 | `Formats/Titanfall2EntityText.cs` | `Singleline` 正则无超时，截断的 `.ent` 中未闭合 `{` 会退化为二次扫描 | 两个正则加 5 秒 `MatchTimeout` |
| P2 | `Pak/VtfTextureDecoder.cs` `TryCreate/TryCreateCube` | `PrepareUpload` 在 `try` 之外，mip 计算/切片异常会逃出 `Try*` | 移入既有 `try` |
| P2 | `Pak/VtfTextureDecoder.cs` `TryCreateFrames` | `frameBytes * FrameCount` 为 int 运算，未压缩大帧会溢出成负偏移 | 64 位计算 + `frameBytes > 0 && largestMipOffset >= 0` 校验；逐帧偏移也用 long |
| P2 | `Pak/MilesBankArchive.cs` `ReadStreams` | `Stream.Read` 合法短读被当成坏 MSTR 头，整个银行可能被判无流 | `ReadExactly` + 逐文件 `EndOfStreamException` |
| P2 | `Pak/VpkArchive.cs` `TryReadAllBytes` | 容量只受 `Array.MaxLength` 限制，损坏 directory 可要求约 2 GB 分配 | 增加 1 GiB `MaximumEntryBytes` 上限 |
| P2 | `Pak/RpakArchive.cs` `ReadCacheCount` | 索引缓存只校验 magic/版本/源文件时间，损坏 `.idx` 可先分配千万条记录 | 计数同时受“剩余字节 / 最小记录长度”限制 |
| P2 | `Resource/StaticPropStreamer.Loading.cs` `RefreshCells` | `PropCell/PropCluster.RenderWanted` 的逐 cell 迟滞只被自己读取，没有消费者，误导为“逐 cell 距离剔除” | 删除整段死状态（`StaticPropRuntimeTypes.cs` 同步删除字段）；距离剔除保留在逐实例/逐批次 |
| P2 | `Resource/WorldMaterialStreamer.cs` | `_statusTimer` 从未 `Start()`，状态日志永不输出；每帧 `Stopwatch.StartNew()` 分配 | 启用时启动计时器，复用 `_budgetTimer` |
| P2 | `Resource/ProcessedCache.cs` | 每次缓存命中都同步 `SetLastWriteTimeUtc`，整图加载产生数千次写元数据 | 每文件每会话只 touch 一次（`ConcurrentDictionary`） |
| P2 | `Resource/Texture.cs` `TryLoadTga` | 函数无条件 `return false`，所有 TGA 都落到 “Unsupported texture format” | 删除死分支及调用 |
| P2 | `Resource/ParticleRopeRenderer.cs` | `OnDestroy` 缺失，绳带 `SceneLineObject` 与材质引用留到 GC | 增加 `OnDestroy` 删除 line 并释放引用（`Material` 无公开释放 API，本轮不伪造） |
| P2 | `Resource/CollisionQueries.cs` `Run` | mask=0 且 `includeOtherObjects=false` 时执行“先 WithTag 再 WithoutTags”，结果命中整个世界；每次 trace 还分配 tag 数组 | 空掩码直接返回无命中；tag 数组按 mask 缓存 |
| P2 | `Titanfall2Log.cs` + `StaticPropStreamer*.cs` + `ParticleStreamer.cs` | `ReportSpike` 的说明字符串在阈值判断**之前**插值，热路径每次分配，注释却声称零分配 | 新增 1/2/3 参数泛型重载（format 只在超阈值时格式化），更新 7 个调用点 |
| P2 | `Resource/Map.cs` 解析日志 | lightmap 无效时固定打印 `skipped (BSP_LIGHTMAPS=0)`，服务器或损坏数据时误导 | 改为 `not parsed` |
| P2 | `Resource/Map.cs` 分类碰撞 | `hasOriginalWorldCollision` 为真但所有 contents 为 0 时，会静默生成“无世界碰撞”的场景；`WorldReady` 可能选中 trigger 组 | 空结果抛 `InvalidDataException`；优先选非 trigger collider 交给 `WorldReady` |
| P2 | `Resource/SoundEventPlayer.cs` `Play` | 原生 Miles 运行时缺失/加载失败时异常从 `OnStart/OnUpdate` 逃出 | 捕获并记录一次，返回 false |
| P2 | `Native/MilesBridge/miles_events.inl` `tf2_miles_events_open` | `root == nullptr` 且无活动会话时会进入 `LoadApi(nullptr)`（`std::wstring` 未定义行为） | 显式空指针检查并返回错误 |
| P2 | `ARCHITECTURE.md` | §2.1/§4 的默认值与代码相反（lightmap、prop 阴影、渲染距离、批次聚类等） | 修正默认值表、解析覆盖表，新增 §4.1 说明派生缓存的有效条件 |

### 1.1 增量代码二次审阅后的补充修复

并发会话的增量（专用服务器、contents 分类碰撞、Miles 事件运行时）停止后，二次审阅又确认并修复：

| 级别 | 位置 | 问题 | 修复 |
|---|---|---|---|
| P1 | `Resource/CollisionQueries.cs` `Run` | `PhysicsTrace` 的 “any tag” 只有 8 个槽位，`WithOptionalTag` 静默丢弃第 9 个以后；`Titanfall2TraceMask.Shot` 有 9 位，`Hitbox` 永远不参与查询 | 按 8 位一组拆分掩码，每组一次 trace 取最近命中；tag 组按 mask 缓存 |
| P1 | `Resource/StaticPropStreamer.cs` OnLoad / `.Collision.cs` | 服务器常驻碰撞预载沿用了运行时 churn 上限（2 个/帧），`ttf2_map_ready` 需要 `collidableProps/2` 帧 | OnLoad 碰撞循环改用 `PropPreloadCollisionBodiesPerYield` 与 `PropPreloadBudgetMilliseconds`（`finally` 恢复），至少 1 个/让出 |
| P1 | `Resource/StaticPropStreamer.Collision.cs` | 模型读取/解析的瞬时失败被当作“永久无碰撞”（`CollisionUnavailable`），readiness 永不发布 | 区分“模型不可用（可重试）”与“模型有效但无 physics（永久）”；前者不置位，交给运行时重试 |
| P1 | `Resource/StaticPropStreamer.cs` OnUpdate | 加载期瞬时失败的实例在运行时补建后，`ResidentCollisionsReady` 不会重新计算 | 运行时碰撞队列处理完后重算 readiness |
| P1 | `Resource/Model.Metadata.cs` `Titanfall2ModelVariant.Apply` | 变体加载失败时 `_applied` 不记录，`OnUpdate` 每帧重新解码 MDL 并创建材质/GPU buffer | 失败也记录 key，并用 `ModelFailureRetrySeconds` 退避；key 改变时丢弃旧模型引用 |
| P1 | `GameMount.cs` `_texturePaths` / `_materialSources` | 预读工作线程读取普通 `Dictionary`，`Mount`/`Shutdown` 无锁 `Clear`/`TryAdd` | 两表改为 `ConcurrentDictionary` |
| P2 | `Resource/WorldReady.cs` | `Titanfall2CollisionManifest.IsReady()` 在 0.25 s 节流**之前**调用，未就绪时每帧全量扫描 physics world | 移到节流之后 |
| P2 | `Resource/MaterialAnimator.cs` `WriteVector` | 每次向量写入插值 `name[0..3]` 并 `ToLowerInvariant()`，与“不分配”的注释矛盾 | 组件 key 与 legacy 目标名按向量名缓存 |
| P2 | `Pak/RpakArchive.cs` | page 表读取没有 `EnsureRange`，截断包只会在 `AsSpan` 抛无意义异常 | 读取前校验 `pageCount * 0x0C` |
| P2 | `Pak/DxbcReflection.cs` | RDEF 的 `bindCount`（≤65536）直接驱动展开，可造成约 10⁹ 次字典探测 | 每个资源最多展开 64 个槽位 |
| P2 | `GameMount.cs` `TryGetSoundFile` | 失败结果永久缓存为 null，一次瞬时解码失败会让该声音整个挂载期静音 | 失败改记 `_soundFailureRetryAfter`，按模型同款退避重试；成功时清除 |
| P2 | `GameMount.cs` `TryGetTextureDependents` | 把内部 `List` 直接逃出锁（当前无调用者） | 在锁内 `ToArray()` 返回副本 |
| P2 | `Native/MilesBridge/miles_events.inl` `DestroyEventSession` | bank 从不卸载，每个会话泄漏 `general.mbnk` + patch + 64 MB 流缓存 | 可选解析 `MilesBankUnload` 并在 driver 前调用（缺失则跳过，不破坏旧运行时） |
| P2 | `Native/MilesBridge/miles_events.inl` listener | `silent` 会话的 master volume 被首次 listener 更新覆盖为可听 | `EventSession` 记录 silent，listener 保持 0 |
| P2 | `Native/MilesBridge/miles_events.inl` `event_stop` | `queueFilter` 设置后从不复位，之后同一队列的播放可能都被上次停止的 id 过滤 | STOPNOW 提交后 `queueFilter(queue, 0)` |
| P2 | `Native/MilesBridge/miles_events.inl` / `miles_bridge.cpp` | C ABI 函数在 try 之外分配（`std::string`/`new`/`LoadApi`），`/EHsc` 下异常会 `std::terminate` | 包住 `events_open` 整体与 `LoadApi` 调用 |
| P2 | `Native/MilesBridge/miles_bridge.cpp` | BCF 头的 `frames`（uint32）未做上限校验就用于 `resize`，可要求数十 GB | 按声明采样率限制 15 分钟长度 |
| P2 | `Resource/StaticPropStreamer.Visibility.cs` | `IsRenderVisible(PropCell/PropCluster, …)` 忽略参数，注释却声称“任一成员 cell” | 合并为 `IsRenderVisible(mask)`，把并集说明移到 `PendingBatch.Add` |
| P2 | `Resource/StaticPropStreamer.Lod.cs` | 注释引用 “line 792”（文件仅 314 行） | 去掉行号 |

## 2. 已确认但未修改

| 级别 | 位置 | 原因 |
|---|---|---|
| P2 | `Resource/Map.cs` `Titanfall2SceneResourceOwner.Configure` 未传 `OwnedResources`；`WorldBuildResult.OwnedResources` 无读者 | 这是 CODE_AUDIT #1 的刻意取舍：生成资源必须活到缓存场景寿命，交给单场景析构会重现“重开变 Error 模型”。要清理需要“挂载卸载时统一释放”的完整方案，不能只改一行 |
| P2 | `Map.Cache` 在默认设置下不命中 | `cacheable` 要求 `BSP_LIGHTMAPS=0`，而默认是 1；缓存格式尚不保存 lightmap/WORLD_LIGHTS。已在 ARCHITECTURE §4.1 明确，扩缓存属于独立工作 |
| P2 | `Pak/LzhamDecoder.cs` 接受短解压（`written != destinationLength` 时 Resize） | 严格失败可能拒绝零售包；无实机数据前不改变兼容语义，改为在报告中记录 |
| P2 | `Native/MilesBridge/miles_events.inl` 会话关闭未 unload bank/project | 需要确认零售 Miles 的符号名（`MilesBankUnload` 等）；错误猜测会让原生桥加载失败。会话结束时进程/驱动仍会回收 |
| P2 | `Resource/StaticPropStreamer.Batches.cs` teardown 销毁 collider 时未回退 `_collidersCreated` | 只在销毁路径，影响 `ResidentCollisionCount` 统计；修复会触动 teardown 顺序 |
| P2 | `Resource/DeferredNavMeshBuilder.cs` disable→enable 的宿主设置恢复依赖 `ReferenceEquals(IncludedBodies, MountedBodies)` | 已改为 ConditionalWeakTable + 引用检查，但未跑编辑器 disable/enable 周期实测；不贸然再改 |
| P2 | `Resource/Model.cs` `ReverseWinding` 对非 3 倍数索引尾部补 0 | 未证实 extractor 会产生非 3 倍数；缓存路径会拒绝这种列表，贸然改会掩盖上游错误 |
| P3 | `Titanfall2SoundEventSession` 构造时原生库缺失会抛异常 | 调用方（`MapAudio`、`SoundEventPlayer`）已捕获；公开 API 的异常语义保留 |
| P2 | `Native/MilesBridge/miles_events.inl` 事件驱动固定 `channelSpec = 2` | 解码路径的 `CreateDriver` 会探测 0..12 直到通道数匹配，事件路径没有做同样的探测。改成探测需要在事件 API 上复刻一整套 driver 创建/校验逻辑，且没有证据表明零售 DirectSound 输出不是立体声；先记录 |
| P2 | `Native/MilesBridge/miles_events.inl` `g_decodeMutex` 最长持有约 30 s（project 15 s + bank 15 s） | 事件会话创建发生在组件更新路径；拆分互斥量属于原生桥结构调整，未在无实机验证时进行 |
| P2 | `Native/MilesBridge/miles_events.inl` 用裸 `EventSession*` 作为会话标识 | 关闭后地址可能被新会话复用，陈旧 C# 句柄会操作新会话。改为代际 id 需要同时改 C# 句柄，超出本轮范围 |
| P2 | `Resource/Map.cs` 只要有一个 CM primitive 被拒绝就整体退回渲染碰撞，服务器直接拒绝加载 | 读取器只对越界/非有限/零面积/超 128 面的原语计数拒绝；四张实图审计为 0。改成比例阈值属于碰撞策略取舍，需实机数据 |
| P2 | `Formats/Titanfall2BspCollisionReader.cs` `hasKnownContents` 恒真 | 越界即抛错，字段与条件分支是死代码；清理会触及公开 record，暂不动 |
| P2 | `Resource/Map.Spawns.cs` / `SkyDome.cs` 初始天球位姿在首个有效相机帧前使用未缩放位置 | 只有无相机的首帧/无头路径可见；稳态表达式一致，未改 |
| P2 | `Resource/Model.cs` 直接构建路径不校验索引范围（缓存路径校验） | 提取器应保证索引合法；在 GPU 上传前加过滤会掩盖上游错误，先记录 |
| P2 | `Resource/Model.Metadata.cs` 零体积 hitbox 与物理 hull 的有效性规则不同 | 零体积 hitbox 在 Source 中可能是有意为之（薄片）；不擅自统一 |
| P2 | `Resource/ParticleStreamer.cs` 每帧 `_pending.RemoveAt(index)` 为 O(n) | 实际地图 FX 数量为百级（上限 100k 只是保险），memmove 成本可忽略 |
| P2 | `GameMount.cs` 粒子 sprite 缓存按 0.01 精度的 frameRate 建 key | 调用方来自 PCF 的少量固定值；量化规则改变会影响播放速度语义 |

## 3. 复核后否定（避免误改）

| 报告 | 复核结论 |
|---|---|
| Cubemap origin 应按 float 读 | **否**。`bsp_tool/branches/respawn/titanfall.py` 的 `Cubemap` 为 `_format = "3iI"`（`origin: vector.vec3 # int32_t`），当前 `ReadInt32` 正确 |
| `PcfReader` 的 radius 回退是死代码 | **否**。`Radius Random` 初始化器允许 `radius_min/max = 0`，回退会把 0 半径抬到 1，可达 |
| 静态道具聚合预算按 index 数而非顶点数 | **否**。`AppendStaticPropMesh` 每三角形追加 3 个顶点，追加顶点数恰为 `Indices.Length / 3 * 3`，与预算一致 |
| `ParticleRampRenderer` 每实例材质泄漏 | **否**。`Material.FromShader` 全局缓存并返回同一实例 |
| `Map.Cache` 命中丢失 `WorldLights` 导致天空色不同 | **否**。缓存只在 `BSP_LIGHTMAPS=0`（`parseLightmaps=false`）时启用，此时首次解析的 `WorldLights` 本来就是空 |
| `Tonemapping` 的 `1 << (2*(level+1))` 会创建 16K 目标 | **否**。该参数是 `sizeFactor` 除数，链路逐级缩小 |

## 4. 覆盖清单

| 目录 | C# 文件 | 规模 | 审阅重点 |
|---|---:|---:|---|
| 根目录 | 4 | 1,628 行 | 挂载生命周期、资源注册、音效缓存、设置/日志、PerfKit/聚合器 |
| `Formats/` | 19 | 7,136 行 | rBSP/MDL53/VTX/PCF/VMT 边界与内容解析、碰撞 contents、动画/物理 |
| `Pak/` | 14 | 3,502 行 | RPAK/VPK/STARPAK、VTF/DXBC、Miles bank 与事件运行时（含 P/Invoke 契约） |
| `Resource/` | 65 | 13,888 行 | 场景构建、streamer、碰撞查询/分类、材质/纹理/模型、天空/雾/灯、粒子/音频 |
| `PerfKit/` | 1 | 592 行 | 聚合构建器、实例批次、剔除器 |
| `Perf/` | 1 | 203 行 | 世界聚合流水线 |
| `Native/` | 3 个文件 | — | LZHAM/Miles 桥与导出签名、内存所有权、空指针 |
| 文档 | 34 | — | 与代码默认值/现状的一致性 |

本轮新增（并发会话）并已审阅：`Resource/CollisionQueries.cs`、`CollisionContents.cs`、
`Model.Metadata.cs`、`SoundEventPlayer.cs`、`Pak/MilesEventSession.cs`、`Native/MilesBridge/miles_events.inl`。

## 5. 格式与 CI

审阅开始时，模块级格式检查失败，共约 5,700 条诊断、48 个文件：

- `ENDOFLINE`：大批文件为 LF，而 `.editorconfig` 要求 `end_of_line = crlf`（`RpakArchive.cs`、`Mdl53Reader.cs`、
  `VtxReader.cs`、`PakFileDecoder.cs` 等）；
- `WHITESPACE`：`Model.cs`、`StaticPropStreamer.Lod.cs`、`ModelExporter.cs` 等；
- `IDE0062`：`Map.World.cs` 的本地函数可设为 `static`。

本轮已对模块执行 `dotnet format` 并复验：

```powershell
dotnet format engine/Mounting/Sandbox.Mounting.Titanfall2/Sandbox.Mounting.Titanfall2.csproj --no-restore
dotnet format engine/Mounting/Sandbox.Mounting.Titanfall2/Sandbox.Mounting.Titanfall2.csproj --verify-no-changes --no-restore
dotnet build  engine/Mounting/Sandbox.Mounting.Titanfall2/Sandbox.Mounting.Titanfall2.csproj -c Release -p:SkipCopyOutputDll=true
```

结果：`format` 退出码 0、`verify` 退出码 0、构建 0 警告 / 0 错误（Debug 与 Release 各跑一次）。
未运行全解决方案的 `SboxBuild format`（会重写引擎其他项目，超出本模块范围）。

原生桥的 C++ 改动用模块自带脚本验证了可编译：

```powershell
pwsh engine/Mounting/Sandbox.Mounting.Titanfall2/Native/MilesBridge/build.ps1 -OutputDirectory .tmp/native-build-check
```

产物已复制到模块 `bin/Debug` 与 `bin/Release` 供本地测试；**部署/发布仍然必须重新执行该脚本**
（`game/mount/titanfall2/titanfall2_miles.dll` 不会由 `dotnet build` 更新）。

## 6. 建议后续

1. **实机回归**：载入 `mp_angel_city` 与 `mp_wargames`，确认世界/道具/碰撞、lightmap 路径、
   `ttf2_map_ready`、Miles 环境音与 `trigger_soundscape` 均正常；再跑一轮专用服务器 smoke。
2. **恢复默认热加载**：把 lightmap/WORLD_LIGHTS 纳入 `Map.Cache`（版本升级 + round-trip 校验），
   否则 `BSP_LIGHTMAPS=1` 的默认路径没有派生解析缓存。
3. **收尾资源所有权**：为 `WorldBuildResult.OwnedResources` 设计挂载级释放点，或删除
   `SceneResourceOwner.Configure(ownedResources)` 死参数。
4. **原生 Miles 收尾**：确认 `MilesBankUnload`/项目关闭符号后，在 `DestroyEventSession` 释放 bank/project。
5. **提交拆分**：并发会话与本次审阅的改动目前混在同一工作树，建议按“专用服务器/碰撞分类”“Miles 事件”
   “本轮健壮性修复”“格式”拆成独立提交，便于回滚与评审。
