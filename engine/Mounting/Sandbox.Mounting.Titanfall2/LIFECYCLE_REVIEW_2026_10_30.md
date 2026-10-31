# Titanfall 2 地图宿主生命周期审阅与修复

日期：2026-10-30。在既有工作区改动和本轮 VPK 修复上继续审阅，确认并修复 `Resource/Titanfall2Map.cs` 的对象所有权、编辑器预览和场景实例引用问题。本报告的验证来自本地源码构建与定向测试。

## 已修复的问题

### P1：卸载或重载地图会删除其他系统的临时对象

原实现遍历宿主的全部直接子对象，删除所有带 `GameObjectFlags.NotSaved` 的对象。这个标志只表示不保存到场景，不能证明对象由地图组件创建。用户的运行时相机、玩家或其他临时子对象也可能使用它，因此关闭组件或重载地图可能误删宿主内容。同时，地图根对象被移出宿主后，原实现又无法回收它。

现在由组件单独记录创建的地图根对象和补充相机，卸载只销毁这些对象。销毁前先清空所有权列表，重复卸载不会再次销毁。复制组件时生成对象不会被复制，新的地图实例通过自己的加载流程建立独立所有权。

### P2：跨根引用丢失，第二个实例可能引用第一个实例的对象

原实现逐个调用 `GameObject.Deserialize`，每个根对象的回调会在下一个根创建前执行。指向后续根对象或其组件的引用因此可能解析为空。再次加载同一 `SceneFile` 时，目录会为冲突的标识临时生成新值，但序列化引用仍保存原标识，因而可能指向第一个实例。纯世界场景的 `Titanfall2WorldReady.Props` 等跨根依赖也使用这条加载路径。

现在先复制整个地图 JSON，再调用现有 `SceneUtility.MakeIdGuidsUnique`，在同一次映射中更新所有根、组件和引用。使用公共 `Scene.BatchGroup()` 把反序列化回调延后到全部根对象创建之后。共享的 `SceneFile` 不会被修改。

正向和反向引用夹具均在修复前失败：前者出现 `Unknown GameObject`，后者在第二实例中指向错误对象；修复后两者均通过，同时验证对象引用和组件引用。

### P2：编辑器预览开关没有完整执行加载、关闭与重试

组件只有更新订阅，缺少 `Component.ExecuteInEditor`，因此开启 `Load In Editor` 不能可靠进入编辑器更新分发。原来的全局播放状态判断也没有区分并存的编辑场景与播放场景，关闭预览时只提前返回，没有卸载生成对象。

现在显式加入编辑器执行接口，用当前 `Scene.IsEditor` 决定预览策略。关闭预览会卸载地图并清除缺失状态；同一路径曾加载失败后，关闭再开启可重新尝试。测试直接使用 `Scene.EditorTick`，没有反射调用私有回调。

### P2：单个根反序列化失败会留下未完成对象

原实现捕获异常后继续加载，却没有销毁已创建的根对象。现在在异常路径移除所有权并立即销毁有效的未完成对象，其余有效根仍继续加载。

## 验证结果

- 新增 `Titanfall2MapLifecycleTests.cs`，共 **11 项**：卸载、禁用、重载、相机回收、反序列化失败、实际编辑器 tick、失败后重新预览、移除组件、克隆所有权、移出宿主后的回收，以及正反向跨根引用。
- 最初五项生命周期用例在旧实现上 **5/5 失败**，首轮修复后 **5/5 通过**；加入边界测试后九项通过；跨根引用的两项用例单独确认失败后修复。
- 最终 `FullyQualifiedName~Titanfall2`：Engine **101/101**、Integration **5/5**，合计 **106/106**。包含上一轮新增的 26 项 VPK 回归。其他测试进程无匹配用例，不计入通过数量。
- `SboxBuild build --config Developer` 成功。managed solution 为 0 warning / 0 error；launcher 发布保留既有 MonoMod 单文件警告。
- 本次两个 C# 文件的完整定向 `dotnet format --verify-no-changes --include ...` 通过。全仓格式检查的既有问题记录在 `ARCHIVE_REVIEW_2026_10_30.md`，本轮没有批量格式化其他文件。
- source build、本地挂载目录、Engine 测试目录、Integration 测试目录中的挂载 DLL SHA256 一致：

`5A6DA8EAD0EBDC945341E98B99C1F682725981D12D1E6F46A0608628C54C1D7C`

## 证据与复跑

证据目录：仓库 `.tmp/tf2-lifecycle-review/`。

- `tests-before.log`、`tests-after.log`：最初五项生命周期测试的修复前后结果。
- `tests-references-before.log`：修正夹具的禁用组件查询后，两项引用用例在生产修复前的真实断言失败。
- `build-final.log`、`tests-final.log`：最终构建和 106 项定向回归。
- `format-targeted-verify.log`、`binary-hashes.json`：格式检查和四份 DLL 身份。

```powershell
dotnet run --project engine/Tools/SboxBuild/SboxBuild.csproj -- build --config Developer
dotnet run --no-build --project engine/Tools/SboxBuild/SboxBuild.csproj -- test --no-build --filter 'FullyQualifiedName~Titanfall2'
dotnet format engine/Sandbox-Engine.slnx --no-restore --verify-no-changes --include engine/Mounting/Sandbox.Mounting.Titanfall2/Resource/Titanfall2Map.cs engine/Tests/Sandbox.Test.Engine/Scene/Titanfall2MapLifecycleTests.cs
```

上一轮的零售归档检查、38 个目录和四张地图完整 BSP 解码结果见 `ARCHIVE_REVIEW_2026_10_30.md`。其中记录的旧 DLL 哈希属于当时的归档修复构建；上述哈希属于包含本轮生命周期修复的最终构建。

这些测试证明加载路径、对象与引用生命周期以及既有原生碰撞集成回归成立；本轮没有启动真实零售地图进行渲染验收，也没有部署 Steam 安装。SP 视觉差异、玩法碰撞分类、全地图 GPU/显存预算和长时间稳定性仍需分别验证。没有改动用户的编辑器或项目状态。
