# Titanfall 2 挂载碰撞审计与后续方案

日期：2026-10-27。范围：地图世界碰撞、静态道具、移动/泰坦/武器/钩抓查询。与画面修改独立。

2026-10-29 更新：下文“字段压成 bool”和“下一步加入命中诊断”是历史状态。当前 v6 已保留实例碰撞字段，生产碰撞体已有来源组件，且真实 trace 回归通过；详见 [COLLISION_DIAGNOSTICS_RUNTIME.md](COLLISION_DIAGNOSTICS_RUNTIME.md)。本轮纠正了旧版 contents 算术结论，修复 CM 索引位宽及世界 / inline model 归属，详见 [CM_COLLISION_CORRECTIONS.md](CM_COLLISION_CORRECTIONS.md)。完整玩法查询分类仍未完成。

## 结论与本轮修复

草、小岩石挡路存在已确认的导入错误：`StaticPropInstance.IsCollidable` 原先使用
`SolidType != 0 || CollisionFlagsAdd != 0`。所谓 `CollisionFlagsAdd` 来自 v13 道具记录
偏移 `0x38`，原版初始化逻辑没有把这个字段用于开启碰撞。很多原本 `solid=0` 的装饰物
在这里有非零数据，因此被挂载器错误地创建为实体。

本轮改为只按原版 `solid` 字节判定实例是否具有实体碰撞，并把误命名字段改成
`ReservedCollisionData`。保留其原始 32 位内容，保留偏移 `0x3c` 的完整 32 位
`CollisionFlagsRemove`，不猜测保留字段的含义。不按名称或体积批量删除岩石、树木碰撞。

这是静态道具碰撞资格的修复，并非完整的原版碰撞分层实现。原本 `solid!=0` 的草、
小石头仍需要下一阶段结合模型 contents 和实例 remove 掩码确认，不应笼统全部放行。

## 实际代码链路

```text
GAME_LUMP.sprp v13
  → Titanfall2BspReader.ParseStaticProps
  → StaticPropInstance.IsCollidable
  → StaticPropStreamer.Loading.Encode（目前只保存一个 Collidable 布尔值）
  → StaticPropStreamer.Collision.EnsureCollisionState
  → ModelCollider（随距离激活/销毁）

MDL53
  → ModelLoader.AddCollision
  → 嵌入 VPHY 凸包
  → 缺失时：命中盒
  → 再缺失时：渲染三角形或整个模型的包围盒

世界几何
  → MapLoader.BuildScene
  → 默认：BuildWorldModel 的可见三角形碰撞，按材质排除部分视觉特效
  → 可选：CM_* brush/tricoll 解析（目前与 DeferredNavMesh 开关绑定）
  → ModelCollider
```

`Map.StaticPropAggregation.cs` 中的静态道具渲染聚合不会自动修复物理语义；被聚合的
道具仍有独立的流式碰撞记录。渲染、阴影、碰撞的生命周期需要分别处理。

## 原版二进制核验

使用本机已安装游戏的只读字节与反汇编，未修改游戏文件。

- `engine.dll` 原版 v13 加载循环每次读 `0x40` 字节，调用 RVA `0x1b0570` 初始化道具。
- 初始化器在 RVA `0x1b05da` 读取记录 `+0x1e` 的 solid 字节。
- RVA `0x1b06f6` 检查保存后的 solid，零值跳过实体 contents 初始化。
- `+0x34..+0x37` 被读取为颜色字节，当前颜色偏移应保留。
- RVA `0x1b0750` 读取记录 `+0x3c` 的完整 **32 位** 字段，再 `NOT`、`AND` 到 contents。
- 这段流程没有使用 `+0x38` 开启碰撞。

公开的 bsp_tool 对 v13 尾部字段仍有不同解释，不能直接照抄其 CPU/GPU、颜色和
两段 ushort 划分。本次以安装版本的原版读取逻辑和实际地图记录交叉确认；没有移动颜色字段，
也没有把 `+0x3c` 拆成两个独立 16 位掩码。

证据存放在：
`C:/Users/Administrator/.codex/visualizations/2026/10/26/01a178a2-7a4b-7472-aadc-cc372816a979/tf2-collision/`

- `retail-static-prop-init.asm`：原版 solid 与 remove 字段使用方式。
- `retail-trace-masks.asm`、`retail-contents.asm`：原版脚本常量注册。
- `native-input-hashes.json`：所读取原版 DLL 的 SHA256。
- `raw-static-props.json`：六张地图的原始道具尾部数据与 CM contents 表。
- `retail-verification.json`：修复后读取器的逐记录验证结果。

## 六张原版地图的影响范围

下表的“误判候选”指原版 `solid=0`、但旧代码因 `+0x38!=0` 判为实体的实例。
它不是当前内存中真实碰撞体数量：部分实例可能尚未进入碰撞激活半径，或没有可用模型。
草、岩石列按模型路径包含 `grass`、`rock` 统计，仅用于展示影响，不作为修复筛选条件。

| 地图 | 道具记录 | 旧代码误判候选 | 其中 grass | 其中 rock | 修复后保留实体候选 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 天使城 `mp_angel_city` | 19,458 | 205 | 0 | 0 | 11,725 |
| 虎大 `mp_forwardbase_kodai` | 30,902 | 11,558 | 906 | 2,093 | 5,984 |
| `mp_homestead` | 16,146 | 4,613 | 2,326 | 0 | 5,687 |
| `mp_eden` | 12,411 | 100 | 45 | 15 | 6,427 |
| `mp_complex3` | 10,945 | 3,351 | 1,296 | 0 | 5,873 |
| `mp_wargames` | 8,933 | 728 | 52 | 0 | 8,026 |
| 合计 | **98,795** | **20,555** | | | |

天使城的 grass 误判为零，说明之前用户在天使城看到的异常草片不能全部归因于这次碰撞错误；
透明裁剪属于材质问题，另一些草模型可能原本就是 solid。需要对具体阻挡点做物理命中取证。

## 原版的分层原理

不是每类玩法都有一份完全独立的全地图模型。通常是同一套几何形状，附带 contents
分类，由不同的查询掩码和 collision group 选择。角色尺寸、移动规则、攀爬规则还会继续筛选。

本机零售版 `client.dll` 的脚本常量注册直接确认了这些值：

| 项目 | 脚本公开值 | 用途 |
| --- | --- | --- |
| `TRACE_MASK_PLAYERSOLID` | `0x0201400B` | 铁驭/普通玩家实体查询 |
| `TRACE_MASK_TITANSOLID` | `0x0220400B` | 泰坦实体查询 |
| `TRACE_MASK_SHOT` | `0x46404033` | 武器查询，与移动查询不同 |
| `CONTENTS_PLAYERCLIP` | `0x00010000` | 玩家阻挡类别 |
| `CONTENTS_MONSTERCLIP` | `0x00020000` | NPC 阻挡类别 |
| `CONTENTS_TITANCLIP` | `0x00200000` | 泰坦阻挡类别 |
| `CONTENTS_BULLETCLIP` | `0x00400000` | 子弹阻挡类别 |
| `CONTENTS_NOGRAPPLE` | `0x00000800` | 禁止钩抓标记 |
| `CONTENTS_NOCLIMB` | `0x00100000` | 禁止攀爬标记 |

`NOGRAPPLE` 是排除条件，不能简单把它当成另一张“会挡人的钩抓碰撞网格”。原版
`mp_ability_grapple.nut` 调用原生 `owner.Grapple(direction)`，挂点选择与排除逻辑还在原生玩法里。
本轮没有宣称已经还原原版钩抓算法或其完整过滤顺序。

## 当前读取原理还缺什么

### 1. 旧 contents 算术结论已纠正；MDL 展开与世界 CM 导入需区分

`Titanfall2BspCollisionReader.BlocksPlayer` 对 CM_UNIQUE_CONTENTS 执行
`contents & 0x0201400B`。**此前本文称 `0x00EB0280` 与该 mask 相与为零，这是算术错误；实际为 `0x00010000`，该普通实体会被当前玩家掩码选中。**
原版道具初始化器将 MDL 的 solid / window / grate 展开成 `0x00EB0280`、`0x00E30240`、`0x00A30240`，再应用实例 remove；这不能作为“CM 需要额外未知转换”的证据。

本轮同时确认了原版 CM 读取中的 21 位 index、3 位 type，以及 world/model 0 的 geoset 前缀范围。
对应代码已修复，静态 prop 不重复并入世界。MDL 展开后的实例查询分类到 s&box 的过滤、surface 和玩法 tag 仍需接入，不能把原始值直接当 Source 2 的过滤配置。
权威碰撞仍为可选路径，尚需真实角色 / 水体 / clip / 多人查询验收。

### 2. 原版分类在运行时被压成一个 bool

`StaticPropStreamer.Loading.Encode` 目前只写 `IsCollidable`；`StreamedProp` 只保留 bool。
实例的 remove 掩码、模型 contents、solid 类型没有完整传到创建碰撞体的地方。
所以即便前面正确读取 remove，后面仍不能让同一道具“挡子弹但不挡玩家”。

### 3. 模型碰撞形状有过于宽泛的回退

VPHY 是实体物理形状；hitbox 是命中检测形状；渲染草片是视觉几何；整模型包围盒则
可能填满模型中的洞。这些用途不同。当前缺 VPHY 后逐级替代，会让部分显式 solid 模型
出现不符合原版的阻挡轮廓。

建议为每个模型记录 collision provenance：`VPHY` / `hitbox` / `render-fallback` /
`bounds-fallback`，让诊断命中能够直接报告来源。后续把 trace 几何和玩家物理形状分开，
仅在明确需要的结构模型上启用有依据的实体回退。不能现在一刀切禁用全部回退，否则桥、
台阶、楼面也可能失去承重面。

### 4. 默认世界碰撞仍是可见三角形

`CollisionFilter` 排除了雾片、水瀑等部分特效，没有完整的内容分类信息。其余可见网格
被当成实体；原版不可见的 clip、平滑移动边界又不一定出现在可见网格里。
因此，单纯补 grass/rock 文件名黑名单无法解决整体问题。

## 推荐实施顺序

1. **本轮已做：修 solid=0 误激活。** 由原始实例属性决定，不根据模型名称或大小删除。
2. **下一步优先：加入物理命中诊断。** 对着阻挡物输出地图、SourceIndex、模型路径、
   原始 solid、remove、模型 contents、collision provenance、实际命中 shape、物体尺寸。
   在天使城、虎大各选草/小石头/平台作为固定回归点。
3. **验证查询类别与几何。** CM_GRID_CELLS 的 world/submodel 归属与 21 位索引已按原版修复，见新纠正文档。
   继续建立玩家、泰坦、武器、NOGRAPPLE 对照，接入 MDL 展开 / remove 与 s&box 查询过滤；保留 brush/bevel 和实际角色移动验收。
4. **保留分类到运行时。** streamer payload 增加版本；传递 SourceIndex、solid、有效分类，
   并保持旧版本读取兼容。解析缓存和场景序列化分别验证。
5. **接 s&box 查询与规则。** 可按确定的“查询行为组合”将世界碰撞分块，挂上专用 tags；
   玩家和泰坦各自选择，子弹/钩抓使用独立 trace 查询。s&box 现有 Scene.Trace 的
   `WithAnyTags` / `WithoutTags` 和玩家 `WithCollisionRules(Tags)` 可以承接这些规则。
   必须同时改玩法侧查询；只给碰撞体加 tag，不会自动改变普通 PlayerController 行为。
6. **最后调整流式距离。** 近处玩家实体碰撞、远距子弹/钩抓查询可以有不同驻留策略。
   现有 1536 开启 / 2560 关闭半径只围绕锚点工作；它不能覆盖远距钩抓和所有多人玩家。

不建议用缩小角色碰撞体、增大跨台阶高度来掩盖错误实体，也不建议按“小石头”名称全部禁碰撞。
分类正确后，必要时再针对真实细碎地面处理移动平滑。

## 验证与生效范围

- `Perf/CollisionVerification/CollisionVerification.csproj`：纯托管测试，链接正式 BSP 读取器。
  45 组 v13 二进制样本以及 45 组缓存 JSON 回读全部通过，覆盖 solid 0/1/2/6/7、
  非零保留字段、remove 掩码、相邻颜色/皮肤/缩放字段。
- 六张安装版地图共 98,795 条记录逐项对照原始字节，全部通过。
- 完整挂载模块已编译到 `.tmp/tf2-collision-audit/mount-build/`，0 警告、0 错误。
- 正式读取器文件的 whitespace 校验通过。
- 未运行全引擎测试，也未完成游戏内移动回归。本轮没有替换同机画面测试使用的
  `game/mount/titanfall2/titanfall2.dll`，避免把队友尚未验收的画面修改一起发布。
- 后续统一构建部署 DLL，再重新加载挂载地图后生效。已序列化场景里的旧 Collidable bool
  不会因 DLL 替换自动恢复，需要重新导入地图；单纯保留旧运行场景也不会消掉已有碰撞体。
- 解析缓存的 identity 含程序集 MVID，正常重建 DLL 会区分旧派生缓存，无须删除原游戏资源。

运行纯托管回归：

```powershell
dotnet run --project engine/Mounting/Sandbox.Mounting.Titanfall2/Perf/CollisionVerification/CollisionVerification.csproj --configuration Release
```

参考（只用于辅助，零售版二进制核验优先）：

- https://raw.githubusercontent.com/snake-biscuits/bsp_tool/master/bsp_tool/branches/respawn/titanfall.py
- https://raw.githubusercontent.com/snake-biscuits/bsp_tool/master/bsp_tool/branches/respawn/titanfall2.py
- https://r2northstar.readthedocs.io/en/latest/reference/respawn/native_server/collision.html
