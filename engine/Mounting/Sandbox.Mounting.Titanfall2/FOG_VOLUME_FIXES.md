# Fog volume 默认绑定修复（2026-10-29）

## 问题与修复

战争游戏的暖灰色遮罩不全是曝光或 Bloom。此前按 BSP 中心挑选雾控制器，会把片头的 `fog_intro_militia` 用到主战场。
原版控制器选择与当前位置的 fog volume 有关，不能按控制器名字猜测或直接删除片头雾。

本次导入根 BSP 的 `fog_volume` inline model `*N`，从 CM grid cell / geoset / brush side 数据恢复六个轴向面及附加切面。
多个凸体按并集处理，保留 volume 的 origin / angles 与 priority；遇到不支持或损坏的原语会拒绝该 volume，不以 AABB 近似来覆盖真实区域。

默认规则为第一份 fog controller；有 master 标记时由后来的 master 覆盖。volume 按 priority 降序、同优先级保持原序，target 对应控制器 targetname。
选中 `fogenable=0` 时明确清除雾，而不是跳过它后错误沿用上一片区域。无法解析的 target 才回退默认。

视图使用实际组合后的 `Camera.View.Position`，并把相同的雾参数传给 `useworldfog` 的 3D skybox。选择器和凸包数据经过场景序列化及缓存元数据保存，重载后继续有效。
当时解析缓存标识更新为 `bsp-default-fog-volumes-v2`。之后 CM 位宽纠正同步覆盖 fog 引用，缓存再升为 `bsp-default-cm-index21-fog-v3`，见 [CM_COLLISION_CORRECTIONS.md](CM_COLLISION_CORRECTIONS.md)。这项缓存验证不代表带 lightmap 的完整地图磁盘缓存已经完成。

## 数据与原生证据

原版 `server.dll` 的默认 fog 管理函数 `0x18014ae30`、volume 优先级插入 `0x18014b950`、点区域查询 `0x18014bbf0` 与实际 BSP 数据相互核对。
源文件读取和静态分析没有修改原游戏。

| 地图 | 导入结果 |
| --- | --- |
| `mp_wargames` | 根 BSP 有 3 个 fog volume；主区域 (900,2700,450) 选择 `env_fog_controller_1`，两个片头区域分别选择 IMC / militia |
| `sp_beacon` | `*19` 的 brush volume 指向 `env_fog_controller_2`；默认采用最后一个 master |
| `sp_tday` | `*12` 的 brush volume 指向 `env_fog_controller_2` |
| `mp_angel_city` | 一份控制器，无需虚构 volume |

主要代码：`Formats/Titanfall2BspFogVolumes.cs`、`Titanfall2FogEnvironment.cs`、`Resource/HeightFog.cs`、`Map.Environment.cs`、`Skybox3D.cs`。

## 已完成验证

此前最终 fog-only DLL：SHA256 `97588572E421B79AF9D1EE2A4DE019A03A0D74491F9BAD9DDF4BCDBC67286988`，MVID `5e5cdd8d-b419-4c89-9f7f-bb3ab75ce245`。
这是本轮兼容编译之前的版本记录，不是当前 Steam 构建的指纹。

- `VerifyFogVolumes.csx`：22 / 22，包括原图凸包、控制器规则、禁用与缺 target、选择过程零分配、缓存元数据回读、组件恢复。
- 实际战争游戏：主区域 → IMC → militia → 主区域，4 / 4，天空与主视图共享正确雾源；最终控制台没有 Error。
- 在同一机位、固定曝光 0.52 下留了旧片头雾与正确 volume 雾的对照图。
- HeightFog 79 / 79、DepthNormals 43 / 43、LightEnvironments 17 / 17 回归通过；这些是在最终格式及缓存组件失效检查的小修之前运行的记录。

证据在工作区 `.tmp/tf2-mcp/fog-final-results.json`、`fog-final-live.json`、`fog-regression-summary.json`。
固定机位图位于本机 visualization 的 `tf2-specular-audit/wargames-old-intro-fog.png` 与 `wargames-correct-volume-fog.png`。

## 仍未完成

原版网络 / 剧情的控制器启停与渐变（包括 fade 时间）、`forceontosky` 完整消费语义、多相机独立 fog buffer、全部 SP 室内外移动验收仍未完成。
挂载负责静态环境资产和区域规则；剧情触发和状态由 ttf2 游戏持有。也不以此修复代替水岸、屏幕、复杂粒子、反射及全套 HDR 原版一致性验收。
