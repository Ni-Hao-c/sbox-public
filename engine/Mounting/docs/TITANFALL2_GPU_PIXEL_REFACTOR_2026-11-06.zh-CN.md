# Titanfall 2 GPU 像素路径改造（2026-11-06）

> 收尾状态（2026-11-07）：本文保留历史改造结论和测量证据。旧 shader 快照像素对照与 SPIR-V 单次研究脚本已退休；当前只保留 `Build/GpuMarkerValidation.cs` / `.targets` 的独立 GPU marker 回归。下文旧文件名和注入命令记录当时验证流程，不代表当前入口。

## 范围与目标

根据用户的 GPU 面板转录及降低分辨率能够提升帧率的现象，本批次优先减少像素路径中的可避免工作，并补全挂载 GPU 可观测性。生产代码仅修改 `engine/Mounting/Sandbox.Mounting.Titanfall2` 与 `game/mount/titanfall2`。

本批次不改变渲染分辨率、SSR/AO/阴影设置、贴图质量或原有透明通道排序。源码与编译验证不能代替 Angel City 原分辨率、固定机位、实际部署一致的帧时间验收。

## 实现

### 材质可选采样

- default/cutout 共用 `titanfall2_optional_surface.hlsl`。扭曲 XY 都为零时跳过 distortion；detail blend 不大于零时跳过 detail。
- cutout 的专用 UV 选择和滚动扭曲公式保留，只在强度为零时返回原 UV。
- `T2ReadEmissive` 在 emissive strength 恰好为零时跳过底图和乘法图；负强度保持原有计算。
- 通用路径依据当前材质参数判断，动态代理可从零切换到非零。
- 新增两档 PS 动态组合 `D_T2_SIMPLE_SURFACE`。仅在不可变、可材质表化的 GPU 材质上，按“无 distortion 且无 detail”分组选择精简档；普通材质与动态代理保持组合 0。精简档从源码预处理阶段排除这两种采样，不增加 GPU 材质表 ABI 字段。
- `SimpleSurfaceGroups` 随 GPU 场景 census 输出。多一个分组条件可能增加 MDI 组数，必须将提交代价与像素收益一起测量。

### 深度法线与平面法线

- 抽出 `T2ReadSurfaceGeometry`，让完整颜色路径与 `T2ReadDepthSurface` 共用法线、gloss、第二层混合和 UV 规则。
- default/cutout/world-lightmapped 的 depth-normal 分支先读取必要输入，不构建 forward 所需的完整颜色材质。
- default 保留原 opacity 计算；cutout 保留 albedo alpha、opacity 图、vertex alpha、material opacity、alpha reference 和裁切后的 opacity=1。
- cutout 的法线导数计算仍位于 `clip` 之前。保留双面翻转、第二层法线、引擎 decals、alpha-to-coverage 与 DepthNormals 输出。
- 没有任何有效法线图的表面直接使用几何法线，避免建立导数切线框架。
- 共享函数同时影响使用它的透明和世界材质；这些路径也需通过 shader 编译及相关画面对照。

原实现已经在 `T2ShadeSurface` 中提前返回 depth-normal，编译器可能已经消除了部分无用工作。此次显式拆分不等于证明每个被移出的采样之前都会运行，也不据此宣称 GPU 毫秒收益。

### GPU 标记

- 固定 view slot 在加载时预录制 command lists 与 dispatch delegates，运行期不创建名称、闭包或列表。
- 按 DepthPrepass / Opaque / Shadow / Other 与 recording slot 分组，内部标记 input barriers、reset、cull and LOD、args、output barriers 和 MDI draw。
- slot 是录制槽位，不是 native ViewId；实际相机/级联结合引擎父 scope 判断。不据此跨 pass 复用可见集。
- compute 继续使用 slot 的私有 RenderAttributes，保留既有 barrier 与 dispatch 顺序，不向 Graphics.Attributes 合并剔除参数。
- 兼容边界增加一次性绑定引擎已有的 managed `CommandList.AddAction`，用于把私有属性 dispatch 放进公开 ProfileScope 中。没有直接访问 native context/interop；不兼容时 `Available=false`，保留 CPU 后端。
- 曝光 meter/adaptation、Bloom 三阶段和 display transform 增加独立 GPU marker；本批次不改变它们的采样、精度或执行顺序。

## 验证方式

复用 `.tmp/tf2-refactor-20261106/workspace`，不另建完整源码副本。编译前保存旧 shader，并在隔离目录生成 `titanfall2_pixel_reference_*` 资源，作为本批次前后像素对照。对照源来自现有隔离目录的前一阶段快照，不代表历史零改动引擎或零改动 TF2 挂载。

`Build/GpuPixelValidation.cs` 位于挂载目录，通过 `Build/GpuPixelValidation.targets` 可选注入已有 Integration 测试项目；主挂载项目仍排除 `Build/**`。注入属性为 `CustomAfterMicrosoftCommonTargets=<绝对路径>/Build/GpuPixelValidation.targets`，不编辑引擎测试项目文件。

验证覆盖：

1. SboxBuild managed build 与 mount shader build。
2. 精简档参数分类边界，以及已有固定池/预算/回收代次测试。
3. 既有 GPU/native 路径、混合 submesh、第二相机、稀疏 ID 与保守回退回归。
4. 旧/新 default 与 cutout 在平面/双层法线、可选特性开/关组合下的颜色及实际 depth-normal 缓冲对照。
5. PS SPIR-V 检查，确认精简档存在不同编译结果；静态指令计数不代表每像素实际执行次数或 GPU 耗时。

## 验证结果

验证日志位于 `.tmp/tf2-refactor-20261106/`：

| 验证 | 结果 | 日志 |
|---|---|---|
| SboxBuild managed build | 通过 | `gpu-pixel-build-verified.log` |
| SboxBuild build-shaders | 通过，含旧版对照 shader | `gpu-pixel-shaders.log` |
| 参数分类/固定池/预算/代次 | 18/18 | `gpu-pixel-unit-tests.log` |
| 既有布局契约 | 15/15 | `gpu-pixel-scoped-tests.log` |
| 既有图形/混合材质/资源回归 | 20/20 | `gpu-pixel-scoped-tests.log` |
| 新旧颜色及 depth-normal 对照 | 8/8，最终同组运行通过 | `gpu-pixel-fixture-tests-final.log` |
| GPU 标记实际时间戳 | 修正夹具后 1/1，最终同组运行通过 | `gpu-pixel-fixture-tests-final.log` |
| 主挂载项目完整 format verify | 通过（工作区加载警告） | `gpu-pixel-format-verify.log` |
| SboxBuild 全仓库 format verify | 未通过；26 个既有 Helldivers2 文件和既有 MixedProp 测试行尾错误，本批新增文件无错误 | `gpu-pixel-full-format-verify.log` |

首次标记夹具调用既有离屏用例，该用例不推进 `Application.FrameCount`，因此一槽位压力配置下 80 次录制中 79 次走保守回退，最终帧没有 compute 标记。已改为夹具每次完整渲染前推进一次 managed frame，再读取真实时间戳；不在每个 pass 推进，不改变生产池生命周期。8 项像素对照在该夹具修正前已全部通过，失败项仅为原标记夹具。

修正并格式化后，9 项新增图形验证在同一测试进程中全部通过，确认 profiler/帧号状态恢复不影响后续像素用例。本批分项共 62 个测试案例通过；这不代表执行了全仓库测试。产物身份与分项结果汇总见 `gpu-pixel-verification.json`。

像素对照包含平面/双层法线、可选特性开/关、default/cutout；每项比较颜色与实际 normals G-buffer，并通过改变法线确认 readback 确实响应表面输入。它不是整张地图、所有材质或所有代理组合的视觉验收。

SPIR-V 分析使用 `Build/inspect-pixel-spirv.py`，结果为 `gpu-pixel-spirv-summary.json`。以下均为材质表路径的静态编译指令位置数量，不是每像素执行次数：

| PS | 改造前采样位置 | 改造后通用档 | 改造后精简档 |
|---|---:|---:|---:|
| default forward | 227 | 227 | 225 |
| cutout forward | 228 | 228 | 225 |
| default depth | 21 | 21 | 20 |
| cutout depth | 21 | 21 | 19 |

这证明精简组合排除了部分采样。通用档新增跳过条件及平面法线判断，静态总指令数有所增加；这些条件的实际收益必须依靠目标 GPU 测量。深度路径原本已经有无用代码消除，显式拆分本身没有减少通用档采样位置。

编译器在改造前对照 shader 与新 shader 中均报告同一组 13 个 probe/light uniform 的动态组合布局诊断。新 shader 因组合数增加，每个 shader 的重复诊断从 13 条增至 26 条，没有新增不同名称的诊断；编译退出码为 0，相关 probe/native/GPU 图形回归通过。这些诊断保留在日志，不能将编译描述为无诊断。

隔离产物 `game/mount/titanfall2/titanfall2.dll` SHA-256：`6F38016028F2080137C896F43123AB23D371382AD6DD3D38032DB709A604039F`。

主工作区已部署 DLL 在本批次开始及验证期间均为 `F975C2FD2781DE430A201EB880380F8FE17488B1A1B295A43F7ED4B5BFB79D4D`；本批次没有部署该隔离 DLL、替换主目录编译 shader 或启动/重启交互式编辑器。未取得当前实际地图原分辨率的前后帧时间，尚不报告整图 FPS 或 GPU 毫秒收益。

上述为隔离实现验证阶段的状态。用户随后授权部署及实际测试，已完成主目录部署和目标引擎 Angel City 5,400 帧样本对照，最终恢复新版。后续结果以 `TITANFALL2_GPU_DEPLOYMENT_2026-11-06.zh-CN.md` 为准；该对照未证明整图 GPU 提速。
