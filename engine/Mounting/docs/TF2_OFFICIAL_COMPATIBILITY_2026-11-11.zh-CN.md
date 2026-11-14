# Titanfall 2 挂载适配官方 s&box

目标安装：`D:\Game\steams\steamapps\common\sbox`。

目标版本：`26.11.10`，构建号 `42902774234`。官方 `Sandbox.Engine.dll` 的 SHA-256 为 `D2329AF30354D3AF1980BB68C9AA2B3315A890796826F91EE0A1CF658103A14F`。

## 适配

最新挂载使用了本地引擎增加的 `ResourceLoader.IsLoading`、`ReleaseCachedResult()` 和 `SceneLoader.ReleaseCachedScene()`。当前官方程序集没有这三个接口，直接编译会产生 9 个错误。

`Resource/LoaderCompatibility.cs` 为官方构建补齐对应语义：检查加载中的任务，使用加载器已有的锁清除结果缓存，并通过引擎的场景销毁路径注销旧 SceneFile。该兼容实现只在释放资源时使用；源码版构建继续调用原来的引擎接口。内部字段有类型检查，避免布局改变后静默释放错误资源。

`SboxBuild build-titanfall2 --engine <安装根目录> --output <新目录>` 使用目标安装的程序集构建，打包桥接库及生产着色器源码，并记录依赖哈希。不会把开发引擎程序集打进挂载包。官方工程和测试放在挂载的 `Build/` 下，由原工程的排除规则隔离。

## 已完成的检查

- 官方程序集构建成功，0 警告、0 错误。
- 源码版程序集的隔离编译成功。
- 3 项官方缓存回归通过：释放后重建、加载途中延后释放、其他线程持锁时保留缓存。
- 在官方引擎的独立复制目录中，用官方 ShaderCompiler 编译全部 30 个生产着色器，全部成功。已有 DoAtmospherics 弃用警告不影响此次编译。

原生资源/场景验证尚未完成：隔离启动缺少内置 addon 配置而中止，未取得真实地图、材质或画面验收结果。用户随后要求直接部署且不备份，因此未继续此项运行验证。

## 部署

用户明确授权部署，并要求不备份。已于 2026-11-11 将官方适配 DLL、两个桥接库、生产着色器源码及 30 个由官方编译器生成的 `.shader_c` 部署到目标安装的 `mount/titanfall2/`。部署时没有运行中的官方 s&box 实例；未启动游戏。

最终包：`.tmp/tf2-official-20261111/ready/`。构建、缓存测试、着色器编译和部署清单位于 `.tmp/tf2-official-20261111/`。安装目录内的 `compatibility.json` 记录构建依赖和文件哈希，`deployment.json` 记录部署时间及来源，`backup` 为 `null`。

这次结果表示官方版编译适配与磁盘部署完成，不代表整图渲染、连续切图、碰撞或性能已验收。
