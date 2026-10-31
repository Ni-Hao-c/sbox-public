# 虎大：光片、环境雾片和灰尘的修正

日期：2026-10-25。地图：`mp_forwardbase_kodai`。本记录区分原始程序的确切修复与当前渲染管线下的视觉校准。后续地形/模型光照、水体和性能工作仍在进行。

## 现象与隔离结果

用户截图中的黄色大片主要来自 BSP 的 `world/atmosphere/beacon_clouds`，并非全部是粒子。真实地图中该材质有 97 个源 mesh，原先集中在一个透明 aggregate 中。固定机位隐藏这一个材质后，岩壁、树和建筑立即清楚。

另外存在 32 个静态灯光束模型、4 个 BSP 光束分区，以及单独的 Sprite/Rope 粒子。分别隔离这些对象不会消除背景的大面积黄雾，因此不能只调整粒子数量或整体曝光。

固定机位：位置 `(-666.817,-864.4,1512.425)`，俯角 18°，方向 210°，FOV 85°，1370×800。主对照文件为会话证据目录 `tf2-kodai-haze/kodai-before-calibration-210.png` 与 `kodai-yaw-210.png`。两者用于观察雾层遮挡变化；它们不是与用户原版截图完全重合的像素对照。

## 已确认并修复的数据错误

1. **距离衰减字段与方向错误。** 原始 CBufUberStatic 的比例、偏置在 `0x90/0x94`，旧代码读取了 `0xA0/0xA4`，并把最终衰减反用了。实际公式是 `saturate(distance * scale + bias)`，近处变淡，远处恢复。
2. **漏掉观察角度衰减。** 从实际零售 DXBC 确认：`max(0, lerp(inner, outer, pow(1-abs(dot(normal,view)), exponent)))`。`beacon_clouds` 的 outer 为 -0.5，掠视时应完全淡出。只依据文件名中的距离不能还原这一项。
3. **深色雾片混合错误。** `atmosphere_dark_1500_fade` 的原始混合是预乘透明，旧路径因名字中有 atmosphere 而强制采用加法光束。现在保留原始混合方式。
4. **忽略正反面规则。** 四个透明/光束 shader 按 `Metadata.DoubleSided` 选择背面剔除。`sbox_pixel.fxc` 会在末尾覆盖前面的剔除状态，因此必须在其 include 之后显式选择。只有声明一个宏并不足以生效，此情况已由反向三角形 GPU 测试发现。
5. **雾片纹理坐标不独立。** `beacon_clouds` 的颜色/透明度使用滚动坐标，发光图使用原始坐标。新增辅助坐标选择，避免三张贴图一起滚动。普通非分层材质暂时保留原路径。
6. **灰尘寿命/透明度范围反向。** 原始地面尘埃的寿命端点为 6 和 1.5，正确范围是 1.5–6 秒；尘埃微粒的 Alpha 为 180 和 75，正确范围是 75/255–180/255。Alpha Random 和根 Alpha 均按字节值解释，数值 1 表示 1/255。
7. **灰尘尺寸与形状。** Radius Scale 使用实际 `radius_start_scale/radius_end_scale`，地面尘埃为 0.5→1.2。新增 `Position Modify Warp Random`：球形发射区域按 `(1,1,0)`→`(1,1,0.1)` 压扁，再保留最终 `(0,0,30)` 偏移。不会把世界坐标向地图原点缩放。

参数只采用 RDEF 标记为已使用的变量。例如 `beacon_se_f` 存储的距离系数为 10000，但其程序不使用该变量，运行时必须保持中性默认值。

## 当前视觉校准，尚非逐项原版复刻

原版还把雾、曝光与发光合成在专用程序里。目前 s&box 的相应合成仍不一致，单纯补齐衰减后 97 片叠加仍过厚。因此给 RPak 的非加法环境雾片单独采用：

- `SBOX_TITANFALL2_ATMOSPHERE_OPACITY=0.15`，可在 0–1 调整；只影响 `world/atmosphere/` 下的透明雾片。
- 与不透明场景深度相交的 96 单位范围内逐渐变淡，减轻地面、岩壁处的硬切线。
- 灯、玻璃、贴花、水与加法光束不采用这一强度系数。

这是一项有固定机位证据的局部视觉回退，不能描述成已经完全复制原版雾公式。`GODRAYS` 和广域烟雾 PCF 的既有默认开关仍需在跨地图回归后决定是否恢复；测试实例以 `SBOX_TITANFALL2_GODRAYS=1` 检查了保留的光束。

## 验证

- 四个 shader 正式 VS/PS 编译通过。
- `Perf/MaterialVerification/VerifyOpacityFade.csx`：48 项 GPU 检查通过，包括实际线性亮度比例、低字节 Alpha、距离/角度、正反面、局部强度以及 32/64 单位的深度交界淡出。
- `VerifyAtmosphereSource.csx`：5 个实际材质和 2 个实际 PCF 数据检查通过。
- `Perf/ParticleVerification/VerifyAtmosphereParticles.csx`：两种位姿、各 100 个真实工厂发射的粒子，局部 Z 均在 10–50 单位内；平面内分布仍保留。
- 既有 `VerifyParticleGeometry.csx`：23 项真实 PCF/Rope/Trail 回归通过。
- Release 构建通过，最终部署版本与后续光照工作一起记录于 `MOUNT_COMPLETENESS.md`。

证据保存在会话可视化目录的 `tf2-kodai-haze/`；原始 DXBC/常量、材质清单、源几何清单和测试 JSON 同时保留。

## 后续边界

广域蒸汽、天空世界粒子、完整多序列动画、Color Lit Per Particle 与粒子事件仍有缺口。当前雾片仍走透明 aggregate；其透明排序、包围盒和覆盖面积将在正确画面下单独测量。静态模型的 SH 探针、地形烘焙光照、反射与曝光尚未统一，因此雾变浅之后仍会看到整体偏暗，不能再用厚雾遮盖它。
