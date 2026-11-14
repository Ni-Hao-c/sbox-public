# TF2 天空盒修复双目标部署记录

日期：2026-11-11。用户授权部署到仓库运行目录，并明确包含 `D:/Game/steams/steamapps/common/sbox`。

## 结果

| 目标挂载目录 | DLL MVID | DLL SHA256 |
| --- | --- | --- |
| `D:/sbox/sbox-public/game/mount/titanfall2` | `c56218e5-30a3-4b44-8170-d5ae694aa969` | `D197FE69DF3B83E3B7511787B8B94D4CD6CB978122AE56661726CBF8E632424E` |
| `D:/Game/steams/steamapps/common/sbox/mount/titanfall2` | `2a71b604-1ada-4889-a26c-ca50422e3d96` | `DF279D4FBC80A34912F78F3B84FC4D5E3C0D5DDA2AC2AE9BDD237B6B3371573B` |

两个运行目录的引擎程序集不同，因此分别通过现有 `SboxBuild build-titanfall2` 入口，使用目标安装自身的程序集构建。仓库目标也使用该入口的缓存兼容层，相关 3 项缓存回归在本地引擎程序集下通过。两个 DLL 包含相同天空盒修正；哈希不同来自目标依赖差异。

实际更改 6 个文件：仓库的挂载 DLL、部署记录；Steam 的挂载 DLL、匹配的 PDB、兼容清单与部署记录。deps.json 内容没有变化。原生桥接库、shader 源文件及 shader_c 均保留。

## 校验与备份

- 两个构建包均为 0 警告、0 错误；部署前重新核对各自的引擎程序集哈希。
- Steam 目标的 DLL 与天空盒修复时完成 8 项定向检查、6 组原生投影对照及两张完整地图渲染的 DLL 哈希一致。
- 仓库目标的构建 DLL 与新增本地运行的 8 项定向检查所用 DLL 哈希一致，0 失败、0 跳过；本次没有重新进行本地引擎的图形对照。
- 两个目标各有 48 个 shader/hlsl 源文件与候选包一致；两个原生桥接 DLL 也已核对一致。天空盒修复没有 shader 或布局变更，因此没有重编译或替换 shader_c。
- 替换前保存全部受影响文件；采用同目录暂存后替换，脚本包含失败回滚。最终逐文件哈希及两个 DLL 的 MVID 均通过校验。
- 备份：`D:/sbox/sbox-public/.tmp/tf2-sky-20261111/deploy-backup-20261111-164718/`，按 Repository、Steam 分开。
- 机器可读记录：`.tmp/tf2-sky-20261111/deployment.json`；两个挂载目录内的 `deployment.json` 也已更新。
- Steam 的 `compatibility.json` 保留原有编译 shader 清单，更新了本轮 DLL、PDB 哈希及天空盒验证信息。

部署前未发现运行中的 s&box，无需关闭用户进程。本次没有启动或重启游戏，完成的是磁盘部署与文件身份验证。下次启动时加载新挂载；部署后的实际加载身份尚未检查。

修复内容、像素对照与地图截图见 [TF2_SKYBOX_FIXES_2026-11-11.zh-CN.md](TF2_SKYBOX_FIXES_2026-11-11.zh-CN.md)。
