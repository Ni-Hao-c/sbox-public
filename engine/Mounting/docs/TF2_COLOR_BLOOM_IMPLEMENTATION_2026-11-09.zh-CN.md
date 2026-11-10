# TF2 局部调色与 Bloom 分支接入（2026-11-09）

## 状态

径向/刷体调色、多 LUT 混合以及 wide/streak Bloom 已完成托管与 shader 构建，并于 2026-11-09 部署到本仓库的 game/mount/titanfall2。针对性验证为 50 项托管检查通过、1 项可选本地内容审计跳过，以及 10 项原生图形检查通过。部署包含 1 个 DLL 和 3 个 shader_c，完整部署清单 37 文件哈希一致。详见[部署记录](TF2_COLOR_BLOOM_DEPLOYMENT_2026-11-09.zh-CN.md)。这些检查不代表零售地图画面一致性或整图性能验收。水和粒子实现未扩展。

## 调色

- `ColorCorrectionDefinition` 一次解析位置、minfalloff/maxfalloff、maxweight、刷体范围和 fadeDuration；渲染时不重复解析实体字符串。
- 径向权重为 `maxweight * (1 - clamp((distance-min)/(max-min), 0, 1))`；任一端点为 -1 或端点相等时不做距离衰减。非有限数值及无效空间定义不会进入 GPU。
- `color_correction_volume` 使用已有 CM 凸刷体恢复，包括模型 bounds、凸平面、实体平移与旋转。位于外部环境分区的实体也在 BSP 流仍打开时解析并写入解析缓存。已有作者 bounds 而无平面时使用该 bounds；不能恢复范围的定义记录后跳过。
- 外部实体与 BSP 副本合并时，生成的 `*trigger_*` 几何元数据不再被误当作不同的作者实体；元数据复制到合并结果，不修改输入字典。
- 刷体进入/离开支持连续线性淡化，读取 fadeDuration，或单独的 fadeInDuration/fadeOutDuration。中途反向从当前权重接续。
- 每个 CameraComponent 独立保存空间权重与淡化状态。当前使用渲染相机的位置，不使用 Source 服务端的玩家碰撞体触发事件；镜头与玩家分离时，两者的边界时机可能不同。
- 多张有效 LUT 按权重归一化后在 GPU 合成 32³ 表，总应用强度为 `clamp(sum(weights), 0, 1)`，沿用原有显示域查表和最终混合路径。每次 dispatch 处理四张表，通过两张私有 RGBA16F volume 交替累加；四张是批次宽度，不是总表数上限。
- 单张有效表直接采样原表，不创建合成纹理或派发合成计算。多表在当前录制中完成合成，不用 CPU 标记假定上一份命令已经在 GPU 执行。
- 挂载源 LUT 只持有引用，组件只释放自身每相机的合成纹理。旧场景的 GradingTexturePath/Weight 单表配置仍可读取；新导入场景序列化多表实体定义。

径向衰减参考 [Valve 客户端实现](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/c_colorcorrection.cpp)。刷体的作者参数与进出淡化用途参考 [Valve 服务端实现](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/colorcorrectionvolume.cpp)。本实现没有照搬服务端玩家触发或其中的特殊淡化公式，也不声称完整复现 TF2 的脚本行为。

## Bloom

曝光测量和显示保留在 `Tonemapping.cs`；Bloom 参数与生成移到 `Tonemapping.Bloom.cs`，原有组件身份及参数名保持可反序列化。

| 分支 | 当前生成方案 | 显示前合成 |
| --- | --- | --- |
| Core | 原有 1/4 预滤、水平/垂直核 | `pow(core * 0.5, corePostExponent)` |
| Wide | 独立前置指数预滤，再降到 1/16，水平/垂直核 | `wideAmount * pow(wide, widePostExponent)` |
| Streak | 独立高阈值预滤，1/4 分辨率下水平采样间距 1/2/4 的连续滤波 | `streakAmount * pow(streak, streakPostExponent)` |

三分支相加后乘已有 BloomStrength，再进入曝光与显示曲线。曝光测量仍读取加 Bloom 前的画面。总强度为零时不生成 Bloom；单分支 amount 为零时不生成该分支。重复使用 streak 临时目标前显式切回 UAV 状态。

本地已有零售导出记录确认 engine_post 分别采样核心、宽范围、条纹贴图，具有独立的 amount/post exponent。采用记录中的参数默认值：

- core post exponent 1；
- wide pre exponent 2、amount 0.2、post exponent 0.7；
- streak cutoff 5、pre exponent 1、amount 0.5、post exponent 1。

证据位于本机 `C:\Users\Administrator\.codex\visualizations\2026\10\24\01a16c2c-16d3-7f51-b93e-2cde2bd4796a\tf2-hdr-audit\`：`bloom-cvar-constructors.txt` 及 `original/engine_post_ps50-s0-d0.dxbc.asm`。这些是已有导出快照，不能代替当前零售客户端的实时验证。

**wide 的分辨率链、streak 的生成半径和滤波链是当前重建方案；本轮未完成零售生成阶段的逐指令还原。** 参数和最终相加形式有导出依据，不能据此宣称光晕尺寸、能量或像素已与零售一致。

## 仍保留的边界

- StartDisabled 按导入时状态处理；实体 I/O、脚本 Enable/Disable/SetWeight 及动态 tonemap controller 输入未实现。
- 刷体是静态导入范围；父实体驱动的移动刷体和服务端玩家碰撞触发未接入。
- 多张同类后处理组件之间仍沿用引擎分组方式；本功能在地图导入的单个调色组件内部混合所有作者 LUT。
- 未测量新增 GPU 耗时、临时目标占用或显存峰值，未作零售画面对照。

## 后续验证与本次覆盖

已完成托管和 shader 编译、径向/旋转刷体/凸平面及淡化纯逻辑、多 LUT GPU 全深度切片读回、正式调色命令链的双表渲染、Bloom 分支的离屏像素变化和重复曝光启停。以下列表仍作为完整验收计划，本次未覆盖的部分不据此视为通过。

1. 构建托管项目，随后通过 SboxBuild 编译 shader；本轮改变了 compute 变体及参数，旧 shader_c 不作为有效验证产物。
2. 核对径向端点、旋转凸刷体边界、外部分区刷体元数据恢复和跨分区去重。
3. 核对单表、双表、超过四表、总权重低于/高于 1、失败 LUT 和不同相机的独立淡化；GPU 读回合成颜色。
4. 分别单独打开 core/wide/streak，核对目标读写转换、边缘采样、零强度路径、不同分辨率和 HDR 非有限输入。
5. 新进程加载地图，以固定机位分别对照图像和 GPU 时间，再确定是否部署及是否调整重建的生成链。
