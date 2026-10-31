# Titanfall 2 全文件审阅索引

审阅基线：2026-10-30，本轮开始时 `rg --files --hidden` 返回的全部 237 个文件；不含忽略的 bin/obj 和生成缓存。这里逐项记录结构覆盖，未把静态扫描等同于逐条运行验证。第三方 .lib 仅核对存在、指纹与 ABI 来源。新增的主报告与本索引是输出文件，不计入 237 项基线。

详细结论见 [FULL_REVIEW_2026_10_30.md](FULL_REVIEW_2026_10_30.md)。完整 SHA256 见 [CSV](FULL_REVIEW_FILE_INDEX.csv)。

| 文件 | 行数 | 关键结构 | 覆盖与结论 |
| --- | ---: | --- | --- |
| [.gitignore](.gitignore) | 1 | — | 构建输入、输出路径、排除规则或依赖来源。构建/配置/交付结构审阅 |
| [ANGEL_CITY_MATERIAL_FIXES.md](ANGEL_CITY_MATERIAL_FIXES.md) | 67 | — | 天使城：草片、广告屏、地面覆盖层与水岸。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [ARCHITECTURE.md](ARCHITECTURE.md) | 354 | — | **本轮修改**；Titanfall 2 挂载项目 · 架构与性能手册。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [ARCHIVE_REVIEW_2026_10_30.md](ARCHIVE_REVIEW_2026_10_30.md) | 65 | — | Titanfall 2 归档读取审阅与修复。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [Assembly.cs](Assembly.cs) | 7 | — | 全局 using；不改变 public API allow-list。关键结构/调用链审阅 |
| [Build/.gitignore](Build/.gitignore) | 3 | — | 构建输入、输出路径、排除规则或依赖来源。构建/配置/交付结构审阅 |
| [Build/NuGet.Config](Build/NuGet.Config) | 3 | — | 构建输入、输出路径、排除规则或依赖来源。构建/配置/交付结构审阅 |
| [Build/Steam.csproj](Build/Steam.csproj) | 18 | — | 已安装 SDK 引用、排除离线程序与生成文件；本轮未重新构建 Steam。构建/配置/交付结构审阅 |
| [CM_COLLISION_CORRECTIONS.md](CM_COLLISION_CORRECTIONS.md) | 114 | — | CM 世界碰撞纠正与离线地图矩阵（2026-10-29）。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [CODE_AUDIT.md](CODE_AUDIT.md) | 80 | — | Titanfall 2 挂载代码审计与修复记录。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [COLLISION_AUDIT.md](COLLISION_AUDIT.md) | 192 | — | Titanfall 2 挂载碰撞审计与后续方案。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [COLLISION_DIAGNOSTICS_RUNTIME.md](COLLISION_DIAGNOSTICS_RUNTIME.md) | 52 | — | 碰撞来源诊断与资源生命周期运行回归。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [COLLISION_LOOK.md](COLLISION_LOOK.md) | 111 | — | 对准物体的碰撞命中诊断。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [DEPTH_BINDING_PERFORMANCE_FIX.md](DEPTH_BINDING_PERFORMANCE_FIX.md) | 61 | — | 实例深度预通道的探针绑定开销。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [Deploy-Steam.ps1](Deploy-Steam.ps1) | 132 | — | **本轮修改**；SDK/锁预检、备份/哈希、shader 源和回滚；本轮未执行部署。构建/配置/交付结构审阅 |
| [ENVIRONMENT_AND_CAVITY_FIXES.md](ENVIRONMENT_AND_CAVITY_FIXES.md) | 136 | — | 环境区域、cavity 反射与法线预通道修复。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [FOG_VOLUME_FIXES.md](FOG_VOLUME_FIXES.md) | 47 | — | Fog volume 默认绑定修复（2026-10-29）。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [FOLIAGE_BLEND_FIXES.md](FOLIAGE_BLEND_FIXES.md) | 61 | — | 殖民地远处植被与地面接缝。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [Formats/Titanfall2BspBrushModels.cs](Formats/Titanfall2BspBrushModels.cs) | 122 | Titanfall2BspReader; BspBrushModel; BspBrushModels | 格式表边界、数据布局、调用者与错误回退。关键结构/调用链审阅 |
| [Formats/Titanfall2BspCollisionReader.cs](Formats/Titanfall2BspCollisionReader.cs) | 438 | Titanfall2BspReader; BspWorldCollision; BspCollisionPart; CollisionPrimitive; CollisionPlane | 格式表边界、数据布局、调用者与错误回退。关键结构/调用链审阅 |
| [Formats/Titanfall2BspFogVolumes.cs](Formats/Titanfall2BspFogVolumes.cs) | 87 | Titanfall2BspReader | 格式表边界、数据布局、调用者与错误回退。关键结构/调用链审阅 |
| [Formats/Titanfall2BspReader.cs](Formats/Titanfall2BspReader.cs) | 1420 | Titanfall2BspReader; ParsedBsp; BspLightmaps; BspLightmapPage; BspWorld; WorldMesh; BspCubemap; WorldVertex; WorldVertexColor; VisibilityCellMask; BspVisibility; VisibilityPlane; VisibilityBspNode; VisibilityPortal; StaticPropInstance; LumpHeader; ModelEntry; MeshEntry; MaterialSortEntry; TextureDataEntry; VisibilityCellEntry; VisibilityPortalEntry; VisibilityAabbNode; VertexRef; GameLumpChildHeader | 格式表边界、数据布局、调用者与错误回退。关键结构/调用链审阅 |
| [Formats/Titanfall2EntityText.cs](Formats/Titanfall2EntityText.cs) | 39 | Titanfall2EntityText | 格式表边界、数据布局、调用者与错误回退。关键结构/调用链审阅 |
| [Formats/Titanfall2FogEnvironment.cs](Formats/Titanfall2FogEnvironment.cs) | 32 | Titanfall2FogEnvironment | 格式表边界、数据布局、调用者与错误回退。关键结构/调用链审阅 |
| [Formats/Titanfall2LightEnvironment.cs](Formats/Titanfall2LightEnvironment.cs) | 112 | Titanfall2LightEnvironment; Volume | 格式表边界、数据布局、调用者与错误回退。关键结构/调用链审阅 |
| [Formats/Titanfall2LightProbe.cs](Formats/Titanfall2LightProbe.cs) | 66 | Titanfall2LightProbe | 格式表边界、数据布局、调用者与错误回退。关键结构/调用链审阅 |
| [Formats/Titanfall2Mdl53AnimationReader.cs](Formats/Titanfall2Mdl53AnimationReader.cs) | 477 | Titanfall2Mdl53AnimationReader; BonePose; DecodedAnimation | 格式表边界、数据布局、调用者与错误回退。关键结构/调用链审阅 |
| [Formats/Titanfall2Mdl53PhysicsReader.cs](Formats/Titanfall2Mdl53PhysicsReader.cs) | 390 | Titanfall2Mdl53PhysicsReader; PhysicsHull; PhysicsSolid; RagdollConstraint; PhysicsData | 格式表边界、数据布局、调用者与错误回退。关键结构/调用链审阅 |
| [Formats/Titanfall2Mdl53Reader.cs](Formats/Titanfall2Mdl53Reader.cs) | 732 | Titanfall2Mdl53Reader; ParsedModel; BoneInfo; LinearBoneInfo; AnimationInfo; SequenceInfo; IncludedModelInfo; HitboxSetInfo; HitboxInfo; TextureInfo; BodyPartInfo; ModelInfo; MeshInfo | 格式表边界、数据布局、调用者与错误回退。关键结构/调用链审阅 |
| [Formats/Titanfall2Mdl53Structures.cs](Formats/Titanfall2Mdl53Structures.cs) | 617 | Titanfall2Mdl53Constants; Matrix3x4; Mdl53StudioHeader; Mdl53Bone; Mdl53JiggleBone; Mdl53LinearBoneTable; Mdl53SourceBoneTransform; Mdl53Attachment; Mdl53HitboxSet; Mdl53Hitbox; Mdl53AnimValue; Mdl53AnimValuePointer; Mdl53RleAnimation; Mdl53AnimationSection; Mdl53Movement; Mdl53FrameMovement; Mdl53AnimationDescription; Mdl53Event; Mdl53AutoLayer; Mdl53IkLock; Mdl53ActivityModifier; Mdl53SequenceDescription; Mdl53IncludedModel; Mdl53MeshVertexData; Mdl53Mesh; Mdl53Model; Mdl53BodyPart; Mdl53IkLink; Mdl53IkChain; Mdl53PoseParameter; Mdl53Texture; Mdl53PerTriHeader; VvdWeightExtra; VvdBoneWeight; VvdVertex; VvdFixup; VvdHeader; VvcHeader; VvcColor32; Titanfall2Mdl53Binary | 格式表边界、数据布局、调用者与错误回退。关键结构/调用链审阅 |
| [Formats/Titanfall2MeshExtractor.cs](Formats/Titanfall2MeshExtractor.cs) | 376 | Titanfall2MeshExtractor; ExtractedLod; MeshGeometry; ExtractedVertex | **本轮修改**；本轮：按 strip 拓扑展开、交替绕序、退化连接过滤；保留 bodygroup/VVD 修复。关键结构/调用链审阅 |
| [Formats/Titanfall2PcfReader.cs](Formats/Titanfall2PcfReader.cs) | 887 | Titanfall2PcfReader; DmxReader; DmxElement; DmxAttribute; Titanfall2ParticleShape; Titanfall2ParticleRenderer; RgbaColor; ParticleFadeDefinition; ParticleScaleDefinition; ParticleControlPointDefinition; ParticlePathDefinition; ParticleDistanceRemapDefinition; ParticleColorRemapDefinition; ParticleVisibilityDefinition; ParticleScalarGraphDefinition; Titanfall2ParticleDefinition | 格式表边界、数据布局、调用者与错误回退。关键结构/调用链审阅 |
| [Formats/Titanfall2SoundscapeScript.cs](Formats/Titanfall2SoundscapeScript.cs) | 38 | Titanfall2SoundscapeScript | 格式表边界、数据布局、调用者与错误回退。关键结构/调用链审阅 |
| [Formats/Titanfall2VmtReader.cs](Formats/Titanfall2VmtReader.cs) | 479 | Titanfall2LegacyPath; Titanfall2VmtDefinition; VmtBlock; VmtTextureTransform; VmtNoiseRange; Titanfall2VmtReader; TokenReader | 格式表边界、数据布局、调用者与错误回退。关键结构/调用链审阅 |
| [Formats/Titanfall2VtxReader.cs](Formats/Titanfall2VtxReader.cs) | 495 | Titanfall2VtxReader; ParsedVtx; MaterialReplacementListInfo; MaterialReplacementInfo; BodyPartInfo; ModelInfo; LodInfo; MeshInfo; StripGroupInfo; StripInfo | 格式表边界、数据布局、调用者与错误回退。关键结构/调用链审阅 |
| [Formats/Titanfall2VtxStructures.cs](Formats/Titanfall2VtxStructures.cs) | 114 | Titanfall2VtxConstants; VtxBoneStateChange; VtxVertex; VtxStripHeader; VtxStripGroupHeader; VtxMeshHeader; VtxModelLodHeader; VtxModelHeader; VtxBodyPartHeader; VtxMaterialReplacementHeader; VtxMaterialReplacementListHeader; VtxHeader | 格式表边界、数据布局、调用者与错误回退。关键结构/调用链审阅 |
| [Formats/Titanfall2WorldLight.cs](Formats/Titanfall2WorldLight.cs) | 98 | Titanfall2WorldLight | 格式表边界、数据布局、调用者与错误回退。关键结构/调用链审阅 |
| [GameMount.cs](GameMount.cs) | 1088 | Titanfall2Mount; MountedAssetSource | Steam 安装发现、RPAK 优先注册、VPK 依赖回退、Miles 缓存与 Shutdown。关键结构/调用链审阅 |
| [HDR_DISPLAY_AND_BLOOM.md](HDR_DISPLAY_AND_BLOOM.md) | 65 | — | HDR 显示、曝光与核心 Bloom。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) | 489 | — | Titanfall 2 挂载系统完善计划。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [KODAI_HAZE_FIXES.md](KODAI_HAZE_FIXES.md) | 48 | — | 虎大：光片、环境雾片和灰尘的修正。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [LIFECYCLE_REVIEW_2026_10_30.md](LIFECYCLE_REVIEW_2026_10_30.md) | 59 | — | Titanfall 2 地图宿主生命周期审阅与修复。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [LIFETIME_COLLISION_FIXES.md](LIFETIME_COLLISION_FIXES.md) | 32 | — | 场景生命周期、导航与碰撞数据修复。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [LIGHTING_DATA_AUDIT.md](LIGHTING_DATA_AUDIT.md) | 142 | — | 地形与静态模型光照：原始数据复核与接入顺序。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [LOCAL_LIGHTS_PROGRESS.md](LOCAL_LIGHTS_PROGRESS.md) | 66 | — | 编译局部灯接入与当前验证边界。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [MATERIAL_PROXY_REVIEW_2026_10_30.md](MATERIAL_PROXY_REVIEW_2026_10_30.md) | 67 | — | Titanfall 2 材质代理调试与修复。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [MODEL_GEOMETRY_REVIEW_2026_10_30.md](MODEL_GEOMETRY_REVIEW_2026_10_30.md) | 59 | — | Titanfall 2 模型几何审阅与修复。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [MOUNT_COMPLETENESS.md](MOUNT_COMPLETENESS.md) | 113 | — | Titanfall 2 挂载完成度与继续工作记录。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [MOUNT_REQUIREMENTS_STATUS.md](MOUNT_REQUIREMENTS_STATUS.md) | 74 | — | **本轮修改**；ttf2 游戏接入需求对照（2026-10-29）。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [Native/LzhamBridge/README.md](Native/LzhamBridge/README.md) | 25 | — | Titanfall 2 LZHAM bridge。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/LICENSE.txt](Native/LzhamBridge/ThirdParty/lzham-alpha8/LICENSE.txt) | 19 | — | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/README.md](Native/LzhamBridge/ThirdParty/lzham-alpha8/README.md) | 16 | — | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham.h) | 765 | lzham_z_internal_state; ilzham | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_assert.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_assert.h) | 40 | — | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_checksum.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_checksum.h) | 13 | — | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_config.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_config.h) | 22 | — | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_core.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_core.h) | 171 | — | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_helpers.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_helpers.h) | 54 | — | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_huffman_codes.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_huffman_codes.h) | 14 | — | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_lzbase.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_lzbase.h) | 45 | CLZBase | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_match_accel.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_match_accel.h) | 146 | node; dict_match; search_accelerator | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_math.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_math.h) | 113 | — | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_mem.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_mem.h) | 112 | — | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_null_threading.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_null_threading.h) | 97 | semaphore; task_pool; executable_task | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_platform.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_platform.h) | 284 | — | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_polar_codes.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_polar_codes.h) | 14 | — | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_prefix_coding.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_prefix_coding.h) | 144 | decoder_tables | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_pthreads_threading.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_pthreads_threading.h) | 383 | semaphore; timespec; spinlock; tsstack; task_pool; executable_task; task; task_flags; object_task_flags; object_task | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_static_lib.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_static_lib.h) | 57 | lzham_static_lib | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_symbol_codec.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_symbol_codec.h) | 556 | symbol_codec; adaptive_arith_data_model; raw_quasi_adaptive_huffman_data_model; quasi_adaptive_huffman_data_model; adaptive_bit_model; output_symbol | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_threading.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_threading.h) | 12 | — | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_timer.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_timer.h) | 99 | lzham_timer; var_args_t; scoped_perf_section | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_traits.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_traits.h) | 137 | scalar_type; bitwise_movable; bitwise_copyable | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_types.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_types.h) | 74 | empty_type | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_utils.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_utils.h) | 58 | — | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_vector.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_vector.h) | 588 | elemental_vector; vector | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_win32_threading.h](Native/LzhamBridge/ThirdParty/lzham-alpha8/include/lzham_win32_threading.h) | 368 | semaphore; tsstack; node; task_pool; executable_task; task; task_flags; object_task_flags; object_task | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/ThirdParty/lzham-alpha8/lib/liblzham_x64.lib](Native/LzhamBridge/ThirdParty/lzham-alpha8/lib/liblzham_x64.lib) | 6520 | — | vendored LZHAM ABI 包；保持原文件。第三方接口/许可/文件完整性盘点；未逐行审计算法或反汇编 .lib |
| [Native/LzhamBridge/build.ps1](Native/LzhamBridge/build.ps1) | 27 | — | 构建输入、输出路径、排除规则或依赖来源。构建/配置/交付结构审阅 |
| [Native/LzhamBridge/lzham_bridge.cpp](Native/LzhamBridge/lzham_bridge.cpp) | 35 | — | LZHAM alpha ABI、profile 与输出长度。关键结构/调用链审阅 |
| [Native/MilesBridge/README.md](Native/MilesBridge/README.md) | 18 | — | Titanfall 2 Miles/Bink Audio bridge。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [Native/MilesBridge/build.ps1](Native/MilesBridge/build.ps1) | 22 | — | 构建输入、输出路径、排除规则或依赖来源。构建/配置/交付结构审阅 |
| [Native/MilesBridge/miles_bridge.cpp](Native/MilesBridge/miles_bridge.cpp) | 338 | DriverConfig; MemoryOutputConfig; MilesApi; CaptureContext | 本地 DLL exports、串行 decode、PCM/driver/sample 清理与超时。关键结构/调用链审阅 |
| [PARTICLE_BEAM_FIXES.md](PARTICLE_BEAM_FIXES.md) | 86 | — | Titanfall 2 光束与粒子修复记录。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [PROBE_UPLOAD_PERFORMANCE_FIX.md](PROBE_UPLOAD_PERFORMANCE_FIX.md) | 49 | — | 天使城实例探针索引上传优化。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [Pak/AssetSource.cs](Pak/AssetSource.cs) | 172 | ITitanfall2AssetSource; ITitanfall2SiblingAssetSource; ITitanfall2PathAssetSource; ITitanfall2TextureSource; LooseAssetSource; VpkPathResolver; VpkAssetSource; RpakTextureAssetSource | **本轮修改**；本轮：game-relative 依赖路径、双分隔符、越界读取拒绝、相邻依赖回退。关键结构/调用链审阅 |
| [Pak/DxbcReflection.cs](Pak/DxbcReflection.cs) | 204 | DxbcVariable; DxbcResourceBinding; DxbcConstantBufferLayout; DxbcShaderReflection | RDEF 表边界、变量 used flags、纹理 bind-point 映射。关键结构/调用链审阅 |
| [Pak/LzhamDecoder.cs](Pak/LzhamDecoder.cs) | 101 | LzhamDecoder | bridge ABI、长度与错误传播；无新 native 改动。关键结构/调用链审阅 |
| [Pak/MilesBankArchive.cs](Pak/MilesBankArchive.cs) | 222 | MilesBankArchive; MilesSoundAsset; MilesStreamFile | v13 source records、语言选择、MSTR 范围与 BCF 重组。关键结构/调用链审阅 |
| [Pak/MilesBinkDecoder.cs](Pak/MilesBinkDecoder.cs) | 115 | MilesBinkDecoder | native 句柄和 PCM finally；本轮未执行真实音频解码。关键结构/调用链审阅 |
| [Pak/PakFileDecoder.cs](Pak/PakFileDecoder.cs) | 375 | PakFileDecoder; DecoderState | 完整缓冲解码、输出尺寸和回溯范围；保留已有溢出修复。关键结构/调用链审阅 |
| [Pak/RpakArchive.cs](Pak/RpakArchive.cs) | 563 | RpakArchive; StrongBufferEntry; RpakPage; RpakPagePointer; RpakAsset; RpakAssetData; RpakCacheStatistics | 页指针、patch 自包含页、索引缓存、弱引用与有界 strong LRU；跨页读取未武断禁止。关键结构/调用链审阅 |
| [Pak/RpakReader.cs](Pak/RpakReader.cs) | 102 | RpakReader; RpakHeader; RpakInspection | 只读 v7 header inspection；实际资源解码由 RpakArchive 承担。关键结构/调用链审阅 |
| [Pak/RpakTextureDecoder.cs](Pak/RpakTextureDecoder.cs) | 306 | RpakTextureDecoder; Mip; RpakTextureData; RpakTextureStatistics | BC/单通道纹理、流式 mip 与 STARPAK 条目、GPU 主线程创建。关键结构/调用链审阅 |
| [Pak/StarPakIndex.cs](Pak/StarPakIndex.cs) | 147 | StarPakIndex; StarPakStatistics | footer/dataEnd、重复偏移拒绝、失败不缓存；保留已有修复。关键结构/调用链审阅 |
| [Pak/TextureMipUpload.cs](Pak/TextureMipUpload.cs) | 30 | TextureMipUpload | 小于 BC 块的 mip 截止规则；不改源/cache 组织。关键结构/调用链审阅 |
| [Pak/VpkArchive.cs](Pak/VpkArchive.cs) | 299 | VpkArchive; VpkEntry; VpkChunk | 树终止、preload、embedded/external 卷、LZHAM；保留已有范围修复。关键结构/调用链审阅 |
| [Pak/VtfTextureDecoder.cs](Pak/VtfTextureDecoder.cs) | 701 | VtfTextureDecoder; VtfTextureData; VtfCubeTextureData; VtfSheetCoordinate | VTF 头、mip 排列、动画/粒子 sheet、cubemap faces 与失败回收。关键结构/调用链审阅 |
| [PerfKit/MountedScenePerfKit.cs](PerfKit/MountedScenePerfKit.cs) | 592 | MountedGeometryPart; MountedGeometryGroup; MountedAggregateFragment; MountedAggregateGeometry; MountedAggregate; MountedAggregateBuilder; IMountedInstanceBatch; MountedInstanceBatchOptions; MountedInstanceBatch; DrawSet; MountedInstanceCull | 聚合 fragment 上限、不可变 draw snapshot、逐实例 LOD/裁剪与 source identity。关键结构/调用链审阅 |
| [PerfKit/README.md](PerfKit/README.md) | 101 | — | **本轮修改**；PerfKit — 可复用的挂载场景性能件。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [Perf/CollisionVerification/CollisionVerification.csproj](Perf/CollisionVerification/CollisionVerification.csproj) | 16 | — | 构建输入、输出路径、排除规则或依赖来源。构建/配置/交付结构审阅 |
| [Perf/CollisionVerification/Offline/.gitignore](Perf/CollisionVerification/Offline/.gitignore) | 2 | — | 构建输入、输出路径、排除规则或依赖来源。构建/配置/交付结构审阅 |
| [Perf/CollisionVerification/Offline/CollisionAudit.csproj](Perf/CollisionVerification/Offline/CollisionAudit.csproj) | 20 | — | 构建输入、输出路径、排除规则或依赖来源。构建/配置/交付结构审阅 |
| [Perf/CollisionVerification/Offline/GeometryAudit.cs](Perf/CollisionVerification/Offline/GeometryAudit.cs) | 158 | Titanfall2BspReader; GeometryAuditResult | 生命周期、调用边界和资源所有权。关键结构/调用链审阅 |
| [Perf/CollisionVerification/Offline/Program.cs](Perf/CollisionVerification/Offline/Program.cs) | 131 | — | 生命周期、调用边界和资源所有权。关键结构/调用链审阅 |
| [Perf/CollisionVerification/VerifyCollisionLook.csx](Perf/CollisionVerification/VerifyCollisionLook.csx) | 107 | — | VerifyCollisionLook；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/CollisionVerification/VerifyCollisionProvenance.csx](Perf/CollisionVerification/VerifyCollisionProvenance.csx) | 82 | — | VerifyCollisionProvenance；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/CollisionVerification/VerifyStaticPropCollision.csx](Perf/CollisionVerification/VerifyStaticPropCollision.csx) | 58 | — | VerifyStaticPropCollision；未发现 finally；不据此单独判缺陷。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/README.md](Perf/MaterialVerification/README.md) | 175 | — | Mounted material GPU checks。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [Perf/MaterialVerification/VerifyAtmosphereSource.csx](Perf/MaterialVerification/VerifyAtmosphereSource.csx) | 50 | — | VerifyAtmosphereSource；未发现 finally；不据此单独判缺陷。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyCachedWorldLifetime.csx](Perf/MaterialVerification/VerifyCachedWorldLifetime.csx) | 54 | — | VerifyCachedWorldLifetime；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyCavity.csx](Perf/MaterialVerification/VerifyCavity.csx) | 57 | — | VerifyCavity；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyColorCorrection.csx](Perf/MaterialVerification/VerifyColorCorrection.csx) | 136 | — | VerifyColorCorrection；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyCompiledLocalLights.csx](Perf/MaterialVerification/VerifyCompiledLocalLights.csx) | 98 | — | VerifyCompiledLocalLights；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyDepthNormals.csx](Perf/MaterialVerification/VerifyDepthNormals.csx) | 44 | — | VerifyDepthNormals；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyEmissionUv.csx](Perf/MaterialVerification/VerifyEmissionUv.csx) | 55 | — | VerifyEmissionUv；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyEnvironmentSkyProbes.csx](Perf/MaterialVerification/VerifyEnvironmentSkyProbes.csx) | 97 | — | VerifyEnvironmentSkyProbes；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyEnvironmentSkyWorld.csx](Perf/MaterialVerification/VerifyEnvironmentSkyWorld.csx) | 22 | — | VerifyEnvironmentSkyWorld；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyExposureReduction.csx](Perf/MaterialVerification/VerifyExposureReduction.csx) | 20 | — | VerifyExposureReduction；未发现 finally；不据此单独判缺陷。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyFogVolumes.csx](Perf/MaterialVerification/VerifyFogVolumes.csx) | 50 | — | VerifyFogVolumes；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyGeneratedWorldResources.csx](Perf/MaterialVerification/VerifyGeneratedWorldResources.csx) | 19 | — | VerifyGeneratedWorldResources；未发现 finally；不据此单独判缺陷。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyHeightFog.csx](Perf/MaterialVerification/VerifyHeightFog.csx) | 31 | — | VerifyHeightFog；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyLayerBlend.csx](Perf/MaterialVerification/VerifyLayerBlend.csx) | 77 | — | VerifyLayerBlend；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyLayerOpacity.csx](Perf/MaterialVerification/VerifyLayerOpacity.csx) | 73 | — | VerifyLayerOpacity；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyLegacyGrid.csx](Perf/MaterialVerification/VerifyLegacyGrid.csx) | 74 | — | VerifyLegacyGrid；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyLightEnvironments.csx](Perf/MaterialVerification/VerifyLightEnvironments.csx) | 54 | — | VerifyLightEnvironments；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyLightProbeData.csx](Perf/MaterialVerification/VerifyLightProbeData.csx) | 72 | — | VerifyLightProbeData；未发现 finally；不据此单独判缺陷。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyLightmapUv.csx](Perf/MaterialVerification/VerifyLightmapUv.csx) | 45 | — | VerifyLightmapUv；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyLiveVmtProxies.csx](Perf/MaterialVerification/VerifyLiveVmtProxies.csx) | 17 | — | VerifyLiveVmtProxies；未发现 finally；不据此单独判缺陷。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyMapCache.csx](Perf/MaterialVerification/VerifyMapCache.csx) | 40 | — | VerifyMapCache；未发现 finally；不据此单独判缺陷。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyMaterialRouting.csx](Perf/MaterialVerification/VerifyMaterialRouting.csx) | 43 | — | VerifyMaterialRouting；未发现 finally；不据此单独判缺陷。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyModelFailure.csx](Perf/MaterialVerification/VerifyModelFailure.csx) | 26 | — | VerifyModelFailure；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyNativeProbeVolume.csx](Perf/MaterialVerification/VerifyNativeProbeVolume.csx) | 18 | — | VerifyNativeProbeVolume；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyNormalEncoding.csx](Perf/MaterialVerification/VerifyNormalEncoding.csx) | 35 | — | VerifyNormalEncoding；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyOpacityFade.csx](Perf/MaterialVerification/VerifyOpacityFade.csx) | 97 | — | VerifyOpacityFade；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyProbeBatchDepth.csx](Perf/MaterialVerification/VerifyProbeBatchDepth.csx) | 97 | — | VerifyProbeBatchDepth；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyProbeBatchLod.csx](Perf/MaterialVerification/VerifyProbeBatchLod.csx) | 88 | — | VerifyProbeBatchLod；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyProbeIndex.csx](Perf/MaterialVerification/VerifyProbeIndex.csx) | 69 | — | VerifyProbeIndex；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyProbeLightPasses.csx](Perf/MaterialVerification/VerifyProbeLightPasses.csx) | 46 | — | VerifyProbeLightPasses；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyProbeLighting.csx](Perf/MaterialVerification/VerifyProbeLighting.csx) | 91 | — | VerifyProbeLighting；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyProbeTransport.csx](Perf/MaterialVerification/VerifyProbeTransport.csx) | 61 | — | VerifyProbeTransport；未发现 finally；不据此单独判缺陷。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyPropVisibilityCoverage.csx](Perf/MaterialVerification/VerifyPropVisibilityCoverage.csx) | 17 | — | VerifyPropVisibilityCoverage；未发现 finally；不据此单独判缺陷。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyRetailBloom.csx](Perf/MaterialVerification/VerifyRetailBloom.csx) | 70 | — | VerifyRetailBloom；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyRetailDisplay.csx](Perf/MaterialVerification/VerifyRetailDisplay.csx) | 95 | — | VerifyRetailDisplay；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifySkyProjection.csx](Perf/MaterialVerification/VerifySkyProjection.csx) | 9 | — | VerifySkyProjection；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifySpecularWorkflow.csx](Perf/MaterialVerification/VerifySpecularWorkflow.csx) | 48 | — | VerifySpecularWorkflow；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifySurfaceCoverage.csx](Perf/MaterialVerification/VerifySurfaceCoverage.csx) | 42 | — | VerifySurfaceCoverage；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifySurfaceUv.csx](Perf/MaterialVerification/VerifySurfaceUv.csx) | 94 | — | VerifySurfaceUv；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyTextureMips.csx](Perf/MaterialVerification/VerifyTextureMips.csx) | 67 | — | VerifyTextureMips；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyVmtProxies.csx](Perf/MaterialVerification/VerifyVmtProxies.csx) | 100 | — | VerifyVmtProxies；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyVtfAtlasMips.csx](Perf/MaterialVerification/VerifyVtfAtlasMips.csx) | 74 | — | VerifyVtfAtlasMips；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyVtfCubeMips.csx](Perf/MaterialVerification/VerifyVtfCubeMips.csx) | 66 | — | VerifyVtfCubeMips；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyWargamesScene.csx](Perf/MaterialVerification/VerifyWargamesScene.csx) | 42 | — | VerifyWargamesScene；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyWaterComposite.csx](Perf/MaterialVerification/VerifyWaterComposite.csx) | 73 | — | VerifyWaterComposite；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyWorldLightData.csx](Perf/MaterialVerification/VerifyWorldLightData.csx) | 62 | — | VerifyWorldLightData；未发现 finally；不据此单独判缺陷。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/MaterialVerification/VerifyWorldSunLightmap.csx](Perf/MaterialVerification/VerifyWorldSunLightmap.csx) | 59 | — | VerifyWorldSunLightmap；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/ParticleVerification/README.md](Perf/ParticleVerification/README.md) | 51 | — | Particle and ribbon runtime checks。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [Perf/ParticleVerification/VerifyAtmosphereParticles.csx](Perf/ParticleVerification/VerifyAtmosphereParticles.csx) | 35 | — | VerifyAtmosphereParticles；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/ParticleVerification/VerifyBeaconClouds.csx](Perf/ParticleVerification/VerifyBeaconClouds.csx) | 23 | — | VerifyBeaconClouds；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/ParticleVerification/VerifyHeightFogParticles.csx](Perf/ParticleVerification/VerifyHeightFogParticles.csx) | 32 | — | VerifyHeightFogParticles；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/ParticleVerification/VerifyParticleGeometry.csx](Perf/ParticleVerification/VerifyParticleGeometry.csx) | 171 | — | VerifyParticleGeometry；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/ParticleVerification/VerifyParticleMapData.csx](Perf/ParticleVerification/VerifyParticleMapData.csx) | 45 | — | VerifyParticleMapData；未发现 finally；不据此单独判缺陷。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/ParticleVerification/VerifyParticleOrientation.csx](Perf/ParticleVerification/VerifyParticleOrientation.csx) | 73 | — | VerifyParticleOrientation；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/ParticleVerification/VerifyParticleQueue.csx](Perf/ParticleVerification/VerifyParticleQueue.csx) | 47 | — | VerifyParticleQueue；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/ParticleVerification/VerifySharedParticleLifetime.csx](Perf/ParticleVerification/VerifySharedParticleLifetime.csx) | 71 | — | VerifySharedParticleLifetime；含 finally 清理。脚本入口、依赖与清理结构扫描；本轮未运行 |
| [Perf/Titanfall2WorldAggregator.cs](Perf/Titanfall2WorldAggregator.cs) | 203 | Titanfall2WorldAggregator; MaterialBatch; Slice; Prepared | **本轮修改**；本轮：producer cancellation + join，主线程异常时退出有界队列。关键结构/调用链审阅 |
| [READY_LIFECYCLE_FIXES.md](READY_LIFECYCLE_FIXES.md) | 45 | — | 纯世界 ready 发布、损坏数据与碰撞生命周期修复。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [REVIEW_2026_10_30.md](REVIEW_2026_10_30.md) | 128 | — | Titanfall 2 挂载审阅、性能与碰撞报告。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [Resource/BrushModel.cs](Resource/BrushModel.cs) | 165 | Titanfall2BrushModelSource; Titanfall2BrushModelLoader; Titanfall2BrushModelData; Titanfall2BrushCollisionPart | 生命周期、调用边界和资源所有权。关键结构/调用链审阅 |
| [Resource/CollisionDiagnostics.cs](Resource/CollisionDiagnostics.cs) | 130 | Titanfall2CollisionDiagnostics | 真实 trace、忽略层级、原始来源、console 注册及输入边界。关键结构/调用链审阅 |
| [Resource/CollisionFilter.cs](Resource/CollisionFilter.cs) | 80 | Titanfall2CollisionFilter | render fallback 材质过滤；仍是近似几何规则。关键结构/调用链审阅 |
| [Resource/CollisionSource.cs](Resource/CollisionSource.cs) | 79 | Titanfall2ModelCollisionData; Titanfall2CollisionSource | 原始/有效 contents 和实例身份；不猜 Source 2 query 分类。关键结构/调用链审阅 |
| [Resource/ColorCorrection.cs](Resource/ColorCorrection.cs) | 61 | Titanfall2ColorCorrection | 32³ LUT、RGB/RGBA、volume 权重和损坏 render 单次拒绝。关键结构/调用链审阅 |
| [Resource/CubemapController.cs](Resource/CubemapController.cs) | 139 | Titanfall2CubemapController | 每场景纹理重建、probe 复用、延迟 fallback 和销毁顺序。关键结构/调用链审阅 |
| [Resource/DeferredNavMeshBuilder.cs](Resource/DeferredNavMeshBuilder.cs) | 135 | Titanfall2DeferredNavMeshBuilder; NavigationState | 每 Scene users、恢复宿主 filter、延迟请求；与碰撞来源独立。关键结构/调用链审阅 |
| [Resource/DynamicMover.cs](Resource/DynamicMover.cs) | 61 | Titanfall2RotatingMover | 生命周期、调用边界和资源所有权。关键结构/调用链审阅 |
| [Resource/EnvironmentController.cs](Resource/EnvironmentController.cs) | 76 | Titanfall2EnvironmentController | 环境体积选择和场景 RenderAttributes；多地图叠加所有权仍需设计。关键结构/调用链审阅 |
| [Resource/HeightFog.cs](Resource/HeightFog.cs) | 116 | Titanfall2HeightFog; Titanfall2FogParameters | 每视图体积选择、线性颜色与 5-vector 常量布局。关键结构/调用链审阅 |
| [Resource/LegacyMaterial.cs](Resource/LegacyMaterial.cs) | 151 | LegacyMaterialLoader | 生命周期、调用边界和资源所有权。关键结构/调用链审阅 |
| [Resource/LocalLightBuffer.cs](Resource/LocalLightBuffer.cs) | 82 | Titanfall2LocalLightData; Titanfall2LocalLightBuffer | 生命周期、调用边界和资源所有权。关键结构/调用链审阅 |
| [Resource/LocalLightController.cs](Resource/LocalLightController.cs) | 66 | Titanfall2LocalLightController | 序列化 compiled lights、RTL 页映射、场景属性解绑。关键结构/调用链审阅 |
| [Resource/Map.Cache.cs](Resource/Map.Cache.cs) | 92 | Titanfall2MapCache | 生命周期、调用边界和资源所有权。关键结构/调用链审阅 |
| [Resource/Map.Collision.cs](Resource/Map.Collision.cs) | 76 | MapLoader | CM 分块提交与 sidecar 选择；保留原始 contents，未转换所有 gameplay masks。关键结构/调用链审阅 |
| [Resource/Map.Environment.cs](Resource/Map.Environment.cs) | 272 | MapLoader | sun/sky、compiled light、fog、LUT/曝光及 entity 分区回退。关键结构/调用链审阅 |
| [Resource/Map.Spawns.cs](Resource/Map.Spawns.cs) | 546 | MapLoader | spawn/sky/FX/audio/dynamic props；纯世界模式与 legacy 预览分支。关键结构/调用链审阅 |
| [Resource/Map.StaticPropAggregation.cs](Resource/Map.StaticPropAggregation.cs) | 87 | MapLoader | 实验静态烘焙资格、LOD/skin 排除和顶点预算。关键结构/调用链审阅 |
| [Resource/Map.World.cs](Resource/Map.World.cs) | 468 | MapLoader | 材质分组、opaque 聚合、透明/水独立 renderer、lightmap 与 fallback collision。关键结构/调用链审阅 |
| [Resource/Map.cs](Resource/Map.cs) | 231 | MapLoader | 解析/派生缓存、world/inline 分离、pure-world 契约与 ready 依赖。关键结构/调用链审阅 |
| [Resource/MapAudio.cs](Resource/MapAudio.cs) | 364 | Titanfall2MapAudio; Emitter; SoundscapeZone; EmitterState | **本轮修改**；本轮：句柄/zone 所有权、启停、原子解析、距离迟滞和启动预算。关键结构/调用链审阅 |
| [Resource/MapRuntimeTypes.cs](Resource/MapRuntimeTypes.cs) | 38 | CollisionCell; CollisionChunk; SkyboxCandidate; VistaOriginKey | 生命周期、调用边界和资源所有权。关键结构/调用链审阅 |
| [Resource/MapWorldTypes.cs](Resource/MapWorldTypes.cs) | 80 | TitanfallMapVertex; MapMeshGroup; WorldMaterialKey; RuntimeLightmapPage; RuntimeLightmaps; WorldRenderCell; WorldRenderChunk; WorldBuildResult | 生命周期、调用边界和资源所有权。关键结构/调用链审阅 |
| [Resource/Material.Cache.cs](Resource/Material.Cache.cs) | 27 | Titanfall2Mount | 生命周期、调用边界和资源所有权。关键结构/调用链审阅 |
| [Resource/Material.cs](Resource/Material.cs) | 687 | Titanfall2DecalMode; Titanfall2MaterialMode; Titanfall2MaterialMetadata; MaterialLoader; VistaMaterialMode | MATL 元数据、blend/cull、legacy 行为合并与纹理 GUID 优先级。关键结构/调用链审阅 |
| [Resource/MaterialAnimator.cs](Resource/MaterialAnimator.cs) | 266 | Titanfall2MaterialAnimationRegistry; Entry; Titanfall2MaterialAnimator | 弱注册表、proxy 顺序、滚动/周期/目标参数；保留已有修复。关键结构/调用链审阅 |
| [Resource/MaterialDescriptor.cs](Resource/MaterialDescriptor.cs) | 471 | Titanfall2MaterialSourceKind; Titanfall2TextureSemantic; Titanfall2DepthMode; Titanfall2CullMode; Titanfall2TextureBinding; Titanfall2MaterialTextureReference; Titanfall2MaterialConstant; Titanfall2RuntimeExpression; Titanfall2MaterialDescriptor | 统一材质描述、语义/层/UV 选择、运行时表达式和缺失纹理回退。关键结构/调用链审阅 |
| [Resource/MaterialParameters.cs](Resource/MaterialParameters.cs) | 136 | Titanfall2UvTransform; Titanfall2MaterialParameters | 生命周期、调用边界和资源所有权。关键结构/调用链审阅 |
| [Resource/MaterialPrefetch.cs](Resource/MaterialPrefetch.cs) | 131 | Titanfall2MaterialPrefetch; Titanfall2Mount | 有界 CPU 预取、取消/等待、prepare budget 释放和主线程 GPU 创建。关键结构/调用链审阅 |
| [Resource/Model.Cache.cs](Resource/Model.Cache.cs) | 58 | ModelLoader; StaticMeshGeometry; StaticLodGeometry | 生命周期、调用边界和资源所有权。关键结构/调用链审阅 |
| [Resource/Model.cs](Resource/Model.cs) | 1353 | ModelLoader; StaticInstanceEligibilityValue; TitanfallVertex; TitanfallSkinnedVertex; SkeletonData; AnimationSourceData; CollisionHullData; RagdollBodyData | native/legacy LOD、骨架动画、VPHY/回退碰撞；skin/attachments 等见能力缺口。关键结构/调用链审阅 |
| [Resource/ModelExporter.cs](Resource/ModelExporter.cs) | 814 | Titanfall2Mount; DmxFaceSetData; ExportSkeleton; Titanfall2ModelExportResult | 导出路径包含检查、DMX/VMDL/material 输出与缩放限制；无完整导出验收。关键结构/调用链审阅 |
| [Resource/ParticleCatalog.cs](Resource/ParticleCatalog.cs) | 127 | Titanfall2Mount; ParticleFileSource; ParticleCatalogProgress | catalog 锁、增量解析、失败统计与 mount reset。关键结构/调用链审阅 |
| [Resource/ParticleControlPoints.cs](Resource/ParticleControlPoints.cs) | 193 | Titanfall2ParticleControlPoints; Titanfall2ParticleGeometry | 世界控制点、路径/距离/颜色 remap、trail 和可见性。关键结构/调用链审阅 |
| [Resource/ParticleRampRenderer.cs](Resource/ParticleRampRenderer.cs) | 163 | Titanfall2ParticleRampRenderer; Item | 独立 render snapshot、颜色 ramp、sheet 动画、锁和禁用清理。关键结构/调用链审阅 |
| [Resource/ParticleRopeRenderer.cs](Resource/ParticleRopeRenderer.cs) | 115 | Titanfall2ParticleRopeRenderer | 粒子顺序、样条细分、宽度/UV 和 line 清理。关键结构/调用链审阅 |
| [Resource/ParticleStreamer.cs](Resource/ParticleStreamer.cs) | 923 | Titanfall2ParticleStreamer; RuntimeEntity; SpawnedEntity; Titanfall2ParticleFactory; Titanfall2ParticleMapControlPoint; Titanfall2ParticleMapEntity | **本轮修改**；本轮：FX 启停、NotSaved 子树；PCF catalog、预算、factory 与未知效果回退。关键结构/调用链审阅 |
| [Resource/ProcessedCache.cs](Resource/ProcessedCache.cs) | 208 | Titanfall2ProcessedCache; CacheStatistics; CacheReader; Titanfall2Mount | 程序集/内容指纹、gzip 完整性、原子发布、内存与磁盘预算。关键结构/调用链审阅 |
| [Resource/SceneResourceOwner.cs](Resource/SceneResourceOwner.cs) | 26 | Titanfall2SceneResourceOwner | 只释放 scene-exclusive 资源，挂载级缓存由 mount 释放。关键结构/调用链审阅 |
| [Resource/SkyDome.cs](Resource/SkyDome.cs) | 199 | Titanfall2SkyDome; DomeEntry | **本轮修改**；本轮：native 模型启停、完整 payload 解码、部分失败清理。关键结构/调用链审阅 |
| [Resource/SkyMaterial.cs](Resource/SkyMaterial.cs) | 27 | Titanfall2SkyMaterialLoader | 生命周期、调用边界和资源所有权。关键结构/调用链审阅 |
| [Resource/Skybox3D.cs](Resource/Skybox3D.cs) | 445 | Titanfall2Skybox3D; Titanfall2SkyboxModel; Titanfall2SkyboxFog | 反射桥接、独立 SceneWorld、视图位置抵消、world fog 与失败销毁。关键结构/调用链审阅 |
| [Resource/Sound.cs](Resource/Sound.cs) | 47 | SoundLoader; MilesSoundLoader | WAV 与 Miles/BCF 到 SoundFile 资源的失败路径。关键结构/调用链审阅 |
| [Resource/StaticPropLightProbe.cs](Resource/StaticPropLightProbe.cs) | 243 | Titanfall2ProbeData; Titanfall2PlainInstanceBatch; Titanfall2ProbeInstanceBatch; IndexTable | 80-byte 系数、source-index 子集 buffer、有界缓存、depth opt-in。关键结构/调用链审阅 |
| [Resource/StaticPropRuntimeTypes.cs](Resource/StaticPropRuntimeTypes.cs) | 185 | Titanfall2StaticPropStreamer; StreamedProp; PropInstance; PendingBatch; CellCoordinate; PropCluster; ClusterCoordinate; PropCell | instance/cell/cluster/pending-batch 身份、source index 与变换不可变约定。关键结构/调用链审阅 |
| [Resource/StaticPropStreamer.Batches.cs](Resource/StaticPropStreamer.Batches.cs) | 134 | Titanfall2StaticPropStreamer | **本轮修改**；本轮：Active 渲染门控；cluster 唯一所有权、失败 fallback 与销毁。关键结构/调用链审阅 |
| [Resource/StaticPropStreamer.Collision.cs](Resource/StaticPropStreamer.Collision.cs) | 192 | Titanfall2StaticPropStreamer | **本轮修改**；本轮：生成 collider 不保存/复制；contents、常驻/距离激活和预算。关键结构/调用链审阅 |
| [Resource/StaticPropStreamer.Loading.cs](Resource/StaticPropStreamer.Loading.cs) | 451 | Titanfall2StaticPropStreamer | payload v3-v6、PVS restore、cell/cluster 列表；空旧场景保持兼容。关键结构/调用链审阅 |
| [Resource/StaticPropStreamer.Lod.cs](Resource/StaticPropStreamer.Lod.cs) | 312 | Titanfall2StaticPropStreamer | native/legacy 切换、队列收敛、模型失败重试与替换批次。关键结构/调用链审阅 |
| [Resource/StaticPropStreamer.Visibility.cs](Resource/StaticPropStreamer.Visibility.cs) | 290 | Titanfall2StaticPropStreamer | PVS 保守回退、逐实例距离、阴影状态；fallback 子集仍需实图压力验收。关键结构/调用链审阅 |
| [Resource/StaticPropStreamer.cs](Resource/StaticPropStreamer.cs) | 566 | Titanfall2StaticPropStreamer | **本轮修改**；本轮：native 对象启停、初次 Configure、load 完成状态；resident/PVS/LOD 调度。关键结构/调用链审阅 |
| [Resource/Texture.Cache.cs](Resource/Texture.Cache.cs) | 51 | Titanfall2TextureCache | **本轮修改**；本轮：缓存 mip 完整字节数、格式、cube 形状与重建回退。关键结构/调用链审阅 |
| [Resource/Texture.cs](Resource/Texture.cs) | 126 | Titanfall2TextureLoader; PreparedTexture | CPU prepare/主线程 GPU、DDS/VTF；TGA 入口仍是未实现 stub。关键结构/调用链审阅 |
| [Resource/Titanfall2Map.cs](Resource/Titanfall2Map.cs) | 267 | Titanfall2Map | 宿主对象所有权、JSON GUID remap、批量反序列化、编辑预览和卸载。关键结构/调用链审阅 |
| [Resource/Titanfall2MapCamera.cs](Resource/Titanfall2MapCamera.cs) | 54 | Titanfall2MapCamera | 生命周期、调用边界和资源所有权。关键结构/调用链审阅 |
| [Resource/Titanfall2SpectatorCamera.cs](Resource/Titanfall2SpectatorCamera.cs) | 44 | Titanfall2SpectatorCamera | 生命周期、调用边界和资源所有权。关键结构/调用链审阅 |
| [Resource/Tonemapping.cs](Resource/Tonemapping.cs) | 119 | Titanfall2Tonemapping; CameraState | 每相机曝光历史、compute barrier、bloom/曲线阶段；保留原有显示策略。关键结构/调用链审阅 |
| [Resource/WaterRenderer.cs](Resource/WaterRenderer.cs) | 17 | Titanfall2WaterRenderer | 透明 pass framebuffer-copy 接线与 ModelRenderer 生命周期。关键结构/调用链审阅 |
| [Resource/WorldMaterialStreamer.cs](Resource/WorldMaterialStreamer.cs) | 96 | Titanfall2WorldMaterialStreamer | 生命周期、调用边界和资源所有权。关键结构/调用链审阅 |
| [Resource/WorldReady.cs](Resource/WorldReady.cs) | 78 | Titanfall2WorldReady | 实际 native body/shape、Props 身份、失效撤销与恢复扫描。关键结构/调用链审阅 |
| [Resource/WorldShadowController.cs](Resource/WorldShadowController.cs) | 65 | Titanfall2WorldShadowController; ShadowChunk | 生命周期、调用边界和资源所有权。关键结构/调用链审阅 |
| [SP_VISUAL_PARITY.md](SP_VISUAL_PARITY.md) | 54 | — | SP 地图画面差异：Beacon 云层朝向修复。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [STEAM_COMPATIBILITY.md](STEAM_COMPATIBILITY.md) | 83 | — | Steam SDK 兼容与部署（2026-10-29）。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [STREAMING_ARCHIVE_REVIEW_2026_10_30.md](STREAMING_ARCHIVE_REVIEW_2026_10_30.md) | 41 | — | Titanfall 2 STARPAK 索引审阅与修复。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [Sandbox.Mounting.Titanfall2.csproj](Sandbox.Mounting.Titanfall2.csproj) | 34 | — | 引用/排除、net10、构建后本地 mount 复制。构建/配置/交付结构审阅 |
| [Titanfall2Log.cs](Titanfall2Log.cs) | 126 | Titanfall2Log | 限流、单次 suppression 与单调时间重试。关键结构/调用链审阅 |
| [Titanfall2StreamingSettings.cs](Titanfall2StreamingSettings.cs) | 381 | Titanfall2PropLodMode; Titanfall2StreamingSettings | 环境变量默认值、运行时开关与加载期配置的边界。关键结构/调用链审阅 |
| [VISUAL_PARITY_PLAN.md](VISUAL_PARITY_PLAN.md) | 205 | — | Titanfall 2 挂载画面差异：Traffic 实测与后续路线。文档索引/引用/历史边界核对；旧数字未全部重跑 |
| [WARGAMES_VISUAL_FIXES.md](WARGAMES_VISUAL_FIXES.md) | 70 | — | 战争游戏：可见性、透明材质与光照差异。文档索引/引用/历史边界核对；旧数字未全部重跑 |
