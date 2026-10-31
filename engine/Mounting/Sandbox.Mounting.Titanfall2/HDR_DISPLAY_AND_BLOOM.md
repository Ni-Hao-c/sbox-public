# HDR 显示、曝光与核心 Bloom

**2026-10-29 后续：** 已确认并修复战争游戏把地下天空强白光应用到主场景的问题，并独立接入 cavity 反射抑制。最新实现、源数据与未完成台账见 [ENVIRONMENT_AND_CAVITY_FIXES.md](ENVIRONMENT_AND_CAVITY_FIXES.md)。下文保留上一轮 HDR / Bloom 的验证记录和当时部署版本。

2026-10-28。接续 [SP_VISUAL_PARITY.md](SP_VISUAL_PARITY.md) 的 HDR 待办。战争游戏偏灰白已改善；本记录不代表 SP / MP 全图达到原版视觉效果。

## 已确认的原版语义

只读分析本机 Titanfall 2 的 VPK 着色器与 `bin/x64_retail` 中的原始 DLL，没有修改原版游戏文件。证据目录：

`C:/Users/Administrator/.codex/visualizations/2026/10/24/01a16c2c-16d3-7f51-b93e-2cde2bd4796a/tf2-hdr-audit/`

- `original/engine_post_ps50*.dxbc.asm`：默认显示曲线为 `x*(10*x+0.3)/(x*(10*x+0.5)+1.5)`，不是旧挂载采用的 Hable。核心 Bloom 乘 0.5，与原始 HDR 场景相加，再乘曝光、进入曲线。宽光晕与拉丝另有纹理及参数。
- `original/exposure_adaptation_ps40*.dxbc.asm`：从平均对数亮度恢复 `L`，曝光目标为 `(1.03-2/(ln(L+1.0002)+2))/L * 2^EV`。每帧按相对差值步进，最后钳制。
- `cvar-default-constructors.txt`、`adaptation-rate-callers.txt`：原版默认曝光范围 0.5～2，适应速率 0.1。对应 SetFloat 调用传递原始速率，没有在已追踪调用中乘 delta time，当前实现按每渲染帧处理。
- `original/downsample_bloom_ps50*.dxbc.asm`：核心预处理为逐通道 `pow(max(rgb-cutoff,0), exponent)`，不同组合含不同降采样方法。未使用 Source 2 Bloom 的 soft knee。
- `original/blurfilter.vmt` 指定 kernel 4；对应核有 13 次双线性采样，覆盖 25 像素，中心权重 0.122765。当前核心模糊采用这组原版系数、横纵两遍与镜像边界。客户端所有质量分支及动态 kernel 选择尚未完整映射。
- `bloom-cvar-constructors.txt`：原版核心 pre / post exponent 为 1，wide amount 为 0.2、wide pre exponent 为 2、wide post exponent 为 0.7，streak amount 为 0.5、streak cutoff 为 5。后两条分支尚未接入，不能把这些数字当作已还原效果。

`source-data.json` 检查了 Beacon、War Games、Angel City 的环境、出生点及脚本实体分区；未找到曝光控制实体。这个结果不覆盖全部 BSP 实体、剧情脚本及地图设置，不能证明原版没有动态曝光 / Bloom 控制。

## 当前实现与默认值

`Resource/Tonemapping.cs` 与 `titanfall2_display.hlsl` / `titanfall2_exposure_cs.shader` / `titanfall2_tonemapping.shader` 接入上述曲线及曝光函数。旧固定 1.5 EV 改为 0 EV，保留环境变量覆盖。关闭该相机的 native auto exposure，避免二次曝光。

测光在原始场景上执行七级加权对数降采样，奇数尺寸不丢边缘像素；每个相机独立保留 1×1 曝光历史，同一 camera build 的重复离屏读取不会再次适应。相机失效或组件析构时释放历史纹理。当前 Rec.601 全图几何均值测光经过 GPU 数值检查，但尚未证明原版 log-luminance 采样链逐字节等价。

`titanfall2_bloom_cs.shader` 在四分之一分辨率执行核心预滤波与横纵模糊。测光始终读取不含 Bloom 的原始场景，Bloom 在曝光与显示曲线之前合成。强度为 0 时跳过三次计算与临时目标分配。

**当前兼容默认值：BloomStrength=0.15、BloomCutoff=1、BloomExponent=1。** 最初单位强度、零门槛在战争游戏白色平台和飞机高光上明显过强，用户实图确认后降低。0.15 / 1 是当前挂载的保守校准值，不是声称从原版地图提取出的数值。原版曲线、曝光范围和适应公式保持不变。后续接入地图 / 脚本设置及正确 HDR 材质范围后再重新校准。

`Map.Environment.cs` 和独立 3D 天空的 cubemap tint 改为白色，避免对已有色彩再叠加蓝色。地图原始天空纹理、vista 材质、雾与 LUT 没有被替换；3D 天空的 lit-vista ambient fallback 仍有独立蓝色值，不能宣称天空环境项全部等价。

## 数值与实际画面验证

以下脚本位于 `Perf/MaterialVerification/`，通过 `sbox-cli --connect <pid> --call run_csharp` 在真实引擎中运行：

| 检查 | 覆盖 |
| --- | --- |
| `VerifyRetailDisplay.csx` | 32 项：曲线灰阶 / RGB、曝光范围与 EV、精确适应步进、重复读取、相机独立历史及释放、非有限设置 |
| `VerifyExposureReduction.csx` | 3 项：7×3 奇数视口的像素计数、加权 log 求和、几何均值 |
| `VerifyColorCorrection.csx` | 48 项：独立 LUT 回归，含真实色表、权重、克隆及体积生命周期 |
| `VerifyRetailBloom.csx` | 12 项：逐通道 cutoff / exponent、核的常量保持、HDR 合成顺序、零强度、较低强度、普通表面门槛、曝光隔离、蓝色脉冲光晕及远处黑色保持 |

最终部署的新进程 PID 3628 中，上述 **95/95 项通过**；整图重新加载确认默认强度 0.15、门槛 1、EV 0，运行程序集 MVID 为 `4cb323f7-043b-4502-91b2-ca499c2529f0`。`mp_wargames-bloom-final-default.png` 为该进程的默认效果截图。最终控制台没有 Error 级消息；另有碰撞构建 spike 及遥测 SSL 警告，未声称控制台完全无警告。

首次加载缺少刚新增的编译 shader 时，Compute 出现 bound pipeline 无 compute shader 警告；正式编译三条 shader 后使用新进程重新验证，不能把只编译成功视为 GPU 验证成功。

实际图：`sp_tday-display-legacy.png` / `sp_tday-display-retail.png`，以及 `mp_wargames-display-legacy.png` / `mp_wargames-display-retail.png`。它们比较旧显示管线与新管线，使用同机位，其他地图效果保留。War Games 曝光约 0.519，平均亮度约 0.255；暗部层次恢复，用户反馈比之前改善。

Bloom 对照：`mp_wargames-bloom-strong.png` / `mp_wargames-bloom-subtle.png` / `mp_wargames-bloom-off.png`。位置 `(900,2700,450)`，固定曝光约 0.520，1280×800，依次比较强度 / 门槛 `1/0`、`0.15/1` 与关闭。第二张显著减少飞机周围及地面的泛白。这些是挂载内部受控对照，不能代替原版同机位验收。

上一轮 `hdr-perf.json` 中 Managed: Tonemapping 平均 0.04 ms、最大 0.32 ms，显存约 4704 / 6467 MiB；该数据不包含本轮新增 Bloom。不要与用户主窗口帧率直接比较。

最终同进程、同地图机位，停止离屏读取并各等待 2.6 秒后，`bloom-perf-off.json` / `bloom-perf-on.json` 两个短采样中全帧 GPU 平均为 19.26 / 19.37 ms，显存为 3363.6 / 3365.7 MiB。差值约 0.11 ms / 2.1 MiB，仅说明这次采样没有明显新增高开销，不是精确的独立 Bloom GPU 计时或所有地图的性能保证。

部署 DLL SHA256：`0D2C574B4023595E9221F329EEB01014F20D20F629332AC371A66DDD84AEA765`。Release 挂载构建 0 警告、0 错误。未运行全仓库 CI。

## 剩余工作

1. **飞机与地面反光。** 降低 Bloom 后仍存在窄而硬的高光，应核对原版 roughness / specular 通道、环境立方体动态范围、反射体积及 BRDF；不能继续靠压曝光掩盖。
2. **原版地图控制。** 导入 BloomScale、曝光补偿 / 强制曝光、脚本与局部控制体积。不能用统一全局默认替代剧情状态。
3. **宽光晕与拉丝。** 追踪原版实际 RT 尺寸、kernel 选择与缩放后再接入。当前只有核心 Bloom。
4. **薄雾和环境。** 继续按具体来源核对 ramp、深度软交界、距离衰减与透明排序；保留 Beacon 朝向修复及已验证 LUT。
5. **验收与性能。** 增加明暗过渡和一张室内 SP 地图的固定机位 / 移动序列；独立测量 Bloom GPU 增量。跨地图显存缓存预算依然是单独待办。
