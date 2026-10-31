# 编译局部灯接入与当前验证边界

**2026-10-28 更新：** 新进程的 `VerifyColorCorrection.csx` 已通过 48 项检查，含真实 LUT、不同输入、部分权重、克隆后恢复和体积混合。当前默认已开启烘焙光照、地图雾和原始 LUT；下文的未开启/未生效说明保留为历史状态。Beacon 云层朝向缺陷的修复、同机位截图和后续 HDR 校准项见 [SP_VISUAL_PARITY.md](SP_VISUAL_PARITY.md)。曝光、反射及整图原版视觉验收仍未完成。

日期：2026-10-27。此文件记录本轮新代码，避免将“数据存在、shader 编译成功”写成完整画面验收。

## 2026-10-27 后续：LUT 执行修复与普通挂载接入

`titanfall2_color_correction.shader` 的 HEADER/MODES/FEATURES 改为引擎 shader 通常采用的多行声明块。旧格式虽报告编译成功，实际绘制未改变输出；相同 LUT 逻辑改为多行后，独立资源路径及全新进程中的正式挂载路径均生效。当前证据支持这个格式/加载链路差异，原生编译器内部原因未进一步定位。

`VerifyColorCorrection.csx` 扩展为 48 项实际 GPU 检查，全部通过：单位表、交换红蓝表、天使城/虎大/家园三张零售表、四种输入颜色、部分权重、组件/相机关闭、复制后销毁源组件、体积半权重和零权重。离屏 fixture 显式更新后处理系统和渲染回调；内置亮度归零作为阳性对照。

战争游戏同机位真实地图另检查 180 个像素：红蓝交换的平均 RGB 距离误差为 0.000358，画面平均变化为 0.071208，检查通过。该交换表仅证明执行，不是地图作者的调色。报告和两张截图保存在 2026-10-27 会话的 `tf2-color-correction/` 证据目录。

普通挂载之前不会创建原版 LUT：它被嵌套在实验 BSP 光照及固定 Hable 分支中。代码已将原版全局调色解耦，保留地图自己的文件名和 maxweight，默认允许创建；固定曝光仍只用于实验烘焙路径。缺失或非法 LUT 记录警告。

用户提供的信标关卡对照仍有明显差异。原始 sp_beacon 实体确认包含 `materials/correction/sp_beacon.raw`、多个雾控制器及定向暖色雾参数。MapFog/AtmosphericCardEffects 原先写死 false，现改为可配置，默认仍关闭；旧 GradientFog 映射没有实现完整高度/方向散射，不能直接视作还原方案。开关分别为 `SBOX_TITANFALL2_MAP_FOG` 和 `SBOX_TITANFALL2_ATMOSPHERIC_CARD_EFFECTS`，设为 1 可用于独立验证；不能将打开开关本身当作正确渲染的证据。

最终新进程的普通模式 sp_beacon 验证：自动创建全局无限体积，LUT 为 `materials/correction/sp_beacon.raw`、weight=1，Play 复制恢复后保留，无 Tonemapping 组件，13,904/13,904 静态物件加载成功、0 失败。塔外固定机位保存 LUT 开/关截图；它们证明颜色变化，不是与用户截图同机位的原版匹配。地图还明确省略 60 组大气卡片效果、暂未支持 3 组天空世界粒子。原版 foghalfdisttop/bottom、fogztop/bottom、fogdircolor/strength 等数据仍需正确消费。

本阶段已通过仓库 SboxBuild Developer 构建，托管解决方案 0 警告/0 错误；五个启动器单文件发布仍有 MonoMod 兼容性提示。最终部署 DLL SHA256：`D30360BE1AAA8E356383AF6E2BA4BD2E1049CC4F35796A778DD4A57DFC7DA2A6`，构建产物与部署一致，最终部署再次通过 48 项 GPU 检查。

已运行标准非 LiveBackend 测试：三个 Integration 分组、所有 allocations 分组及 Text 通过；Engine 的 3 个 WebSurface.IsUriAllowed 用例失败，Unit 在 HostSyncAttributeAnalyzer 之后 Stack overflow，不能写成全仓库测试通过。标准 format --verify 报告大量既有文件行尾问题；本轮修改的 Map.Environment.cs 和 Titanfall2StreamingSettings.cs 未出现格式诊断，git diff --check 通过。完整构建、测试及格式日志均复制到本轮证据目录。

下方“交换 LUT 未生效”的描述属于本次修复之前；原版曝光、空间雾、天空与跨地图视觉验收仍未完成。

## 已实现

- WORLD_LIGHTS v1/v2/v3 的位置、HDR 颜色、聚光锥、范围、编译多项式衰减、反平方标志及高光参数读取。
- 地形通过 RTL 四个通道的整数灯 ID、页表与双线性遮挡权重读取局部灯。页表索引先映射紧凑实时列表，再映射完整源表。
- 静态道具探针使用完整源表索引；与地形页表不是同一索引空间。保留四个 byte 权重及四个 ushort ID，使用 80 字节 GPU 记录中原先保留的 12 字节，不增加实例表步长。
- 道具序列化版本升至 5，保留 v3/v4 读取。权重与 ID 按整数写入，避免 `0xffffffff` 等位模式被浮点 NaN 序列化改变。
- 灯表按场景视图、普通物件和实例绘制属性绑定。材质属性不能代替场景/绘制属性；实际 GPU 检查曾因此得到全黑，已修正。
- 克隆后的场景控制器重建灯表和页表；道具 streamer 重建自身绘制灯表，删除场景时释放缓冲。

没有给每盏灯创建原生 PointLight/SpotLight。每个受光点最多处理原始关联的四盏灯，保留稳定的烘焙遮挡，不随玩家距离开关阴影贴图。

## 已取得的证据

`VerifyCompiledLocalLights.csx` 最终 30 项在新进程通过：点光颜色、遮挡权重、聚光内外侧、反平方单位、范围/非法索引、普通与实例路径一致、地形页表，以及四个全 byte 权重位模式检查。

`VerifyProbeTransport.csx` 的 11 项通过：80 字节布局、SH/天空、局部灯位模式、v3/v4 兼容、截断拒绝和裁剪源索引。灯光数据 15 项、实例/LOD 13 项、高光工作流 18 项、太阳通道 8 项、实例 ID 9 项也已回归通过。

挂载项目 Release 构建 0 警告、0 错误；8 个 shader 的 VS/PS 正式编译通过。战争游戏后续修复及缓存场景生命周期审计已重新构建部署，DLL SHA256：`24819966B1202699CD7C0129AAA630A34BDE24A458751EC773AD01DDA9D5FA0C`。生成光照纹理也随缓存场景保留，两轮回收/重载检查通过。仅进行了目标项目构建和检查，未运行全仓库 CI，详见 CODE_AUDIT.md。

天使城运行场景中：19,455/19,455 主世界道具、0 失败；世界及道具均恢复 1,282 项灯表，世界页表 1,197 项；12 张 cubemap 均有效。固定机位图及 JSON 位于会话证据目录 `tf2-local-lights/`。这些计数验证接入和恢复，不证明原版逐像素一致。

## LUT 与曝光尚未验收

已从三张原版地图读取 98,304 字节的 32³ RGB LUT：天使城 `mp_angel_city_dlc.raw`、虎大 `sp_beacon.raw`、家园 `mp_homestead.raw`。新增体积纹理恢复及调色 shader。

实际 GPU 测试的交换通道 LUT 没有改变结果，因此 LUT 不能算已完成。当前离屏渲染的 `SceneCamera.OnRenderStageHook` 探针未收到任何阶段回调，虽然后处理命令层存在。应进一步核实当前原生二进制、主视图及截图路径的回调执行；不可仅调整 LUT 颜色掩盖执行问题。

战争游戏主视图的 `perf_overview` 已报告 Managed Tonemapping pass。因此“离屏路径未执行”不能扩大成“整个原生后处理都坏了”，需要继续区分主视图和截图路径。

`SBOX_TITANFALL2_BAKED_COLOR_CORRECTION` 默认 **0**。完整烘焙光照仍由 `SBOX_TITANFALL2_BSP_LIGHTMAPS=1` 实验开关控制，默认未开启。固定曝光 Hable 是既有基线；其在本次离屏路径上的实际执行也需重新验证，不能声称已复刻原版自动曝光。

## 后续复杂工作

1. 验证后处理的实际执行路径，再让单位 LUT 保持颜色、交换通道 LUT 明显换色、三张真实 LUT 匹配 CPU 三线性结果；随后验证场景复制、体积及零权重回退。
2. 校准原版 HDR 显示曲线与曝光，保存同位置室内、门口、室外的移动序列，量化亮度和适应时间。不要将统一提亮代替空间明暗。
3. 当前局部灯使用引擎表面 BRDF；球形光源面积高光、投影灯 cookie、额外实时遮挡及特殊散射尚未复刻。源参数保留不等于全部使用。
4. 反射已有 cubemap 重建与坏 mip 修复；还要核对原版 envmap 体积、投影边界和室内外混合，水面反射另做验收。
5. 三地图完整回归、显存/帧时间检查及带烘焙数据的持久地图缓存完成后，再决定默认开启。

战争游戏实图已验证 8,931/8,931 道具、0 失败，1,347 项灯表、756 项地形页表和三张有效 cubemap。飞机缺失是静态道具可见性关联问题；网格需要透明绘制路径。修改与剩余明暗、屏幕、网格校准工作见 [WARGAMES_VISUAL_FIXES.md](WARGAMES_VISUAL_FIXES.md)。此前未通过的 LUT 检查保留，不能在交付中写成全部完成。
