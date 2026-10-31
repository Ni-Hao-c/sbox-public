# PerfKit — 可复用的挂载场景性能件

`MountedScenePerfKit.cs` 是一个**单文件、零游戏格式依赖**的性能模块。
把它整份拷到下一个 `Sandbox.Mounting.*` 项目（或直接留在挂载程序集里）即可使用；
Titanfall 2 挂载已经在生产路径上使用它（世界聚合 / 道具实例批次 / 距离剔除），
所以这不是"示例代码"，而是被真机编译与运行验证过的实现。

## 三个单元

### 1. 世界几何聚合 — `MountedAggregateBuilder`

把"按 (空间分区, 材质) 分组的几何"重组成"每个材质一个模型、每个分区一个 draw"。

需要载入期流水线时，把它拆成两步：`PrepareGeometry(parts, start, count)` 只做纯托管的顶点/索引
拼接（可放后台线程），`CreateAggregate(material, geometry, name)` 在主线程建 `Mesh`/`Model`。
`Titanfall2WorldAggregator` 用「后台 Parallel + 有界队列 + 主线程消费」把这两步重叠起来；
`SBOX_TITANFALL2_PARALLEL_AGGREGATE=0` 可退回顺序构建做 A/B。

```csharp
var groups = new List<MountedGeometryGroup<MyVertex>>();
foreach ( var (material, cells) in byMaterial )
{
    var group = new MountedGeometryGroup<MyVertex>( material );
    foreach ( var cell in cells )
        group.Add( cell.Vertices, cell.Indices, cell.Bounds );
    groups.Add( group );
}

var aggregates = MountedAggregateBuilder.Build( groups, "mygame/map/mapname_world" );
foreach ( var aggregate in aggregates )
    MountedAggregateBuilder.Attach( worldObject, $"world_aggregate_{i++}", aggregate, castShadows: false );
```

- 每个分区仍是独立 fragment → 仍可单独剔除；
- 分区数超过 `AggregateRenderer.MaxFragments`（512）时**自动切片，绝不丢几何**；
- 顶点/索引列表按总量预分配，避免大块 `List` 扩容拷贝；
- 索引在合并时重基，`AddSubMesh` 的 base vertex 必须传 0（传偏移会叠两次，材质错乱）。

### 2. 实例批次 — `MountedInstanceBatch`

一次 `DrawModelInstanced` 画多个永久实例，可选择显式 LOD，并支持运行期替换"实际绘制的子集"。

```csharp
var batch = new MountedInstanceBatch( scene.SceneWorld, model, transforms,
    new MountedInstanceBatchOptions
    {
        CastShadows = true,
        Batchable = true,
        IncludeInCubemap = false,       // 静态道具通常不需要进反射 pass
        NeedsEnvironmentMap = false,
        Tint = color,
        SelectLod = distance => distance < 1500f ? 0 : distance < 4000f ? 1 : 2,
    } );
```

- `SelectLod` 为 null 时使用引擎自己的选级（单级模型 / 自动 LOD）；
- 显式 LOD 用**离相机最近的实例原点**，不用批次 bounds（bounds 最近点常落在实例内部，会系统性偏精细）；
- 数组视为不可变：渲染线程每帧读取，只能替换引用，不能原地写。

### 3. 距离剔除 — `MountedInstanceCull`

```csharp
// 每个批次一个，状态常驻
var changed = batchCuller.Update( allTransforms, anchor, keepDistance: 4096f + 512f );
if ( batchCuller.KeptCount == 0 ) batch.RenderingEnabled = false;
else if ( changed ) batch.SetDrawTransforms( batchCuller.Subset );
```

- 全部在半径内 → 直接复用完整数组（零分配）；
- 需要裁剪时，在保留数量、实际成员或输入数组引用变化时重建子集；相同数量的不同成员不能复用旧列表；
- 换模型/重建批次后可用 `ApplyTo(batch)` 恢复子集，避免闪一帧全量实例；
- `Reset()` 用于运行时关闭剔除。

## 接入清单（新挂载项目）

1. [ ] 世界几何是否"按 cell 切 + 按材质切 draw"？→ 用单元 1 聚合成每材质一个模型。
2. [ ] 静态实例是否按对象逐个提交？→ 用单元 2 合成 instanced 批次。
3. [ ] 移动时是否有成片道具突然消失？→ 用单元 3 做逐实例距离剔除 + 迟滞。
4. [ ] 所有开关做成环境变量，默认值 = 已验证的最优；A/B 保持同一相机与位姿。
5. [ ] 用 `Obj`（渲染对象数）而不是 `DrawCalls` 衡量收益：`DrawCalls` 不统计自定义
   `SceneCustomObject` 内的 `DrawModelInstanced`。

## 实测收益（Titanfall 2 / mp_angel_city）

| 手段 | 效果 |
|---|---|
| 世界聚合 | 774 renderers / 4444 draws → 334 aggregates / 1091 draws；Render 17.4ms → 13.1ms，画面一致 |
| 实例批次 + 逐实例剔除 @4096 | Obj 2196 → ~1200，Render 10.4ms → 7.8ms（76 → 93 fps，同点 A/B） |
| 显式 LOD（单位修正后） | 三角形 5.61M → 1.86M（−67%），fps 45.3 → 52.8 |

数据与踩坑记录：`D:\sbox\titanfall2-perf-findings.md`；方法论：`D:\sbox\mount-perf-playbook.md`。

## 设计不变量（改代码前先读）

- `AggregateRenderer` 的 fragment 顺序 = draw 顺序；每个 fragment 有自己的 bounds 作为剔除单位。
- fragment 上限是原生能力（`MaxFragments`），不是建议值；切片而不是丢弃。
- 合并顶点缓冲后索引必须重基，且 `AddSubMesh` 的 `startVertex` 传 0。
- `Attach` 里先赋 `Fragments` 再赋 `Model`：`Model` 的 setter 会重建原生 aggregate，
  空 fragment 列表会重建出一个空聚合体。
- 实例批次的 `Transform[]` 一旦交给渲染线程就不可变。
- `MountedInstanceCull` 的子集数组同样不可变；只替换引用。
