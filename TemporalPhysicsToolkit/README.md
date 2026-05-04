# Temporal Physics Toolkit(悖论引擎)

这是一个 Unity 运行时脚本包，用于实现“过去玩家影响物理世界，系统立即复制当前 PhysX 状态并加速模拟到收敛，然后把收敛后的世界状态同步给未来视图/未来玩家”的流程。


## 适用场景

- 双人时空玩法：过去玩家改变物理世界，未来玩家看到改变后的收敛状态。
- 本地分屏调试：左侧渲染过去场景，右侧渲染未来场景。
- 网络同步：主机生成未来世界 JSON，发送给客户端；客户端解析并应用状态。
- 需要避免飞出场地物体永不收敛的物理沙盒。

## 包内容

- `TemporalWorldState.cs`
  - 定义可序列化世界状态、物体状态、逻辑状态。
  - 输出 JSON 的核心数据结构。
- `TemporalPhysicsBody.cs`
  - 挂在需要参与时空投影的物体上。
  - 捕获/恢复 Rigidbody、Transform、active 状态和可选逻辑状态。
- `TemporalProjectionExclusion.cs`
  - 提供 `ITemporalProjectionExclusion` 排除接口和 `TemporalProjectionExclusion` 组件。
  - 挂到物体或父物体后，该层级下的 Rigidbody 不会参与时间加速。
- `TemporalPhysicsProjector.cs`
  - 核心加速模拟器。
  - 创建独立 LocalPhysicsMode.Physics3D 场景，复制静态碰撞体和时空物体，快速 `PhysicsScene.Simulate()`，直到收敛或达到最大步数。
  - 在投影场景中创建 6 个边界面，并在每个模拟步检查越界物体，越界后输出为 `active=false`。
- `TemporalProjectionBoundary.cs`
  - 投影边界触发器组件。主删除逻辑仍由 `TemporalPhysicsProjector` 的每步检查兜底。
- `PastFutureTimelineController.cs`
  - 时间线控制器。
  - 负责检测过去世界扰动、请求投影、缓存未来 JSON、广播未来状态。
- `TemporalSplitScreenFutureView.cs`
  - 本地右侧 Future View 渲染器。
  - 创建未来场景和未来相机，把未来 JSON 应用到右侧克隆物体。
- `TemporalPastPlayerController.cs`
  - 示例过去玩家控制器。
  - WASD 移动，推动/拾取/放下刚体，并通知时间线触发投影。
- `TemporalCameraFollow.cs`
  - 示例相机跟随脚本。

## 输入

### 场景输入

- 需要参与时空投影的动态物体：
  - `GameObject`
  - `Rigidbody`
  - `Collider`
  - `TemporalPhysicsBody` 可手动添加；如果没有添加，运行时会自动补上
- 不需要参与时间加速的物体：
  - 给该物体或父物体添加 `TemporalProjectionExclusion`
  - 或在自定义组件中实现 `ITemporalProjectionExclusion`
  - 返回 `true` 时，该 Rigidbody 不会被自动注册、追踪、克隆或写入未来状态
- 静态环境：
  - 普通 `Collider` / `MeshCollider` / `Renderer`
  - 不要挂 `TemporalPhysicsBody`
  - 不要包含非 kinematic Rigidbody，否则不会作为静态环境克隆
- 时间线控制器：
  - 场景中创建一个空物体，例如 `TemporalTimelineController`
  - 添加：
    - `TemporalPhysicsProjector`
    - `PastFutureTimelineController`
    - 可选 `TemporalSplitScreenFutureView`

### 程序输入

- 主机触发投影：
  - `PastFutureTimelineController.RequestProjection()`
  - `PastFutureTimelineController.NotifyPastInfluenceEnded(reason)`
  - `PastFutureTimelineController.NotifyPastObjectDropped(body)`
  - 或开启 `autoProjectOnAnyBodyDisturbance`
- 客户端接收未来状态：
  - `PastFutureTimelineController.ReceiveFutureStateJson(json)`
  - 或直接调用 `TemporalSplitScreenFutureView.ApplyFutureStateJson(json)`
- 自定义逻辑状态：
  - 让组件实现 `ITemporalStateSerializable`
  - `CaptureTemporalState()` 输出 JSON 字符串
  - `RestoreTemporalState(json)` 恢复逻辑状态

## 输出

### TemporalWorldState

投影完成后输出 `TemporalWorldState`：

- `tick`
  - 生成时的 Unity `Time.frameCount`
- `simulatedSteps`
  - 加速模拟执行的步数
- `simulatedSeconds`
  - 加速模拟覆盖的物理时间
- `converged`
  - 是否成功收敛
  - **必须为 `true` 才会被广播给未来人**：投影管线只在 `converged=true` 的状态下触发 `OnProjectionCompleted`；如果触发了 `SafetyHardStepCap`（1,000,000 步的内部安全闸）仍未收敛，则会向控制台输出 `LogError` 并丢弃本次结果，未来人不会收到任何更新
- `objects`
  - 每个 `TemporalPhysicsBody` 的状态列表

### TemporalObjectState

每个物体输出：

- `objectId`
  - 稳定 ID，用于网络两端匹配对象
- `active`
  - 是否仍存在于未来状态
  - 飞出六面边界后会输出 `false`
- `position`
- `rotation`
- `scale`
- `linearVelocity`
- `angularVelocity`
- `isSleeping`
- `logicStates`

### JSON

主机可通过：

```csharp
string json = timelineController.LatestFutureStateJson;
```

或事件：

```csharp
timelineController.OnFutureStateReady += SendToFutureClient;
```

把 JSON 发送给未来玩家。客户端收到后调用：

```csharp
timelineController.ReceiveFutureStateJson(json);
```

## 触发条件

### 自动触发

`PastFutureTimelineController.projectOnceOnStart = true` 时：

- 游戏开局第一帧后会自动执行一次投影
- 不需要玩家先推动或放下物体
- 初始 Future View 会直接拿到一次收敛后的未来状态
- `initialProjectionDelay` 可用于延迟这次开局投影

`PastFutureTimelineController.autoProjectOnAnyBodyDisturbance = true` 时：

- 每个 `FixedUpdate` 监控所有 tracked `TemporalPhysicsBody`
- 只要位移、线速度或角速度超过阈值，就立即 branch 出投影物理场景
- 投影场景会加速模拟到收敛
- 完成后缓存 `LatestFutureStateJson` 并广播 `OnFutureStateReady`

这种模式适合飞出场地的物体，因为不需要等待主世界自己收敛。

### 手动触发

推荐在玩家行为结束时触发：

- 放下物体
- 推动物体后影响结束
- 按按钮/机关改变物理状态

调用：

```csharp
timelineController.NotifyPastInfluenceEnded("Past influence ended");
```

或：

```csharp
timelineController.RequestProjection("Manual projection");
```

### 网络触发

过去玩家所在主机生成 JSON：

```csharp
timelineController.OnFutureStateReady += json =>
{
    // Send json through your network transport.
};
```

未来玩家客户端接收 JSON：

```csharp
timelineController.ReceiveFutureStateJson(json);
```

## 加速模拟流程

1. `TemporalPhysicsProjector` 创建独立投影场景。
2. 克隆静态环境碰撞体。
3. 克隆所有未被排除的 tracked `TemporalPhysicsBody`。
4. 把主世界当前状态写入克隆体。
5. 根据静态碰撞体自动计算世界边界，或使用手动边界。
6. 创建 6 个边界面。
7. 每步执行 `projectionPhysicsScene.Simulate(stepDuration)`。
8. 每步检查所有投影物体：
   - 触碰/越过六面边界则 `active=false`
   - inactive 物体不再参与收敛判断
9. 所有 active Rigidbody 速度低于阈值并持续若干帧，则认为收敛。
10. 捕获投影世界状态并输出 JSON。
11. 卸载投影场景。

## 边界与越界删除

`TemporalPhysicsProjector` 的边界设置：

- `removeBodiesOutsideBounds`
  - 是否启用越界删除
- `autoFitBoundsFromStaticColliders`
  - 是否从静态碰撞体自动计算世界边界
- `manualBoundsCenter`
  - 手动边界中心
- `manualBoundsSize`
  - 手动边界尺寸
- `staticBoundsPadding`
  - 自动边界在 X/Z 上的额外 padding
  - 当前建议为 `0`
- `verticalBoundsPadding`
  - 自动边界在 Y 上的额外 padding
  - 当前建议为 `0`
- `boundaryContactTolerance`
  - 接触边界的容差

注意：如果你希望物体刚离开地面围墙高度就删除，自动边界必须来自真实场地碰撞体，且 padding 不要过大。否则边界会被放到更远处，看起来像没有删除。

## 本地分屏使用

1. 把 `TemporalSplitScreenFutureView` 加到 `TemporalTimelineController`。
2. 确保项目里有 `FutureView` layer。
3. 左侧 Past Camera 渲染普通层。
4. Future View 会创建右侧 camera，只渲染 `FutureView` layer。
5. Future View 会复制当前场景作为未来静态视图，并在收到未来 JSON 后更新右侧克隆体。

可观察字段：

- `FutureBodyCount`
- `LastAppliedFutureObjectCount`
- `LastAppliedFutureFrame`
- `LastAppliedFutureStateJson`

如果右侧没有更新，优先看这些字段是否变化。

## 推荐场景配置

- Unity Physics Simulation Mode: `FixedUpdate`
- 所有场景内 `Rigidbody` 会在运行时自动补 `TemporalPhysicsBody` 并参与未来运算
- 如需排除个别物体，给它或父物体挂 `TemporalProjectionExclusion`
- 每个动态时空物体有唯一 `TemporalPhysicsBody.objectId`
- `PastFutureTimelineController.projectOnceOnStart = true`，用于开局自动生成初始未来状态
- `TemporalPhysicsProjector.fastForwardInSingleFrame = true`
- `TemporalPhysicsProjector.coarseSimulationFactor` 默认 `1.0`，可在 `[1.0, 1.5]` 区间调高，用于让投影场景的单步时长加大、降低单次投影成本（`1.5` 表示步长放大 50%，约能减少 33% 的步数）。仅影响投影场景，不影响主世界物理精度
- `TemporalPhysicsProjector.stepsPerFrame` 控制每帧最多模拟多少步；当 `fastForwardInSingleFrame = true` 时，建议把它设得足够大，让投影一帧内完成；当 `false` 时，它就是每帧预算
- 投影会**一直模拟到收敛**，不再有公开的"最大步数"参数；只有内部的 `SafetyHardStepCap = 1,000,000` 步安全闸用来在异常场景下兜底，触发后会写 `LogError` 且不广播未来状态
- `PastFutureTimelineController.autoProjectOnAnyBodyDisturbance = true`，适合早期调试
- 正式玩法可在玩家交互结束时手动触发，减少重复投影

## 使用注意事项

- 当前脚本使用 Unity 6 的 `Rigidbody.linearVelocity` API。低版本 Unity 可能需要替换为 `Rigidbody.velocity`。
- 所有脚本在全局命名空间；如果导入项目里已有同名类，会产生冲突。
- `TemporalPhysicsBody.objectId` 必须稳定且唯一。复制 prefab 后请检查 ID 是否重复。
- `TemporalProjectionExclusion` 会作用于自身和子物体；适合排除玩家、UI 代理、临时特效或不希望进入预测的运行时物体。
- Future View 是“未来状态渲染”，不是未来玩家的可交互真实物理世界。需要可交互未来世界时，应在客户端自己的物理场景里应用状态并继续模拟。
- 投影场景会禁用克隆体上除 `TemporalPhysicsBody` 外的 MonoBehaviour，避免副作用重复执行。
- 静态环境克隆只包含没有非 kinematic Rigidbody 的根物体。
- 飞出边界的物体不会被 Destroy，而是在输出状态里 `active=false`。接收端会隐藏该对象。
- 如果需要对象重新出现，下一次状态必须再次发送 `active=true`。
- 网络层只需传输 JSON；本包不包含网络传输实现。
- 加速模拟越复杂，单帧耗时越高。大量刚体时建议把 `fastForwardInSingleFrame` 改为 `false` 让投影分多帧执行，或调高 `coarseSimulationFactor` 用更粗糙的步长换取性能；不建议绕过收敛检查直接发送未来状态。
- **未收敛 → 不广播**：若投影过程触发 `SafetyHardStepCap` 仍未收敛，本次投影结果会被丢弃，未来人不会收到更新。出现该错误一般意味着场景里存在永动刚体或者收敛阈值过严，请优先排查后再重试。

## 快速接入步骤

1. 解压 zip。
2. 将 `Runtime` 文件夹放入 Unity 项目，例如 `Assets/TemporalPhysicsToolkit/Runtime`。
3. 默认不需要手动给刚体添加脚本；运行时会自动把当前场景内所有未排除的 `Rigidbody` 注册为时空物体。
4. 如果某个刚体不应参与时间加速，挂 `TemporalProjectionExclusion` 或实现 `ITemporalProjectionExclusion`。
5. 创建 `TemporalTimelineController` 空物体。
6. 添加 `TemporalPhysicsProjector` 和 `PastFutureTimelineController`。
7. 可选添加 `TemporalSplitScreenFutureView`。
8. 保持 `projectOnceOnStart = true`，开局会先生成一次未来状态。
9. 设置 `autoProjectOnAnyBodyDisturbance = true` 进行调试。
10. 运行场景，推动任意 Rigidbody。
11. 查看 `LatestFutureStateJson` 和 Future View 是否更新。

## 排除物体示例

直接挂组件：

```csharp
gameObject.AddComponent<TemporalProjectionExclusion>();
```

或在已有组件中实现接口：

```csharp
public sealed class DoNotPredictThisBody : MonoBehaviour, ITemporalProjectionExclusion
{
    public bool ExcludeFromTemporalProjection => true;
}
```

运行时切换：

```csharp
TemporalProjectionExclusion exclusion =
    gameObject.GetComponent<TemporalProjectionExclusion>() ??
    gameObject.AddComponent<TemporalProjectionExclusion>();

exclusion.SetExcluded(true);
```

## 最小示例

```csharp
public sealed class FutureNetworkBridge : MonoBehaviour
{
    [SerializeField] private PastFutureTimelineController timeline;

    private void OnEnable()
    {
        timeline.OnFutureStateReady += SendToFuture;
    }

    private void OnDisable()
    {
        timeline.OnFutureStateReady -= SendToFuture;
    }

    private void SendToFuture(string json)
    {
        // Replace this with your network transport.
        Debug.Log(json);
    }
}
```

```csharp
public sealed class FutureReceiver : MonoBehaviour
{
    [SerializeField] private PastFutureTimelineController timeline;

    public void OnNetworkJsonReceived(string json)
    {
        timeline.ReceiveFutureStateJson(json);
    }
}
```

## 调试建议

- `TemporalPhysicsProjector.LastProjection`
  - 查看最后一次投影结果。
- `TemporalPhysicsProjector.LastBoundaryRemovalCount`
  - 查看边界删除了多少投影物体。
- `PastFutureTimelineController.LatestFutureStateJson`
  - 查看主机是否已经产生可发送状态。
- `TemporalSplitScreenFutureView.LastAppliedFutureObjectCount`
  - 查看右侧视图实际应用了多少物体状态。

## 版本

- Package version: `0.1.0`
- Tested with Unity `6000.4.5f1`
