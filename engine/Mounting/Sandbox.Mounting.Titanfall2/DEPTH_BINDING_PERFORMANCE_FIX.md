# 实例深度预通道的探针绑定开销

2026-10-29。接续 [PROBE_UPLOAD_PERFORMANCE_FIX.md](PROBE_UPLOAD_PERFORMANCE_FIX.md)，进一步处理 CPU 绘制提交。此前的 identity 索引复用继续保留。

## 实现

生产静态 opaque/cutout shader 在深度模式下，先输出覆盖率、法线和粗糙度，之后才是探针和局部灯光。因此 `Titanfall2ProbeInstanceBatch` 在 `SceneLayerType.DepthPrepass` 中可直接提交相同模型、变换、LOD，而不准备探针索引或绑定光照资源。

- 在 `StaticPropStreamer.Batches.cs` 中，仅为生产实例批次启用 `SkipDepthProbeBindings`。通用构造器默认保留完整路径，兼容只有 Forward 模式的自定义或诊断 shader。
- Forward 和 Shadow 路径继续绑定全部探针 / 局部灯数据；没有将这个优化扩大到未经本轮验证的 Shadow 路径。
- 五个固定属性名提前转换为 `StringToken`。挂载 DLL 没有游戏编译器的字符串字面量重写，原来的 string 重载每次都要查询 token 字典。
- 保留同进程开关及 `SkippedDepthDraws` 统计，用于确认快路径实际执行。

没有修改 shader、LOD 距离、显示距离、纹理分辨率、地图资源或碰撞策略。批次大小仍为一个 cell；没有把更大的批次设为默认。

## 三个视角与有效收益范围

使用 Steam SDK 的独立测试进程加载实际 `mp_angel_city`，不包含游戏侧玩家控制，避免相机被游戏代码或输入覆盖。每个视角按关 → 开 → 关 → 开采集；相机和总共 19455 个静态道具、5559 个批次保持一致。

密集街道的四个短窗口：

| 路径 | FPS | CPU / 帧 | GPU | Render |
| --- | ---: | ---: | ---: | ---: |
| 完整深度绑定，第 1 次 | 51.3 | 19.51 ms | 11.95 ms | 18.17 ms |
| 跳过深度光照绑定，第 1 次 | 52.0 | 19.25 ms | 11.55 ms | 17.77 ms |
| 完整深度绑定，第 2 次 | 50.2 | 19.90 ms | 12.72 ms | 18.30 ms |
| 跳过深度光照绑定，第 2 次 | 54.5 | 18.36 ms | 10.91 ms | 17.34 ms |

两次均值约为 50.8 → 53.3 FPS，约 +4.9%；CPU / 帧 19.71 → 18.81 ms，Render 18.24 → 17.56 ms。四个窗口均为 3745 次绘制、2,761,753 个三角形、相同相机，CapWait=0；虽然目标来源在前台 / 失焦之间变化，但没有触及任一帧率上限。两个成对样本的提升幅度有波动，不将其视为全图最低帧率保证。

庭院与草地也完成了相同的开关采样，几何和相机稳定，但部分窗口被失焦 90 FPS 上限限制，或出现瞬时抖动；不以它们计算额外的帧率收益。每个开启窗口都有实际跳过的深度绘制，索引上传与复用操作数约减半。

本轮是隔离相机基准，和上一轮含玩法的独立游戏测试不同。不能把两轮绝对 FPS 连成一条连续收益曲线。属性 token 缓存也没有单独切换测量，因此上述开关对照主要衡量深度快路径。

原始数据：`.tmp/tf2-cluster-bindings-street.json`、`courtyard.json`、`grass.json`，完整文件名使用同一 `tf2-cluster-bindings-` 前缀；运行日志为 `.tmp/tf2-depth-bindings-runtime.log`。

## 验证

| 检查 | 结果 |
| --- | --- |
| `VerifyProbeBatchDepth.csx`，读取原生 G-buffer，覆盖实例旋转法线、粗糙度、alpha clip 和开关两条路径 | 18 / 18 |
| `VerifyProbeBatchLod.csx`，索引、LOD、重排 / 前缀与 buffer 销毁 | 23 / 23 |
| `VerifyEnvironmentSkyProbes.csx`，普通 / 实例探针与天空 | 84 / 84 |
| `VerifyCompiledLocalLights.csx`，局部灯传输与 GPU 光照 | 30 / 30 |
| Titanfall 2 过滤套件，Engine 54 + Integration 3 | 57 / 57 |

合计 155 项 GPU 检查与 57 项挂载回归通过。新 G-buffer 验证最初将背景地图的全局计数误当作局部测试计数，产生两个错误断言；最终测试保留 18 项实际像素输出断言，生产快路径执行次数由基准日志单独验证。

源码 SDK 构建成功，Steam SDK Release 构建 0 warning / 0 error。两个 C# 文件的目标格式检查通过。没有运行全仓库 CI。

## 部署与编辑器

Steam 44 个部署文件通过哈希核对，28 条 shader 均由原生编译器确认 up to date。本次没有 shader 改动。

- Steam DLL SHA256：`B299A29DF26B237FF4709CA74A8B4B7BA2EDDA8A3DC9DF933471827A5676821F`。
- 独立基准与重新打开的 ttf2 编辑器运行 MVID：`1511830a-817e-4e9a-b70f-2ac95d216814`，与部署文件一致。
- 备份：`D:/Game/steams/steamapps/common/sbox/mount-backups/titanfall2/20261029-215314-061/mount`。

用户已授权结束 Play、部署验证后重新打开项目。旧编辑器随后退出；测试结束时发现用户已重新打开 ttf2，因此复用了该实例完成剩余 GPU 检查，没有再启动第二个编辑器。最终 PID 41228，项目 `ttf2`、场景 `minimal`、Play=true、编译成功、无未保存场景修改。

最终可机读摘要：`.tmp/tf2-depth-bindings-verification.json`。
