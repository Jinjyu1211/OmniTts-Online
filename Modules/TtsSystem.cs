using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;

namespace OmniToolbox.TreeHouseOnline;

/// <summary>
/// TTS 语音播报系统：对外提供统一的语音播报入口，其他模块通过命令或反射桥调用。
/// 支持 Edge TTS（在线神经网络语音）、系统 SAPI、自定义命令行三种引擎。
/// 所有能力均基于 BCL 与系统组件实现，不引用宿主未提供的第三方程序集。
/// </summary>
public sealed class TtsSystem : ModuleBase
{
    // 仓库规范要求 Info 放在类型的首个成员位置。
    public override ModuleInfo Info { get; } = new()
    {
        Title = "TTS 语音播报系统",
        Description = "统一的语音播报入口，支持 Edge TTS 与系统语音，供其他模块调用。",
        Category = ModuleCategory.Interface,
        Author = "Jinyu",
        SupportUrls = ["https://afdian.com/a/YouShu"],
        ReportURL = "https://discord.com/channels/1456729574330077206/1456740706109493339"
    };

    /// <summary>
    /// 设置页自绘的命令列表：(描述, 命令模板)。{quick} 会替换为快捷播报文本。
    /// 宿主命令列表只渲染描述与复制按钮、无法执行，因此这里自绘“执行 + 复制”按钮，
    /// 并把 Info.Commands 留空，避免同一份命令重复展示两遍。
    /// </summary>
    private static readonly (string Description, string Command)[] CommandRows =
    [
        ("播报文本", "/omni TtsSystem say {quick}"),
        ("停止播报", "/omni TtsSystem stop"),
        ("播放测试", "/omni TtsSystem test"),
        ("清空队列", "/omni TtsSystem clear"),
        ("清除音频缓存", "/omni TtsSystem purge"),
    ];

    private TtsSystemConfig config = new();

    /// <summary>当前已加载的实例，供同环境内其他模块取用。</summary>
    public static TtsSystem? Instance { get; private set; }

    private readonly object gate = new();
    private readonly Queue<string> pending = new();
    private Thread? worker;
    private bool workerRunning;
    private volatile bool disposed;
    private volatile bool abortCurrent;
    private CancellationTokenSource? currentCts;
    private Process? currentProcess;

    private string lastText = string.Empty;
    private long lastTick;
    private string statusText = "空闲";

    public TtsSystem()
    {
        Instance = this;
    }

    public override bool HasSettings => true;

    /// <summary>启用时后台预热连接与常用文本缓存。ModuleBase 提供该生命周期钩子。</summary>
    protected override void OnEnable()
    {
        Instance = this;
        PreheatAsync();
        base.OnEnable();
    }

    /// <summary>停用时停止当前播报并关闭复用连接。</summary>
    protected override void OnDisable()
    {
        TtsEdgeClient.CloseConnection();
        Stop();
        base.OnDisable();
    }

    /// <summary>模块配置。主要为本地调试与测试工程提供访问入口。</summary>
    public TtsSystemConfig Config
    {
        get => config;
        set => config = value ?? new TtsSystemConfig();
    }

    // ---------- 对外调用入口 ----------

    /// <summary>请求播报一段文本，返回是否已进入播报流程。</summary>
    public bool Speak(string text)
    {
        if (disposed || !config.Enabled)
        {
            return false;
        }

        var normalized = Normalize(text);
        if (normalized.Length == 0)
        {
            return false;
        }

        if (config.CooldownMs > 0
            && string.Equals(normalized, lastText, StringComparison.Ordinal)
            && Environment.TickCount64 - lastTick < config.CooldownMs)
        {
            return false;
        }

        lastText = normalized;
        lastTick = Environment.TickCount64;

        lock (gate)
        {
            if (config.Policy == TtsPolicy.DropIfBusy && (workerRunning || pending.Count > 0))
            {
                return false;
            }

            if (config.Policy == TtsPolicy.Interrupt)
            {
                pending.Clear();
                abortCurrent = true;
                currentCts?.Cancel();
                TryKillCurrent();
            }

            pending.Enqueue(normalized);
            EnsureWorker();
        }

        return true;
    }

    /// <summary>带参数播报，不修改已保存的配置。</summary>
    public bool Speak(string text, string? voice, int? speed, int? volume)
    {
        var snapshot = config;
        try
        {
            var clone = CloneConfig(snapshot);
            if (!string.IsNullOrWhiteSpace(voice)) clone.Voice = voice;
            if (speed.HasValue) clone.Speed = Math.Clamp(speed.Value, 1, 200);
            if (volume.HasValue) clone.Volume = Math.Clamp(volume.Value, 0, 100);
            config = clone;
            return Speak(text);
        }
        finally
        {
            config = snapshot;
        }
    }

    /// <summary>静态快捷入口，等价于 Instance?.Speak。</summary>
    public static bool Say(string text) => Instance?.Speak(text) ?? false;

    /// <summary>停止当前播报并清空队列。</summary>
    public void Stop()
    {
        lock (gate)
        {
            pending.Clear();
            abortCurrent = true;
            currentCts?.Cancel();
            TryKillCurrent();
        }

        // 中断 PlaySound 正在播放的声音。
        try
        {
            TtsWinmmSound.Cancel();
        }
        catch
        {
            // 忽略：未使用 WAV 播放时无需取消。
        }

        statusText = "已停止";
    }

    /// <summary>仅清空等待队列，不打断正在播报的内容。</summary>
    public void ClearQueue()
    {
        lock (gate)
        {
            pending.Clear();
        }
    }

    /// <summary>删除本地音频缓存。</summary>
    public int PurgeCache()
    {
        try
        {
            var dir = TtsEdgeClient.CacheDirectory;
            if (!Directory.Exists(dir))
            {
                return 0;
            }

            var files = Directory.GetFiles(dir, "*.mp3");
            var wavFiles = Directory.GetFiles(dir, "*.wav");
            var all = new List<string>(files);
            all.AddRange(wavFiles);

            foreach (var file in all)
            {
                File.Delete(file);
            }

            statusText = $"已清除 {all.Count} 个缓存文件";
            return all.Count;
        }
        catch (Exception ex)
        {
            statusText = $"清除缓存失败：{ex.Message}";
            return 0;
        }
    }

    // ---------- 命令路由 ----------

    public override bool TryHandleCommand(string arguments)
    {
        var split = (arguments ?? string.Empty).Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var verb = split.Length > 0 ? split[0] : string.Empty;
        var rest = split.Length > 1 ? split[1] : string.Empty;

        switch (verb.ToLowerInvariant())
        {
            case "say":
            case "speak":
                if (rest.Length == 0)
                {
                    statusText = "用法：/omni TtsSystem say 文本";
                    return true;
                }

                Speak(rest);
                return true;

            case "stop":
                Stop();
                return true;

            case "clear":
                ClearQueue();
                return true;

            case "purge":
            case "cache":
                PurgeCache();
                return true;

            case "test":
                Speak(config.TestText);
                return true;
        }

        return false;
    }

    // ---------- 设置界面 ----------

    private static readonly string[] EngineNames = ["Edge TTS（在线，推荐）", "系统 SAPI（离线）", "自定义命令行"];
    private static readonly string[] VoicePresets =
    [
        "zh-CN-XiaoxiaoNeural", "zh-CN-XiaoyiNeural", "zh-CN-YunxiNeural", "zh-CN-YunyangNeural",
        "zh-CN-YunxiaNeural", "zh-CN-liaoning-XiaobeiNeural", "zh-CN-shaanxi-XiaoniNeural",
        "zh-HK-HiuGaaiNeural", "zh-TW-HsiaoChenNeural", "ja-JP-NanashiNeural", "en-US-AriaNeural", "自定义"
    ];
    private static readonly string[] PolicyNames = ["排队播报", "打断上一条", "忙碌时丢弃"];
    private static readonly string[] AudioFormatNames = ["自动", "WAV（兼容性最好）", "MP3（体积最小）"];

    // Omni 的设置页会把控件拉满整行，ImGui 画在控件右侧的标签会被裁掉。
    // 因此标签一律画在控件上一行，控件用 "##id" 做内部 ID 并显式占满剩余宽度。
    private static bool LabeledSliderInt(string label, string id, ref int value, int min, int max)
    {
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1);
        return ImGui.SliderInt(id, ref value, min, max);
    }

    private static bool LabeledCombo(string label, string id, ref int current, string[] items)
    {
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1);
        return ImGui.Combo(id, ref current, items, items.Length);
    }

    private static bool LabeledInputText(string label, string id, ref string text, int maxLength)
    {
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1);
        // Dalamud.Bindings.ImGui 的真实重载：(label, ref string buf, int maxLength, flags)
        // maxLength 是 int，传 uint 会误落到 Span<byte> 重载上导致编译失败。
        return ImGui.InputText(id, ref text, maxLength);
    }

    public override bool DrawSettings()
    {
        var dirty = false;

        var enabled = config.Enabled;
        if (ImGui.Checkbox("启用语音播报", ref enabled))
        {
            config.Enabled = enabled;
            dirty = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("停止"))
        {
            Stop();
        }

        ImGui.SameLine();
        if (ImGui.Button("清除缓存"))
        {
            PurgeCache();
        }

        ImGui.SameLine();
        if (ImGui.Button("测试播报"))
        {
            Speak(config.TestText);
        }

        var engine = (int)config.Engine;
        if (LabeledCombo("播报引擎（Edge 为微软神经网络语音，需联网；SAPI 为系统离线语音）", "##ttsEngine", ref engine, EngineNames))
        {
            config.Engine = (TtsEngine)engine;
            dirty = true;
        }

        var preset = Math.Clamp(config.VoicePreset, 0, VoicePresets.Length - 1);
        if (LabeledCombo("音色（预设）", "##ttsVoicePreset", ref preset, VoicePresets))
        {
            config.VoicePreset = preset;
            config.Voice = VoicePresets[preset];
            dirty = true;
        }

        var voice = config.Voice;
        if (LabeledInputText("音色短名（选“自定义”后在此填写，如 zh-CN-XiaoxiaoNeural）", "##ttsVoice", ref voice, 128))
        {
            config.Voice = voice;
            dirty = true;
        }

        if (config.Engine == TtsEngine.EdgeTts)
        {
            var style = config.Style ?? string.Empty;
            if (LabeledInputText("表达风格（如 cheerful / angry / whisper，留空不启用）", "##ttsStyle", ref style, 64))
            {
                config.Style = string.IsNullOrWhiteSpace(style) ? null : style;
                dirty = true;
            }

            var degree = config.StyleDegree;
            if (LabeledSliderInt("风格强度", "##ttsStyleDegree", ref degree, 1, 200))
            {
                config.StyleDegree = degree;
                dirty = true;
            }

            var cache = config.CacheAudio;
            if (ImGui.Checkbox("缓存合成音频（相同内容重复播报零延迟）", ref cache))
            {
                config.CacheAudio = cache;
                dirty = true;
            }

            var audioFormat = config.AudioFormat;
            if (LabeledCombo("音频格式（自动 = 优先 MP3，播放失败换 WAV）", "##ttsAudioFormat", ref audioFormat, AudioFormatNames))
            {
                config.AudioFormat = audioFormat;
                dirty = true;
            }

            var fallback = config.FallbackToSapi;
            if (ImGui.Checkbox("失败时回退系统语音", ref fallback))
            {
                config.FallbackToSapi = fallback;
                dirty = true;
            }
        }

        var volume = config.Volume;
        if (LabeledSliderInt("音量", "##ttsVolume", ref volume, 0, 100))
        {
            config.Volume = volume;
            dirty = true;
        }

        var speed = config.Speed;
        if (LabeledSliderInt("语速（100 为正常）", "##ttsSpeed", ref speed, 1, 200))
        {
            config.Speed = speed;
            dirty = true;
        }

        if (config.Engine == TtsEngine.EdgeTts)
        {
            var pitch = config.Pitch;
            if (LabeledSliderInt("音调（100 为正常）", "##ttsPitch", ref pitch, 1, 200))
            {
                config.Pitch = pitch;
                dirty = true;
            }
        }

        var policy = (int)config.Policy;
        if (LabeledCombo("冲突策略（多条播报同时到来时：排队 / 打断 / 丢弃）", "##ttsPolicy", ref policy, PolicyNames))
        {
            config.Policy = (TtsPolicy)policy;
            dirty = true;
        }

        var cooldown = config.CooldownMs;
        if (LabeledSliderInt("去重间隔（毫秒，相同文本在该时间内只播一次）", "##ttsCooldown", ref cooldown, 0, 10000))
        {
            config.CooldownMs = cooldown;
            dirty = true;
        }

        var maxLength = config.MaxLength;
        if (LabeledSliderInt("最大文本长度（超长截断）", "##ttsMaxLength", ref maxLength, 8, 200))
        {
            config.MaxLength = maxLength;
            dirty = true;
        }

        var strip = config.StripBrackets;
        if (ImGui.Checkbox("去除尖括号与方括号内容（过滤游戏消息里的标记）", ref strip))
        {
            config.StripBrackets = strip;
            dirty = true;
        }

        ImGui.Separator();

        var testText = config.TestText;
        if (LabeledInputText("测试文本", "##ttsTestText", ref testText, 256))
        {
            config.TestText = testText;
            dirty = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("试听"))
        {
            Speak(config.TestText);
        }

        if (config.Engine == TtsEngine.CustomCommand)
        {
            var exe = config.CustomExecutable;
            if (LabeledInputText("自定义程序（可执行文件路径）", "##ttsCustomExe", ref exe, 512))
            {
                config.CustomExecutable = exe;
                dirty = true;
            }

            var tpl = config.CustomArguments;
            if (LabeledInputText("参数模板", "##ttsCustomArgs", ref tpl, 512))
            {
                config.CustomArguments = tpl;
                dirty = true;
            }

            ImGui.TextUnformatted("占位符：{text} {voice} {volume} {speed}");
        }

        ImGui.Separator();

        var quickText = config.HotkeyText;
        if (LabeledInputText("快捷播报文本（下方“播报文本”的执行按钮读这一句）", "##ttsQuickText", ref quickText, 256))
        {
            config.HotkeyText = quickText;
            dirty = true;
        }

        DrawCommandList();

        ImGui.TextUnformatted($"状态：{statusText}");
        ImGui.TextUnformatted(
            $"诊断：NAudio={TtsEdgeClient.IsNaudioAvailable()}，上次格式={lastWorkingFormat ?? "无"}，日志={TtsEdgeClient.LogFilePath}");

        return dirty;
    }

    /// <summary>自绘命令列表：描述 + 命令文本，右侧“执行”直接运行、“复制”写入剪贴板。</summary>
    private void DrawCommandList()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("命令（点击“执行”直接运行，或复制到聊天框使用）");

        for (var i = 0; i < CommandRows.Length; i++)
        {
            var (description, command) = CommandRows[i];
            command = command.Replace("{quick}", config.HotkeyText);

            ImGui.TextUnformatted($"{description}  {command}");
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetWindowWidth() - 128);
            if (ImGui.Button($"执行##ttsCmdExec{i}"))
            {
                RunCommand(i);
            }

            ImGui.SameLine();
            if (ImGui.Button($"复制##ttsCmdCopy{i}"))
            {
                ImGui.SetClipboardText(command);
            }
        }
    }

    /// <summary>执行命令列表第 index 行对应的动作，与 TryHandleCommand 中的同名命令一致。</summary>
    private void RunCommand(int index)
    {
        switch (index)
        {
            case 0: Speak(config.HotkeyText); break;
            case 1: Stop(); break;
            case 2: Speak(config.TestText); break;
            case 3: ClearQueue(); break;
            case 4: PurgeCache(); break;
        }
    }

    public override bool ResetSettings()
    {
        var voice = config.Voice;
        var preset = config.VoicePreset;
        var exe = config.CustomExecutable;
        var tpl = config.CustomArguments;
        var replacements = config.PhonemeReplacements;

        config = new TtsSystemConfig
        {
            Voice = voice,
            VoicePreset = preset,
            CustomExecutable = exe,
            CustomArguments = tpl,
            PhonemeReplacements = replacements,
        };
        return true;
    }

    // ---------- 资源释放 ----------

    /// <summary>
    /// 停止播报并释放后台线程。
    /// ModuleBase.Dispose() 本身不可覆写，释放逻辑应挂在 protected virtual OnDispose() 上。
    /// </summary>
    protected override void OnDispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        abortCurrent = true;

        lock (gate)
        {
            pending.Clear();
            currentCts?.Cancel();
            TryKillCurrent();
        }

        Instance = null;
        base.OnDispose();
    }

    // ---------- 播报调度 ----------

    private void EnsureWorker()
    {
        if (workerRunning || disposed)
        {
            return;
        }

        workerRunning = true;
        worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "OmniTtsWorker",
        };
        worker.Start();
    }

    private void WorkerLoop()
    {
        while (true)
        {
            string text;
            lock (gate)
            {
                if (pending.Count == 0)
                {
                    workerRunning = false;
                    statusText = "空闲";
                    return;
                }

                text = pending.Dequeue();
            }

            if (disposed)
            {
                return;
            }

            statusText = $"播报中：{text}";
            SpeakNow(text);
        }
    }

    private void SpeakNow(string text)
    {
        var cts = new CancellationTokenSource();
        lock (gate)
        {
            currentCts = cts;
        }

        try
        {
            var played = false;

            if (config.Engine == TtsEngine.EdgeTts)
            {
                played = SpeakViaEdge(text, cts.Token);

                if (!played && config.FallbackToSapi && !cts.IsCancellationRequested)
                {
                    statusText = $"Edge 不可用（{lastEdgeError ?? "播放失败"}），回退系统语音";
                    LogEdge($"回退 SAPI：{lastEdgeError}");
                    played = SpeakViaSapi(text);
                }
            }
            else if (config.Engine == TtsEngine.SystemSapi)
            {
                played = SpeakViaSapi(text);
            }
            else
            {
                played = SpeakViaCustom(text);
            }

            if (!played && statusText.StartsWith("播报中", StringComparison.Ordinal))
            {
                statusText = "播报未完成";
            }
        }
        catch (Exception ex)
        {
            statusText = $"播报失败：{ex.Message}";
        }
        finally
        {
            lock (gate)
            {
                currentCts = null;
            }

            cts.Dispose();
        }
    }

    // ---------- 引擎：Edge TTS ----------

    private bool SpeakViaEdge(string text, CancellationToken token)
    {
        // 合成失败（网络/限流）与格式无关，直接终止不再换格式重试；
        // 只有「合成成功但播放失败」才值得换另一种格式再试。
        var order = PreferredFormats();
        var errors = new List<string>(order.Length);
        for (var i = 0; i < order.Length; i++)
        {
            var outcome = TrySpeakWith(text, order[i], token);
            if (outcome == SpeakOutcome.Success)
            {
                return true;
            }

            errors.Add($"{order[i]}: {(lastEdgeError ?? (outcome == SpeakOutcome.PlayFailed ? "播放失败" : "合成失败"))}");

            if (outcome == SpeakOutcome.SynthFailed)
            {
                break;
            }

            if (i == 0)
            {
                statusText = $"{order[0]} 播放不可用，改用 {order[1]}";
            }
        }

        lastEdgeError = string.Join("；", errors);
        return false;
    }

    private enum SpeakOutcome
    {
        Success,
        SynthFailed,
        PlayFailed,
    }

    /// <summary>最近一次 Edge 失败的原因，用于回退时在状态行显示真实原因。</summary>
    private string? lastEdgeError;

    private string[] PreferredFormats()
    {
        var preferred = config.AudioFormat switch
        {
            1 => "wav",
            _ => lastWorkingFormat ?? "mp3",
        };

        return string.Equals(preferred, "mp3", StringComparison.OrdinalIgnoreCase)
            ? ["mp3", "wav"]
            : ["wav", "mp3"];
    }

    /// <summary>已验证可用的格式，避免每次失败都重新合成一遍另一种格式。</summary>
    private static volatile string? lastWorkingFormat;

    /// <summary>
    /// 后台预热：先建立连接，再把常用文本合成进缓存，让首次触发几乎无感。重复调用无副作用。
    /// </summary>
    private void PreheatAsync()
    {
        if (config.Engine != TtsEngine.EdgeTts)
        {
            return;
        }

        TtsEdgeClient.WarmUp();

        if (!config.CacheAudio)
        {
            return;
        }

        var format = PreferredFormats()[0];
        Task.Run(() =>
        {
            foreach (var text in new[] { config.HotkeyText, config.TestText })
            {
                if (disposed || string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var request = BuildRequest(text, format);
                try
                {
                    if (TtsEdgeClient.TryGetCached(request) is not null)
                    {
                        continue;
                    }

                    var audio = TtsEdgeClient.SynthesizeAsync(request).GetAwaiter().GetResult();
                    if (audio is { Length: > 0 })
                    {
                        TtsEdgeClient.SaveToCache(request, audio);
                    }
                }
                catch
                {
                    // 预热失败不影响后续正常合成。
                }
            }
        });
    }

    private TtsEdgeRequest BuildRequest(string text, string format) => new()
    {
        Text = text,
        Voice = config.Voice,
        Speed = config.Speed,
        Pitch = config.Pitch,
        Volume = config.Volume,
        Style = config.Style,
        StyleDegree = config.StyleDegree,
        OutputFormat = format,
    };

    private SpeakOutcome TrySpeakWith(string text, string format, CancellationToken token)
    {
        string? file = null;

        try
        {
            var request = BuildRequest(text, format);

            var cached = config.CacheAudio ? TtsEdgeClient.TryGetCached(request) : null;
            if (cached is not null)
            {
                file = cached;
                LogEdge($"播放缓存 {format} {Path.GetFileName(file)}");
            }
            else
            {
                statusText = "合成中…";
                var sw = Stopwatch.StartNew();
                var audio = TtsEdgeClient.SynthesizeAsync(request, token).GetAwaiter().GetResult();
                LogEdge($"合成 {format} 耗时 {sw.ElapsedMilliseconds}ms -> {(audio is { Length: > 0 } ? audio.Length + "B" : "空音频")}");
                if (audio is null || audio.Length == 0)
                {
                    lastEdgeError = string.Equals(format, "wav", StringComparison.OrdinalIgnoreCase)
                        ? "服务端不支持 WAV（返回空音频）"
                        : "服务端返回空音频（可能被限流，稍后再试）";
                    LogEdge(lastEdgeError);
                    return SpeakOutcome.SynthFailed;
                }

                file = config.CacheAudio
                    ? TtsEdgeClient.SaveToCache(request, audio)
                    : TtsEdgeClient.WriteTemp(request, audio);
            }

            if (file is null || abortCurrent || token.IsCancellationRequested)
            {
                return SpeakOutcome.SynthFailed;
            }

            var played = TtsAudioBackend.Play(file, () => abortCurrent || token.IsCancellationRequested);
            LogEdge(played ? $"播放成功 {format}" : $"播放失败 {format}");
            if (played)
            {
                lastWorkingFormat = format;
                lastEdgeError = null;
                return SpeakOutcome.Success;
            }

            lastEdgeError = $"{format} 播放失败";
            return SpeakOutcome.PlayFailed;
        }
        catch (OperationCanceledException)
        {
            LogEdge("合成被取消或超时");
            lastEdgeError = "合成超时或被取消";
            return SpeakOutcome.SynthFailed;
        }
        catch (Exception ex)
        {
            LogEdge($"合成异常 {ex.GetType().Name}: {ex.Message}");
            lastEdgeError = $"{ex.GetType().Name}: {ex.Message}";
            return SpeakOutcome.SynthFailed;
        }
        finally
        {
            if (file is not null && !config.CacheAudio)
            {
                TryDelete(file);
            }
        }
    }

    /// <summary>
    /// 诊断日志：把游戏进程内的合成/播放结果落盘，供排查 Omni 环境下的失败原因。
    /// 文件位于 %TEMP%\OmniTtsCache\tts.log，失败不影响播报。
    /// </summary>
    internal static readonly object LogGate = new();
    private static bool logDirectoryReady;

    internal static void LogEdge(string message)
    {
        try
        {
            lock (LogGate)
            {
                if (!logDirectoryReady)
                {
                    Directory.CreateDirectory(TtsEdgeClient.CacheDirectory);
                    logDirectoryReady = true;
                }

                File.AppendAllText(
                    TtsEdgeClient.LogFilePath,
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志写入失败不影响播报。
        }
    }

    // ---------- 引擎：系统 SAPI ----------

    private bool SpeakViaSapi(string text)
    {
        // 文本与参数全部走环境变量传入，命令行本身不含用户输入，避免引号与转义问题。
        var script = new StringBuilder()
            .AppendLine("Add-Type -AssemblyName System.Speech")
            .AppendLine("$s = New-Object System.Speech.Synthesis.SpeechSynthesizer")
            .AppendLine("$s.Volume = [int]$env:OMNI_TTS_VOLUME")
            .AppendLine("$s.Rate = [int]$env:OMNI_TTS_RATE")
            .AppendLine("if ($env:OMNI_TTS_VOICE) { try { $s.SelectVoice($env:OMNI_TTS_VOICE) } catch { } }")
            .AppendLine("$s.Speak($env:OMNI_TTS_TEXT)")
            .ToString();

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Sta");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

        startInfo.Environment["OMNI_TTS_TEXT"] = text;
        startInfo.Environment["OMNI_TTS_VOLUME"] = config.Volume.ToString();
        startInfo.Environment["OMNI_TTS_RATE"] = SapiRate.ToString();
        startInfo.Environment["OMNI_TTS_VOICE"] = config.Voice;

        try
        {
            using var process = new Process { StartInfo = startInfo };
            lock (gate)
            {
                currentProcess = process;
            }

            if (!process.Start())
            {
                return false;
            }

            process.WaitForExit();
            return true;
        }
        catch (Exception ex)
        {
            statusText = $"系统语音失败：{ex.Message}";
            return false;
        }
        finally
        {
            lock (gate)
            {
                currentProcess = null;
            }
        }
    }

    private int SapiRate => Math.Clamp((config.Speed - 100) / 10, -10, 10);

    // ---------- 引擎：自定义命令行 ----------

    private bool SpeakViaCustom(string text)
    {
        if (string.IsNullOrWhiteSpace(config.CustomExecutable))
        {
            statusText = "未配置自定义程序";
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = config.CustomExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        foreach (var part in (config.CustomArguments ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            startInfo.ArgumentList.Add(part
                .Replace("{text}", text)
                .Replace("{voice}", config.Voice)
                .Replace("{volume}", config.Volume.ToString())
                .Replace("{speed}", config.Speed.ToString()));
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            lock (gate)
            {
                currentProcess = process;
            }

            return process.Start() && process.WaitForExit(60000);
        }
        catch (Exception ex)
        {
            statusText = $"自定义播报失败：{ex.Message}";
            return false;
        }
        finally
        {
            lock (gate)
            {
                currentProcess = null;
            }
        }
    }

    // ---------- 辅助 ----------

    private void TryKillCurrent()
    {
        var process = currentProcess;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch
        {
            // 进程可能已退出，忽略。
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch
        {
            // 临时文件删除失败不影响播报。
        }
    }

    private string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var result = text.Replace('\r', ' ').Replace('\n', ' ').Trim();

        if (config.StripBrackets)
        {
            result = BracketPattern1.Replace(result, string.Empty);
            result = BracketPattern2.Replace(result, string.Empty);
            result = result.Replace("  ", " ").Trim();
        }

        if (config.PhonemeReplacements is { Count: > 0 })
        {
            foreach (var pair in config.PhonemeReplacements)
            {
                if (!string.IsNullOrEmpty(pair.Key))
                {
                    result = result.Replace(pair.Key, pair.Value ?? string.Empty);
                }
            }
        }

        if (result.Length > config.MaxLength)
        {
            result = result[..config.MaxLength];
        }

        return result;
    }

    /// <summary>
    /// 浅拷贝配置，用于「临时改写参数再还原」的调用。
    /// 用 MemberwiseClone 而非逐字段罗列，避免新增配置项时漏拷（数组为浅拷贝，此处无需深拷贝）。
    /// </summary>
    private static readonly System.Reflection.MethodInfo ShallowClone =
        typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    private static TtsSystemConfig CloneConfig(TtsSystemConfig source) =>
        (TtsSystemConfig)ShallowClone.Invoke(source, null)!;

    // 不用 GeneratedRegex：在线模块由宿主用 Roslyn 动态编译，未必启用源生成器。
    private static readonly System.Text.RegularExpressions.Regex BracketPattern1 =
        new("<[^>]*>", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex BracketPattern2 =
        new(@"\[[^\]]*\]", System.Text.RegularExpressions.RegexOptions.Compiled);
}

/// <summary>
/// Edge TTS 合成请求。全部字段参与缓存键计算。
/// </summary>
public sealed class TtsEdgeRequest
{
    public string Text { get; set; } = string.Empty;
    public string Voice { get; set; } = "zh-CN-XiaoxiaoNeural";
    public int Speed { get; set; } = 100;
    public int Pitch { get; set; } = 100;
    public int Volume { get; set; } = 100;
    public string? Style { get; set; }
    public int StyleDegree { get; set; } = 100;

    /// <summary>输出格式，wav 或 mp3。wav 可用系统自带组件直接播放。</summary>
    public string OutputFormat { get; set; } = "wav";
}

/// <summary>
/// Edge TTS 客户端：通过 WebSocket 直连微软 Edge 的神经网络语音服务。
/// 仅依赖 BCL 的 ClientWebSocket 与 SHA256，无需密钥，也无需第三方程序集。
/// </summary>
public static class TtsEdgeClient
{
    private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const string SecGecVersion = "1-134.0.3124.66";
    private const string WssUrl =
        "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1?TrustedClientToken=" + TrustedClientToken;
    private const int TimeoutMs = 10000;
    private const int BufferSize = 4096;

    // 复用的连接最长存活时间，超时后下次使用时重建，避免服务端静默断开。
    private const int PooledLifetimeSeconds = 240;

    private static readonly SemaphoreSlim SynthesisGate = new(1, 1);
    private static WebSocket? pooledSocket;
    private static DateTimeOffset pooledSince;

    // WAV 可由 winmm 的 PlaySound 直接播放，无需任何解码器与第三方库，因此作为默认格式。
    private const string OutputWav = "riff-24khz-16bit-mono-pcm";
    private const string OutputMp3 = "audio-24khz-48kbitrate-mono-mp3";

    public static string CacheDirectory =>
        Path.Combine(Path.GetTempPath(), "OmniTtsCache");

    /// <summary>把 wav / mp3 简写映射为服务端 outputFormat。</summary>
    public static string ResolveOutputFormat(string format) =>
        string.Equals(format, "mp3", StringComparison.OrdinalIgnoreCase) ? OutputMp3 : OutputWav;

    /// <summary>按格式决定缓存文件扩展名。</summary>
    public static string ResolveExtension(string format) =>
        string.Equals(format, "mp3", StringComparison.OrdinalIgnoreCase) ? ".mp3" : ".wav";

    /// <summary>探测宿主是否已提供 NAudio，决定能否直接播放 MP3。结果会被缓存。</summary>
    public static bool IsNaudioAvailable() => FindNaudioTypes(out _, out _);

    // 探测要遍历全部已加载程序集，而设置界面每帧都会调用，因此成功结果必须缓存；
    // 失败结果不缓存——NAudio 可能是在模块之后才被宿主加载的。
    private static bool naudioResolved;
    private static Type? cachedReaderType;
    private static Type? cachedWaveOutType;

    /// <summary>诊断日志路径，界面与日志写入共用同一份。</summary>
    public static string LogFilePath => Path.Combine(CacheDirectory, "tts.log");

    /// <summary>
    /// 遍历已加载程序集查找 NAudio 类型并按兼容性配对。
    /// 游戏进程内可能同时存在多份 NAudio：reader 与 WaveOutEvent 若来自不同版本，
    /// 其 IWaveProvider 是不同类型，Init 反射调用会抛 ArgumentException。
    /// 因此先取 WaveOutEvent 的 Init 参数类型（IWaveProvider），再找能赋值给它的 reader。
    /// </summary>
    internal static bool FindNaudioTypes(out Type? readerType, out Type? waveOutType)
    {
        if (naudioResolved)
        {
            readerType = cachedReaderType;
            waveOutType = cachedWaveOutType;
            return readerType is not null && waveOutType is not null;
        }

        readerType = null;
        waveOutType = null;
        Type? foundReader = null;
        Type? foundWaveOut = null;

        try
        {
            var readers = new List<Type>();
            var waveOuts = new List<Type>();

            // NAudio 拆分为多个程序集，需跨程序集分别收集两个类型。
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Collect(assembly, readers, waveOuts);
            }

            // 已加载的没有，再按需加载一次（宿主携带但未使用过时不会自动加载）。
            foreach (var name in new[] { "NAudio.Core", "NAudio.WinMM", "NAudio.Wasapi", "NAudio" })
            {
                System.Reflection.Assembly assembly;
                try
                {
                    assembly = System.Reflection.Assembly.Load(new System.Reflection.AssemblyName(name));
                }
                catch
                {
                    continue;
                }

                Collect(assembly, readers, waveOuts);
            }

            foreach (var waveOut in waveOuts)
            {
                var initParameters = waveOut.GetMethod("Init")?.GetParameters();
                if (initParameters is null || initParameters.Length == 0)
                {
                    continue;
                }

                var providerType = initParameters[0].ParameterType;
                Type? reader = null;
                foreach (var candidate in readers)
                {
                    if (providerType.IsAssignableFrom(candidate))
                    {
                        reader = candidate;
                        break;
                    }
                }

                if (reader is null)
                {
                    continue;
                }

                foundReader = reader;
                foundWaveOut = waveOut;
                break;
            }
        }
        catch
        {
            return false;
        }

        if (foundReader is null || foundWaveOut is null)
        {
            return false;
        }

        naudioResolved = true;
        cachedReaderType = foundReader;
        cachedWaveOutType = foundWaveOut;
        readerType = foundReader;
        waveOutType = foundWaveOut;
        return true;
    }
    private static void Collect(System.Reflection.Assembly assembly, List<Type> readers, List<Type> waveOuts)
    {
        try
        {
            foreach (var name in new[] { "NAudio.Wave.AudioFileReader", "NAudio.Wave.MediaFoundationReader" })
            {
                if (assembly.GetType(name) is { } reader && !readers.Contains(reader))
                {
                    readers.Add(reader);
                }
            }

            if (assembly.GetType("NAudio.Wave.WaveOutEvent") is { } waveOut && !waveOuts.Contains(waveOut))
            {
                waveOuts.Add(waveOut);
            }
        }
        catch
        {
            // 部分程序集反射会抛异常，忽略后继续。
        }
    }

    /// <summary>合成语音，返回音频字节；失败返回 null。</summary>
    public static async Task<byte[]?> SynthesizeAsync(TtsEdgeRequest request, CancellationToken cancellationToken = default)
    {
        await SynthesisGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 复用已有连接：单次握手约 0.7–1.3 秒，占整体耗时一半以上，跳过握手能显著缩短首字音延迟。
            if (TryTakePooled(out var pooled))
            {
                TtsSystem.LogEdge("复用连接开始合成");
                try
                {
                    var reused = await SynthesizeOnAsync(pooled!, request, cancellationToken).ConfigureAwait(false);
                    if (reused is { Length: > 0 })
                    {
                        pooledSince = DateTimeOffset.UtcNow;
                        return reused;
                    }

                    TtsSystem.LogEdge("复用连接返回空音频");
                }
                catch (Exception ex)
                {
                    TtsSystem.LogEdge($"复用连接失败 {ex.GetType().Name}: {ex.Message}，转新建");
                }

                DropSocket(pooled!);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeoutMs);

            TtsSystem.LogEdge("新建连接…");
            var sw = Stopwatch.StartNew();
            var ws = await ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
            TtsSystem.LogEdge($"握手完成 {sw.ElapsedMilliseconds}ms");
            var audio = await SynthesizeOnAsync(ws, request, timeoutCts.Token).ConfigureAwait(false);
            if (audio is { Length: > 0 })
            {
                pooledSocket = ws;
                pooledSince = DateTimeOffset.UtcNow;
                return audio;
            }

            TtsSystem.LogEdge("新建连接返回空音频");
            DropSocket(ws);
            return null;
        }
        finally
        {
            SynthesisGate.Release();
        }
    }

    /// <summary>后台预热连接，让首次播报不必等待握手。重复调用无副作用。</summary>
    public static void WarmUp()
    {
        Task.Run(async () =>
        {
            try
            {
                await SynthesisGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (pooledSocket is { State: WebSocketState.Open })
                    {
                        return;
                    }

                    using var cts = new CancellationTokenSource(TimeoutMs);
                    pooledSocket = await ConnectAsync(cts.Token).ConfigureAwait(false);
                    pooledSince = DateTimeOffset.UtcNow;
                }
                finally
                {
                    SynthesisGate.Release();
                }
            }
            catch
            {
                // 预热失败不影响后续自动重连。
            }
        });
    }

    /// <summary>释放复用的连接。</summary>
    public static void CloseConnection()
    {
        try
        {
            SynthesisGate.Wait();
            try
            {
                if (pooledSocket is not null)
                {
                    DropSocket(pooledSocket);
                    pooledSocket = null;
                }
            }
            finally
            {
                SynthesisGate.Release();
            }
        }
        catch
        {
            // 忽略释放异常。
        }
    }

    private static bool TryTakePooled(out WebSocket? socket)
    {
        socket = null;
        var pooled = pooledSocket;
        if (pooled is null)
        {
            return false;
        }

        if (pooled.State != WebSocketState.Open ||
            DateTimeOffset.UtcNow - pooledSince > TimeSpan.FromSeconds(PooledLifetimeSeconds))
        {
            DropSocket(pooled);
            pooledSocket = null;
            return false;
        }

        socket = pooled;
        return true;
    }

    private static void DropSocket(WebSocket socket)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                socket.Abort();
            }
        }
        catch
        {
            // 忽略关闭异常。
        }
        finally
        {
            try { socket.Dispose(); } catch { /* ignored */ }
        }
    }

    private static async Task<WebSocket> ConnectAsync(CancellationToken token)
    {
        var ws = new ClientWebSocket();
        try
        {
            Configure(ws);
            var url = $"{WssUrl}&Sec-MS-GEC={GenerateSecMsGec()}&Sec-MS-GEC-Version={SecGecVersion}&ConnectionId={Guid.NewGuid():N}";
            await ws.ConnectAsync(new Uri(url), token).ConfigureAwait(false);
            return ws;
        }
        catch
        {
            DropSocket(ws);
            throw;
        }
    }

    private static async Task<byte[]?> SynthesizeOnAsync(WebSocket ws, TtsEdgeRequest request, CancellationToken token)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffK");

        await SendAsync(ws, BuildConfig(requestId, timestamp, request), token).ConfigureAwait(false);
        await SendAsync(ws, BuildSsml(requestId, timestamp, request), token).ConfigureAwait(false);

        return await ReceiveAudioAsync(ws, requestId, token).ConfigureAwait(false);
    }

    /// <summary>生成 Sec-MS-GEC：取当前 Windows 文件时间向下取整到 300 秒，拼上可信令牌后取 SHA256。</summary>
    public static string GenerateSecMsGec()
    {
        var ticks = DateTime.Now.ToFileTimeUtc();
        var str = $"{ticks - ticks % 3_000_000_000}{TrustedClientToken}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(str))).ToUpperInvariant();
    }

    public static string? TryGetCached(TtsEdgeRequest request)
    {
        var path = CachePath(request);
        return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    public static string SaveToCache(TtsEdgeRequest request, byte[] audio)
    {
        Directory.CreateDirectory(CacheDirectory);
        var path = CachePath(request);
        File.WriteAllBytes(path, audio);
        return path;
    }

    public static string WriteTemp(TtsEdgeRequest request, byte[] audio)
    {
        Directory.CreateDirectory(CacheDirectory);
        var path = Path.Combine(CacheDirectory, $"tmp-{Guid.NewGuid():N}{ResolveExtension(request.OutputFormat)}");
        File.WriteAllBytes(path, audio);
        return path;
    }

    public static string CachePath(TtsEdgeRequest request)
    {
        var key = string.Join('|', request.Text, request.Voice, request.Speed, request.Pitch, request.Volume,
            request.Style ?? string.Empty, request.StyleDegree, ResolveOutputFormat(request.OutputFormat));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20];
        return Path.Combine(CacheDirectory, hash + ResolveExtension(request.OutputFormat));
    }

    private static void Configure(ClientWebSocket ws)
    {
        ws.Options.SetRequestHeader("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36 Edg/134.0.0.0");
        ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        ws.Options.SetRequestHeader("Accept-Encoding", "gzip, deflate, br");
        ws.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        ws.Options.SetRequestHeader("Pragma", "no-cache");
    }

    private static string BuildConfig(string requestId, string timestamp, TtsEdgeRequest request) =>
        new StringBuilder()
            .AppendLine("Path:speech.config")
            .AppendLine($"X-RequestID:{requestId}")
            .AppendLine($"X-Timestamp:{timestamp}")
            .AppendLine("Content-Type:application/json")
            .AppendLine()
            .AppendLine($"{{\"context\":{{\"synthesis\":{{\"audio\":{{\"metadataoptions\":{{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"}},\"outputFormat\":\"{ResolveOutputFormat(request.OutputFormat)}\"}}}}}}}}")
            .ToString();

    private static string BuildSsml(string requestId, string timestamp, TtsEdgeRequest request) =>
        new StringBuilder()
            .AppendLine("Path:ssml")
            .AppendLine($"X-RequestID:{requestId}")
            .AppendLine($"X-Timestamp:{timestamp}")
            .AppendLine("Content-Type:application/ssml+xml")
            .AppendLine()
            .AppendLine(BuildSsmlBody(request))
            .ToString();

    private static string BuildSsmlBody(TtsEdgeRequest request)
    {
        var rate = Math.Clamp(request.Speed, 1, 200) - 100;
        var pitch = (Math.Clamp(request.Pitch, 1, 200) - 100) / 2;
        var volume = Math.Clamp(request.Volume, 1, 100);

        var sb = new StringBuilder()
            .Append("<speak xmlns=\"http://www.w3.org/2001/10/synthesis\" xmlns:mstts=\"http://www.w3.org/2001/mstts\" version=\"1.0\" xml:lang=\"en-US\">")
            .Append($"<voice name=\"{EscapeXml(request.Voice)}\">")
            .Append($"<prosody rate=\"{rate}%\" pitch=\"{pitch}%\" volume=\"{volume}\">")
            .Append(BuildExpressAs(request))
            .Append("</prosody></voice></speak>");

        return sb.ToString();
    }

    private static string BuildExpressAs(TtsEdgeRequest request)
    {
        var hasStyle = !string.IsNullOrWhiteSpace(request.Style) &&
                       !string.Equals(request.Style, "general", StringComparison.OrdinalIgnoreCase);

        if (!hasStyle)
        {
            return request.Text;
        }

        var degree = Math.Max(1, request.StyleDegree) / 100f;
        return new StringBuilder("<mstts:express-as")
            .Append($" style=\"{EscapeXml(request.Style!)}\"")
            .Append($" styledegree=\"{degree.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"")
            .Append('>')
            .Append(request.Text)
            .Append("</mstts:express-as>")
            .ToString();
    }

    private static string EscapeXml(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&apos;",
                _ => c.ToString(),
            });
        }

        return sb.ToString();
    }

    private static async Task SendAsync(WebSocket ws, string message, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReceiveAudioAsync(WebSocket ws, string requestId, CancellationToken token)
    {
        using var audio = new MemoryStream();
        var receiveBuffer = new byte[BufferSize];
        var messageBuffer = new List<byte>();
        var streaming = false;

        while (!token.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), token).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return audio.Length > 0 ? audio.ToArray() : null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return audio.Length > 0 ? audio.ToArray() : null;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var message = Encoding.UTF8.GetString(receiveBuffer, 0, result.Count);
                if (message.Contains(requestId, StringComparison.Ordinal) && message.Contains("Path:turn.start", StringComparison.Ordinal))
                {
                    streaming = true;
                }
                else if (streaming && message.Contains("Path:turn.end", StringComparison.Ordinal))
                {
                    return audio.ToArray();
                }

                continue;
            }

            messageBuffer.AddRange(new ArraySegment<byte>(receiveBuffer, 0, result.Count));
            if (!result.EndOfMessage)
            {
                continue;
            }

            var data = messageBuffer.ToArray();
            messageBuffer.Clear();

            if (data.Length < 2)
            {
                continue;
            }

            // 前 2 字节为大端序的头部长度。
            var headerLength = (data[0] << 8) | data[1];
            if (data.Length < 2 + headerLength)
            {
                continue;
            }

            var header = Encoding.UTF8.GetString(data, 2, headerLength);
            if (!header.EndsWith("Path:audio\r\n", StringComparison.Ordinal))
            {
                continue;
            }

            await audio.WriteAsync(data.AsMemory(2 + headerLength), token).ConfigureAwait(false);
            streaming = true;
        }

        return audio.Length > 0 ? audio.ToArray() : null;
    }
}

/// <summary>
/// 音频播放后端：全部走系统组件或宿主已有程序集，不引入第三方依赖。
/// WAV 由 winmm 的 PlaySound 直接播放；MP3 优先用宿主提供的 NAudio（反射探测），
/// 再退到 MCI，都不可用则返回 false，由调用方降级为 WAV 重新合成。
/// 音量在 SSML 的 prosody volume 阶段已经生效，播放端无需再调节。
/// </summary>
public static class TtsAudioBackend
{
    public static bool Play(string file, Func<bool> shouldStop)
    {
        if (!File.Exists(file) || shouldStop())
        {
            return false;
        }

        if (file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            return TtsWinmmSound.PlaySync(file);
        }

        return TryPlayMp3WithNaudio(file, shouldStop) || TtsMciPlayer.Play(file, shouldStop);
    }

    private static bool TryPlayMp3WithNaudio(string file, Func<bool> shouldStop)
    {
        // 反射调用，避免编译期依赖 NAudio；宿主未提供时回退 MCI。
        try
        {
            if (!TtsEdgeClient.FindNaudioTypes(out var readerType, out var waveOutType)
                || readerType is null || waveOutType is null)
            {
                TtsSystem.LogEdge("NAudio 类型未找到");
                return false;
            }

            // Init 参数类型（IWaveProvider）必须与 reader 同源，跨版本混用会抛 ArgumentException。
            var initParameters = waveOutType.GetMethod("Init")?.GetParameters();
            var providerType = initParameters is { Length: > 0 } ? initParameters[0].ParameterType : null;

            // AudioFileReader 依赖 ACM/Media Foundation 解码 MP3；失败时退到 MediaFoundationReader。
            object? reader = null;
            Exception? readerError = null;
            try
            {
                reader = Activator.CreateInstance(readerType, file);
            }
            catch (Exception ex)
            {
                readerError = ex;
            }

            if (reader is null)
            {
                TtsSystem.LogEdge($"AudioFileReader 创建失败 {readerError?.InnerException?.GetType().Name ?? readerError?.GetType().Name}: {readerError?.InnerException?.Message ?? readerError?.Message}");
                reader = TryCreateViaMediaFoundation(file);
            }

            if (reader is null
                || providerType is null
                || !providerType.IsInstanceOfType(reader))
            {
                TtsSystem.LogEdge(reader is null
                    ? "MediaFoundationReader 也创建失败"
                    : $"reader 与 {providerType?.Name} 不兼容，弃用");
                (reader as IDisposable)?.Dispose();
                return false;
            }

            if (readerType is null || !reader.GetType().Equals(readerType))
            {
                TtsSystem.LogEdge("已改用 MediaFoundationReader 打开");
            }

            var waveOut = Activator.CreateInstance(waveOutType);
            if (waveOut is null)
            {
                TtsSystem.LogEdge("WaveOutEvent 创建失败");
                (reader as IDisposable)?.Dispose();
                return false;
            }

            try
            {
                waveOutType.GetMethod("Init")?.Invoke(waveOut, [reader]);
                waveOutType.GetMethod("Play")?.Invoke(waveOut, null);

                var stateProperty = waveOutType.GetProperty("PlaybackState");
                var state = stateProperty?.GetValue(waveOut)?.ToString();
                TtsSystem.LogEdge($"WaveOut 已启动，状态={state}");
                while (!shouldStop())
                {
                    Thread.Sleep(80);
                    state = stateProperty?.GetValue(waveOut)?.ToString();
                    if (!string.Equals(state, "Playing", StringComparison.Ordinal))
                    {
                        break;
                    }
                }

                waveOutType.GetMethod("Stop")?.Invoke(waveOut, null);
                return true;
            }
            finally
            {
                (waveOut as IDisposable)?.Dispose();
                (reader as IDisposable)?.Dispose();
            }
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException ?? ex;
            TtsSystem.LogEdge($"NAudio 播放异常 {ex.GetType().Name}/{inner.GetType().Name}: {inner.Message}");
            return false;
        }
    }

    /// <summary>Media Foundation 解码路径：反射创建 MediaFoundationReader，不依赖 ACM。</summary>
    private static object? TryCreateViaMediaFoundation(string file)
    {
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var mfType = assembly.GetType("NAudio.Wave.MediaFoundationReader");
                if (mfType is null)
                {
                    continue;
                }

                return Activator.CreateInstance(mfType, file);
            }

            TtsSystem.LogEdge("MediaFoundationReader 类型未找到");
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException ?? ex;
            TtsSystem.LogEdge($"MediaFoundationReader 创建失败 {inner.GetType().Name}: {inner.Message}");
        }

        return null;
    }
}

/// <summary>winmm PlaySound：系统自带，直接播放 WAV，无需解码器与第三方库。</summary>
public static class TtsWinmmSound
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint flags);

    private const uint SND_FILENAME = 0x00020000;
    private const uint SND_SYNC = 0x00000000;
    private const uint SND_NODEFAULT = 0x00000002;

    /// <summary>同步播放 WAV 文件，播放完成或被取消后返回。</summary>
    public static bool PlaySync(string file) =>
        PlaySound(file, IntPtr.Zero, SND_FILENAME | SND_SYNC | SND_NODEFAULT);

    /// <summary>取消当前正在播放的声音，用于实现打断。</summary>
    public static void Cancel() =>
        PlaySound(null, IntPtr.Zero, 0);
}

/// <summary>winmm MCI 播放器：系统自带，用于播放 MP3。部分系统缺少 MPEG 解码器会失败，此时应降级为 WAV。</summary>
public static class TtsMciPlayer
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? returnBuffer, int returnLength, IntPtr callback);

    public static bool Play(string file, Func<bool> shouldStop)
    {
        var alias = "omniTts" + (Environment.TickCount64 % 100000).ToString(System.Globalization.CultureInfo.InvariantCulture);

        var openResult = Send($"open \"{file}\" type mpegvideo alias {alias}");
        if (openResult != 0)
        {
            TtsSystem.LogEdge($"MCI 打开 MP3 失败 rc={openResult}（缺 MPEG 解码器时常见）");
            return false;
        }

        try
        {
            Send($"play {alias}");

            while (!shouldStop())
            {
                Thread.Sleep(80);
                var buffer = new StringBuilder(64);
                mciSendString($"status {alias} mode", buffer, buffer.Capacity, IntPtr.Zero);
                var mode = buffer.ToString().Trim();
                if (mode.Length == 0 || string.Equals(mode, "stopped", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            return true;
        }
        finally
        {
            Send($"stop {alias}");
            Send($"close {alias}");
        }
    }

    private static int Send(string command) =>
        mciSendString(command, null, 0, IntPtr.Zero);
}

/// <summary>
/// 反射调用桥：供其他在线模块在不产生编译期引用的情况下调用 TTS 模块。
/// 复制到自己的模块文件中时请修改类型名，避免与其他模块的同名辅助类型冲突。
/// </summary>
public static class TtsBridge
{
    private static object? cachedInstance;
    private static System.Reflection.MethodInfo? cachedSpeak;

    public static bool Available => EnsureResolved();

    public static bool Say(string text)
    {
        if (!EnsureResolved() || cachedInstance is null || cachedSpeak is null)
        {
            return false;
        }

        try
        {
            return cachedSpeak.Invoke(cachedInstance, [text]) is true;
        }
        catch
        {
            return false;
        }
    }

    private static bool EnsureResolved()
    {
        if (cachedInstance is not null && cachedSpeak is not null)
        {
            return true;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type;
            try
            {
                type = assembly.GetType("OmniToolbox.TreeHouseOnline.TtsSystem");
            }
            catch
            {
                continue;
            }

            if (type is null)
            {
                continue;
            }

            var instanceProperty = type.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var instance = instanceProperty?.GetValue(null);
            var speak = type.GetMethod("Speak", [typeof(string)]);

            if (instance is null || speak is null)
            {
                continue;
            }

            cachedInstance = instance;
            cachedSpeak = speak;
            return true;
        }

        return false;
    }
}

public enum TtsEngine
{
    EdgeTts = 0,
    SystemSapi = 1,
    CustomCommand = 2,
}

public enum TtsPolicy
{
    Queue = 0,
    Interrupt = 1,
    DropIfBusy = 2,
}

public sealed class TtsSystemConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>播报引擎，默认使用 Edge TTS。</summary>
    public TtsEngine Engine { get; set; } = TtsEngine.EdgeTts;

    /// <summary>音色短名，Edge TTS 形如 zh-CN-XiaoxiaoNeural。</summary>
    public string Voice { get; set; } = "zh-CN-XiaoxiaoNeural";

    /// <summary>设置界面中音色下拉的选中项。</summary>
    public int VoicePreset { get; set; }

    /// <summary>SSML 表达风格，为空时不启用。</summary>
    public string? Style { get; set; }

    public int StyleDegree { get; set; } = 100;

    public int Volume { get; set; } = 100;

    /// <summary>语速，100 为正常，范围 1–200。</summary>
    public int Speed { get; set; } = 100;

    /// <summary>音调，100 为正常，范围 1–200，仅 Edge TTS 生效。</summary>
    public int Pitch { get; set; } = 100;

    public TtsPolicy Policy { get; set; } = TtsPolicy.Queue;

    public int CooldownMs { get; set; } = 1500;

    public int MaxLength { get; set; } = 80;

    public bool StripBrackets { get; set; } = true;

    /// <summary>缓存 Edge TTS 合成结果，相同内容重复播报时零延迟。</summary>
    public bool CacheAudio { get; set; } = true;

    /// <summary>音频格式：0 自动（优先上次可用格式，默认 MP3）、1 强制 WAV、2 强制 MP3。</summary>
    public int AudioFormat { get; set; }

    /// <summary>Edge TTS 失败（如断网）时回退到系统语音。</summary>
    public bool FallbackToSapi { get; set; } = true;

    public string TestText { get; set; } = "语音测试";

    /// <summary>发音替换表，用于修正游戏专有名词的读法。</summary>
    public Dictionary<string, string> PhonemeReplacements { get; set; } = new()
    {
        ["欧米茄"] = "欧米加",
        ["歐米茄"] = "歐米加",
        ["要塞"] = "要赛",
        ["拾级迷宫"] = "十级迷宫",
    };

    public string CustomExecutable { get; set; } = string.Empty;
    public string CustomArguments { get; set; } = "{text}";

    /// <summary>快捷播报文本：设置页命令列表中“播报文本”的执行按钮读这一句。</summary>
    public string HotkeyText { get; set; } = "副本即将开始";
}
