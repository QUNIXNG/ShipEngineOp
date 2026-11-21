# Ship Engine Optimization（分级与发动机构型优化）

Kerbal Space Program 1.12.5 的编辑器辅助模组，用来在**给定载荷、目标 Δv 和推重比**的前提下，
挑出**总质量最低**的发动机构型与分级方案。

本仓库是 [BComFly/ShipEngineOp](https://github.com/BComFly/ShipEngineOp) 的 fork，
在 2.0.2 基础上替换了自动分级算法并加入计算缓存，详见 [本 fork 的改动](#本-fork-的改动)。

```
游戏内名称：分级和发动机构型优化
安装目录：  GameData/StagingEngineConfig
程序集：    StagingEngineConfig.dll（v2.0.2）
```

---

## 它能做什么

- **发动机极限曲线**：输入载荷质量，按目标 Δv 或目标 TWR 画出各发动机的总质量曲线，直接读出拐点。
- **多发动机对比**：最多同时比较 3 款发动机（固体燃料发动机不参与）。
- **自动选发动机**：按当前分级的 Δv / TWR 需求，自动选出让本级质量最小的发动机与并联数量。
- **自动优化分级（2.0 新增）**：把全船 Δv 在各分级之间重新分配，使全船总质量最小。
- **自动拆分分级**：某一级 Δv 过大时尝试拆成多级，找出更省质量的分法。
- **洋葱式分级**：支持同一级挂多台发动机并共用燃料箱的排布。
- **额外质量**：可把分离器、供电、散热等不计入发动机自身质量的部件成本算进去。
- **行星与大气**：切换目标行星与大气条件，推重比按对应重力换算。
- **生涯模式过滤**：只统计已在研发中心解锁的零件。
- **发动机黑名单**：把不想参与自动选择的发动机排除掉。
- **读取当前飞船**：一键从编辑器读出分级数、逐级 Δv / 推重比 / 质量与载荷，不用手填。
- **逐级对比表**：每行显示「现状 → 优化后」，最后一行给出全船总质量变化。
- **逐级真空 / 大气**：每一级可独立选择按真空比冲还是海平面推重比计算。
- **锁推重比**：勾选后该级保留自己的推重比目标，不随「按级推重比」开关统一到第一级。
- **燃料质量比配置**：不同燃料类型的湿重/干重比写在配置文件里，可按需修改。

## 安装

1. 关闭游戏。
2. 把 `StagingEngineConfig` 文件夹放进 `Kerbal Space Program/GameData/`。
3. 启动游戏，进入 VAB 或 SPH，点击右侧工具栏的模组图标（`SEO_appIcon`）。

依赖：无强制依赖。装有 **RealFuels** / **SolverEngines** 时会额外支持对应的发动机配置，
未安装时对应分支不启用。

## 用法要点

- 「限制 dv」与「限制 TWR」两种约束模式只能二选一：前者严格满足 Δv、TWR 浮动；后者相反。
- **自动优化分级**只能在「限制 dv」模式下使用，且会把所选分级的目标推重比与大气条件
  统一成所选编号最小那一级的设置。各级工作条件不同时不要用它做规划。
- 自动优化会**主动删减分级**来求更优解；结果不满意可以手动改，或把某些发动机加入黑名单。
- 自动拆分不会考虑分离器质量。想要更准的分配：先拆分，再把分离器质量填进「额外分级质量」，重新做一次优化。

## 读取当前飞船与逐级对比

编辑器里点「读取当前飞船」，模组会通过游戏自带的 `VesselDeltaV` 读出分级数据，不需要手填：

- **载荷质量**：顶级发动机油箱以上的全部部件（返回舱、降落伞、仪器、服务舱），
  由 `MeasurePayloadMass` 统一测量；「从编辑器加载」按钮也用同一套算法，两处数字必然一致。
- **逐级数据**：每一级的真空 Δv、海平面 Δv、推重比、点火时整船质量，以及该级的发动机。
- **对比表**：从上到下按**火箭从上往下**的顺序排（最上面一级在第一行），
  每行格式为 `级 | Delta-v | 推重比 | 质量 | 发动机`，单元格写作 `现状 → 优化后`。

表格里每级有两个开关：

| 开关 | 作用 |
| --- | --- |
| 真空 / 大气 | 该级按真空比冲还是海平面推重比计算。蓝色＝真空，琥珀色＝大气 |
| 锁 | 勾选后这级保留自己的推重比目标；配合顶部「按级推重比」关闭时，只有勾选的级不被统一 |

顶部还有「目标总 Δv」：填多少，优化器就把全船 Δv 按这个总量重新分配；点「用当前飞船 Δv」直接采用读到的值。

候选发动机的过滤规则：

- 固体燃料发动机不进候选（`SolidFuel`）
- 吸气式发动机不进候选（`IntakeAir`，真空里没有推力）
- 只能侧挂、没有堆叠节点的发动机不进候选（`attachRules.allowStack = false`，如 Mk-55、Twitch）

每级至少分到 **30 m/s**，避免优化器把某一级算成只飞几米每秒的死重。

## 配置文件

`Config/defaultPropMR.cfg` —— 各燃料类型的默认质量比：

```
SECODEFAULTMR
{
    propellant = LiquidFuel
    ratio = 9
    priority = 0
}
```

`priority` 用于同种燃料出现多条定义时决定默认取哪条。文件内已给出 LiquidFuel、Oxidizer、
MonoPropellant、XenonGas、ArgonGas、Lithium、LqdHydrogen、LqdMethane 等条目。

## 本地化

`Localization/` 下有 `en-us.cfg`、`zh-cn.cfg`、`ru.cfg`，共 104 条 `#LOC_SHIPENGOP_*` 词条。
新增语言只需复制一份并按同样键名翻译。

## 本 fork 的改动

提交 `0b628fe` `feat(staging): read the craft and fix the per-stage mass model`

- 新增 `SEO_ShipReader.cs`：复用游戏的 `VesselDeltaV` 读取分级，不再自行推算 Δv。
- 质量递推改成火箭方程形式 `W = (P+E)·R·(1−a) / (1−R·a)`，其中 `a = 1/(湿干比−1)`、
  `R = exp(Δv/(g0·Isp))`，并拒绝 `R·a ≥ 1` 这类物理上不可行的构型。
- 载荷链改为自下而上倒推：顶级载荷由飞船实测，下级继承上一级重量，
  顶级不再被链条覆盖，也不会把爆炸性数值传下去（超过 1,000,000 t 直接判不可行）。
- 候选池排除固体、吸气式与纯径向发动机；每级 Δv 下限 30 m/s。
- 逐级真空/大气切换、锁推重比、逐级对比表、目标总 Δv。

提交 `c519530` `perf(staging): replace random PSO with deterministic optimizer`

原版 2.0.2 用随机粒子群（50 粒子 × 最多 1000 次迭代）分配各分级 Δv，
同一艘船多次计算可能给出不同结果，且单次可能要几万次目标函数求值。
本 fork 改为：

- **确定性搜索**：以「当前分配 / 均分 / 递增 / 递减」四种初始方案为起点，
  逐对做黄金分割一维搜索 + 多轮扫描直到不再改进。同一输入必得同一结果。
- **目标函数缓存**：按分配值组合缓存总质量结果，避免重复求值。
- **发动机数据缓存**：缓存零件查找、大气压力、各发动机的比冲/推力，
  避免每次求值都线性扫描全零件表。
- **PSO 保留为回退路径**：新优化器抛异常时自动回退到原 PSO，不会因为算法问题导致功能失效。
- 修正了最后一级 Δv 可能为负的问题，改为下限截断并重新结算。

离线测试（合成目标函数，2~8 级）的求值次数为 72~7030 次，
相比原来最多约 50000 次的量级明显减少；分级总数越多收益越明显。

新版内部结构：

| 文件 | 作用 |
| --- | --- |
| `SEO_AutoStageOptimizer.cs` | 确定性分级 Δv 分配器 |
| `SEO_OptimizationCache.cs` | 零件 / 大气 / 比冲推力的缓存层 |
| `SEO_ShipReader.cs` | 从编辑器读飞船：分级、Δv、推重比、载荷 |
| `SEO_pso.cs` | 原粒子群实现，现仅作回退 |

## 构建

工程是旧式 csproj（`ToolsVersion 15.0`，`TargetFrameworkVersion v4.7.2`），
输出 `StagingEngineConfig.dll`。

需要引用：

- `Assembly-CSharp.dll`、`Assembly-CSharp-firstpass.dll`、`UnityEngine*.dll` —— 取自本机 KSP
- `RealFuels.dll`、`SolverEngines.dll` —— 取自对应模组；**未安装时可提供同名桩程序集**，
  只保留被调用的类型与成员即可编译（本 fork 的验证构建就是这样做的）

仓库内的 csproj 里 HintPath 仍是原作者机器上的路径，克隆后请改成自己的路径，
或直接用命令行编译。没有 .NET SDK 时可用 Roslyn 的 `csc.exe`：

```powershell
csc /nologo /target:library /platform:anycpu /optimize+ /langversion:latest `
    /out:StagingEngineConfig.dll `
    /reference:"<KSP>\KSP_x64_Data\Managed\mscorlib.dll" `
    /reference:"<KSP>\KSP_x64_Data\Managed\Assembly-CSharp.dll" `
    /reference:"<KSP>\KSP_x64_Data\Managed\UnityEngine.CoreModule.dll" `
    ... ShipEngineOp\*.cs
```

注意两点：

1. 编译产物缺失 **Win32 文件版本资源** 时，KSP 插件加载器会在
   `AssemblyLoader.LoadPluginInfo` 里抛 `ArgumentNullException`，表现为卡加载。
   用标准 csproj 构建会自动生成；手工用 csc 编时需自行补版本资源。
2. `AssemblyInfo.cs` 里的 `TargetFramework` 标识建议保留，缺失时同样可能影响插件识别。

## 已知限制

- 计算假设同种燃料的燃料箱湿重/干重比一致；用变比例油箱时结果会有偏差。
- 未适配的模组燃料类型（尤其是 RealFuels 的部分燃料）湿干比会显示为 1，且不出现在图表里。
- 多模式发动机（如 RAPIER）只按第一个工作模式计算。
- 比冲随条件连续变化的发动机不受支持。
- 固体燃料发动机不参与曲线与自动选择。

## 许可与来源

上游：[BComFly/ShipEngineOp](https://github.com/BComFly/ShipEngineOp)（2.0.2）。
CKAN 元数据把本模组标注为 MIT，但上游仓库内并未附带 LICENSE 文件，
如需再分发或商用，建议先向原作者确认许可条款。