# Steam SDK 兼容与部署（2026-10-29）

## 当前状态

**最新 CPU 提交版本：** 已部署深度预通道探针绑定优化，Steam DLL SHA256 `B299A29DF26B237FF4709CA74A8B4B7BA2EDDA8A3DC9DF933471827A5676821F`，运行 MVID `1511830a-817e-4e9a-b70f-2ac95d216814`。155 项 GPU 与 57 项挂载回归通过，当前重开的 ttf2 编辑器已核对加载该版本。实测与限制见 [DEPTH_BINDING_PERFORMANCE_FIX.md](DEPTH_BINDING_PERFORMANCE_FIX.md)。

**最新性能版本：** 已部署实例探针 identity 索引复用优化，Steam DLL SHA256 为 `D8AF1416C603757251F51E40489242531E05FA8621227A76A24CB1334B32E01C`，运行 MVID `d7b1cbd1-9e47-453f-95f8-4bd700dde6f6`。天使城同进程有效对照、137 项 GPU 与 57 项挂载回归详见 [PROBE_UPLOAD_PERFORMANCE_FIX.md](PROBE_UPLOAD_PERFORMANCE_FIX.md)。下方 ready 阶段指纹为历史记录。

**2026-10-29 ready 后续：** 就绪调度、损坏数据及碰撞生命周期已继续修复，57 / 57 针对性回归通过。原编辑器退出后，已完成 Steam 44 文件部署、备份与哈希复核；28 条 shader 中 24 条增量检查通过、4 条重编译成功。最新证据见 [READY_LIFECYCLE_FIXES.md](READY_LIFECYCLE_FIXES.md) 与 `.tmp/tf2-ready-verification.json`；下表 CM 阶段的构建指纹保留为历史记录。

需求来源：`D:\sbox\ttf2\Docs\mount-requirements.md`、`plan.md`。

本轮用实际 Steam 安装 `bin/managed/*.dll` 编译，修复了 `ParticleSpriteRenderer.ColorRamp` 不存在导致的 CS1061。
源代码 SDK 与 Steam SDK 均为 Release 0 warning / 0 error。新增纯世界路径、常驻 prop 碰撞与就绪组件也包含在当前构建中。

**编译通过不代表 Steam 实图验收完成。** 先前保留的 Steam 编辑器现已退出，本次未关闭用户进程；确认没有实例后完成部署。新进程纯世界、整图 ready 与 GPU 取样仍待验收。

| 项目 | 证据 / 状态 |
| --- | --- |
| 编译目标 | `D:\Game\steams\steamapps\common\sbox`，用户指定 Steam 26.10.28 |
| Steam `Sandbox.Engine.dll` SHA256 | `DD147404CA3FCC7AD1BBD699D6F22D6269BA8C62DF391CDF617EF41691EA3271` |
| ready 修复阶段 Steam 构建 / 部署 DLL SHA256 | `FB63DE0D7B82043A0B76EB7EBE289C322177E73CE436812DCD18255E4B7F6323`，最新性能版本见上方 |
| CM 修复阶段工作区 Steam 构建 DLL SHA256 | `320E5DD433EFCF3B80EBAAF9DE1C05C162C5BB994AFB99CC674D23AE88AB379B`，含 CM 范围与位宽纠正；最新 ready 构建见上方后续记录 |
| 本任务首轮 Steam 部署 DLL SHA256 | `7B5D30F0D7F7A66D5E2E6506B651E46EFC2195D6EC9D7C1ECE997EA91E54023F`，尚不含后续纯世界及 CM 补充 |
| 原旧版 DLL SHA256 | `F396FC3078F9B3A6B568695E3255CEEE712E5326EB282B81CCC939D6E1987D24` |
| 首轮部署 | 44 个文件逐个 SHA256 比对成功；编辑器日志已进入战争游戏并保留 8931 个静态 prop |
| 回退备份 | `D:\Game\steams\steamapps\common\sbox\mount-backups\titanfall2\20261029-143248-869\mount` |
| 针对性测试 | mount / 生命周期 16 项 + CM 读取 20 项，共 36 / 36；其余测试项目没有匹配用例，不代表全仓库测试通过 |
| 原版碰撞数据矩阵 | 23 MP + 13 SP = 36 图离线通过，见 CM_COLLISION_CORRECTIONS.md；不代表实际物理场景验收 |
| 粒子 ramp shader | 工作区正式编译 VS / PS 成功；初版 `Fog` 变量与引擎 `Fog` 类型冲突已修为 `FogStrength`，现已随最新版本部署并由 Steam 编译器检查通过。GPU 取样仍待做 |

当前构建位置：`Build/bin/Release/net10.0/Sandbox.Mounting.Titanfall2.dll`。不要误用源代码 SDK 的 `bin/Release/` DLL 替换 Steam。

## 一条重新部署命令

关闭目标安装的编辑器、游戏以及残留 `ShaderCompiler` 后，从仓库根目录用 PowerShell 7 执行：

```powershell
pwsh -NoProfile -File .\engine\Mounting\Sandbox.Mounting.Titanfall2\Deploy-Steam.ps1 -EngineRoot 'D:\Game\steams\steamapps\common\sbox'
```

脚本不关闭进程：发现目标安装正在使用时会停止。第一次实际部署遇到了编辑器退出后残留的 ShaderCompiler 映射旧 DLL，预检现已覆盖此进程和三份 DLL 的独占打开测试。

流程为：读取指定 SDK → 无外部包源 restore → 独立 Release 编译 → 验证部署清单 → 完整备份旧 mount → 复制并逐文件比较 SHA256 → 用目标安装的 ShaderCompiler 检查并增量编译挂载 shader → 写入 `deployment.json`。需要强制全编译时追加 `-ForceRecompileShaders`；默认仍由原生编译器验证依赖，不是直接跳过 shader 检查。

`Build/Steam.csproj` 不引用源码引擎项目，不覆盖源引擎 managed 输出。`Deploy-Steam.ps1` 不修改 Steam 核心 DLL、核心 sprite shader、游戏项目或用户配置。
当前脚本的完整 Steam 重部署已在原编辑器退出后通过。首次强制编译在未变化的 cutout 上耗时数分钟，本次停止自己启动的编译器并验证回滚，然后改为原生增量检查，最终 28 条全部通过。复制、哈希或 shader 编译失败时，会从备份恢复原有文件并报错（首次安装没有旧版可恢复）；新增但旧版未使用的文件会保留。错误会提供备份位置，不要把脚本退出失败视为完整部署成功。

## 部署清单

安装目标：`<Steam sbox>/mount/titanfall2/`。

- `titanfall2.dll`：必须使用同一次 Steam SDK 编译产物。
- `titanfall2_lzham.dll`：VPK 原生解码桥。
- `titanfall2_miles.dll`：Miles 解码桥。
- `Assets/shaders/**/*.shader`：生产 shader 源码。
- `Assets/shaders/**/*.hlsl`：全部生产 include，含 `common/imported_height_fog.hlsl`。
- 目标 ShaderCompiler 生成的 `.shader_c` 及相关依赖信息。
- `deployment.json`：SDK 指纹、备份位置及已复制源文件指纹。

不复制源 SDK 的 `.shader_c`，测试 shader 目录也不进入生产清单。原 Steam mount 可能有历史测试文件；脚本不会删除它们。

特别注意：原先 `common/imported_height_fog.hlsl` 只在源码引擎 `game/core` 中存在，Steam 并没有这份文件。
现在挂载 Assets 内提供同内容的 include，避免只复制 DLL 后雾效 shader 依赖缺失。两份 helper 后续修改时需要同步。

## ColorRamp 兼容方法

Steam 的 sprite 结构和本地改动过的 sprite 结构不同，不能将源码引擎的核心 sprite shader 整包覆盖到 Steam。

`Titanfall2ParticleRampRenderer` 仅接管含 `$ramptexture` 的 SpriteCard，其他粒子仍使用原生 sprite renderer。它使用两个 SDK 共有的公开 API，按系统构建一个 draw，保留 HDR tint、sheet 序列、帧混合后 RG 查表、alpha / additive、深度软化、相机淡出和雾。

已处理非正方形纹理与偏心 pivot 的包围球，避免新渲染路径在转动视角时被过小 bounds 裁掉。实际运动、方向、排序、灯光以及粒子克隆 / 重启仍需 GPU 回归，不能只凭 C# 与 shader 编译认定视觉完全等价。

## 下一次 Steam 运行验收

1. 最新版本已部署并记录 DLL SHA256 / 文件 MVID；下一次启动时核对进程内 MVID。
2. 新进程确认来自 Steam 安装，编译生产 shader，检查没有缺 include / shader 或 MissingMethod / TypeLoad 错误。
3. 同时验证旧 `.bsp.scene` 与新 `.bsp.world.scene`；新路径不能出现 SpawnPoint、MapCamera、观察者、导航生成或动态剧情道具。
4. 记录 `ttf2_map_ready` 首次出现时刻，确认世界及 prop 物理体已可查询；地图两端各做地面 / prop trace，移动相机后重复。缺失碰撞模型应阻止就绪，不能偷偷发布成功。
5. 做 ramp 粒子的 GPU 取样：非线性 ramp 的帧混合、HDR、透明排序、软交界、镜头旋转、clone / disable / re-enable。
6. 再做天使城、战争游戏、`sp_training` 与 `sp_tday` 的固定机位、移动和重载对照。记录加载时间、内存、物理体数；常驻碰撞可能增加首次加载成本。

以上待验收项与本次 57 项针对性回归分别记录，不能由编译与受控 native fixture 推断已完成整图验收。
