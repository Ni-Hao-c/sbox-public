# Titanfall 2 碰撞过滤、结果状态修复及远景黑带定位

日期：2026-11-07。范围：只修改 `engine/Mounting/Sandbox.Mounting.Titanfall2/`，目标目录只部署碰撞修复 DLL，不替换托管或原生引擎，不修改生产 shader。

## 已复现的问题与修复

1. `CollisionFilter.IsVisualEffectName` 用宽泛的 `mist` 子串匹配，将实体材质 `models/industrial/hanging_plant_misters` 当成特效。改为名称 token 匹配，保留 `mist`、`mist01`、`ground_mist_loop` 等雾效，避免命中 `misters`。
2. collision-only 导入对“已知全部被过滤”“已知没有可用几何”和“读取/解析失败”均返回 null，streamer 因而重试已完成的空结果。新增内部 `Titanfall2CollisionModelResult`，区分以下四种状态：

| 状态 | 含义 | streamer 行为 |
|---|---|---|
| Unavailable | 读取、解析或资源构建未成功，默认值也是此状态 | 保留原 30 秒重试退避，不发布 Ready |
| Loaded | 已构建可用碰撞 Model | 原有 collider 创建与原生物理验证 |
| Filtered | 导入成功，无 VPHY/hitbox 形成的碰撞，全部网格已由现有材质规则排除 | 记入 filtered，结束等待 |
| Empty | 导入成功，仍无可用碰撞，且不能证明全部网格已被过滤 | 记入明确失败、停止空结果重试，继续阻止 Ready |

终止结果按 loader 的 skin/body 缓存，仅持有状态、contents 和来源字符串，不持有 native Model。Loaded 仍使用 weak Model 缓存。真实失败不写入终止缓存，允许恢复。

嵌入 VPHY 解析抛出异常且没有成功回退几何时，仍返回 Unavailable，防止被缓存为确定 Empty/Filtered。原先完整 Model 只在确实有物理 parts 时才能作为临时加载失败的回退，避免无物理体的渲染 Model 改写失败性质。

没有改动 `_collidableProps`、Ready 判定等式或 `WorldReady` 的物理世界校验。9 个水洼/贴花实例被现有材质规则明确过滤；没有按文件名硬编码跳过实例，也没有把未知缺口计成成功。当前 Model 碰撞仍可能使用 render-mesh fallback，未新增 MDL PerTriAabb 解析，不能将此修复等同于零售碰撞完全等价。

## 测试

- 修复前，8 个名称回归用例中 2 个失败，均为 mister 实体材质误过滤；其余雾效用例通过。
- 修复后模块独立测试 26/26 通过，包含新增 8 个过滤名称用例和原 18 个运行时边界测试。
- 新增 mount-local 原生 Integration fixtures，验证实际导入链路：确定 Empty 的结果缓存、临时读取失败后恢复、损坏 MDL 不误判成功、损坏嵌入 VPHY 不缓存为 Empty。
- 四项原生结果状态测试最终 4/4 通过；与模块 26 项、现有挂载 40 项合计 70 个聚焦用例通过，不代表全仓库测试通过。
- 现有 `Titanfall2MountTests` 40/40 通过。
- 使用既有隔离 workspace，经 `SboxBuild build --config Developer` 构建；目标引擎未参与输出替换。首次构建因系统 NuGet HTTP 缓存访问被拒而失败，改用本次子进程的可写 `NUGET_HTTP_CACHE_PATH` 后构建成功，没有关闭漏洞审计或修改系统设置。
- 改动实现文件的 whitespace verify 和 `git diff --check` 通过。未将仓库其他已有格式问题纳入本次修改。

## 实际地图验证

地图为 `mp_angel_city`，私人 `facepunch.sandbox` 会话。首个候选版本经过真实游戏 tick 和场景加载后：

| 指标 | 修复前 | 修复后 |
|---|---:|---:|
| 目标 collidable 实例 | 11,725 | 11,725 |
| resident collider | 11,528 | 11,716 |
| 明确 filtered | 0 | 9 |
| 未解析 | 193 | 0 |
| 明确失败 | 4 | 0 |
| HasPendingResidentCollisions | true | false |
| 失败重试 key | 7 | 0 |
| ResidentCollisionsReady | false | true |
| ttf2_map_ready | 未发布 | 发布 |

两类喷雾器分别 116 和 72 个，共 188 个实例均存在实际原生 physics body 和 shape；总原生静态道具 body 数为 11,716，与 resident 计数一致。不是仅检查序列化 Model 引用。首次与后续快照相隔 80.325 秒，状态保持，超过 30 秒失败退避周期。

首个候选结果保存在 `.tmp/tf2-collision-fix-20261107/intermediate/`。增加嵌入 VPHY 损坏保护后，最终 DLL 再次启动地图并复测：表中修复后计数全部一致，188 个喷雾器原生 body/shape 均存在。首次和后续最终快照相隔 125.375 秒，Ready、filtered=9、失败/未解析/重试 key=0 均保持；后续快照也已在恢复原画质后验证。

## 远景黑带定位

使用未改 shader 的固定相机 `(-3729,2245,1800)`、角度 `(20,180,0)`，分别隐藏两个远景模型、逐个把 6 个材质的 `g_tAlbedo` 换为 Texture.White，结束后在 finally 中恢复贴图、rendering flags 和相机。最初一组截图部分被控制台遮挡，已排除并关闭面板重新采集；有效证据位于 `vista-clean/`。

- 隐藏 `models/vistas/angel_city_se.mdl` 后宽黑带消失；只隐藏 `angel_city_scrolling_uv.mdl`，黑带保留。
- 只替换 `models/vistas/angel_city/angelcity_mid_se` 的 albedo，原黑带区域变为白色；其余材质的替换没有消除宽黑带。
- 对应挂载 shader 为 `titanfall2_vista_cutout.shader`；原始 shader set 为 `uberUnlitCutSamp2_fix`，反射绑定的 albedo 为 `models/vistas/angel_city/angelcity_mid_se_col`。
- 实际 GPU 贴图为 BC7、4096×4096，回读 alpha 范围 0–255，16,777,216 像素中 9,149,878 个 alpha<128。透明通道没有整体丢失。
- 相关两个 mesh 所有顶点 alpha 为 255；材质 `UsesVertexAlpha=false` 与 shader 参数一致，不支持“顶点 alpha 未启用导致此横带”的解释。
- 当前 UV 变换为 identity、alpha test reference 0.5、material opacity 1。原始 metadata `DoubleSided=false`，但 vista shader 固定 `S_RENDER_BACKFACES=1`，这是后续需要受控验证的差异；尚未证明它就是黑带成因。

本轮已把范围从整个 3D skybox 缩小到一个模型的一张 cutout 材质；没有通过隐藏内容提交视觉修复，也没有随意把不透明度/亮度阈值调成黑带不可见。UV/几何、原始采样与背面剔除仍需进一步隔离，黑带尚未修复，视觉验收仍未通过。

证据：`vista-clean/captures.json`、`materials.json`、`details.json`、`descriptor.json`、`mid-se-albedo.png` 及逐阶段截图。

## 最终部署与现场恢复

- 部署目标：`D:\sbox\sbox-public\game\mount\titanfall2\titanfall2.dll`。本轮仅替换此 DLL；原 30 个生产 shader 编译产物最终逐项复核，无变化。
- 最终 SHA-256：`6A350F486C730417A382E14C17ED1CBCBF976BCDF10F3EB92D573614190C3597`。
- 实际运行 MVID：`7ad79b4a-78ee-444b-89a9-5ec84f5e3448`。
- 托管引擎 SHA-256 仍为 `04F25A4A8772669A2D8A43DC486D2BA24B5EA6F59510D955AA008A599273163A`。
- 最终实例 PID 24252，主目录 `game/sbox.exe`，私人 Angel City 会话保留运行。前两个自有实例正常提交 quit 并退出，没有停止其他用户实例。
- 玩家位置与视角已恢复至重启前，三项输入/相机/转向控制均为 true，无临时诊断相机，GPU profiler 为 false。
- 重启后引擎曾套用另一套画质配置，已检测到差异并通过 CookieContainer/RenderSettings 接口恢复原值、重新应用。最终为 render scale 1、VSync true、MSAA None、CMAA2、纹理/阴影 Low、Upscaler Off、FPS 500，video 配置原有键的 Value 零差异。没有据此次跨配置重启报告 FPS 提升。
- 最终进程日志无 Error 级输出、无 Titanfall 挂载 Warn；其他 addon 的已有 Warn 不属于本次修复范围。
- `final-scene.png` 为恢复后引擎截图，已查看确认正常出图；不能代替高处黑带视觉验收。

原部署 DLL 与本轮修改前源码均保存在 `.tmp/tf2-collision-fix-20261107/before/`。最终证据：`deployment-final.json`、`owner.json`、`live-verification.json`、`live-verification-followup.json`、`live-statistics-followup.json`、`restored-final.json`、`video-restored.json`、`video-differences.json`、`verification.json`。碰撞缺口与状态修复在 Angel City 的本次条件下验收通过；零售碰撞语义完全等价、其他地图和远景黑带修复仍未验收。
