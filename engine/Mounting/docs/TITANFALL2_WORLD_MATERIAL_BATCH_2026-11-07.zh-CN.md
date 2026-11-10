# 世界材质表批处理：实现收尾与连续帧验证

> 收尾状态：本报告保留历史测量和结论。同进程 A/B 切换入口、额外参照几何和专用跑分脚本已移除；当前配置不再提供 `WORLD_MATERIAL_TABLE_REFERENCE`。`MeasureGpuPasses.cs` 现只采集当前相机和配置，不自动移动相机、改变画质或关闭通道。正常兼容回退与正确性测试保留。

日期：2026-11-07。接续“设计高性能通用挂载框架”聊天最后中断的 CPU/GPU 优化工作。本轮保持引擎源码不变，修改范围为挂载模块、挂载诊断脚本和说明文档。

后续配置决定：用户在上述验证完成后明确要求默认开启。`WORLD_MATERIAL_TABLE` 已改为默认 true，仍可显式设为 0 回退；`WORLD_MATERIAL_TABLE_REFERENCE` 保持默认 false。以下默认关闭的描述记录当时的验证决策，不再代表当前默认值。

## 结论与默认策略

世界材质表确实大幅减少原生渲染提交，但不能按提交降幅推算整帧收益。Angel City 当前固定机位的连续帧对照中，100% 渲染比例平均帧时间仅下降约 0.15 ms（1.2%），与轮次漂移接近；50% 比例下平均帧时间下降约 1.28 ms（11.5%），Render scope 下降约 1.05 ms。GPU 平均时间没有下降，两个比例下分别增加约 0.10 / 0.07 ms。

因此 `WORLD_MATERIAL_TABLE` 继续默认关闭，保留为 CPU 提交受限时的可选路径。`WORLD_MATERIAL_TABLE_REFERENCE` 也默认关闭，只有诊断时额外生成原路径模型。本次验证实例显式开启两者，不能把该实例的状态误认为默认配置。

## 实现与复用边界

- `Resource/WorldMaterialBatch.cs` 按 lightmap 页共享提交材质。源材质参数和纹理索引在加载时捕获到 GPU 表，逐顶点保存材质编号；绘制仍由引擎 `AggregateRenderer` 执行。
- 保留每个原空间片段的 bounds、顶点和三角形范围，继续使用引擎剔除及 aggregate 片段上限拆分。Angel City 为 314 个材质表行、18 个候选 aggregate；诊断参照为 327 个 aggregate，两者保留 1,670 个世界空间片段。
- cutout、动画及其他 shader 使用原路径；透明、贴花等独立分类没有接入该表。按页共用的光照参数保持原绑定，材质差异通过表读取。
- 普通世界顶点仍为 44 字节；只有实验路径使用额外材质编号属性。编号采用可精确表示整数的 float，范围 0–16,777,216，转换前拒绝越界，保留原位置、法线、UV、lightmap UV 和顶点色。
- 生成材质不能通过普通磁盘资源属性恢复，因此地图构建持有源材质数组，场景通过独立缓存键恢复。地图构建释放时移除键；控制器禁用或销毁时释放 GPU buffer。缓存键彼此隔离，不将生成材质数组序列化成磁盘引用。
- buffer 绑定使用场景视图属性，当前明确限制一个 scene 中只有一个材质表世界。此限制尚未推广成通用多世界绑定方案。
- 可复用部分仍是现有 `MountedAggregateBuilder` 的几何准备、片段拆分和原生提交。TF2 材质兼容性、光照页和参数布局属于游戏适配层，尚未抽成所有游戏通用的材质 ABI。

## 构建与测试

在 `.tmp/tf2-refactor-20261106/workspace` 隔离构建，避免修改主工作区引擎输出。最后功能调整后的验证：

| 检查 | 结果 | 证据（相对仓库根目录） |
|---|---|---|
| `SboxBuild build --config Developer` | 通过 | `.tmp/tf2-runtime-opt-20261107/continued-build.log` |
| `SboxBuild build-shaders` | 通过 | `continued-shaders.log` |
| 模块 Refactor.Tests | 28/28 通过 | `continued-unit-tests.log` |
| 经 SboxBuild 运行 `Titanfall2WorldBatchValidation` 过滤 | 实际命中 3 项，全部通过 | `continued-native-tests.log` |
| 格式整理后的 Developer 构建 | 通过 | `continued-formatted-build-retry.log` |
| 本轮 6 个实现文件 whitespace verify | 通过 | `continued-scoped-format.log` |
| 全仓库 `SboxBuild format --verify` | 未通过；其他文件存在既有格式问题 | `continued-format-all.log` |

上表除第一项外，未写完整路径的日志都位于 `.tmp/tf2-runtime-opt-20261107/`。原生过滤命中的 3 项为片段上限拆分，以及普通 / 分层材质颜色与深度法线对照。四份像素输出的平均及最大绝对误差均为 0；测试另有仅改变第 1 行材质的正向控制，确认编号确实影响对应像素。证据为 `continued-pixels/pixels-*.json`。这些是合成夹具结果，不能替代整图、全部材质和阴影验收。

格式整理后的第一次重建因默认 NuGet HTTP 缓存目录无写权限发生 NU1900，设置工作区内 `NUGET_HTTP_CACHE_PATH` 后重试通过。没有关闭包漏洞检查。完整格式检查仍报告其他模块及既有测试文件的问题；没有为使检查变绿而改动它们。

## 部署和运行身份

开始时 CLI 与系统进程均无 s&box 实例。为本次验证启动 PID **26628**，启动 UTC `2026-11-07T01:56:47.3402374Z`，执行文件 `D:\sbox\sbox-public\game\sbox.exe`。私人 `facepunch.sandbox` 会话加载 `mount://titanfall2/maps/mp_angel_city.bsp.scene`。

本次安装的是经过上述测试的功能版本，随后源码仅作 whitespace 整理；运行中的 DLL 没有被替换。必须区分实际运行版本和后续格式化构建输出：

| 产物 | 身份 |
|---|---|
| 实际挂载 DLL SHA-256 | `BAD5D18B019B8FA2FA61FC72549510B3EA75C9D998FA12E29454E07C4A002368` |
| 运行时 MVID / Location | `4ab355fd-8b91-4561-9d23-92f506863640` / `game/mount/titanfall2/titanfall2.dll` |
| 已部署 world shader_c SHA-256 | `17D11416DEE2BE1E84E42CB47685D16BDBBAF9437A106733C95479C330AA160E` |
| 主工作区托管引擎 SHA-256 | `04F25A4A8772669A2D8A43DC486D2BA24B5EA6F59510D955AA008A599273163A`，与部署前一致 |
| 格式化后的隔离 DLL SHA-256 | `6A9F7C43ABFEB454C96FC63C980866A8F429CD5551052EB403E2CDC5820EB895`，未覆盖正在运行的 DLL |

备份、前后哈希和启动记录在 `continued-before/`、`continued-deployment.json`、`continued-owner.json`。运行时的 314 个源材质全部有效，静态道具准备和常驻碰撞均就绪。另在实际进程确认 world lightmapped shader 有效，VS / PS 的 `WasLoadedFromCompiled` 均为 true，记录为 `continued-loaded-shader.json`。

## 测量方式和采样修正

同进程固定相机，位置 `2032,-1850,189.0323`，旋转 `0,0,0.70711,0.70711`，FOV `76.51833`，输出 2560×1600。VSync 关闭，MaxFrameRate 500；保持阴影、LOD、材质、后处理设置，分别测 100% 和 50% 内部渲染比例。

最初补测使用 `Task.Delay` 观察不同 frame ID，产生 `continued-full-world.json` / `continued-half-world.json`，共 3,840 个观察样本。复核发现这些是抽样而非连续帧，而且物理固定更新不会落在每个渲染帧上。物理 scope 的中位数可以降为 0，并不表示物理工作消失。不能据此把整帧中位数降幅直接解释成同等吞吐改善。

最终采用引擎已经记录的 `DevTools.FrameHistory`：每个比例按 reference → table → table → reference 顺序，切换后预热 3 秒，再等待至少 260 个历史帧推进，读取最后 240 个**连续**帧。每段历史编号均连续，总计 8 段、1,920 个连续帧，焦点观察均为 true。每种状态每个比例有两段、480 帧。统计先在段内计算，再等权汇总两段；表中的 P95 是两段 P95 的平均，不是合并分布的 P95。

GPU 值是该 CPU 帧记录时最新返回的 GPU 时间，存在返回延迟，不能作同编号 CPU/GPU 精确配对。Render scope 包含原生提交与可能的等待，不能全算作纯 CPU 计算。焦点以采样时检查为准，没有额外修改引擎记录逐帧焦点。

## 最终连续帧结果

| 指标 | 100% reference | 100% table | 50% reference | 50% table |
|---|---:|---:|---:|---:|
| Frame 平均（ms） | 12.399 | 12.247 | 11.178 | 9.897 |
| Frame 段内中位数平均（ms） | 12.722 | 12.427 | 12.254 | 10.230 |
| Frame 段内 P95 平均（ms） | 15.475 | 14.916 | 13.844 | 13.509 |
| GPU 最新值平均（ms） | 12.004 | 12.101 | 7.719 | 7.794 |
| Render scope 平均（ms） | 8.073 | 7.934 | 7.168 | 6.123 |
| Physics scope 平均（ms） | 2.079 | 2.132 | 1.871 | 1.687 |
| 限帧 Idle 平均（ms） | 0 | 0 | 0 | 0 |

100% 两段 reference 的 Frame 平均分别为 12.548、12.249 ms，本身漂移约 0.30 ms，大于合并后 0.15 ms 的收益。两段 table 分别为 12.253、12.241 ms。只有两段每状态，不报告统计显著性或全图稳定提帧。50% 下 table 的 Render 平均下降约 1.05 ms，支持该路径减轻当前机位的提交开销，但没有降低 GPU 工作时间。

提交统计来自各段的现场观察样本。100% 的段中位数汇总：

| 全场景统计项 | reference | table | 说明 |
|---|---:|---:|---|
| Aggregate indirect submits | 888 | 60 | 下降 93.2%，不是总帧时间降幅 |
| Draw calls | 约 1,853 | 1,024 | 包含其他场景内容 |
| Material changes | 约 1,680 | 851 | 下降约 49.3% |
| Constant buffer updates | 3,648 | 1,977 | 下降约 45.8% |
| Aggregate fragments | 3,659 | 3,659 | 全场景、多通道统计，不等同于世界构建的 1,670 个片段 |
| Triangles | 约 2,937,430 | 约 2,937,436 | 全场景动态内容有少量波动；没有大幅减面 |

原始记录：`continuous-full-world.json`、`continuous-half-world.json`；可复算汇总：`continuous-summary.json`。旧聊天中 `ab-*.json` 仅作为历史数据保留，不与这次不同机位或不同采样方法的数据跨轮相减。

## 复现脚本和收尾

原同进程 A/B 采集脚本和专用解析器已退休，清理前源码归档于 `.tmp/tf2-closeout-20261107/before/engine/Mounting/Sandbox.Mounting.Titanfall2/Build/`。当前正常配置不再生成额外参照几何。原始记录和汇总仍保留，可用于复核本报告数值；当前场景的 GPU pass 采集改用只读场景状态的 `Build/MeasureGpuPasses.cs`。

收尾已移除本次临时 rendering collector，并等待引擎刷新后复核：RenderScale=1、VSync=false、MaxFrameRate=500、Profiler=false、开发者面板关闭、临时相机 0、玩家三项控制均恢复、材质表仍为该验证实例启动时的 true，314 个源材质有效，准备及常驻碰撞就绪。证据为 `continued-final-state.json`。进程继续运行。

已查看引擎直接生成的 `continued-final-world.png`，当前建筑和世界材质有正常出图。该截图不是相同机位的零售游戏比较，也不替代多地图、全部材质、阴影、卸载重载、长时间和实际玩法碰撞验收。下一步 GPU 优化应针对真实 pass 的耗时另作隔离对照；当前数据不支持声称已经同时提高 CPU 和 GPU 吞吐。
