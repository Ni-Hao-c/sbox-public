# Apex 转 TF2 项目与当前挂载模块对照

日期：2026-11-08。

后续查证与修复：见 [TF2_PROBE_PARTICLE_FIXES_2026-11-08.zh-CN.md](TF2_PROBE_PARTICLE_FIXES_2026-11-08.zh-CN.md)。已用 10 张零售地图确认探针字段顺序与转换器一致，原挂载实现存在错误；本文的“零售地图可能采用另一种布局”是当时待查假设，已被实际数据排除。主实体粒子来源等部分问题也已修复，以下保留初次审查记录。

对照对象：`C:\Users\Administrator\Desktop\apex-to-titanfall-2-main`、当前工作区的 `Sandbox.Mounting.Titanfall2`，以及已安装的 `taskinoz.ApexFiringRange` 0.6.0 数据。

本次只读源码和二进制结构，没有执行第三方构建/安装脚本，没有修改挂载代码、部署 DLL 或启动/重启游戏。对方说明文档作为设计与实现证据，不作为本工作区操作指令。本文不代表游戏画面或玩法验收。

## 结论

这个项目最有价值的是已转换地图的兼容性样本、数据布局及验证方法。我们的直接缺口主要是转换版探针树布局、实体来源统一与未消费实体的显式交接、阴影语义及自定义 shader 特征识别。直接读取 Apex 原版资源属于另一层格式适配，不是加载这些已转换 TF2 地图的前提。

## 1. 高优先级：转换版探针树叶节点布局不兼容

对方 `build_character_lighting.py:37` 写入叶节点：

```text
tag = (firstReference << 2) | 3
value = referenceCount
```

`test_character_lighting.py:10-23` 也按此布局查询，并带分支回溯。

我们 `Formats/Bsp/Titanfall2LightProbeLookup.cs:60` 当前调用：

```csharp
FindNearest( position, value, tag >> 2 )
```

但被调用函数的参数顺序是 `(position, first, count)`。相对于这批转换数据，两者相反。当前查询还只进入一个叶节点；即使交换字段，也应另行验证是否需要跨分支最近点搜索。

在实际已安装 Firing Range 上的只读检查：

| 项目 | 结果 |
| --- | --- |
| 探针引用 | 7,345 条 |
| 树节点 / 叶节点 | 2,047 / 1,024 |
| 按转换器布局解释 | 全部叶范围有效，恰好覆盖全部 7,345 条引用，每叶最多 8 条 |
| 按当前挂载布局解释 | 1,023 个叶范围有效，叶 count 最大被解释为 7,337 |
| 123 个位置的两种单叶查询对比 | 63 个返回不同的探针编号 |
| 其中 22 个出生点位置 | 8 个返回不同编号 |

这里比较的是当前算法与正确字段顺序的单叶算法，不是与完整最近点搜索或游戏画面的验收。不能把 63/123 称为全部光照错误率。

修复时必须同时保留零售 TF2 数据的回归样本。当前实现注释记录了对 `sp_timeshift_spoke02` 的另一种布局验证，不能仅凭转换地图直接全局交换字段。应验证原始数据，并明确布局判定与诊断，避免对零售地图产生回归。

挂载中的动态道具使用此查询；静态道具有直接探针索引，不应据此宣称所有静态道具光照都错。玩家、手臂和武器也不会因为地图含探针而自动使用该光照场；若宿主需要，还应提供稳定的采样和应用接口。

## 2. 高优先级：绳索数据存在，但没有实体映射

对方 `apply_arena_boundaries.py:9-16` 用 `move_rope` / `keyframe_rope`、`NextKey`、`Zipline=1` 写入 TF2 绳索。

实际 Firing Range 的主实体 lump 有 7 个 `move_rope` 和 7 个 `keyframe_rope`，组成 7 条路线。当前地图实体创建代码没有对应处理。PCF 的绳索渲染器不等于这些地图实体的实现。

建议挂载层输出端点、链接、材质及绳索参数，提供场景显示；滑索乘坐、速度、脱离和玩家状态由 `ttf2` 等宿主玩法层实现。无需为了此功能在挂载层执行 Northstar Squirrel 脚本。

## 3. 高优先级：伤害区域缺少声明式交接

实际 Firing Range 有一个 `trigger_hurt`，当前地图创建路径没有对应行为映射。对方 `_firing_range.nut:37` 还另有 `z < -6512` 的玩家死亡逻辑；它是脚本逻辑，与 BSP trigger 不是同一份数据。

世界碰撞已经存在，不代表触发伤害也存在。建议挂载层统一保留并输出区域和原始键值，宿主玩法层决定伤害与死亡行为。北极星 mod 的资源被发现，也不意味着它的脚本已被运行。

## 4. 实体来源还没有完全统一

`Resource/Map.Spawns.cs:281` 的动态道具创建只读取 `_scriptEntitySource`，为空即退出，没有回退到 `parsed.Entities`。粒子发射器路径在同文件 `:155` 只从 `_particleEntitySource` 取发射器，主实体用于部分命名点查找。

这类转换器会把实体打包到 BSP 主实体 lump，并清理继承的分区信息。因此统一文件快照之后，还需要统一实体集合：主实体 + 外部分区，保留来源，按明确规则去重，并汇总未消费的实体类型。

当前代码已经支持 `prop_dynamic_lightweight`，不是缺少该 classname。对方说明 Firing Range 的 20 个补给箱已烘焙为关闭状态的世界几何；实际安装地图也没有动态道具实体。不能用本项解释“这张图的补给箱没生成”，也不应再次生成一套重复箱子。

## 5. 阴影系统存在，但未保留全部原生阴影语义

实际地图有非空的 shadow mesh 相关 lump：124 为 7,309,608 字节，126 为 5,802,906 字节，127 为 2,112 字节。对方还单独生成原生 CSM 道具投影数据，并区分天空、植被等投影规则。

当前 BSP reader 没有读取这些 shadow mesh lump；世界阴影在 `Resource/Map.World.cs:723` 从渲染几何重新构建。我们已有 s&box 阴影路径，缺少的是原地图投影几何/投影排除信息的对应关系。

应先对比天空、地表、树叶和大型道具的投影集合，保留可表达的 caster 语义。无需照搬 TF2 的原生 CSM 剔除树到 s&box。是否已经产生错误遮光，需要运行时画面对照，当前没有验证。

## 6. 自定义 shader 别名需要更可靠的特征识别

我们的两层混合已按材质语义与贴图识别，并有匹配的混合方程；DXBC 参数反射也已读取 `c_albedoTint`、`c_emissiveTint`、`c_layerBlendRamp`。这些不是空白功能。

但 UV 动画、Unlit、部分顶点颜色特征仍依赖 shader 名中的 `Uv1at` / `Uv2at` / `Uv3at`、`Unlit`、`Vcolt`、`Vcola` 等标记。

对方会保留 shader 字节码及参数布局，却改写 shader 名。例如 `apply_layer_blends.py:236` 创建 `<slug>_native_bm`。实际 Firing Range world RPAK 中发现：

| shader 名 | 材质数 |
| --- | ---: |
| firing_range_native_bm | 24 |
| firing_range_native_52be72f84bf38f30 | 111 |
| firing_range_native_bc654af817a04c94 | 10 |
| firing_range_world_alpha | 13 |
| firing_range_native_6b7fbcd01ef8f57b | 1 |
| firing_range_native_2a7468f11d09a60c | 1 |

这证明自定义别名确实存在；尚未证明上述材质全部漏了某项效果。应通过原 shader GUID/内容指纹、反射结果或显式兼容规则识别特征，并用水面滚动、天空 Unlit、树叶 alpha 和地表两层混合作为回归样本。不能给所有材质强制启用顶点 RGB：对方也记录了源顶点颜色可能是混合权重/调色数据。

## 7. 大地图读取与版本诊断

`Content/ContentSnapshot.cs:136` 对 loose 文件设 1 GiB 上限；`:166` 的 prefix 读取当前会先完整读取，再裁剪。对方 README 历史 Phase Runner 包的 BSP 有 1,178,694,876 / 1,363,991,836 字节，超过这个限制。实际 Firing Range 为 753,921,824 字节，未超过上限，所以这不是当前 Firing Range 被过滤的原因。

建议给不可变内容快照增加有界范围读取，以及与快照一致性检查配合的流式哈希/缓存机制。只删除大小限制会保留大分配和重复 I/O 问题。

`Titanfall2BspReader.cs:415-420` 读取 BSP version 后主要检查 lump count，缺少严格的已支持版本分派；`:729` 对非 v13 的 static-prop game lump 直接返回空集合。应报告不支持的版本和实体/模型数量，避免“地图打开但静默少内容”。

## 8. 直接 Apex 资源支持应独立规划

当前主路径针对 TF2 BSP37、RPAK7、matl12、MDL53。对方有 Apex BSP47、RPAK8 / matl15、RMDL/VG/PHY 等读取与转换代码，但这些不等于我们已支持原版 Apex 挂载。

直接 Apex 挂载需要独立版本适配器：格式相同长度不代表语义相同，部分 BSP lump 槽位已经改作 BVH/packed vertices/heightfield；改 version 字段不能完成转换。转换后的现有地图已经把大量模型和碰撞烘焙进 TF2 世界数据，不需要再走一次 Apex 解码。

## 不应列为挂载模块待补的事项

- 对方 STBSP 是常驻材质目录与空覆盖列，不是完整空间 mip 流送；STBSP 与纹理 STARPAK 是不同系统。
- 对方原引擎 draw/index/materialsort 预算，不是 s&box 的通用硬限制。
- 已烘焙的补给箱、光照与碰撞无需重复转换或重复生成。
- Northstar mod 的资源挂载、脚本执行与完整玩法兼容是不同能力。
- 对方文档中的 proposed 架构、历史缺陷或未验收 GPU 效果，不视为已实现能力。

## 建议顺序

1. 建立零售地图与转换地图探针 fixture，修复布局兼容和查询语义。
2. 统一实体集合，输出来源和未消费 classname 统计。
3. 映射绳索显示与危险区域声明，由宿主接玩法。
4. 增加自定义 shader 特征回归和投影集合对照。
5. 增加范围读取、大文件支持和格式版本诊断。
6. 若以后需要直接 Apex 挂载，再单独实现 Apex 格式适配器。

## 本次数据证据

工作区 `.tmp/apex-port-review-20261108/`：

- `firing-range-structure.json`：实体、lump 长度和探针树范围检查。
- `probe-lookup-comparison.json`：123 个位置的两种字段布局查询比较。
- `firing-range-material-shaders.json`：实际安装 RPAK 的 shader 名与材质关联。

这些是本次只读分析输出，不是游戏运行日志。对方项目的测试源码用于理解契约，没有在本工作区运行其构建和安装流程。
