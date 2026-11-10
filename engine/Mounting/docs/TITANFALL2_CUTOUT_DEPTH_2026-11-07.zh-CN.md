# Cutout 深度通道优化验证（2026-11-07）

> 收尾状态：本报告保留历史测量和结论。同进程 A/B 切换入口、额外参照几何和专用跑分脚本已移除；当前配置不再提供 `WORLD_MATERIAL_TABLE_REFERENCE`。`MeasureGpuPasses.cs` 现只采集当前相机和配置，不自动移动相机、改变画质或关闭通道。正常兼容回退与正确性测试保留。

## 范围与方法

继续验证 GPU pass 分析提出的 cutout 预通道优化，只改挂载源码和资源。保留正常分辨率、透明裁剪阈值、法线、预通道和道具阴影。当前 Angel City 进程 PID 26628 始终运行，没有重启或替换挂载 DLL。

每个候选在同一进程、同一 shader 的两个动态组合之间切换：late=1 保留原有采样后裁剪的路径，late=0 使用候选。每个视角执行 late-a → early-a → early-b → late-b，预热后每段保存 240 个连续帧、至少 20 个不同的已完成 GPU frame。两个候选合计 3,840 个连续帧样本。街道相机为 (-3729,2245,470)，pitch=5；高处为同 XY、z=1800，pitch=20；yaw 均为 0。使用复制的场景相机保留原有后处理，不移动玩家。用户明确前后台设置一致，焦点保留为元数据，不作为拒收条件。

临时在每个固定视图的原有两组间接绘制外添加 GPU marker，区分普通不透明组与 cutout 组；不更改几何、实例、材质行、draw 范围或可见性政策。分析器沿实际 Parent 链区分 DepthPrepass / Opaque / Shadow，父子时间重叠，不相加。

## 两个候选

1. 显式梯度版本：在 clip 前准备 UV 和位置导数，在 clip 后用 SampleGrad 读取法线、光泽和分层纹理；透明度采样和覆盖率公式保持原状。
2. 完整 quad 裁剪版本：先计算覆盖率，用 QuadReadAcrossX/Y/Diagonal 判断整个 quad 是否透明；只提前丢弃完全透明的 quad，其余 quad 保留原来的隐式 LOD 采样和切线导数，再逐像素裁剪。该版本无需修改共享 surface / normals 函数，但增加 quad 指令与 shader capability。

两个候选均通过 SboxBuild shader 构建。编译日志仍包含已有的 probe 常量布局诊断和废弃 API 警告，quad 版本还记录了 spvGroupNonUniformQuad 的 profile 自动升级，不能把工具退出成功描述为日志没有诊断。

## 像素验证

隔离原生夹具使用编辑前冻结的 cutout shader 与独立前缀的 include 文件作参考。32×32、6 个 mip 的纹理在各 mip 中采用不同数值，倾斜面与高频不透明度纹理覆盖普通 / GPU 材质表、单层 / 分层、普通 / 扭曲 UV，以及无 MSAA / 有 MSAA 共 8 组。分别读回颜色和实际深度法线缓冲，要求有限输出、参考有可见像素、平均绝对误差 <0.002、最大误差 <0.04；另有透明度改零的正向控制，证明读回受真实裁剪影响。

最初运行了旧的夹具二进制，材质表控制测试有 4 项失败，未计作通过。修正控制采样顺序并重新编译夹具后，显式梯度和 quad 版本分别 8/8 通过。它们只验证这些夹具，不证明全地图、所有硬件或所有材质的画面一致性。

## 同进程计时

下表将每个视角相同模式的两个段的中位数取平均。GPU 总时间来自历史中的最新 GPU 回读，具有延迟，不能与同编号 CPU 帧精确配对；不换算为实际游戏 FPS。

| 候选 | 视角 | 旧总 GPU（ms） | 新总 GPU（ms） | 旧 cutout 深度（ms） | 新 cutout 深度（ms） |
|---|---|---:|---:|---:|---:|
| 显式梯度 | 街道 | 12.252 | 12.273 | 0.3034 | 0.3016 |
| 显式梯度 | 高处 | 13.950 | 13.881 | 0.3151 | 0.3103 |
| 完整 quad | 街道 | 12.430 | 12.320 | 0.3041 | 0.3057 |
| 完整 quad | 高处 | 13.899 | 13.873 | 0.3159 | 0.3177 |

显式梯度只让目标 pass 减少约 0.0018 / 0.0049 ms，街道总 GPU 反而增加约 0.021 ms，高处减少约 0.069 ms；没有一致的总收益。quad 版本目标 pass 分别增加约 0.0015 / 0.0018 ms。虽然总时间略降，但街道两段旧路径本身从约 12.30 变到 12.56 ms，存在明显的时间漂移，且目标 pass 没有加速，不能把总时间下降归因于 quad 裁剪。

结论：两个候选均不作为性能优化交付。已撤回全部候选 shader 源码、共享函数改动、新组合以及实验夹具，实际 compiled shader 也恢复至原备份。

分组计时提供了更明确的下一步依据：显式梯度实验的旧路径街道深度 MDI 中，普通不透明组约 0.917 ms，cutout 组约 0.303 ms；cutout 组整个深度绘制只约占总 GPU 的 2.5%，纹理采样只是其中一部分。街道前向 MDI 的 cutout 和普通不透明组都在约 1.0–1.1 ms，后续更应查前向材质工作和普通不透明深度组。不能再以“道具预通道共约 1 ms”为由，把全部成本归到透明裁剪上。

## 证据与复现

证据根目录：`.tmp/tf2-cutout-depth-20261107/`。

- `before/`：编辑前 cutout source / compiled shader、共享 surface / normals 的精确备份。
- `gradient-candidate/`、`quad-candidate/`：两个未验收候选的源码与 compiled shader。
- `runtime/passes.json`、`runtime/summary.json`：显式梯度版本完整 8 段对照。
- `quad-runtime/passes.json`、`quad-runtime/summary.json`：quad 版本完整 8 段对照。
- `pixels/`、`quad-pixels/`：每组颜色与深度法线像素读回。
- `pixel-tests-diagnostic.log`、`quad-pixel-tests.log`：各 8 项有效原生验证；初始失败另存于 `pixel-tests.log`。
- `shader-build.log`、`quad-shader-build.log`：两个候选的 shader 编译日志。
- `validation/`：夹具、目标文件、候选交替脚本及冻结参考资源；实验代码留在证据目录，不加入正常挂载构建。
- `final-loaded.json`、`final-live-state.json`、`final-artifacts.json`：恢复后的实际加载和磁盘身份。

保留到挂载 Build 目录的改进只有通用 GPU 分组计时：`MeasureGpuPasses.cs` 在测量时临时添加分组 marker，finally 中恢复原命令列表；`analyze-gpu-passes.py` 输出各类 pass 的 cutout / opaque 子组，保持原 JSON 输出字段。既有世界材质表默认开启的改动保持不变。

## 恢复验证

最终在原 shader 上执行通用分组工具的单段烟测，成功采集并解析 GPU 分组数据，随后确认临时 marker 和命令列表已恢复。default / cutout PS 各 8 个组合、compiled resource 有效；挂载 DLL MVID `4ab355fd-8b91-4561-9d23-92f506863640`、引擎 MVID `f8177a3c-2c38-46a5-9042-923d08a71894` 与开始时一致。挂载 DLL SHA256 仍为 `BAD5D18B019B8FA2FA61FC72549510B3EA75C9D998FA12E29454E07C4A002368`；cutout compiled shader 恢复为 `3582D78334FBFD11078E6F1CD0A1EF279C7372A05DA266E1D37FFE49E765CC7B`。

现场 RenderScale=1、VSync=false、MaxFrameRate=MaxFrameRateInactive=500、CastShadows=WantsPrePass=true、世界材质表=true，地图准备与常驻碰撞就绪。实例仍为 19,303、draw records 4,722、2 个材质组、8 槽高水位 3、溢出 0。玩家三项控制恢复、临时相机 0、临时 rendering collector 已移除、profiler 和开发者面板关闭。
