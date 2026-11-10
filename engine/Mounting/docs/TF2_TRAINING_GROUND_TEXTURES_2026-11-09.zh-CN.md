# Allusive.TrainingGround 自定义材质未加载查证

日期：2026-11-09。

用户安装目录：`D:\Game\steams\steamapps\common\Titanfall2\R2Northstar\mods\Allusive.TrainingGround`，版本 1.0.2。

## 原因

实际游戏日志中的挂载模块 MVID 为 `4e20f7f7-625a-49f5-bfaf-c41c015722aa`。该版本能发现 mod 及其 loose BSP，但忽略了 VPK 配置中的 `MapOnly` 声明；未显式指定地图上下文时，也没有激活同一地图的 RPAK 规则。

实际配置：

```json
// vpk/vpk.json
{"MapOnly":{"client_mp_training_ground.bsp":"mp_training_ground"}}

// paks/rpak.json 中的主地图规则
{"mp_training_ground.rpak":"^mp_training_ground$"}
```

游戏日志明确记录：VPK `client_mp_training_ground` deferred；RPAK `mp_training_ground.rpak` 没有生效的 Preload/Postload/map rule，未索引。因此 BSP 和大量零售模型可加载，自定义地面等材质却缺失。

## 已有修复与本次工作

本次开始时，工作区 `Content/NorthstarProfile.cs` 已有未部署的 MapOnly 兼容改动及回归测试。本次复用该改动，比较旧部署 DLL 与从当前源码构建的候选，检查真实 BSP 的完整材质清单、原生 GUID 依赖及地面纹理数据。没有重复改写这部分生产代码。

修正逻辑包括包名 `.bsp` 后缀规范化；未指定地图时预载 MapOnly 包，并将声明的地图名用于该 mod 的 RPAK 选择；明确指定另一张地图时仍延后无关包。内容快照仍在挂载时固定，按 mod 优先级解析。

## 真实安装数据对照

| 项目 | 旧部署 DLL | 当前源码候选 |
| --- | ---: | ---: |
| 本 mod 的主 RPAK 索引资产 | 0 | 4,498 |
| 本 mod 的地图 VPK 索引成员 | 0 | 557 |
| BSP 材质路径数量 | 203 | 203 |
| 无 MATL/VMT 来源的 BSP 材质 | 32 | 0 |
| 训练场专用地面材质找到的数量 | 0/5 | 5/5 |
| 这些地面材质引用的独立纹理读取 | 未注册 | 11/11 成功 |

缺少的地面材质包括：

- `world/dirt/sand_training_ground_02_vertex_alpha`
- `world/dirt/sand_training_ground_tracks_02_bm`
- `world/dirt/sand_training_ground_02_va`
- `world/dirt/sand_traing_ground_01_and_02_bm`（源数据本来就是这个拼写）
- `world/dirt/sand_training_ground_tracks_01_bm`

每个非零纹理 GUID 都能从候选快照解析。11 张纹理的数据读取覆盖 RPAK / STARPAK 路径，没有修改 mod 的包或配置文件。材质路径不再缺失与纹理数据可读取，并不等同于整张地图全部渲染效果已验收。

## 验证记录

`.tmp/tf2-training-ground-20261109/`：

- `bsp-material-names.json`：实际 BSP 的 203 个材质路径。
- `inventory-deployed.json`：旧部署 DLL 的真实内容清单和缺失依赖。
- `inventory-candidate.json`：当前源码候选的同一检查。
- `content-tests.log`：28 项内容回归通过；1 项未请求的可选安装清单测试跳过。
- `InspectTrainingGround.cs`：隔离原生测试，实际加载 5 个地面材质及其 albedo 纹理，检查纹理有效、非错误且非白色回退。
- `sbox-build.log` / `native-tests.log` / `native-materials.json` / `verification.json`：构建与最终验证记录。

最终原生检查通过：5 个地面材质全部加载成功，8 个 albedo 绑定均为有效、非错误、非 Texture.White 的 1024×1024 纹理；3 个 BM 材质均识别为两层混合。SboxBuild 构建及相关文件格式检查通过。隔离原生测试模块 MVID 为 `a3a0cff6-8981-459d-9fbd-99e5b1f468ed`。

可部署候选已固定保存为 `.tmp/tf2-training-ground-20261109/ready/titanfall2.dll`，SHA-256 为 `9D5895995B30876434F958F47A3FFFD675423D2968B4022F7BADFD17AAC63FC5`，与原生测试加载的模块文件一致。

未在本任务中停止或重启用户游戏，主部署文件保持原样。当前运行版仍需更新后重启加载；本次不把候选验证当作旧进程已经显示修复效果。
