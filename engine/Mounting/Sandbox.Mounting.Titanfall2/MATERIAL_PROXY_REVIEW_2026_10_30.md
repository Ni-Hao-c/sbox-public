# Titanfall 2 材质代理调试与修复

日期：2026-10-30。保留工作区原有改动。本轮从实际零售 VMT 与生产解析 / 更新路径定位问题。

用户最新要求：先 debug 和修复，最后由用户统一验证。因此，以下明确区分已获得的阶段证据与最新源码待验收项，不继续反复运行构建、GPU 或整图测试。

## P2：TextureScroll 丢失作者设置的缩放

原解析器没有把 `texturescale` 保存到运行时表达式，更新器始终给矩阵写入缩放 1。直接写入 `$basetexturetransform` 或 `$texture2transform` 的滚动代理因此改变了纹理重复密度。

38 个零售 VPK 目录中去重读取 591 个 VMT，读取失败为零。去掉行注释后，25 个 VMT 包含 `texturescale`，例如：

| 实际材质 | 作者设置 |
| --- | --- |
| `models/beacon/heatscreen_heat_glow` | 第二层 0.25 |
| `models/fx/emp_inner_scroll_01` | 第一层 2、第二层 0.8 |
| `skies/boomtowncubemap_static` | 第一层 6、第二层 0.2 |
| `models/fx/uv_scanner` | 第二层 5，滚动速度为 0 |

修复在 `Formats/Titanfall2VmtReader.cs` 与 `Resource/MaterialAnimator.cs`：

- 保存缩放常量与变量名，运行时解析变量输入。
- 缺省滚动速度使用 1，显式 0 保持静止；静态速度读取与运行时表达式保持一致。
- 为直接矩阵输出应用缩放。共享 shader 围绕 UV 中心缩放，而 Source 的 TextureScroll 围绕原点缩放，因此附加 `(scale - 1) / 2` 的平移补偿。
- 中间向量输出仍只包含滚动平移，避免改变 `TextureScroll → TextureTransform` 的输入语义。

默认值与矩阵 / 向量输出语义依据 [Valve Source SDK TextureScroll 实现](https://raw.githubusercontent.com/ValveSoftware/source-sdk-2013/master/src/game/client/texturescrollmaterialproxy.cpp)。这是明确的 Source 兼容性依据，不等同于已经验证 Titanfall 2 全部定制代理。

## P2：Sine 相位与特殊周期错误

原更新器使用 `time + timeoffset`，把延迟写成提前；解析器和更新器还各自把周期钳到至少 0.0001，丢失零周期、负周期及更小正周期的含义。

修复后使用 `time - timeoffset`；仅将恰好为 0 的周期按 1 秒计算，保留负周期及非零小周期。解析器不再提前改写周期。

依据 [Valve Source SDK CSineProxy 实现](https://raw.githubusercontent.com/ValveSoftware/source-sdk-2013/master/src/game/client/mathproxy.cpp)。上述 591 个零售 VMT 中没有发现实际 `timeoffset`，所以不把相位修复描述成已确认的实图改善；它修复的是当前解释器与该 Source 规则的确定差异。

## 缓存兼容

`Resource/Material.Cache.cs` 的材质转换标识从 `material-vmt-proxies-v2` 升为 `material-vmt-proxies-v3`。已有 RPAK 材质描述可能合并 VMT 运行时行为，必须重建，才能获得新表达式参数与周期语义。无需删除用户的整个缓存目录。

## 回归准备及验证状态

新增 `engine/Tests/Sandbox.Test.Engine/Scene/Titanfall2MaterialProxyTests.cs`，目前共 17 个数据用例：

- 8 个滚动解析用例：多种缩放、零缩放、缺省缩放、变量输入、缺省速度与显式零速度。
- 3 个 Sine 解析用例：零、负及极小正周期原样保留。
- 6 个 Sine 计算用例：延迟方向、负偏移、零周期回退、负周期与极小正周期。

已有阶段证据：修复滚动解析前，8 个用例中有 6 个失败；修复后，这 8 个用例通过。该阶段还确认原生材质参数不再固定为缩放 1。后加入的 Sine 修复、测试调整和最终源码没有重新构建或运行，按用户要求留待统一验证。

临时 GPU 场景的默认参数对照也返回全零像素，其采样环境尚未调通，不能证明 UV 输出；该临时 Integration 测试已撤下。排查过程中对 `TestAppSystem.cs` 的多余改动已完全撤回。没有为了测试场景改动生产渲染器，也没有把全零输出当成挂载 shader 的已确认缺陷。

证据保存在仓库 `.tmp/tf2-proxy-review/`：零售扫描、缩放清单、修复前后的解析断言及临时 GPU 诊断日志。旧日志中的构建和测试结果只适用于各自阶段，不代表最终源码已验收。

## 本次审阅覆盖与后续统一验证

此前的容器及宿主生命周期修复分别见：

- [VPK 审阅](ARCHIVE_REVIEW_2026_10_30.md)：数据边界、目录内数据与分卷来源。
- [地图生命周期审阅](LIFECYCLE_REVIEW_2026_10_30.md)：对象所有权、编辑器预览、重试、部分失败清理及跨根引用。
- [STARPAK 审阅](STREAMING_ARCHIVE_REVIEW_2026_10_30.md)：数据区边界、重复偏移和损坏索引发布。

还检查了 RPAK 页读取、派生缓存及预取退出。预取已有取消、任务等待和准备数据清理；没有仅凭后台任务存在就添加推测性卸载修复。RPAK 跨页读取可能服务于连续段，也没有无格式依据地禁止它。

用户统一验证时应以最终 DLL 为准，覆盖挂载定向回归、地图重载 / 预览切换、实际双纹理材质缩放与滚动，并在实图验证前核对运行产物身份。当前没有部署 Steam，也没有声称完成整图画面一致性或全部代理功能。

仍需单独研究的能力包括任意二维缩放 / 变换中心、全部动态代理输入及实体驱动代理；这些属于已识别的覆盖边界，本轮没有用猜测补齐。
