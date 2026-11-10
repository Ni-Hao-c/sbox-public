# TF2 调色与 Bloom 部署记录（2026-11-09）

## 部署结果

- 目标：`D:\sbox\sbox-public\game\mount\titanfall2`。
- 新 DLL MVID：`85207b75-c919-4e16-b328-a614f463db67`。
- 新 DLL SHA256：`8A4CDE01835AC9D845242AEC721BB1794CD7D95A3B68C348B73A7796A4DB8F54`。
- 原 DLL MVID：`4e20f7f7-625a-49f5-bfaf-c41c015722aa`。
- 原 DLL SHA256：`8D55CC821E3F042EC4933EA0816DAF96222CD42E0CFA43CACF598E38F9B04772`。
- 备份：`D:\sbox\sbox-public\.tmp\tf2-deploy-color-bloom-20261109\backup-20261109-192008`。
- 详细清单：本机 `.tmp/tf2-deploy-color-bloom-20261109/deployment.json` 与 `deployment-plan.json`。

实际替换四个文件：

1. `titanfall2.dll`。
2. `Assets/shaders/titanfall2_bloom_cs.shader_c`。
3. `Assets/shaders/titanfall2_color_lut_blend_cs.shader_c`。
4. `Assets/shaders/titanfall2_tonemapping.shader_c`。

DLL 与模块源码目录中的现有 shader 组成的 37 文件清单均与构建产物哈希一致。模块构建输出、托管测试引用、原生测试引用和部署 DLL 的哈希一致。部署前后均未发现 s&box 进程，没有关闭或重启用户游戏。

## 构建和检查

- SboxBuild Developer 托管构建通过；挂载模块编译零错误、零警告。
- SboxBuild build-shaders 通过，三份变更 shader 已生成当前 shader_c。
- 本次新增/修改的调色、Bloom 与环境代码及新增测试完成针对性格式检查。
- 托管检查：50 通过、1 跳过。跳过项为需要显式选择本地安装数据的可选内容审计。
- 原生图形检查：10 通过，启用 SBOX_TEST_GRAPHICS=1。

原生验证包含：正式调色组件的双表命令链渲染；五张 LUT 分成两批累加，对 32 个深度切片的所有像素读回；独立 wide/streak 分支使离屏像素发生变化；相机曝光两次启停恢复；画质保存事件与阴影四档预算；准备贴图的上传消费及释放。

曝光恢复测试明确打开宿主原生自动曝光，使“归还为开启”可观测；导入默认原生基线本身关闭自动曝光，不能把测试错误要求的开启状态当作功能问题。

## 尚未验收

- 没有启动用户游戏确认新 MVID 已被实际加载。
- 没有做零售地图固定机位画面对照、整图帧时间或峰值显存测试。
- wide/streak 生成链仍是实现记录所述重建方案，不能把编译与离屏检查解释为零售像素一致。
- 刷体/相机语义、静态范围与实体 I/O 的边界仍见[实现记录](TF2_COLOR_BLOOM_IMPLEMENTATION_2026-11-09.zh-CN.md)。
