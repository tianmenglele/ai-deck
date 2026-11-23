using System.Collections.Concurrent;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using AICollabHub.Models;

namespace AICollabHub.Services;

/// <summary>协同提示音：总开关 + 轻量化 + 内嵌 DOLLARS 风格电钢短乐句（约 0.95–2.20 秒，无循环音轨）；可幂等静音 Cursor 无障碍进度音。</summary>
public static class SoundFxService
{
    public enum SoundKind
    {
        DispatchFlight,
        StatusChange,
        WorkStarted,
        TaskCompleted,
        VerifyPassed
    }

    private static readonly ConcurrentDictionary<SoundKind, byte[]> WavCache = new();
    private static readonly object PlayLock = new();
    private static readonly object CursorSilenceLock = new();
    private static DateTime _lastPlayUtc = DateTime.MinValue;
    private static SoundKind _lastKind;
    private static SoundPlayer? _activePlayer;
    private static MemoryStream? _activeStream;
    private static volatile bool _shuttingDown;
    /// <summary>进程级硬闸：false 时即使传入的 cfg 仍为 true 也禁止起播。</summary>
    private static volatile bool _effectsArmed = true;
    /// <summary>--cli 无头进程强制静默，不响应任何播音。</summary>
    private static volatile bool _cliSilent;
    private static DateTime _lastCursorSilenceUtc = DateTime.MinValue;
    private static volatile bool _cursorSilenceDone;

    /// <summary>--cli 静默模式是否已激活（供 SaveUserConfig 保留 UI 偏好）。</summary>
    public static bool IsCliSilent => _cliSilent;

    /// <summary>--cli 入口调用：本进程永久静默，不播任何音效。</summary>
    public static void EnterCliSilentMode()
    {
        _cliSilent = true;
        _effectsArmed = false;
        StopAll();
        SyncProcessAudioMute(true);
    }

    /// <summary>
    /// 按最新用户配置同步硬闸门：关闭时立即 StopAll + 进程静音，并幂等静音 Cursor 无障碍进度音；开启时解除静音。
    /// </summary>
    public static void ApplySoundPreference(UserHubConfig? cfg)
    {
        if (_cliSilent || _shuttingDown)
        {
            _effectsArmed = false;
            StopAll();
            SyncProcessAudioMute(true);
            return;
        }

        bool enabled = cfg?.EnableSoundEffects == true;
        _effectsArmed = enabled;
        if (!enabled)
        {
            StopAll();
            // winmm 硬切断（StopAll 内已调用）；再同步混音器静音
            try { NativePlaySound(null, IntPtr.Zero, 0); } catch { }
            SyncProcessAudioMute(true);
            TrySilenceCursorAccessibilityAudioDebounced();
            return;
        }

        SyncProcessAudioMute(false);
    }

    /// <summary>标记应用正在退出：此后禁止再播，并立刻切断当前音效。</summary>
    public static void MarkShuttingDown(bool shuttingDown = true)
    {
        _shuttingDown = shuttingDown;
        if (shuttingDown)
        {
            _effectsArmed = false;
            StopAll();
            SyncProcessAudioMute(true);
        }
        else if (!_cliSilent)
        {
            // 自检复位：允许后续按 cfg 重新武装
            _effectsArmed = true;
        }
    }

    /// <summary>
    /// 通过 Windows Core Audio（WASAPI）将本进程在系统混音器中物理静音/解除静音。
    /// 隔离测试环境零 COM 副作用；全过程吞异常，绝不抛出。
    /// </summary>
    public static void SyncProcessAudioMute(bool muted)
    {
        if (AppPaths.IsIsolatedTest) return;
        try
        {
            MuteOwnProcessSessions(muted);
        }
        catch
        {
            // 安全兜底：混音器 COM 不可用时不影响主流程
        }
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    private static extern bool NativePlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    private static void MuteOwnProcessSessions(bool muted)
    {
        object? enumeratorObj = null;
        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            if (enumeratorType == null) return;
            enumeratorObj = Activator.CreateInstance(enumeratorType);
            if (enumeratorObj is not IMMDeviceEnumerator enumerator) return;

            int myPid = Environment.ProcessId;
            // 优先默认播放终端（eRender=0, eMultimedia=1），再兜底枚举全部活跃渲染终端
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out var defaultDev) == 0 && defaultDev != null)
                {
                    try { MuteSessionsOnDevice(defaultDev, myPid, muted); }
                    finally { Marshal.ReleaseComObject(defaultDev); }
                }
            }
            catch { }

            try
            {
                // eRender=0, DEVICE_STATE_ACTIVE=0x1
                if (enumerator.EnumAudioEndpoints(0, 0x1, out var collection) == 0 && collection != null)
                {
                    try
                    {
                        collection.GetCount(out var count);
                        for (int i = 0; i < count; i++)
                        {
                            if (collection.Item(i, out var dev) != 0 || dev == null) continue;
                            try { MuteSessionsOnDevice(dev, myPid, muted); }
                            finally { Marshal.ReleaseComObject(dev); }
                        }
                    }
                    finally { Marshal.ReleaseComObject(collection); }
                }
            }
            catch { }
        }
        finally
        {
            if (enumeratorObj != null)
            {
                try { Marshal.ReleaseComObject(enumeratorObj); } catch { }
            }
        }
    }

    private static void MuteSessionsOnDevice(IMMDevice device, int processId, bool muted)
    {
        // IAudioSessionManager2
        Guid iidSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        if (device.Activate(ref iidSessionManager2, 0, IntPtr.Zero, out var mgrObj) != 0 || mgrObj == null)
            return;
        try
        {
            var mgr = (IAudioSessionManager2)mgrObj;
            if (mgr.GetSessionEnumerator(out var sessionEnum) != 0 || sessionEnum == null) return;
            try
            {
                sessionEnum.GetCount(out var count);
                for (int i = 0; i < count; i++)
                {
                    if (sessionEnum.GetSession(i, out var sessionObj) != 0 || sessionObj == null) continue;
                    try
                    {
                        var ctl2 = (IAudioSessionControl2)sessionObj;
                        if (ctl2.GetProcessId(out var pid) != 0 || (int)pid != processId) continue;
                        var vol = (ISimpleAudioVolume)sessionObj;
                        Guid ctx = Guid.Empty;
                        vol.SetMute(muted, ref ctx);
                    }
                    catch { }
                    finally
                    {
                        try { Marshal.ReleaseComObject(sessionObj); } catch { }
                    }
                }
            }
            finally
            {
                try { Marshal.ReleaseComObject(sessionEnum); } catch { }
            }
        }
        finally
        {
            try { Marshal.ReleaseComObject(mgrObj); } catch { }
        }
    }

    #region WASAPI COM（扁平 vtable，避免接口继承导致槽位错位）

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr audioSessionGuid, uint streamFlags, out IntPtr sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr audioSessionGuid, uint streamFlags, out IntPtr audioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        [PreserveSig] int RegisterSessionNotification(IntPtr sessionNotification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr sessionNotification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA0124EA707")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr newNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr newNotifications);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }

    #endregion

    /// <summary>立刻停止并释放一次性音效句柄（含 winmm 硬切断）。</summary>
    public static void StopAll()
    {
        lock (PlayLock)
        {
            DisposeActivePlayerUnlocked();
            try { NativePlaySound(null, IntPtr.Zero, 0); } catch { }
        }
    }

    /// <summary>是否应播放该种类音效（供自检与运行时共用）。</summary>
    public static bool ShouldPlay(UserHubConfig? cfg, SoundKind kind)
    {
        if (_shuttingDown || _cliSilent || !_effectsArmed) return false;
        if (cfg == null || !cfg.EnableSoundEffects) return false;
        if (!cfg.SoundLightweightMode) return true;
        return kind is SoundKind.TaskCompleted or SoundKind.VerifyPassed;
    }

    public static void Play(UserHubConfig? cfg, SoundKind kind)
    {
        if (_shuttingDown || _cliSilent || !_effectsArmed) return;
        if (AppPaths.IsIsolatedTest) return;
        if (!ShouldPlay(cfg, kind)) return;

        lock (PlayLock)
        {
            if (_shuttingDown || _cliSilent || !_effectsArmed) return;
            if (!ShouldPlay(cfg, kind)) return;

            var now = DateTime.UtcNow;
            // 防抖：避免短时间内高频状态切换连续叠音吵耳
            double minGapMs = kind is SoundKind.TaskCompleted or SoundKind.VerifyPassed ? 100 : 220;
            if ((now - _lastPlayUtc).TotalMilliseconds < minGapMs && _lastKind == kind)
                return;
            _lastPlayUtc = now;
            _lastKind = kind;

            try
            {
                DisposeActivePlayerUnlocked();

                var wavBytes = LoadEmbeddedWav(kind);
                var ms = new MemoryStream(wavBytes, writable: false);
                var player = new SoundPlayer(ms);
                _activeStream = ms;
                _activePlayer = player;
                player.Play();
            }
            catch
            {
                DisposeActivePlayerUnlocked();
            }
        }
    }

    private static void DisposeActivePlayerUnlocked()
    {
        try { _activePlayer?.Stop(); } catch { }
        try { _activePlayer?.Dispose(); } catch { }
        _activePlayer = null;
        try { _activeStream?.Dispose(); } catch { }
        _activeStream = null;
    }

    private static void TrySilenceCursorAccessibilityAudioDebounced()
    {
        if (AppPaths.IsIsolatedTest || _cliSilent) return;
        lock (CursorSilenceLock)
        {
            var now = DateTime.UtcNow;
            if (_cursorSilenceDone && (now - _lastCursorSilenceUtc).TotalMilliseconds < 1500)
                return;
            _lastCursorSilenceUtc = now;
            try
            {
                var (ok, _) = SilenceCursorAccessibilityAudio();
                if (ok) _cursorSilenceDone = true;
            }
            catch
            {
                // 静音 Cursor 设置失败不影响协同中心主流程
            }
        }
    }

    /// <summary>
    /// 幂等写入 Cursor User/settings.json：关闭无障碍进度循环音与对话铃声。
    /// 支持 JSONC（注释/尾逗号）；解析失败拒写；修改前备份 .aicollab.bak。
    /// 隔离测试且未传 override 时直接跳过，绝不触碰真实 %APPDATA%\Cursor。
    /// 严禁写入 editor.accessibilitySupport（会破坏 Chromium UIA 树）。
    /// </summary>
    public static (bool Ok, string Message) SilenceCursorAccessibilityAudio(string? overrideSettingsPath = null)
    {
        if (AppPaths.IsIsolatedTest && string.IsNullOrWhiteSpace(overrideSettingsPath))
            return (true, "隔离测试已跳过真实 Cursor settings.json");

        string settingsPath;
        if (!string.IsNullOrWhiteSpace(overrideSettingsPath))
        {
            settingsPath = Path.GetFullPath(overrideSettingsPath);
        }
        else
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            settingsPath = Path.Combine(appData, "Cursor", "User", "settings.json");
        }

        try
        {
            string? dir = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string raw = File.Exists(settingsPath) ? File.ReadAllText(settingsPath, Encoding.UTF8) : "{}";
            if (string.IsNullOrWhiteSpace(raw)) raw = "{}";

            JsonObject root;
            try
            {
                var docOpts = new System.Text.Json.JsonDocumentOptions
                {
                    CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };
                if (JsonNode.Parse(raw, documentOptions: docOpts) is not JsonObject parsed)
                    return (false, "Cursor settings.json 顶层不是对象，已放弃写入（未改动文件）");
                root = parsed;
            }
            catch (Exception parseEx)
            {
                return (false, $"Cursor settings.json 解析失败，已放弃写入（未改动文件）：{parseEx.Message}");
            }

            bool changed = false;
            changed |= EnsureBool(root, "cursor.composer.shouldChimeAfterChatFinishes", false);
            changed |= EnsureNumber(root, "accessibility.signalOptions.volume", 0);
            changed |= EnsureSignalSoundOff(root, "accessibility.signals.progress");
            changed |= EnsureSignalSoundOff(root, "accessibility.signals.chatResponseReceived");
            changed |= EnsureSignalSoundOff(root, "accessibility.signals.chatRequestSent");
            changed |= EnsureSignalSoundOff(root, "accessibility.signals.chatEditModifiedFile");
            changed |= EnsureSignalSoundOff(root, "accessibility.signals.taskCompleted");
            changed |= EnsureSignalSoundOff(root, "accessibility.signals.taskFailed");
            changed |= EnsureSignalSoundOff(root, "accessibility.signals.terminalCommandFailed");
            changed |= EnsureSignalSoundOff(root, "accessibility.signals.terminalBell");

            // 严禁写入/保留强制关闭 UIA 的键
            if (root.ContainsKey("editor.accessibilitySupport"))
            {
                // 不主动删除用户已有值；也绝不新增为 off
            }

            if (!changed)
                return (true, "Cursor 无障碍提示音配置已是目标态，无需刷盘");

            if (File.Exists(settingsPath))
                File.Copy(settingsPath, settingsPath + ".aicollab.bak", overwrite: true);

            var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(settingsPath, root.ToJsonString(opts), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return (true, "已写入 Cursor 用户设置：关闭无障碍进度循环音与对话提示音");
        }
        catch (Exception ex)
        {
            return (false, $"写入 Cursor 用户设置失败：{ex.Message}");
        }
    }

    private static bool EnsureBool(JsonObject root, string key, bool value)
    {
        if (root[key] is JsonValue jv && jv.TryGetValue<bool>(out var cur) && cur == value)
            return false;
        root[key] = value;
        return true;
    }

    private static bool EnsureNumber(JsonObject root, string key, double value)
    {
        if (root[key] is JsonValue jv)
        {
            if (jv.TryGetValue<double>(out var d) && Math.Abs(d - value) < 0.0001) return false;
            if (jv.TryGetValue<int>(out var i) && Math.Abs(i - value) < 0.0001) return false;
            if (jv.TryGetValue<long>(out var l) && Math.Abs(l - value) < 0.0001) return false;
        }
        root[key] = value;
        return true;
    }

    private static bool EnsureSignalSoundOff(JsonObject root, string key)
    {
        if (root[key] is JsonObject existing)
        {
            if (existing["sound"] is JsonValue sv &&
                sv.TryGetValue<string>(out var sound) &&
                string.Equals(sound, "off", StringComparison.OrdinalIgnoreCase))
                return false;
            existing["sound"] = "off";
            return true;
        }

        root[key] = new JsonObject { ["sound"] = "off" };
        return true;
    }

    /// <summary>程序集内嵌 RIFF/WAVE 资源名（44100 Hz / 16-bit / 单声道电钢乐句，单文件发布后仍可离线读取）。</summary>
    public static string EmbeddedWavResourceName(SoundKind kind)
    {
        string file = kind switch
        {
            SoundKind.DispatchFlight => "dispatch_flight.wav",
            SoundKind.StatusChange => "status_change.wav",
            SoundKind.WorkStarted => "work_started.wav",
            SoundKind.TaskCompleted => "task_completed.wav",
            SoundKind.VerifyPassed => "verify_passed.wav",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未登记的提示音种类")
        };
        return "AICollabHub.Assets.Sounds." + file;
    }

    /// <summary>读取并缓存该种类的内嵌 PCM 波形。缺失时抛出，不回退公式合成。</summary>
    public static byte[] LoadEmbeddedWav(SoundKind kind) =>
        WavCache.GetOrAdd(kind, ReadEmbeddedWav);

    private static byte[] ReadEmbeddedWav(SoundKind kind)
    {
        string name = EmbeddedWavResourceName(kind);
        using Stream? stream = typeof(SoundFxService).Assembly.GetManifestResourceStream(name);
        if (stream == null)
            throw new InvalidOperationException("缺少内嵌提示音: " + name);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        if (ms.Length < 44)
            throw new InvalidOperationException("内嵌提示音过短: " + name);
        return ms.ToArray();
    }
}
