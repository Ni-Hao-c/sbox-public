# CM 世界碰撞纠正与离线地图矩阵（2026-10-29）

本轮继续游戏侧 P0 的权威世界碰撞工作。修复仅进入工作区构建，未覆盖用户指定保留的 Steam 测试实例。
`SBOX_TITANFALL2_ORIGINAL_WORLD_COLLISION` 默认仍为 false；本轮验证不等于真实角色移动或整张 SP 场景验收。

## 先纠正旧结论

旧 `COLLISION_AUDIT.md` 称 `0x00EB0280 & 0x0201400B` 为零，这是算术错误。
实际结果是 **`0x00010000`**，普通展开后的 solid 会被现有玩家查询掩码选中。
不能以旧结论为依据添加一层猜测的“CM mask 转换”。相关台账与需求状态已同步纠正。

原版 `engine.dll` 的静态道具初始化器仍明确执行：

1. 在实例 solid 类型非零的分支中读取 MDL contents；contents 为零或含 bit 0 时，清 bit 0 并 OR `0x00EB0280`。
2. window 清对应位并 OR `0x00E30240`，grate 清对应位并 OR `0x00A30240`。
3. 最后 AND 实例 `collisionFlagsRemove` 的反码。

这是 MDL / 静态 prop 的展开与删位规则，不能据此声称世界 CM 表中的值还需要重复展开。
原版 CM 查询中的 `0x180129db0..0x180129dbf` 读取 unique contents，并与当前查询掩码执行 test。
从原版位域到 s&box 的 Surface / collider tag / 玩法查询隔离仍是待做工作；本轮没有把原始位直接套入 Source 2 的碰撞配置。

## 已修复的读取缺陷

### 1. Primitive 的位宽

原版 primitive / geoset 引用实际拆分为：

| 位 | 含义 |
| --- | --- |
| 0–7 | unique contents 索引 |
| 8–28 | 21 位 primitive 索引，或 geoset 子数组起点 |
| 29–31 | 3 位类型：0 brush，2 tricoll，3 static prop |

旧代码按 8 / 16 / 8 拆分，索引达到 65536 后会截断，索引高位还可能被误认为类型。
现在使用 `0x1FFFFF` 与 `raw >> 29`，并同步修复 fog volume 的引用解码。
静态 prop 交给独立的 prop streamer / VPHY 导入，不重复构建进世界模型；未知类型会明确拒绝。

证据：本机原版 `engine.dll` 的 `0x18014c106..0x18014c122`、`0x18014c142..0x18014c175`，包含类型测试 `0xE0000000` 和索引掩码 `0x1FFFFF`。
公开 [bsp_tool Titanfall 2 格式表](https://raw.githubusercontent.com/snake-biscuits/bsp_tool/master/bsp_tool/branches/respawn/titanfall2.py) 可辅助定位 lump 与类型，但其中 8 / 16 / 8 描述与本机零售版代码不一致，本次以原版读取逻辑为准。

这项修复通过了 65536 索引与子数组起点的构造用例；不据此宣称所有原版地图都曾触发截断。

### 2. World 与独立 brush model 的归属

旧 `EnumerateWorldPrimitives` 遍历了全部 geoset，会将门、电梯、触发区域等 inline model 的局部碰撞并入世界。

原版 `0x18014c045..0x18014c096` 给出范围规则：

- model 0 使用 geoset 前缀 `[0, model 1 的 first_geo_set)`；只有一个 model 时使用全部 geoset。
- model N>0 使用 `CM_GRID_CELLS[grid_width * grid_height + N]` 中的 first / count。
- cell 总数应等于 `grid_width * grid_height + model_count`，加载器 `0x180127494..0x1801274b7` 验证这一关系。

不能只取 XY cell 的并集。model 0 还包含全局 cell 和部分未被 XY cell 引用的前缀 geoset；一并删除会丢失世界碰撞。
现在遵照原版前缀范围，排除 N>0，保留这些世界数据。Models 和 GridCells 也加入 sidecar 读取清单。

渲染读取器原先已限定 `models[0]`。本次修正使世界碰撞也遵守这一边界，为后续独立 brush 资源及游戏拥有的移动实体提供基础；尚未实现独立 brush 的公开加载路径。

### 3. 引用去重与损坏数据

- 使用完整 raw primitive 引用去重，保留同一几何、不同 contents 的引用；避免先遇到 water-only 记录后，把后来的玩家阻挡记录错误去掉。
- geoset 子数组越界、contents 索引越界、未知类型、grid/model 不匹配时拒绝导入，不截断数组后继续猜测。
- brush 的附加平面缺失或无效时拒绝该原语，不悄悄将其放大成 AABB。
- tricoll 用 long 计算顶点范围，防止整数溢出；顶点非有限值、三角形索引越界时回滚已写入的几何，避免留下半个碰撞面。
- 新增 `RejectedPrimitiveCount`。MapLoader 不把含拒绝原语的结果作为完整权威碰撞；回退时明确输出 unavailable or incomplete。
- fog 凸包引用解码也受位宽影响，因此默认 BSP 解析缓存标识改为 `bsp-default-cm-index21-fog-v3`。

## 原版数据验证结果

本机安装中的 **23 张 MP + 13 张 SP，共 36 张地图全部通过离线检查**：

- 读取原始 BSP 及 `.bsp_lump[.client]` sidecar，不写原游戏目录。
- world 的原始唯一引用数量与生产读取器统计一致。
- 输出碰撞非空，索引均有效、顶点均为有限值，**拒绝原语为 0**。
- 共排除 **16,543 个仅属于 inline model 的 brush/tricoll 原始引用**，其中包含非玩家阻挡 / 触发区域；不是“16,543 个实际挡路物”。
- 合计生成 26,741,856 个世界碰撞三角形，这是跨地图总量，未同时加载进一个物理场景。

| 地图 | world 原始引用 | 排除的 inline 引用 | 输出三角形 | 拒绝 |
| --- | ---: | ---: | ---: | ---: |
| mp_angel_city | 15,327 | 48 | 827,100 | 0 |
| mp_wargames | 7,865 | 3 | 353,745 | 0 |
| sp_beacon | 10,196 | 849 | 1,345,570 | 0 |
| sp_tday | 16,770 | 341 | 1,279,146 | 0 |
| sp_s2s | 940 | 8,555 | 181,986 | 0 |
| sp_training | 3,647 | 58 | 729,481 | 0 |

完整矩阵和输入 BSP / sidecar SHA256：工作区 `.tmp/tf2-collision-audit/world-ownership-all.json`。
原生只读取证输出：`.tmp/tf2-collision-audit/contents-native/`，原版 engine.dll SHA256 为
`58EB1A1B44B30275BDD21368DE264D856BC310D37D93A0F76F111F0026913487`。

## 自动检查与复跑

新增 `Titanfall2CollisionReaderTests` 的 20 项检查，加上此前 `Titanfall2MountTests` 的 16 项，最终 **36 / 36**：
世界 / global cell / inline 范围、单 model、21 位索引与数组起点、fog 引用、重复和不同 contents、water / solid、已知 prop 跳过、损坏引用、平面缺失、溢出、NaN、三角形回滚，以及既有资源和生命周期检查。

```powershell
dotnet run --project engine/Tools/SboxBuild/SboxBuild.csproj -- test --no-build --filter 'FullyQualifiedName~Titanfall2CollisionReaderTests|FullyQualifiedName~Titanfall2MountTests'
```

无编辑器离线工具在 `Perf/CollisionVerification/Offline/`，直接编译生产 BSP / VPK 读取源码，不依赖 s&box 启动。
先 restore / build，再运行（路径按本机安装调整）：

```powershell
dotnet restore engine/Mounting/Sandbox.Mounting.Titanfall2/Perf/CollisionVerification/Offline/CollisionAudit.csproj --configfile engine/Mounting/Sandbox.Mounting.Titanfall2/Build/NuGet.Config
dotnet run --project engine/Mounting/Sandbox.Mounting.Titanfall2/Perf/CollisionVerification/Offline/CollisionAudit.csproj -c Release --no-restore -- 'D:\Game\steams\steamapps\common\Titanfall2' '.tmp\collision-audit.json' mp_angel_city mp_wargames sp_training sp_crashsite
```

省略末尾地图名时默认检查天使城、战争游戏、Beacon 和 `sp_tday`。本轮全矩阵显式传入安装中的全部 MP / SP 名称，不包括没有对应可玩地图的 common 资源库。

## 仍需完成的验收

源码 SDK、Steam SDK Release 均编译通过，0 warning / 0 error。但尚未将本轮代码部署进用户保留的 Steam 实例，也没有进行新的原生物理世界地图 trace。

下一步在可测试的新进程里检查世界地面、斜坡 / 台阶、隐形 clip、水体、模型活动区域以及两个相距较远的玩家；随后接入查询分类与 surface/tag，并实现独立 brush 模型。
离线 BSP 可解析、数据范围正确，不足以证明角色不会穿地、碰撞面朝向 / skin 完全一致，或已经达到全部 SP 视觉验收。
