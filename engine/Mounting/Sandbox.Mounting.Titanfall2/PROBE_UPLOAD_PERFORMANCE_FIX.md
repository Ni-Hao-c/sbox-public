# 天使城实例探针索引上传优化

2026-10-29。用户报告 Steam 独立游戏天使城约 20 FPS。本次修复重复 GPU 上传；没有改变纹理分辨率、渲染分辨率、LOD 距离、道具显示距离或光照参数。

## 根因与实现

`Titanfall2ProbeInstanceBatch.DrawInstances` 在每个批次的每次绘制中调用 `GpuBuffer.SetData(sourceIndices)`。引擎将此 API 定义为同步上传。很多批次只有一个实例，或始终按原顺序绘制，但仍在深度、颜色等 pass 中反复上传同一个 `[0,1,2,...]` 索引表。

现在每个批次在创建时上传一次只读 identity 索引表。绘制索引是它的前缀时直接复用；LOD 重排、距离剔除或其他子集仍使用原来的动态索引上传路径。identity 与动态索引使用不同 GPU buffer，避免动态更新破坏共享只读表。三个 GPU buffer 均在批次销毁时释放，构造失败也沿用清理路径。

`ReuseIdentityIndices` 保留同进程旧 / 新路径 A/B；默认开启。`GetIndexStatistics()` 提供实际上传和复用次数，不从 FPS 推算命中率。

## 有效的同进程对照

Steam 原生 renderer，天使城，固定相机 `(2101.92,-1914.86,180.0312)`、yaw 94.5、FOV 90。初始渲染配置为 2560×1600、无 MSAA、upscaling Off。两次采样在同一候选程序集进程中，只切换索引复用开关。

| 指标 | 原上传路径 | 复用只读索引 |
| --- | ---: | ---: |
| FPS | 24.6 | 56.6 |
| 平均帧耗时 | 40.72 ms | 17.65 ms |
| GPU 平均耗时 | 40.73 ms | 11.51 ms |
| 主线程 Render | 38.71 ms | 15.46 ms |
| 绘制次数 | 3103 | 3103 |
| 三角形 | 2,039,899 | 2,039,899 |
| 全图实例批次 | 5559 | 5559 |

这一个固定机位的 FPS 增加约 130%，GPU 耗时减少约 71.7%。开启时 95.77% 的探针索引绘制复用了静态表。瓶颈从 GPU 转为 CPU 绘制提交；不是 56.6 FPS 的全图最低帧保证。

有效原始数据为 `.tmp/tf2-fps-ab-off1.json` 和 `.tmp/tf2-fps-ab-on1.json`，运行 MVID 均为 `d7b1cbd1-9e47-453f-95f8-4bd700dde6f6`。其余 `off2/on2/off3/on3` 采样中相机发生移动，或尚无稳定帧窗口，因此不用于收益计算。后续 before/after 截图同样没有满足相同机位条件，不作为画质等价证据。不能挑选其中 69.6 FPS 的单次结果来宣传收益。

## 正确性和构建检查

实际 Steam GPU 路径：

- `VerifyProbeBatchLod.csx`：23 / 23。包含 LOD 分组与反序、非连续重排、恢复全量、identity 全量与前缀、关闭优化的旧路径，以及三个 buffer 的销毁。
- `VerifyEnvironmentSkyProbes.csx`：84 / 84。普通对象与实例化对象的实际材质光照、两种 shader、七个法线方向及天空覆盖。
- `VerifyCompiledLocalLights.csx`：30 / 30。保留每个实例的局部灯索引与权重。

合计 137 项受控 GPU 检查通过。这些检查验证索引和光照传输，不代替所有地图的固定机位视觉验收。

Steam SDK Release 编译 0 warning / 0 error。源码 SDK 构建、Titanfall 2 过滤套件、部署指纹及最终格式检查见 `.tmp/tf2-fps-verification.json`。没有运行全仓库 CI。

## 部署与现场保护

候选版本已通过 `Deploy-Steam.ps1` 完整备份、44 文件哈希核对和 28 条 shader 原生增量检查后部署。未修改 shader 源码；已有 shader 均为 up to date。

Steam DLL SHA256：`D8AF1416C603757251F51E40489242531E05FA8621227A76A24CB1334B32E01C`。运行 MVID 与该部署产物对应。部署前备份位于 `D:/Game/steams/steamapps/common/sbox/mount-backups/titanfall2/20261029-205934-316/mount`。

测试进程均由 CLI 拥有并已退出。后续发现用户新打开了 ttf2 编辑器，因此没有启动第二个基准实例、关闭该编辑器或覆盖运行中的安装。游戏项目的代码、场景及视频设置均未由本次优化改写。
