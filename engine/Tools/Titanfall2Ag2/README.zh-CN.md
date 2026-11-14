# Titanfall 2 模型与 AG2 资源转换工具

独立的 .NET 10 工具项目，不启动 s&box、不依赖运行中的挂载，也不替换引擎 DLL。读取零售版 MDL53/VPK，输出可复制到下一个 s&box 项目的模型、骨架和普通动画片段。解码器通过源码链接与现有挂载共享；项目自身不引用引擎程序集。

## 构建和验证

从仓库根目录执行：

```powershell
# 设置后，测试会调用该安装的官方资源编译器，验证模型、骨架、动态/单帧动画。
$env:TF2AG2_TEST_ENGINE = 'D:\Game\steams\steamapps\common\sbox'
dotnet run --project engine/Tools/SboxBuild/SboxBuild.csproj -- build-titanfall2-ag2
```

工具位于 `Cli/bin/Release/net10.0/Titanfall2Ag2.exe`。复制工具时须保留整个输出目录，包括 `.deps.json`、`.runtimeconfig.json`、Core DLL 和 `titanfall2_lzham.dll`，目标机器需安装 .NET 10。`--format` 可在检查前整理本项目的代码格式。未设置 `TF2AG2_TEST_ENGINE` 时，原生编译测试会明确显示为跳过。

在构建命令后增加 `--output <新目录>`，会另生成 `Tool/` 可运行工具和 `Source/` 独立源码快照；快照含解码器源码及本地路径配置，会再次构建检查，不再依赖原仓库。源码快照的常规构建方法见其中的 `BUILD.md`。

## 读取和转换

```powershell
$tool = 'engine/Tools/Titanfall2Ag2/Cli/bin/Release/net10.0/Titanfall2Ag2.exe'
$common = 'D:\Game\steams\steamapps\common\Titanfall2\vpk\englishclient_mp_common.bsp.pak000_dir.vpk'

& $tool list --vpk $common --match 'stalker'
& $tool inspect --vpk $common --model 'models/robots/stalker/robot_stalker.mdl'

& $tool convert --vpk $common `
  --model 'models/robots/stalker/robot_stalker.mdl' `
  --sequence 'st_idle' --sequence 'st_combat_idle' `
  --cache 'D:\sbox\exports\Titanfall2Ag2\cache' `
  --engine 'D:\Game\steams\steamapps\common\sbox' `
  --output 'D:\sbox\exports\Titanfall2Ag2\stalker'
```

- `--vpk` 可重复，先提供的 VPK 优先；必须传目录 VPK（`*_dir.vpk`），数据卷按原安装布局读取。
- `--root` 指向解包后的内容根目录，例如其中有 `models/`。同时传入 VPK 时，松散文件优先。不会自动读取 Northstar Mod 或修改其配置。
- 支持 `mount://titanfall2/models/…mdl.vmdl` 形式的模型路径；它会被还原为原游戏路径。
- include 动画模型递归解析，缺少依赖时报错；不会用不完整输入制造成功缓存。
- `--sequence` 是原序列名称，可重复。省略时尝试导出所有普通序列；混合序列内的多个源动画分别导出为 `名称__blend_N`，不会自动恢复原混合规则。
- `--body` 是原游戏 packed body 值，`--skin` 是皮肤索引，默认均为 0。
- 不传 `--engine` 时仅导出可编辑源文件。传入时另调用官方 `resourcecompiler.exe`，生成编译资源。
- `--output` 导出一份缓存包的副本，目标必须尚不存在，以保护你在已导出资源上的修改。省略它时资源仍完整保存在缓存条目内。

## 资源包

```text
manifest.json                      输入哈希、编译版本、文件校验、动作名称和路径、限制
IMPORT.md                          导入说明
compile.log                        官方编译结果（启用编译时）
Assets/models/tf2ag2/<内容标识>/
  model.dmx                        LOD0 网格、法线、UV0、蒙皮和骨架
  model.vmdl                       可编辑 ModelDoc 模型，引用同一份 .skel
  model.skel                       AG2 骨架描述
  model.data.json                  骨骼参考变换、网格与材质名称、源序列信息
  model_materials/                  可编辑中性占位 .vmat，保留槽位映射
  model_animations/
    clip_0000.dmx                  四元数动画通道
    clip_0000.anim                 绑定上述骨架的 AG2 片段描述
  …_c                              官方编译结果及生成纹理（启用编译时）
```

把输出包 `Assets` **里面的内容**复制到下一个项目的 `Assets` 中，保留所有相对路径。资源描述内没有原 TF2 安装路径、缓存的绝对路径或 `mount://` 依赖；`manifest.json` 中保留原始输入位置作为来源记录。

新版本引擎应从源文件重新编译，而不是强行复用旧版本 `_c`。在 ModelDoc 中打开 `model.vmdl`；在 AG2 创建图时选 `model.skel`，从 `manifest.json` 的 `Clips` 查找动作名称和片段路径。`examples/PlayExportedClip.cs.txt` 是可选播放组件，可复制为项目内的 `.cs`，指定 Renderer、模型和 Clip 即可播放。

## 缓存行为

缓存键包括模型及全部 include 的内容 SHA-256、工具程序集哈希、body/skin/序列选择，以及引擎版本和相关编译二进制哈希。命中时逐个校验输出文件，缺失或修改会触发重建。每个键有独占文件锁；编译完成并确认输出存在后才发布清单。失败的 `.work-*` 目录保留用于排错，不作为可用缓存；被修改的完整条目保留为 `.invalid-*`，避免覆盖手工修改。

## 当前范围

- 已导出 LOD0 和所选 body/skin；没有重建全部 LOD、可切换 bodygroup、碰撞、morph、UV1/顶点色、附件/hitbox、动画事件、IK 规则或原游戏行为图。
- 材质是中性占位文件，**没有原始 RPAK 贴图和原材质外观**。原材质名称与槽位可在清单和数据文件中查到，便于后续替换。
- 普通动画保留帧率和骨骼姿势；循环设置记在清单里，播放图应按需使用。根位移仍保留在姿势里，尚未进行 AG2 root motion 提取。
- 默认批量导出会明确记录跳过的 additive 动作；显式指定 additive 时会报错，避免把叠加差值当作完整姿势。
- 正的统一骨骼缩放写入 DMX。非统一缩放、非有限变换和无效骨架会明确拒绝，避免静默丢失数据。
- 源动画按名称映射到目标骨架，未匹配的目标骨骼保持参考姿势并写入警告；这不等于任意骨架重定向。
- 官方编译通过只证明资源被编译器接受；完整人物画面、动作匹配和项目内播放仍需实际查看。

纯内存构建 AG2 骨架/片段不在本项目本次范围内。
