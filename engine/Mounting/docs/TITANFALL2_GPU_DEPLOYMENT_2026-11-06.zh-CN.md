# Titanfall 2 GPU 改造部署与实际地图验证

日期：2026-11-06。目标：`D:\sbox\sbox-public\game`。状态：新版本已部署、目标引擎实际运行验证完成；本次固定机位样本没有证明整图 GPU 提速。

## 部署与运行身份

- 部署范围：`game/mount/titanfall2/titanfall2.dll` 及挂载根 shader 目录下 30 个生产 `.shader_c`，共 31 个文件、6,000,076 字节。未部署隔离测试 shader 或 `pixel_reference` 对照资源。
- 部署前逐文件备份，部署后及测试结束均逐文件校验，最终 31/31 哈希一致。
- 回退文件和部署清单：`.tmp/tf2-refactor-20261106/deployment/before/`、`deployment/manifest.json`。`before/` 同时保存旧版测量 JSON；回退文件以清单的 `Backup` 字段为准。
- 新 DLL SHA-256：`6F38016028F2080137C896F43123AB23D371382AD6DD3D38032DB709A604039F`。
- 新 DLL 实际加载路径：`D:\sbox\sbox-public\game\mount\titanfall2\titanfall2.dll`。
- 新 DLL 实际 MVID：`3c583890-17a8-408d-a0f3-2de9c4627f8a`。
- 对照 DLL SHA-256：`F975C2FD2781DE430A201EB880380F8FE17488B1A1B295A43F7ED4B5BFB79D4D`，实际 MVID `b206de29-1c77-44a1-8a66-61358799be40`。
- 目标托管引擎 SHA-256：`04F25A4A8772669A2D8A43DC486D2BA24B5EA6F59510D955AA008A599273163A`，实际 MVID `f8177a3c-2c38-46a5-9042-923d08a71894`。它与前一阶段隔离验证引擎不同，本次没有替换引擎文件。
- 目标引擎存在所需托管方法，运行时兼容边界 `Available=true`；真实地图已使用 GPU 后端。
- 最终运行中的 default/cutout 均有效、`WasLoadedFromCompiled=true`，PS 各有 8 个组合，包含新精简组合。身份记录见 `deployment/loaded-shaders.json`。

最终进程 PID 为 43740，执行文件为主目录 `game/sbox.exe`，运行私人 `facepunch.sandbox` 会话。早先两次运行分别为新版 PID 34680、旧版 PID 33864，均为本次创建的测试进程。

## 地图与可见路径验证

- 地图：`mount://titanfall2/maps/mp_angel_city.bsp.scene`。
- streamer 状态 Ready，`PreparationComplete`、`ResourcesPrepared` 为 true。
- GPU 场景 19,303 实例，包含 18,689 个完全接管实例及 614 个混合实例；495 个材质身份、2 个材质组，两个组均使用精简路径。
- 新旧对照的 GPU 静态几何均为 2,759,870 顶点、7,166,595 索引、4,722 个 draw records。draw records 是整个布局记录数，不是单帧可见 draw 数。
- 8 个 view slots，高水位 3，最终累计 27,351 次录制时 `ConservativeRecordings=0`，没有依靠保守回退完成本次测量。
- 新 GPU 标记已在目标进程显示，能区分 Shadow / DepthPrepass / Opaque 以及 reset、cull/LOD、args 和 barrier。最后一次 GPU 快照中 Static Opaque Forward 约 3.06 ms、Depth Normal Large 约 1.44 ms、Translucent Forward 约 1.48 ms；这些父通道与其子通道不能相加。
- 实际窗口检查和最终引擎截图均可见完整街道、城市远景、霓虹、武器与 HUD，没有把隐藏整组地图内容作为优化条件。没有完成整张地图逐材质视觉验收。

碰撞仍未达到完整 Ready：11,528/11,725 个目标 collider，缺口 197。不同运行中的 missing-model 与 unresolved 分类数量有变化，但合计缺口及 resident collider 数一致。本次不将渲染验证等同于完整玩法碰撞验收。

## 固定机位对照条件

顺序为新版 A → 部署前版本 → 新版 B，每次使用新启动进程、同一目标引擎、相同 31 个挂载产物集合的相应版本。最后已恢复新版。

- 三轮均为 2560×1600 输出、原生 Vulkan renderer。
- 当前用户配置：MSAA None、CMAA2、Upscaler Off、VSync Off，fps_max 500。启动请求 exclusive fullscreen 失败后，由引擎回退 desktop fullscreen；各轮一致。
- 这与早先截图/旧报告中的 4× MSAA 条件不同，不能将这里的帧时间与早先约 47–49 FPS 直接相减并归因于此次改造。
- 固定位置 `(-3729,2245,470)`，角度 `(5,0,0)`，实际 FOV 76.51833。克隆游戏相机保留其后处理组件；暂时关闭玩家相机跟随、输入和 look controls，物理及其他系统继续运行。
- 开发者面板关闭，GPU profiler 开启，直接从引擎读取数据。新版新增标记的 profiling 成本包含在本次结果内。
- 每轮比例顺序 `100%、75%、50%、50%、75%、100%`，每段预热 4 秒，采集 300 个不同 frame ID。每轮 1,800 个样本，总计 5,400，最终接受的各段均为 300/300 有焦点样本。
- 首次直接前台工具调用丢失焦点，0/300 有焦点的尝试已拒绝，没有混入结果；后续使用隐藏采样进程及窗口焦点检查。
- 每个比例每轮合并两段，共 600 个样本。相机、FOV 和输出尺寸均校验。

## 结果

GPU 中位时间，单位 ms：

| Render scale | 部署前版本 | 新版 A | 新版 B |
|---|---:|---:|---:|
| 100% | 11.696 | 11.685 | 11.745 |
| 75% | 10.087 | 10.176 | 10.099 |
| 50% | 7.442 | 7.542 | 7.487 |

100% 的整帧统计：

| 指标 | 部署前版本 | 新版 A | 新版 B |
|---|---:|---:|---:|
| Frame p50（ms） | 12.052 | 12.159 | 12.227 |
| Frame p99（ms） | 25.837 | 14.968 | 19.734 |
| Draws p50 | 2,061 | 2,056 | 2,057 |
| Triangles p50 | 3,143,587 | 3,143,575 | 3,143,581 |

本样本没有测到整图 GPU 收益。100% 新版相对旧版 GPU 中位变化约 -0.10% 与 +0.42%；50% 新版反而高约 0.04–0.10 ms，不能报告为性能提升。当前固定机位下，消除的少量可选采样没有转化为明确的整帧收益。

降低分辨率确实降低 GPU 时间：新版从 100% 约 11.7 ms 降到 50% 约 7.5 ms。但 CPU、几何、固定成本与同步仍然存在，四分之一像素不等于四分之一整帧时间。

短样本的 p99 有改善现象，但不同进程中的背景活动、缓存和设备状态未完全控制，玩家出生位置和少量动态/UI draw 也有细小差异；不能据此宣称卡顿问题解决。本次是一个固定机位、一个地图的范围测试，未覆盖移动路线、全材质、长期运行或多相机长期稳定性。

## 收尾与证据

- 最终 31 个文件均为新版哈希，托管引擎哈希与部署前一致。
- 临时固定相机已销毁，原相机及玩家三项控制恢复，render scale 恢复 1，原 FPS 设置恢复，GPU profiler 关闭。
- 对比 `video-before.json`，原有 video 配置各键的 Value 未发生变化。
- 新版进程保留运行，供用户直接查看地图。
- 旧版对照进程正常退出等待超过 20 秒；日志已到 `Source2Shutdown` / `ShutdownSource2Logging`。核对 PID、执行文件和启动时间后，仅强制结束该自有进程。不能据此宣称连续退出/卸载验收通过。
- 最终 Windows Graphics Capture 两次返回 `IGraphicsCaptureItemInterop.CreateForMonitor failed (0x80070057)`。改用引擎自身异步截图成功，保存 `deployment/final-scene.png`，已查看确认出图。

所有原始数据位于 `.tmp/tf2-refactor-20261106/deployment/`：

- `manifest.json`：备份、来源、部署文件及哈希。
- `verification.json`：最终部署、进程和恢复状态。
- `new-a/`、`before/`、`new-b/`：每段原始帧数据、汇总、GPU timeline、实际场景配置与 census。
- `comparison.json`、`analyze.py`：聚合结果和可重复分析脚本。
- `loaded-shaders.json`、`restored-state.json`、`video-setting-differences.json`：实际 shader 元数据及收尾核对。
- `after-final-runtime.log`、`before-runtime.log`：运行日志。
- `final-scene.png`：恢复正常控制后的最终画面；不是固定机位性能采样截图。
