# Titanfall 2 GPU 部署后继续验证

日期：2026-11-07。验证目标为现有部署的 Angel City 多视角分辨率响应、移动时 GPU 视图池状态，以及上一轮未关闭的碰撞和远景视觉问题。本轮不修改引擎或挂载实现，不重新部署或构建。

## 当前产物和运行身份

- 开始前检查工作区状态，保留原有改动和删除项。
- CLI 和系统进程均未发现上一轮实例，启动单个自有验证实例 PID 7092，启动 UTC `2026-11-06T23:48:26.2416752Z`。
- 执行文件：`D:\sbox\sbox-public\game\sbox.exe`；私人 `facepunch.sandbox` 会话，`mp_angel_city`。
- 31 个部署文件逐项比对上一轮清单，全部哈希一致。
- 实际运行 DLL SHA-256：`6F38016028F2080137C896F43123AB23D371382AD6DD3D38032DB709A604039F`。
- 实际运行 MVID：`3c583890-17a8-408d-a0f3-2de9c4627f8a`。
- 目标托管引擎 SHA-256：`04F25A4A8772669A2D8A43DC486D2BA24B5EA6F59510D955AA008A599273163A`，未替换引擎。
- 真实进程中 default/cutout 均有效并从 compiled shader 加载，PS 各 8 个组合；收尾复核见 `loaded-shaders.json`。
- 部署产物、进程及现场原始配置记录在 `.tmp/tf2-validation-20261107/artifacts.json`、`owner.json`、`live-before.json`、`live-setup.json`。

## 六视角同进程分辨率验证

输出 2560×1600、MSAA None、CMAA2、Upscaler Off，GPU profiler 开启，开发者面板关闭。相机克隆自游戏相机，保留后处理；玩家相机跟随、输入和转向暂时关闭。每个视角执行 render scale `1 → 0.5 → 0.5 → 1`，每段预热 3 秒并采集 240 个不同 frame ID。每个比例每个视角合并 480 个样本，共 24 段、5,760 个样本，全部有焦点。原始数据包含逐帧 frame/GPU 时间、draw、三角形数、scene views、frame ID 和焦点状态。

本次启动现场 VSync 为 **true**，上一轮为 false；本轮保持现场设置。因此不能把本次绝对帧时间与上一轮跨进程结果相减，也不能把分辨率收益归因于代码改造。没有重新执行旧 DLL 对照。

| 视角 | 位置 / pitch,yaw | GPU p50 100%（ms） | GPU p50 50%（ms） | GPU 时间下降 | Frame p50 100%→50%（ms） |
|---|---|---:|---:|---:|---:|
| 街道正向 | -3729,2245,470 / 5,0 | 13.651 | 8.667 | 36.5% | 13.627→10.312 |
| 街道左侧 | 同位置 / 5,90 | 10.155 | 6.206 | 38.9% | 9.961→5.569 |
| 街道反向 | 同位置 / 5,180 | 11.977 | 7.017 | 41.4% | 11.903→6.732 |
| 街道右侧 | 同位置 / 5,270 | 11.951 | 7.285 | 39.0% | 11.625→7.215 |
| 高处正向 | -3729,2245,1800 / 20,0 | 13.328 | 7.793 | 41.5% | 13.370→13.291 |
| 高处反向 | 同高处位置 / 20,180 | 7.231 | 4.696 | 35.1% | 7.165→4.354 |

结果证明本地图这六个视角对渲染比例都有明显 GPU 响应。高处正向 GPU 时间下降而整帧中位数基本不变，说明不能把 GPU 百分比下降直接报告成相同百分比 FPS 提升；本次没有隔离该视角的 CPU、呈现和同步原因。降低到 50% 也没有消除固定 GPU 工作。

正向街道首个完整分辨率 GPU timeline 快照中，Static Opaque Forward 3.728 ms、其中 T2 Opaque MDI 2.714 ms；Translucent Forward 2.228 ms；Depth Normal Prepass Large 1.401 ms、其中 T2 Depth MDI 1.220 ms。它们是单帧快照，父子 scope 重叠，不能相加或当成跨帧均值。下一轮优化仍应围绕实际可见材质和像素工作，当前证据不支持继续扩大视图池或只减少 CPU 提交。

原始帧数据、24 份 GPU timeline、6 份场景 census、6 张引擎截图及其请求记录位于 `multiview/`；可重复分析脚本 `analyze.py`，输出 `comparison.json`、`timeline-summary.json`。

## 视觉检查边界

六张截图均已查看。近景建筑、植被、武器、HUD 和远景均有出图，但高处视角的远景有明显黑色横带，低处朝海方向也能看到较窄横带。本轮不能标记视觉验收通过。高处是人为抬高的观察点，未确认零售游戏在相同可达位置的表现，也未确认此问题由本次 GPU 改造引入。需要同机位隔离远景对象和原版对照；不能以文件哈希、资源计数或构建成功代替视觉验收。

## 移动及转向检查

同一进程、100% render scale 下，临时相机在街道观察点和高处的 6 个路径节点之间插值，40 秒一圈、共 120.009 秒，并持续以每秒 36 度转向。此为相机移动，不是玩家碰撞穿越测试。脚本观察到 7,181 个不同帧，全部有焦点；每秒记录一次轻量状态，共 119 份。观察不保证覆盖每个实际渲染帧。

- 19,303 个 GPU 实例及固定缓冲总预算 159,822,680 字节保持不变。
- 8 个视图槽，高水位始终为 3，`ConservativeRecordings=0`。此次没有刻意制造超过槽数的多相机压力，不能据此验收溢出路径。
- 开始及最后一次观察的 GPU recordings 为 76,980→111,084，MDI calls 为 153,960→222,168；最后一次观察位于 119.212 秒。
- 居留 collider 始终为 11,528，没有因相机移动减少；`ResidentCollisionsReady=false` 一直未改变。
- 进程 private bytes 在这两次观察间增加约 12.47 MiB，托管堆增加约 2.60 MiB，Gen2 collection count 均为 18。只证明短期观察，不证明全进程无泄漏。
- 后续内存快照：显存使用 3,647.7 MiB / 预算 7,188 MiB，texture pool 2,870.7 MiB / limit 6,164 MiB，pending streaming requests 0。快照不等于历史峰值。
- 从地图加载完成日志 cursor 116 起至检测 cursor 125，没有 Error 级输出；本轮存在碰撞等 Warn，不能称日志无诊断。

证据：`route/observations.json`、`route/statistics.json`、`memory-final.json`、`runtime-errors.txt`。多视角及移动脚本均成功结束，退出码为 0。

## 碰撞缺口归因

通过场景实际实例和 collision GameObject 对应关系检查，而不是只读取计数：总 19,455 实例中 11,725 被标记为 collidable，实际存活 collider 11,528、filtered 0、缺口 197。具体为：

| MDL | 缺口实例 |
|---|---:|
| `models/industrial/hanging_plant_mister.mdl` | 116 |
| `models/industrial/hanging_plant_mister_cap.mdl` | 72 |
| `models/water/water_puddle_decal_01.mdl` | 5 |
| `models/levels_terrain/mp_angel_city/ac_hardpointb_decals.mdl` | 1 |
| 同目录 `ac_hardpointb_decals_01.mdl` | 1 |
| 同目录 `ac_hardpointb_decals_02.mdl` | 1 |
| 同目录 `ac_hardpointb_decals_03.mdl` | 1 |

七个 MDL 均从当前 mount 源成功读取并解析，`Contents=1`，没有 embedded VPHY；它们有非零的 PerTriAabb leaf/vertex 数据，不能把“无 VPHY”当成原始资源完全无碰撞数据的证明。当前挂载 `LoadModelVariant(collisionOnly:true)` 对七个模型均返回 null。

两种喷雾器的唯一材质名都是 `models\industrial\hanging_plant_misters`，真实过滤函数 `IsVisualEffectName` 返回 true。`CollisionFilter.cs` 的宽泛 substring `mist` 命中了 `misters`，`BuildCollisionOnly` 因此不保留该网格；模型又没有 VPHY/hitbox，最终 `AddCollision` 返回零 hull，`BuildCollisionOnly` 返回 null。这是 188 个实例的已复现过滤链路，不能按真实 I/O 失败处理。

其余 9 个实例属于水洼/贴花模型，但本轮没有完成原版查询语义对照，不能仅根据命名把它们删除或视为已过滤。源码还存在状态问题：`BuildCollisionOnly` 把确定的零 hull 和读取/解析失败都表示为 null，`LoadModelWithRetryCache` 将它们放入失败重试，streamer 不能结束 pending 状态。此次诊断时 4 个 unavailable、193 个 unresolved；这两个分类依赖加载顺序，合计 197 才是稳定缺口。未修改计数以绕过 Ready。

后续修复应分开返回“成功且有碰撞”“已知被过滤/无可用碰撞”“读取/解析失败”；收窄特效名过滤并补充 `misters` 回归覆盖，再实际重载验证。MDL PerTriAabb 的格式与原版查询语义仍需独立核对，不能用渲染网格回退宣称原始碰撞完全等价。

证据：`collision-audit.json`（逐实例清单）、`collision-models.json`（真实源字节解析、材质分类、物理变体结果）。

## 远景同机位隔离

相机固定在 `(-3729,2245,1800)`、角度 `(20,180,0)`、render scale 1，执行 A→B→A：仅改变 `Titanfall2Skybox3D` 中两个 `SceneModel.RenderingEnabled`，未改变主世界 GPU 场景和相机。

- A：远景开启，黑色横带存在。
- B：两个远景模型关闭，黑带随远景消失，主世界码头、吊机和水面仍然可见。
- A2：恢复两个模型，黑带再次出现。

该结果把问题定位到挂载的 3D skybox 绘制内容/路径，不支持将黑带归因于主世界静态道具 GPU cull。两个模型为 `models/vistas/angel_city_se.mdl` 与 `angel_city_scrolling_uv.mdl`；尚未按单个材质和模型进一步隔离，也没有确定 alpha、投影、几何或纹理中哪一个环节有问题。不能声称已修复或已证明此次 refactor 引入回归。所有远景 rendering flags 在 finally 中恢复。

证据：`vista/captures.json`、`vista/models.json`、`vista/a-vista-visible.png`、`vista/b-vista-hidden.png`、`vista/a2-vista-visible.png`。截图之间存在动态水面、曝光和截图通知变化，因此用作内容隔离，未做全帧逐像素相等断言。

## 收尾与结论

固定视角分辨率响应和 120 秒移动观察完成，但完整视觉和碰撞验收未通过；本轮不能更新为“整体验收通过”，也没有新增“代码已经提速”的结论。

临时相机已销毁，原相机及玩家相机/输入/转向控制恢复，render scale 1、FPS 设置恢复 500，GPU profiler 恢复 false。现场 VSync true 保留，远景两个模型恢复显示。最终实例 PID 7092 保留运行。恢复证据 `restored-state.json`；最终产物复核及设置复核见 `verification.json`。

最终 31 个产物再次复核，零哈希差异；运行前后采集的关键画质设置无差异。`video-before-final-check.json` 是分辨率和移动测试完成后保存的配置文件，最终复核其各键 Value 无变化；不将它描述为整个测试开始前的完整配置备份。文档 `git diff --check` 通过。本轮未改动实现，无新增构建/单元测试结果；此前构建和聚焦测试结果仍属于各自原有的验证阶段。
