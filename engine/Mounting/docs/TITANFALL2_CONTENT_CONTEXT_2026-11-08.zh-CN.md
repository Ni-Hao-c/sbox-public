# Titanfall 2 不可变内容上下文与 Northstar 资产接入

日期：2026-11-08。实现位于 `Sandbox.Mounting.Titanfall2/Content/`。当前工作区原有修改保留；本批不改宿主 ABI、引擎其他源码或 shader，不部署主运行目录 DLL。

## 1. 当前接入

```text
安装目录 / Northstar profile
  → 发现启用的 Mod、包、loose 文件与逻辑资产
  → Titanfall2ContentSnapshot（固定修订、全部条目、winner、冲突候选、GUID 索引）
  → PrepareContentCatalog（只选择已有 decoder 能转换的资源）
  → MountContext.Add
  → 现有 Model / Material / Texture / Map / Sound loader
```

虚拟目录与引擎目录分离：`.vvd`、`.vtx`、`.phy`、BSP lump sidecar、实体分区和脚本仍在原始目录；RPAK 未实现 decoder 的类型以 `Kind + _guid/<id>` 保留元数据，不伪装成可加载模型或 UI。

内容快照使用冻结字典和只读集合，源优先级在发布前确定。绑定到文件的 source 持有它自己的快照：模型 companion、动画 include、BSP 分区和 sidecar、VMT 源、PCF 与 soundscape 不再由当前文件所在物理包自行决定依赖。依赖可命中另一个包或高优先级 Mod。缺失/损坏 winner 不自动改读低优先级副本。

RPAK 的 GUID、shader 与资源注册表从同一快照派生；材质转换、GPU 渲染及引擎纹理创建仍使用现有实现。Miles bank 在内容发现阶段索引，其逻辑声音从快照注册。显式 Mod WAV 路径覆盖优先于 Miles 查找；Northstar 自定义 audio JSON 重映射另见限制。

## 2. Northstar 依据与范围

主要核对用户指定的 `D:/northstar/NorthstarLauncher-main/primedev/mods/modmanager.cpp`、`mods/mod.cpp`、`mods/modmanager.h` 与 `core/filesystem/{filesystem,rpakfilesystem}.cpp`。旧 nscn-v1.18 仅作为旧配置格式参考。未复制其 C++ 实现、运行启动器或修改其文件。

支持的资产配置：

| 配置/布局 | 行为 |
| --- | --- |
| `<profile>/mods/<mod>/mod.json` | 读取 Name、Version、LoadPriority；支持 JSON 注释、尾逗号和 UTF-8 BOM |
| `<profile>/packages/Author-Package-Version/mods/<mod>` | 按 main 的 Thunderstore 包布局发现 |
| `<profile>/runtime/remote/mods/` | 识别直放 ModWorkshop 与包布局，按 main 首次发现行为禁用 remote 内容，不执行网络下载 |
| 旧 `enabledmods.json` | `Name: bool`，只读解释、不迁移原文件 |
| main `enabledmods.json` | `Version: 1`，按 `Name → ModVersion → bool` 选择；多个版本同时启用时全部禁用并报告 |
| `<mod>/mod/` | loose 文件覆盖与新增内容；只对已索引虚拟路径解析依赖 |
| `<mod>/vpk/vpk.json` | 缺少配置时自动载入；配置存在时按 Preload、MapOnly 或固定地图的 `client_<map>` 包选择。MapOnly 视为 MapOnly 声明的地图专属预载：未指定地图时同样载入并报告诊断，选中其他地图时推迟。键可写 `client_x`、`client_x.bsp` 或完整目录文件名 |
| `<mod>/paks/rpak.json` | Preload、Postload（当前固定载入包集合及同 Mod 闭包）、固定地图正则；Aliases 支持指向本 Mod 或原版包。未指定 `NORTHSTAR_MAP` 时，同 Mod 的 MapOnly 声明为该 Mod 的地图上下文，供这些正则匹配 |
| 自定义地图 RPAK | 指定地图名且原版没有同名包时，可通过同名 Mod RPAK 接入，无需旧式 rpak.json；同时处理 `<map>.rpak` 和 `<map>_patch.rpak`，并接入同 Mod 的 Postload 依赖 |
| STARPAK | 沿用现有读取器，发布前固定其实际位置及文件版本；所有已解析依赖与缺失状态参与内容修订，包括不在平铺包目录中的依赖 |

文件覆盖顺序：全部启用 Mod 的 loose 层优先，Mod 按 LoadPriority 降序；再按同样 Mod 顺序处理 RPAK、VPK；最后为原版 RPAK、原版 VPK、生成天空材质与 Miles。loose 文件优先于 VPK 符合 Northstar 文件替换 hook。相同优先级采用目录 ordinal 顺序，避免依赖操作系统枚举顺序；Northstar 的等优先级排序本身没有稳定承诺。

`Dependencies` 是 Squirrel 依赖常量，不作为版本依赖或文件覆盖顺序。脚本、插件、ConVars、ConCommands 不因被发现而执行。`keyvalues/`、`media/`、`audio/` 等专有变换目录报告未应用；目前不会编译 KeyValues #base、PDEF、播放列表、音频事件替换或原游戏玩法。跨 Mod 的动态包加载/卸载、任意路径 alias 与原游戏脚本驱动的 pak 行为不作完整兼容承诺。

## 3. 配置

默认自动发现实际游戏安装下的 `R2Northstar`，即本机的：

```text
D:/Game/steams/steamapps/common/Titanfall2/R2Northstar/
  enabledmods.json
  mods/
  packages/
  runtime/remote/mods/
```

`D:/northstar/NorthstarLauncher-main` 是参考源码，不是 profile。需要使用其他 profile 时，在引擎启动前配置：

```powershell
$env:SBOX_TITANFALL2_NORTHSTAR_PROFILE = "D:\Game\steams\steamapps\common\Titanfall2\R2Northstar"
$env:SBOX_TITANFALL2_NORTHSTAR_MAP = "mp_angel_city"
```

PROFILE 显式空字符串表示仅原版。MAP 不设置时不激活地图条件包；设置时仅发布该地图的场景入口，避免把专属覆盖用于其他地图。原始虚拟列表仍可列出其他地图文件。没有静默切换同一 mount 地址内容的热更新；新修订需要重启引擎会话。

查询入口：

```text
titanfall2_content_stats
titanfall2_content_list "models/" 0 100
titanfall2_content_explain "models/example.mdl"
titanfall2_content_explain "models/example_material" "matl"
```

`Titanfall2Mount.GetContentStatistics/GetContentFiles/ExplainContent` 为按需查询入口，列表单页最多 1000 条；不在 frame/update 中生成目录快照。

## 4. 快照与缓存边界

- 不可变的是目录、来源选择和解析规则，未把全游戏 payload 复制进内存。
- loose 文件按 SHA-256 捕获，读取时验证；同大小、同时间替换也会失败，直到建立新会话。
- VPK 检查目录及读取涉及分卷的大小/mtime；RPAK、STARPAK 也固定位置与版本。此处不能替代内容摘要，故意保持大小/mtime 的包替换不属于强一致性保证。
- 派生缓存键包含模块 MVID 和完整内容修订。Mod 顺序、manifest、候选源、loose 摘要或包版本变化保守失效；不是精确依赖级最小失效。
- 继续使用既有 RPAK 缓冲预算与引擎资源生命周期；本批不引入显存驱逐或完整跨地图资源所有权图。
- 保留原版引擎资源地址，通过固定进程会话避免同地址串代；不宣称实现了多 Profile 同进程并存。
- 生产挂载不再使用 `LooseAssetSource`/`VpkPathResolver` 决定跨资源搜索顺序；旧读取工具为既有独立测试与调用保留。

## 5. 验证记录

构建和 native 回归在 `.tmp/tf2-refactor-20261106/workspace` 运行，本批日志在 `.tmp/tf2-content-20261108/`。新测试位于模块自身的 `Build/Refactor.Tests/ContentSnapshotTests.cs`，由已有 `Compile Remove="Build/**"` 排除，不修改 engine/Tests。

独立夹具覆盖路径规范、候选/winner、跨层 companion/依赖读取、只读集合、同大小同时间文件替换、旧快照并发读取、Mod 重排与缺失依赖新增、旧/新版本启用清单、包布局、remote 初次禁用、重复版本、VPK 条件、RPAK 加载规则、alias 边界以及虚拟目录/引擎目录分离。

本机原版安装加 `D:/northstar` 额外样本做过只读索引，并读取 Mod 模型头和 RPAK/STARPAK 纹理数据；这不是默认配置。用户指出的实际 `R2Northstar` profile 另行检查，尊重其原有 enabledmods.json。最终数量、耗时和 diagnostics 以本批 JSON 报告为准，不将目录构建时间当作地图加载或帧率结果。

本批未部署主运行目录 DLL、未启动或重启游戏实例，未做 Mod 画面、完整碰撞、玩法、服务器或多地图验收。最终构建/测试/格式结果见同目录验证日志及 `verification.json`。

### 最终结果

| 检查 | 结果 |
| --- | --- |
| SboxBuild Developer 构建 | 通过，使用隔离副本中的 native 预编译产物 |
| 最终模块编译 | 通过，0 warning / 0 error；文件身份辅助下沉到 Formats 后单独重建 |
| 独立模块回归 | 50/50，通过，含本批 23 项内容用例与原有 27 项 |
| 既有内容相关 native 回归 | Engine 73/73，通过；VPK、loose、map import、texture cache、material proxy；其他测试项目没有匹配项，不计为额外覆盖 |
| 实际 R2Northstar profile | 235201 条记录、103803 个 winner；13 个配置项均禁用，Mod winner 为 0，尊重原状态 |
| D:/northstar 额外样本 | 236614 条记录、104431 个 winner；1068 个 Mod winner；读取 Mod MDL 头与 RPAK/STARPAK 纹理数据成功 |
| TF2 模块完整 format verify | 通过（工作区加载有既有 warning） |
| 全仓 SboxBuild format verify | 未通过：未修改的 Helldivers2 文件及既有 Titanfall2MixedPropRenderingTests.cs 存在格式/行尾问题 |

扩大范围时，`Titanfall2MapLifecycleTests.ReloadKeepsHostObjectsAndReplacesMapRootsAndCamera` 在 Engine-attached tier 触发 native `0xC0000005`，堆栈为 SceneCamera → ParticleLightingSystem → Texture3D 创建。该 tier 的 AssemblyInitialize 明确不启动引擎循环/渲染设备；未把这次中止或中止前的 59 项通过计为整组通过。收窄到上述内容相关测试后 73/73 成功。没有修改相机或测试框架来掩盖这项未通过结果。

最后经过执行的模块 SHA-256 为 `9F3409DA656481B8BD6B00AA3AE5BCCE8D91B2044B1E942CA9436BC140ADB4BA`，MVID 为 `4e67c956-e0c8-4ad3-ad30-6c5b74246bfa`；独立测试目录、Engine 测试目录和隔离 mount DLL 的 SHA-256 相同。主运行目录模块保持本批开始时的 `427A0A1E0B89B6BC8F1EAC60674626A0419B23C47B56C553952201614BC343D9`。

证据：`.tmp/tf2-content-20261108/sbox-build-final.log`、`module-build-final.log`、`unit-tests-final.log`、`native-content-regressions-final.log`、`actual-profile-inventory-final.json`、`extra-profile-inventory-final.json`、`module-format-verify-final.log`、`sbox-format-verify-final.log`。目录计时包含当次缓存状态，未与冷/暖缓存基线做受控对比。
