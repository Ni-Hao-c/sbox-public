# Titanfall 2 静态道具职责拆分

## 改动范围

生产改动限于挂载目录。保留现有 GPU Scene、CPU 回退、混合模型特殊网格、碰撞语义和场景序列化字段；没有修改引擎源码、interop 或生产 shader。本轮目标是加载与资源生命周期，不以帧率提升作为结论。

## 数据编译与加载

`Resource/StaticPropSceneData.cs` 负责 CPU 数据预处理：道具记录、稳定实例 ID、model/skin 分组、空间 cell 排列、PVS 表、碰撞元数据、fade 距离、灯光编码以及校验和。保留 TSP2 v9、TSV2 v2 和旧版场景读取能力。它不创建 Model、SceneObject、碰撞体或 GpuBuffer。

`MapLoader` 在创建道具组件之前调用该编译器，将结果通过 `ConfigureCompiled()` 写入场景模板。原来的公开 `Configure()` 仍可使用，转发到同一编译器。模板只保存编码数据；场景复制恢复不依赖跨场景共享的原生引用。

`InitializeCells()` 仅恢复 CPU 数据、空间结构和工作队列。原来在此处调用的灯光 GPU 缓冲恢复，移至组件 `OnLoad()` 的资源准备事务。空道具场景不会分配渲染缓冲；专用服务器和没有图形设备的场景也不会创建这部分 GPU 资源。

需要已解析模型几何和材质的 GPU Layout 仍在场景加载阶段编译，之后由渲染持有者上传。没有把显存分配提前到挂载初始化，也没有将 GPU Layout 或运行时材质引用序列化进地图。

## 资源与碰撞所有权

| 对象 | 职责与寿命 |
| --- | --- |
| `Titanfall2StaticPropSceneData` | 地图编译／场景恢复的纯数据处理。 |
| `Titanfall2StaticPropRenderResources` | 每个场景单独持有 GPU Scene、灯光表、原生剩余网格和 CPU 回退批次。构造函数不分配原生资源，准备操作幂等，释放操作幂等。 |
| `CollisionRuntime` | 拥有碰撞队列、游标、加载／运行预算、计数、距离迟滞和 Ready 状态；负责创建、启停和释放碰撞实例。 |
| `Titanfall2StaticPropStreamer` | 保留序列化组件入口，协调加载、CPU 剩余对象可见性和上述两个持有者。 |

`CollisionRuntime` 是独立工作对象，嵌套在组件类型中以使用私有实例记录，不增加对游戏开放的引擎 API。物理体与渲染对象的释放分开处理。cluster 和 instance 对原生渲染对象仅保留借用引用，由渲染持有者统一删除，之后再释放这些对象依赖的灯光缓冲。

运行时更新只协调相机、CPU 剩余对象及碰撞工作；GPU Scene 创建明确限制在加载事务。加载发生异常时走与销毁相同的幂等资源清理，清除 Ready 状态，避免留下部分加载的原生资源。

## 官方接口的限制

本轮保留 `GpuSceneCompatibility.cs`，没有将其删除后静默回退到 CPU。

针对本次实际测试的官方程序集核查：

- `CommandList.ExecuteOnRenderThread()` 非公开。
- `CommandList.AddAction()` 非公开。
- `Sandbox.Clutter.ClutterBatchSceneObject` 非公开。

官方 Clutter 的内部批次类目前不能作为挂载的公开替换接口。公开的相机 CommandList 插入接口也不能直接等价替换现有按场景对象、按视图和阴影 pass 执行的路径。删除现有适配层会失去当前 GPU Scene 路径，而不是自动转用官方 Clutter。

另一个 `LoaderCompatibility.cs` 处理资源加载器缓存释放，和本次 GPU 渲染适配层是两件事；它也未在本轮移除。要彻底采用官方公开接口，需要官方提供对应的场景绘制执行／外部实例输入及加载缓存释放能力，或者明确接受更换渲染与资源释放行为。

## 验证

构建使用 `SboxBuild build-titanfall2` 对实际官方安装的程序集编译，包输出到独立 `.tmp` 目录。没有覆盖安装中的挂载 DLL、引擎 DLL 或 shader 编译产物。

`Build/Official.Tests/StaticPropSceneDataTests.cs` 覆盖稳定 ID、空路径过滤后的 fade 对齐、skin、碰撞元数据、独立解码、校验失败以及空地图；与已有官方兼容和零售地图检查合计 12 项通过。

`Build/StreamerValidation/` 是独立原生验证程序，使用与编译相同的引擎安装，覆盖：

- 数据恢复不创建 GPU Scene／灯光缓冲。
- 克隆独立持有资源，空地图 Ready 语义保持。
- 重复准备不重复上传，原生对象由持有者删除，重复释放安全。
- 无效灯光负载使加载事务失败，并清理资源、拒绝 Ready。
- 实际地图加载、等待异步预载完成、关闭／启用、绘制、销毁和再次加载。

`sp_boomtown_start` 的两次原生加载结果：

| 指标 | 第一次 | 第二次 |
| --- | ---: | ---: |
| 静态实例 | 13,948 | 13,948 |
| GPU 实例 | 13,924 | 13,924 |
| 同时保留特殊网格的混合实例 | 1,450 | 1,450 |
| MDI 提交组 | 3 | 3 |
| draw record | 2,028 | 2,028 |
| 常驻碰撞体 | 9,186 | 9,186 |
| 明确过滤的碰撞实例 | 6 | 6 |
| 失败道具 | 0 | 0 |

测试程序必须等待组件预载完成；`Scene.Load()` 返回不表示异步 `OnLoad()` 已完成。最初的即时 Ready 断言因此失败，测试已改为推进引擎任务并等待加载事务，再进行验收。

无图形设备模式也通过了数据恢复、克隆和资源持有者检查，确认不创建灯光 GPU 缓冲。无效灯光加载失败用例在有图形设备模式执行。原生验证程序保存并恢复 `core/cfg/video.txt` 和 `config/video.json`，避免测试进程初始化设备时改写用户配置。

源码版挂载条件分支以现有本地引擎程序集隔离编译成功，0 警告、0 错误。当前原项目缺少依赖工程的 Release ref 产物，跳过依赖构建的尝试不能运行；本轮没有为此清空或重建用户的引擎输出。因此隔离编译不等于完整引擎构建通过。

官方测试安装版本为 `26.11.10a`，构建号 `43053683282`，实际 `Sandbox.Engine.dll` SHA-256：`47002EDEB8068508C62FC70FE01D85FEF6B5687D50BED6A515A629A076CFAA50`。

整图原生测试实际加载的挂载 MVID：`6ae514b7-5294-4d68-8457-e195435693f6`。独立包 `package-final/titanfall2.dll`、原生整图测试 DLL、无图形设备最终测试 DLL 的 SHA-256 相同。尚未部署该包。

构建、格式、测试和原生证据位于 `.tmp/tf2-streamer-20261112/`，其中 `native-final.json` 为整图结果，`native-headless-final.json` 为无图形设备结果。整图像素一致性、其他地图、带有效 PVS 的实图、非居留碰撞模式及性能 A/B 不在本轮已验证结论内；本图本次解析的 PVS 为空。
