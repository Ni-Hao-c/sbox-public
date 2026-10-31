# Titanfall 2 归档读取审阅与修复

日期：2026-10-30。本轮在工作区既有改动上继续审阅资源入口、归档读取与派生缓存边界；最终修复集中在 `Pak/VpkArchive.cs`。不覆盖整个挂载模块的全部正确性，也不沿用其他报告的 GPU、性能或部署结果作为本轮证据。

## 已修复的问题

### P2：截断的 VPK 目录仍可能成功发布

原实现只在进入目录循环时检查 `stream.Position < treeEnd`。字符串、固定字段、chunk 描述与 preload 可以跨越这个边界；循环到达边界时也不会要求文件、目录、扩展名列表的结束标记。声明的 tree 长度超过文件时，提前遇到空字符串仍可能返回成功。

最小夹具确认：删除全部三级列表结束标记，或将声明的 tree 长度增大到文件之外，旧实现均返回 `true`。另外，文件后方保留原始字节时，将 tree 截在字段中间，读取器可能用边界之外的数据补齐字段。

修复后，header 中 tree/data 范围必须落在文件内；所有目录字段和 preload 必须落在 tree 内；每级列表必须显式结束；最终位置必须与声明的 tree 末尾一致。失败时返回 `false`、空 archive 和错误信息。合法空目录仍可读取。

目录的三级结束标记和有界读取也与独立的 [Titanfall 2 VPK 读取器实现](https://gist.github.com/pg9182/7792ac5b750b063baf24d5d0d043a034) 交叉核对；零售兼容性结论来自下方本机数据实测。

### P2：外部分卷失败可能返回无关的目录文件字节

原实现读外部分卷失败后，无条件尝试 `_dir.vpk` 中 `16 + treeSize + chunk.Offset` 的字节。在夹具中移除外部分卷、保留目录内的 `WRNG` 数据，旧实现仍返回 `true` 和 `WRNG`，掩盖了资源缺失。分卷截断也有同样问题。

修复后，明确按 archive index 选择数据源：外部分卷失败直接报错；内嵌数据读取保留独立路径，并检查声明的数据块范围。内嵌路径不再优先读取同名的 `_32767.vpk`。非标准目录文件名无法推导外部分卷时明确失败，避免字符串替换未命中后误读目录文件自身的 header。

保留既有 payload 路径缓存和输出容量预分配，以及 `englishclient_` 到 `client_` 的文件名解析。

## 本轮验证

- 新增 `engine/Tests/Sandbox.Test.Engine/Scene/Titanfall2VpkArchiveTests.cs`：26 项回归，覆盖字段及结束标记截断、错误长度、合法空目录、preload、多分卷顺序、缺失/截断分卷、非标准命名、内嵌数据源选择与范围。
- 最终 `FullyQualifiedName~Titanfall2`：Engine **90/90**、Integration **5/5**，合计 **95/95**。其他测试进程没有匹配的测试，不计入通过数量。
- 本机零售安装中 **38 个 `_dir.vpk`** 均成功解析，修复前后各目录 entry 数一致。
- 对四张地图完整解码 BSP，修复前后字节数和 SHA256 均一致。此检查经过生产 VPK 读取源码及实际 LZHAM bridge，不启动地图渲染。

| 地图 | BSP 字节数 | SHA256，修复前后相同 |
| --- | ---: | --- |
| `mp_angel_city` | 183090216 | `8FFC97EB1BDA6F11177D9637FF40A7FD8144C2DD0FE088B489CB2148415A4C32` |
| `mp_wargames` | 80345356 | `0A3A91CEE1A0EF7E317966251D3624A723B0C7F8A617AE1A2597B21B5D14B188` |
| `sp_training` | 270258312 | `1AF1145CD55B031722878142932BC4BAEBC9BD9EE6ED3E2C25F65A9E3C25746E` |
| `sp_beacon` | 320042052 | `384AA2F93538B92AE4A72B1F37F665530A6ACBDD619711BB9AC6ACB1B0605FCD` |

`SboxBuild build --config Developer` 最终成功，managed solution 为 0 warning / 0 error；launcher 发布仍有既有 MonoMod 单文件警告。一轮重新构建因远端 public artifact manifest 的 SSL 连接失败中止，随后重试成功；最终测试使用重试后的产物。

全仓 `SboxBuild format --verify` 未通过，其他文件仍有既有空白、行尾和 `Map.World.cs` 的 IDE0062 问题。本轮只格式化两个改动的 C# 文件，随后对它们执行完整 `dotnet format --verify-no-changes --include ...`，通过。没有批量格式化其他文件。

最终 source build、本地 `game/mount/titanfall2/titanfall2.dll`、Engine 测试目录的 DLL SHA256 均为：

`8DA9377BDC7E0BAE7E8794C50FA7C321F18E15B3ECE5A6A1982EF49955F97DB7`

## 复跑与证据

从仓库根目录运行：

```powershell
dotnet run --project engine/Tools/SboxBuild/SboxBuild.csproj -- build --config Developer
dotnet run --project engine/Tools/SboxBuild/SboxBuild.csproj -- test --no-build --filter 'FullyQualifiedName~Titanfall2'
dotnet format engine/Sandbox-Engine.slnx --no-restore --verify-no-changes --include engine/Mounting/Sandbox.Mounting.Titanfall2/Pak/VpkArchive.cs engine/Tests/Sandbox.Test.Engine/Scene/Titanfall2VpkArchiveTests.cs
```

本轮原始证据保留在仓库 `.tmp/tf2-archive-review/`：

- `before.log`、`after-final.log`：缺陷复现、38 个目录条目数、四张地图解码结果。
- `Repro.csproj`、`Program.cs`：隔离复现及零售数据对照入口，参数为 Titanfall 2 安装根目录。
- `build-final-retry.log`、`tests-final.log`：最终构建与定向回归。
- `format.log`、`format-targeted-verify.log`：全仓检查失败与本次文件检查通过的独立结果。
- `binary-hashes.json`：三个本地 DLL 的身份核对。

本轮完成源码修复和本地构建，没有部署 Steam 安装，没有进行固定相机视觉或 FPS 验收。现有 SP 视觉差异、玩法碰撞分类、GPU/显存预算及多地图长时间稳定性仍需要各自的专项验证。
