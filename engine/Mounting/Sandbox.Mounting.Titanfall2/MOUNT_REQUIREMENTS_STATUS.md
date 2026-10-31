# ttf2 游戏接入需求对照（2026-10-29）

依据游戏侧 `D:\sbox\ttf2\Docs\mount-requirements.md` 和 `plan.md`。
本文件区分已实现、已编译 / 针对性检查和实图验收，不能用加载资源数量代替原作效果验收。

**2026-10-29 ready 后续：** 已修复更新接口遗漏、损坏 prop 数据误报成功、克隆继承旧标记及世界碰撞丢失后不撤销的问题；补入实际 GameTick 和原生物理 / trace 回归。完整复现、构建与验收边界见 [READY_LIFECYCLE_FIXES.md](READY_LIFECYCLE_FIXES.md)。

## 分工与当前入口

挂载只提供原作资产与世界。出生点、触发器、AI / 导航、武器数值、剧情与 `.nut` 重写属于 ttf2 游戏。
游戏不需要引用挂载程序集，使用资源路径即可。

| 路径 | 当前含义 |
| --- | --- |
| `mount://titanfall2/maps/<map>.bsp.scene` | 保留旧预览 / 独立运行场景，包含旧出生点和备用相机逻辑 |
| `mount://titanfall2/maps/<map>.bsp.world.scene` | 新增纯世界场景：世界、静态 prop、碰撞、天空 / 光雾、环境音与粒子 |
| `Titanfall2Map.PureWorld = true` | 已引用该编辑器组件的场景可选择新路径；纯世界模式忽略 `SpawnSpectatorCamera` |
| `mount://titanfall2/models/<original>.mdl.vmdl` | 现有 MDL 资源名追加 `.vmdl`；RMDL 同样保留原扩展后追加 |
| `mount://titanfall2/sound/<registered-name>.vsnd` | 已索引 Miles 源音频，例如 `sound/12_gauge_shotgun_shell_drop_01.vsnd`；不等同于完整声音事件契约 |
| `mount://titanfall2/<registered-texture-name>.vtex` | 已注册 RPak / VTF 纹理；应以挂载索引名称为准 |

现有 `ResourceType.Sound` 输出是 `.vsnd`，不要把需求举例中的 `sounds/<event>.sound` 当成已实现路径。
独立 brush 已注册为 `mount://titanfall2/maps/<map>.bsp/brush/<index>.vmdl`（index 从 1 开始），保留模型局部几何和独立碰撞。按名粒子 prefab、独立 AG2 clip / skeleton、`version.txt` 仍没有正式可用的路径契约。当前全文件审阅与本轮改动见 [FULL_REVIEW_2026_10_30.md](FULL_REVIEW_2026_10_30.md)，下表历史验收数字属于各自阶段。

## P0 对照

| 需求 | 当前状态 / 下一项验收 |
| --- | --- |
| 1.1 Steam 编译 | 通过。独立 Steam SDK 构建 0 warning / 0 error，ColorRamp 改为挂载自有渲染路径。 |
| 1.2 完整部署 | 原测试实例退出后，最新纯世界、CM 与 ready 修复已重新部署到 Steam；44 文件备份与哈希核对完成。新进程整图验收仍待做，见 READY_LIFECYCLE_FIXES.md。 |
| 1.3 一命令部署 | `Deploy-Steam.ps1 -EngineRoot ...` 的完整流程已执行通过。默认由目标原生 shader 编译器检查增量依赖，28 条中 24 条缓存有效、4 条重编译成功；明确需要全编译时可加 `-ForceRecompileShaders`。 |
| 2.1 纯世界 | 新路径与属性已实现；不调用创建 spawn、MapCamera、dynamic prop、DeferredNavMesh 的分支。路径、模式和序列化检查通过；实际地图的对象清单验收待做。独立实体 brush 的分离仍属于 3.x 未完成工作。 |
| 2.2 坐标 | 当前代码为 XYZ 直传、1 单位 = 1 inch、+X 前 / +Y 左 / +Z 上，无轴交换；`angles` 为 degrees，依次 `new Angles(pitch, yaw, roll).ToRotation()`，没有额外 yaw 偏移。游戏侧实际出生点落地 / 朝向对照待验收。 |
| 2.3 ready | 新纯世界 `worldspawn` 根上的 `Titanfall2WorldReady` 发布 `ttf2_map_ready`。显式订阅更新循环；resident preload 或数据解码失败时不发布；加载 / 启用先清除旧 tag；原生世界 body / shape 失效及依赖禁用时撤销。受控原生正负例与 trace 已通过，实际整图首次 tag 时的 trace 待验收。见 READY_LIFECYCLE_FIXES.md。 |
| 2.4 权威碰撞默认 | **未完成**。`SBOX_TITANFALL2_ORIGINAL_WORLD_COLLISION` 默认仍 false。本轮纠正了旧 contents 算术结论，修复 CM 21 位索引、世界 / inline model 范围、损坏原语拒绝及重复引用问题，见 CM_COLLISION_CORRECTIONS.md。继续验证真实玩家 / titan / bullet / grapple 及 clip / 水体，再改默认。当前 ready 表示已选碰撞源就绪，不表示与原版权威碰撞完全一致。 |
| 2.5 多人 prop 碰撞 | 新纯世界 streamer 设置 `KeepAllCollisions`，加载阶段完成全图 prop collision，运行时不按相机回收。覆盖渲染碰撞全局关闭开关，保证纯世界契约；旧预览路径保留原策略。策略克隆检查通过，两端玩家与加载 / 内存成本待实测。 |
| 2.6 Surface / tag | **已实现，已针对性测试，未实图验收**。世界 CM 按 contents 分组、static prop（含 dedicated server 碰撞模型）、`prop_dynamic` 碰撞、inline brush（`Titanfall2Mount.CreateBrushCollision`，每种 contents 一个子碰撞体；零售 1,053 个 inline model 中 191 个混合多种 contents）都会打 `ttf2_contents` + `ttf2_content_<bit>`，查询用 `Titanfall2CollisionQueries.Run(trace, Titanfall2TraceMask.*)`。另加可读 tag：`playerclip` / `titanclip`（只在不挡子弹时打，solid prop 展开后的 0x00EB0280 不算 clip）、`nograpple`（CONTENTS_NOGRAPPLE）、`nowallrun`（由 CONTENTS_NOCLIMB 推断；零售没有 nowallrun 位，这个映射没有对照原版墙跑代码验证）。hitbox 的 surface 用骨骼 surfaceprop。世界三角面的 surfaceprop 仍未映射到 s&box `Surface`。 |
| 2.7 分区 / SP | 现有地图分区导入继续使用，新路径为每个已发现 BSP 增加独立 loader。本轮 23 MP + 13 SP 的离线 BSP / CM 矩阵全部通过；仍缺逐图场景加载、完整资产及实际游玩验收。 |
| 2.8 headless | **已实现，已编译，未在真实 dedicated server 起图**。`Application.IsDedicatedServer` 时强制原版世界碰撞，跳过环境、粒子、音频与渲染器；static prop 只建碰撞专用模型（不建材质 / GPU mesh）并保留 contents 分类；`prop_dynamic` 只挂碰撞，`Titanfall2ModelVariant` 在服务端加载碰撞变体；声音组件 `DontExecuteOnServer` 且 headless 不播放。 |

纯世界目前只将 `ttf2_map_ready` 加到挂载产生的 `worldspawn` 根上。若游戏使用外层包装 GameObject，应在其挂载子树中查找该 tag，而不是假设 tag 会自动写到游戏自己的包装对象。
单个模型无法建立碰撞时，preload 记录失败且不发布 ready；游戏需要超时提示并读取挂载日志，不能无期限等待而没有错误反馈。

## P1 对照与推进顺序

| 需求 | 当前缺口 |
| --- | --- |
| 3.1 / 3.2 brush | 独立 inline brush 资源、局部渲染/碰撞与平移旋转已接入；已有原生回归。世界路径排除 inline model N>0。门、电梯及剧情驱动行为仍由游戏侧实现，不能将独立资源等同于完整 SP 实体逻辑。 |
| 4.1 attachments | **已实现，已原生回归**。MDL attachment 以原名导入（`model.GetAttachment("muzzle_flash")`），绑定原骨骼。零售 6,218 个 MDL / 10,494 个 attachment 离线审计：骨骼索引全部有效；33 个武器模型有重名（如 `muzzle_flash`），按原版 Studio_FindAttachment 取第一个；462 个（如 Ronin Prime `SCR_*`）矩阵全零，保留名字并使用骨骼原点 / 朝向。 |
| 4.2 hitbox | **已实现，已原生回归，未实战验收**。每个 hitbox set 用 `AddHitboxSet` 导入（零售 104 个模型有 `default` + `entryboxes` 两组；`Model.HitboxSet` 只返回第一组），保留骨骼绑定、surface（骨骼 surfaceprop）和 tag：`titanfall2_hitgroup_<n>`、按 client.dll HITGROUP_* 常量命名的 `generic/head/chest/stomach/leftarm/rightarm/leftleg/rightleg/gear`、`titanfall2_critical_override`（零售 472 个 hitbox 设置了 critical override）。原值与 keyvalue 在 `Titanfall2ModelData.Hitboxes`。 |
| 4.3 bodygroup / skin | **已实现，已原生回归**。有多选项体部的 body 0 模型导入为原生 bodygroup（名字保持原样，空名为 `bodypart_N`），`ModelRenderer.SetBodyGroup("name", n)` 直接切换；skin family 导入为 `Skin 0..N` material group（`ModelRenderer.MaterialGroup = "Skin 1"`）。零售最多 41 个选项（引擎上限 64），12 个 skin family，未发现同一材质槽在不同 skin 下分叉。地图 `skin`/`body` 与打包 body 值仍走 `Titanfall2ModelVariant` / `LoadModelVariant`（碰撞也随选中体部）。VTX 的 LOD 材质替换在 `ResolveMaterial` 中处理并有测试，但零售 MDL 实际没有任何替换条目。 |
| 4.4 include | 骨骼 / 动画已有部分导入，pilot、titan、grunt / spectre 需要逐套验证；不算完整通过。 |
| 5.1–5.3 sequence | 核对 fps、loop、root motion、事件、activity，以及跨模型骨骼一致性、非等比缩放失败清单。 |
| 5.4 AG2 | 依赖 Steam 挂载 API 支持产出 Skeleton / Clip，不能用本地源码引擎 API 假装解决 Steam 缺口；尚未向外部发送需求。 |
| 6.1 sound event | **已实现，已编译，未实听验收**。原版 Miles event 通过原生桥 `titanfall2_miles.dll` 按名字播放（`Titanfall2SoundEventPlayer` 组件，Library `titanfall2_sound_event`；或 `Titanfall2Mount.CreateSoundEventSession`），随机变体、分层、循环、音量 / 衰减与 controller 由原版 event 程序决定，支持 `SetController`。这不是 `Sound.Play` 路径，也没有 `sounds/<event>.sound` 资源；`.vsnd` 仍只是单个源音频。 |
| 6.2 particle prefab | 地图 FX 已有导入，按名单独生成 PrefabFile 和公开控制点约定待做。 |
| 6.3 UI | 已有纹理导入底座，r2_ui / HUD 名称覆盖需盘点。 |
| 7.x | 本文记录当前路径，正式版本资源、完整 collider tag 清单、changelog 和破坏性接口升级机制待做。 |

建议后续按以下顺序继续：

1. 最新代码已在原实例退出后重新部署。下一次新进程完成纯世界、ready、全图 prop 物理体和 ramp GPU 验收，核对运行 MVID。
2. 以本轮已修复的世界 / inline 范围和 21 位索引为基础，完成实图 trace、MDL 展开 / remove 与 player / titan / 子弹 / grapple 查询隔离，再决定权威碰撞默认。
3. Surface / tag、attachment / HitboxSet，支撑墙跑和命中。
4. 独立 brush 与动画 / event / prefab 契约，支撑 SP 的门、电梯、剧情与武器效果。
5. 持续做 SP 同机位 HDR / 雾 / 发光 / 反射及地图重载检查。游戏剧情状态仍由游戏实现，不在 mount 中重建整套脚本。

## 本轮检查与限制

- Steam SDK、源码 SDK Release 构建均 0 warning / 0 error。
- `Titanfall2MountTests` 16 项 + `Titanfall2CollisionReaderTests` 20 项，共 36 / 36。另有原版 36 地图离线矩阵，见 CM_COLLISION_CORRECTIONS.md。
- 新 ramp shader 在源码 SDK 的正式编译器通过 VS / PS 编译。尚缺最终 Steam GPU 取样。
- 不把这些检查称为真实多人、全图碰撞完整性、整套 SP 或原作视觉一致性验收。
- 用户指定保留 `D:\sbox\_scratch\tf2_steam_test` 实例，后续没有关闭或改写它的运行状态。
