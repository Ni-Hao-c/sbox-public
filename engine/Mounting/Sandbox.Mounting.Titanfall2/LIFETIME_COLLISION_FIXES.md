# 场景生命周期、导航与碰撞数据修复

日期：2026-10-27。本文件记录本轮实际改动，历史完成度与审计记录中的对应待办应以这里为准。

2026-10-28 追加：共享粒子、挂载刷新、生成地形缓存过期和重开已补运行检查；新增碰撞命中来源诊断。结果和精确边界见 [COLLISION_DIAGNOSTICS_RUNTIME.md](COLLISION_DIAGNOSTICS_RUNTIME.md)。下文“没有启动编辑器”的说明属于 10-27 阶段。

## 行为变化

- SceneResourceOwner 仅释放明确传给自己的独占资源。地图销毁不再按 MountIdent 查找并释放全挂载缓存，因此临时构建场景、编辑器/Play 克隆、多个地图和旧挂载实例的销毁不会清空另一实例的粒子纹理。
- 共享粒子帧和 sprite 保留到挂载卸载；卸载只移除挂载引用，不主动 Dispose 可能仍被旧场景使用的 Texture。中性环境贴图采用同样处理。最后消费者结束后由资源包装的正常回收路径释放，不承诺立即降低显存。
- 挂载 Shutdown 补清 RPAK/VPK 来源、路径、VMT 定义、材质元数据与 shader 反射字典，解除对索引表的引用。
- 禁用挂载导航时不再改写宿主导航。启用时按 Scene 共同持有状态，最后一个组件禁用/销毁后恢复原设置；父对象禁用时也不取得状态。已有宿主导航的过滤标签被保留；宿主后续替换过滤器时不会被退出过程覆盖。
- 原始世界碰撞改由 `SBOX_TITANFALL2_ORIGINAL_WORLD_COLLISION=1` 单独选择，默认 0。`SBOX_TITANFALL2_DEFERRED_NAVMESH=1` 只控制延迟导航；需要同时测试原始碰撞与导航时必须同时设置两项。渲染几何回退在导航开启时有相应 tag，删除了与实际行为不符的“不提交回退”警告。
- 静态道具 payload 升为 v6，逐实例保存 SourceIndex、SolidType、ReservedCollisionData 与 CollisionFlagsRemove。v3/v4/v5 继续读取，旧 bool 行为保留，缺失字段不猜值。旧缓存场景需重新导入才能取得原始字段。
- 有选择地取消忽略 Perf 三类验证目录中的 csx，使已有回归脚本能够随源码提交。脚本中依赖本机外部 fixture 的路径仍需单独整理。

## 验证

- 标准 SboxBuild Developer 构建成功，并部署挂载 DLL。
- 新增 `engine/Tests/Sandbox.Test.Engine/Scene/Titanfall2MountTests.cs`，覆盖卸载索引清理、v6 多种 solid 与完整 uint 掩码往返、截断拒绝、三个旧版本兼容、默认导航保持宿主、多组件禁用/重启、父对象禁用以及宿主后续过滤器修改。
- 测试采用先统一构建，再 `SboxBuild test --no-build`。直接让多个测试分组同时构建曾产生共享输出文件锁冲突。
- 非 LiveBackend 全套测试已运行：Integration、Unit、分配及 Text 分组通过；Engine 有三个 WebSurface.IsUriAllowed 失败，分别是 127-0-0-1、192-168-1-1、10-0-0-1.mattstevens.co.uk 的 HTTPS URL。不能写成全套通过。
- 标准 format --verify 已执行，仓库仍有其他文件及已有代码格式诊断。未批量格式化用户已有改动；本轮 C# 行尾已规范为 CRLF。
- 构建与测试日志在仓库 `.tmp/tf2-lifetime-*.log`，最终部署指纹另见 `.tmp/tf2-lifetime-verification.json`。

## 保留边界

这轮修复提前释放和导航状态污染，不是完整显存预算方案。生成世界模型/烘焙纹理继续与缓存 SceneFile 一同保留，以维持重开地图的正确性；没有增加不安全的场景关闭即 Dispose。

碰撞字段已完整传输，但 contents 到玩家、泰坦、子弹、钩抓查询的映射仍未实现，原始 CM 碰撞仍为可选实验路径。不能把保存 remove 掩码当作已经应用原版过滤。

没有启动编辑器进行双地图粒子画面检查或长期换图/显存压力采样，也没有改动原版光照、雾、天空、水体的视觉实现。这些仍按独立验收推进。
