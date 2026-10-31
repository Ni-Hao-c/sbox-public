# 对准物体的碰撞命中诊断

2026-10-28。接续 `COLLISION_AUDIT.md` 与 `COLLISION_DIAGNOSTICS_RUNTIME.md`。

## 使用方法

在正在运行的地图里对准挡路物，打开控制台执行：

```text
titanfall2_collision_look
```

默认从当前游戏相机发出 512 单位长的物理射线，输出可复制的 JSON。
缩短距离、并做半径 12 单位的球体扫掠：

```text
titanfall2_collision_look 256 12
```

第一参数是最大距离，第二参数是球体半径。半径为 0 时使用射线。
它是按需查询，不逐帧执行，不移动玩家、不修改碰撞或原版掩码，也不移除模型。

对小石头、草根等低矮阻挡物，应向下对准实际卡住的位置；从眼睛水平发出的射线
可能从脚边障碍上方通过。球体扫掠用于观察附近是否有物理形状，并非完整的角色胶囊体、
跨台阶或滑动移动模拟。

使用相机已组合的 `CameraComponent.View`，避免玩家相机修改器与相机对象原始 Transform
不一致时，查到与画面方向不同的位置。能识别到相机所属玩家，或场景中唯一使用内建相机
控制的本地 PlayerController 时，会忽略该玩家层级；否则仅忽略相机自身层级，不猜测自定义玩家。

## 输出怎么读

| 字段 | 含义 |
| --- | --- |
| `Outcome` | `hit` / `no-hit` / `started-solid`；后者表示查询起点已经在实体内 |
| `Query` / `SphereRadius` | 物理射线或球体扫掠及半径 |
| `TravelDistance` | 射线或球体中心从起点移动到命中所经过的距离 |
| `ContactPosition` | 实际接触点；球体扫掠时与球体中心位置不同 |
| `GeometrySource` | 导入时真正执行的碰撞构建分支：VPHY、hitbox、render-mesh fallback、simplified-box 等 |
| `SolidType` | 原版实例 solid 字节；缺少原始身份时为 null |
| `CollisionFlagsRemoveHex` | 完整 32 位实例 remove 掩码，按十六进制显示 |
| `ReservedCollisionDataHex` | 原版保留字段，不能当成开启碰撞的标记 |
| `ModelContentsHex` | 原始 MDL contents，尚未转换成玩法查询分类 |
| `IsMeshShape` / `IsHullShape` | 实际命中形状是否为网格或凸包，避免把 CLR `PhysicsShape` 包装名误当成形状分类 |
| `SolidCollisionsEnabled` / `ShapeTags` | 命中形状当前实体碰撞开关与标签 |
| `PhysicsBounds` | 命中物理 body 的世界空间边界，可与模型边界、缩放对照 |
| `Details.Source` | 地图路径、模型路径、SourceIndex、原始标记和来源信息 |
| `Details.ModelData` | 模型导入来源、凸包数量与原始模型 contents |

`PhysicsBounds` 是 body 的 AABB，不是逐个凸包轮廓。不能仅凭 AABB 较大认定模型使用了
包围盒回退，应以 `GeometrySource` 为准。

这条命令查询当前 s&box 物理世界，排除触发器，不启用渲染网格或高精度 hitbox 查询。
没有套用尚未验证的原版玩家/泰坦/子弹掩码，也没有模拟宿主游戏自定义的碰撞规则。
因此“查询命中”不自动等于“宿主角色一定会被它阻挡”；应结合 `ShapeTags`、实体碰撞开关
和宿主 PlayerController 的规则继续确认。

命中没有来源元数据的宿主碰撞体，或旧版本场景时，输出保留 null/unknown。
不会根据附近草或岩石的渲染对象猜测被命中的模型，也不会把缺失掩码报告为已知的零。

## 一次取证应该保存什么

1. 地图与卡住的位置。
2. 向实际卡住点执行 `titanfall2_collision_look 256 0` 的输出。
3. 同一方向执行 `titanfall2_collision_look 256 12` 的输出。
4. 确认是走路被挡、跨台阶失败，还是钩抓/武器查询问题。

优先对草、小石头、台阶、桥面各取一个点。对照结果后再处理错误回退或 contents 分类，
不按模型名称或尺寸批量删除实体。

## 实现与验证

- `Resource/CollisionDiagnostics.cs`：查询入口、控制台命令、可复制 JSON。
- `GameMount.cs`：挂载时注册命令，卸载时清理。挂载程序集没有 PackageLoader 的自动
  控制台注册，因此仅写 `[ConCmd]` 不够；桥接代码局限于可信挂载，不扩展游戏代码访问白名单。
- 复用已有 `Titanfall2CollisionSource.DescribeHit` 与模型来源数据，没有替换生产物理形状。
- `Perf/CollisionVerification/VerifyCollisionLook.csx`：隔离物理场景的回归脚本，结束时销毁
  测试 Scene、恢复宿主 Scene，临时注册命令的测试同样恢复原有注册状态。

已完成的运行对照包括：最近碰撞、触发器排除、纯渲染干扰物、完整 32 位掩码、无元数据、
旧场景身份、初始重叠、距离限制、偏轴射线与球体扫掠、相机入口、JSON、控制台注册/执行/清理。
最终源码的独立探针在新进程中 **18 项全部通过**；其 MVID 为
`0d8c9948-e756-4e92-90bb-2ab37f6f3b11`，避免将之前加载的旧探针计为最终版本。
证据在 `.tmp/tf2-collision-look/results/controlled-final.json`，真实地图记录在同目录
`live-map.json`。

另在当前已加载的 `sp_beacon` 地图中，对四个实际道具做物理命中，逐项对照安装版 BSP：

| SourceIndex | 原版模型 | 核验 |
| ---: | --- | --- |
| 5531 | `beacon_beam_support_01.mdl` | 模型、solid、保留字段和 remove 一致 |
| 5532 | `beacon_beam_support_01.mdl` | 模型、solid、保留字段和 remove 一致 |
| 9641 | `beacon_railing_lrg_128_01.mdl` | 模型、solid、保留字段和 remove 一致 |
| 13203 | `cargo_container_imc_01_green.mdl` | 模型、solid、保留字段和 remove 一致 |

四次报告均为 VPHY、solid=6、原始模型 contents=`0x00000001`，没有使用合成的实例索引冒充
地图原始数据。这验证身份对应和实际 VPHY 命中；没有据此宣称已修复用户截图中所有草或碎石。

## 部署边界

本轮正式模块编译到仓库 `.tmp/tf2-collision-look/mount-build/`，没有覆盖画面任务使用的 DLL。
运行验证通过单独编译、来自同一诊断源码的探针完成；临时程序集不是最终部署 DLL 的运行验收。
正式使用新命令需要部署包含这些源码修改的挂载 DLL，并在新进程中加载挂载。
现有 v6 道具来源组件可以直接供查询使用，不需要为了诊断修改模型碰撞。

本轮也在当时存活的画面测试编辑器临时注册了同一诊断源码的命令，便于直接取证。
该临时注册随进程退出失效；重新启动后应以统一构建部署的正式挂载 DLL 为准。
本轮自有的最小测试编辑器在验证结束后关闭，画面任务的编辑器保持原样。

完整玩家/泰坦/子弹/钩抓分层仍是后续工作；本工具保存并显示原始值，不将 remove 直接套入
s&box 物理过滤。
