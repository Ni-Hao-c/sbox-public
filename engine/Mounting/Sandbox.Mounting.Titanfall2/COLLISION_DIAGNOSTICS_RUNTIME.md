# 碰撞来源诊断与资源生命周期运行回归

日期：2026-10-28。接续 LIFETIME_COLLISION_FIXES.md。当前工作树中的其他修改保持原样。

补充：已新增面向使用者的 `titanfall2_collision_look` 射线/球体扫掠命令与十六进制报告，详见
[COLLISION_LOOK.md](COLLISION_LOOK.md)。下文七项来源测试是底层来源记录的历史验证，新的命令验证另见该文档。

## 本轮实现

普通 MDL 模型导入时写入 `titanfall2_collision` 模型数据，保存来源路径、碰撞几何来源、凸包数量与原始 MDL contents。来源明确区分 VPHY、hitbox、render-mesh fallback、simplified-box 和 none，沿用真正执行的构建分支，不按模型名称猜测。

静态道具的生产碰撞创建路径增加 `Titanfall2CollisionSource` 组件，保留地图、模型、SourceIndex、原始 solid、ReservedCollisionData 和完整 remove 掩码。它没有逐帧更新，不改变碰撞过滤。旧版本 payload 缺失身份时明确标为无原始实例信息。

世界碰撞组件同样标记原始 CM 或过滤后的可见三角形回退。诊断基于实际命中的 Collider 查找来源，不以附近渲染对象代替物理命中。

在可信 MCP 脚本中可直接返回：

```csharp
var hit = scene.Trace.Ray(start, end).Run();
return Titanfall2CollisionSource.DescribeHit(hit);
```

输出包含物理命中位置、法线、triangle、Collider/Shape 包装类型、模型资源和边界、缩放、实例元数据及模型来源。ShapeType 当前为 CLR 包装类型，不代表引擎内部凸包/网格枚举；具体几何构建来源从 ModelData 读取。缺少来源组件/模型元数据时保留 null/unknown，不伪造分类。

## 最终构建上的运行检查

在本次自有测试编辑器执行，未保存项目或用户场景。隔离场景在 finally 中销毁，测试编辑器在结束后关闭。

1. **共享粒子：12 项通过。** 使用原版 `particle/muzzleflash/flash_core` 的五个实际 GPU 帧，两个隔离场景共同使用 Sprite。销毁其中一个、刷新挂载、创建新缓存、销毁旧场景及强制 GC 后，存活引用的贴图保持有效，新挂载缓存也不被旧场景销毁清空。首个烟雾候选实际为单帧，没有覆盖目标缓存；最终脚本对多帧样本做了硬性前置检查。
2. **生成地形：15 项通过。** 使用 `sp_beacon` 的两个 worldspawn 克隆，检查 269 个生成模型以及 90 个烘焙材质的 SKY A/B、RTL A/B。关闭一个克隆后检查剩余克隆，再关闭全部、等待 13 秒并强制 GC，重新实例化后仍全部有效。
3. **真实碰撞命中：7 项通过。** 真实零售岩石模型通过生产 `EnsureCollisionState` 创建碰撞并被物理射线命中，结果返回 VPHY、17 个凸包及 MDL contents=1。原始 contents 与从安装包重新解析的 MDL header 一致。实例索引 12345、solid=6、保留值 0xFEDCBA98 和 remove=0xFFFFFFFF 是合成对照，用于验证数据传输，不是该岩石在实际地图中的原始记录。
4. **Engine 针对性测试：10 项通过。** 在此前九项基础上，增加来源组件克隆后完整保留身份和 32 位掩码的回归。

标准 SboxBuild Developer 构建通过。非 LiveBackend 套件的 Integration、Unit、分配及 Text 分组通过；Engine 分组 3075 通过、3 失败，失败仍为 WebSurface.IsUriAllowed 的三个内网域名用例。全仓库 format --verify 仍报告已有格式/行尾问题，本轮新增 CollisionSource.cs、Titanfall2MountTests.cs 及修改的 StaticPropStreamer.Collision.cs 未出现在其诊断中。

最终部署与构建 DLL SHA256 均为 `2D2A18934B5A5D6F4DB3C4FB678BCE99199B45063B6BEF7856336521C5DB8063`。运行进程的 MVID 为 `6e41fc61-eb12-44da-8d70-e0cba9b92526`，已与部署 DLL 核对一致。

可重复运行的脚本：

- Perf/ParticleVerification/VerifySharedParticleLifetime.csx
- Perf/MaterialVerification/VerifyCachedWorldLifetime.csx
- Perf/CollisionVerification/VerifyCollisionProvenance.csx

共享粒子脚本会 Refresh 整个 Titanfall 2 挂载，只能在专用测试进程运行。地形脚本需要 `SBOX_TITANFALL2_BSP_LIGHTMAPS=1`；未启用时其烘焙纹理检查会失败，不能解释为通过。

构建、针对性测试和运行结果保存在仓库 `.tmp/tf2-collision-diagnostics-*.log`、`.tmp/tf2-*-runtime-final.log` 与 `.tmp/tf2-collision-provenance-runtime.log`；最终 DLL 身份和汇总见 `.tmp/tf2-runtime-continuation-verification.json`。

## 边界与下一步

这些结果验证 GPU 资源句柄/模型有效性、物理命中及元数据传输，不是逐像素画面还原验收，也没有测得长期换图显存预算。两份同地图世界克隆不能替代两个不同地图完整运行的长期压力验证。

contents 到玩家、泰坦、子弹和钩抓规则的转换仍须从原版行为取证。当前只保存和报告原始值，不把 remove 直接应用到 s&box 物理掩码。下一阶段可用本轮诊断对真实地图的草、岩石、台阶、不可见 clip 逐点取证，再实现有依据的分类。
