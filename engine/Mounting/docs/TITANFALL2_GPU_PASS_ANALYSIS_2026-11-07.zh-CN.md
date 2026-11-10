# 世界材质表默认开启与 GPU pass 对照

> 收尾状态：本报告保留历史测量和结论。同进程 A/B 切换入口、额外参照几何和专用跑分脚本已移除；当前配置不再提供 `WORLD_MATERIAL_TABLE_REFERENCE`。`MeasureGpuPasses.cs` 现只采集当前相机和配置，不自动移动相机、改变画质或关闭通道。正常兼容回退与正确性测试保留。

日期：2026-11-07。用户要求默认开启世界材质表，并继续开展 GPU pass 耗时工作；随后明确当前前台、后台使用相同渲染设置，可以进行后台对照。本轮未修改引擎源码。

## 当前默认配置

`Titanfall2StreamingSettings.WorldMaterialTable` 的 fallback 已由 false 改为 true，显式设置 `SBOX_TITANFALL2_WORLD_MATERIAL_TABLE=0` 仍可回退。`WorldMaterialTableReference` 保持默认 false，正常运行不额外保留完整参照几何。

上一份世界材质表报告保留当时的实验结论，并补记此次用户要求的配置决定。此次默认值变更没有改变原有材质兼容性、动画、cutout、空间片段或阴影规则。

## 实例及产物身份

继续使用 PID 26628 的 Angel City，未停止或重启进程，也未替换运行中的挂载 DLL：

- 运行时 MVID：`4ab355fd-8b91-4561-9d23-92f506863640`。
- 实际挂载 DLL SHA-256：`BAD5D18B019B8FA2FA61FC72549510B3EA75C9D998FA12E29454E07C4A002368`。
- 该实例启动环境已显式开启世界材质表，314 个源材质有效；静态道具准备、常驻碰撞就绪。
- 主工作区引擎 DLL SHA-256 仍为 `04F25A4A8772669A2D8A43DC486D2BA24B5EA6F59510D955AA008A599273163A`。
- 最终源码在隔离工作区构建出的默认开启 DLL 为 `.tmp/tf2-refactor-20261106/workspace/game/mount/titanfall2/titanfall2.dll`，SHA-256 `FC154058B3B36DE5B00AED1D190CD5E746291465E8E50430CB33212EEE3E9064`。它没有覆盖正在运行的 DLL；新默认值属于此次源码和该构建产物，当前实例的开启状态来自原启动配置。

## 两视角通道定位

固定相机克隆自游戏相机，保留后处理；临时关闭玩家相机跟随、输入和转向。输出 2560×1600，内部 RenderScale=1，VSync=false，MaxFrameRate=500；没有更改贴图、LOD、阴影质量或渲染比例。

| 视角 | 相机位置 | pitch / yaw |
|---|---|---|
| 街道 | -3729,2245,470 | 5 / 0 |
| 高处 | -3729,2245,1800 | 20 / 0 |

每个视角执行 baseline-a → 仅道具阴影关闭 → 仅道具深度预通道关闭 → baseline-b。每次切换后预热 3 秒，再等待至少 260 个历史帧推进，读取最后 240 个连续帧，并每 100 ms 观察已完成 GPU 帧的逐行通道时间。8 段共 1,920 个连续帧、250 份不重复的 GPU 快照，全部焦点观察为 true。第一轮失去焦点的 6 段保留为 `passes-unfocused.json`，没有参与此表。

以下是两个 baseline 段各自均值的等权平均，单位 ms：

| 通道 / 指标 | 街道 | 高处 |
|---|---:|---:|
| 总 GPU 最新返回时间 | 12.593 | 14.089 |
| Static Opaque Forward | 3.360 | 4.619 |
| 其中 TF2 道具 Opaque MDI | 2.250 | 3.144 |
| Translucent Forward | 1.689 | 1.569 |
| Depth Normal Prepass (Large) | 1.710 | 1.444 |
| 其中 TF2 道具 DepthPrepass MDI | 1.461 | 1.338 |
| DrawDepthOpaque | 1.378 | 0.921 |
| 其中 TF2 道具 Shadow MDI | 1.007 | 0.634 |

父 scope 已包含子 scope，不能把表中所有行相加。MDI 子项根据 GPU 计时记录的父链识别，合并同类实际视图的时间；没有用 slot 编号猜测相机身份。总 GPU 值是 CPU 帧历史中最新返回的 GPU 时间，存在回读延迟，不能与同编号 CPU 帧作精确配对。Render scope 也包含提交或等待，不能全计为纯 CPU 计算。

当前 19,303 个道具实例、4,722 个 draw records、8 个可写视图槽；视图槽高水位为 3，保守溢出 recordings 为 0。此次证据不支持先增加视图槽或扩大缓冲预算。

## 临时关闭通道说明了什么

| 诊断状态 | 街道总 GPU（ms） | 高处总 GPU（ms） |
|---|---:|---:|
| baseline 两段均值 | 12.593 | 14.089 |
| 仅 GPU 道具阴影关闭 | 11.629 | 13.572 |
| 仅 GPU 道具深度预通道关闭 | 11.544 | 13.442 |

关闭道具阴影可减少约 0.96 / 0.52 ms，但会直接丢失这些阴影，因此仅用来识别成本，不作为最终配置。关闭道具预通道可减少约 1.05 / 0.65 ms，同时 Static Opaque Forward 从约 3.36 / 4.62 ms 增为 3.75 / 5.05 ms：移除预通道有部分成本转移到前向通道，而且会改变深度法线缓冲内容。此次没有把它当成保持画质的优化。

优先级因此是：前向材质/光照工作，以及保持覆盖率和法线输出的道具深度预通道精简。阴影同样值得查，但当前固定视角的可见性计算远小于实际绘制成本。数据来自两个视角，不能推广为所有地图或场景的固定占比。

## 单层 shader 候选与回退

现场 GPU 材质表有 495 个源材质：494 个非分层，1 个分层。尝试按不可变材质能力选择 `D_T2_SINGLE_LAYER`，在编译时删除非分层材质不需要的第二层逻辑；动画、分层和非有限参数继续使用通用路径。

候选构建、shader 编译和 10 个原生验证通过：8 组颜色/深度法线对照、GPU marker 验证，以及 draw 范围完整性和动画/异常输入排除验证。不过，测试通过只证明这些夹具下的输出和约定，不证明实景更快。

为保持同进程对照，运行 DLL 始终不变。先备份两个 compiled shader，通过引擎 `mat_reloadshaders` 加载经过测试的候选；实际 PS 组合数由 8 变为 16，均确认从 compiled resource 加载。诊断脚本在测量前一次性读出不可变 draw / vertex 元数据，按与候选相同的分类重排 draw 表和保守输出，重新记录固定视图命令；几何、实例、LOD、材质行和可见性政策保留。2 组变为 3 组，其中 4,718 / 4,722 条 draw records 使用精简组合。

每个视角执行 general-a → specialized-a → specialized-b → general-b，仍测正常分辨率，各段读取 240 个连续帧，共 1,920 帧。此次焦点记录为 false；按用户明确的前后台同设置约束接受，保留焦点元数据。每段记录的 VSync、MaxFrameRate、RenderScale 和 Idle 用于复核限帧政策；没有把它与前一轮不同时间的基线直接相减。

| 视角 | 通用路径总 GPU（ms） | 精简路径总 GPU（ms） | 差值 |
|---|---:|---:|---:|
| 街道 | 12.332 | 12.437 | +0.105 ms，约 +0.85% |
| 高处 | 14.029 | 13.995 | -0.034 ms，约 -0.25% |

没有一致、稳定的总 GPU 收益，街道的精简段还出现了明显的通道长尾。本轮不把它报告为有效 GPU 优化。候选实现、测试改动和新增 shader 组合均已从交付源码撤回，实验补丁和候选产物仅保留在本地证据目录。没有因“材质数量很多”或静态代码更少而直接默认启用这项候选。

## 最终验证和复现

最终保留的实现变更是世界材质表默认开启，以及诊断工具和文档。最终 Developer 构建、28 个模块测试、3 个世界材质表原生验证，以及涉及实现文件的 scoped whitespace verify 均通过。本轮候选阶段曾遇到测试缺少类型导入，以及并行 shader 编译占用隔离构建输出的问题；修正导入、待 shader 编译结束后顺序构建均通过，最终日志单独保存，没有将失败日志计作通过。

新增可复用工具：

- `Sandbox.Mounting.Titanfall2/Build/MeasureGpuPasses.cs`：供 `sbox-cli run_csharp` 执行的方法体，先复制到 CLI 的 exchange directory。设置 `T2_GPU_PASS_EVIDENCE` 可指定持久输出目录；否则输出到 exchange directory。它临时切换上述诊断状态，并在 finally 中恢复相机、控制、渲染比例、阴影、预通道和 collector 租约。焦点只记录，不单独过滤后台样本。
- `Sandbox.Mounting.Titanfall2/Build/analyze-gpu-passes.py`：只依赖 Python 标准库，检查连续历史帧、已完成 GPU frame ID 唯一性和有限耗时，输出各段的通道、帧时间及几何统计。各通道分别统计，不对嵌套 scope 做总和推断。

所有以下证据都相对仓库根目录 `.tmp/tf2-gpu-pass-20261107/`：

| 证据 | 内容 |
|---|---|
| `passes.json` / `pass-summary.json` | 两视角、8 段有效通道诊断及可复算统计 |
| `specialization-passes.json` / `specialization-summary.json` | 单层候选同进程交替对照 |
| `candidate-single-layer.patch` / `candidate-source/` | 已撤回的候选差异及 compiled shader |
| `candidate-pixel-tests.log` | 候选 10 个原生验证 |
| `default-build.log` / `final-build.log` | 默认值变更及最终构建 |
| `final-unit-tests.log` / `final-restored-world-tests.log` | 最终 28 项模块、3 项原生回归 |
| `final-format-verify.log` | 本轮涉及实现文件的格式验证 |
| `before-live/` / `final-loaded-shaders.json` | shader 备份及回退后的实际加载身份 |
| `final-artifacts.json` / `final-live-state.json` | 最终磁盘产物、进程和画质状态 |

收尾确认：default / cutout 的 PS 均恢复为 8 个组合，compiled resource 加载有效，磁盘哈希与备份一致；原 draw 表、保守输出和命令列表恢复，两组提交保持原配置。CastShadows=true、WantsPrePass=true、RenderScale=1、VSync=false、MaxFrameRate=500、临时相机 0、三项玩家控制恢复；临时 rendering collector 已移除，最终 profiler 和开发者面板关闭。世界材质表仍为该实例原有的 true，地图准备及常驻碰撞就绪，进程继续运行。

后续值得验证的具体候选，是 cutout 预通道对会被裁掉像素的无用纹理采样：需要在裁剪前准备导数，并验证显式梯度采样、边缘覆盖率、分层/扭曲 UV、MSAA 和阴影的一致性。本轮没有实现或验收该候选，也没有通过关闭整条预通道来替代它。

该 cutout 候选的后续验证已完成，两个实现均因没有稳定收益撤回，见 [cutout 深度通道验证](TITANFALL2_CUTOUT_DEPTH_2026-11-07.zh-CN.md)。通用计时工具现可进一步区分 cutout / opaque 绘制组。
