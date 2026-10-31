# SP 地图画面差异：Beacon 云层朝向修复

**2026-10-28 HDR 后续：** 已接入原版显示曲线、独立曝光适应和核心 Bloom，默认 EV 已从本轮历史记录的 1.5 改为 0；用户反馈光晕过强后采用保守 Bloom 默认。证据、参数来源及未完成边界见 [HDR_DISPLAY_AND_BLOOM.md](HDR_DISPLAY_AND_BLOOM.md)。下文为此前云层修复记录。

日期：2026-10-28。地图：`sp_beacon.bsp`。本轮修复已复现的导入错误，SP 原版画面还原仍未完成。

## 现象与隔离

新进程、默认设置下，塔身、岩壁和远景被大片灰紫色半透明层覆盖。固定机位依次关闭 LUT、高度雾、世界 atmosphere 材质和粒子精灵：关闭粒子精灵时，大面积遮挡明显消失；关闭高度雾或世界 atmosphere 材质仍保留主要遮挡。LUT 会改变色调，但不是覆盖整座塔的主要来源。

实际 PCF 中，`P_cloud_thickCover_beacon` 半径为 6000，`P_cloud_thickCover_SM_beacon` 半径为 3000，二者均使用 `orientation_type=2`。旧代码把 2 映射到 `RotateToCamera`，令本应水平铺在谷底的云层竖起来面对相机。源数据并不要求减小半径或统一降低 alpha。

可与本机死锁挂载的 `Code/Deadlock/Particles/Renderers/RenderSprites.cs` 中 `WorldZAlignedBasis` 对照：该平面位于世界 XY。类型 1 才是保持直立的 screen-Z billboard。

## 已修改

- `Resource/ParticleStreamer.cs`：类型 1 映射至直立 billboard；类型 2 走完整粒子旋转，并创建几何控制器。每步先重新计算原始粒子角度，避免累积 90 度变换。
- `Resource/ParticleControlPoints.cs`：普通类型 2 精灵在模拟后转到世界 XY 平面，保留卡片局部 roll、yaw 和翻转。拖尾保留原有独立处理。
- 本轮没有调整 PCF 云半径、alpha、颜色、发射率，也没有改动继承的 1.5 EV、地图雾和原始 LUT。

类型 3 的已有路径仅保留兼容；没有实现完整粒子法线、控制点朝向或所有定制 PCF 渲染器。

## 实图与验证

证据目录：
`C:/Users/Administrator/.codex/visualizations/2026/10/24/01a16c2c-16d3-7f51-b93e-2cde2bd4796a/tf2-sp-parity/`

`beacon-tower-orientation-before.png` 与 `beacon-tower-orientation-after.png` 为同一新进程、同一机位、冻结粒子时间、仅切换旧/新朝向的对照；`beacon-valley-orientation-before.png`、`beacon-valley-orientation-after.png` 为第二机位。旧方向在新进程中临时重现，测试结束恢复正确方向。塔身和上方山体不再被竖直云片大面积覆盖，谷底云层仍存在。

塔机位：位置 `(-3000,-800,300)`，目标 `(1000,0,550)`；山谷机位：位置 `(-5500,4500,650)`，目标 `(0,0,350)`。视场 80，近裁剪 1，远裁剪 30000，1280×800。它们是引擎离屏截图，不能据此宣称用户主窗口的帧率或全部后处理等价。

| 检查 | 结果 |
| --- | --- |
| `VerifyParticleOrientation.csx` | 14/14：工厂路由、六个 GPU 侧视/俯视投影、连续 roll 与 yaw 翻转 |
| `VerifyParticleGeometry.csx` | 23/23 |
| `VerifyAtmosphereParticles.csx` | 2/2 |
| `VerifyBeaconClouds.csx` | 17/17，含两种真实云定义的水平法线检查 |
| `VerifyHeightFogParticles.csx` | 8/8 |
| `VerifyColorCorrection.csx` | 48/48，含真实色表、部分权重、克隆和体积生命周期 |

合计 112 项针对性检查通过。新进程运行控制台没有 Error 级消息。挂载项目 Release 构建 0 警告、0 错误；本轮目标 C# 空白格式检查通过。未运行全仓库 CI。

源码构建产物与部署 DLL SHA256 一致：
`7D6286E356DF81BE6987351CB987F509125414AEBF7B9D7B898ACB624BD1DD15`。

## 仍存在的差异与下一步

1. **HDR、曝光和偏紫色调。** 朝向修复后，岩壁和白色建筑仍偏亮，阴影和远景仍带紫色。LUT 已通过数值检查，但整个显示管线还不等价。下一步读取本图曝光控制实体与脚本状态，核对零售 HDR 曲线、天空/环境项和当前固定 1.5 EV；收集明处、暗处、过渡区域的原版同机位参考后再修改默认值。
2. **其余云雾材质。** 谷底云、薄雾和世界 atmosphere 各自仍会影响层次，需要核对源材质 ramp、雾混合、透明排序和距离衰减。全部关闭粒子不能作为正式修复。
3. **SP 状态与天空。** 检查 `start_active`、脚本控制的实体/特效状态和当前延后的 3D 天空粒子。当前导入没有运行完整零售剧情逻辑。
4. **灯光、反射与材质。** 烘焙/局部灯和 LUT 的受控检查不能代替整图视觉验收。继续核对环境反射体积、特殊发光材质与曝光/Bloom，以及屏幕、地面层混合和水体实效。
5. **地图范围。** 本次实图覆盖 Beacon，方向映射 GPU 检查是通用的；尚未据此确认所有 SP 图。后续增加一张室内 SP 图和室内外移动序列，记录显存与帧时间。

旧审计中 LUT 失败和默认关闭说明属于更早阶段；本轮确认当前默认已开启烘焙光照、地图雾和原始 LUT。判断当前状态以本文件日期、当前源码和运行证据为准。
