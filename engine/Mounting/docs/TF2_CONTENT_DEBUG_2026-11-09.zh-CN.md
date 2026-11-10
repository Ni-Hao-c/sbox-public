# TF2 内容解析与缓存补充调试（2026-11-09）

## 范围与状态

本轮按用户要求转查其他挂载问题，保留已部署的调色、Bloom、水和粒子方案。生产修改仅在 Titanfall 2 挂载项目：修正地图补充包选择，以及 STARPAK 依赖参与快照修订。没有修改引擎源码、shader、游戏安装文件或 Northstar 配置。

源码修正已通过正式托管构建、35 项定向检查和修改文件的格式验证。修正尚未部署；主运行目录仍是上一轮调色与 Bloom 版本。

## 确认并修复的问题

### 地图补充 RPAK 未被自动选择

本地参考 `D:/northstar/NorthstarLauncher-main/primedev/core/filesystem/rpakfilesystem.cpp` 的 `h_LoadMapRpaks` 会分别请求 `<map>.rpak` 和 `<map>_patch.rpak`，`FixupPakPath` 在原版缺少对应包时从 Mod 中补足。挂载原先只处理前一个名字。

修正后，两个名字均进入固定地图的请求集合；原版缺包时选择 Mod 的对应包，同 Mod 的 Postload 闭包也能跟随补充包激活。原版已有同名包时不因这条自动规则选入 Mod 副本；未选择地图、也没有 MapOnly 上下文时不自动载入地图包，其他地图的补充包仍保持推迟状态。这里的 `_patch` 是文件名后缀，与 `(01).rpak` 等版本补丁编号不同。

新增用例在旧实现下应选 3 包而实际仅选 1 包；修正后通过。本机已安装 Mod 中没有发现 `_patch.rpak`，因此本项是参考实现与用例确认的兼容缺口，尚未绑定到某张已安装地图的画面异常。

### 实际 STARPAK 依赖未全部参与内容修订

RPAK 的 `FreezeSources` 原先已固定 STARPAK 的实际路径与文件版本，防止当前会话读入更换后的来源；但快照修订只另外扫描预定目录。原版包目录扫描不递归，而读取器允许解析子目录和游戏相对位置，所以这些实际依赖可能没有参与快照修订。

这会让新快照在依赖被替换，或原先缺失的依赖后来补齐时，仍保留旧修订，进而让解码贴图缓存使用相同来源键。

`FreezeSources` 现在将实际冻结的 RPAK 版本、各 STARPAK 引用及其解析版本或缺失状态返回给发现层，由同一份结果参与快照修订。没有增加文件内容复制或每帧文件检查。依赖版本继续沿用路径、大小和修改时间的既有契约；本项不增加对同大小、同修改时间内容替换的检测。

两个新增用例在旧实现下均复现修订未变化；修正后通过，并确认旧会话仍不会中途采用新补上的文件。

## 验证

- 隔离工作区：`.tmp/tf2-refactor-20261106/workspace`，生产源文件由主工作区同步。
- `SboxBuild build --config Developer --skip-native` 成功。快速挂载项目构建为 0 警告、0 错误；正式构建仍有既有 launcher 的 MonoMod 单文件发布警告。
- 修改的 3 个生产文件和内容测试文件通过 `dotnet format --verify-no-changes`；`git diff --check` 通过。
- `ContentSnapshotTests` 和 `TexturePreparationTests` 合计 35 项通过、0 失败、0 跳过，包含 3 项新增回归与实际安装目录审计。
- 实际安装目录审计读取 `D:/Game/steams/steamapps/common/Titanfall2` 和其 `R2Northstar`，固定地图上下文为 `mp_angel_city`。索引 236,781 个候选、104,988 个最终条目；抽查的 Mod 模型和 RPAK 纹理解码成功。地图条件包仍按所选地图推迟，不能据此宣称其他 Mod 地图完成实图验收。
- 构建输出、隔离挂载候选和测试使用的挂载 DLL SHA256 均为 `1F5160990559035DABF971F6E63F8D26F9F1FAA7AEA0DAE790016F8935372BC1`；测试记录 MVID 为 `91815442-2a1d-48c6-84b3-6f1791f90639`。
- 主运行目录 DLL SHA256 仍为 `8A4CDE01835AC9D845242AEC721BB1794CD7D95A3B68C348B73A7796A4DB8F54`，未覆盖上一轮部署。未发现运行中的 s&box 进程。

日志与本地数据报告在 `.tmp/tf2-content-debug-20261109/build.log`、`tests.log` 和 `installed-inventory.json`。本轮未运行原生图形检查，也未进行地图画面、连续切图或性能验收。
