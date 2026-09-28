using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Reflection;
using OmniToolbox.TreeHouseOnline;

// 本地测试入口，编译的是仓库里的 Modules/TtsSystem.cs 本体。
//
//   OmniTtsLocalTest                                  播报默认快捷播报文本
//   OmniTtsLocalTest say <文本> [--wav|--mp3] [--voice 短名] [--speed n] [--volume n] [--wait 秒]
//   OmniTtsLocalTest synth <文本> [wav|mp3]           只合成并保存，不播放
//   OmniTtsLocalTest diag                             检测本机播放链路（NAudio / MCI / WAV）
//   OmniTtsLocalTest bench [次数]                基准：连续合成，比较冷启动与复用连接的耗时
//   OmniTtsLocalTest purge                            清除音频缓存
//   OmniTtsLocalTest info                             打印运行环境信息
//
// 说明：say 会真正出声，先调好系统音量。想安静验证链路时先用 synth / diag。
// 本工程额外引用了 NAudio 且会主动加载它，用于模拟「宿主已提供 NAudio」的情形；
// 在线模块本身不依赖 NAudio，只在运行时反射探测。

// 强制加载 NAudio 程序集，让模块内的反射探测在本地测试中命中。
_ = typeof(NAudio.Wave.WaveOutEvent);

var cli = Environment.GetCommandLineArgs().Skip(1).ToArray();
var module = new TtsSystem();

if (cli.Length == 0)
{
    Speak(module, module.Config.HotkeyText, 15);
    return;
}

switch (cli[0].ToLowerInvariant())
{
    case "say":
    {
        var text = ArgValue(cli, 0) ?? module.Config.HotkeyText;
        var config = module.Config;

        if (Has(cli, "--mp3")) config.AudioFormat = 2;
        else if (Has(cli, "--wav")) config.AudioFormat = 1;

        var voice = Option(cli, "--voice");
        if (!string.IsNullOrWhiteSpace(voice)) config.Voice = voice;

        if (int.TryParse(Option(cli, "--speed"), out var speed)) config.Speed = speed;
        if (int.TryParse(Option(cli, "--volume"), out var volume)) config.Volume = volume;

        var wait = int.TryParse(Option(cli, "--wait"), out var w) ? w : 15;
        Speak(module, text, wait);
        return;
    }

    case "synth":
    {
        var text = cli.Length > 1 ? cli[1] : module.Config.HotkeyText;
        var format = cli.Length > 2 ? cli[2] : "mp3";

        Console.WriteLine($"合成中… 文本=「{text}」 格式={format}");
        var request = new TtsEdgeRequest
        {
            Text = text,
            Voice = module.Config.Voice,
            Speed = module.Config.Speed,
            Pitch = module.Config.Pitch,
            Volume = module.Config.Volume,
            OutputFormat = format,
        };

        try
        {
            var audio = TtsEdgeClient.SynthesizeAsync(request).GetAwaiter().GetResult();
            if (audio is not { Length: > 0 })
            {
                Console.WriteLine("结果：失败（返回空）。可能是网络不通、触发限流，或该 outputFormat 不被服务端接受。");
                return;
            }

            var path = TtsEdgeClient.SaveToCache(request, audio);
            Console.WriteLine($"结果：成功 {audio.Length} 字节 -> {path}");
            Console.WriteLine($"头 4 字节：{Convert.ToHexString(audio[..Math.Min(4, audio.Length)])}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"结果：异常 {ex.GetType().Name}: {ex.Message}");
        }

        return;
    }

    case "diag":
    {
        Console.WriteLine("=== 播放链路自检 ===");
        Console.WriteLine($"NAudio 可用        : {TtsEdgeClient.IsNaudioAvailable()}");
        Console.WriteLine($"typeof 程序集      : {typeof(NAudio.Wave.WaveOutEvent).Assembly.GetName().Name}");
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name)
            .Where(n => n is not null && n.Contains("NAudio", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"已加载含 NAudio    : {string.Join(", ", loaded)}");

        var sample = FindSampleFile();
        if (sample is null)
        {
            Console.WriteLine("MCI 打开 MP3       : 跳过（没有样本文件，先跑一次 synth）");
        }
        else
        {
            var rc = NativeMci.Send($"open \"{sample}\" type mpegvideo alias diag1");
            Console.WriteLine($"MCI 打开 MP3       : rc={rc} {NativeMci.ErrorText(rc)}");
            if (rc == 0)
            {
                NativeMci.Send("close diag1");
            }
        }

        var wav = Path.Combine(Path.GetTempPath(), "OmniTtsDiag.wav");
        WriteSilentWav(wav, 0.1);
        Console.WriteLine($"PlaySound 播放 WAV : {TtsWinmmSound.PlaySync(wav)}");
        File.Delete(wav);

        Console.WriteLine();
        Console.WriteLine("若 NAudio 为 True，模块走 NAudio 播放 MP3；");
        Console.WriteLine("若为 False 且 MCI rc 不为 0，MP3 播不出，模块会自动回退到系统 SAPI 语音。");
        return;
    }

    case "bench":
    {
        // 连续合成多次不同文本：第 1 次含握手（冷），之后复用连接（热）。
        var count = cli.Length > 1 && int.TryParse(cli[1], out var c) ? c : 5;
        Console.WriteLine($"=== 合成耗时基准（{count} 段不同文本，MP3）===");
        long cold = 0;

        // --warm：模拟模块启用时的后台预热，验证首次播报也能跳过握手
        if (cli.Any(a => string.Equals(a, "--warm", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("（预热连接中…）");
            TtsEdgeClient.WarmUp();
            Thread.Sleep(2500);
        }
        for (var i = 0; i < count; i++)
        {
            var request = new TtsEdgeRequest
            {
                Text = $"计时测试第 {1001 + i} 句",
                Voice = module.Config.Voice,
                OutputFormat = "mp3",
            };

            var sw = Stopwatch.StartNew();
            var audio = TtsEdgeClient.SynthesizeAsync(request).GetAwaiter().GetResult();
            sw.Stop();
            if (i == 0) cold = sw.ElapsedMilliseconds;
            Console.WriteLine($"  #{i + 1} {sw.ElapsedMilliseconds,6} ms  {audio?.Length ?? 0} 字节");
        }

        Console.WriteLine($"冷启动 = {cold} ms，其余为复用连接耗时");
        TtsEdgeClient.CloseConnection();
        return;
    }

    case "purge":
        Console.WriteLine($"已清除 {module.PurgeCache()} 个缓存文件");
        return;

    case "info":
        Console.WriteLine($"NAudio 可用      : {TtsEdgeClient.IsNaudioAvailable()}");
        Console.WriteLine($"缓存目录         : {TtsEdgeClient.CacheDirectory}");
        Console.WriteLine($"Sec-MS-GEC       : {TtsEdgeClient.GenerateSecMsGec()}");
        Console.WriteLine($"默认音色         : {module.Config.Voice}");
        Console.WriteLine($"引擎             : {module.Config.Engine}");
        Console.WriteLine($"音频格式         : {module.Config.AudioFormat}（0 自动 / 1 WAV / 2 MP3）");
        Console.WriteLine($"IPC 状态         : {TtsIpc.Status}");

        if (Directory.Exists(TtsEdgeClient.CacheDirectory))
        {
            Console.WriteLine($"缓存文件数       : {Directory.GetFiles(TtsEdgeClient.CacheDirectory).Length}");
        }

        return;

    case "ipctest":
        // 用鸭子类型的假插件接口验证 IPC 反射链路：泛型重载匹配、RegisterFunc/RegisterAction 传参、委托签名。
        // 不触发真实播报，只检查端点能否被正确注册并调用。
        RunIpcTest();
        return;

    default:
        Console.WriteLine("未知命令。可用：say / synth / diag / purge / info / ipctest");
        return;
}

static void RunIpcTest()
{
    var ipcType = typeof(TtsIpc);
    var getProvider = ipcType.GetMethod("GetProvider", BindingFlags.NonPublic | BindingFlags.Static);
    var registerFunc = ipcType.GetMethod("RegisterFunc", BindingFlags.NonPublic | BindingFlags.Static);
    var registerAction = ipcType.GetMethod("RegisterAction", BindingFlags.NonPublic | BindingFlags.Static);

    if (getProvider is null || registerFunc is null || registerAction is null)
    {
        Console.WriteLine("失败：TtsIpc 的反射入口缺失");
        return;
    }

    var fake = new FakePluginInterface();
    var ok = true;

    // Func<int>：OmniTts.Version
    registerFunc.Invoke(null, [fake, new[] { typeof(int) }, "OmniTts.Version", (Func<int>)(() => TtsIpc.Version)]);
    var versionProvider = fake.LastProvider as FakeIpcProvider<int>;
    ok &= Report("Version 端点", versionProvider?.Func is not null && versionProvider!.Func!() == TtsIpc.Version);

    // Func<string, bool>：OmniTts.Say（不调用，避免真实播报）
    registerFunc.Invoke(null,
        [fake, new[] { typeof(string), typeof(bool) }, "OmniTts.Say", (Func<string, bool>)(_ => true)]);
    ok &= Report("Say 端点", (fake.LastProvider as FakeIpcProvider<string, bool>)?.Func is not null);

    // Func<string, string, int, int, bool>：OmniTts.SayEx（不调用）
    registerFunc.Invoke(null,
    [
        fake,
        new[] { typeof(string), typeof(string), typeof(int), typeof(int), typeof(bool) },
        "OmniTts.SayEx",
        (Func<string, string, int, int, bool>)((_, _, _, _) => true),
    ]);
    ok &= Report("SayEx 端点",
        (fake.LastProvider as FakeIpcProvider<string, string, int, int, bool>)?.Func is not null);

    // Action：OmniTts.Stop
    var stopped = false;
    registerAction.Invoke(null, [fake, "OmniTts.Stop", (Action)(() => stopped = true)]);
    var stopProvider = fake.LastProvider as FakeIpcProvider<object>;
    stopProvider?.Action?.Invoke();
    ok &= Report("Stop 端点（Action 可被回调）", stopped);

    ok &= Report("端点名依次为 Version/Say/SayEx/Stop",
        string.Join(",", fake.Names) == "OmniTts.Version,OmniTts.Say,OmniTts.SayEx,OmniTts.Stop");

    Console.WriteLine(ok ? "IPC 反射链路：全部通过" : "IPC 反射链路：存在失败项");
}

static bool Report(string name, bool ok)
{
    Console.WriteLine($"  {name,-34} {(ok ? "通过" : "失败")}");
    return ok;
}

static void Speak(TtsSystem module, string text, int waitSeconds)
{
    Console.WriteLine($"提交播报：{text}");
    var accepted = module.Speak(text);
    Console.WriteLine(accepted
        ? $"已进入队列，等待 {waitSeconds} 秒…（Ctrl+C 可提前结束）"
        : "未进入队列：可能被去重策略过滤，或模块已停用。");

    if (accepted)
    {
        Thread.Sleep(Math.Max(1, waitSeconds) * 1000);
    }
}

static bool Has(string[] args, string flag) =>
    args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

static string? Option(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}

static string? ArgValue(string[] args, int index) =>
    index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
        ? args[index + 1]
        : null;

static string? FindSampleFile()
{
    var dir = TtsEdgeClient.CacheDirectory;
    return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.mp3").FirstOrDefault() : null;
}

static void WriteSilentWav(string path, double seconds)
{
    const int rate = 22050;
    var samples = (int)(rate * seconds);
    using var fs = new FileStream(path, FileMode.Create);
    using var bw = new BinaryWriter(fs);
    bw.Write("RIFF"u8.ToArray());
    bw.Write(36 + samples * 2);
    bw.Write("WAVE"u8.ToArray());
    bw.Write("fmt "u8.ToArray());
    bw.Write(16);
    bw.Write((short)1);
    bw.Write((short)1);
    bw.Write(rate);
    bw.Write(rate * 2);
    bw.Write((short)2);
    bw.Write((short)16);
    bw.Write("data"u8.ToArray());
    bw.Write(samples * 2);
    bw.Write(new byte[samples * 2]);
}

internal static class NativeMci
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? ret, int len, IntPtr cb);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool mciGetErrorString(int code, StringBuilder buffer, int length);

    public static int Send(string command) => mciSendString(command, null, 0, IntPtr.Zero);

    public static string ErrorText(int code)
    {
        var sb = new StringBuilder(256);
        mciGetErrorString(code, sb, sb.Capacity);
        return sb.ToString();
    }
}

// ---- IPC 反射链路测试用的鸭子类型：只模拟 Dalamud 的 IPC 接口形状，不需要 Dalamud 运行时 ----

internal sealed class FakePluginInterface
{
    public List<string> Names { get; } = [];

    public object? LastProvider { get; private set; }

    public FakeIpcProvider<TRet> GetIpcProvider<TRet>(string name) =>
        Track(name, new FakeIpcProvider<TRet>());

    public FakeIpcProvider<T1, TRet> GetIpcProvider<T1, TRet>(string name) =>
        Track(name, new FakeIpcProvider<T1, TRet>());

    public FakeIpcProvider<T1, T2, T3, T4, TRet> GetIpcProvider<T1, T2, T3, T4, TRet>(string name) =>
        Track(name, new FakeIpcProvider<T1, T2, T3, T4, TRet>());

    private T Track<T>(string name, T provider)
    {
        Names.Add(name);
        LastProvider = provider;
        return provider;
    }
}

internal sealed class FakeIpcProvider<TRet>
{
    public Func<TRet>? Func { get; private set; }

    public Action? Action { get; private set; }

    public int UnregisterCalls { get; private set; }

    public void RegisterFunc(Func<TRet> func) => Func = func;

    public void RegisterAction(Action action) => Action = action;

    public void UnregisterFunc() => UnregisterCalls++;

    public void UnregisterAction() => UnregisterCalls++;
}

internal sealed class FakeIpcProvider<T1, TRet>
{
    public Func<T1, TRet>? Func { get; private set; }

    public int UnregisterCalls { get; private set; }

    public void RegisterFunc(Func<T1, TRet> func) => Func = func;

    public void RegisterAction(Action action) => throw new NotSupportedException();

    public void UnregisterFunc() => UnregisterCalls++;

    public void UnregisterAction() => UnregisterCalls++;
}

internal sealed class FakeIpcProvider<T1, T2, T3, T4, TRet>
{
    public Func<T1, T2, T3, T4, TRet>? Func { get; private set; }

    public int UnregisterCalls { get; private set; }

    public void RegisterFunc(Func<T1, T2, T3, T4, TRet> func) => Func = func;

    public void RegisterAction(Action action) => throw new NotSupportedException();

    public void UnregisterFunc() => UnregisterCalls++;

    public void UnregisterAction() => UnregisterCalls++;
}
