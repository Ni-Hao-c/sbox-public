# Titanfall 2 性能工作收尾（2026-11-07）

按用户决定结束本轮优化，移除性能 A/B 专用代码与无用状态，保留已验证实现、正确性回归和测量结论。

## 保留的实现与结论

- 世界材质表默认开启，可通过 `SBOX_TITANFALL2_WORLD_MATERIAL_TABLE=0` 在启动时选择兼容路径。同 lightmap 页合批，保留原空间片段、边界、三角形范围和原生 aggregate 剔除。固定视角证据显示提交数明显下降，但正常分辨率整帧改善较小，不能按 draw 降幅推算 FPS。
- GPU 静态道具路径、混合材质拆分、GPU PVS / 视锥 / 距离 / LOD / 阴影资格筛选保留。初始化失败时仍使用 CPU 渲染；启动配置可关闭 GPU 路径。特殊材质残余网格仍由原生对象绘制。
- 单层 shader 特化、cutout 显式梯度、quad 提前裁剪均没有通过稳定收益验收，候选实现已撤回。cutout 深度组在所测街道视角只约 0.30 ms，普通不透明深度组约 0.92 ms；前向两组各约 1.0–1.1 ms。数据只代表记录的视角与配置，不证明全地图 FPS。
- 在只改挂载模块且保持画质的前提下，继续大幅改善的难度已经提高；尚无证据支持继续追加小型 shader 改写能稳定提高整帧性能。

详细结论与历史数据：

- [世界材质表批处理](TITANFALL2_WORLD_MATERIAL_BATCH_2026-11-07.zh-CN.md)
- [GPU pass 分析与单层候选](TITANFALL2_GPU_PASS_ANALYSIS_2026-11-07.zh-CN.md)
- [cutout 深度候选验证](TITANFALL2_CUTOUT_DEPTH_2026-11-07.zh-CN.md)

## 清理范围

- 删除 `WORLD_MATERIAL_TABLE_REFERENCE`、额外世界参照 aggregate / shadow 标记、场景互斥显示列表与 `SetTableEnabled`。材质源租约和场景 GPU buffer 绑定仍负责正常克隆场景的生命周期，不属于实验代码。
- 删除 streamer 的 `SetGpuRenderingEnabled` / `SetMixedGpuRenderingEnabled`，以及用于反复切换的原生对象创建、mask 恢复辅助、GPU 所有权重复标志和跨帧实例列表。混合道具只在加载时移交正常渲染所有权；加载失败的 mask 回滚保留。
- 删除 GPU 场景混合对照开关、compute shader 的混合对照筛选和两份无用保守输出。视图溢出仍分别使用相机 / 阴影的只读保守输出，内存预算与回归断言同步更新。
- 删除 `MeasureWorldMaterialBatch.cs`、`analyze-world-material-batches.py`、单次 shader 指令研究脚本和 `Titanfall2GpuMapTests.cs` 的固定机位 ABBA 跑分夹具。
- `MeasureGpuPasses.cs` 改为当前相机的单段采集，不再移动相机、接管玩家控制、改变渲染比例或关闭阴影 / 预通道。只临时启用计时和绘制分组 marker，并在 finally 中恢复；分析器仍能解析历史数据。
- 删除依赖旧 shader 快照的 8 组候选像素对照，GPU marker 验证独立为 `GpuMarkerValidation.cs`。保留材质、渲染覆盖率、LOD、PVS、阴影、视图池、预算及资源生命周期的正确性测试；删除退休的 A/B 状态断言。

清理前相关源码存于 `.tmp/tf2-closeout-20261107/before/`，原始性能测量与撤回候选仍在原 `.tmp` 证据目录中。它们不属于正常编译与运行入口。历史报告明确标注旧 A/B 接口已经退休。

## 验证和部署边界

所有构建都在 `.tmp/tf2-refactor-20261106/workspace` 完成，日志在 `.tmp/tf2-closeout-20261107/`。

| 检查 | 结果 | 证据 |
|---|---|---|
| 清理后 Developer 构建 | 通过；实验快照夹具退休后再次构建通过 | `build.log`、`final-build.log` |
| Shader 构建 | 通过；此次 compute shader 删除混合对照筛选 | `shaders.log` |
| 模块正确性测试 | 26/26 通过；删除的 2 个 data rows 仅对应退休混合对照模式 | `unit-tests.log` |
| 原生回归 | Engine 2/2、Integration 35/35 通过，其中 8 项旧快照实验随后退休；保留的生产回归覆盖 GPU 剔除、间接绘制、混合材质、空 LOD 和材质表 | `native-tests.log` |
| 最终 marker / 材质表复核 | 新夹具重建后，实际命中 Integration 4/4 通过 | `final-native-tests.log` |
| 本次修改文件 whitespace verify | 模块、单元夹具、Integration 与新 marker 夹具均通过；工作区加载仍有既有警告 | `format-verify-*.log`、`final-marker-format.log` |
| 通用采集工具 | 当前相机单段运行、JSON 分析成功，结束后恢复 profiler / collector | `passive-smoke/`、`passive-smoke-command.log` |
| 静态清理审计 | 活跃源码中退休接口 / 配置引用为 0；采集脚本不修改相机、控制或画质 | `source-audit.json` |
| 当前部署与实例 | DLL / compiled shader 哈希不变；退出前读回的 MVID、世界就绪、控制与画质状态正常，最终已无运行实例 | `live-artifacts.json`、`final-live-state.json`、`final-instance-status.json` |

未重新执行已知有其他文件既有问题的全仓库格式整理，也没有把本次限定测试等同于全仓库或全地图验收。

仅在隔离 workspace 构建和验证清理后的 DLL / shader，不替换主工作区当前挂载 DLL 或 compiled shader，不重启当前游戏。本次清理的源码不会通过此收尾操作注入现有运行实例。

最终进程检查时 PID 26628 已不存在，CLI `--list` 与系统进程列表均没有 s&box 实例。此前最后一次读回的玩家控制、相机、profiler / collector 和画质状态均正常。没有执行关闭或重启动作，退出原因未确认；保留时间先后的两份状态，不把退出前快照描述为当前运行状态。
