# Titanfall 2 运行期边界重构：第一批

日期：2026-11-06。实现范围：挂载代码与模块 shader。既有工作区修改保留；没有部署到主工作区的运行目录。

## 已实现

1. **固定 GPU 视图池**：加载时建立配置数量的可写输出，render callback 不再增长池。每个同帧 recording 独占一个槽位。超额时使用四份预建只读列表，按 mixed/shadow 分开，顶点阶段继续执行当前视图的 LOD、距离和 PVS 规则。
2. **预算统一**：使用实际 GPU 结构 stride 计算永久缓冲、source→candidate 映射、全部可写输出和四份只读输出。布局编译与对象创建都校验预算；失败仍走原有 CPU reference。预算只覆盖这些缓冲 payload，不包含源模型、纹理、驱动开销或全游戏显存。
3. **绘制命令预录制**：各输出的 MDI 命令在准备阶段建立，首次 render 不再建立命令列表内容。
4. **延时回收代次**：地图构建、场景取得 lease、mount 和 shutdown 取消旧任务。延时回收切回主线程，重新验证代次和活跃消费者；旧任务 finally 不会清除新代任务状态。同代 follow-up 合并。
5. **加载与运行分离**：保留组件身份和序列化字段，加载准备移到 Preparation partial，运行协调移到 Runtime partial。CPU cluster 归属在后端变化时缓存。GPU 批次不进入 CPU 可见性队列，混合模型原生残余仍参与。
6. **减少无消费者的调度**：准备完成后不再执行 populate；全部 GPU 接管且常驻碰撞完成时，不刷新无消费者的 CPU 空间队列。保留低内存碰撞模式、损坏碰撞修复与诊断切换路径。
7. **实际配置与统计**：显式快照提供质量参数、后端参数、模块/引擎 MVID 和模块位置；增加运行状态、CPU render cluster、槽位高水位、超额 recording 与缓冲字节统计。快照和序列化不在每帧执行。

## 超额视图的取舍

只读输出包含每个模型四级 LOD 的源 ID，顶点阶段根据当前视图只保留应出现的一级，因此空远景 LOD 仍为空。使用专门分支避免正常 compute 路径执行这段额外判定。

超额路径省去 compute 视锥剔除，会增加顶点处理；仍保留距离与 PVS。阴影按原有投影资格分组，但超额路径不更新/执行 GPU 距离阴影回滞门控，开启该门控时可能额外出现远处阴影。`ConservativeRecordings > 0` 表示应检查槽位配置；增加 `GPU_VIEW_SLOTS` 同时增加显式预算。

固定 8 槽不等于固定 8 个 camera：一次 camera 的 depth、forward、shadow 等 recording 可能分别占槽。设置在进程初始化时读取。

## 验证方法

所有构建与 native 回归在独立副本中运行：

```text
.tmp/tf2-refactor-20261106/workspace/
```

该副本的代码来自当前主工作区；game 内容/预编译内容以已有隔离环境为基础，随后用本次 shader 源码重新编译。副本使用独立 managed 输出、模块 DLL 和 shader 产物，没有链接回主工作区的输出目录。

新增独立测试位于 `Sandbox.Mounting.Titanfall2/Build/Refactor.Tests/`，由模块已有的 `Compile Remove="Build/**"` 排除，不修改 `engine/Tests/` 或引擎解决方案。它不需要 native 初始化；已有图形回归由 SboxBuild 执行。

验证重点：池容量/并发/构建失败清理、代次取消、四份保守列表的来源与归属、空远景 LOD、结构/预算、CPU/GPU 材质与混合模型图像。图形回归设置 `SBOX_TEST_GRAPHICS=1`、`SBOX_TITANFALL2_GPU_VIEW_SLOTS=1`，对额外 recording 的路径施加压力。

第一次图形回归发现固定 LOD0 的超额方案破坏空远景 LOD，现已改为顶点阶段执行同一 LOD 规则。保留该回归结果作为修复依据，不将第一次结果记作通过。

最终结果：

| 检查 | 结果 |
|---|---|
| SboxBuild `build --config Developer --skip-native` | 通过；全量托管构建，native 使用隔离副本中的预编译产物 |
| SboxBuild `build-shaders` | 通过；最终版本重新编译 default/cutout，其他 shader 扫描复用 |
| 新增独立回归 | 10/10 通过 |
| 既有布局与接口回归 | 15/15 通过 |
| 图形与资源生命周期范围回归 | 20/20 通过，含空远景 LOD、混合子网格、default/cutout、第二相机和 managed/native 场景路径 |
| 最终 vertex 分支收尾后复验 | 5/5 通过，含生产材质 CPU/GPU 对照和统一 cull |
| 本模块完整 `dotnet format --verify-no-changes` | 通过 |
| 全仓 SboxBuild `format --verify` | 未通过；首次检查中本批新增文件的问题已修正并通过模块复验，其余报错来自未修改的 Helldivers2 文件与既有混合材质测试文件的格式/行尾 |

新增测试不在 SboxBuild 的内置项目枚举中，额外执行：

```text
dotnet test engine/Mounting/Sandbox.Mounting.Titanfall2/Build/Refactor.Tests/Refactor.Tests.csproj -c Release -p:BuildProjectReferences=false -p:SkipCopyOutputDll=true
```

该命令在先完成构建的隔离副本中运行。范围回归使用 SboxBuild `test --no-build --filter`，选择 GPU scene、GPU visibility、mixed prop、compiled scene 和 SceneLoader/ResourceLoader release 用例；其他测试项目无匹配不计作额外测试数量。

本地证据：[构建](../../../.tmp/tf2-refactor-20261106/build-final.log)、[shader 编译](../../../.tmp/tf2-refactor-20261106/shaders-final.log)、[新增测试](../../../.tmp/tf2-refactor-20261106/refactor-tests-final.log)、[范围回归](../../../.tmp/tf2-refactor-20261106/scoped-tests-final.log)、[最终 shader 对照](../../../.tmp/tf2-refactor-20261106/shader-conformance-final.log)、[模块格式](../../../.tmp/tf2-refactor-20261106/module-format-verify.log)、[产物身份](../../../.tmp/tf2-refactor-20261106/verification.json)。

隔离构建的模块、挂载目录副本与两个既有测试输出目录的模块 SHA-256 均一致：`01dadb2251c82642c11f58914b7555d58e60992f09b827799eec8db6bc40c7e5`。最终 shader 二进制和源码摘要单独记录在 verification.json。主工作区部署 DLL 仍为原有 `254a61567c79b62108ac3d7de35b34ee2d8ec4461c2e01ce3cb543f2a8f3a82b`。

## 交付边界

本批完成运行期边界和调度职责的重构。通用 VFS、Northstar Mod 全面接线、精确按地图资源所有权表、PreparedMapPlan 和跨游戏框架提炼仍按[迁移路线](TITANFALL2_MIGRATION_PLAN.zh-CN.md)继续；没有将它们标成已经实现。

现有 GC 补救措施仍保留，只收紧执行时机。没有完成真实 Angel City 全图性能、连续多图驻留、服务器或长时间验收。Scoped 图形/单元测试不代表整体 FPS 已提高。
