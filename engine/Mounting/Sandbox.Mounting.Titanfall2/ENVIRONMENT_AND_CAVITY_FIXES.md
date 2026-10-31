# 环境区域、cavity 反射与法线预通道修复

2026-10-29。接续 [HDR_DISPLAY_AND_BLOOM.md](HDR_DISPLAY_AND_BLOOM.md)。本轮处理三个已证实的导入缺陷；完整原版视觉还原尚未完成。

## 1. 天空区域强白光被应用到战争游戏主场景

旧代码按整个 BSP 包围盒的中心，寻找最近的 `light_environment` 图标。包围盒还包含放在地下的天空区域。战争游戏因此选中了 index 0 的天空白光，运行颜色为 `(3,3,3)`，主场景本应使用 index 1 的暗蓝环境。

原始环境实体和 WORLD_LIGHTS 的对应关系：

| 环境 | 体积 / 主区域 | 编译太阳 RGB | 编译天空 RGB |
| --- | --- | --- | --- |
| War Games index 0 | 地下天空体积，中心 `(-6184,-3624,-6519)` | `(3,3,3)` | `(0.2,0.2,0.2)` |
| War Games index 1 | 体积外主场景 | `(0.05311749,0.08345605,0.09803922)` | `(0.03921569,0.03921569,0.03921569)` |
| Angel City index 0 | 主场景 | `(4,3.5650485,2.8618617)` | `(0.7154654,0.8512517,1)` |
| Angel City index 1 | 地下天空体积 | `(3,3,3)` | `(0.2,0.2,0.2)` |

固定曝光时，关闭太阳会去掉飞机和地面的大块白色高光；关闭环境反射探针则仍保留主要高光。原版飞机着色器也确认光泽基本换算为 `1-gloss`，不是之前怀疑的指数编码。主要故障首先是环境选择，不能靠压低所有材质或继续降曝光修补。

### 实现

- `Formats/Titanfall2LightEnvironment.cs`：读取 `lightEnvironmentIndex`，按 `light_environment_volume` 的局部包围盒与凸平面选择区域。多 brush 取并集，支持旋转，拒绝无效数值。没有体积覆盖的位置优先使用不属于局部体积的环境，不再按 BSP 中心到灯图标的距离选择。
- `Resource/EnvironmentController.cs`：保留可序列化的实体 / HDR 状态，主相机进出体积时更新同一盏太阳及天空色。实现挂载程序集所需的显式 `IUpdateSubscriber`；生命周期测试曾捕获遗漏该标记导致不更新的问题，最终版本已修复。
- `Map.Environment.cs` / `Map.World.cs`：初始太阳、地形天空光、静态道具天空光统一使用正确默认环境。
- `titanfall2_probe_inputs.hlsl` / `titanfall2_probe_lighting.hlsl` / `titanfall2_world_lightmapped.shader`：当前环境天空色通过场景 RenderAttributes 传递，覆盖普通对象、批量实例和地形烘焙天空项。没有控制器时保留原有材质 / 道具数据，独立测试场景不受影响。

体积提前解析，运行时 Contains 查询不分配内存。没有按环境数量创建额外 native 灯。

**范围：** 已验证战争游戏、天使城源数据，以及单环境 Beacon。当前依照活动 Scene.Camera 更新；多相机同时渲染、独立天空世界、复杂重叠体积的原版优先级及脚本淡入淡出还需单独接入。多个未绑定环境或没有未绑定环境时采用确定性回退，不能宣称全部等价于原版原生选择器。

## 2. cavity 与 AO 被合并，凹槽反射缺少抑制

原版 `goblin_dropship_1_big` 的 shader 为 `uberAoCavEmitEntcolmeSamp2222222_fix`。D3D 反汇编中 t11 是 `aoTexture`，t12 是 `cavityTexture`；最终直接光与环境镜面反射之和乘 cavity 红通道，随后与漫反射和自发光合成。

旧描述符把两张图都标为 AmbientOcclusion，运行时每个槽只保留第一张贴图，因此 cavity 没有进入着色。

### 实现

- 在语义枚举末尾添加独立 Cavity，保留既有枚举数值。识别反射资源名与 `_cav` / `_cavity` 后缀，绑定 `g_tCavity`，保留它自己的 UV 选择。
- 缺少该图时使用中性因子 1。仅当成功绑定时启用采样。
- 普通材质和地形材质均对镜面反射应用 cavity，不乘到漫反射、自发光或不透明度上。
- 世界聚合材质复制同时保留新贴图及参数，避免单体模型有效、聚合地形失效。

`VerifyCavity.csx` 在真实 GPU 中检查白 / 黑 / 半强度、缺失贴图、独立漫反射、自发光及环境立方体反射；并检查真实飞机有两张不同且有效的 AO / cavity 纹理。普通与地形路径均覆盖。

## 3. 深度 / 法线预通道写入了颜色与地图雾

实际 native `NormalsGBuffer` 回读确认：普通材质开启强雾后，原本约 `(0,0.5,0.5)` 的编码法线变为雾的红色；无光照材质会输出自发光颜色。地形、vista 和不透明双纹理 VMT 也声明了 `Depth()`，但没有输出深度模式需要的法线 / 粗糙度。贴花在标准着色器已经返回法线后，又叠加了一次导入雾。这会向 AO、SSR 等消费该缓冲的效果提供错误输入。

修复在颜色合成前返回 `DepthNormals::Output`：

- `titanfall2_surface.hlsl` 的公共入口在无光照、探针选择和雾之前处理深度模式；保留 native decals 与 alpha-to-coverage 的准备步骤。
- 地形只读取该通道所需的表面法线 / gloss，跳过烘焙光照、直接光、反射与雾的颜色合成。正常 Forward 路径保持原逻辑。
- 不透明 vista、裁剪 vista、双纹理屏幕与 cubemap VMT 输出几何法线；裁剪在深度输出之前执行。
- mesh decal 在标准着色器返回法线后直接返回，保留其裁剪与 native decals 处理。

`Perf/MaterialVerification/VerifyDepthNormals.csx` 使用一个独立场景，直接读取 native 法线缓冲，覆盖全部八种 `Depth()` 材质。43/43：强雾开关、无光照开关、旋转后的世界法线、两种粗糙度以及草叶 / 贴花透明裁剪均通过。粗糙度按 opaque alpha 检查；alpha-test 路径的 alpha-to-one 按引擎约定检查为 1。验证 shader 为 `shaders/tests/titanfall2_gbuffer_read.shader`，处理 MSAA、viewport offset，并关闭全屏读取的深度测试。正式编译后重启编辑器，避免把常驻旧 shader 缓存作为结果。

### 最终预通道版本的回归与实图

16 条受影响 shader 正式编译成功，预通道修复阶段完成以下检查，合计 276/276：

| 检查 | 结果 |
| --- | --- |
| native 法线、粗糙度、雾隔离、裁剪 | 43/43 |
| 环境天空普通 / 批量实例、地形、cavity、太阳可见性、局部灯 | 140/140 |
| Legacy grid / 屏幕合成 | 31/31 |
| 地面层混合 | 5/5 |
| 草片 / 贴花表面覆盖 | 10/10 |
| 水体合成 | 3/3 |
| Bloom / HDR 显示 | 44/44 |

这些是材质与传输回归，不能据此宣布屏幕、草、水岸等所有地图实效已还原。

最终版本还重新运行了 `sp_tday` 的固定机位留图和战争游戏的同机位环境对照。战争游戏主区域 → 地下天空 → 主区域的实际索引仍为 1 → 0 → 1，SP 环境仍为 0。保留 `.tmp/tf2-mcp/depth-final-*.json`、`depth-regression-summary.json`、`depth-extra-summary.json`，源 shader SHA256 见 `depth-final-source-hashes.json`。DLL SHA256 / MVID 仍为下节记录的版本；本项为 shader 修复。

`sp_tday` 当前一次 2 秒窗口采样：1083 draw calls、2,186,897 triangles、GPU 平均 30.22 ms，不透明 Forward 17.70 ms、Small Depth Normal Prepass 11.99 ms、Large 0.15 ms、Display 0.17 ms、LUT 0.06 ms；显存约 3386.6 / 7188 MiB。源报告 `depth-final-sp-perf.json`。两次采样没有控制到相同编辑器视图及运行条件，因此不把旧 49.91 ms 到当前 30.22 ms 当作本修复的性能收益。当前主要开销仍在几何 / 不透明绘制，不能承诺修复法线后 GPU 占用率会明显下降。

## 环境与 cavity 验证及证据

目录：`C:/Users/Administrator/.codex/visualizations/2026/10/24/01a16c2c-16d3-7f51-b93e-2cde2bd4796a/tf2-specular-audit/`

- `environment-selection.json`：四张真实地图的环境 WORLD_LIGHTS、worldspawn 与旧运行灯光。
- `goblin_dropship_1_big-descriptor.json`、相应 `.ps.dxbc.asm`：原始贴图槽和像素程序。
- `wargames-baseline.png`、`wargames-no-sun.png`、`wargames-no-probes.png`：光源隔离。
- `wargames-old-selection.png`、`wargames-correct-fixed-exposure.png`、`wargames-correct-auto-exposure.png`：同机位、固定曝光的旧 / 新环境，以及恢复正常曝光后的效果。修复后的夜景是源灯光数据的结果，没有额外全局压暗。

实际相机往返 `(900,2700,450)` → `(-6184,-3624,-6519)` → 主场景，运行环境索引依次为 1 → 0 → 1；太阳与天空 HDR 值随之恢复。

最终部署 SHA256：`53B42C38141BF9C8E2C73D00B8009137F0C68561979D13CA786880F5B4540AC0`。

最终运行程序集 MVID：`015f9638-0edc-47db-a2ac-c6b6a5b9b1c8`，验证进程 PID 25172。

| 针对性检查 | 最终版本结果 |
| --- | --- |
| `VerifyLightEnvironments.csx`，直接读取当前安装的真实原版实体 | 17/17 |
| `VerifyEnvironmentSkyProbes.csx`，普通 / 批量、两种材质、七个法线方向、开启 / 关闭覆盖 | 84/84 |
| `VerifyEnvironmentSkyWorld.csx`，地形天空色与回退 | 4/4 |
| `VerifyCavity.csx`，直接 / 环境镜面、漫反射、自发光、真实绑定 | 14/14 |
| `VerifyWorldSunLightmap.csx` | 8/8 |
| `VerifyCompiledLocalLights.csx` | 30/30 |
| `VerifyRetailDisplay.csx` | 32/32 |
| `VerifyRetailBloom.csx` | 12/12 |
| `VerifyColorCorrection.csx` | 48/48 |

合计 249/249。战争游戏另做“停止 → 关闭地图 → 重开 → Play”的真实重载，控制器仍能在 1 → 0 → 1 间切换；`VerifyGeneratedWorldResources.csx` 的六项资源检查全部通过，223 个不透明聚合模型、37 个透明块和四种光照纹理仍有效。

切换至 `sp_tday` 后，环境索引保持 0，太阳 `(3.2489662,3.0669746,4)`、天空 `(0.62534475,0.72991896,1)`，没有继承战争游戏夜间状态。`sp_tday-final.png` 为同一固定机位复核；地面仍有明显高光，不能由此宣称 SP 材质与原版完整等价。最终运行控制台没有 Error 级消息。

本轮挂载 Release 构建 0 警告、0 错误，14 条受影响着色器正式编译成功。针对性验证脚本位于 `Perf/MaterialVerification/`；不以这些检查代替原版同机位完整验收，也没有宣称全仓库 CI 通过。

## 仍待处理的问题台账

| 问题 | 当前状态与下一项工作 |
| --- | --- |
| 飞机 / 地面大块白色高光 | 环境选错已修；cavity 已接入。还需继续核对原版直接光 BRDF、反射滤波与剩余通道。 |
| 暖色薄雾 / 片头雾混入 | 后续已按原生默认规则、根 BSP 的真实 fog volume 凸包及视图位置修复，含场景 / 缓存恢复与 3D skybox 联动。22 / 22 针对性检查、战争游戏 4 / 4 区域往返通过；剧情 / 网络渐变仍待做。见 [FOG_VOLUME_FIXES.md](FOG_VOLUME_FIXES.md)。 |
| HDR / 曝光 / 发光 | 原版显示曲线、独立曝光、LUT 与保守核心 Bloom 保留。地图 / 剧情控制、wide / streak 分支未完成。 |
| 墙面屏幕与层混合 | 此前已有双纹理调制、UV 和代理链修复；还需当前版本原版同机位复核，不能宣称所有屏幕程序兼容。 |
| 草片 / 远处彩边 | 此前有裁剪、UV、BC 小 mip 修复；当前版本仍需天使城具体草种、近远移动实测。 |
| 地面纹理 / 水岸过渡 | 已有层混合与水体路径，但原版水岸泡沫、特殊地面覆盖及全部 shader 变体仍未验收。 |
| 阴影 | 烘焙可见性、太阳通道与局部灯已接入；局部动态遮挡、原版 emitter shape 和阴影距离行为仍待完善。 |
| 静态模型聚合 / 远景跳变 | 已有不透明静态道具聚合、实例与 PVS 路径；还需按地图检查大型边界模型与 LOD 切换。 |
| 二次加载缓存 | 已有部分处理缓存和场景生成资源保活；启用 BSP lightmaps 的完整磁盘地图缓存仍未完成。 |
| GPU / 显存 | 有实例、聚合和按图资源控制；跨地图生成资源的可重建 LRU 预算仍未完成，关闭场景不能直接当作释放全部缓存。 |
| SP 地图 | Beacon 云朝向与显示管线的局部修复保留；剧情实体状态、复杂粒子、雾 / 天空和室内外过渡仍需逐图验收。 |

这些是后续修复范围，而非已经解决的承诺。当前不能把“所有问题”标记为完成。

## 后续执行顺序与验收

1. **Steam 游戏接入与 SP 状态。** 静态 fog 默认绑定与触发区域已修复，参见 [FOG_VOLUME_FIXES.md](FOG_VOLUME_FIXES.md)。当前先按 [MOUNT_REQUIREMENTS_STATUS.md](MOUNT_REQUIREMENTS_STATUS.md) 完成 Steam、纯世界与碰撞契约；随后验证游戏侧剧情 / 网络控制以及 SP 室内 → 门口 → 室外移动序列。
2. **剩余高光与地面 / 水岸。** 对同一片实际材质做原始贴图、UV、gloss / specular、环境探针和灯光隔离，再核对原始像素程序。地面层混合及水体合成的受控检查通过，只说明基础路径有效，仍需定位每张图具体失效的材质变体。
3. **几何与资源预算。** 当前主要 GPU 耗时指向 Forward 和预通道。固定相机、分辨率、活动视图数量和预热时间，按 draw calls / triangles / pass ms 比较空间聚合、PVS 和 LOD；保留草叶裁剪及大型远景轮廓。跨图 LRU 必须同时可重建 SceneFile、Model、Material 和 lightmap，逐项裸释放会重现重载缺地形。

每一项均需新进程原版同机位图、近远移动与重载复核；不以单张截图或一组像素测试替代逐图验收。
