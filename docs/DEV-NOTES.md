# 开发与发布备忘（DEV-NOTES）

面向模块作者与想复用这套写法的人；使用者只需要看 README。

## 调用方式

### 命令（最通用）

```text
/omni TtsSystem say 副本即将开始
/omni TtsSystem stop
/omni TtsSystem clear
/omni TtsSystem purge
```

`say` 后面所有内容都会作为文本播报，不需要引号。
`test` 仍可用，是「朗读快捷播报文本」的别名（不再有独立的测试文本）。

### 静态调用（同一个仓库内的其他模块）

```csharp
TtsSystem.Say("倒计时 3 秒");
TtsSystem.Instance?.Speak("可拿到返回值判断是否进入播报");
TtsSystem.Instance?.Speak("用云扬播报", "zh-CN-YunyangNeural", 110, 80);
```

需要编译期可见，适合放在同一个在线仓库里的模块之间互相调用。

### 反射桥（独立模块，零编译依赖）

把 `TtsSystem.cs` 里的 `TtsBridge` 复制到自己的模块文件中，**并修改类型名**（如 `MyTtsBridge`）：

```csharp
if (MyTtsBridge.Available)
{
    MyTtsBridge.Say("来自另一个模块的播报");
}
```

`TtsBridge` 通过反射在已加载程序集中查找 `TtsSystem.Instance`，
调用方不需要在编译期引用本模块，本模块不存在时也不会报错。

> 辅助类型重名会造成加载冲突，复制后务必改名。

### Dalamud IPC（给任意 Dalamud 插件，推荐）

`TtsIpc` 在 `OnEnable` 时注册六个端点（`OmniTts.Version / IsAvailable / Say / SayEx / Stop / ClearQueue`），
`OnDispose` 时全部注销。实现全程反射，只用到宿主提供的 `OmniToolbox.Host.DalamudServices.PluginInterface`，
不新增 Dalamud.dll 的编译期引用，因此不违反在线模块的依赖约束。

几个关键点：

- `GetIpcProvider` 有多个泛型重载，按**泛型参数个数**匹配；返回类型是最后一个泛型参数。
- 注册前先调一次 `UnregisterFunc` / `UnregisterAction`：在线模块重载会产生新的动态程序集，
  旧程序集注册的端点可能残留，先注销再注册可避免重名冲突。
- 委托实例要存进静态列表保活，否则可能被 GC 回收导致调用失效。
- `RegisterAction` 用 `GetIpcProvider<object>(name)` 承载 `Action`。

订阅方写法与端点契约见 [CALLERS.md](CALLERS.md)。
本地可用 `OmniTtsLocalTest ipctest` 验证反射链路（用鸭子类型的假插件接口，不需要 Dalamud 运行时）。

## 配置项

| 配置 | 说明 | 默认值 |
| --- | --- | --- |
| `Enabled` | 总开关，关闭时所有调用静默失败 | `true` |
| `Engine` | `EdgeTts` / `SystemSapi` / `CustomCommand` | `EdgeTts` |
| `Voice` | 音色短名，如 `zh-CN-XiaoxiaoNeural` | `zh-CN-XiaoxiaoNeural` |
| `Style` / `StyleDegree` | SSML 表达风格（如 `cheerful`）与强度，留空不启用 | 空 / `100` |
| `Volume` | 音量 0–100，写入 SSML 的 `prosody volume` | `100` |
| `Speed` | 语速 1–200，100 为正常 | `100` |
| `Pitch` | 音调 1–200，100 为正常，仅 Edge 生效 | `100` |
| `AudioFormat` | 0 自动（MP3 优先）/ 1 WAV 优先 / 2 MP3 优先 | `0` |
| `Policy` | 冲突策略：排队 / 打断 / 丢弃 | 排队 |
| `CooldownMs` | 相同文本的去重间隔，0 表示不去重 | `1500` |
| `MaxLength` | 单条文本最大长度 | `80` |
| `StripBrackets` | 去除 `<>` 与 `[]` 内容 | `true` |
| `CacheAudio` | 缓存合成音频 | `true` |
| `FallbackToSapi` | Edge 失败时回退系统语音 | `true` |
| `PhonemeReplacements` | 发音替换表，默认含「欧米茄→欧米加」等 | 见代码 |
| `HotkeyText` | 快捷播报文本：命令列表「播报文本」执行按钮、测试播报、`test` 命令读的内容 | `副本即将开始` |
| `CustomExecutable` / `CustomArguments` | 自定义引擎程序与参数模板 | 空 |

自定义引擎参数模板支持 `{text}` `{voice}` `{volume}` `{speed}`，按空格拆分成参数数组，无需手工转义。

## 命令执行按钮

设置页底部自绘命令列表，每条命令右侧有两个按钮：

- **执行**：直接运行该命令，不需要去聊天框输 `/omni ...`。
  「播报文本」的执行按钮会朗读上方的**快捷播报文本**（`HotkeyText`）。
- **复制**：把完整命令文本写入剪贴板。

宿主自带的命令列表只有复制按钮、无法执行，因此模块自绘命令列表并把 `Info.Commands` 留空，避免同一份命令重复展示两遍。`/omni TtsSystem ...` 聊天命令不受影响，仍然全部可用。

## 设置界面说明

设置页所有下拉框、滑条、输入框均在控件**上方**标注了作用说明。之所以这样布局，是因为 Omni 的设置页会把控件拉满整行，ImGui 画在控件右侧的默认标签会被裁掉；把标签画在控件上一行就不受宿主布局影响。给其他模块写设置界面时建议沿用这一写法（`TextUnformatted` + `SetNextItemWidth(-1)` + `##id` 隐藏标签）。

## 实现要点

### Edge TTS 协议

- **Sec-MS-GEC**：取当前 Windows 文件时间并向下取整到 300 秒，拼上可信令牌
  `6A5AA1D4EAFF4E9FB37E23D68491D6F4` 后取 SHA256。
- **WebSocket**：直连 `wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1`，
  带 `Sec-MS-GEC` 与 `Sec-MS-GEC-Version=1-134.0.3124.66`；使用 BCL 自带的 `ClientWebSocket`，不引第三方包。
- **消息**：先发 `Path:speech.config`（声明输出格式），再发 `Path:ssml`（SSML 文本），
  随后接收二进制帧；每帧前 2 字节是大端序的头部长度，头部以 `Path:audio` 结尾的帧体才是音频数据，
  遇到 `Path:turn.end` 结束。
- 音色、语速、音调、音量全部通过 SSML 的 `voice` / `prosody` 传递。

### 播放链路：为什么以 MP3 为主

本机实测结论：

| 路径 | 结果 |
| --- | --- |
| Edge 输出 `riff-24khz-16bit-mono-pcm`（WAV） | **服务端返回空音频**；同一时刻 MP3 正常 → 目前只接受 MP3 系列 |
| Edge 输出 `audio-24khz-48kbitrate-mono-mp3` | 成功，帧头 `FFF3` |
| winmm `PlaySound` 播放 WAV | 成功 |
| winmm MCI 播放 MP3 | 失败，错误 266（驱动加载失败，缺 MPEG 解码器） |
| NAudio 播放 MP3 | 成功（需宿主已提供） |

因此模块默认以 MP3 优先，播放顺序为：

1. **NAudio**（反射探测宿主是否携带）→ 播 MP3，体验最好。
2. **MCI** → 播 MP3，系统自带，但精简版系统可能缺解码器。
3. 都失败 → 若开启 `FallbackToSapi`，改用系统 SAPI 离线语音兜底，不会完全无声。

只有在 MP3 合成失败时才会尝试 WAV（保留该分支以防服务端日后重新支持 PCM）。

> 已知的坑：NAudio 2.x 已拆成 `NAudio.Core` / `NAudio.WinMM` / `NAudio.Wasapi` 等多个程序集，
> 且 `AudioFileReader` 与 `WaveOutEvent` **分属不同程序集**。
> 用 `Type.GetType("…, NAudio")` 按程序集名查找必然漏判，必须跨程序集按类型全名分别收集。

#### 响应速度

一次播报的耗时主要花在网络上，本模块做了三层处理：

| 手段 | 效果 |
| --- | --- |
| **WebSocket 连接复用** | 单次握手约 0.7–1.3 秒，占冷启动耗时一半以上。服务端允许同一条连接连续合成多条（重发 `speech.config` 即可），因此连接建好后保留复用，直到 240 秒未被使用才重建。 |
| **启用时预热** | `OnEnable` 后台建立连接，并把快捷播报文本提前合成进缓存，让首次触发也几乎不用等握手。 |
| **结果缓存** | 相同文本+音色+语速+音调+音量+格式直接读缓存文件，通常几十毫秒出声；可在设置里关闭，或用 `purge` 命令清除。 |

本机实测（短句、MP3、同一服务节点）：

| 场景 | 耗时 |
| --- | --- |
| 未预热首次合成 | 约 3.4 秒 |
| 预热后首次合成 | 约 0.8 秒 |
| 后续合成（复用连接） | 约 0.5–1.1 秒 |
| 缓存命中 | 几十毫秒 |

可用 `OmniTtsLocalTest bench [次数] [--warm]` 自行测量：`--warm` 模拟模块启用时的后台预热。

缓存命中依赖参数完全一致——改动音量、语速等会让缓存失效并重新合成一次。
若希望某些话首次就快，把它们放进「快捷播报文本」或在游戏里先播一遍，之后便一直在缓存里。

## 系统 SAPI 引擎

不引用 `System.Speech`（.NET Core 上属于 Windows 兼容包，宿主未必加载），
改为启动 `powershell.exe` 子进程承载 SAPI：脚本用 `-EncodedCommand` 传 Base64，
文本与参数全部走 `OMNI_TTS_*` 环境变量，用户文本永远不进入命令行，规避转义与注入。

## 注意事项

- Edge TTS **需要联网**，且微软对同一 IP 有频率限制；高频播报请开启缓存并调大去重间隔。
- 首次合成约 0.5–1.5 秒，命中缓存后直接播放。
- 可用音色取决于服务端；完整列表可用
  `https://speech.platform.bing.com/consumer/speech/synthesize/readaloud/voices/list?TrustedClientToken=6A5AA1D4EAFF4E9FB37E23D68491D6F4` 查询。
- 缓存位于 `%TEMP%\OmniTtsCache`，可用 `purge` 命令或设置里的按钮清除。
- **诊断日志**：`%TEMP%\OmniTtsCache\tts.log`，记录每次握手/合成/播放的耗时与失败原因。
  设置页底部状态行下方会显示 `NAudio` 可用性、上次成功格式与日志路径；
  状态行出现「回退系统语音」时会带上真实失败原因，不再只显示笼统的「合成失败」。
- 合成失败（网络异常、超时、空音频）不会重试第二种音频格式——失败与格式无关，
  只有「合成成功但播放失败」才会自动换格式。单次合成超时 10 秒。
- 在线模块与 Omni 同权限、无沙箱；启用自动更新等于持续信任本仓库后续代码。
- 卸载时调用 `Dispose`；若你的 Omni 版本 `ModuleBase` 提供 `OnDisable` / `Dispose` 虚方法，
  把释放逻辑改挂到对应方法即可。

## 仓库结构

```text
OmniTts-Online/
  Modules/
    TtsSystem.cs          # 模块本体（Edge 客户端 / 播放后端 / 配置 / 反射桥）
  Tools/
    Update-Sha256.ps1     # 重算摘要、可选递增版本
    Publish-Check.ps1     # 发布前自检：清单字段、摘要、类型名、Info 位置、编码换行
    LocalTest/            # 控制台测试工程，直接编译模块本体
  .gitattributes          # 固定换行符，避免下载校验失败
  TreeHouseModules.json   # 清单
```

## 本地测试

`Tools/LocalTest` 是一个控制台工程，直接编译 `Modules/TtsSystem.cs` 本体，并**引用本机 Omni / Dalamud 的真实程序集**（非桩类型），
因此本地编译结果与 Omni 宿主在线编译一致，能在提交前暴露 `ImGui` / `ModuleBase` 的签名差异。

默认路径取自卫月启动器，可用 MSBuild 属性覆盖：

```powershell
dotnet build Tools\LocalTest\LocalTest.csproj -p:OmniDir="...\installedPlugins\OmniToolbox\x.x.x.x" -p:DalamudHooksDir="...\addon\Hooks\dev"
dotnet run --project Tools\LocalTest -- info / diag / synth / say / purge
```

也可以直接运行已编译好的可执行文件：

```text
Tools\LocalTest\bin\Debug\net10.0\OmniTtsLocalTest.exe say "测试" --volume 30
Tools\LocalTest\bin\Debug\net10.0\OmniTtsLocalTest.exe diag
```

`say` 支持 `--voice zh-CN-YunyangNeural`、`--speed 120`、`--volume 60`、`--wav` / `--mp3` 等参数。

测试工程额外引用了 NAudio 并主动加载它，用来模拟「宿主已提供 NAudio」的情形；
在线模块本身不引用 NAudio，只在运行时反射探测。

## 真实 API 签名备忘（Dalamud.Bindings.ImGui + OmniToolbox.Common）

**Omni 宿主的 Roslyn 编译管线没有 implicit usings、不含 `System.Linq`**：
模块源码里不能用 `FirstOrDefault` / `Select` 等 LINQ 扩展方法，需写成普通循环。
`LocalTest` 工程开了 ImplicitUsings，本地能编译过不代表宿主能过——新增代码前先检查是否引入了 LINQ。

以下签名来自本机 `addon\Hooks\dev\Dalamud.Bindings.ImGui.dll` 与 `OmniToolbox.Common.dll`，与常见 ImGui.NET 写法有出入，写模块时按此为准：

```csharp
ImGui.InputText(string label, ref string buf, int maxLength = 512, ImGuiInputTextFlags flags = 0); // maxLength 是 int，传 uint 会编译报错
ImGui.Combo(string label, ref int currentItem, ReadOnlySpan<string> items, int popupMaxHeightInItems = -1);
ImGui.SliderInt(string label, ref int v, int vMin, int vMax, string format = null, ImGuiSliderFlags flags = 0);
ImGui.Checkbox(string label, ref bool v);
```

`ModuleBase`（`OmniToolbox.Common.Module.Abstractions`）成员：

| 成员 | 说明 |
| --- | --- |
| `Info` / `HasSettings` | 抽象/虚，必须提供 |
| `TryHandleCommand(string)` / `TryHandleCommand(string, string)` | 命令路由，返回 false 交回宿主 |
| `DrawSettings()` / `ResetSettings()` | 返回 true 触发宿主保存配置 |
| **`OnEnable()` / `OnDisable()` / `OnDispose()`** | protected virtual 生命周期钩子 |
| `IsEnabled` | 由宿主设置，模块只读 |
| `OpenIconBrowser(...)` | protected |

注意 `Dispose()` 本身**不可覆写**，释放逻辑要写在 `OnDispose()` 里。
`ModuleCommand` 是 `record(string DescriptionKey, string ClipboardText)`，`Commands` / `SupportUrls` 为 `IReadOnlyList<>`。

## 发布与更新

### 首次发布

1. **建公开仓库**：在线模块只支持**公开**仓库中的 `.cs` / `.dll`，不支持私有仓库、Release 附件或 ZIP。
2. **提交目录结构**：根目录 `TreeHouseModules.json` + `Modules/TtsSystem.cs`（`Tools/` 只是本地工具，不会被下载）。
3. **重算摘要**（改过代码就跑）：

   ```powershell
   pwsh -File Tools/Update-Sha256.ps1
   ```

4. **发布前自检**：

   ```powershell
   pwsh -File Tools/Publish-Check.ps1
   ```

5. **推送**，然后在 Omni 里「插件设置 → 在线模块」填写仓库地址（或清单的 raw 地址），手动刷新。
6. **启用**：首次安装默认关闭，到「树树妙妙屋 → 在线」手动启用。
7. **远端复核**：本地一致不等于远端一致（Git 会转换换行符）。下载 `File` 指向的 raw 文件再算一次 SHA256 比对：

   ```powershell
   $raw = "$env:TEMP\TtsSystem.cs"
   Invoke-WebRequest 'https://raw.githubusercontent.com/Jinjyu1211/OmniTts-Online/main/Modules/TtsSystem.cs' -OutFile $raw
   (Get-FileHash -LiteralPath $raw -Algorithm SHA256).Hash
   ```

### 后续更新

```powershell
pwsh -File Tools/Update-Sha256.ps1 -BumpVersion
```

脚本会重算摘要**并递增版本**；只改摘要不提版本不会触发已安装模块更新。
改完再跑一次 `Publish-Check.ps1`，然后一起提交清单与模块文件。

校验一致只说明文件与清单相符，不代表代码安全或作者可信。

### 排查「在线模块获取失败」

- 仓库侧确认：仓库为 **Public**、默认分支根目录有 `TreeHouseModules.json`、清单 JSON 有效（`Publish-Check.ps1` 通过）。
- 网络侧确认：Omni 从用户机器直接访问 GitHub（raw / API），国内网络直连经常失败；
  让代理覆盖游戏进程（TUN / 系统代理）后手动刷新，或等 Omni 每 6 小时的自动刷新。
- 沙箱/CI 环境若只有 `api.github.com` 可达而 `github.com` git 端口不通，
  可用 `gh api -X PUT repos/<owner>/<repo>/contents/<path>` 逐文件上传（大文件用 `--input` 请求体）。
