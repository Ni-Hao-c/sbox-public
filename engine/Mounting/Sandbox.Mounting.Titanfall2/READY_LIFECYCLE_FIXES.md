# 纯世界 ready 发布、损坏数据与碰撞生命周期修复

2026-10-29。接续 [MOUNT_REQUIREMENTS_STATUS.md](MOUNT_REQUIREMENTS_STATUS.md) 的 P0 2.3。本次针对纯世界的就绪契约修复，不代表整图碰撞分类或原作视觉验收完成。

## 已复现的问题与修复

1. **ready 检查没有进入场景更新循环。** `Titanfall2WorldReady` 覆写 `OnUpdate`，却没有实现 `IUpdateSubscriber`。挂载 DLL 没有游戏编译器的接口重写阶段，`Scene.ObjectIndex` 不会把它加入更新集合。旧测试直接反射调用方法，遗漏了真实调度缺陷。现在显式订阅，负例和原生正例均通过 `Scene.GameTick()` 执行。
2. **损坏的 prop 数据被误报为空地图加载成功。** `InitializeCells()` 捕获解码异常后清空 cells，而 `OnLoad` 将空 cells 当成成功。现在保留初始化失败状态，在空列表成功分支之前阻止就绪。非法 Base64、错误签名、未知版本、截断头及第二条记录截断均已复现并覆盖；正常空列表仍可完成加载。
3. **序列化或重新启用带入旧 ready 标记。** 旧发布逻辑看到已有 tag 就返回，没有验证新实例。现在在 `OnLoad`、`OnEnabled` 清除继承状态；启用与禁用两种克隆都必须重新验证。
4. **世界碰撞失效后仍保留 ready。** `ModelCollider` 可以保持 Active，但模型已被清除，原生 shape 已消失。现在先检查有效模型及物理数据，再持续检查之前验证的原生 `PhysicsBody` 是否有效、有 shape 且仍属于同一个 collider。依赖禁用、切换或数量变化时重新验证；碰撞恢复后可重新发布。

稳态每帧只检查依赖与已验证世界 body，不重复扫描全图物理体。模型缺失或预加载失败时也直接退出。首次发布及依赖变化时仍核对物理世界中的世界 body 与 resident prop body 数量。

没有修改 payload v6 的字段布局或场景路径。已有缓存可以继续读取；损坏数据不再被当成成功。

## 检查与复现证据

- 标准 `SboxBuild build --config Developer` 成功，挂载 Release 项目 0 warning / 0 error；完整构建的 launcher 发布仍出现既有 MonoMod 单文件警告。
- 使用 `SboxBuild test --no-build --filter 'FullyQualifiedName~Titanfall2'`，最终 57 / 57 通过：Engine 54 项，Integration 3 项，覆盖挂载、CM 解析、独立 brush 原生物理和 ready 生命周期。其余过滤后没有匹配用例的分组不计入通过数。
- `Titanfall2MountTests` 增加全挂载回调订阅检查，防止后续 `OnUpdate` / `OnFixedUpdate` / `OnPreRender` 再次遗漏接口。
- 原生就绪测试使用代码生成的 box 世界碰撞及空 resident prop 列表：首次 GameTick 发布，真实射线命中；清除世界模型后撤销且射线不命中；恢复后重新发布；prop streamer 与 publisher 禁用时撤销。
- 本次五个 C# 文件的 whitespace / 行尾格式检查通过。
- Steam SDK 使用独立 `Build/Steam.csproj` 编译；仅产生工作区构建结果。

红灯日志：`.tmp/tf2-ready-tests-red.log`（缺少更新订阅与旧 tag）；`.tmp/tf2-ready-tests-intermediate.log`（仅补接口后，五种损坏数据、旧 tag 和碰撞丢失仍失败）。最终日志：`.tmp/tf2-ready-build-final.log`、`.tmp/tf2-ready-tests-final.log`、`.tmp/tf2-ready-steam-build.log`、`.tmp/tf2-ready-format-final.log`。最终计数和二进制指纹见 `.tmp/tf2-ready-verification.json`。

## 部署与剩余边界

源码引擎的构建按项目规则更新 `game/mount/titanfall2/titanfall2.dll`。最初发现的 Steam `ttf2` 编辑器 PID 8312 在本次工作期间已退出，随后确认没有 s&box 实例。本次没有关闭用户编辑器、修改游戏项目或重启编辑器；确认退出后完成 Steam 挂载部署。

### 部署脚本强制全编译的问题

初次运行 `Deploy-Steam.ps1` 时，固定 `-f` 导致未变化的 cutout 变体持续优化数分钟。核对部署前备份后确认：41 份 shader / include 源文件全部相同，SDK `Sandbox.Engine.dll` 指纹也相同，且旧编译缓存存在。停止本次任务自己启动的 ShaderCompiler 后，脚本非零退出并恢复了全部旧文件；旧 DLL SHA256 也核对为原值。该中断不能算成一次成功编译。

脚本现在默认使用原生编译器的增量依赖检查，而不是自行跳过验证；新增 `-ForceRecompileShaders` 供明确需要强制重编译时使用。重新执行正式部署成功：28 条 shader 中 24 条确认 up to date，4 条重编译成功，编译阶段 6 秒。44 个部署文件全部再次核对 SHA256。

- Steam 备份：`D:/Game/steams/steamapps/common/sbox/mount-backups/titanfall2/20261029-201758-871/mount`。
- 源码 SDK 构建 / 部署 / 原生集成测试 DLL SHA256：`20831BAA3FBEE2172FF60FF1CDDA2834C349C60BC46D8806815F70705577BBE2`，文件 MVID `36e6b153-fa18-4837-bae2-379f0d2f2f5f`。
- Steam SDK 构建 / 实际部署 DLL SHA256：`FB63DE0D7B82043A0B76EB7EBE289C322177E73CE436812DCD18255E4B7F6323`，文件 MVID `34c9e96f-4e4a-4774-8d5f-4cfb33ec5eb6`。

文件 MVID 由 PE 元数据读取；本次没有将其冒称为新 Steam 编辑器进程内的运行 MVID。强制编译中断日志保留在 `.tmp/tf2-ready-steam-deploy-forced-aborted.log`，成功部署清单另复制到 `.tmp/tf2-ready-steam-deployment.json`。

本次原生测试包含真实物理体与 trace，但使用受控 fixture，没有重新加载整张零售纯世界地图。Steam 新进程、实际整图首次 ready、地图两端 trace、多人和 dedicated server 仍需独立验收。尚未实现任意外部操作删除 resident prop 物理体后的持续逐体审计；现有发布检查继续使用 preload 状态及原生 body 数量。

此次只运行 Titanfall 2 过滤后的针对性套件，不代表全仓库 CI 全通过，也不验证 player / titan / bullet / grapple 查询语义或 SP 像素还原。
