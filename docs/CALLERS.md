# 其他插件如何调用本 TTS 模块

对外一共有三条路，按推荐程度排序：

| 方式 | 适用场景 | 依赖 | 说明 |
| --- | --- | --- | --- |
| **Dalamud IPC**（推荐） | 任意 Dalamud 插件（含你自己的插件） | 无编译期依赖 | 标准跨插件调用，有版本协商，插件未安装时可优雅降级 |
| **反射调用** | 同一个 Omni 里的其他在线模块 | 无编译期依赖 | 直接用现成的 `TtsBridge`，或自己反射 `TtsSystem.Instance` |
| **聊天命令** | 任意插件，最简单 | 命令管理器 | `/omni TtsSystem say <文本>`，走文本命令，延迟略高 |

---

## 方式一：Dalamud IPC（推荐）

模块启用时会注册下面这些端点。订阅方按**同样的名字 + 同样的泛型签名**订阅即可。

| 端点名 | 签名 | 说明 |
| --- | --- | --- |
| `OmniTts.Version` | `Func<int>` | 协议版本，当前为 `1`；签名变更时递增 |
| `OmniTts.IsAvailable` | `Func<bool>` | 模块已加载且已启用 |
| `OmniTts.Say` | `Func<string, bool>` | 按模块当前配置播报，返回是否进入播报流程 |
| `OmniTts.SayEx` | `Func<string, string, int, int, bool>` | `文本 / 音色 / 语速 / 音量`；音色留空、语速或音量 ≤ 0 表示沿用模块配置 |
| `OmniTts.Stop` | `Action` | 停止当前播报并清空队列 |
| `OmniTts.ClearQueue` | `Action` | 仅清空等待队列，不打断正在播报的内容 |

### 订阅端示例（其它插件里这样写）

```csharp
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

public sealed class MyPluginCaller
{
    private readonly ICallGateSubscriber<int> version;
    private readonly ICallGateSubscriber<bool> isAvailable;
    private readonly ICallGateSubscriber<string, bool> say;
    private readonly ICallGateSubscriber<string, string, int, int, bool> sayEx;
    private readonly ICallGateSubscriber<object> stop;

    public MyPluginCaller(IDalamudPluginInterface pluginInterface)
    {
        // 只取订阅器，不需要 TTS 插件真的在场；真正调用时才会失败。
        version = pluginInterface.GetIpcSubscriber<int>("OmniTts.Version");
        isAvailable = pluginInterface.GetIpcSubscriber<bool>("OmniTts.IsAvailable");
        say = pluginInterface.GetIpcSubscriber<string, bool>("OmniTts.Say");
        sayEx = pluginInterface.GetIpcSubscriber<string, string, int, int, bool>("OmniTts.SayEx");
        stop = pluginInterface.GetIpcSubscriber<object>("OmniTts.Stop");
    }

    /// <summary>播报一句，模块没装 / 没启用时安静返回 false。</summary>
    public bool Say(string text)
    {
        try
        {
            if (!isAvailable.InvokeFunc())
            {
                return false;
            }

            return say.InvokeFunc(text);
        }
        catch (IpcNotReadyError)
        {
            // TTS 模块未加载，属于预期情况，不要当成错误抛出。
            return false;
        }
        catch (IpcTypeMismatchError)
        {
            // 端点签名与本文档不一致，说明协议版本变了。
            return false;
        }
    }

    /// <summary>带音色与语速播报：音色传 null/空、语速音量传 0 表示沿用模块配置。</summary>
    public bool SayEx(string text, string? voice, int speed, int volume) =>
        sayEx.InvokeFunc(text, voice ?? string.Empty, speed, volume);

    public void Stop() => stop.InvokeAction();

    public int ProtocolVersion()
    {
        try
        {
            return version.InvokeFunc();
        }
        catch (IpcNotReadyError)
        {
            return 0;
        }
    }
}
```

### 行为约定

- **必须先启用模块**：Omni 里首次安装在线模块默认关闭，需要到「树树妙妙屋 → 在线」手动启用一次。未启用时 `IsAvailable` 返回 `false`、`Say` 返回 `false`。
- **返回值不代表已出声**：`Say` 返回 `true` 只表示已进入播报队列。若被冷却去重（同样文本在冷却时间内）或队列策略为「忙时丢弃」，会返回 `false`。
- **线程安全**：可以从任意线程调用，模块内部串行处理队列。
- **音色名**用 Edge TTS 短名，如 `zh-CN-XiaoxiaoNeural`、`zh-CN-YunxiNeural`；语速 `1–200`（100 为正常），音量 `0–100`。

---

## 方式二：反射（同一个 Omni 内的其它在线模块用这个）

在线模块之间没有公共引用，只能反射。`Modules/TtsSystem.cs` 末尾自带的 `TtsBridge` 就是干这个的：把它复制到你的模块文件里（记得改类名避免同名冲突），然后：

```csharp
if (TtsBridge.Available)
{
    TtsBridge.Say("副本即将开始");
}
```

想直接调更多能力（停止、带参数播报）就自己取实例：

```csharp
var type = System.Type.GetType("OmniToolbox.TreeHouseOnline.TtsSystem, OmniToolbox.TreeHouseOnline")
           ?? FindInLoadedAssemblies("OmniToolbox.TreeHouseOnline.TtsSystem");
var instance = type?.GetProperty("Instance")?.GetValue(null);
type?.GetMethod("Speak", new[] { typeof(string) })?.Invoke(instance, new object[] { "文本" });
```

**注意**：在线模块是运行时编译进动态程序集的，`Type.GetType` 常常拿不到，需要遍历 `AppDomain.CurrentDomain.GetAssemblies()`（也就是 `TtsBridge` 的做法）。类型名与程序集名由 Omni 决定，可能随宿主版本变化，所以反射调用要始终带空值判断。

---

## 方式三：聊天命令

任意插件都能通过命令管理器触发：

```csharp
// Dalamud 的 ICommandManager
commandManager.ProcessCommand("/omni TtsSystem say 副本即将开始");
commandManager.ProcessCommand("/omni TtsSystem stop");
commandManager.ProcessCommand("/omni TtsSystem clear");
commandManager.ProcessCommand("/omni TtsSystem purge");
```

`say` 后面所有内容都会当作播报文本，不需要引号。命令方式拿不到返回值，且依赖 Omni 的命令路由，适合"触发一下就行"的场景。

---

## 排查

- 模块设置了 IPC 但订阅端报 `IpcNotReadyError`：模块未启用，或 Omni 尚未加载在线模块。
- 设置页底部「诊断」行会显示 `IPC=已注册 6 个端点` 或失败原因（例如宿主未暴露 `DalamudServices.PluginInterface`）。
- 播报本身的问题（合成失败、播放失败）看 `%TEMP%\OmniTtsCache\tts.log`。
