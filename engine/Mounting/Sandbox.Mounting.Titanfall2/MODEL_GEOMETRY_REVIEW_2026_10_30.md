# Titanfall 2 模型几何审阅与修复

日期：2026-10-30。本轮继续审阅 MDL / VVD / VTX 到运行时模型、静态 LOD 和聚合几何的路径。保留原有工作区改动，包括此前的表分配边界检查、材质代理和生命周期修复。

按用户要求，本轮先 debug 和修复，最终构建、回归及实图验证留给用户统一进行。以下结论来自生产代码及格式规则核对，不把新测试写成已通过。

## P2：将 bodygroup 的 Base 当作默认模型下标

`Titanfall2MeshExtractor.ExtractLod` 原来把 `Mdl53BodyPart.Base` 直接赋给默认模型下标，仅在越界时回退 0。

`Base` 实际是组合 body 数值时使用的倍率；选择规则是 `(body / Base) % modelCount`。未传入 body 覆盖时，body 为 0，每个部件应选模型 0。依据 [Valve Source SDK GetBodygroup](https://raw.githubusercontent.com/ValveSoftware/source-sdk-2013/master/src/game/shared/animation.cpp)。

因此，当 Base 为 1 或 2、模型数量又足够大时，旧代码会选择第二或第三个模型。若作者把其中一个变体设为空，这也可能使部件消失。当前未统计实际零售模型的影响数量，不将这项描述为已完成实图改善。

修复后默认只选择模型 0，保留作者的空默认选项；显式 `allBodyGroupChoices` 仍能导入所有变体。跳过变体的几何输出时，仍累计其 VVD 顶点范围，防止后续部件错用前一变体的顶点。相关配置注释及架构文档已更正。

## P2：VTX strip 索引起点被误当作字节偏移

`Titanfall2VtxReader.ParseStrips` 原来使用 `stripHeaderAddress + IndexOffset` 读取 ushort，并将 offset 为 0 视为不存在。

strip 的 `IndexOffset` 是其所属 strip group 索引数组内的元素下标；组自身的 `IndexOffset` 才是相对组头的字节偏移。Source 的实际消费路径也按 `group.pIndex(strip.indexOffset + i)` 取索引，见 [Valve Source SDK VRAD 几何读取](https://raw.githubusercontent.com/ValveSoftware/source-sdk-2013/master/src/utils/vrad/vradstaticprops.cpp)。

修复后先读取组索引数组，再从该数组切出每个 strip 的索引：

- 允许第一个 strip 从元素 0 开始。
- 后续 strip 不再从结构体或其他数据区域读取。
- 检查负偏移、负计数、超出本组及溢出输入；使用减法比较避免加法溢出。
- 保持组索引数组不变，修复的是 `StripInfo.Indices` 的解析结果。当前 extractor 使用组索引，不能仅凭此修复宣称所有渲染拓扑问题已解决。

## P2：VVD 损坏重排记录被静默截短

`BuildLodVertexMap` 原来只把每条 fixup 的数量截到剩余容量，没有检查源顶点范围。负源下标、超出顶点数组的范围、超大计数及累计超量都可能继续生成部分映射；后续顶点提取还可能把不存在的顶点变成原点位置。

现在在增加映射前验证源范围和剩余目标容量，非法记录直接报错，不发布截短的成功几何。合法的零长度末尾记录仍可读取。合法重排顺序和 LOD 过滤保持原逻辑。

## P2：MDL 吞掉内嵌 VTX 的解析失败

`ParseEmbeddedVtx` 原来捕获全部解析异常并返回 null。资源加载器因此无法区分缺失 VTX 和声明了完整内嵌块但内容损坏；带骨架的模型还可能仅创建 bindpose / 动画而被报告为加载成功。

现在让已声明内嵌块的解析异常传递至资源加载器，由现有模型路径日志报告具体失败。原有无内嵌块的可选数据行为仍保留，动画模型不需要为了本修复伪造 VTX。

## 缓存与回归准备

当前模型派生缓存包含 `ContentFingerprint`，其起点是挂载程序集 MVID。重新构建挂载后，旧几何缓存自然不会命中新转换器，因而本轮无需额外清空缓存或新增缓存键版本。运行中已加载的引擎资源仍应在最终统一验证时重新加载。

新增 `engine/Tests/Sandbox.Test.Engine/Scene/Titanfall2ModelGeometryTests.cs`，包含 26 个数据用例：

| 范围 | 数量 | 重点 |
| --- | ---: | --- |
| bodygroup | 5 | 多种 Base、空默认、全部变体、后续部件顶点偏移 |
| VVD fixup | 9 | 合法重排、零长度末尾、六种非法范围、累计超量 |
| VTX strip | 8 | 起点 0、非零元素起点、零长度末尾、六种本组边界错误 |
| MDL 内嵌 VTX | 4 | 无块、合法块、版本错误及范围错误传播 |

本轮没有启动编辑器、部署 Steam、构建或运行这些新增用例。只做了代码和 diff 检查；26 项是已添加的用例数量，不是通过数。

统一验证时应包含默认 / 全变体导入、同一模型各 LOD 的后续部件位置、零售模型内嵌 VTX 解析，以及坏输入的明确失败。非默认 skin / bodygroup 的公开切换接口、triangle strip 的完整渲染支持及整图外观不在本轮完成声明中。

此前材质代理修复见 [MATERIAL_PROXY_REVIEW_2026_10_30.md](MATERIAL_PROXY_REVIEW_2026_10_30.md)；容器与地图生命周期的阶段证据继续保留在各自审阅记录中。
