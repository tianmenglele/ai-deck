using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AICollabHub.Models;
using AICollabHub.Services;
using AICollabHub.UI;

namespace AICollabHub;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (Console.IsOutputRedirected)
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
        }
        catch
        {
            // WinExe 无控制台句柄时安全跳过
        }

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--data-dir")
            {
                AppPaths.SetDataDirOverride(args[i + 1]);
            }
        }

        if (args.Contains("--cli"))
        {
            return RunCli(args);
        }

        var captureLiveIdx = Array.IndexOf(args, "--capture-live");
        if (captureLiveIdx >= 0)
        {
            var outDir = captureLiveIdx + 1 < args.Length
                ? Path.GetFullPath(args[captureLiveIdx + 1])
                : Path.Combine(AppPaths.UserDataDir, "live_shots");
            return RunCaptureLive(outDir);
        }

        var selfTestIdx = Array.IndexOf(args, "--self-test");
        if (selfTestIdx >= 0)
        {
            var outDir = selfTestIdx + 1 < args.Length
                ? Path.GetFullPath(args[selfTestIdx + 1])
                : Path.Combine(AppPaths.UserDataDir, "self_test");
            try
            {
                int code = RunSelfTest(outDir);
                Console.WriteLine($"SELFTEST_EXIT={code}");
                return code;
            }
            catch (Exception ex)
            {
                Directory.CreateDirectory(outDir);
                File.WriteAllText(Path.Combine(outDir, "self_test_error.txt"), ex.ToString(), new UTF8Encoding(false));
                Console.WriteLine("SELFTEST_EXIT=2");
                return 2;
            }
        }

        // 单实例保护：使用命名互斥锁仅保护 GUI 实例（不与 --cli 后台派单进程冲突）
        using var guiMutex = new Mutex(true, @"Local\AICollabHub_GuiSingleInstance_v1", out bool isFirstGui);
        if (!isFirstGui)
        {
            int curPid = Environment.ProcessId;
            var allProcs = System.Diagnostics.Process.GetProcessesByName("AI协同中心");
            try
            {
                var others = allProcs.Where(p => p.Id != curPid).ToList();

                bool hasVisibleWindow = others.Any(p =>
                {
                    try { p.Refresh(); return p.MainWindowHandle != IntPtr.Zero; }
                    catch { return false; }
                });

                if (hasVisibleWindow)
                {
                    var sigFile = Path.Combine(AppPaths.UserDataDir, "wakeup_ui.signal");
                    try
                    {
                        File.WriteAllText(sigFile, args.Contains("--floating") ? "floating" : "main");
                        for (int i = 0; i < 22; i++)
                        {
                            Thread.Sleep(100);
                            if (!File.Exists(sigFile))
                            {
                                return 0;
                            }
                        }
                    }
                    catch { }
                }

                // 已有实例无主窗（幽灵状态）或唤醒超时未响应：清理无主窗/无响应旧进程后重新拉起界面
                try
                {
                    foreach (var p in others)
                    {
                        try
                        {
                            p.Refresh();
                            if (p.MainWindowHandle == IntPtr.Zero || !p.Responding)
                            {
                                p.Kill();
                            }
                        }
                        catch { }
                    }
                    Thread.Sleep(250);
                    try { guiMutex.WaitOne(300); } catch (AbandonedMutexException) { }
                }
                catch { }
            }
            finally
            {
                foreach (var p in allProcs) { try { p.Dispose(); } catch { } }
            }
        }

        var app = CreateWpfApp();
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
        app.DispatcherUnhandledException += (_, exArgs) =>
        {
            try
            {
                File.WriteAllText(Path.Combine(AppPaths.UserDataDir, "last_crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UI异常: {exArgs.Exception}", new UTF8Encoding(false));
            }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, exArgs) =>
        {
            try
            {
                File.WriteAllText(Path.Combine(AppPaths.UserDataDir, "last_crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 致命异常: {exArgs.ExceptionObject}", new UTF8Encoding(false));
            }
            catch { }
        };

        var reg = new RegistryService();
        reg.LoadAll();
        bool floatingOnlyMode = args.Contains("--floating");
        if (floatingOnlyMode)
        {
            reg.UserConfig.FloatingWindowEnabled = true;
            reg.UserConfig.FloatingWindowCollapsed = false;
        }
        var win = new MainWindow(reg);
        if (floatingOnlyMode)
        {
            win.WindowState = WindowState.Minimized;
        }
        app.Run(win);
        return 0;
    }

    private static Application CreateWpfApp()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var theme = (ResourceDictionary)Application.LoadComponent(
            new Uri("UI/ThemeResources.xaml", UriKind.Relative));
        app.Resources.MergedDictionaries.Add(theme);
        return app;
    }

    private static int RunCli(string[] args)
    {
        // --cli 无头派单/同步进程强制静默：不播任何音效，也不读取启动时旧配置去发声
        SoundFxService.EnterCliSilentMode();

        var cmdIdx = Array.IndexOf(args, "--cli") + 1;
        var cmd = cmdIdx < args.Length ? args[cmdIdx].ToLowerInvariant() : "status";

        var reg = new RegistryService();
        reg.LoadAll();
        var discovery = new DiscoveryService();
        var sync = new SyncService();
        var bus = new CollabBusService();
        var autoDispatch = new AutoDispatchService();
        var share = new ShareExportService();

        if (reg.UserConfig.Agents.Count == 0) discovery.RunDiscovery(reg);

        // 支持 --project-dir "<任意工程目录>"：在 Antigravity 的任何一个项目对话框里说话都能自动纳管并切换
        var projDirIdx = Array.IndexOf(args, "--project-dir");
        if (projDirIdx >= 0 && projDirIdx + 1 < args.Length && !string.IsNullOrWhiteSpace(args[projDirIdx + 1]))
        {
            var switched = reg.EnsureOrSwitchProjectByDir(args[projDirIdx + 1]);
            sync.InitializeSharedFoundation(reg, switched);
        }

        var proj = reg.GetActiveProject();
        if (proj == null)
        {
            Console.WriteLine("ERROR: 尚未配置任何项目档案。");
            return 1;
        }

        switch (cmd)
        {
            case "scan":
                int n = discovery.RunDiscovery(reg);
                Console.WriteLine($"SCAN_OK count={n}");
                foreach (var a in reg.UserConfig.Agents)
                    Console.WriteLine($"- [{(a.Enabled ? "ON" : "OFF")}] {a.Name} | detected={a.Detected} | {a.Evidence}");
                return 0;

            case "sync":
                Console.WriteLine(sync.ExecuteSyncAll(reg, proj, bus));
                return 0;

            case "snapshot":
                Console.WriteLine(bus.CreateBaselineSnapshot(reg, proj));
                return 0;

            case "diff":
                Console.WriteLine(bus.GenerateLatestDiff(reg, proj));
                Console.WriteLine($"DIFF_FILE={bus.GetLatestDiffPath(proj)}");
                return 0;

            case "rollback":
                Console.WriteLine(bus.RollbackToBaseline(reg, proj));
                return 0;

            case "pack":
                var packPath = bus.BuildSanitizedRemotePack(reg, proj, true, true, true);
                Console.WriteLine($"PACK_OK path={packPath}");
                return 0;

            case "dispatch":
                var targetAg = ReadSingleArg(args, "--agent", "auto");
                var taskInstruction = ReadMultiWordArg(args, "--task");
                var sessMode = ReadSingleArg(args, "--session", "auto");
                var res = autoDispatch.ExecuteHeadlessDispatch(reg, proj, bus, targetAg, taskInstruction, reg.UserConfig.SingleAttemptTimeoutSeconds, sessMode);
                PrintDispatchResultToConsole(res);
                return res.Success ? 0 : 1;

            case "resume":
                var resumeRes = autoDispatch.ResumeInterruptedTaskChain(reg, proj, bus, reg.UserConfig.SingleAttemptTimeoutSeconds);
                PrintDispatchResultToConsole(resumeRes);
                return resumeRes.Success ? 0 : 1;

            case "net-check":
                var (netOk, netMsg) = NetworkResilienceService.ProbeLocalNetworkHealth(1000);
                var pendingCp = NetworkResilienceService.LoadInterruptedCheckpoint(proj);
                Console.WriteLine($"NET_ONLINE={netOk}");
                Console.WriteLine($"NET_DETAIL={netMsg}");
                Console.WriteLine($"HAS_INTERRUPTED_CHECKPOINT={pendingCp != null}");
                if (pendingCp != null)
                {
                    Console.WriteLine($"CHECKPOINT_INFO=Round {pendingCp.CurrentRound}/{pendingCp.MaxRounds} | Agent={pendingCp.TargetAgentName} | Reason={pendingCp.LastFailureLabel}");
                }
                return netOk ? 0 : 1;

            case "sessions":
                var sessFilterIdx = Array.IndexOf(args, "--agent");
                var sessFilter = (sessFilterIdx >= 0 && sessFilterIdx + 1 < args.Length) ? args[sessFilterIdx + 1] : null;
                var sessionSvc = new SessionDialogService();
                var foundSessions = sessionSvc.DiscoverProjectSessions(proj, sessFilter);
                Console.WriteLine($"SESSIONS_COUNT={foundSessions.Count} | PROJECT={proj.Name} | ROOT={proj.PrimaryRoot}");
                foreach (var s in foundSessions)
                {
                    Console.WriteLine($"- [{s.AgentName}] ID={s.SessionId} | UPDATED={s.UpdatedAt} | TURNS={s.TurnCount} | TITLE={s.Title}");
                    if (!string.IsNullOrWhiteSpace(s.LastAssistantMessage))
                        Console.WriteLine($"  LAST_REPLY: {s.LastAssistantMessage}");
                }
                return 0;

            case "read-session":
                var rAgIdx = Array.IndexOf(args, "--agent");
                var rAg = (rAgIdx >= 0 && rAgIdx + 1 < args.Length) ? args[rAgIdx + 1] : "auto";
                var rSidIdx = Array.IndexOf(args, "--session");
                var rSid = (rSidIdx >= 0 && rSidIdx + 1 < args.Length) ? args[rSidIdx + 1] : "auto";
                var rSvc = new SessionDialogService();
                var targetSess = rSvc.FindBestMatchingSession(proj, rAg, rSid)
                                 ?? rSvc.DiscoverProjectSessions(proj, null).FirstOrDefault();
                if (targetSess == null)
                {
                    Console.WriteLine("NO_SESSION_FOUND: 当前项目在其他 AI 工具下尚无历史对话框，调用 dispatch 时将自动在该项目目录下新建对话框。");
                    return 0;
                }
                Console.WriteLine(rSvc.ReadSessionTranscriptMarkdown(targetSess, 12));
                return 0;

            case "archive":
                var noteIdx = Array.IndexOf(args, "--note");
                var note = (noteIdx >= 0 && noteIdx + 1 < args.Length) ? args[noteIdx + 1] : null;
                Console.WriteLine(bus.ArchiveCurrentTask(proj, note));
                return 0;

            case "export-share":
                var outIdx = Array.IndexOf(args, "--out");
                var outZip = (outIdx >= 0 && outIdx + 1 < args.Length)
                    ? Path.GetFullPath(args[outIdx + 1])
                    : Path.Combine(AppPaths.UserDataDir, $"AI协同中心_v{AppPaths.AppVersion}_Win64绿色分享版.zip");
                Console.WriteLine($"SHARE_ZIP={share.ExportCleanPortableZip(outZip)}");
                return 0;

            default:
                Console.WriteLine($"PROJECT={proj.Name} | ROOT={proj.PrimaryRoot}");
                foreach (var item in sync.InspectSyncHealth(reg, proj))
                    Console.WriteLine($"- {item.AgentName}: {item.StatusText} ({item.Detail})");
                return 0;
        }
    }

    private static void PrintDispatchResultToConsole(HeadlessDispatchOutcome res)
    {
        Console.WriteLine($"DISPATCH_OK={res.Success}");
        Console.WriteLine($"MASTER_PLANNER={res.MasterPlannerName}");
        Console.WriteLine($"AGENT={res.AgentName}");
        Console.WriteLine($"SELECTED_MODEL={res.SelectedModel}");
        Console.WriteLine($"SELECTED_TIER={res.SelectedTierLabel}");
        Console.WriteLine($"ROUNDS_EXECUTED={res.RoundsExecuted}/{res.MaxRounds}");
        Console.WriteLine($"NET_RETRIES_USED={res.NetworkRetriesUsed}");
        Console.WriteLine($"FAILOVER_OCCURRED={res.FailoverOccurred}");
        Console.WriteLine($"CAN_RESUME_CHECKPOINT={res.CanResumeFromCheckpoint}");
        Console.WriteLine($"VERIFY_PASSED={res.VerificationPassed}");
        Console.WriteLine($"VERIFY_SUMMARY={res.VerificationSummary}");
        Console.WriteLine($"SESSION_ID={res.SessionId}");
        Console.WriteLine($"SESSION_ACTION={res.SessionAction}");
        Console.WriteLine($"INBOX_FILE={res.OutputInboxFile}");
        Console.WriteLine($"SUMMARY={res.SummaryMessage}");
        if (!string.IsNullOrWhiteSpace(res.OutputText))
        {
            Console.WriteLine("--- INBOX_CONTENT_START ---");
            Console.WriteLine(res.OutputText);
            Console.WriteLine("--- INBOX_CONTENT_END ---");
        }
    }

    private static string ReadSingleArg(string[] args, string flag, string defaultValue)
    {
        var idx = Array.IndexOf(args, flag);
        return (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--")) ? args[idx + 1] : defaultValue;
    }

    private static string? ReadMultiWordArg(string[] args, string flag)
    {
        var idx = Array.IndexOf(args, flag);
        if (idx < 0 || idx + 1 >= args.Length) return null;
        var parts = new List<string>();
        for (int i = idx + 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--")) break;
            parts.Add(args[i]);
        }
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    private static int RunSelfTest(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var isolatedData = Path.Combine(outDir, "isolated_data");
        if (Directory.Exists(isolatedData)) Directory.Delete(isolatedData, recursive: true);
        AppPaths.SetDataDirOverride(isolatedData);

        var log = new List<string>();
        void AssertTrue(bool cond, string msg)
        {
            if (!cond) throw new InvalidOperationException("SELF_TEST_FAIL: " + msg);
            log.Add("PASS: " + msg);
        }

        // 1. 准备隔离的双目录模拟工程（不触碰真实游戏业务文件）
        var mockMain = Path.Combine(outDir, "mock_game_main");
        var mockTool = Path.Combine(outDir, "mock_companion_tool");
        if (Directory.Exists(mockMain)) Directory.Delete(mockMain, recursive: true);
        if (Directory.Exists(mockTool)) Directory.Delete(mockTool, recursive: true);
        Directory.CreateDirectory(Path.Combine(mockMain, ".cursor", "rules"));
        Directory.CreateDirectory(Path.Combine(mockMain, ".cursor", "skills", "multi-ai-auto-dispatch"));
        Directory.CreateDirectory(Path.Combine(mockMain, ".cursor", "skills", "godot-visual-mcp-accept"));
        Directory.CreateDirectory(Path.Combine(mockMain, "scripts"));
        Directory.CreateDirectory(mockTool);

        File.WriteAllText(Path.Combine(mockMain, "project.godot"), "; Engine configuration\nconfig_version=5\n");
        File.WriteAllText(Path.Combine(mockMain, ".cursor", "rules", "verify-before-delivery.mdc"), "---\ndescription: 做完必须自己运行真实测试或查看真实截图验证\n---\n# 交付前必验硬闸门\n");
        File.WriteAllText(Path.Combine(mockMain, ".cursor", "rules", "agent-input-hygiene.mdc"), "---\ndescription: 能后台脚本或 MCP 完成的操作严禁抢占用户键鼠\n---\n# 键鼠卫生规范\n");
        File.WriteAllText(Path.Combine(mockMain, ".cursor", "skills", "multi-ai-auto-dispatch", "SKILL.md"), "---\nname: multi-ai-auto-dispatch\ndescription: 多 AI 全自动跨工具对话框寻址、读取内容、续接或新建对话框分配工作与结果回收\n---\n# 总包工头规程\n");
        File.WriteAllText(Path.Combine(mockMain, ".cursor", "skills", "godot-visual-mcp-accept", "SKILL.md"), "---\nname: godot-visual-mcp-accept\ndescription: Godot 画面与角色外观改动的 MCP 截图实看验收\n---\n# 画面截图验收\n");
        var sampleScript = Path.Combine(mockMain, "scripts", "combat_demo.gd");
        File.WriteAllText(sampleScript, "extends Node\nvar damage := 10\n", new UTF8Encoding(false));

        var app = CreateWpfApp();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var reg = new RegistryService();
        reg.LoadAll();

        // 绑定测试项目档案
        reg.UserConfig.Projects.Clear();
        var testProj = new ProjectProfile
        {
            Id = "test_proj",
            Name = "隔离验收测试工程 (Godot 双目录)",
            PrimaryRoot = mockMain,
            CompanionRoots = new List<string> { mockTool },
            PresetId = "godot_project"
        };
        reg.UserConfig.Projects.Add(testProj);
        reg.UserConfig.ActiveProjectId = testProj.Id;
        reg.SaveUserConfig();

        // 2. 验证本机探测
        var discovery = new DiscoveryService();
        int foundCount = discovery.RunDiscovery(reg);
        AssertTrue(foundCount >= 3, $"本机真实扫描检出项 >= 3 (实际={foundCount})");

        // 3. 验证同步引擎与快照 Diff / 回滚 / 脱敏胶囊 / 归档
        var sync = new SyncService();
        var bus = new CollabBusService();
        var syncMsg = sync.ExecuteSyncAll(reg, testProj, bus);
        AssertTrue(File.Exists(Path.Combine(mockMain, ".agents", "skills.json")), "主工程已生成 .agents/skills.json 零拷贝指针");
        AssertTrue(File.Exists(Path.Combine(mockTool, ".agents", "skills.json")), "副目录已生成跨目录 .agents/skills.json 指针");
        AssertTrue(File.Exists(Path.Combine(mockTool, "AGENTS.md")), "副目录已自动补齐 AGENTS.md 跳板");
        AssertTrue(File.Exists(Path.Combine(mockMain, "work", "collab", ".gdignore")), "Godot 工程协作目录已自动写入 .gdignore");
        var ruleSkillInv = sync.InspectRulesAndSkillsInventory(reg, testProj);
        AssertTrue(ruleSkillInv.Count(x => x.Kind == "skill") >= 2 && ruleSkillInv.Count(x => x.Kind == "rule") >= 2, "已完整检出项目内所有技能 (Skills) 与规则 (Rules) 及各 AI 应用状态");

        // 快照 + 改动 + Diff + 回滚
        bus.CreateBaselineSnapshot(reg, testProj);
        File.WriteAllText(sampleScript, "extends Node\nvar damage := 99\nvar api := \"sk-SecretTestKey1234567890\"\n", new UTF8Encoding(false));
        bus.GenerateLatestDiff(reg, testProj);
        var diffTxt = File.ReadAllText(bus.GetLatestDiffPath(testProj), Encoding.UTF8);
        AssertTrue(diffTxt.Contains("+var damage := 99"), "无 Git 快照引擎准确捕获行级新增改动");

        // 脱敏远程胶囊导出校验
        var packFile = bus.BuildSanitizedRemotePack(reg, testProj, true, true, true);
        var packTxt = File.ReadAllText(packFile, Encoding.UTF8);
        AssertTrue(!packTxt.Contains("sk-SecretTestKey1234567890") && packTxt.Contains("[REDACTED_SECRET]"), "远程胶囊已自动脱敏过滤密钥");
        AssertTrue(!packTxt.Contains(mockMain, StringComparison.OrdinalIgnoreCase), "远程胶囊已将本机绝对路径脱敏替换为 ${PROJECT_ROOT}");

        // 回滚至基准快照
        bus.RollbackToBaseline(reg, testProj);
        var rolledBack = File.ReadAllText(sampleScript, Encoding.UTF8);
        AssertTrue(rolledBack.Contains("var damage := 10") && !rolledBack.Contains("99"), "一键回滚准确还原源码且保留回滚前草稿备份");

        // 收口归档与沉淀经验
        bus.ArchiveCurrentTask(testProj, "测试沉淀经验：动场景前先核对快照");
        var pitfallsTxt = File.ReadAllText(Path.Combine(mockMain, ".cursor", "memory", "shared-pitfalls.md"), Encoding.UTF8);
        AssertTrue(pitfallsTxt.Contains("测试沉淀经验：动场景前先核对快照"), "收口归档已将新避坑经验写入共读经验库");

        // 全自动后台静默拉起派单与跨工具项目对话框寻址（无对话框自动新建 -> 有对话框自动续接并读取）验证
        reg.UserConfig.Agents.Add(new AgentRosterEntry
        {
            Id = "mock_headless_ai",
            Name = "后台静默协同引擎",
            Category = "local_cli",
            Enabled = true,
            Detected = true,
            RunnerMode = "cli_custom",
            RunnerCmd = "powershell -NoProfile -Command \"[IO.File]::WriteAllText('{OUTPUT_FILE}', '【后台AI推演结论】技能冷却与数值边界核验通过 (Session={SESSION_ID})', [Text.Encoding]::UTF8)\"",
            TagScores = new Dictionary<string, int> { ["design_copywriting"] = 5 }
        });
        var autoDispatch = new AutoDispatchService();
        var dispCreate = autoDispatch.ExecuteHeadlessDispatch(reg, testProj, bus, "mock_headless_ai", "第1轮：请核验雷劫奇遇技能数值曲线", 30, "auto");
        AssertTrue(dispCreate.Success && dispCreate.SessionAction == "created" && !string.IsNullOrWhiteSpace(dispCreate.SessionId), "当项目尚无对应对话框时，自动在该项目目录下新建对话框并分配工作");
        AssertTrue(File.Exists(dispCreate.OutputInboxFile) && dispCreate.OutputText.Contains("【后台AI推演结论】"), "新建对话框执行完毕后产出完整收回至 work/collab/inbox/");

        var dispResume = autoDispatch.ExecuteHeadlessDispatch(reg, testProj, bus, "mock_headless_ai", "第2轮：请继续补充暴击率衰减公式", 30, "auto");
        AssertTrue(dispResume.Success && dispResume.SessionAction == "resumed" && dispResume.SessionId == dispCreate.SessionId, "再次分配工作时自动找到该项目已有的对话框并续接上下文执行");

        var sessionSvc = new SessionDialogService();
        var foundProjSessions = sessionSvc.DiscoverProjectSessions(testProj, "mock_headless_ai");
        AssertTrue(foundProjSessions.Count >= 1 && foundProjSessions[0].TurnCount >= 4, "可按项目目录准确检索并读取对应 AI 工具对话框的多轮历史与反馈");
        var readMd = sessionSvc.ReadSessionTranscriptMarkdown(foundProjSessions[0], 10);
        AssertTrue(readMd.Contains("第1轮") && readMd.Contains("第2轮"), "跨工具对话框内容读取完整包含历史指令与 AI 反馈");
        AssertTrue(File.Exists(Path.Combine(mockMain, "work", "collab", "LIVE_STATUS.md")), "已实时生成 LIVE_STATUS.md 供用户随时查看当前工作状态");
        reg.UserConfig.Agents.RemoveAll(a => a.Id == "mock_headless_ai");

        // 验证「主脑智能选模型与高中低档位 + 验收不通过自动打回同一对话框返工直至通过」闭环
        var reworkMarker = Path.Combine(outDir, "rework_round_marker.txt");
        if (File.Exists(reworkMarker)) File.Delete(reworkMarker);
        var reworkScriptCmd =
            $"powershell -NoProfile -Command \"$m='{reworkMarker.Replace("'", "''")}'; " +
            $"if (!(Test-Path $m)) {{ [IO.File]::WriteAllText($m, '1'); [IO.File]::WriteAllText('{{OUTPUT_FILE}}', '首轮尝试尚有疏漏 [[NEEDS_REWORK]]', [Text.Encoding]::UTF8) }} " +
            $"else {{ [IO.File]::WriteAllText('{sampleScript.Replace("'", "''")}', 'extends Node`nvar damage := 250`n', [Text.Encoding]::UTF8); [IO.File]::WriteAllText('{{OUTPUT_FILE}}', '【落地修改汇报】已将 scripts/combat_demo.gd 的 damage 实操修改为 250 并完成自检。', [Text.Encoding]::UTF8) }}\"";

        reg.UserConfig.Agents.Add(new AgentRosterEntry
        {
            Id = "mock_rework_ai",
            Name = "多轮返工实操引擎",
            Category = "local_cli",
            Enabled = true,
            Detected = true,
            RunnerMode = "cli_custom",
            RunnerCmd = reworkScriptCmd,
            ModelSelectionMode = "auto",
            PreferredEffortTier = "auto",
            HighTierModel = "Claude 4.6 Sonnet Thinking (High)",
            MediumTierModel = "Claude 4.5 Sonnet (Medium)",
            LowTierModel = "Cursor Fast (Low)",
            TagScores = new Dictionary<string, int> { ["code_edit"] = 5 }
        });

        var reworkOutcome = autoDispatch.ExecuteHeadlessDispatch(
            reg,
            testProj,
            bus,
            "mock_rework_ai",
            "请直接落地修改工程文件 scripts/combat_demo.gd，将 damage 参数调整为 250",
            30,
            "new");
        AssertTrue(
            reworkOutcome.Success && reworkOutcome.VerificationPassed && reworkOutcome.RoundsExecuted == 2,
            $"首轮未通过主脑验收时，自动打回同一对话框执行第 2 轮实操修复并通过主脑验收 (实际轮次={reworkOutcome.RoundsExecuted}, Passed={reworkOutcome.VerificationPassed})");
        AssertTrue(
            reworkOutcome.SelectedEffortTier == "high" && reworkOutcome.SelectedModel.Contains("High"),
            $"打回返工轮次自动提档至高档深度模型执行 (实际模型={reworkOutcome.SelectedModel})");
        AssertTrue(
            File.ReadAllText(sampleScript, Encoding.UTF8).Contains("var damage := 250"),
            "执行 AI 已真正动手修改工程脚本文件落盘并通过主脑 Diff 核验");
        reg.UserConfig.Agents.RemoveAll(a => a.Id == "mock_rework_ai");

        // 3.5 验证全场景网络容灾：远端 503/连接重置退避重连 + 本机断网挂起恢复 + 跨 AI 故障转移 + 彻底断网断点封存与一键续跑
        var flakyCounterFile = Path.Combine(outDir, "flaky_net_counter.txt");
        if (File.Exists(flakyCounterFile)) File.Delete(flakyCounterFile);
        var flakyCmd =
            $"powershell -NoProfile -Command \"$c='{flakyCounterFile.Replace("'", "''")}'; " +
            $"$n = if (Test-Path $c) {{ [int](Get-Content $c) }} else {{ 0 }}; $n++; [IO.File]::WriteAllText($c, $n.ToString()); " +
            $"if ($n -le 2) {{ [Console]::Error.WriteLine('503 Service Unavailable / ECONNRESET'); exit 1 }} " +
            $"else {{ [IO.File]::WriteAllText('{sampleScript.Replace("'", "''")}', 'extends Node`nvar damage := 360`n', [Text.Encoding]::UTF8); [IO.File]::WriteAllText('{{OUTPUT_FILE}}', '【落地修改汇报】断线重连成功后已将 damage 改为 360', [Text.Encoding]::UTF8) }}\"";

        reg.UserConfig.Agents.Add(new AgentRosterEntry
        {
            Id = "mock_flaky_net_ai",
            Name = "弱网重连实操引擎",
            Category = "local_cli",
            Enabled = true,
            Detected = true,
            RunnerMode = "cli_custom",
            RunnerCmd = flakyCmd,
            TagScores = new Dictionary<string, int> { ["code_edit"] = 5 }
        });

        var netRetryOutcome = autoDispatch.ExecuteHeadlessDispatch(
            reg,
            testProj,
            bus,
            "mock_flaky_net_ai",
            "请直接落地修改工程文件 scripts/combat_demo.gd，将 damage 改为 360",
            30,
            "new");
        AssertTrue(
            netRetryOutcome.Success && netRetryOutcome.NetworkRetriesUsed == 2 && File.ReadAllText(sampleScript, Encoding.UTF8).Contains("360"),
            $"AI 服务端 503/ECONNRESET 波动时自动执行指数退避重连并重发成功 (重连次数={netRetryOutcome.NetworkRetriesUsed})");

        // 模拟本机网卡短暂掉线（前 3 次探测离线，随后恢复在线）
        NetworkResilienceService.SimulatedOfflineProbesRemaining = 3;
        var localRecoverOutcome = autoDispatch.ExecuteHeadlessDispatch(
            reg,
            testProj,
            bus,
            "mock_flaky_net_ai",
            "请直接落地修改工程文件 scripts/combat_demo.gd，核验本机断网恢复重连",
            30,
            "auto");
        AssertTrue(
            localRecoverOutcome.Success && localRecoverOutcome.NetworkRetriesUsed >= 1 && NetworkResilienceService.SimulatedOfflineProbesRemaining == 0,
            "本机网络掉线时自动挂起轮询等待本机网络恢复，恢复后立即无感重发完成任务");
        reg.UserConfig.Agents.RemoveAll(a => a.Id == "mock_flaky_net_ai");

        // 模拟单个 AI 服务器彻底瘫痪时，主脑自动触发「跨 AI 备用故障转移」接力（且严格不动未勾选启用的工具）
        var savedEnabledSnapshot = reg.UserConfig.Agents.ToDictionary(a => a.Id, a => a.Enabled);
        foreach (var a in reg.UserConfig.Agents)
        {
            if (a.Id != "antigravity") a.Enabled = false;
        }
        reg.UserConfig.Agents.Add(new AgentRosterEntry
        {
            Id = "mock_dead_server_ai",
            Name = "服务端宕机主力AI",
            Category = "local_cli",
            Enabled = true,
            Detected = true,
            RunnerMode = "cli_custom",
            RunnerCmd = "powershell -NoProfile -Command \"[Console]::Error.WriteLine('502 Bad Gateway: upstream server down'); exit 1\"",
            TagScores = new Dictionary<string, int> { ["code_edit"] = 5 }
        });
        reg.UserConfig.Agents.Add(new AgentRosterEntry
        {
            Id = "mock_healthy_backup_ai",
            Name = "健康备用接力AI",
            Category = "local_cli",
            Enabled = true,
            Detected = true,
            RunnerMode = "cli_custom",
            RunnerCmd = $"powershell -NoProfile -Command \"[IO.File]::WriteAllText('{sampleScript.Replace("'", "''")}', 'extends Node`nvar damage := 520`n', [Text.Encoding]::UTF8); [IO.File]::WriteAllText('{{OUTPUT_FILE}}', '【备用接力落地汇报】已接力修改 damage := 520', [Text.Encoding]::UTF8)\"",
            TagScores = new Dictionary<string, int> { ["code_edit"] = 4 }
        });

        var failoverOutcome = autoDispatch.ExecuteHeadlessDispatch(
            reg,
            testProj,
            bus,
            "mock_dead_server_ai",
            "请直接落地修改工程文件 scripts/combat_demo.gd，将 damage 改为 520",
            30,
            "new");
        AssertTrue(
            failoverOutcome.Success && failoverOutcome.FailoverOccurred && failoverOutcome.AgentId == "mock_healthy_backup_ai" && File.ReadAllText(sampleScript, Encoding.UTF8).Contains("520"),
            $"单个 AI 服务器持续宕机时，主脑自动故障转移切换至已启用的备用 AI 接力完成实操落盘 (接力者={failoverOutcome.AgentName})");

        // 模拟本机长时间彻底断网导致任务链暂停保存断点，网络恢复后调用 ResumeInterruptedTaskChain 一键原地断点续跑！
        NetworkResilienceService.SimulatedOfflineProbesRemaining = 9999;
        var interruptedOutcome = autoDispatch.ExecuteHeadlessDispatch(
            reg,
            testProj,
            bus,
            "mock_healthy_backup_ai",
            "请直接落地修改工程文件 scripts/combat_demo.gd，测试长时间断网断点保护与续跑",
            30,
            "new");
        AssertTrue(
            !interruptedOutcome.Success && interruptedOutcome.CanResumeFromCheckpoint && NetworkResilienceService.LoadInterruptedCheckpoint(testProj) != null,
            "长时间彻底断网时自动保护封存任务链进度至 TASK_CHAIN_CHECKPOINT.json 断点账本");

        NetworkResilienceService.SimulatedOfflineProbesRemaining = 0;
        var resumedOutcome = autoDispatch.ResumeInterruptedTaskChain(reg, testProj, bus, 30);
        AssertTrue(
            resumedOutcome.Success && NetworkResilienceService.LoadInterruptedCheckpoint(testProj) == null,
            "网络恢复后一键断点续跑成功完成任务链并自动清理已完成断点");

        reg.UserConfig.Agents.RemoveAll(a => a.Id is "mock_dead_server_ai" or "mock_healthy_backup_ai");
        foreach (var a in reg.UserConfig.Agents)
        {
            if (savedEnabledSnapshot.TryGetValue(a.Id, out var wasOn)) a.Enabled = wasOn;
        }

        // 13. 验证 Magpie 式工作定位与模型精细路由（支持按生图/文本/快执行/总分析精准路由到指定 AI 与模型）
        var grokAg = reg.UserConfig.Agents.FirstOrDefault(a => a.Id == "grok_bot");
        var cursorAg = reg.UserConfig.Agents.FirstOrDefault(a => a.Id == "cursor");
        if (grokAg != null && cursorAg != null)
        {
            grokAg.Enabled = true;
            cursorAg.Enabled = true;
            CloudRecommendationService.EnsureAgentWorkProfilesPopulated(grokAg, overwrite: true);
            CloudRecommendationService.EnsureAgentWorkProfilesPopulated(cursorAg, overwrite: true);
            var grokTier = RoutingService.ResolveAgentModelAndTier(grokAg, "high", false);
            AssertTrue(
                !LocalModelCatalogService.SupportsModelSelection("grok_bot") &&
                grokAg.ModelProfiles.Count == 1 &&
                grokAg.ModelProfiles[0].ModelName == LocalModelCatalogService.ManagedDefaultModelDisplay &&
                string.IsNullOrEmpty(grokTier.EngineCodexModel),
                $"Grok Bot 锁定客户端默认托管且不传模型覆写参数以防风控 (Profiles={grokAg.ModelProfiles.Count}, EngineFlag='{grokTier.EngineCodexModel}')");

            var autoImgRoute = RoutingService.ResolveRouteForWorkType(reg, testProj, "image_generation", "请为新角色生成一张概念设计图");
            AssertTrue(
                autoImgRoute.TargetAgent?.Id == "grok_bot" && autoImgRoute.TargetModelName == LocalModelCatalogService.ManagedDefaultModelDisplay,
                $"Magpie 路由命中不可选模型的 Grok Bot 并保持安全锁定模型 (Agent={autoImgRoute.TargetAgent?.Name}, Model={autoImgRoute.TargetModelName})");

            testProj.WorkTypeRoutes["fast_execution"] = new WorkRouteRule
            {
                WorkTypeId = "fast_execution",
                TargetAgentId = "cursor",
                TargetModelName = "cursor-fast-low",
                TargetEffortTier = "low"
            };
            var execDecision = RoutingService.AnalyzeAndSelectAgentForTask(
                reg, testProj, "请快速修改一下冷却数值并直接落地改工程", "auto",
                reg.UserConfig.Agents.Where(a => a.Enabled).ToList());
            AssertTrue(
                execDecision.TargetAgent?.Id == "cursor" && execDecision.TargetSelectedModel == "cursor-fast-low",
                $"Magpie 精细路由表指定「快速实行」命中指定 AI 与极速模型 ({execDecision.TargetAgent?.Name} / {execDecision.TargetSelectedModel})");
            testProj.WorkTypeRoutes.Remove("fast_execution");
        }

        // 13.5 验证从本机客户端配置实时读取桌面端真正可选模型列表，自动过滤隐藏/未开启/下线模型并禁止无效自定义输入
        var agyModels = LocalModelCatalogService.ProbeLocalModelsForAgent("antigravity", forceRefresh: true);
        var codexLocalModels = LocalModelCatalogService.ProbeLocalModelsForAgent("codex_cli", forceRefresh: true);
        var cursorModels = LocalModelCatalogService.ProbeLocalModelsForAgent("cursor", forceRefresh: true);
        AssertTrue(
            agyModels.Models.Contains("Gemini 4 Carbon") && agyModels.Models.Contains("Claude Opus 5.5") &&
            !codexLocalModels.Models.Any(m => m.Contains("gpt-reserve", StringComparison.OrdinalIgnoreCase) || m.Contains("codex-auto-review", StringComparison.OrdinalIgnoreCase)) &&
            !cursorModels.Models.Any(m => m.Contains("kimi-k3", StringComparison.OrdinalIgnoreCase) || m.Contains("glm-5", StringComparison.OrdinalIgnoreCase)) &&
            !LocalModelCatalogService.SupportsCustomModelInput("cursor"),
            $"本机直读严格过滤桌面不可选模型 (Antigravity={agyModels.Models.Count}, Cursor={cursorModels.Models.Count}, Codex={codexLocalModels.Models.Count})");

        // 13.6 验证云端模型特征推断 + 统一单一数据源回写 + 旧上下分裂配置平滑合并（绝不打架）
        var agyEntry = reg.UserConfig.Agents.FirstOrDefault(a => a.Id == "antigravity");
        if (agyEntry != null)
        {
            var gemCarbonSpec = CloudRecommendationService.InferCloudModelSpec("antigravity", "Gemini 4 Carbon");
            var orchSpec = CloudRecommendationService.InferCloudModelSpec("antigravity", "Orchestrator (Carbon)");
            var sonnetSpec = CloudRecommendationService.InferCloudModelSpec("antigravity", "Claude Sonnet 5.5");
            var argonSpec = CloudRecommendationService.InferCloudModelSpec("antigravity", "Gemini 4 Argon");
            var autoSpec = CloudRecommendationService.InferCloudModelSpec("cursor", "Auto (default)");
            var flashSpec = CloudRecommendationService.InferCloudModelSpec("antigravity", "Gemini 3.8 Flash");
            AssertTrue(
                gemCarbonSpec.Tier == "high" && gemCarbonSpec.RecommendedRoles.Contains("brain_analysis") &&
                orchSpec.Tier == "high" && orchSpec.RecommendedRoles.Contains("brain_analysis") &&
                sonnetSpec.Tier == "medium" && argonSpec.Tier == "medium" && autoSpec.Tier == "medium",
                $"云端推断各档位分层准确 (Carbon={gemCarbonSpec.Tier}, Orch={orchSpec.Tier}, Sonnet={sonnetSpec.Tier}, Argon={argonSpec.Tier}, Auto={autoSpec.Tier})");
            AssertTrue(
                flashSpec.Tier == "low" && flashSpec.SpeedTier == "fast",
                $"云端推断 Gemini 3.8 Flash 准确识别为 low 档极速模型 (实际 Tier={flashSpec.Tier}, Speed={flashSpec.SpeedTier})");

            CloudRecommendationService.BuildAutoModelProfilesForAgent(agyEntry, refreshLocalModels: false);
            agyEntry.HighTierModel = "Orchestrator (Carbon)";
            CloudRecommendationService.ReconcileLegacySplitTiers(agyEntry);
            var firstHighSlot = agyEntry.ModelProfiles.FirstOrDefault(p => p.RecommendedEffort == "high");
            AssertTrue(
                firstHighSlot != null && firstHighSlot.ModelName == "Orchestrator (Carbon)" &&
                agyEntry.PrimaryWorkRoles.Contains("brain_analysis") &&
                agyEntry.HighTierModel == firstHighSlot.ModelName,
                $"统一模型分工单一数据源与旧分裂档位平滑合并生效 (HighSlot={firstHighSlot?.ModelName}, HighTier={agyEntry.HighTierModel}, Roles={string.Join(",", agyEntry.PrimaryWorkRoles)}, Profiles={string.Join(";", agyEntry.ModelProfiles.Select(p => p.ModelName + ":" + p.RecommendedEffort))})");

            // 13.7 验证模型槽位上下调整顺序后，靠前槽位优先成为对应档位与路由首选模型
            agyEntry.ModelProfiles.Insert(0, new ModelWorkProfile
            {
                ModelName = "Gemini 4 Carbon",
                PrimaryRoles = new List<string> { "brain_analysis", "deep_debug", "visual_accept" },
                RecommendedEffort = "high",
                SpeedTier = "high"
            });
            CloudRecommendationService.SyncDerivedFieldsFromProfiles(agyEntry);
            var routeAfterReorder = RoutingService.ResolveRouteForWorkType(reg, testProj, "brain_analysis");
            AssertTrue(
                agyEntry.HighTierModel == "Gemini 4 Carbon" && routeAfterReorder.TargetModelName == "Gemini 4 Carbon",
                $"模型槽位调整顺序后靠前槽位优先命中 (HighTier={agyEntry.HighTierModel}, RouteModel={routeAfterReorder.TargetModelName})");

            CloudRecommendationService.BuildAutoModelProfilesForAgent(agyEntry, refreshLocalModels: false);
        }

        // 验证跨任意新项目自动纳管 (--project-dir)
        var anotherProjDir = Path.Combine(outDir, "any_other_project");
        Directory.CreateDirectory(anotherProjDir);
        var autoSwitched = reg.EnsureOrSwitchProjectByDir(anotherProjDir);
        AssertTrue(autoSwitched.PrimaryRoot == Path.GetFullPath(anotherProjDir), "支持从任意项目对话框自动纳管并切换工程目录");
        reg.UserConfig.Projects.Remove(autoSwitched);
        reg.UserConfig.ActiveProjectId = testProj.Id;
        reg.SaveUserConfig();

        // 验证工作流总开关停用时彻底剥离协同锚点，重新开启时重新挂载
        sync.DeactivateAndStripAllCollabAnchors(reg, testProj);
        var strippedAgentsMd = File.ReadAllText(Path.Combine(mockMain, "AGENTS.md"), Encoding.UTF8);
        AssertTrue(!strippedAgentsMd.Contains("AI-COLLAB-HUB:START"), "停用工作流或关闭软件时，已彻底剥离 AGENTS.md 内的协同锚点恢复原生状态");
        sync.ExecuteSyncAll(reg, testProj, bus);
        var remountedAgentsMd = File.ReadAllText(Path.Combine(mockMain, "AGENTS.md"), Encoding.UTF8);
        AssertTrue(remountedAgentsMd.Contains("AI-COLLAB-HUB:START"), "重新开启工作流时，自动恢复挂载协同锚点");

        // 验证云端推荐评分拉取与一键应用（且保持未启用工具 Enabled=false 不变）
        var cloudSvc = new CloudRecommendationService();
        var codexEntry = reg.UserConfig.Agents.FirstOrDefault(a => a.Id == "codex_cli");
        if (codexEntry != null) codexEntry.Enabled = false;
        var cloudRep = cloudSvc.FetchAndAnalyzeRecommendations(reg, applyToAgents: true);
        AssertTrue(cloudRep.Items.Count >= 3, "云端推荐能力评分引擎返回完整 AI 评分建议");
        if (codexEntry != null)
        {
            AssertTrue(!codexEntry.Enabled && codexEntry.TagScores.Count >= 5, "应用推荐评分后 Codex 保持不启用且能力星级已就绪");
        }

        // 4. 离屏渲染全部 5 个主界面页面截图（不抢占用户桌面焦点或键鼠！）
        var shotsDir = Path.Combine(outDir, "shots");
        Directory.CreateDirectory(shotsDir);

        var win = new MainWindow(reg)
        {
            Width = 1180,
            Height = 740,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowActivated = false
        };
        win.Show();
        var navAdv = win.FindName("NavAdvancedExpander") as Expander;
        AssertTrue(navAdv != null && navAdv.IsExpanded == false, "MainWindow 初始化时 NavAdvancedExpander 默认收起");
        win.PreloadCachesForLiveCapture();

        var tabNames = new[] { "01_overview", "02_dialogs", "03_sync", "04_ops", "05_settings" };
        for (int idx = 1; idx <= 5; idx++)
        {
            win.SwitchToTabForTest(idx);
            win.UpdateLayout();
            CaptureElementPng(win, 1220, 760, Path.Combine(shotsDir, $"shot_{tabNames[idx - 1]}.png"));
        }

        // 同时验证最小窗口尺寸 960x600 下的多页对齐与自适应换行（无裁切、无遮挡）
        win.Width = 960;
        win.Height = 600;
        win.SwitchToTabForTest(1);
        win.UpdateLayout();
        CaptureElementPng(win, 960, 600, Path.Combine(shotsDir, "shot_06_minsize_overview_960x600.png"));

        win.SwitchToTabForTest(2);
        win.UpdateLayout();
        CaptureElementPng(win, 960, 600, Path.Combine(shotsDir, "shot_07_minsize_dialogs_960x600.png"));

        // 4.5 防卡死压测：并发多线程读写模型目录缓存 + UI 主线程悬浮窗刷新与二级弹窗构建耗时断言（严防 AppHangB1）
        var parallelTasks = Enumerable.Range(0, 8).Select(t => System.Threading.Tasks.Task.Run(() =>
        {
            for (int iter = 0; iter < 50; iter++)
            {
                _ = LocalModelCatalogService.ProbeLocalModelsForAgent(t % 2 == 0 ? "cursor" : "antigravity", forceRefresh: false);
                _ = CloudRecommendationService.InferCloudModelSpec("antigravity", "Gemini 4 Carbon");
            }
        })).ToArray();
        bool parallelFinished = System.Threading.Tasks.Task.WaitAll(parallelTasks, millisecondsTimeout: 3000);

        var uiSw = System.Diagnostics.Stopwatch.StartNew();
        var fwTest = win.GetOrCreateFloatingWindow();
        fwTest.RefreshOverlayData(force: true);
        fwTest.Close();
        var (cfgWinTest, _) = Dialogs.BuildAgentConfigDialogWindow(null, reg, reg.UserConfig.Agents[0], cloudSvc);
        cfgWinTest.Close();
        var (cloudWinTest, _) = Dialogs.BuildCloudRecommendationDialogWindow(null, reg, cloudSvc);
        cloudWinTest.Close();
        uiSw.Stop();
        AssertTrue(
            parallelFinished && uiSw.ElapsedMilliseconds < 450 && System.Windows.Input.Mouse.Captured == null,
            $"防 AppHangB1 并发缓存与 UI 主线程零阻塞压测通过 (并发完成={parallelFinished}, UI弹窗与悬浮窗刷新耗时={uiSw.ElapsedMilliseconds}ms, 鼠标残留捕获={System.Windows.Input.Mouse.Captured != null})");

        // 4.6 深度优化专项断言（44-47）：僵尸 working 状态收敛、核心任务短标题提炼、会话增量指纹缓存与冻结画刷池
        var zombieMap = new Dictionary<string, LiveAgentWorkState>(StringComparer.OrdinalIgnoreCase)
        {
            ["cursor"] = new LiveAgentWorkState
            {
                AgentId = "cursor",
                AgentName = "Cursor",
                ProjectName = testProj.Name,
                Status = "working",
                WorkflowPhase = "executing",
                DispatcherPid = 999999,
                UpdatedUtcTicks = DateTime.UtcNow.AddHours(-2).Ticks,
                TaskSummary = "僵尸残留任务"
            }
        };
        RegistryService.WriteJsonAtomic(AutoDispatchService.LiveStateMapPath, zombieMap);
        var sweptMap = AutoDispatchService.ReadAllLiveWorkStates();
        AssertTrue(
            sweptMap.TryGetValue("cursor", out var sweptEntry) && sweptEntry.Status != "working",
            $"僵尸 working 状态（不存在的 DispatcherPid=999999）已自动识别并收敛 (Status={sweptEntry?.Status})");
        AutoDispatchService.ClearFinishedOrStaleLiveStates();

        var dirtyPrompt = "你正在作为「Cursor」参与「多 AI 协同工作流」的闭环实操派单任务（当前第 1/3 轮）。\n- **全局统筹**：Antigravity\n\n## 【最高优先级】本次专门指派给你的实操任务指令（请直接动手落地完成本节指令）\n请直接落地修改工程文件：深度对标参考图：重建太玄地砖法线细节与缝隙质感。\n\n## 当前项目任务活卡";
        var cleanedBrief = AutoDispatchService.ExtractCleanTaskBrief(dirtyPrompt, 20);
        AssertTrue(
            !cleanedBrief.Contains("你正在作为") && !cleanedBrief.Contains("请直接落地修改工程文件") && cleanedBrief.Contains("重建太玄地砖"),
            $"核心任务短标题提炼准确剥离派单模板与套话前缀 (结果={cleanedBrief})");
        // v1.9.3：冒号引导词后取真实业务正文，并清理未闭合括号残句
        var truncatedParenBrief = AutoDispatchService.ExtractCleanTaskBrief(
            "请直接落地修改工程文件并完成编译自检（目标源码工程：修复悬浮窗主脑待命与胶囊态穿帮", 22);
        AssertTrue(
            !truncatedParenBrief.Contains("（目标源码工程") &&
            !truncatedParenBrief.StartsWith("请直接", StringComparison.Ordinal) &&
            (truncatedParenBrief.Contains("修复悬浮窗") || truncatedParenBrief.Contains("主脑待命") || truncatedParenBrief.Contains("胶囊")),
            $"ExtractCleanTaskBrief 冒号后正文+未闭合括号清理通过 (结果={truncatedParenBrief})");
        // v1.9.4：严禁把 Windows 盘符 C: 当引导冒号；路径前缀剥离后保留中文主题
        var driveColonBrief = AutoDispatchService.ExtractCleanTaskBrief(
            "请直接修改 C:/Project/Godot/配套工具/tools/ai-collab-hub 工程，完成 7 项问题修复与悬浮窗美学精雕升级", 24);
        AssertTrue(
            !driveColonBrief.StartsWith("/Project", StringComparison.OrdinalIgnoreCase) &&
            !driveColonBrief.StartsWith("\\Project", StringComparison.OrdinalIgnoreCase) &&
            !driveColonBrief.Contains("C:/Project", StringComparison.OrdinalIgnoreCase) &&
            !Regex.IsMatch(driveColonBrief, @"^[A-Za-z]:[\\/]") &&
            (driveColonBrief.Contains("完成") || driveColonBrief.Contains("问题修复") || driveColonBrief.Contains("悬浮窗")),
            $"ExtractCleanTaskBrief 盘符冒号不误切且保留中文主题 (结果={driveColonBrief}, Ver={AppPaths.AppVersion})");

        _ = sessionSvc.DiscoverProjectSessions(testProj, null);
        var cacheSw = System.Diagnostics.Stopwatch.StartNew();
        var cachedSessions = sessionSvc.DiscoverProjectSessions(testProj, null);
        cacheSw.Stop();
        AssertTrue(
            cachedSessions.Count >= 1 && cacheSw.ElapsedMilliseconds < 25,
            $"SessionDialogService 增量指纹缓存命中极速返回 (会话数={cachedSessions.Count}, 耗时={cacheSw.ElapsedMilliseconds}ms)");

        var b1 = UiBrushPool.Get("#38BDF8");
        var b2 = UiBrushPool.Get("#38BDF8");
        AssertTrue(
            b1.IsFrozen && ReferenceEquals(b1, b2),
            "UiBrushPool 全局冻结画刷池生效（零 GC 分配与同引用复用）");

        AssertTrue(
            win.FindName("HeaderVersionText") is TextBlock verText &&
            verText.Text == "v1.9.16" &&
            verText.Text == AppUpdateService.GetCurrentVersionDisplay() &&
            win.FindName("HeaderVersionBtn") is Button &&
            win.FindName("HeaderUpdateBadgeBtn") is Button updateBadge &&
            updateBadge.Visibility == Visibility.Collapsed &&
            win.FindName("TextAboutCurrentVersion") is TextBlock aboutVer &&
            aboutVer.Text.Contains("v1.9.16", StringComparison.Ordinal) &&
            win.FindName("TextAppUpdateStatus") is TextBlock &&
            win.FindName("TextUpdateRepoUrl") is TextBox &&
            win.FindName("ChkAutoCheckAppUpdate") is CheckBox autoUpdateChk &&
            autoUpdateChk.IsChecked == true &&
            win.FindName("BtnCheckAppUpdate") is Button,
            "顶栏版本徽标显示 v1.9.16，新版本按钮默认折叠，设置页更新卡片已挂上");
        AssertTrue(
            win.FindName("LblUpdateRepoUrl") is TextBlock updateRepoLabel &&
            updateRepoLabel.Visibility == Visibility.Collapsed &&
            win.FindName("TextUpdateRepoUrl") is TextBox updateRepoBox &&
            updateRepoBox.Visibility == Visibility.Collapsed,
            "设置页更新源标签与输入框默认隐藏");

        win.Width = 960;
        win.UpdateLayout();
        string brandTitle = win.Title ?? "";
        var brandBlock = win.FindName("HeaderBrandText") as TextBlock;
        var versionBlock = win.FindName("HeaderVersionText") as TextBlock;
        var headerGrid = win.FindName("HeaderBarGrid") as Grid;
        var headerBadge = win.FindName("HeaderUpdateBadgeBtn") as Button;
        var onboardBtn = win.FindName("HeaderOnboardingBtn") as Button;
        object? badgeContent = headerBadge?.Content;
        var badgeVisibility = headerBadge?.Visibility ?? Visibility.Collapsed;
        if (headerBadge != null)
        {
            headerBadge.Visibility = Visibility.Visible;
            headerBadge.Content = "发现新版本 v1.9.17";
        }
        win.UpdateLayout();
        double headerUsed = 0;
        if (headerGrid != null)
        {
            foreach (UIElement child in headerGrid.Children)
            {
                if (child is FrameworkElement fe && fe.Visibility == Visibility.Visible)
                    headerUsed += fe.ActualWidth;
            }
        }
        bool headerFits = headerGrid != null && headerGrid.ActualWidth >= 900 && headerUsed <= headerGrid.ActualWidth + 1.0;
        bool headerBrandOk = brandBlock != null &&
            brandBlock.Text.Contains("AIDeck", StringComparison.Ordinal) &&
            brandBlock.ActualWidth > 40;
        bool headerVersionOk = versionBlock != null && versionBlock.Text == "v1.9.16" && versionBlock.ActualWidth > 20;
        bool headerBadgeOk = headerBadge != null &&
            headerBadge.Visibility == Visibility.Visible &&
            headerBadge.ActualWidth > 20;
        // DesiredSize 含左右 Margin，ActualWidth 不含；须加回 Margin 后再与 DesiredSize 比，否则带 Margin 的顶栏项会恒判截断。
        bool brandFits = brandBlock != null &&
            brandBlock.ActualWidth + brandBlock.Margin.Left + brandBlock.Margin.Right + 0.5 >= brandBlock.DesiredSize.Width;
        bool versionFits = versionBlock != null &&
            versionBlock.ActualWidth + versionBlock.Margin.Left + versionBlock.Margin.Right + 0.5 >= versionBlock.DesiredSize.Width;
        bool badgeFits = headerBadge != null &&
            headerBadge.ActualWidth + headerBadge.Margin.Left + headerBadge.Margin.Right + 0.5 >= headerBadge.DesiredSize.Width;
        bool onboardOk = onboardBtn != null &&
            onboardBtn.Visibility == Visibility.Visible &&
            onboardBtn.ActualWidth >= 24 &&
            ((System.Windows.Automation.AutomationProperties.GetName(onboardBtn) ?? "").Contains("新手向导", StringComparison.Ordinal)
             || (onboardBtn.ToolTip as string ?? "").Contains("新手向导", StringComparison.Ordinal));
        bool onboardFits = onboardBtn != null &&
            onboardBtn.ActualWidth + onboardBtn.Margin.Left + onboardBtn.Margin.Right + 0.5 >= onboardBtn.DesiredSize.Width;
        double brandW = brandBlock?.ActualWidth ?? 0;
        double verW = versionBlock?.ActualWidth ?? 0;
        double badgeW = headerBadge?.ActualWidth ?? 0;
        if (headerBadge != null)
        {
            headerBadge.Content = badgeContent;
            headerBadge.Visibility = badgeVisibility;
        }
        AssertTrue(
            brandTitle.Contains("AIDeck", StringComparison.Ordinal) &&
            brandTitle.Contains("v1.9.16", StringComparison.Ordinal) &&
            headerBrandOk && headerVersionOk && headerBadgeOk && headerFits &&
            brandFits && versionFits && badgeFits && onboardOk && onboardFits,
            $"主窗口标题包含 AIDeck 且 HeaderVersionText 仍为 v1.9.16，顶栏不截断 (Title={brandTitle}, Ver={versionBlock?.Text}, BrandW={brandW:0}, VerW={verW:0}, BadgeW={badgeW:0}, OnboardW={onboardBtn?.ActualWidth:0}, HeaderUsed={headerUsed:0}, HeaderW={headerGrid?.ActualWidth:0}, brandFits={brandFits}, versionFits={versionFits}, badgeFits={badgeFits}, onboardFits={onboardFits}, headerFits={headerFits})");

        navAdv!.IsExpanded = false;
        win.SwitchToTabForTest(3);
        AssertTrue(navAdv.IsExpanded, "SwitchToTabForTest(3) 后 NavAdvancedExpander 自动展开");
        navAdv.IsExpanded = false;
        win.SwitchToTabForTest(4);
        AssertTrue(navAdv.IsExpanded, "SwitchToTabForTest(4) 后 NavAdvancedExpander 自动展开");

        bool HeaderIconFits(Button? b) =>
            b != null && b.Visibility == Visibility.Visible && b.ActualWidth >= 24 &&
            b.ActualWidth + b.Margin.Left + b.Margin.Right + 0.5 >= b.DesiredSize.Width;
        var navSettingsHidden = win.FindName("NavAppSettings") as RadioButton;
        var topSettingsBtn = win.FindName("TopOpenSettingsBtn") as Button;
        var floatHeaderBtn = win.FindName("ToggleFloatingBtn") as Button;
        var wfSwitchBtn = win.FindName("WorkflowSwitchBtn") as Button;
        var wfSwitchLabel = win.FindName("WorkflowSwitchLabel") as TextBlock;
        AssertTrue(navSettingsHidden != null && navSettingsHidden.Visibility == Visibility.Collapsed,
            "左侧不再重复显示设置项");
        AssertTrue(HeaderIconFits(topSettingsBtn) && HeaderIconFits(floatHeaderBtn) && HeaderIconFits(onboardBtn),
            $"960px 顶栏设置、悬浮窗、新手向导均可见且不截断 (SettingsW={topSettingsBtn?.ActualWidth:0}, FloatW={floatHeaderBtn?.ActualWidth:0}, OnboardW={onboardBtn?.ActualWidth:0})");
        AssertTrue(wfSwitchBtn != null && wfSwitchBtn.Visibility == Visibility.Visible && wfSwitchBtn.Content is not string
            && wfSwitchLabel != null && (wfSwitchLabel.Text == "开" || wfSwitchLabel.Text == "关"),
            "协同开关保留矢量图标，只显示开或关");

        win.SwitchToTabForTest(6);
        win.UpdateLayout();
        var settingsPage = win.FindName("PageAppSettings") as FrameworkElement;
        var activeSettingsStyle = win.FindResource("IconToolBtnActive") as Style;
        var idleSettingsStyle = win.FindResource("IconToolBtn") as Style;
        AssertTrue(settingsPage != null && settingsPage.Visibility == Visibility.Visible
            && ReferenceEquals(topSettingsBtn?.Style, activeSettingsStyle),
            "SwitchToTabForTest(6) 进入设置二级页并点亮顶栏设置按钮");

        string[] settingsKeys = { "general", "appearance", "resilience", "about" };
        string[] settingsPanels = { "SettingsPanelGeneral", "SettingsPanelAppearance", "SettingsPanelResilience", "SettingsPanelAbout" };
        string[] settingsTabs = { "TabSettingsGeneral", "TabSettingsAppearance", "TabSettingsResilience", "TabSettingsAbout" };
        for (int si = 0; si < settingsKeys.Length; si++)
        {
            win.SwitchSettingsTabForTest(settingsKeys[si]);
            win.UpdateLayout();
            bool tabOk = true;
            for (int sj = 0; sj < settingsPanels.Length; sj++)
            {
                var panel = win.FindName(settingsPanels[sj]) as FrameworkElement;
                var tab = win.FindName(settingsTabs[sj]) as RadioButton;
                if (panel == null || tab == null) { tabOk = false; break; }
                bool selected = sj == si;
                if (selected)
                {
                    if (panel.Visibility != Visibility.Visible || tab.IsChecked != true) tabOk = false;
                }
                else if (panel.Visibility != Visibility.Collapsed || tab.IsChecked == true)
                {
                    tabOk = false;
                }
            }
            AssertTrue(tabOk, $"设置分标签 {settingsKeys[si]} 互斥显隐");
        }
        var aboutRepoLabel = win.FindName("LblUpdateRepoUrl") as TextBlock;
        var aboutRepoBox = win.FindName("TextUpdateRepoUrl") as TextBox;
        AssertTrue(aboutRepoLabel != null && aboutRepoLabel.Visibility == Visibility.Collapsed
            && aboutRepoBox != null && aboutRepoBox.Visibility == Visibility.Collapsed,
            "切到关于后更新源标签与输入框保持隐藏");

        var settingsBackBtn = win.FindName("BtnSettingsBack") as Button;
        AssertTrue(settingsBackBtn != null, "设置页存在返回按钮");
        settingsBackBtn!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        win.UpdateLayout();
        var opsPage = win.FindName("PageOps") as FrameworkElement;
        AssertTrue(opsPage != null && opsPage.Visibility == Visibility.Visible
            && settingsPage!.Visibility == Visibility.Collapsed
            && ReferenceEquals(topSettingsBtn?.Style, idleSettingsStyle),
            "从设置返回上一主页面，并取消顶栏设置高亮");

        win.Close();
        AssertTrue(Directory.GetFiles(shotsDir, "*.png").Length == 7, "已离屏渲染生成标准尺寸与最小尺寸共 7 张功能页高清截图");

        // 4.7 跨工程隔离、配额识别、职责勾选白名单与子项目会话精准寻址回归断言（48-51）
        var nxIsolationProj = new ProjectProfile
        {
            Id = "proj_nx_isolation_test",
            Name = "NX (隔离测试)",
            PrimaryRoot = Path.Combine(outDir, "nx_isolation_dir")
        };
        var isoMap = new Dictionary<string, LiveAgentWorkState>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{testProj.Id}::cursor"] = new LiveAgentWorkState
            {
                AgentId = "cursor",
                AgentName = "Cursor",
                ProjectId = testProj.Id,
                ProjectRoot = testProj.PrimaryRoot,
                ProjectName = testProj.Name,
                Status = "error",
                WorkflowPhase = "error",
                LastFailureBadge = "超时",
                TaskSummary = "XX旧异常",
                UpdatedUtcTicks = DateTime.UtcNow.Ticks
            }
        };
        RegistryService.WriteJsonAtomic(AutoDispatchService.LiveStateMapPath, isoMap);
        var nxLiveStates = AutoDispatchService.ReadLiveWorkStatesForProject(nxIsolationProj);
        var xxLiveStates = AutoDispatchService.ReadLiveWorkStatesForProject(testProj);
        AutoDispatchService.ClearAgentLiveState("cursor", testProj);
        var xxAfterClear = AutoDispatchService.ReadLiveWorkStatesForProject(testProj);
        AssertTrue(
            !nxLiveStates.ContainsKey("cursor") && xxLiveStates.ContainsKey("cursor") && !xxAfterClear.ContainsKey("cursor"),
            "跨工程实时状态严格隔离（切换 NX 工程零串台显示 XX 旧报错）且单卡片复位精准清除");

        var bannerErr = "OpenAI Codex v0.167.0-alpha.4.1\n--------\nworkdir: C:\\Project\\Godot\\XX\nmodel: gpt-6-luna\nprovider: openai\n--------\n{\"error\":{\"message\":\"You’ve hit your usage limit. Upgrade to Plus\",\"codex_error_info\":\"usage_limit_exceeded\"}}";
        var extractedCodexErr = AutoDispatchService.ExtractMeaningfulCodexError(bannerErr, testProj.PrimaryRoot, 1);
        var quotaCls = NetworkResilienceService.ClassifyFailure(1, "", extractedCodexErr, null, true, true);
        AssertTrue(
            extractedCodexErr.Contains("usage_limit_exceeded") && !extractedCodexErr.StartsWith("OpenAI Codex") &&
            quotaCls.FailureKind == "quota_exceeded" && !quotaCls.IsRetryable,
            $"Codex 长 Banner 精确剥离提取配额耗尽错误并禁用无效断网误报 (Kind={quotaCls.FailureKind}, Retry={quotaCls.IsRetryable})");

        var grokRoleTest = reg.UserConfig.Agents.FirstOrDefault(a => a.Id == "grok_bot");
        var cursorRoleTest = reg.UserConfig.Agents.FirstOrDefault(a => a.Id == "cursor");
        if (grokRoleTest != null && cursorRoleTest != null)
        {
            var bakGrokRoles = grokRoleTest.PrimaryWorkRoles.ToList();
            var bakGrokProfiles = grokRoleTest.ModelProfiles.ToList();
            var bakGrokEn = grokRoleTest.Enabled;
            var bakCurRoles = cursorRoleTest.PrimaryWorkRoles.ToList();
            var bakCurProfiles = cursorRoleTest.ModelProfiles.ToList();
            var bakCurEn = cursorRoleTest.Enabled;

            grokRoleTest.Enabled = true;
            grokRoleTest.Detected = true;
            grokRoleTest.PrimaryWorkRoles = new List<string> { "text_writing", "image_generation" };
            grokRoleTest.ModelProfiles = new List<ModelWorkProfile>
            {
                new() { ModelName = LocalModelCatalogService.ManagedDefaultModelDisplay, PrimaryRoles = new List<string> { "text_writing", "image_generation" } }
            };
            cursorRoleTest.Enabled = true;
            cursorRoleTest.Detected = true;
            cursorRoleTest.PrimaryWorkRoles = new List<string> { "deep_debug", "fast_execution" };
            cursorRoleTest.ModelProfiles = new List<ModelWorkProfile>
            {
                new() { ModelName = "Grok 4.7 (grok-4.7)", PrimaryRoles = new List<string> { "deep_debug", "fast_execution" } }
            };

            var rolePool = new List<AgentRosterEntry> { grokRoleTest, cursorRoleTest };
            var reviewAuto = RoutingService.AnalyzeAndSelectAgentForTask(reg, testProj, "【并行验收旁路】主脑已直接落地 build.py 改动，请旁路复核改动与编译结果，勿重复大改", "auto", rolePool);
            var reviewForced = RoutingService.AnalyzeAndSelectAgentForTask(reg, testProj, "请旁路验收 build.py 编译结果", "grok_bot", rolePool);
            var codeAuto = RoutingService.AnalyzeAndSelectAgentForTask(reg, testProj, "请直接落地修改工程文件 build.py", "auto", rolePool);
            var copyAuto = RoutingService.AnalyzeAndSelectAgentForTask(reg, testProj, "请撰写一段世界观剧情文案策划", "auto", rolePool);

            grokRoleTest.Enabled = bakGrokEn;
            grokRoleTest.PrimaryWorkRoles = bakGrokRoles;
            grokRoleTest.ModelProfiles = bakGrokProfiles;
            cursorRoleTest.Enabled = bakCurEn;
            cursorRoleTest.PrimaryWorkRoles = bakCurRoles;
            cursorRoleTest.ModelProfiles = bakCurProfiles;

            AssertTrue(
                !RoutingService.IsAgentEligibleForWorkType(grokRoleTest, "deep_debug") &&
                reviewAuto.TargetAgent?.Id == "cursor" &&
                reviewForced.TargetAgent?.Id == "cursor" &&
                codeAuto.TargetAgent?.Id == "cursor" &&
                copyAuto.TargetAgent?.Id == "grok_bot",
                $"严守工作职责勾选白名单（未勾选验收/代码的 Grok 拦截改派 Cursor，文案策划正常命中 Grok：Auto={reviewAuto.TargetAgent?.Id}, Forced={reviewForced.TargetAgent?.Id}, Copy={copyAuto.TargetAgent?.Id})");
        }

        var umbrellaRoot = Path.Combine(outDir, "nx_umbrella");
        Directory.CreateDirectory(Path.Combine(umbrellaRoot, "北大肿瘤医院"));
        Directory.CreateDirectory(Path.Combine(umbrellaRoot, "TBH"));
        Directory.CreateDirectory(Path.Combine(umbrellaRoot, "兵器时代5"));
        var umbrellaProj = new ProjectProfile
        {
            Id = "proj_nx_umb_test",
            Name = "NX (Test)",
            PrimaryRoot = umbrellaRoot,
            CollabRelDir = "work/collab"
        };
        var mockUmbrellaSessions = new List<ExternalDialogSessionInfo>
        {
            new() { SessionId = "sid_tbh", AgentId = "cursor", Title = "TBH Trainer project analysis", ProjectDir = umbrellaRoot, UpdatedAt = "2026-11-17 16:00:00" },
            new() { SessionId = "sid_hosp", AgentId = "cursor", Title = "北大肿瘤医院项目对话框", ProjectDir = umbrellaRoot, UpdatedAt = "2026-11-17 15:00:00" }
        };
        sessionSvc.SaveActiveSessionForAgent(umbrellaProj, "cursor", "sid_tbh");
        var matchedHosp = sessionSvc.FindBestMatchingSession(umbrellaProj, "cursor", "auto", mockUmbrellaSessions, "请直接修改 NX\\北大肿瘤医院 WishStore.java");
        var matchedNewSub = sessionSvc.FindBestMatchingSession(umbrellaProj, "cursor", "auto", mockUmbrellaSessions, "请优化 兵器时代5 核心脚本");
        AssertTrue(
            matchedHosp?.SessionId == "sid_hosp" && matchedNewSub == null,
            $"多子项目对话框按子项目名与关键词精准寻址（命中北大={matchedHosp?.Title}, 全新子项目返回空触发新建={matchedNewSub == null}）");

        // 4.8 全维 12 项能力矩阵识别、多媒体独立隔离与工程子类平滑回退断言（52-53）
        var wtVideo = RoutingService.InferWorkTypeFromTask("请基于剧本生成一段宣传片视频与镜头运镜分镜");
        var wtAudio = RoutingService.InferWorkTypeFromTask("请生成击剑打击音效 SFX 与主界面 BGM 背景音乐");
        var wt3d = RoutingService.InferWorkTypeFromTask("请用 Blender 调整 3D模型 骨骼蒙皮权重并导出 .glb");
        var wtVfx = RoutingService.InferWorkTypeFromTask("请优化石板 qinglan_ground.gdshader 着色器高光特效");
        AssertTrue(
            WorkSpecialtyCatalog.OrderedItems.Count == 12 &&
            reg.Tags.Tags.Count >= 12 &&
            wtVideo == "video_animation" &&
            wtAudio == "audio_sfx_bgm" &&
            wt3d == "model_3d_motion" &&
            wtVfx == "ui_shader_vfx",
            $"全维 12 项能力目录与多模态语义识别就绪 (能力数={WorkSpecialtyCatalog.OrderedItems.Count}, 标签数={reg.Tags.Tags.Count}, video={wtVideo}, audio={wtAudio}, 3d={wt3d}, vfx={wtVfx})");

        if (grokRoleTest != null && cursorRoleTest != null)
        {
            var bakGEn = grokRoleTest.Enabled;
            var bakGR = grokRoleTest.PrimaryWorkRoles.ToList();
            var bakGP = grokRoleTest.ModelProfiles.ToList();
            var bakCEn = cursorRoleTest.Enabled;
            var bakCR = cursorRoleTest.PrimaryWorkRoles.ToList();
            var bakCP = cursorRoleTest.ModelProfiles.ToList();

            grokRoleTest.Enabled = true;
            grokRoleTest.Detected = true;
            grokRoleTest.PrimaryWorkRoles = new List<string> { "text_writing", "image_generation", "video_animation" };
            grokRoleTest.ModelProfiles = new List<ModelWorkProfile>
            {
                new() { ModelName = LocalModelCatalogService.ManagedDefaultModelDisplay, PrimaryRoles = new List<string> { "text_writing", "image_generation", "video_animation" } }
            };
            cursorRoleTest.Enabled = true;
            cursorRoleTest.Detected = true;
            cursorRoleTest.PrimaryWorkRoles = new List<string> { "fast_execution", "deep_debug" };
            cursorRoleTest.ModelProfiles = new List<ModelWorkProfile>
            {
                new() { ModelName = "Grok 4.7 (grok-4.7)", PrimaryRoles = new List<string> { "fast_execution", "deep_debug" } }
            };

            var pool12 = new List<AgentRosterEntry> { grokRoleTest, cursorRoleTest };
            var routeVid = RoutingService.AnalyzeAndSelectAgentForTask(reg, testProj, "请制作一段宣传片短视频分镜", "auto", pool12);
            var routeAudioBlocked = RoutingService.AnalyzeAndSelectAgentForTask(reg, testProj, "请合成一段战斗击打音效与BGM配乐", "auto", pool12);
            var routeVfxFallback = RoutingService.AnalyzeAndSelectAgentForTask(reg, testProj, "请优化场景着色器 shader 视觉粒子特效", "auto", pool12);

            grokRoleTest.Enabled = bakGEn;
            grokRoleTest.PrimaryWorkRoles = bakGR;
            grokRoleTest.ModelProfiles = bakGP;
            cursorRoleTest.Enabled = bakCEn;
            cursorRoleTest.PrimaryWorkRoles = bakCR;
            cursorRoleTest.ModelProfiles = bakCP;

            AssertTrue(
                routeVid.TargetAgent?.Id == "grok_bot" &&
                routeAudioBlocked.TargetAgent == null &&
                routeVfxFallback.TargetAgent?.Id == "cursor",
                $"多模态独立白名单与工程子类兼容生效（视频->Grok={routeVid.TargetAgent?.Id}, 无人勾音效->拦截交回主脑={routeAudioBlocked.TargetAgent == null}, UI特效兼容->Cursor={routeVfxFallback.TargetAgent?.Id}）");
        }

        // 4.9 云端多套全参数配置方案、防封号安全锁、用户多套自定义方案持久化及全局多流派同步断言（54-56）
        var cursorPresetTarget = reg.UserConfig.Agents.FirstOrDefault(a => a.Id == "cursor");
        var grokPresetTarget = reg.UserConfig.Agents.FirstOrDefault(a => a.Id == "grok_bot");
        var codexPresetTarget = reg.UserConfig.Agents.FirstOrDefault(a => a.Id == "codex_cli");
        if (cursorPresetTarget != null && grokPresetTarget != null)
        {
            var cursorCloudPresets = CloudRecommendationService.BuildCloudPresetsForAgent(reg, cursorPresetTarget, refreshLocalModels: false);
            var grokCloudPresets = CloudRecommendationService.BuildCloudPresetsForAgent(reg, grokPresetTarget, refreshLocalModels: false);

            var cDeep = cursorCloudPresets.FirstOrDefault(p => p.PresetId == "cloud_deep_arch");
            var cMulti = cursorCloudPresets.FirstOrDefault(p => p.PresetId == "cloud_multimedia");
            var cSafe = cursorCloudPresets.FirstOrDefault(p => p.PresetId == "cloud_safe_audit");
            bool grokLockedAllPresets = grokCloudPresets.Count >= 5 && grokCloudPresets.All(p =>
                p.ModelProfiles.Count == 1 &&
                p.ModelProfiles[0].ModelName == LocalModelCatalogService.ManagedDefaultModelDisplay);

            AssertTrue(
                cursorCloudPresets.Count >= 5 &&
                cDeep != null && cMulti != null && cSafe != null &&
                cDeep.RuntimeParams.Temperature < cMulti.RuntimeParams.Temperature &&
                cDeep.RuntimeParams.ReasoningEffort == "high" &&
                cSafe.Permissions.AllowDirectSourceWrite == false &&
                grokLockedAllPresets,
                $"云端生成多套差异化全参数方案且严守 Grok 防风控锁（Cursor方案={cursorCloudPresets.Count}, DeepTemp={cDeep?.RuntimeParams.Temperature:0.00}<MultiTemp={cMulti?.RuntimeParams.Temperature:0.00}, SafeWrite={cSafe?.Permissions.AllowDirectSourceWrite}, GrokLocked={grokLockedAllPresets})");

            // 断言 55：用户多套自定义方案完整保存、磁盘持久化重载、无损切换与删除
            var bakCursorMode = cursorPresetTarget.ModelSelectionMode;
            var bakCursorPresetId = cursorPresetTarget.ActivePresetId;
            var bakCursorRuntime = cursorPresetTarget.RuntimeParams.Clone();
            var bakCursorPerms = cursorPresetTarget.Permissions.Clone();
            var bakCursorProfiles = cursorPresetTarget.ModelProfiles.Select(p => p.Clone()).ToList();
            var bakCursorTags = new Dictionary<string, int>(cursorPresetTarget.TagScores);
            var bakCursorCustomPresets = cursorPresetTarget.CustomPresets.Select(p => p.Clone()).ToList();

            cursorPresetTarget.RuntimeParams = new AgentRuntimeParams
            {
                Temperature = 0.12,
                ReasoningEffort = "xhigh",
                ContextBudget = "max",
                MaxOutputTokens = 32768,
                TimeoutSeconds = 420,
                MaxRetries = 5,
                ExtraRunnerArgs = "--verbose-test"
            };
            cursorPresetTarget.Permissions.AllowDirectSourceWrite = true;
            if (cursorPresetTarget.ModelProfiles.Count > 0)
            {
                cursorPresetTarget.ModelProfiles[0].CustomTemperature = 0.08;
            }
            var userP1 = CloudRecommendationService.CaptureCurrentAsUserPreset(cursorPresetTarget, "严谨排障自定义");

            cursorPresetTarget.RuntimeParams = new AgentRuntimeParams
            {
                Temperature = 0.88,
                ReasoningEffort = "low",
                ContextBudget = "compact",
                MaxOutputTokens = 8192,
                TimeoutSeconds = 95,
                MaxRetries = 1,
                ExtraRunnerArgs = "--fast-creative"
            };
            cursorPresetTarget.Permissions.AllowDirectSourceWrite = false;
            var userP2 = CloudRecommendationService.CaptureCurrentAsUserPreset(cursorPresetTarget, "创意极速自定义");
            reg.SaveUserConfig();

            var reloadReg = new RegistryService();
            reloadReg.LoadAll();
            var reloadedCursor = reloadReg.UserConfig.Agents.First(a => a.Id == "cursor");
            int loadedPresetCount = reloadedCursor.CustomPresets.Count;
            var restoredP1 = reloadedCursor.CustomPresets.FirstOrDefault(p => p.PresetId == userP1.PresetId);
            var restoredP2 = reloadedCursor.CustomPresets.FirstOrDefault(p => p.PresetId == userP2.PresetId);
            if (restoredP1 != null) CloudRecommendationService.ApplyPresetToAgent(reloadedCursor, restoredP1);
            bool p1RestoredOk = Math.Abs(reloadedCursor.RuntimeParams.Temperature - 0.12) < 0.001 &&
                                reloadedCursor.RuntimeParams.ReasoningEffort == "xhigh" &&
                                reloadedCursor.RuntimeParams.TimeoutSeconds == 420 &&
                                reloadedCursor.RuntimeParams.ExtraRunnerArgs == "--verbose-test" &&
                                (reloadedCursor.ModelProfiles.FirstOrDefault()?.CustomTemperature ?? 0) == 0.08;
            if (restoredP2 != null) CloudRecommendationService.ApplyPresetToAgent(reloadedCursor, restoredP2);
            bool p2RestoredOk = Math.Abs(reloadedCursor.RuntimeParams.Temperature - 0.88) < 0.001 &&
                                reloadedCursor.Permissions.AllowDirectSourceWrite == false;
            bool delOk = CloudRecommendationService.DeleteUserPreset(reloadedCursor, userP2.PresetId);
            int countAfterDel = reloadedCursor.CustomPresets.Count;

            // 恢复自检现场
            cursorPresetTarget.ModelSelectionMode = bakCursorMode;
            cursorPresetTarget.ActivePresetId = bakCursorPresetId;
            cursorPresetTarget.RuntimeParams = bakCursorRuntime;
            cursorPresetTarget.Permissions = bakCursorPerms;
            cursorPresetTarget.ModelProfiles = bakCursorProfiles;
            cursorPresetTarget.TagScores = bakCursorTags;
            cursorPresetTarget.CustomPresets = bakCursorCustomPresets;
            reg.SaveUserConfig();

            AssertTrue(
                loadedPresetCount >= 2 && countAfterDel == loadedPresetCount - 1 && p1RestoredOk && p2RestoredOk && delOk,
                $"用户多套自定义全参数方案保存、持久化重载、无损切换与删除全通 (Loaded={loadedPresetCount}, AfterDel={countAfterDel}, P1={p1RestoredOk}, P2={p2RestoredOk}, Del={delOk})");
        }

        // 断言 56：全局多套云端团队阵容流派切换与未启用成员状态锁定校验
        var globalFleets = CloudRecommendationService.GetGlobalFleetPresets();
        bool codexOrigEnabled = codexPresetTarget?.Enabled ?? false;
        if (codexPresetTarget != null) codexPresetTarget.Enabled = false;
        var safeReport = cloudSvc.BuildAndApplyReportFromCache(reg, applyToAgents: true, fleetPresetId: "fleet_safe_review");
        bool safeWriteDuringAudit = grokPresetTarget?.Permissions.AllowDirectSourceWrite ?? true;
        bool safeAuditApplied = grokPresetTarget != null &&
                                grokPresetTarget.ActivePresetId == "cloud_safe_audit" &&
                                !safeWriteDuringAudit &&
                                (codexPresetTarget == null || codexPresetTarget.Enabled == false);
        var balancedReport = cloudSvc.BuildAndApplyReportFromCache(reg, applyToAgents: true, fleetPresetId: "fleet_balanced");
        if (codexPresetTarget != null) codexPresetTarget.Enabled = codexOrigEnabled;
        reg.SaveUserConfig();
        AssertTrue(
            globalFleets.Count >= 4 && safeReport.Items.Count >= 4 && balancedReport.Items.Count >= 4 && safeAuditApplied,
            $"全局 4 套云端协同流派切换生效且严守成员启停开关不变 (流派数={globalFleets.Count}, SafeAuditWrite={safeWriteDuringAudit}, CodexEnabled={codexPresetTarget?.Enabled})");

        // 4.10 零键鼠抢占默认配置、目录名“音效”防误判、卡死看门狗分类与用户自主选择解决方式断言（57-59）
        var wtShaderInAudioFolder = RoutingService.InferWorkTypeFromTask(
            "修改 c:/Project/Godot/XX/assets/shaders/废院石坪_weathered_paving.gdshader 与 scripts/05_地图环境与音效/paving_mesh_tune.gd，提升石面温润油亮包浆高光");
        var wtGdInAudioFolder = RoutingService.InferWorkTypeFromTask(
            "请修复 scripts/05_地图环境与音效/paving_mesh_tune.gd 中的节点初始化顺序");
        AssertTrue(
            wtShaderInAudioFolder == "ui_shader_vfx" && wtGdInAudioFolder == "fast_execution",
            $"文件路径含『地图环境与音效』目录名时精准剥离防误判 (gdshader+gd={wtShaderInAudioFolder}, 纯gd={wtGdInAudioFolder})");

        // 代码修复单含「音效」业务词 ≠ 多媒体资产生成；真实 BGM 单仍归 audio_sfx_bgm
        const string soundFxCodeReworkTask =
            "【主脑验收打回重修·直接落地改 C# 源码】修复 SoundFxService.cs 音效开关关了还发声并运行 dotnet build";
        var wtSoundFxCode = RoutingService.InferWorkTypeFromTask(soundFxCodeReworkTask);
        var wtRealAudioAsset = RoutingService.InferWorkTypeFromTask("请为游戏制作打击音效与战斗背景音乐 BGM");
        AssertTrue(
            wtSoundFxCode != "audio_sfx_bgm" &&
            (wtSoundFxCode == "deep_debug" || wtSoundFxCode == "fast_execution" || wtSoundFxCode == "build_testing") &&
            wtRealAudioAsset == "audio_sfx_bgm" &&
            !RoutingService.HasExplicitMediaCreationCue(soundFxCodeReworkTask) &&
            RoutingService.HasExplicitMediaCreationCue("请为游戏制作打击音效与战斗背景音乐 BGM"),
            $"代码音效修复≠音频资产 (code={wtSoundFxCode}, realAudio={wtRealAudioAsset})");

        if (grokRoleTest != null && cursorRoleTest != null)
        {
            var bakGEn2 = grokRoleTest.Enabled;
            var bakGR2 = grokRoleTest.PrimaryWorkRoles.ToList();
            var bakGP2 = grokRoleTest.ModelProfiles.ToList();
            var bakCEn2 = cursorRoleTest.Enabled;
            var bakCR2 = cursorRoleTest.PrimaryWorkRoles.ToList();
            var bakCP2 = cursorRoleTest.ModelProfiles.ToList();
            try
            {
                grokRoleTest.Enabled = true;
                grokRoleTest.Detected = true;
                grokRoleTest.PrimaryWorkRoles = new List<string> { "audio_sfx_bgm", "video_animation", "image_generation", "text_writing" };
                grokRoleTest.ModelProfiles = new List<ModelWorkProfile>
                {
                    new() { ModelName = LocalModelCatalogService.ManagedDefaultModelDisplay, PrimaryRoles = new List<string> { "audio_sfx_bgm", "video_animation", "image_generation", "text_writing" } }
                };
                cursorRoleTest.Enabled = true;
                cursorRoleTest.Detected = true;
                cursorRoleTest.PrimaryWorkRoles = new List<string> { "fast_execution", "deep_debug", "build_testing" };
                cursorRoleTest.ModelProfiles = new List<ModelWorkProfile>
                {
                    new() { ModelName = "Grok 4.7 (grok-4.7)", PrimaryRoles = new List<string> { "fast_execution", "deep_debug", "build_testing" } }
                };
                var poolSoundFx = new List<AgentRosterEntry> { grokRoleTest, cursorRoleTest };
                var routeSoundFxForced = RoutingService.AnalyzeAndSelectAgentForTask(
                    reg, testProj, soundFxCodeReworkTask, "cursor", poolSoundFx);
                var routeRealAudio = RoutingService.AnalyzeAndSelectAgentForTask(
                    reg, testProj, "请为游戏制作打击音效与战斗背景音乐 BGM", "auto", poolSoundFx);
                AssertTrue(
                    routeSoundFxForced.TargetAgent?.Id == "cursor" &&
                    routeSoundFxForced.WorkTypeId != "audio_sfx_bgm" &&
                    routeRealAudio.TargetAgent?.Id == "grok_bot" &&
                    routeRealAudio.WorkTypeId == "audio_sfx_bgm",
                    $"SoundFx 代码重修稳命中 Cursor 不改派 Grok；真实音频→Grok (forced={routeSoundFxForced.TargetAgent?.Id}/{routeSoundFxForced.WorkTypeId}, audio={routeRealAudio.TargetAgent?.Id}/{routeRealAudio.WorkTypeId})");
            }
            finally
            {
                grokRoleTest.Enabled = bakGEn2;
                grokRoleTest.PrimaryWorkRoles = bakGR2;
                grokRoleTest.ModelProfiles = bakGP2;
                cursorRoleTest.Enabled = bakCEn2;
                cursorRoleTest.PrimaryWorkRoles = bakCR2;
                cursorRoleTest.ModelProfiles = bakCP2;
            }
        }

        // 飞行胶囊：具体任务名 + 窄窗截断 + 空任务回退
        {
            const string flightTask = "全面升级云桥台跨设备访问";
            string flightFull = FloatingOverlayWindow.FormatFlightBadgeText("dispatch", flightTask, ultraNarrow: false);
            string flightUltra = FloatingOverlayWindow.FormatFlightBadgeText("dispatch", flightTask, ultraNarrow: true);
            string flightEmpty = FloatingOverlayWindow.FormatFlightBadgeText("dispatch", null);
            string flightBlank = FloatingOverlayWindow.FormatFlightBadgeText("dispatch", "   ");
            static string BadgeBody(string s)
            {
                int i = s.IndexOf(' ');
                return i < 0 ? s : s[(i + 1)..];
            }
            string fullBody = BadgeBody(flightFull);
            string ultraBody = BadgeBody(flightUltra);
            AssertTrue(
                flightFull.Contains("云桥", StringComparison.Ordinal) &&
                flightUltra.Contains("云桥", StringComparison.Ordinal) &&
                flightFull.StartsWith("📋", StringComparison.Ordinal) &&
                ultraBody.Length is >= 6 and <= 9 &&
                ultraBody.EndsWith("…", StringComparison.Ordinal) &&
                fullBody.Length is > 9 and <= 13 &&
                fullBody.Length > ultraBody.Length &&
                flightEmpty == "📋 任务单" &&
                flightBlank == "📋 任务单",
                $"飞行胶囊任务名截断与空回退 (full={flightFull}, ultra={flightUltra}, empty={flightEmpty})");
            double durShort = FloatingOverlayWindow.ComputeAdaptiveFlightDurationMs(36, compactMode: true);
            double durLong = FloatingOverlayWindow.ComputeAdaptiveFlightDurationMs(280, compactMode: false);
            AssertTrue(
                durShort >= 680 && durShort <= 900 &&
                durLong >= 1000 && durLong <= 1280 &&
                durLong > durShort,
                $"飞行耗时随距离自适应 (short={durShort:F0}, long={durLong:F0})");
        }

        // 悬浮窗实时同步：探测缓存 TTL、忙碌点亮与调度进程死后的滞留回落
        {
            int ttlMs = AutoDispatchService.DesktopLiveProbeCacheTtlMs;
            AssertTrue(
                ttlMs > 0 && ttlMs <= 2000 &&
                FloatingOverlayWindow.ResolveOverlayFallbackPollMs(0) == 1000 &&
                FloatingOverlayWindow.ResolveOverlayFallbackPollMs(1500) == 1000 &&
                FloatingOverlayWindow.ResolveOverlayFallbackPollMs(800) == 800 &&
                new UserHubConfig().FloatingRefreshIntervalMs == 1000,
                $"实时同步 TTL/轮询兜底 (ttl={ttlMs}, defaultPoll={new UserHubConfig().FloatingRefreshIntervalMs})");

            var lit = AutoDispatchService.ResolveBidirectionalDesktopMerge("idle", "idle", 0, false, true, true);
            var stale = AutoDispatchService.ResolveBidirectionalDesktopMerge("working", "executing", 424242, false, true, false);
            var reworkStale = AutoDispatchService.ResolveBidirectionalDesktopMerge("reworking", "reworking", 0, false, true, false);
            var keepAlive = AutoDispatchService.ResolveBidirectionalDesktopMerge("working", "executing", 424242, true, true, false);
            var stopSnap = new AutoDispatchService.SidebarBusyWindowSnapshot
            {
                HasStopGeneration = true,
                HasSidebarGroupStructure = false
            };
            var idleSnap = new AutoDispatchService.SidebarBusyWindowSnapshot();
            var stopHit = AutoDispatchService.EvaluateGlobalDesktopBusy(stopSnap);
            var idleHit = AutoDispatchService.EvaluateGlobalDesktopBusy(idleSnap);
            AssertTrue(
                lit.Status == "working" && lit.WorkflowPhase == "executing" && lit.IsDesktopLiveOverlay &&
                stale.Status is "done" or "idle" && stale.Status != "working" &&
                reworkStale.Status is "done" or "idle" && reworkStale.Status != "reworking" &&
                keepAlive.Status == "working" &&
                stopHit.IsWorking && !idleHit.IsWorking,
                $"忙碌/空闲双向合并 (lit={lit.Status}/{lit.WorkflowPhase}, stale={stale.Status}, rework={reworkStale.Status}, keep={keepAlive.Status}, stop={stopHit.IsWorking})");

            var debounceT0 = new DateTime(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc);
            var debounceUp = AutoDispatchService.ResolveDebouncedDesktopBusyTransition(
                false, 2, debounceT0, true, false, true, debounceT0.AddSeconds(1));
            AssertTrue(
                debounceUp.StableIsWorking && debounceUp.NextConsecutiveIdleCount == 0 &&
                debounceUp.NextIdleStreakStartUtc == null && debounceUp.StateChanged,
                "上行即时切忙：空闲收到 working 立即为 true 并清零空闲计数");

            var debounceHold = AutoDispatchService.ResolveDebouncedDesktopBusyTransition(
                true, 1, debounceT0, false, true, true, debounceT0.AddSeconds(2));
            AssertTrue(
                debounceHold.StableIsWorking && debounceHold.NextConsecutiveIdleCount == 1 &&
                debounceHold.NextIdleStreakStartUtc == debounceT0 && !debounceHold.StateChanged,
                "超时或异常不确定态保态：保持 working 且不推进空闲计数");

            var debounceIdle1 = AutoDispatchService.ResolveDebouncedDesktopBusyTransition(
                true, 0, null, false, false, true, debounceT0);
            var debounceIdle2 = AutoDispatchService.ResolveDebouncedDesktopBusyTransition(
                debounceIdle1.StableIsWorking, debounceIdle1.NextConsecutiveIdleCount, debounceIdle1.NextIdleStreakStartUtc,
                false, false, true, debounceT0.AddMilliseconds(1300));
            var debounceIdleShort = AutoDispatchService.ResolveDebouncedDesktopBusyTransition(
                debounceIdle2.StableIsWorking, debounceIdle2.NextConsecutiveIdleCount, debounceIdle2.NextIdleStreakStartUtc,
                false, false, true, debounceT0.AddMilliseconds(3499));
            AssertTrue(
                debounceIdle1.StableIsWorking && debounceIdle1.NextConsecutiveIdleCount == 1 && !debounceIdle1.StateChanged &&
                debounceIdle2.StableIsWorking && debounceIdle2.NextConsecutiveIdleCount == 2 && !debounceIdle2.StateChanged &&
                debounceIdleShort.StableIsWorking && debounceIdleShort.NextConsecutiveIdleCount == 3 && !debounceIdleShort.StateChanged,
                "短暂可靠空闲防抖：1~2 次或连续 3 次但不足 3500ms 仍保持 working");

            var debounceIdleLong = AutoDispatchService.ResolveDebouncedDesktopBusyTransition(
                debounceIdle2.StableIsWorking, debounceIdle2.NextConsecutiveIdleCount, debounceIdle2.NextIdleStreakStartUtc,
                false, false, true, debounceT0.AddMilliseconds(3500));
            var debounceAlreadyIdle = AutoDispatchService.ResolveDebouncedDesktopBusyTransition(
                false, 4, debounceT0, false, false, true, debounceT0.AddSeconds(8));
            AssertTrue(
                !debounceIdleLong.StableIsWorking && debounceIdleLong.NextConsecutiveIdleCount == 0 &&
                debounceIdleLong.NextIdleStreakStartUtc == null && debounceIdleLong.StateChanged &&
                !debounceAlreadyIdle.StableIsWorking && debounceAlreadyIdle.NextConsecutiveIdleCount == 0 &&
                debounceAlreadyIdle.NextIdleStreakStartUtc == null && !debounceAlreadyIdle.StateChanged,
                "连续 >=3 次可靠空闲且跨度 >=3500ms 回落为空闲；本来空闲则保持 false 并清零");

            var debounceExited = AutoDispatchService.ResolveDebouncedDesktopBusyTransition(
                true, 2, debounceT0, false, false, false, debounceT0.AddSeconds(1));
            AssertTrue(
                !debounceExited.StableIsWorking && debounceExited.NextConsecutiveIdleCount == 0 &&
                debounceExited.NextIdleStreakStartUtc == null && debounceExited.StateChanged,
                "进程退出（ProcessFound=false）立即回落为空闲");

            string overlaySrc = @"C:\Project\Godot\配套工具\tools\ai-collab-hub\src\UI\FloatingOverlayWindow.xaml.cs";
            string overlayTxt = File.Exists(overlaySrc) ? File.ReadAllText(overlaySrc, Encoding.UTF8) : "";
            AssertTrue(
                overlayTxt.Contains("FileSystemWatcher", StringComparison.Ordinal) &&
                overlayTxt.Contains("LiveStateMapPath", StringComparison.Ordinal) &&
                overlayTxt.Contains("DisposeLiveSyncHooks", StringComparison.Ordinal) &&
                overlayTxt.Contains("ShutdownForAppExit", StringComparison.Ordinal) &&
                overlayTxt.Contains("RefreshAllDesktopAgentsBusySilent", StringComparison.Ordinal),
                $"FloatingOverlay 源码含 FileSystemWatcher 监听 LiveStateMapPath 且 Closed/Shutdown 释放 (exists={File.Exists(overlaySrc)})");

            var wtOggScript = RoutingService.InferWorkTypeFromTask(
                "排查并修改 scripts/soundscape.gd，BGM 路径 res://audio/bgm/theme.ogg 与图片 icons/bgm.png 无声，不要新建音频文件");
            AssertTrue(
                wtOggScript != "audio_sfx_bgm" && wtOggScript != "image_generation",
                $"带 ogg/png 路径的脚本排查仍走代码通道 (type={wtOggScript})");
        }

        // 工程脚本排查含 BGM 等业务词时禁止误派 Grok Bot，优先直改代码的 Cursor
        const string soundscapeShort = "修改 soundscape.gd 与 BGM 排查脚本";
        const string soundscapeLong = "排查 scripts/soundscape.gd 与 BGM 加载为什么不生效，并直接落地修改工程脚本";
        AssertTrue(
            RoutingService.IsEngineeringSourceEditTask(soundscapeShort) &&
            RoutingService.IsEngineeringSourceEditTask(soundscapeLong) &&
            !RoutingService.IsEngineeringSourceEditTask("请为游戏制作打击音效与战斗背景音乐 BGM") &&
            RoutingService.InferWorkTypeFromTask(soundscapeLong) != "audio_sfx_bgm" &&
            RoutingService.InferWorkTypeFromTask(soundscapeShort) != "audio_sfx_bgm",
            $"soundscape.gd 代码排查不归音频资产 (short={RoutingService.InferWorkTypeFromTask(soundscapeShort)}, long={RoutingService.InferWorkTypeFromTask(soundscapeLong)})");
        if (grokRoleTest != null && cursorRoleTest != null)
        {
            var bakGEn3 = grokRoleTest.Enabled;
            var bakGR3 = grokRoleTest.PrimaryWorkRoles.ToList();
            var bakGP3 = grokRoleTest.ModelProfiles.ToList();
            var bakGWrite = grokRoleTest.Permissions.AllowDirectSourceWrite;
            var bakCEn3 = cursorRoleTest.Enabled;
            var bakCR3 = cursorRoleTest.PrimaryWorkRoles.ToList();
            var bakCP3 = cursorRoleTest.ModelProfiles.ToList();
            var bakCWrite = cursorRoleTest.Permissions.AllowDirectSourceWrite;
            try
            {
                grokRoleTest.Enabled = true;
                grokRoleTest.Detected = true;
                grokRoleTest.Permissions.AllowDirectSourceWrite = false;
                grokRoleTest.PrimaryWorkRoles = new List<string> { "audio_sfx_bgm", "image_generation", "deep_debug", "fast_execution" };
                grokRoleTest.ModelProfiles = new List<ModelWorkProfile>
                {
                    new() { ModelName = LocalModelCatalogService.ManagedDefaultModelDisplay, PrimaryRoles = new List<string> { "audio_sfx_bgm", "image_generation", "deep_debug", "fast_execution" } }
                };
                cursorRoleTest.Enabled = true;
                cursorRoleTest.Detected = true;
                cursorRoleTest.Permissions.AllowDirectSourceWrite = true;
                cursorRoleTest.PrimaryWorkRoles = new List<string> { "text_writing" };
                cursorRoleTest.ModelProfiles = new List<ModelWorkProfile>
                {
                    new() { ModelName = "Cursor Code", PrimaryRoles = new List<string> { "text_writing" } }
                };
                var poolCode = new List<AgentRosterEntry> { grokRoleTest, cursorRoleTest };
                var routeShort = RoutingService.AnalyzeAndSelectAgentForTask(reg, testProj, soundscapeShort, "auto", poolCode);
                var routeLongForced = RoutingService.AnalyzeAndSelectAgentForTask(reg, testProj, soundscapeLong, "grok_bot", poolCode);
                AssertTrue(
                    routeShort.TargetAgent?.Id == "cursor" &&
                    routeLongForced.TargetAgent?.Id == "cursor" &&
                    routeShort.TargetAgent?.Id != "grok_bot" &&
                    routeLongForced.WorkTypeId != "audio_sfx_bgm",
                    $"含 BGM 的 soundscape.gd 排查必须给 Cursor 而不是 Grok Bot (auto={routeShort.TargetAgent?.Id}/{routeShort.WorkTypeId}, forced={routeLongForced.TargetAgent?.Id}/{routeLongForced.WorkTypeId})");
            }
            finally
            {
                grokRoleTest.Enabled = bakGEn3;
                grokRoleTest.PrimaryWorkRoles = bakGR3;
                grokRoleTest.ModelProfiles = bakGP3;
                grokRoleTest.Permissions.AllowDirectSourceWrite = bakGWrite;
                cursorRoleTest.Enabled = bakCEn3;
                cursorRoleTest.PrimaryWorkRoles = bakCR3;
                cursorRoleTest.ModelProfiles = bakCP3;
                cursorRoleTest.Permissions.AllowDirectSourceWrite = bakCWrite;
            }
        }

        // 同项目新派单成功交付后自动清除其他 Agent 陈旧 error/awaiting_user_choice
        {
            var siblingClearProj = new ProjectProfile
            {
                Id = "proj_selftest_sibling_clear",
                Name = "SiblingClearSelfTest",
                PrimaryRoot = testProj.PrimaryRoot
            };
            byte[]? bakLiveMap = File.Exists(AutoDispatchService.LiveStateMapPath)
                ? File.ReadAllBytes(AutoDispatchService.LiveStateMapPath) : null;
            try
            {
                var mapForClear = new Dictionary<string, LiveAgentWorkState>(StringComparer.OrdinalIgnoreCase)
                {
                    ["proj_selftest_sibling_clear::cursor"] = new LiveAgentWorkState
                    {
                        AgentId = "cursor",
                        AgentName = "Cursor",
                        ProjectId = siblingClearProj.Id,
                        ProjectRoot = siblingClearProj.PrimaryRoot,
                        ProjectName = siblingClearProj.Name,
                        Status = "done",
                        WorkflowPhase = "verified_delivered",
                        TaskSummary = "成功交付",
                        UpdatedUtcTicks = DateTime.UtcNow.Ticks
                    },
                    ["proj_selftest_sibling_clear::grok_bot"] = new LiveAgentWorkState
                    {
                        AgentId = "grok_bot",
                        AgentName = "Grok Bot",
                        ProjectId = siblingClearProj.Id,
                        ProjectRoot = siblingClearProj.PrimaryRoot,
                        ProjectName = siblingClearProj.Name,
                        Status = "error",
                        WorkflowPhase = "awaiting_user_choice",
                        LastFailureBadge = "CLI未登录",
                        TaskSummary = "陈旧误派",
                        UpdatedUtcTicks = DateTime.UtcNow.Ticks - TimeSpan.TicksPerMinute
                    }
                };
                RegistryService.WriteJsonAtomic(AutoDispatchService.LiveStateMapPath, mapForClear);
                int clearedN = AutoDispatchService.ClearSiblingStaleFailuresForProject(siblingClearProj, "cursor");
                var afterClear = AutoDispatchService.ReadLiveWorkStatesForProject(siblingClearProj);
                bool grokIdle = afterClear.TryGetValue("grok_bot", out var grokSt) &&
                                grokSt.Status == "idle" &&
                                grokSt.WorkflowPhase == "idle" &&
                                string.IsNullOrWhiteSpace(grokSt.LastFailureBadge);
                bool cursorKept = afterClear.TryGetValue("cursor", out var curSt) &&
                                  curSt.Status == "done" &&
                                  curSt.WorkflowPhase == "verified_delivered";
                AssertTrue(
                    clearedN >= 1 && grokIdle && cursorKept,
                    $"同项目成功交付后清理兄弟 Agent 陈旧异常 (cleared={clearedN}, grokIdle={grokIdle}, cursorKept={cursorKept})");
            }
            finally
            {
                if (bakLiveMap != null) File.WriteAllBytes(AutoDispatchService.LiveStateMapPath, bakLiveMap);
                else if (File.Exists(AutoDispatchService.LiveStateMapPath)) File.Delete(AutoDispatchService.LiveStateMapPath);
            }
        }

        var stallCls = NetworkResilienceService.ClassifyFailure(-2, "", "EXECUTION_STALLED: 子进程连续 90 秒无输出响应", null, true, true);
        var modalCls = NetworkResilienceService.ClassifyFailure(1, "MODAL_BLOCKED: Create New Bot", "", null, true, true);
        var loginCls = NetworkResilienceService.ClassifyFailure(1, "", "Error: Not signed in. Run 'grok login' in your terminal first.", null, true, true);
        var defaultRosterItem = new AgentRosterEntry();
        AssertTrue(
            !defaultRosterItem.LaunchWindowOnDispatch &&
            !string.IsNullOrWhiteSpace(reg.UserConfig.StallRecoveryPolicy) &&
            stallCls.FailureKind == "execution_stalled" && stallCls.IsRetryable &&
            modalCls.FailureKind == "execution_stalled" && modalCls.IsRetryable &&
            loginCls.FailureKind == "cli_not_signed_in" && !loginCls.IsRetryable,
            $"默认零焦点后台派单 + 卡死/弹窗阻塞与 CLI 未登录精准分级 (LaunchWin={defaultRosterItem.LaunchWindowOnDispatch}, Stall={stallCls.FailureKind}/{stallCls.IsRetryable}, Login={loginCls.FailureKind}/{loginCls.IsRetryable})");

        var stuckSampleAgent = grokPresetTarget ?? reg.UserConfig.Agents.First();
        NetworkResilienceService.SaveCheckpoint(testProj, new TaskChainCheckpoint
        {
            Status = "interrupted",
            ProjectId = testProj.Id,
            ProjectName = testProj.Name,
            ProjectRoot = testProj.PrimaryRoot,
            TargetAgentId = stuckSampleAgent.Id,
            TargetAgentName = stuckSampleAgent.Name,
            TaskInstruction = "测试卡住后由用户选择解决方式并移交主脑或取消复位",
            LastFailureKind = "execution_stalled",
            LastFailureLabel = "🛠 进程/弹窗卡住·待选解决方式"
        });
        var (stallDlgTest, _) = Dialogs.BuildStallResolutionDialogWindow(
            null, reg, testProj, stuckSampleAgent, null, NetworkResilienceService.LoadInterruptedCheckpoint(testProj));
        bool stallDlgBuilt = stallDlgTest.Content != null;
        stallDlgTest.Close();

        var handoverRes = autoDispatch.ExecuteUserChosenResolution(reg, testProj, bus, stuckSampleAgent, "handover_to_master");
        bool handoverOk = handoverRes.Success && File.Exists(handoverRes.OutputInboxFile) && NetworkResilienceService.LoadInterruptedCheckpoint(testProj) == null;
        var cancelRes = autoDispatch.ExecuteUserChosenResolution(reg, testProj, bus, stuckSampleAgent, "cancel_and_reset");
        var tipCard = FloatingOverlayWindow.BuildRichTooltipCard(
            stuckSampleAgent,
            new LiveAgentWorkState
            {
                AgentId = stuckSampleAgent.Id,
                AgentName = stuckSampleAgent.Name,
                Status = "done",
                SelectedModel = "Grok 4.7 (grok-4.7)",
                TaskSummary = "协助盘点并安全迁移资源到 E:\\项目资源汇总",
                LatestLogTail = "# 自动派单产出 - Cursor\n- 执行时间: 2026-11-17\n- **统筹与验收主脑**: `Antigravity`\n- 已完成迁移校验"
            },
            (true, 4932, "窗口在线 (PID 4932)"));
        AssertTrue(
            stallDlgBuilt && handoverOk && cancelRes.Success && tipCard.MaxWidth <= 420 && tipCard.Content is System.Windows.Controls.StackPanel,
            $"用户选择解决方式面板与深色结构化悬停卡片校验通过 (DialogBuilt={stallDlgBuilt}, HandoverOk={handoverOk}, TipMaxW={tipCard.MaxWidth})");

        // 4.11 跨工程/自动纳管工程工作路由全局同步 + 顶层职责与模型槽位严格对齐断言（60）
        testProj.WorkTypeRoutes["fast_execution"] = new WorkRouteRule { WorkTypeId = "fast_execution", TargetAgentId = "cursor", TargetModelName = "auto" };
        testProj.MasterPlannerId = "antigravity";
        reg.UserConfig.GlobalWorkTypeRoutes.Clear();
        var subAutoProjDir = Path.Combine(outDir, "auto_sub_hospital");
        Directory.CreateDirectory(subAutoProjDir);
        var subAutoProj = reg.EnsureOrSwitchProjectByDir(subAutoProjDir);
        var agyCheck = reg.UserConfig.Agents.FirstOrDefault(a => a.Id == "antigravity");
        bool roleAlignedOk = true;
        if (agyCheck != null)
        {
            var bakRoles = agyCheck.PrimaryWorkRoles.ToList();
            agyCheck.PrimaryWorkRoles = new List<string> { "brain_analysis", "visual_accept" };
            if (agyCheck.ModelProfiles.Count > 0 && !agyCheck.ModelProfiles[0].PrimaryRoles.Contains("text_writing"))
                agyCheck.ModelProfiles[0].PrimaryRoles.Add("text_writing");
            reg.SyncGlobalRoutesAndAgentRoleConsistency(persistIfChanged: false);
            roleAlignedOk = agyCheck.ModelProfiles.All(mp => !mp.PrimaryRoles.Contains("text_writing"));
            agyCheck.PrimaryWorkRoles = bakRoles;
            reg.SyncGlobalRoutesAndAgentRoleConsistency(persistIfChanged: false);
        }
        AssertTrue(
            subAutoProj.WorkTypeRoutes.ContainsKey("fast_execution") &&
            subAutoProj.WorkTypeRoutes["fast_execution"].TargetAgentId == "cursor" &&
            subAutoProj.MasterPlannerId == "antigravity" &&
            roleAlignedOk,
            $"自动纳管新工程完整同步全局工作路由且模型槽位严格对齐顶层职责 (SubRoute={subAutoProj.WorkTypeRoutes.GetValueOrDefault("fast_execution")?.TargetAgentId}, RoleAligned={roleAlignedOk})");

        // 断言 61：当 Cursor 桌面通道暂未就绪且 codex_cli 未启用时，绝不误调官方 Codex 并误报 Cursor 无额度
        var cursorOnlyCheck = reg.UserConfig.Agents.FirstOrDefault(a => a.Id == "cursor");
        var codexOnlyCheck = reg.UserConfig.Agents.FirstOrDefault(a => a.Id == "codex_cli");
        if (cursorOnlyCheck != null && codexOnlyCheck != null)
        {
            bool bakCodexEn = codexOnlyCheck.Enabled;
            codexOnlyCheck.Enabled = false;
            var stallClsFromMsg = NetworkResilienceService.ClassifyFailure(
                1,
                "",
                "Cursor 桌面端后台通道暂未就绪，已跳过未启用/超额的 Codex 引擎（并非 Cursor 没额度）。",
                null,
                true,
                true);
            codexOnlyCheck.Enabled = bakCodexEn;
            AssertTrue(
                stallClsFromMsg.FailureKind != "quota_exceeded" && stallClsFromMsg.IsRetryable,
                $"禁止将 Codex 引擎问题误报为 Cursor 额度耗尽 (FailureKind={stallClsFromMsg.FailureKind}, Retryable={stallClsFromMsg.IsRetryable})");
        }

        // 断言 62：多套团队配置方案保存、切换（如常规 vs 省主脑额度 Cursor 主力）与删除闭环
        var bakActiveTp = reg.UserConfig.ActiveTeamProfileId;
        var bakTpList = reg.UserConfig.TeamProfiles.ToList();
        var tp1 = reg.CaptureCurrentAsTeamProfile("测试常规方案", "主脑深度参与");
        if (testProj.WorkTypeRoutes != null)
        {
            reg.UserConfig.GlobalWorkTypeRoutes["deep_debug"] = new WorkRouteRule { WorkTypeId = "deep_debug", TargetAgentId = "cursor", TargetModelName = "auto" };
        }
        reg.UserConfig.MaxReworkRounds = 1;
        var tp2 = reg.CaptureCurrentAsTeamProfile("测试省流Cursor主力方案", "主脑仅出方案与终验");
        reg.ApplyTeamProfile(tp1, saveToDisk: false);
        bool switchedBackOk = reg.UserConfig.ActiveTeamProfileId == tp1.Id;
        reg.ApplyTeamProfile(tp2, saveToDisk: false);
        bool switchedEcoOk = reg.UserConfig.ActiveTeamProfileId == tp2.Id &&
                             reg.UserConfig.GlobalWorkTypeRoutes.GetValueOrDefault("deep_debug")?.TargetAgentId == "cursor" &&
                             reg.UserConfig.MaxReworkRounds == 1;
        bool delTpOk = reg.DeleteTeamProfile(tp1.Id) && reg.DeleteTeamProfile(tp2.Id);
        reg.UserConfig.ActiveTeamProfileId = bakActiveTp;
        reg.UserConfig.TeamProfiles = bakTpList;
        AssertTrue(
            switchedBackOk && switchedEcoOk && delTpOk,
            $"多套团队配置方案捕获、切换与删除全通 (Back={switchedBackOk}, Eco={switchedEcoOk}, Del={delTpOk})");

        // 断言 63+：悬浮窗自由缩放 / 飞行动效 / v1.9.3 主脑待命·胶囊·路由精修
        {
            var limits = FloatingOverlayWindow.ClampExpandedWidth(200);
            var limitsHi = FloatingOverlayWindow.ClampExpandedWidth(999);
            var uLo = FloatingOverlayWindow.ClampUltraWidth(10);
            var uHi = FloatingOverlayWindow.ClampUltraWidth(9999);
            var hLo = FloatingOverlayWindow.ClampExpandedHeight(10);
            var hHi = FloatingOverlayWindow.ClampExpandedHeight(9999);
            var cLo = FloatingOverlayWindow.ClampCollapsedWidth(10);
            var cHi = FloatingOverlayWindow.ClampCollapsedWidth(9999);
            var dynCap3 = FloatingOverlayWindow.ComputeDynamicCollapsedWidth(3);
            var dynCap4 = FloatingOverlayWindow.ComputeDynamicCollapsedWidth(4);
            AssertTrue(
                limits == 340 && limitsHi == 860 &&
                uLo == FloatingOverlayWindow.UltraWidthMin && uHi == FloatingOverlayWindow.UltraWidthMax &&
                FloatingOverlayWindow.UltraWidthDefault == 232 &&
                FloatingOverlayWindow.UltraWidthMin == 216 &&
                FloatingOverlayWindow.CompactWidthDefault == 356 &&
                hLo == 165 && hHi == 760 && cLo == 128 && cHi == 680 &&
                dynCap3 >= 174 && dynCap3 <= 186 && dynCap4 >= 204 && dynCap4 <= 216 &&
                AppPaths.AppVersion == "1.9.16",
                $"悬浮窗尺寸上下限与版本号校验通过 (W={limits}~{limitsHi}, Ultra={uLo}~{uHi}/def={FloatingOverlayWindow.UltraWidthDefault}, H={hLo}~{hHi}, Capsule={cLo}~{cHi}, Dyn3={dynCap3}, Dyn4={dynCap4}, Ver={AppPaths.AppVersion})");

            // v1.9.7/1.9.11：5 款内嵌正圆官方 PNG + 发送验证辅助（跨目录根提取/签名）
            string[] brandIds = { "antigravity", "cursor", "grok_bot", "codex_cli", "claude_code" };
            int brandOk = 0;
            int circleOk = 0;
            foreach (var bid in brandIds)
            {
                var icon = OfficialBrandIconService.GetOfficialIconForTest(bid);
                if (icon is BitmapSource bmp && bmp.PixelWidth >= 128 && bmp.PixelHeight >= 128 &&
                    (bmp.IsFrozen || bmp.CanFreeze))
                {
                    brandOk++;
                }
                var vis = OfficialBrandIconService.CreateCircularAvatarVisual(bid, 28);
                if (vis != null && Math.Abs(vis.Width - 28) < 0.01 && vis.Clip is EllipseGeometry eg &&
                    Math.Abs(eg.RadiusX - 14) < 0.01 && Math.Abs(eg.RadiusY - 14) < 0.01)
                {
                    circleOk++;
                }
            }
            var wtStatusFx = RoutingService.InferWorkTypeFromTask(
                "请直接落地修改 FloatingOverlayWindow.StatusFx.cs 与能量条动效");
            var extRoots = AutoDispatchService.ExtractExplicitExternalProjectRoots(
                "请直接修改 C:/Project/Godot/配套工具/tools/ai-collab-hub 工程完成发送验证加固",
                @"C:\Project\Godot\XX");
            var extSig = AutoDispatchService.ComputeExternalRootsLatestWriteSignature(extRoots);
            AssertTrue(
                brandOk == 5 && circleOk == 5 &&
                wtStatusFx != "audio_sfx_bgm" &&
                extRoots.Count >= 1 &&
                extRoots.Any(r => r.Contains("ai-collab-hub", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(extSig) &&
                AppPaths.AppVersion == "1.9.16",
                $"内嵌正圆图标与发送验证辅助校验通过 (Icons128={brandOk}/5, Circles={circleOk}/5, ExtRoots={extRoots.Count}, ExtSigLen={extSig.Length}, StatusFxRoute={wtStatusFx}, Ver={AppPaths.AppVersion})");

            // v1.9.9/1.9.10：官方 CLI 优先配置默认、ResolveCursorAgent 排除 cursor.cmd、渐进尝试与桌面会话索引
            AssertTrue(
                reg.UserConfig.PreferOfficialCliFirst && reg.UserConfig.RequireDesktopVisibleSession,
                $"PreferOfficialCliFirst/RequireDesktopVisibleSession 默认均为 true (cli={reg.UserConfig.PreferOfficialCliFirst}, desk={reg.UserConfig.RequireDesktopVisibleSession})");
            // 禁止桌面 AI 静默降级纯后台无头 CLI：默认开关 false + 策略判定对任意 Stage1 失败原因一律 false
            AssertTrue(
                new UserHubConfig().AllowHeadlessCliFallbackForDesktopAgents == false &&
                reg.UserConfig.AllowHeadlessCliFallbackForDesktopAgents == false,
                "默认 UserHubConfig 禁止桌面 AI 静默降级纯后台无头 CLI (AllowHeadlessCliFallbackForDesktopAgents==false)");
            AssertTrue(
                new UserHubConfig().StrictNoForegroundStealOnDispatch == true &&
                reg.UserConfig.StrictNoForegroundStealOnDispatch == true,
                "默认 UserHubConfig.StrictNoForegroundStealOnDispatch == true（派单严禁抢前台）");
            {
                string[] desktopFailKinds =
                {
                    "no_project_group_fallback_cli", "revert_modal_aborted", "send_failed", ""
                };
                bool cursorAllBlocked = desktopFailKinds.All(k =>
                    !AutoDispatchService.ShouldFallbackToHeadlessCliForDesktopAgent("cursor", false, k) &&
                    !AutoDispatchService.ShouldFallbackToHeadlessCliForDesktopAgent(
                        new AgentRosterEntry { Id = "cursor", Name = "Cursor" },
                        new UserHubConfig { AllowHeadlessCliFallbackForDesktopAgents = false },
                        k));
                bool grokAllBlocked = desktopFailKinds.All(k =>
                    !AutoDispatchService.ShouldFallbackToHeadlessCliForDesktopAgent("grok_bot", false, k) &&
                    !AutoDispatchService.ShouldFallbackToHeadlessCliForDesktopAgent(
                        new AgentRosterEntry { Id = "grok_bot", Name = "Grok Bot" },
                        new UserHubConfig { AllowHeadlessCliFallbackForDesktopAgents = false },
                        k));
                bool allowTrueWorks = AutoDispatchService.ShouldFallbackToHeadlessCliForDesktopAgent(
                    "cursor", true, "no_project_group_fallback_cli");
                AssertTrue(
                    cursorAllBlocked && grokAllBlocked && allowTrueWorks &&
                    AutoDispatchService.IsDesktopClientChannelAgent(new AgentRosterEntry { Id = "cursor", Name = "Cursor" }) &&
                    AutoDispatchService.IsDesktopClientChannelAgent(new AgentRosterEntry { Id = "grok_bot", Name = "Grok Bot" }),
                    $"桌面 AI 禁止无头降级策略自检通过 (cursorBlocked={cursorAllBlocked}, grokBlocked={grokAllBlocked}, allowTrue={allowTrueWorks})");
            }
            var fakeCursorCmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "cursor", "Cursor", "cursor.cmd");
            var resolvedCursorAgent = AutoDispatchService.ResolveCursorAgentCliBinary(fakeCursorCmd);
            AssertTrue(
                resolvedCursorAgent == null ||
                (!resolvedCursorAgent.EndsWith("cursor.cmd", StringComparison.OrdinalIgnoreCase) &&
                 !Path.GetFileName(resolvedCursorAgent).Equals("cursor.exe", StringComparison.OrdinalIgnoreCase)),
                $"ResolveCursorAgentCliBinary 严禁返回 cursor.cmd/cursor.exe (got={resolvedCursorAgent ?? "<null>"})");
            var cursorAttempts = AutoDispatchService.BuildProgressiveCliAttemptsForAgent(
                new AgentRosterEntry { Id = "cursor", Name = "Cursor", RunnerCmd = "auto" }, reg.UserConfig);
            var cursorForced = AutoDispatchService.BuildProgressiveCliAttemptsForAgent(
                new AgentRosterEntry { Id = "cursor", Name = "Cursor", RunnerCmd = "auto" }, reg.UserConfig, forceCursorCli: true);
            var codexAttempts = AutoDispatchService.BuildProgressiveCliAttemptsForAgent(
                new AgentRosterEntry { Id = "codex_cli", Name = "Codex", RunnerCmd = "auto" }, reg.UserConfig);
            var grokAttempts = AutoDispatchService.BuildProgressiveCliAttemptsForAgent(
                new AgentRosterEntry { Id = "grok_bot", Name = "Grok", RunnerCmd = "auto" }, reg.UserConfig);
            var claudeAttempts = AutoDispatchService.BuildProgressiveCliAttemptsForAgent(
                new AgentRosterEntry { Id = "claude_code", Name = "Claude", RunnerCmd = "auto" }, reg.UserConfig);
            AssertTrue(
                cursorAttempts != null && codexAttempts != null && grokAttempts != null && claudeAttempts != null &&
                (codexAttempts!.Count == 0 || codexAttempts.All(a => a.AddDirArgs.Contains("--add-dir") || a.Kind == "codex_exec")),
                $"BuildProgressiveCliAttemptsForAgent 四代理可用 (cursor={cursorAttempts!.Count}, codex={codexAttempts!.Count}, grok={grokAttempts!.Count}, claude={claudeAttempts!.Count})");
            // v1.9.9 精修：grok_bot Stage1 三档全为 grok_cli，严禁混入 codex_exec
            bool grokAllCli = grokAttempts!.Count == 0 ||
                (grokAttempts.Count == 3 &&
                 grokAttempts.All(a => a.Kind == "grok_cli") &&
                 !grokAttempts.Any(a => a.Kind == "codex_exec") &&
                 grokAttempts.Select(a => a.Mode).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3 &&
                 grokAttempts[0].Mode == "streaming_json_resume" &&
                 grokAttempts[1].Mode == "json_fresh" &&
                 grokAttempts[2].Mode == "prompt_fallback");
            // 本机无 grok 时 Count==0 也算结构正确；有 grok 必须 3 档差异化
            if (AutoDispatchService.ResolveGrokCliBinary("auto") != null)
            {
                AssertTrue(grokAllCli && grokAttempts.Count == 3 && grokAttempts.All(a => a.Kind != "codex_exec"),
                    $"grok_bot Stage1 三档全为 grok_cli 且 Mode 差异化 (count={grokAttempts.Count}, kinds={string.Join(",", grokAttempts.Select(a => a.Kind))}, modes={string.Join(",", grokAttempts.Select(a => a.Mode))})");
            }
            else
            {
                AssertTrue(grokAttempts.All(a => a.Kind != "codex_exec"),
                    "grok_bot Stage1 方案不含 codex_exec");
            }
            // codex_cli 三档差异化模式 resume / fresh_exec / minimal_compat
            bool codexModesOk = codexAttempts.Count == 0 ||
                (codexAttempts.Count == 3 &&
                 codexAttempts.All(a => a.Kind == "codex_exec") &&
                 codexAttempts.Select(a => a.Mode).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3 &&
                 codexAttempts.Any(a => a.Mode == "resume") &&
                 codexAttempts.Any(a => a.Mode == "fresh_exec") &&
                 codexAttempts.Any(a => a.Mode == "minimal_compat"));
            if (AutoDispatchService.ResolveCodexBinary(null) != null)
            {
                AssertTrue(codexModesOk,
                    $"codex_cli 存在 3 档差异化模式 (modes={string.Join(",", codexAttempts.Select(a => a.Mode))})");
            }
            // RequireDesktopVisibleSession=true 时 cursor Stage1 默认空；forceCursorCli 不得含 codex_exec
            AssertTrue(
                reg.UserConfig.RequireDesktopVisibleSession && cursorAttempts.Count == 0 &&
                cursorForced.All(a => a.Kind != "codex_exec"),
                $"Cursor 桌面优先时 Stage1 为空且 forceCursorCli 不含 codex_exec (stage1={cursorAttempts.Count}, forced={cursorForced.Count})");
            {
                string launcherCheckPath = @"C:\Project\Godot\配套工具\tools\ai-collab-hub\启动协同中心.ps1";
                string launcherCheckTxt = File.Exists(launcherCheckPath) ? File.ReadAllText(launcherCheckPath, Encoding.UTF8) : "";
                AssertTrue(
                    !launcherCheckTxt.Contains("/SC MINUTE", StringComparison.OrdinalIgnoreCase) &&
                    launcherCheckTxt.Contains("AICollabHub_GuiLaunch", StringComparison.OrdinalIgnoreCase) &&
                    launcherCheckTxt.Contains("schtasks /Run", StringComparison.OrdinalIgnoreCase),
                    "启动协同中心.ps1 已删除永久 MINUTE 轮询，改为一次性 GuiLaunch schtasks");
            }
            var syncProj = new ProjectProfile
            {
                Name = "selftest_sync",
                PrimaryRoot = Path.Combine(outDir, "sync_proj_" + Guid.NewGuid().ToString("N")[..8]),
                CollabRelDir = "work/collab"
            };
            Directory.CreateDirectory(Path.Combine(syncProj.PrimaryRoot, "work", "collab"));
            var sessionSvcSync = new SessionDialogService();
            sessionSvcSync.SyncDesktopVisibleSessionIndex(
                syncProj,
                new AgentRosterEntry { Id = "cursor", Name = "Cursor" },
                "sess_test_199",
                "请直接修改 ai-collab-hub 升至 v1.9.10");
            var syncReg = sessionSvcSync.LoadProjectSessionRegistry(syncProj);
            AssertTrue(
                syncReg.ActiveSessionByAgent.GetValueOrDefault("cursor") == "sess_test_199" &&
                syncReg.DesktopVisibleByAgent.ContainsKey("cursor") &&
                File.Exists(Path.Combine(syncProj.PrimaryRoot, "work", "collab", "DESKTOP_VISIBLE_SESSIONS.json")),
                "SyncDesktopVisibleSessionIndex 已写入 ACTIVE_SESSIONS 与 DESKTOP_VISIBLE_SESSIONS");
            string launcherPs = Path.Combine(AppPaths.AppBaseDir, "..", "..", "..", "..", "启动协同中心.ps1");
            if (!File.Exists(launcherPs))
                launcherPs = Path.GetFullPath(Path.Combine(AppPaths.AppBaseDir, "..", "..", "..", "启动协同中心.ps1"));
            if (!File.Exists(launcherPs))
                launcherPs = @"C:\Project\Godot\配套工具\tools\ai-collab-hub\启动协同中心.ps1";
            string launcherTxt = File.Exists(launcherPs) ? File.ReadAllText(launcherPs, Encoding.UTF8) : "";
            AssertTrue(
                launcherTxt.Contains("schtasks", StringComparison.OrdinalIgnoreCase) &&
                launcherTxt.Contains("/IT", StringComparison.OrdinalIgnoreCase) &&
                launcherTxt.Contains("Win32_Process", StringComparison.OrdinalIgnoreCase),
                $"启动协同中心.ps1 含 schtasks(/IT)+WMI 常驻兜底 (exists={File.Exists(launcherPs)})");

            // ========== v1.9.11 断言：动态高度 / 精简模式 / 动画开关 / WCAG对比度 / #AARRGGBB / 音效 / 额度隔离 / CLI 安装源 ==========
            double dynMin3 = FloatingOverlayWindow.ComputeDynamicMinHeight(3);
            double dynMin5 = FloatingOverlayWindow.ComputeDynamicMinHeight(5);
            AssertTrue(dynMin5 > dynMin3 && dynMin3 >= FloatingOverlayWindow.ExpandedHeightMin,
                $"动态最低高度随启用数增长 (n3={dynMin3}, n5={dynMin5})");
            var modeFull = FloatingOverlayWindow.ResolveCompactMode(520, 3);
            var modeCompact = FloatingOverlayWindow.ResolveCompactMode(252, 3);
            var modeUltra = FloatingOverlayWindow.ResolveCompactMode(140, 5);
            AssertTrue(
                modeFull == FloatingOverlayWindow.FloatingCompactMode.Full &&
                modeCompact == FloatingOverlayWindow.FloatingCompactMode.Compact &&
                modeUltra == FloatingOverlayWindow.FloatingCompactMode.Ultra,
                $"精简紧凑模式切换 (full={modeFull}, compact={modeCompact}, ultra={modeUltra})");
            AssertTrue(
                !FloatingOverlayWindow.AllowsLoopingStatusAnimation(FloatingRichFxKind.VerifiedDelivered) &&
                !FloatingOverlayWindow.AllowsLoopingStatusAnimation(FloatingRichFxKind.MasterStandby) &&
                !FloatingOverlayWindow.AllowsLoopingStatusAnimation(FloatingRichFxKind.IdleReady) &&
                !FloatingOverlayWindow.AllowsLoopingStatusAnimation(FloatingRichFxKind.IdleOffline) &&
                !FloatingOverlayWindow.AllowsLoopingStatusAnimation(FloatingRichFxKind.Independent) &&
                !FloatingOverlayWindow.AllowsLoopingStatusAnimation(FloatingRichFxKind.Error) &&
                FloatingOverlayWindow.AllowsLoopingStatusAnimation(FloatingRichFxKind.Coding) &&
                FloatingOverlayWindow.AllowsLoopingStatusAnimation(FloatingRichFxKind.Thinking),
                "静止完成/待命/空闲/错误态动画循环彻底关闭，仅活跃执行态允许循环");
            AssertTrue(
                FloatingOverlayWindow.WithAlphaHex("#4C1D95", 0x33) == "#334C1D95" &&
                FloatingOverlayWindow.WithAlphaHex("#0D9488", 0x48) == "#480D9488",
                "WithAlphaHex 生成合法 WPF #AARRGGBB 色彩串，杜绝发绿错位");

            static double ParseHexLum(string hex)
            {
                var h = hex.Trim().TrimStart('#');
                if (h.Length == 8) h = h[2..];
                double Ch(int idx)
                {
                    double c = Convert.ToInt32(h.Substring(idx, 2), 16) / 255.0;
                    return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
                }
                return 0.2126 * Ch(0) + 0.7152 * Ch(2) + 0.0722 * Ch(4);
            }
            static double WcagContrast(string fgHex, string bgHex)
            {
                double l1 = ParseHexLum(fgHex), l2 = ParseHexLum(bgHex);
                return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
            }

            var allSpecs = FloatingOverlayWindow.GetAllRichStatusSpecsForTest();
            double minContrast = allSpecs.Min(s => WcagContrast("#FFFFFF", s.BadgeBgHex));
            double minTextBgContrast = allSpecs.Min(s => WcagContrast(s.TextHex, "#141C2B"));
            var masterIdle = FloatingOverlayWindow.ResolveRichStatusForTest(
                new AgentRosterEntry { Id = "antigravity", Name = "Antigravity", Enabled = true },
                null, true, true, true);
            var workerIdle = FloatingOverlayWindow.ResolveRichStatusForTest(
                new AgentRosterEntry { Id = "cursor", Name = "Cursor", Enabled = true },
                null, true, true, false);
            var delivered = FloatingOverlayWindow.ResolveRichStatusForTest(
                new AgentRosterEntry { Id = "cursor", Name = "Cursor", Enabled = true },
                new LiveAgentWorkState { AgentId = "cursor", Status = "done", WorkflowPhase = "verified_delivered", TaskSummary = "完成" },
                true, true, false);
            AssertTrue(
                allSpecs.Count == 13 &&
                minContrast >= 4.8 &&
                minTextBgContrast >= 4.8 &&
                WcagContrast("#FFFFFF", masterIdle.BadgeBgHex) >= 7.0 &&
                WcagContrast("#FFFFFF", workerIdle.BadgeBgHex) >= 7.0 &&
                WcagContrast("#FFFFFF", delivered.BadgeBgHex) >= 7.0 &&
                masterIdle.BadgeBgHex != workerIdle.BadgeBgHex &&
                masterIdle.BadgeBgHex != delivered.BadgeBgHex &&
                workerIdle.BadgeBgHex != delivered.BadgeBgHex &&
                masterIdle.AccentHex != workerIdle.AccentHex &&
                masterIdle.AccentHex != delivered.AccentHex &&
                workerIdle.AccentHex != delivered.AccentHex &&
                !masterIdle.IsActiveWork && !workerIdle.IsActiveWork && !delivered.IsActiveWork,
                $"全 13 种状态 WCAG 对比度与三静态高区分色校验通过 (minBadge={minContrast:F2}, minText={minTextBgContrast:F2}, master={masterIdle.BadgeBgHex}, idle={workerIdle.BadgeBgHex}, done={delivered.BadgeBgHex})");

            AssertTrue(new UserHubConfig().CloseMainWindowToFloating == false,
                "默认点 × 彻底关闭：CloseMainWindowToFloating 默认值为 false");
            AssertTrue(string.Equals(new UserHubConfig().CloseButtonAction, "ask", StringComparison.Ordinal),
                "默认 CloseButtonAction 为 ask（每次弹窗询问）");

            // —— 关闭确认弹窗离屏渲染 + 决策落盘逻辑 + 托盘最小化/恢复 ——
            {
                var (closeDlg, _, rememberChk, minBtn, exitBtn, cancelBtn) = Dialogs.BuildCloseConfirmDialogWindow(null);
                closeDlg.WindowStartupLocation = WindowStartupLocation.Manual;
                closeDlg.Left = -32000;
                closeDlg.Top = -32000;
                closeDlg.ShowActivated = false;
                closeDlg.Show();
                closeDlg.UpdateLayout();

                static List<TextBlock> CollectTextBlocks(DependencyObject? root)
                {
                    var list = new List<TextBlock>();
                    if (root == null) return list;
                    var visited = new HashSet<DependencyObject>();
                    void Walk(DependencyObject node)
                    {
                        if (!visited.Add(node)) return;
                        if (node is TextBlock tb) list.Add(tb);
                        int n = 0;
                        try { n = VisualTreeHelper.GetChildrenCount(node); } catch { }
                        for (int i = 0; i < n; i++)
                        {
                            DependencyObject? child = null;
                            try { child = VisualTreeHelper.GetChild(node, i); } catch { }
                            if (child != null) Walk(child);
                        }
                        if (node is System.Windows.Controls.Panel panel)
                        {
                            foreach (var c in panel.Children.OfType<DependencyObject>()) Walk(c);
                        }
                        else if (node is ContentControl cc && cc.Content is DependencyObject cd) Walk(cd);
                        else if (node is Decorator dec && dec.Child is DependencyObject dd) Walk(dd);
                    }
                    Walk(root);
                    if (root is Window w && w.Content is DependencyObject contentRoot) Walk(contentRoot);
                    return list;
                }

                var texts = CollectTextBlocks(closeDlg);
                string allText = string.Join("\n", texts.Select(t => t.Text ?? ""));
                AssertTrue(closeDlg.Title.Contains("关闭主界面确认", StringComparison.Ordinal),
                    "CloseConfirmDialog 标题为「关闭主界面确认」");
                AssertTrue(allText.Contains("最小化到系统托盘", StringComparison.Ordinal)
                           && allText.Contains("关闭软件", StringComparison.Ordinal)
                           && allText.Contains("系统托盘", StringComparison.Ordinal),
                    "CloseConfirmDialog 说明文案完整渲染（托盘 / 关闭差异）");
                AssertTrue(string.Equals(minBtn.Content as string, "最小化到系统托盘", StringComparison.Ordinal)
                           && string.Equals(exitBtn.Content as string, "关闭软件", StringComparison.Ordinal)
                           && string.Equals(cancelBtn.Content as string, "取消", StringComparison.Ordinal),
                    "CloseConfirmDialog 三按钮文案完整：最小化到系统托盘 / 关闭软件 / 取消");
                AssertTrue(rememberChk.IsChecked != true
                           && (rememberChk.Content as string)?.Contains("记住此操作", StringComparison.Ordinal) == true,
                    "CloseConfirmDialog 「记住此操作」勾选框默认未勾选且文案完整");
                closeDlg.Close();

                string prevCloseAction = reg.UserConfig.CloseButtonAction;
                bool prevCloseFloat = reg.UserConfig.CloseMainWindowToFloating;
                try
                {
                    reg.UserConfig.CloseButtonAction = "ask";
                    reg.UserConfig.CloseMainWindowToFloating = false;

                    MainWindow.ApplyCloseDialogDecision(reg, new Dialogs.CloseConfirmChoice
                    {
                        Kind = Dialogs.CloseConfirmChoiceKind.MinimizeToTray,
                        Remember = true
                    });
                    AssertTrue(
                        string.Equals(reg.UserConfig.CloseButtonAction, "minimize_to_tray", StringComparison.OrdinalIgnoreCase)
                        && reg.UserConfig.CloseMainWindowToFloating,
                        "选择「最小化到系统托盘」+ 记住 → CloseButtonAction=minimize_to_tray 并落盘");

                    MainWindow.ApplyCloseDialogDecision(reg, new Dialogs.CloseConfirmChoice
                    {
                        Kind = Dialogs.CloseConfirmChoiceKind.ExitApp,
                        Remember = true
                    });
                    AssertTrue(
                        string.Equals(reg.UserConfig.CloseButtonAction, "exit", StringComparison.OrdinalIgnoreCase)
                        && !reg.UserConfig.CloseMainWindowToFloating,
                        "选择「关闭软件」+ 记住 → CloseButtonAction=exit 并落盘");

                    reg.UserConfig.CloseButtonAction = "ask";
                    reg.UserConfig.CloseMainWindowToFloating = false;
                    reg.SaveUserConfig();
                    MainWindow.ApplyCloseDialogDecision(reg, new Dialogs.CloseConfirmChoice
                    {
                        Kind = Dialogs.CloseConfirmChoiceKind.Cancel,
                        Remember = true
                    });
                    AssertTrue(
                        string.Equals(reg.UserConfig.CloseButtonAction, "ask", StringComparison.OrdinalIgnoreCase)
                        && !reg.UserConfig.CloseMainWindowToFloating,
                        "选择「取消」即使勾选记住也绝不改写 CloseButtonAction");

                    // 旧配置兼容：空 CloseButtonAction + CloseMainWindowToFloating=true → minimize_to_tray
                    var legacyCfg = new UserHubConfig { CloseButtonAction = "", CloseMainWindowToFloating = true };
                    AssertTrue(
                        string.Equals(MainWindow.ResolveCloseActionChoice(legacyCfg), "minimize_to_tray", StringComparison.Ordinal),
                        "旧配置空 CloseButtonAction + CloseMainWindowToFloating=true 兼容为 minimize_to_tray");
                }
                finally
                {
                    reg.UserConfig.CloseButtonAction = prevCloseAction;
                    reg.UserConfig.CloseMainWindowToFloating = prevCloseFloat;
                    MainWindow.SyncCloseButtonActionFields(reg.UserConfig);
                    reg.SaveUserConfig();
                }

                var trayProbe = new MainWindow(reg, suppressAutoShowFloating: true)
                {
                    Width = 960,
                    Height = 600,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                    ShowActivated = false,
                    ShowInTaskbar = true
                };
                trayProbe.Show();
                trayProbe.UpdateLayout();
                AssertTrue(trayProbe.IsVisible && trayProbe.ShowInTaskbar,
                    "托盘探针窗初始可见且 ShowInTaskbar=true");
                trayProbe.MinimizeMainWindowToTray(ensureFloatingVisible: false);
                trayProbe.UpdateLayout();
                AssertTrue(!trayProbe.ShowInTaskbar && !trayProbe.IsVisible,
                    "MinimizeMainWindowToTray 后 ShowInTaskbar=false 且不可见（缩到系统托盘）");
                trayProbe.RestoreAndActivateMainWindow();
                trayProbe.UpdateLayout();
                AssertTrue(trayProbe.ShowInTaskbar && trayProbe.IsVisible
                           && trayProbe.WindowState != WindowState.Minimized,
                    "RestoreAndActivateMainWindow 后 ShowInTaskbar=true 且正常显示");
                trayProbe.Close();
            }

            var soundCfgOn = new UserHubConfig { EnableSoundEffects = true, SoundLightweightMode = false };
            var soundCfgLite = new UserHubConfig { EnableSoundEffects = true, SoundLightweightMode = true };
            var soundCfgOff = new UserHubConfig { EnableSoundEffects = false, SoundLightweightMode = false };
            AssertTrue(
                SoundFxService.ShouldPlay(soundCfgOn, SoundFxService.SoundKind.DispatchFlight) &&
                !SoundFxService.ShouldPlay(soundCfgLite, SoundFxService.SoundKind.DispatchFlight) &&
                SoundFxService.ShouldPlay(soundCfgLite, SoundFxService.SoundKind.VerifyPassed) &&
                !SoundFxService.ShouldPlay(soundCfgOff, SoundFxService.SoundKind.VerifyPassed),
                "音效总开关与轻量化过滤逻辑正确");

            SoundFxService.MarkShuttingDown(true);
            bool allKindsMuted = true;
            foreach (SoundFxService.SoundKind kind in Enum.GetValues<SoundFxService.SoundKind>())
            {
                if (SoundFxService.ShouldPlay(soundCfgOn, kind))
                {
                    allKindsMuted = false;
                    break;
                }
            }
            AssertTrue(allKindsMuted, "MarkShuttingDown(true) 后 ShouldPlay 对所有 SoundKind 均返回 false");
            try { SoundFxService.StopAll(); }
            catch (Exception ex) { AssertTrue(false, $"StopAll 调用应安全无异常: {ex.Message}"); }
            SoundFxService.MarkShuttingDown(false);
            AssertTrue(SoundFxService.ShouldPlay(soundCfgOn, SoundFxService.SoundKind.VerifyPassed),
                "MarkShuttingDown(false) 复位后音效闸门恢复");

            // —— 进程级硬闸门：ApplySoundPreference(off) 拦截所有 SoundKind ——
            SoundFxService.ApplySoundPreference(soundCfgOn);
            AssertTrue(SoundFxService.ShouldPlay(soundCfgOn, SoundFxService.SoundKind.WorkStarted),
                "音效开启时 WorkStarted 应允许播");
            soundCfgOn.EnableSoundEffects = false;
            SoundFxService.ApplySoundPreference(soundCfgOn);
            bool allKindsHardMuted = true;
            foreach (SoundFxService.SoundKind kind in Enum.GetValues<SoundFxService.SoundKind>())
            {
                if (SoundFxService.ShouldPlay(soundCfgOn, kind) || SoundFxService.ShouldPlay(soundCfgOff, kind))
                {
                    allKindsHardMuted = false;
                    break;
                }
            }
            AssertTrue(allKindsHardMuted, "ApplySoundPreference(off) 后所有 SoundKind 均被硬拦截");
            SoundFxService.Play(soundCfgOn, SoundFxService.SoundKind.WorkStarted);
            SoundFxService.Play(soundCfgOn, SoundFxService.SoundKind.StatusChange);
            try { SoundFxService.StopAll(); }
            catch (Exception ex) { AssertTrue(false, $"静音态 StopAll 应安全无异常: {ex.Message}"); }
            // 复位供后续默认配置断言使用
            soundCfgOn.EnableSoundEffects = true;
            SoundFxService.ApplySoundPreference(new UserHubConfig { EnableSoundEffects = true, SoundLightweightMode = false });
            AssertTrue(SoundFxService.ShouldPlay(soundCfgOn, SoundFxService.SoundKind.DispatchFlight),
                "重新 ApplySoundPreference(on) 后音效闸门恢复");

            // —— 内嵌真实 RIFF/WAVE：每个 SoundKind 命中程序集资源，时长与峰值可复现 ——
            var embeddedWavNames = typeof(SoundFxService).Assembly.GetManifestResourceNames();
            var wavDigests = new HashSet<string>(StringComparer.Ordinal);
            foreach (SoundFxService.SoundKind kind in Enum.GetValues<SoundFxService.SoundKind>())
            {
                string resName = SoundFxService.EmbeddedWavResourceName(kind);
                AssertTrue(embeddedWavNames.Contains(resName),
                    $"SoundKind.{kind} 命中程序集内嵌资源 {resName}");
                byte[] wav = SoundFxService.LoadEmbeddedWav(kind);
                bool parsed = TryInspectEmbeddedPcmWav(
                    wav, out int rate, out int channels, out int bits,
                    out double dur, out double active, out double peak);
                // DOLLARS 电钢短乐句约 0.95–2.20 s，断言窗口覆盖 0.5–2.8 s
                AssertTrue(
                    parsed && rate == 44100 && bits == 16 && channels >= 1
                    && dur >= 0.5 && dur <= 2.8
                    && active >= 0.5 && active <= 2.8
                    && peak > 0.02 && peak <= 0.98,
                    $"SoundKind.{kind} 为合法非静音 RIFF/WAVE (rate={rate}, bits={bits}, ch={channels}, dur={dur:0.000}, active={active:0.000}, peak={peak:0.000})");
                wavDigests.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(wav)));
            }
            AssertTrue(
                wavDigests.Count == Enum.GetValues<SoundFxService.SoundKind>().Length,
                "各 SoundKind 内嵌波形互不重复");

            // —— SilenceCursorAccessibilityAudio 隔离 JSONC 单测 ——
            string cursorSilenceDir = Path.Combine(outDir, "cursor_silence_probe");
            Directory.CreateDirectory(cursorSilenceDir);
            string goodJsoncPath = Path.Combine(cursorSilenceDir, "settings_good.jsonc");
            File.WriteAllText(goodJsoncPath,
                """
                {
                  // keep me
                  "update.mode": "manual",
                  "accessibility.signals.progress": { "sound": "auto" },
                }
                """,
                Encoding.UTF8);
            var (silenceOk, silenceMsg) = SoundFxService.SilenceCursorAccessibilityAudio(goodJsoncPath);
            AssertTrue(silenceOk, $"SilenceCursorAccessibilityAudio 合法 JSONC 应成功: {silenceMsg}");
            string goodAfter = File.ReadAllText(goodJsoncPath, Encoding.UTF8);
            AssertTrue(
                goodAfter.Contains("\"update.mode\"", StringComparison.Ordinal) &&
                goodAfter.Contains("manual", StringComparison.Ordinal),
                "SilenceCursorAccessibilityAudio 须保留原 JSONC 字段 update.mode");
            AssertTrue(
                goodAfter.Contains("accessibility.signals.progress", StringComparison.Ordinal) &&
                goodAfter.Contains("\"sound\": \"off\"", StringComparison.Ordinal),
                "须写入 accessibility.signals.progress.sound=off");
            AssertTrue(
                goodAfter.Contains("accessibility.signalOptions.volume", StringComparison.Ordinal) &&
                goodAfter.Contains("cursor.composer.shouldChimeAfterChatFinishes", StringComparison.Ordinal) &&
                goodAfter.Contains("false", StringComparison.Ordinal),
                "须写入 volume=0 与 shouldChimeAfterChatFinishes=false");
            AssertTrue(
                !goodAfter.Contains("editor.accessibilitySupport", StringComparison.Ordinal),
                "严禁写入 editor.accessibilitySupport");
            string badJsoncPath = Path.Combine(cursorSilenceDir, "settings_bad.jsonc");
            const string badRaw = "{ this is not valid jsonc !!!";
            File.WriteAllText(badJsoncPath, badRaw, Encoding.UTF8);
            var (badOk, _) = SoundFxService.SilenceCursorAccessibilityAudio(badJsoncPath);
            AssertTrue(!badOk, "损坏 JSONC 须安全拒写");
            AssertTrue(
                File.ReadAllText(badJsoncPath, Encoding.UTF8) == badRaw,
                "损坏 JSONC 拒写后原文件内容不得被破坏");

            // —— 全源码不得引入循环播音 API（拼针避免本断言文本自命中）——
            string hubSrcRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            if (!Directory.Exists(Path.Combine(hubSrcRoot, "Services")))
                hubSrcRoot = @"C:\Project\Godot\配套工具\tools\ai-collab-hub\src";
            string loopApiNeedle = "." + "Play" + "Looping" + "(";
            var playLoopingHits = Directory.EnumerateFiles(hubSrcRoot, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(f => File.ReadAllText(f, Encoding.UTF8).Contains(loopApiNeedle, StringComparison.Ordinal))
                .Select(Path.GetFileName)
                .ToList();
            AssertTrue(playLoopingHits.Count == 0,
                $"全源码不得引入循环播音 API (命中={string.Join(",", playLoopingHits)})");

            var misBadge = FloatingOverlayWindow.SanitizeFailureBadgeForDisplay("cursor", "⚠️ 账号配额耗尽 · Codex");
            AssertTrue(
                misBadge.Contains("Codex", StringComparison.OrdinalIgnoreCase) &&
                !misBadge.Contains("Cursor 额度已用完", StringComparison.Ordinal),
                $"额度误报隔离校验 (badge={misBadge})");
            AssertTrue(
                CliInstallService.Catalog.Count >= 4 &&
                CliInstallService.FindSource("cursor") is { } curCliSrc &&
                curCliSrc.InstallCommandHint.Contains("win32=true", StringComparison.OrdinalIgnoreCase) &&
                CliInstallService.FindSource("codex_cli") != null &&
                !CliInstallService.BuildMissingCliGuidance("Cursor").Contains("安装", StringComparison.Ordinal) &&
                CliInstallService.BuildMissingCliGuidance("Cursor").Contains("跳过该通道", StringComparison.Ordinal),
                $"CLI 一键安装源与静默跳过提示校验通过 (count={CliInstallService.Catalog.Count}, cursorHint={CliInstallService.FindSource("cursor")!.InstallCommandHint})");
            AssertTrue(reg.UserConfig.EnableSoundEffects && !reg.UserConfig.SoundLightweightMode,
                "音效默认启用且轻量化默认关闭");
            var trayIco = TrayIconService.ResolveTrayIconFile();
            AssertTrue(!string.IsNullOrWhiteSpace(trayIco) && File.Exists(trayIco),
                $"单文件托盘与任务栏图标可从内嵌资源释放校验通过 (ico={trayIco})");

            // 细分状态文案断言（不依赖屏幕焦点）
            string thinkBadge = FloatingOverlayWindow.ResolveRichStatusForTest(
                new AgentRosterEntry { Id = "cursor", Name = "Cursor", Enabled = true },
                new LiveAgentWorkState { AgentId = "cursor", Status = "working", WorkflowPhase = "executing", TaskSummary = "思考任务", LatestLogTail = "Thinking about plan" },
                true, true, false).BadgeText;
            string analyzeBadge = FloatingOverlayWindow.ResolveRichStatusForTest(
                new AgentRosterEntry { Id = "antigravity", Name = "Antigravity", Enabled = true },
                new LiveAgentWorkState { AgentId = "antigravity", Status = "working", WorkflowPhase = "master_analyzing", TaskSummary = "分析" },
                true, true, true).BadgeText;
            string codingBadge = FloatingOverlayWindow.ResolveRichStatusForTest(
                new AgentRosterEntry { Id = "cursor", Name = "Cursor", Enabled = true },
                new LiveAgentWorkState { AgentId = "cursor", Status = "working", WorkflowPhase = "executing", TaskSummary = "改码", LatestLogTail = "Editing file\nRunning tests" },
                true, true, false).BadgeText;
            string verifyBadge = FloatingOverlayWindow.ResolveRichStatusForTest(
                new AgentRosterEntry { Id = "cursor", Name = "Cursor", Enabled = true },
                new LiveAgentWorkState { AgentId = "cursor", Status = "working", WorkflowPhase = "master_verifying", TaskSummary = "审查" },
                true, true, false).BadgeText;
            string idleBadge = FloatingOverlayWindow.ResolveRichStatusForTest(
                new AgentRosterEntry { Id = "cursor", Name = "Cursor", Enabled = true },
                null, true, true, false).BadgeText;
            string offlineBadge = FloatingOverlayWindow.ResolveRichStatusForTest(
                new AgentRosterEntry { Id = "cursor", Name = "Cursor", Enabled = true },
                null, false, true, false).BadgeText;
            string deliverBadge = FloatingOverlayWindow.ResolveRichStatusForTest(
                new AgentRosterEntry { Id = "cursor", Name = "Cursor", Enabled = true },
                new LiveAgentWorkState { AgentId = "cursor", Status = "working", WorkflowPhase = "executing", TaskSummary = "交付", LatestLogTail = "落盘完成，正在提交主脑验收 inbox" },
                true, true, false).BadgeText;
            string reworkBadge = FloatingOverlayWindow.ResolveRichStatusForTest(
                new AgentRosterEntry { Id = "cursor", Name = "Cursor", Enabled = true },
                new LiveAgentWorkState { AgentId = "cursor", Status = "working", WorkflowPhase = "reworking", TaskSummary = "精修", CurrentRound = 2 },
                true, true, false).BadgeText;
            string doneBadge = FloatingOverlayWindow.ResolveRichStatusForTest(
                new AgentRosterEntry { Id = "cursor", Name = "Cursor", Enabled = true },
                new LiveAgentWorkState { AgentId = "cursor", Status = "done", WorkflowPhase = "verified_delivered", TaskSummary = "完成" },
                true, true, false).BadgeText;
            // v1.9.3：主脑空闲（含窗口未运行）始终「👑 主脑待命」，且可联动终验文案
            var masterIdleOffline = FloatingOverlayWindow.ResolveRichStatusForTest(
                new AgentRosterEntry { Id = "antigravity", Name = "Antigravity", Enabled = true },
                null, winRunning: false, workflowEnabled: true, isMaster: true);
            var masterIdleAfterDeliver = FloatingOverlayWindow.ResolveRichStatusForTest(
                new AgentRosterEntry { Id = "antigravity", Name = "Antigravity", Enabled = true },
                null, winRunning: true, workflowEnabled: true, isMaster: true,
                activeWorkerOrNull: null, recentWorkerDelivered: true);
            AssertTrue(
                thinkBadge.Contains("思考推演") && analyzeBadge.Contains("深度分析") && codingBadge.Contains("编码实操") &&
                verifyBadge.Contains("严格审查") && idleBadge.Contains("空闲待命") && offlineBadge.Contains("未启动") &&
                deliverBadge.Contains("正在交付") && reworkBadge.Contains("打回精修") && doneBadge.Contains("达标已交付") &&
                masterIdleOffline.BadgeText.Contains("主脑待命") && !masterIdleOffline.BadgeText.Contains("未启动") &&
                masterIdleOffline.StatusLine.Contains("全入口统领主脑") &&
                masterIdleAfterDeliver.StatusLine.Contains("已终验放行"),
                $"细分状态徽章文案齐全 (Think={thinkBadge}, Analyze={analyzeBadge}, Code={codingBadge}, Verify={verifyBadge}, Idle={idleBadge}, Off={offlineBadge}, Deliver={deliverBadge}, Rework={reworkBadge}, Done={doneBadge}, MasterIdle={masterIdleOffline.BadgeText}/{masterIdleOffline.StatusLine}, MasterDeliver={masterIdleAfterDeliver.StatusLine})");

            // v1.9.3：明确写代码指令时不因「反馈/图像」字眼误归 visual_accept
            var wtCodeWithVisualWords = RoutingService.InferWorkTypeFromTask(
                "请直接落地修改 FloatingOverlayWindow.xaml.cs 与 StatusFx.cs，并根据反馈图像核对主脑徽章展示");
            var wtPureVisual = RoutingService.InferWorkTypeFromTask("请做画面验收与 MCP 截图实看核对角色同框");
            AssertTrue(
                wtCodeWithVisualWords != "visual_accept" && wtPureVisual == "visual_accept",
                $"写代码指令不被 visual_accept 误拦 (codeMix={wtCodeWithVisualWords}, pureVisual={wtPureVisual})");

            var fxHost = new MainWindow(reg, suppressAutoShowFloating: true)
            {
                Width = 960,
                Height = 600,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowActivated = false
            };
            fxHost.Show();
            var floatFx = new FloatingOverlayWindow(reg, fxHost)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -12000,
                Top = -12000,
                ShowActivated = false
            };
            floatFx.Show(); // 外部 Show 一次；ResolveAvatarCenterForTest 内部禁止 Close/重复 Show
            floatFx.ApplyCustomSizeForTest(500, 280, collapsed: false);
            AssertTrue(
                Math.Abs(floatFx.Width - 500) < 0.5 &&
                reg.UserConfig.FloatingCustomWidth == 500 &&
                reg.UserConfig.FloatingCustomHeight == 280,
                $"展开态自定义宽高持久化字段写入正确 (W={floatFx.Width}, H={reg.UserConfig.FloatingCustomHeight})");

            floatFx.ApplyCustomSizeForTest(900, null, collapsed: false);
            AssertTrue(floatFx.Width == 860, $"展开态宽度上限钳制到 860 (实际={floatFx.Width})");
            floatFx.ApplyCustomSizeForTest(400, null, collapsed: true);
            AssertTrue(
                Math.Abs(floatFx.Width - 400) < 0.5 &&
                reg.UserConfig.FloatingCollapsedCustomWidth == 400 &&
                floatFx.ResizeThumbCorner.Visibility == Visibility.Collapsed,
                $"收起胶囊自定义宽度持久化正确且斜纹抓手已隐藏 (W={floatFx.Width}, CornerVis={floatFx.ResizeThumbCorner.Visibility})");
            // 还原自定义胶囊宽后验证动态默认收窄
            reg.UserConfig.FloatingCollapsedCustomWidth = null;
            floatFx.ApplyCollapsedVisual(true, preserveRightAnchor: false);
            floatFx.RefreshOverlayData(force: true);
            double dynCollapsed = FloatingOverlayWindow.ComputeDynamicCollapsedWidth(
                Math.Max(1, reg.UserConfig.Agents.Count(a => a.Enabled)));
            AssertTrue(
                Math.Abs(floatFx.Width - dynCollapsed) < 1.0 &&
                floatFx.Width >= FloatingOverlayWindow.CollapsedWidthMin &&
                floatFx.CapsuleBriefText.Visibility == Visibility.Collapsed &&
                floatFx.CollapsedMiniCapsule.ToolTip != null,
                $"收起胶囊动态宽度与去任务文案正确 (W={floatFx.Width}, Dyn={dynCollapsed}, BriefVis={floatFx.CapsuleBriefText.Visibility})");

            floatFx.ApplyCustomSizeForTest(520, 300, collapsed: false);
            AssertTrue(
                floatFx.ResizeThumbCorner.Visibility == Visibility.Collapsed &&
                floatFx.ResizeThumbBottom.Visibility == Visibility.Collapsed,
                $"展开态已禁用上下拖拽并隐藏斜纹抓手，仅保留横向调宽 (CornerVis={floatFx.ResizeThumbCorner.Visibility}, BottomVis={floatFx.ResizeThumbBottom.Visibility})");
            floatFx.RefreshOverlayData(force: true);
            floatFx.UpdateLayout();
            var expandedCursorPt = floatFx.ResolveAvatarCenterForTest("cursor");
            var expandedMasterPt = floatFx.ResolveAvatarCenterForTest("antigravity");
            AssertTrue(
                expandedCursorPt.HasValue && expandedMasterPt.HasValue &&
                expandedCursorPt.Value.X > 0 && expandedMasterPt.Value.X > 0,
                $"展开态头像中心坐标可解析 (cursor={expandedCursorPt}, master={expandedMasterPt})");

            floatFx.ApplyCustomSizeForTest(400, null, collapsed: true);
            floatFx.RefreshOverlayData(force: true);
            floatFx.UpdateLayout();
            var collapsedCursorPt = floatFx.ResolveAvatarCenterForTest("cursor");
            var collapsedMasterPt = floatFx.ResolveAvatarCenterForTest("antigravity");
            AssertTrue(
                collapsedCursorPt.HasValue && collapsedMasterPt.HasValue &&
                collapsedCursorPt.Value.X > 0 && collapsedMasterPt.Value.X > 0,
                $"收起胶囊态头像中心坐标可解析且不被隐藏展开态覆盖 (cursor={collapsedCursorPt}, master={collapsedMasterPt})");

            floatFx.ApplyCustomSizeForTest(520, 300, collapsed: false);
            floatFx.RefreshOverlayData(force: true);
            int animBefore = floatFx.InteractionAnimationTriggerCountForTest;
            int animAfter = floatFx.TriggerInteractionAnimationForTest("dispatch", "antigravity", "cursor");
            floatFx.TriggerInteractionAnimationForTest("deliver", "cursor", "antigravity");
            floatFx.TriggerInteractionAnimationForTest("rework", "antigravity", "cursor");
            floatFx.TriggerInteractionAnimationForTest("relay", "cursor", "grok_bot");
            bool bannerOk = floatFx.TriggerVerifyBannerForTest("悬浮窗验收动效自检", "antigravity", "cursor");
            AssertTrue(
                animAfter == animBefore + 1 &&
                floatFx.InteractionAnimationTriggerCountForTest >= animBefore + 4 &&
                bannerOk &&
                floatFx.VerifyBannerTriggerCountForTest >= 1,
                $"飞行交互动画与主脑验收横幅触发通过 (Anim={floatFx.InteractionAnimationTriggerCountForTest}, Banner={floatFx.VerifyBannerTriggerCountForTest})");

            bool flightBakCollapsed = reg.UserConfig.FloatingWindowCollapsed;
            string flightBakLayout = reg.UserConfig.FloatingLayoutMode;
            double? flightBakW = reg.UserConfig.FloatingCustomWidth;
            double? flightBakH = reg.UserConfig.FloatingCustomHeight;
            try
            {
                static bool FlightPathSafe(FloatingOverlayWindow.FlightGeometrySample g)
                {
                    double hi = Math.Max(4, g.WinH - 4);
                    double cap = Math.Max(28, g.WinH - 4);
                    bool yOk = g.FromY >= 4 - 0.05 && g.FromY <= hi + 0.05 &&
                               g.MidY >= 4 - 0.05 && g.MidY <= hi + 0.05 &&
                               g.ToY >= 4 - 0.05 && g.ToY <= hi + 0.05;
                    return g.WithinSafeZone && yOk && g.MaxRingDiameter > 0 && g.MaxRingDiameter <= cap + 0.05;
                }

                reg.UserConfig.FloatingWindowCollapsed = true;
                reg.UserConfig.FloatingLayoutMode = "full";
                reg.UserConfig.FloatingCustomHeight = null;
                floatFx.ApplyCollapsedVisual(true, preserveRightAnchor: false);
                floatFx.RefreshOverlayData(force: true);
                floatFx.UpdateLayout();
                floatFx.TriggerFlightForTest("dispatch", "antigravity", "cursor", "折叠胶囊飞行");
                var collapsedFlight = floatFx.LastFlightGeometryForTest;
                AssertTrue(
                    floatFx.ActualHeight > 8 &&
                    Math.Abs(collapsedFlight.WinH - floatFx.ActualHeight) < 1.5 &&
                    FlightPathSafe(collapsedFlight) &&
                    floatFx.InteractionFxChildCountForTest == 0,
                    $"折叠胶囊飞行不越出高度安全区且无残留 (H={floatFx.ActualHeight:F1}, midY={collapsedFlight.MidY:F1}, ring={collapsedFlight.MaxRingDiameter:F1}, residue={floatFx.InteractionFxChildCountForTest})");

                reg.UserConfig.FloatingWindowCollapsed = false;
                reg.UserConfig.FloatingLayoutMode = "full";
                reg.UserConfig.FloatingCustomWidth = 520;
                reg.UserConfig.FloatingCustomHeight = 300;
                floatFx.ApplyCollapsedVisual(false, preserveRightAnchor: false);
                floatFx.RefreshOverlayData(force: true);
                floatFx.UpdateLayout();
                floatFx.TriggerFlightForTest("verified_delivered", "cursor", "antigravity", "展开验收飞行");
                var fullFlight = floatFx.LastFlightGeometryForTest;
                AssertTrue(
                    floatFx.CurrentCompactModeForTest == FloatingOverlayWindow.FloatingCompactMode.Full &&
                    Math.Abs(fullFlight.WinH - floatFx.ActualHeight) < 2.0 &&
                    FlightPathSafe(fullFlight) &&
                    fullFlight.WinH > 64 &&
                    floatFx.InteractionFxChildCountForTest == 0,
                    $"展开态飞行不越出高度安全区且无残留 (H={floatFx.ActualHeight:F1}, midY={fullFlight.MidY:F1}, ring={fullFlight.MaxRingDiameter:F1}, residue={floatFx.InteractionFxChildCountForTest})");
            }
            finally
            {
                reg.UserConfig.FloatingWindowCollapsed = flightBakCollapsed;
                reg.UserConfig.FloatingLayoutMode = flightBakLayout;
                reg.UserConfig.FloatingCustomWidth = flightBakW;
                reg.UserConfig.FloatingCustomHeight = flightBakH;
                floatFx.ApplyCollapsedVisual(flightBakCollapsed, preserveRightAnchor: false);
                floatFx.UpdateLayout();
            }

            // 动效构建冒烟：刷新后卡片树非空（含能量竖条/状态签）
            AssertTrue(
                floatFx.FloatingAgentsRowCountForTest > 0 &&
                floatFx.BuildRichStatusBadgeTextForTest(FloatingRichFxKind.Coding).Contains("编码实操") &&
                floatFx.BuildRichStatusBadgeTextForTest(FloatingRichFxKind.Thinking).Contains("思考推演"),
                $"细分状态卡片与动效构建冒烟通过 (Rows={floatFx.FloatingAgentsRowCountForTest})");

            int enCnt = Math.Max(1, reg.UserConfig.Agents.Count(a => a.Enabled));
            double ultraDynH = FloatingOverlayWindow.ComputeDynamicMinHeight(enCnt);
            floatFx.ApplyCustomSizeForTest(420, ultraDynH, collapsed: false);
            floatFx.RefreshOverlayData(force: true);
            var expectedMinMode = enCnt <= 1
                ? FloatingOverlayWindow.FloatingCompactMode.Full
                : FloatingOverlayWindow.FloatingCompactMode.Ultra;
            bool ultraNarrowOk = expectedMinMode != FloatingOverlayWindow.FloatingCompactMode.Ultra
                || (Math.Abs(floatFx.Width - FloatingOverlayWindow.UltraWidthDefault) < 1.0
                    || (reg.UserConfig.FloatingUltraCustomWidth is double uw
                        && Math.Abs(floatFx.Width - FloatingOverlayWindow.ClampUltraWidth(uw)) < 1.0));
            AssertTrue(
                floatFx.CurrentCompactModeForTest == expectedMinMode &&
                floatFx.FloatingAgentsRowCountForTest == enCnt &&
                ultraNarrowOk,
                $"最低动态高度切换至 {expectedMinMode} 且全员完整展示 (Rows={floatFx.FloatingAgentsRowCountForTest}/{enCnt}, DynMinH={ultraDynH}, UltraW={floatFx.Width})");

            // 显式 Ultra 布局：宽度应收窄到 UltraWidthDefault，且行内不渲染任务描述
            reg.UserConfig.FloatingLayoutMode = "ultra";
            reg.UserConfig.FloatingCustomHeight = null;
            reg.UserConfig.FloatingUltraCustomWidth = null;
            floatFx.ApplyUserPreferencesFromSettings();
            floatFx.RefreshOverlayData(force: true);
            floatFx.UpdateLayout();
            bool ultraNoTaskLine = true;
            foreach (var child in floatFx.FloatingAgentsStack.Children.OfType<FrameworkElement>())
            {
                if (CollectLogicalAndVisual<System.Windows.Controls.TextBlock>(child).Any(t => t.Name == "TaskStatusLine"))
                {
                    ultraNoTaskLine = false;
                    break;
                }
            }
            AssertTrue(
                floatFx.CurrentCompactModeForTest == FloatingOverlayWindow.FloatingCompactMode.Ultra &&
                Math.Abs(floatFx.Width - FloatingOverlayWindow.UltraWidthDefault) < 1.0 &&
                ultraNoTaskLine &&
                floatFx.HeaderTitleText.Visibility == Visibility.Collapsed,
                $"Ultra 极简行收窄且无任务文案 (W={floatFx.Width}, NoTaskLine={ultraNoTaskLine}, TitleVis={floatFx.HeaderTitleText.Visibility})");

            // 真实 HWND 往返：Collapsed ➔ Ultra ➔ Full ➔ Collapsed ➔ Toggle 展开回 Ultra，每步校验 ActualWidth（防卡死在 188）
            {
                double? rtOrigCustomW = reg.UserConfig.FloatingCustomWidth;
                double? rtOrigCollapsedW = reg.UserConfig.FloatingCollapsedCustomWidth;
                bool rtOrigCollapsed = reg.UserConfig.FloatingWindowCollapsed;
                string rtOrigLayout = reg.UserConfig.FloatingLayoutMode;
                double? rtOrigCustomH = reg.UserConfig.FloatingCustomHeight;
                double? rtOrigUltraW = reg.UserConfig.FloatingUltraCustomWidth;
                try
                {
                    reg.UserConfig.FloatingCustomWidth = null;
                    reg.UserConfig.FloatingCollapsedCustomWidth = null;
                    reg.UserConfig.FloatingCustomHeight = null;
                    reg.UserConfig.FloatingUltraCustomWidth = null;
                    double rtCollapsedW = FloatingOverlayWindow.ClampCollapsedWidth(FloatingOverlayWindow.ComputeDynamicCollapsedWidth(enCnt));
                    double ultraW = FloatingOverlayWindow.UltraWidthDefault;
                    double fullW = FloatingOverlayWindow.ExpandedWidthDefault;

                    bool WidthMatches(double want) =>
                        Math.Abs(floatFx.Width - want) < 1.5 &&
                        Math.Abs(floatFx.ActualWidth - want) < 2.0 &&
                        Math.Abs(floatFx.OuterFrame.Width - want) < 1.5;

                    void SetMode(bool collapsed, string layout)
                    {
                        reg.UserConfig.FloatingLayoutMode = layout;
                        reg.UserConfig.FloatingWindowCollapsed = collapsed;
                        floatFx.ApplyCollapsedVisual(collapsed, preserveRightAnchor: false);
                        floatFx.RefreshOverlayData(force: true);
                        floatFx.UpdateLayout();
                    }

                    bool UltraBadgesInside(out string detail)
                    {
                        detail = "";
                        foreach (var row in floatFx.FloatingAgentsStack.Children.OfType<FrameworkElement>())
                        {
                            if (row.Tag is not string) continue;
                            foreach (var pill in CollectLogicalAndVisual<System.Windows.Controls.Border>(row).Where(b => b.Name == "StatusBadgePill"))
                            {
                                if (!pill.IsVisible || pill.ActualWidth <= 0) continue;
                                double right = pill.TransformToAncestor(row).Transform(new Point(pill.ActualWidth, 0)).X;
                                double winRight = pill.TransformToAncestor(floatFx).Transform(new Point(pill.ActualWidth, 0)).X;
                                if (right > row.ActualWidth + 0.5 || winRight > floatFx.ActualWidth + 0.5)
                                {
                                    detail = $"{row.Tag}: pillRight={right:F1}/row={row.ActualWidth:F1}, winRight={winRight:F1}/win={floatFx.ActualWidth:F1}";
                                    return false;
                                }
                            }
                        }
                        return true;
                    }

                    SetMode(true, "ultra");
                    bool s1 = WidthMatches(rtCollapsedW);
                    string w1 = $"{floatFx.Width:F1}/{floatFx.ActualWidth:F1}";

                    SetMode(false, "ultra");
                    bool s2 = WidthMatches(ultraW) && floatFx.CurrentCompactModeForTest == FloatingOverlayWindow.FloatingCompactMode.Ultra;
                    bool s2Badges = UltraBadgesInside(out string badgeDetail);
                    string w2 = $"{floatFx.Width:F1}/{floatFx.ActualWidth:F1}";

                    SetMode(false, "full");
                    bool s3 = WidthMatches(fullW);
                    string w3 = $"{floatFx.Width:F1}/{floatFx.ActualWidth:F1}";

                    SetMode(true, "ultra");
                    bool s4 = WidthMatches(rtCollapsedW);
                    string w4 = $"{floatFx.Width:F1}/{floatFx.ActualWidth:F1}";

                    floatFx.ToggleCollapseState();
                    floatFx.RefreshOverlayData(force: true);
                    floatFx.UpdateLayout();
                    bool s5 = !reg.UserConfig.FloatingWindowCollapsed && WidthMatches(ultraW) && UltraBadgesInside(out _);
                    string w5 = $"{floatFx.Width:F1}/{floatFx.ActualWidth:F1}";

                    AssertTrue(
                        s1 && s2 && s2Badges && s3 && s4 && s5,
                        $"悬浮窗真实 ActualWidth 往返切换精确生效且 Ultra 状态胶囊零裁切 (Collapsed={w1}→Ultra={w2}→Full={w3}→Collapsed={w4}→Toggle={w5}, 目标 {rtCollapsedW}/{ultraW}/{fullW}{(s2Badges ? "" : ", " + badgeDetail)})");
                }
                finally
                {
                    reg.UserConfig.FloatingCustomWidth = rtOrigCustomW;
                    reg.UserConfig.FloatingCollapsedCustomWidth = rtOrigCollapsedW;
                    reg.UserConfig.FloatingWindowCollapsed = rtOrigCollapsed;
                    reg.UserConfig.FloatingLayoutMode = rtOrigLayout;
                    reg.UserConfig.FloatingCustomHeight = rtOrigCustomH;
                    reg.UserConfig.FloatingUltraCustomWidth = rtOrigUltraW;
                    floatFx.ApplyCollapsedVisual(rtOrigCollapsed, preserveRightAnchor: false);
                }
            }

            floatFx.ResetAdaptiveHeightForTest();
            floatFx.RefreshOverlayData(force: true);
            AssertTrue(
                reg.UserConfig.FloatingCustomHeight == null &&
                floatFx.CurrentCompactModeForTest == FloatingOverlayWindow.FloatingCompactMode.Full &&
                Math.Abs(floatFx.Width - (reg.UserConfig.FloatingCustomWidth ?? FloatingOverlayWindow.ExpandedWidthDefault)) < 1.0,
                $"恢复默认自适应高度后 FloatingCustomHeight 为空且维持 Full 档 (Mode={floatFx.CurrentCompactModeForTest}, W={floatFx.Width})");

            AssertTrue(
                new UserHubConfig().CompletionToastDurationSeconds == 8 &&
                UserHubConfig.NormalizeCompletionToastDuration(0) == 8 &&
                UserHubConfig.NormalizeCompletionToastDuration(1) == 3 &&
                UserHubConfig.NormalizeCompletionToastDuration(99) == 30 &&
                UserHubConfig.NormalizeCompletionToastDuration(8) == 8,
                "完成提示时长默认 8 秒，NormalizeCompletionToastDuration 边界 0->8、1->3、99->30");
            AssertTrue(
                fxHost.FindName("ComboCompletionToastDuration") is System.Windows.Controls.ComboBox toastCombo &&
                toastCombo.Items.Count >= 5 &&
                fxHost.FindName("BtnPreviewCompletionBubble") is System.Windows.Controls.Button,
                "设置页存在 ComboCompletionToastDuration 与 BtnPreviewCompletionBubble");

            {
                bool bakCol = reg.UserConfig.FloatingWindowCollapsed;
                string bakMode = reg.UserConfig.FloatingLayoutMode ?? "auto";
                double? bakW = reg.UserConfig.FloatingCustomWidth;
                double? bakUw = reg.UserConfig.FloatingUltraCustomWidth;
                double? bakH = reg.UserConfig.FloatingCustomHeight;
                double? bakCw = reg.UserConfig.FloatingCollapsedCustomWidth;
                try
                {
                    foreach (string mode in new[] { "collapsed", "ultra", "compact", "full" })
                    {
                        reg.UserConfig.FloatingCustomWidth = null;
                        reg.UserConfig.FloatingUltraCustomWidth = null;
                        reg.UserConfig.FloatingCustomHeight = null;
                        reg.UserConfig.FloatingCollapsedCustomWidth = null;
                        if (mode == "collapsed")
                        {
                            reg.UserConfig.FloatingWindowCollapsed = true;
                            reg.UserConfig.FloatingLayoutMode = "full";
                        }
                        else
                        {
                            reg.UserConfig.FloatingWindowCollapsed = false;
                            reg.UserConfig.FloatingLayoutMode = mode;
                        }
                        floatFx.ApplyCollapsedVisual(reg.UserConfig.FloatingWindowCollapsed, preserveRightAnchor: false);
                        floatFx.RefreshOverlayData(force: true);
                        floatFx.UpdateLayout();
                        bool shown = floatFx.TriggerVerifyBannerForTest("任务名", "antigravity", "cursor", "完成要点");
                        string title = floatFx.VerifyCelebrationTitle.Text ?? "";
                        string body = floatFx.VerifyCelebrationContent.Text ?? "";
                        double bw = floatFx.VerifyCelebrationBanner.Width;
                        bool separated =
                            !string.IsNullOrWhiteSpace(title) &&
                            !string.IsNullOrWhiteSpace(body) &&
                            !string.Equals(title.Trim(), body.Trim(), StringComparison.Ordinal) &&
                            title.IndexOf(body, StringComparison.Ordinal) < 0 &&
                            body.IndexOf(title, StringComparison.Ordinal) < 0;
                        bool widthOk = bw >= 290 && bw <= 380;
                        bool notSqueezed = mode != "collapsed" || bw >= floatFx.Width + 30;
                        AssertTrue(
                            shown && separated && widthOk && notSqueezed,
                            $"完成气泡 {mode} 标题内容分离且不被胶囊压窄 (title={title}, body={body}, bw={bw:0}, win={floatFx.Width:0}, modeNow={floatFx.CurrentCompactModeForTest})");
                    }
                }
                finally
                {
                    reg.UserConfig.FloatingWindowCollapsed = bakCol;
                    reg.UserConfig.FloatingLayoutMode = bakMode;
                    reg.UserConfig.FloatingCustomWidth = bakW;
                    reg.UserConfig.FloatingUltraCustomWidth = bakUw;
                    reg.UserConfig.FloatingCustomHeight = bakH;
                    reg.UserConfig.FloatingCollapsedCustomWidth = bakCw;
                    floatFx.ApplyCollapsedVisual(bakCol, preserveRightAnchor: false);
                    floatFx.RefreshOverlayData(force: true);
                }
            }

            // ========== v1.9.12 断言：悬浮窗去主面板按钮 / 底部外置完成提示 / 6套全局主题 / 配置方案新建复制改名及全模型原子切换 ==========
            AssertTrue(
                floatFx.OpenMainBtn.Visibility == Visibility.Collapsed &&
                floatFx.CapsuleMainBtn.Visibility == Visibility.Collapsed &&
                floatFx.ResizeThumbRight.Visibility == Visibility.Collapsed &&
                floatFx.ResizeThumbBottom.Visibility == Visibility.Collapsed &&
                floatFx.FindName("HideFloatBtn") == null &&
                floatFx.VerifyCelebrationPopup != null,
                "悬浮窗已彻底移除打开主面板按钮、右上角关闭✕与四向边缘缩放抓手，且完成提示已移至底部独立 Popup");

            AssertTrue(
                ThemeService.Catalog.Count == 6 &&
                ThemeService.Resolve("obsidian_gold").Id == "obsidian_gold" &&
                ThemeService.Resolve("crimson_carbon").Id == "crimson_carbon",
                $"全局颜色主题目录完整 (Count={ThemeService.Catalog.Count})");
            ThemeService.ApplyGlobalTheme("obsidian_gold");
            var goldAccent = (Application.Current?.Resources["AccentBlueBrush"] as SolidColorBrush)?.Color.ToString() ?? "";
            ThemeService.ApplyGlobalTheme("dark_slate");
            AssertTrue(
                goldAccent.EndsWith("FBBF24", StringComparison.OrdinalIgnoreCase),
                $"全局动态主题切换生效 (obsidian_gold Accent={goldAccent})");

            var cursorAgentForProfile = reg.UserConfig.Agents.First(a => a.Id == "cursor");
            string origCursorHigh = cursorAgentForProfile.HighTierModel;
            var createdProf = reg.CreateNewTeamProfile("自检新建方案A", "测试全模型跟随方案切换");
            cursorAgentForProfile.HighTierModel = "Claude Opus Test-A";
            cursorAgentForProfile.MediumTierModel = "Claude Sonnet Test-A";
            cursorAgentForProfile.LowTierModel = "Composer Test-A";
            reg.ApplyTeamProfile(createdProf, saveToDisk: false);
            cursorAgentForProfile.HighTierModel = "Claude Opus Test-A";
            cursorAgentForProfile.MediumTierModel = "Claude Sonnet Test-A";
            cursorAgentForProfile.LowTierModel = "Composer Test-A";
            reg.SyncActiveTeamProfileWithCurrentState(saveToDisk: false);

            var dupProf = reg.DuplicateTeamProfile(createdProf.Id, "自检复制方案B");
            AssertTrue(dupProf != null && reg.RenameTeamProfile(dupProf.Id, "自检改名方案B+", "已改名说明"),
                "配置方案支持新建、复制与修改名称/说明");
            dupProf!.AgentSnapshots.First(s => s.AgentId == "cursor").HighTierModel = "GPT-5.4 Test-B";
            reg.ApplyTeamProfile(dupProf, saveToDisk: false);
            string switchedHighB = reg.UserConfig.Agents.First(a => a.Id == "cursor").HighTierModel;
            reg.ApplyTeamProfile(createdProf, saveToDisk: false);
            string switchedBackA = reg.UserConfig.Agents.First(a => a.Id == "cursor").HighTierModel;
            AssertTrue(
                switchedHighB == "GPT-5.4 Test-B" && switchedBackA == "Claude Opus Test-A",
                $"所有模型配置均随配置方案原子切换 (B={switchedHighB}, A={switchedBackA})");
            cursorAgentForProfile.HighTierModel = origCursorHigh;

            var (profMgrWin, _) = Dialogs.BuildTeamProfileManagerDialogWindow(null, reg, new CloudRecommendationService());
            profMgrWin.WindowStartupLocation = WindowStartupLocation.Manual;
            profMgrWin.Left = -32000;
            profMgrWin.Top = -32000;
            profMgrWin.ShowActivated = false;
            profMgrWin.Show();
            profMgrWin.UpdateLayout();
            (profMgrWin.Tag as Action<int, string?>)?.Invoke(0, "cursor");
            profMgrWin.UpdateLayout();
            (profMgrWin.Tag as Action<int, string?>)?.Invoke(1, null);
            profMgrWin.UpdateLayout();
            AssertTrue(profMgrWin.Content != null, "统一配置方案管理弹窗（含原地展开抽屉与12类路由分页）构建与布局自检通过");
            profMgrWin.Close();

            // ========== v1.9.13 断言：主界面只读总览收口 / CLI 智能只读与有更新才可点 / 未安装禁提 CLI / Workspace Trust 自动通行 ==========
            static List<T> CollectLogicalAndVisual<T>(DependencyObject? root) where T : DependencyObject
            {
                var found = new List<T>();
                if (root == null) return found;
                var visited = new HashSet<DependencyObject>();
                void Walk(DependencyObject node)
                {
                    if (!visited.Add(node)) return;
                    if (node is T matched) found.Add(matched);
                    int vCount = 0;
                    try { vCount = VisualTreeHelper.GetChildrenCount(node); } catch { }
                    for (int i = 0; i < vCount; i++)
                        Walk(VisualTreeHelper.GetChild(node, i));
                    foreach (var child in LogicalTreeHelper.GetChildren(node))
                    {
                        if (child is DependencyObject d) Walk(d);
                    }
                }
                Walk(root);
                return found;
            }

            fxHost.SwitchToTabForTest(5);
            fxHost.SwitchSettingsSubViewForTest(false);
            fxHost.UpdateLayout();
            var rosterCheckBoxes = CollectLogicalAndVisual<System.Windows.Controls.CheckBox>(fxHost.DetectedAgentsList);
            var rosterButtons = CollectLogicalAndVisual<System.Windows.Controls.Button>(fxHost.DetectedAgentsList);
            var rosterToggles = CollectLogicalAndVisual<System.Windows.Controls.Primitives.ToggleButton>(fxHost.DetectedAgentsList);
            var workflowToggles = rosterToggles
                .Where(t => t.Tag is string tag && tag.StartsWith("WorkflowToggle:", StringComparison.Ordinal))
                .ToList();
            var undetectedButtons = CollectLogicalAndVisual<System.Windows.Controls.Button>(fxHost.UndetectedAgentsWrap);
            bool hasConfigOrRecBtn = rosterButtons.Any(b =>
                string.Equals(b.Content as string, "配置", StringComparison.Ordinal) ||
                string.Equals(b.Content as string, "推荐", StringComparison.Ordinal));
            var detectedForToggle = reg.UserConfig.Agents.Where(a => a.Detected || string.IsNullOrEmpty(a.DetectorId)).ToList();
            var workflowToggleIds = workflowToggles
                .Select(t => t.Tag as string)
                .Where(tag => !string.IsNullOrWhiteSpace(tag) && tag!.StartsWith("WorkflowToggle:", StringComparison.Ordinal))
                .Select(tag => tag!["WorkflowToggle:".Length..])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool togglesMatchVisible = detectedForToggle.Count > 0 &&
                                       detectedForToggle.All(a => workflowToggleIds.Contains(a.Id));
            var wfMgrByName = fxHost.FindName("WorkflowSwitchManagerBtn") as System.Windows.Controls.Button;
            var filterBarByName = fxHost.FindName("RosterFilterBar") as System.Windows.Controls.WrapPanel;
            fxHost.SwitchSettingsSubViewForTest(true);
            fxHost.UpdateLayout();
            var routeCombos = CollectLogicalAndVisual<System.Windows.Controls.ComboBox>(fxHost.MagpieWorkRoutesPanel);
            AssertTrue(
                rosterCheckBoxes.Count == 0 && !hasConfigOrRecBtn && undetectedButtons.Count == 0 && routeCombos.Count == 0,
                $"主界面 AI 阵容已去掉旧散乱 CheckBox/配置推荐按钮，未安装区零安装按钮 (CheckBox={rosterCheckBoxes.Count}, CfgRecBtn={hasConfigOrRecBtn}, UndetectedBtn={undetectedButtons.Count}, RouteCombo={routeCombos.Count})");
            AssertTrue(
                workflowToggles.Count >= 1 && togglesMatchVisible &&
                workflowToggles.Count >= detectedForToggle.Count,
                $"主界面 WorkflowToggle 胶囊开关匹配当前可见已检出可切换工具 (WorkflowToggle={workflowToggles.Count}, Detected={detectedForToggle.Count}, MatchIds={togglesMatchVisible})");
            AssertTrue(
                wfMgrByName != null && wfMgrByName.IsEnabled &&
                filterBarByName != null && filterBarByName.IsEnabled &&
                fxHost.WorkflowSwitchManagerBtn != null && fxHost.RosterFilterBar != null,
                "顶部「工作流开关」按钮与 RosterFilterBar 筛选条存在且可用");

            // 主流 AI 彻底扫描 + 专用工作流开关全链路（agent / Snapshot / Routing 三端原子同步 + 最后 1 个拦截 + RosterFilter + AppHangB1）
            {
                string[] requiredMainstream =
                {
                    "trae", "kiro", "cline", "claude_desktop", "cherry_studio", "qwen_code", "ollama_local", "lm_studio"
                };
                int mainstreamCnt = DiscoveryService.GetMainstreamCatalogCount(reg);
                var detectorIds = (reg.Detectors.Detectors ?? new List<DetectorSpec>())
                    .Select(d => d.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                bool hasAllRequired = requiredMainstream.All(id => detectorIds.Contains(id));
                AssertTrue(
                    mainstreamCnt >= 28 && hasAllRequired,
                    $"DiscoveryService 主流 AI 目录 >= 28 且含新增主流工具 (实际={mainstreamCnt}, RequiredOk={hasAllRequired})");

                var shareProbe = new ShareExportService();
                var shareProbeZip = Path.Combine(outDir, "share_mainstream_probe.zip");
                shareProbe.ExportCleanPortableZip(shareProbeZip);
                var shareProbeExtract = Path.Combine(outDir, "share_mainstream_probe");
                if (Directory.Exists(shareProbeExtract)) Directory.Delete(shareProbeExtract, recursive: true);
                ZipFile.ExtractToDirectory(shareProbeZip, shareProbeExtract);
                var shareDetPath = Path.Combine(shareProbeExtract, "AI协同中心_绿色版", "registry", "detectors.json");
                AssertTrue(File.Exists(shareDetPath), "绿色版分享包含 registry/detectors.json");
                var shareDetTxt = File.ReadAllText(shareDetPath);
                bool sharePrivacyOk =
                    !shareDetTxt.Contains("F:\\\\AIChat", StringComparison.OrdinalIgnoreCase) &&
                    !shareDetTxt.Contains("F:/AIChat", StringComparison.OrdinalIgnoreCase) &&
                    !Regex.IsMatch(shareDetTxt, @"[A-Za-z]:\\\\Users\\\\[^\\%""]+\\\\(?!AppData)");
                bool shareHasAll = requiredMainstream.All(id =>
                    shareDetTxt.Contains($"\"id\": \"{id}\"", StringComparison.OrdinalIgnoreCase) ||
                    shareDetTxt.Contains($"\"id\":\"{id}\"", StringComparison.OrdinalIgnoreCase));
                AssertTrue(
                    sharePrivacyOk && shareHasAll,
                    $"绿色版 detectors.json 隐私静态校验通过且含全部主流工具 (PrivacyOk={sharePrivacyOk}, HasAll={shareHasAll})");

                var bakEnabledMap = reg.UserConfig.Agents.ToDictionary(
                    a => a.Id, a => a.Enabled, StringComparer.OrdinalIgnoreCase);
                try
                {
                    // 确保至少 2 个已启用，便于三端同步与最后 1 个拦截测试
                    var switchable = reg.UserConfig.Agents
                        .Where(a => a.Detected || string.IsNullOrEmpty(a.DetectorId))
                        .ToList();
                    foreach (var a in switchable.Take(2))
                    {
                        if (!a.Enabled) reg.SetAgentWorkflowEnabled(a.Id, true);
                    }

                    var worker = switchable.FirstOrDefault(a => a.Enabled &&
                                                               !string.Equals(a.Id, "antigravity", StringComparison.OrdinalIgnoreCase))
                                 ?? switchable.First(a => a.Enabled);
                    string workerId = worker.Id;
                    var activeProf = reg.UserConfig.TeamProfiles?.FirstOrDefault(p =>
                        string.Equals(p.Id, reg.UserConfig.ActiveTeamProfileId, StringComparison.OrdinalIgnoreCase));

                    var off = reg.SetAgentWorkflowEnabled(workerId, false);
                    AssertTrue(off.Ok && !worker.Enabled, $"WorkflowToggle 关闭成功 ({workerId})");
                    var snapOff = activeProf?.AgentSnapshots?.FirstOrDefault(s =>
                        string.Equals(s.AgentId, workerId, StringComparison.OrdinalIgnoreCase));
                    AssertTrue(snapOff != null && !snapOff.Enabled, "关闭后当前方案 AgentSnapshot.Enabled 已同步为 false");
                    var enabledIdsOff = reg.UserConfig.Agents.Where(a => a.Enabled)
                        .Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    AssertTrue(!enabledIdsOff.Contains(workerId), "关闭后 agent.Enabled 已为 false");
                    int routeLeakOff = 0;
                    foreach (var wid in WorkSpecialtyCatalog.OrderedItems.Select(w => w.Id).Take(6))
                    {
                        var cands = RoutingService.ListCapableRouteCandidatesForOverview(reg, reg.GetActiveProject(), wid);
                        if (cands.Any(c => !enabledIdsOff.Contains(c.Agent.Id))) routeLeakOff++;
                        if (cands.Any(c => string.Equals(c.Agent.Id, workerId, StringComparison.OrdinalIgnoreCase)))
                            routeLeakOff++;
                    }
                    AssertTrue(routeLeakOff == 0, $"关闭后 RoutingService 候选集合已剔除该工具且仅含 Enabled (Leak={routeLeakOff})");

                    var on = reg.SetAgentWorkflowEnabled(workerId, true);
                    AssertTrue(on.Ok && worker.Enabled, $"WorkflowToggle 重新开启成功 ({workerId})");
                    var snapOn = activeProf?.AgentSnapshots?.FirstOrDefault(s =>
                        string.Equals(s.AgentId, workerId, StringComparison.OrdinalIgnoreCase));
                    AssertTrue(snapOn != null && snapOn.Enabled, "开启后当前方案 AgentSnapshot.Enabled 已同步为 true");
                    var enabledIdsOn = reg.UserConfig.Agents.Where(a => a.Enabled)
                        .Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    AssertTrue(enabledIdsOn.Contains(workerId), "开启后 agent.Enabled 已为 true");
                    int routeMissOn = 0;
                    foreach (var wid in WorkSpecialtyCatalog.OrderedItems.Select(w => w.Id).Take(6))
                    {
                        var cands = RoutingService.ListCapableRouteCandidatesForOverview(reg, reg.GetActiveProject(), wid);
                        if (cands.Any(c => !enabledIdsOn.Contains(c.Agent.Id))) routeMissOn++;
                    }
                    AssertTrue(routeMissOn == 0, $"开启后 RoutingService 候选集合与 Enabled 池一致 (Leak={routeMissOn})");

                    // 关到只剩最后 1 个，再关必须被拦截
                    var stillOn = reg.UserConfig.Agents.Where(a => a.Enabled).ToList();
                    var keepLast = stillOn.First();
                    foreach (var a in stillOn.Where(a => a.Id != keepLast.Id).ToList())
                        reg.SetAgentWorkflowEnabled(a.Id, false);
                    AssertTrue(reg.UserConfig.Agents.Count(a => a.Enabled) == 1, "已收敛到仅 1 个工作流工具");
                    var lastGuard = reg.SetAgentWorkflowEnabled(keepLast.Id, false);
                    AssertTrue(
                        !lastGuard.Ok && keepLast.Enabled && lastGuard.Enabled &&
                        reg.UserConfig.Agents.Count(a => a.Enabled) == 1,
                        $"禁止把最后一个工具关成 0 个 (Ok={lastGuard.Ok}, Enabled={keepLast.Enabled})");
                }
                finally
                {
                    // 先全部开启应开启的，再关闭应关闭的，避免恢复过程误触最后 1 个拦截
                    foreach (var kv in bakEnabledMap.Where(kv => kv.Value))
                        reg.SetAgentWorkflowEnabled(kv.Key, true);
                    foreach (var kv in bakEnabledMap.Where(kv => !kv.Value))
                        reg.SetAgentWorkflowEnabled(kv.Key, false);
                    fxHost.RefreshAllViews();
                }

                // RosterFilter 切换 + 含 6 通道索引刷新的 AppHangB1 隔离测验
                fxHost.SwitchToTabForTest(5);
                fxHost.SwitchSettingsSubViewForTest(false);
                fxHost.UpdateLayout();
                var filterSw = System.Diagnostics.Stopwatch.StartNew();
                void ClickRosterFilter(string mode)
                {
                    var btn = CollectLogicalAndVisual<System.Windows.Controls.Button>(fxHost.RosterFilterBar)
                        .FirstOrDefault(b => b.Tag is string t &&
                                             string.Equals(t, "RosterFilter:" + mode, StringComparison.Ordinal));
                    AssertTrue(btn != null, $"RosterFilterBar 存在筛选芯片 RosterFilter:{mode}");
                    btn!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    fxHost.UpdateLayout();
                    // 顺带触达路由 6 通道候选索引，验证不卡顿
                    foreach (var wid in WorkSpecialtyCatalog.OrderedItems.Select(w => w.Id).Take(6))
                        _ = RoutingService.ListCapableRouteCandidatesForOverview(reg, reg.GetActiveProject(), wid);
                }
                ClickRosterFilter("enabled");
                int enabledListCount = CollectLogicalAndVisual<System.Windows.Controls.Primitives.ToggleButton>(fxHost.DetectedAgentsList)
                    .Count(t => t.Tag is string tag && tag.StartsWith("WorkflowToggle:", StringComparison.Ordinal));
                AssertTrue(enabledListCount >= 1, $"RosterFilter:enabled 列表刷新正常 (Toggle={enabledListCount})");
                ClickRosterFilter("catalog");
                int catalogListCount = CollectLogicalAndVisual<System.Windows.Controls.Primitives.ToggleButton>(fxHost.DetectedAgentsList)
                    .Count(t => t.Tag is string tag && tag.StartsWith("WorkflowToggle:", StringComparison.Ordinal));
                AssertTrue(catalogListCount >= enabledListCount, $"RosterFilter:catalog 列表刷新正常 (Toggle={catalogListCount})");
                ClickRosterFilter("all");
                filterSw.Stop();
                AssertTrue(
                    filterSw.ElapsedMilliseconds < 450,
                    $"RosterFilter 切换与 6 通道索引刷新满足 AppHangB1 (<450ms, 实际={filterSw.ElapsedMilliseconds}ms)");
            }

            // registry/detectors.json 本机活口隐私静态校验
            var detJsonPath = Path.Combine(AppPaths.RegistryDir, "detectors.json");
            var detTxt = File.ReadAllText(detJsonPath);
            AssertTrue(
                !detTxt.Contains("F:\\\\AIChat", StringComparison.OrdinalIgnoreCase) &&
                !detTxt.Contains("F:/AIChat", StringComparison.OrdinalIgnoreCase) &&
                !Regex.IsMatch(detTxt, @"[A-Za-z]:\\\\Users\\\\[^\\%""]+\\\\(?!AppData)"),
                "registry/detectors.json 通过隐私/静态校验（无个人私有绝对路径）");

            // 路由总览多候选胶囊：PreferNative + Cursor 攻坚模型时仍须展示 Cursor，且胶囊不写 AI 名
            {
                bool bakPreferNative = reg.UserConfig.PreferNativeModelsWhenTied;
                var agAgent = reg.UserConfig.Agents.First(a => a.Id == "antigravity");
                var curAgent = reg.UserConfig.Agents.First(a => a.Id == "cursor");
                var bakAgEnabled = agAgent.Enabled;
                var bakCurEnabled = curAgent.Enabled;
                var bakAgProfiles = agAgent.ModelProfiles?.Select(m => m.Clone()).ToList() ?? new List<ModelWorkProfile>();
                var bakCurProfiles = curAgent.ModelProfiles?.Select(m => m.Clone()).ToList() ?? new List<ModelWorkProfile>();
                var bakAgRoles = new List<string>(agAgent.PrimaryWorkRoles ?? new());
                var bakCurRoles = new List<string>(curAgent.PrimaryWorkRoles ?? new());
                var bakAgTags = new Dictionary<string, int>(agAgent.TagScores ?? new(), StringComparer.OrdinalIgnoreCase);
                var bakCurTags = new Dictionary<string, int>(curAgent.TagScores ?? new(), StringComparer.OrdinalIgnoreCase);
                try
                {
                    reg.UserConfig.PreferNativeModelsWhenTied = true;
                    agAgent.Enabled = true;
                    curAgent.Enabled = true;
                    var allRoles = WorkSpecialtyCatalog.OrderedItems.Select(w => w.Id).ToList();
                    agAgent.PrimaryWorkRoles = new List<string>(allRoles);
                    curAgent.PrimaryWorkRoles = new List<string>(allRoles);
                    agAgent.TagScores ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    curAgent.TagScores ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var wid in allRoles)
                    {
                        agAgent.TagScores[wid] = 5;
                        curAgent.TagScores[wid] = 5;
                    }

                    var agModel = new ModelWorkProfile
                    {
                        ModelName = "Orchestrator (Neon)",
                        Enabled = true,
                        OwnershipKind = "native",
                        UsagePolicy = "primary_default",
                        RecommendedEffort = "high"
                    };
                    var curAuto = new ModelWorkProfile
                    {
                        ModelName = "Auto (default)",
                        Enabled = true,
                        OwnershipKind = "native",
                        UsagePolicy = "light_fast_first",
                        RecommendedEffort = "medium"
                    };
                    var curOpus = new ModelWorkProfile
                    {
                        ModelName = "Claude Opus 5.5 (claude-opus-5-5)",
                        Enabled = true,
                        OwnershipKind = "third_party",
                        UsagePolicy = "complex_or_rework_only",
                        RecommendedEffort = "high"
                    };
                    foreach (var wid in allRoles)
                    {
                        agModel.SetCapabilityScore(wid, 5);
                        curAuto.SetCapabilityScore(wid, 3);
                        curOpus.SetCapabilityScore(wid, 5);
                    }
                    agAgent.ModelProfiles = new List<ModelWorkProfile> { agModel };
                    curAgent.ModelProfiles = new List<ModelWorkProfile> { curAuto, curOpus };

                    // 服务层：每类至少 2 个具备能力的候选，且含 Cursor
                    int svcMulti = 0;
                    int svcHasCursor = 0;
                    foreach (var wid in allRoles)
                    {
                        var cands = RoutingService.ListCapableRouteCandidatesForOverview(reg, reg.GetActiveProject(), wid);
                        if (cands.Count >= 2) svcMulti++;
                        if (cands.Any(c => c.Agent.Id.Equals("cursor", StringComparison.OrdinalIgnoreCase))) svcHasCursor++;
                    }

                    fxHost.SwitchToTabForTest(5);
                    fxHost.SwitchSettingsSubViewForTest(true);
                    fxHost.UpdateLayout();

                    var rowBorders = fxHost.MagpieWorkRoutesPanel.Children.OfType<System.Windows.Controls.Border>()
                        .Where(b => string.Equals(b.Tag as string, "route-overview-row", StringComparison.Ordinal))
                        .ToList();
                    int rowsOk = 0;
                    int multiCapsuleRows = 0;
                    int cursorCapsuleRows = 0;
                    int nameLeak = 0;
                    int missingTip = 0;
                    string[] forbiddenNames = { "Antigravity", "Cursor", "Grok Bot", "Codex", "Claude Code" };
                    foreach (var row in rowBorders)
                    {
                        var wraps = CollectLogicalAndVisual<System.Windows.Controls.WrapPanel>(row)
                            .Where(w => string.Equals(w.Tag as string, "route-overview-candidates", StringComparison.Ordinal))
                            .ToList();
                        var capsules = wraps.SelectMany(w => w.Children.OfType<System.Windows.Controls.Border>()
                            .Where(c => (c.Tag as string)?.StartsWith("route-cand:", StringComparison.Ordinal) == true))
                            .ToList();
                        if (capsules.Count == 0) continue;
                        rowsOk++;
                        if (capsules.Count >= 2) multiCapsuleRows++;
                        if (capsules.Any(c => (c.Tag as string)?.Contains("cursor", StringComparison.OrdinalIgnoreCase) == true))
                            cursorCapsuleRows++;
                        foreach (var cap in capsules)
                        {
                            if (cap.ToolTip == null) missingTip++;
                            foreach (var tb in CollectLogicalAndVisual<System.Windows.Controls.TextBlock>(cap))
                            {
                                var t = tb.Text ?? "";
                                if (forbiddenNames.Any(n => t.Equals(n, StringComparison.OrdinalIgnoreCase) ||
                                                           t.Contains(n, StringComparison.OrdinalIgnoreCase)))
                                    nameLeak++;
                            }
                        }
                    }

                    AssertTrue(
                        svcMulti == 12 && svcHasCursor == 12 &&
                        rowsOk == 12 && multiCapsuleRows == 12 && cursorCapsuleRows == 12 &&
                        nameLeak == 0 && missingTip == 0,
                        $"路由总览多候选胶囊自检通过 (SvcMulti={svcMulti}/12, SvcCursor={svcHasCursor}/12, Rows={rowsOk}, Multi={multiCapsuleRows}, CursorCaps={cursorCapsuleRows}, NameLeak={nameLeak}, MissingTip={missingTip})");
                }
                finally
                {
                    reg.UserConfig.PreferNativeModelsWhenTied = bakPreferNative;
                    agAgent.Enabled = bakAgEnabled;
                    curAgent.Enabled = bakCurEnabled;
                    agAgent.ModelProfiles = bakAgProfiles;
                    curAgent.ModelProfiles = bakCurProfiles;
                    agAgent.PrimaryWorkRoles = bakAgRoles;
                    curAgent.PrimaryWorkRoles = bakCurRoles;
                    agAgent.TagScores = bakAgTags;
                    curAgent.TagScores = bakCurTags;
                    fxHost.SwitchToTabForTest(5);
                    fxHost.UpdateLayout();
                }
            }

            // 五异构 AI（含托管 Sync 保能力 / 未扫描自定义 / 陈旧 PrimaryWorkRoles + 新 CapabilityScores）路由总览必显自检
            {
                var ag = reg.UserConfig.Agents.First(a => a.Id == "antigravity");
                var cur = reg.UserConfig.Agents.First(a => a.Id == "cursor");
                var grok = reg.UserConfig.Agents.FirstOrDefault(a => a.Id == "grok_bot");
                var codex = reg.UserConfig.Agents.FirstOrDefault(a => a.Id == "codex_cli")
                            ?? reg.UserConfig.Agents.FirstOrDefault(a => a.Id == "windsurf");
                AssertTrue(grok != null && codex != null, "五异构 AI 自检需要 grok_bot 与 codex_cli/windsurf 花名册条目");

                var bakAgents = reg.UserConfig.Agents.ToList();
                AgentRosterEntry? customAdded = null;
                var bakStates = new Dictionary<string, (bool En, bool Det, List<string> Roles, List<ModelWorkProfile> Profiles, Dictionary<string, int> Tags)>(StringComparer.OrdinalIgnoreCase);
                void Backup(AgentRosterEntry a) => bakStates[a.Id] = (
                    a.Enabled, a.Detected,
                    new List<string>(a.PrimaryWorkRoles ?? new()),
                    a.ModelProfiles?.Select(m => m.Clone()).ToList() ?? new List<ModelWorkProfile>(),
                    new Dictionary<string, int>(a.TagScores ?? new(), StringComparer.OrdinalIgnoreCase));

                try
                {
                    Backup(ag); Backup(cur); Backup(grok!); Backup(codex!);
                    string focusWork = "fast_execution";

                    void ArmAgent(AgentRosterEntry a, string modelName, bool detected, params string[] roles)
                    {
                        a.Enabled = true;
                        a.Detected = detected;
                        a.PrimaryWorkRoles = new List<string>(roles);
                        a.TagScores ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        foreach (var r in roles) a.TagScores[r] = 4;
                        var mp = new ModelWorkProfile
                        {
                            ModelName = modelName,
                            Enabled = true,
                            OwnershipKind = "native",
                            UsagePolicy = "primary_default",
                            RecommendedEffort = "medium"
                        };
                        foreach (var r in roles) mp.SetCapabilityScore(r, 4);
                        a.ModelProfiles = new List<ModelWorkProfile> { mp };
                    }

                    ArmAgent(ag, "Orchestrator (Neon)", true, "brain_analysis", focusWork, "visual_accept");
                    ArmAgent(cur, "Auto (default)", true, focusWork, "ui_shader_vfx");
                    ArmAgent(codex!, "gpt-5.3-codex", true, focusWork, "build_testing");

                    // Grok Bot 托管模型：配置 CapabilityScores 后 Sync，能力必须保留且必显
                    ArmAgent(grok!, LocalModelCatalogService.ManagedDefaultModelDisplay, true, "text_writing", "image_generation", focusWork);
                    grok!.ModelProfiles[0].SetCapabilityScore(focusWork, 5);
                    grok.ModelProfiles[0].SetCapabilityScore("ui_shader_vfx", 3);
                    LocalModelCatalogService.SyncAgentWithLocalClientModels(grok, forceOverwriteTiers: false);
                    AssertTrue(
                        grok.ModelProfiles.Count == 1 &&
                        grok.ModelProfiles[0].GetCapabilityScore(focusWork) >= 5 &&
                        grok.ModelProfiles[0].CapabilityScores != null &&
                        grok.ModelProfiles[0].CapabilityScores.GetValueOrDefault(focusWork) >= 5 &&
                        (grok.TagScores?.GetValueOrDefault(focusWork) ?? 0) >= 5,
                        $"Grok Bot Sync 后 CapabilityScores/TagScores 不得被清空 (fe={grok.ModelProfiles[0].GetCapabilityScore(focusWork)}, tag={grok.TagScores?.GetValueOrDefault(focusWork) ?? 0})");

                    // 陈旧 PrimaryWorkRoles 仅 text_writing；启用模型配置 fast_execution=4★ / ui_shader_vfx=4★；Detected=false
                    customAdded = new AgentRosterEntry
                    {
                        Id = "custom_vps_ai",
                        DetectorId = "vps_remote",
                        Name = "Custom VPS AI",
                        Category = "external",
                        Enabled = true,
                        Detected = false,
                        PrimaryWorkRoles = new List<string> { "text_writing" },
                        TagScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["text_writing"] = 3 },
                        ModelProfiles = new List<ModelWorkProfile>()
                    };
                    var customMp = new ModelWorkProfile
                    {
                        ModelName = "Remote-Opus",
                        Enabled = true,
                        OwnershipKind = "third_party",
                        UsagePolicy = "primary_default",
                        RecommendedEffort = "high"
                    };
                    customMp.SetCapabilityScore(focusWork, 4);
                    customMp.SetCapabilityScore("ui_shader_vfx", 4);
                    customAdded.ModelProfiles.Add(customMp);
                    if (!reg.UserConfig.Agents.Any(x => x.Id.Equals("custom_vps_ai", StringComparison.OrdinalIgnoreCase)))
                        reg.UserConfig.Agents.Add(customAdded);

                    reg.SyncGlobalRoutesAndAgentRoleConsistency(persistIfChanged: false);

                    string[] fiveIds = { "antigravity", "cursor", "grok_bot", codex!.Id, "custom_vps_ai" };
                    AssertTrue(
                        RoutingService.IsAgentEligibleForWorkType(customAdded, focusWork),
                        "陈旧 PrimaryWorkRoles + 新 CapabilityScores 的自定义 AI 不得被 IsAgentEligible 误杀");

                    var cands = RoutingService.ListCapableRouteCandidatesForOverview(reg, reg.GetActiveProject(), focusWork);
                    int hit = fiveIds.Count(id => cands.Any(c => c.Agent.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
                    AssertTrue(hit == 5 && cands.Count >= 5,
                        $"五异构 AI 在 fast_execution 服务层总览全显 (Hit={hit}/5, Count={cands.Count}, Ids={string.Join(",", cands.Select(c => c.Agent.Id))})");

                    fxHost.SwitchToTabForTest(5);
                    fxHost.SwitchSettingsSubViewForTest(true);
                    fxHost.UpdateLayout();

                    var rows = fxHost.MagpieWorkRoutesPanel.Children.OfType<System.Windows.Controls.Border>()
                        .Where(b => string.Equals(b.Tag as string, "route-overview-row", StringComparison.Ordinal))
                        .ToList();
                    int bestRowHits = 0;
                    int nameLeak5 = 0;
                    int missingTip5 = 0;
                    int missingAvatar5 = 0;
                    string[] forbidNames = { "Antigravity", "Cursor", "Grok Bot", "Codex", "Claude Code", "Custom VPS AI", "Windsurf" };
                    foreach (var row in rows)
                    {
                        bool isFocusRow = CollectLogicalAndVisual<System.Windows.Controls.TextBlock>(row).Any(t =>
                            (t.Text ?? "").Contains("代码开发实装", StringComparison.Ordinal) ||
                            (t.Text ?? "").Contains("fast_execution", StringComparison.OrdinalIgnoreCase));
                        var wraps = CollectLogicalAndVisual<System.Windows.Controls.WrapPanel>(row)
                            .Where(w => string.Equals(w.Tag as string, "route-overview-candidates", StringComparison.Ordinal))
                            .ToList();
                        var caps = wraps.SelectMany(w => w.Children.OfType<System.Windows.Controls.Border>()
                            .Where(c => (c.Tag as string)?.StartsWith("route-cand:", StringComparison.Ordinal) == true))
                            .ToList();
                        int rowHit = fiveIds.Count(id => caps.Any(c => (c.Tag as string)?.Contains(id, StringComparison.OrdinalIgnoreCase) == true));
                        if (isFocusRow || rowHit >= 5)
                        {
                            if (rowHit > bestRowHits) bestRowHits = rowHit;
                        }
                        if (rowHit >= 5)
                        {
                            foreach (var cap in caps.Where(c => fiveIds.Any(id => (c.Tag as string)?.Contains(id, StringComparison.OrdinalIgnoreCase) == true)))
                            {
                                if (cap.ToolTip == null) missingTip5++;
                                var avatars = CollectLogicalAndVisual<System.Windows.Controls.Grid>(cap)
                                    .Where(g => (g.Tag as string)?.StartsWith("avatar:", StringComparison.Ordinal) == true)
                                    .ToList();
                                if (avatars.Count == 0) missingAvatar5++;
                                foreach (var tb in CollectLogicalAndVisual<System.Windows.Controls.TextBlock>(cap))
                                {
                                    var t = tb.Text ?? "";
                                    if (forbidNames.Any(n => t.Equals(n, StringComparison.OrdinalIgnoreCase) ||
                                                           t.Contains(n, StringComparison.OrdinalIgnoreCase)))
                                        nameLeak5++;
                                }
                            }
                            break;
                        }
                    }

                    AssertTrue(
                        bestRowHits == 5 && nameLeak5 == 0 && missingTip5 == 0 && missingAvatar5 == 0,
                        $"五异构 AI 胶囊渲染自检通过 (RowHits={bestRowHits}/5, NameLeak={nameLeak5}, MissingTip={missingTip5}, MissingAvatar={missingAvatar5})");
                }
                finally
                {
                    reg.UserConfig.Agents.RemoveAll(a => a.Id.Equals("custom_vps_ai", StringComparison.OrdinalIgnoreCase));
                    foreach (var kv in bakStates)
                    {
                        var a = reg.UserConfig.Agents.FirstOrDefault(x => x.Id.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                        if (a == null) continue;
                        a.Enabled = kv.Value.En;
                        a.Detected = kv.Value.Det;
                        a.PrimaryWorkRoles = kv.Value.Roles;
                        a.ModelProfiles = kv.Value.Profiles;
                        a.TagScores = kv.Value.Tags;
                    }
                    if (bakAgents.Count > 0)
                    {
                        var restored = new List<AgentRosterEntry>();
                        foreach (var old in bakAgents)
                        {
                            if (old.Id.Equals("custom_vps_ai", StringComparison.OrdinalIgnoreCase)) continue;
                            var curAg = reg.UserConfig.Agents.FirstOrDefault(x => x.Id.Equals(old.Id, StringComparison.OrdinalIgnoreCase));
                            restored.Add(curAg ?? old);
                        }
                        reg.UserConfig.Agents = restored;
                    }
                    fxHost.SwitchToTabForTest(5);
                    fxHost.UpdateLayout();
                }
            }

            // 未安装实体不提供 CLI 按钮；已安装无更新为只读徽章，有新版本时才变为可点「⬆ 更新 CLI」
            var fakeUninstalledClaude = new AgentRosterEntry
            {
                Id = "nonexistent_cli_agent",
                DetectorId = "claude_code",
                Name = "Fake Uninstalled",
                Detected = false,
                Enabled = false
            };
            bool offerForFake = CliInstallService.ShouldOfferCliControl(fakeUninstalledClaude);
            var cursorRoster = reg.UserConfig.Agents.First(a => a.Id == "cursor");
            bool origCursorDetected = cursorRoster.Detected;
            cursorRoster.Detected = true;
            try
            {
                CliInstallService.SetMockUpdateStatusForTest("cursor",
                    new CliInstallService.CliUpdateStatus("cursor", true, @"C:\mock\cursor-agent.cmd", "2026.01.01", "2026.01.01", false, DateTime.UtcNow));
                fxHost.SwitchSettingsSubViewForTest(false);
                fxHost.SwitchToTabForTest(5);
                fxHost.UpdateLayout();
                var btnsNoUpd = CollectLogicalAndVisual<System.Windows.Controls.Button>(fxHost.DetectedAgentsList);
                var txtsNoUpd = CollectLogicalAndVisual<System.Windows.Controls.TextBlock>(fxHost.DetectedAgentsList);
                bool hasClickableInstalledBtn = btnsNoUpd.Any(b => string.Equals(b.Content as string, "✓ CLI已装", StringComparison.Ordinal));
                bool hasReadOnlyInstalledBadge = txtsNoUpd.Any(t => string.Equals(t.Text, "CLI", StringComparison.Ordinal));

                CliInstallService.SetMockUpdateStatusForTest("cursor",
                    new CliInstallService.CliUpdateStatus("cursor", true, @"C:\mock\cursor-agent.cmd", "2026.01.01", "2026.11.18", true, DateTime.UtcNow));
                fxHost.SwitchToTabForTest(5);
                fxHost.UpdateLayout();
                var btnsHasUpd = CollectLogicalAndVisual<System.Windows.Controls.Button>(fxHost.DetectedAgentsList);
                bool hasClickableUpdateBtn = btnsHasUpd.Any(b =>
                    b.IsEnabled &&
                    b.Tag is LongActionTracker.LongActionTag updateTag &&
                    updateTag.Key == LongActionTracker.CliUpdate("cursor") &&
                    b.Content is System.Windows.Shapes.Path &&
                    (b.ToolTip as string)?.Contains("新版本可用", StringComparison.Ordinal) == true);

                AssertTrue(
                    !offerForFake && !hasClickableInstalledBtn && hasReadOnlyInstalledBadge && hasClickableUpdateBtn,
                    $"CLI 智能状态机自检通过 (FakeOffer={offerForFake}, ClickableInstalled={hasClickableInstalledBtn}, ReadOnlyBadge={hasReadOnlyInstalledBadge}, UpdateBtn={hasClickableUpdateBtn})");
            }
            finally
            {
                CliInstallService.SetMockUpdateStatusForTest("cursor", null);
                cursorRoster.Detected = origCursorDetected;
            }

            LongActionTracker.ResetForTest();
            bool longActionMutex = false;
            bool longActionLabel = false;
            bool longActionRelease = false;
            try
            {
                bool first = LongActionTracker.TryBegin("cli_update:cursor", "更新中");
                bool second = LongActionTracker.TryBegin("cli_update:cursor", "更新中");
                bool other = LongActionTracker.TryBegin(LongActionTracker.ScanLocal, "扫描中");
                string label = LongActionTracker.FormatBusyLabel("cli_update:cursor");
                longActionMutex = first && !second && other
                    && LongActionTracker.IsBusy("cli_update:cursor")
                    && LongActionTracker.IsBusy(LongActionTracker.ScanLocal);
                longActionLabel = label.Contains('⏳') && Regex.IsMatch(label, @"更新中 \d+s");
                LongActionTracker.End("cli_update:cursor");
                bool afterEnd = !LongActionTracker.IsBusy("cli_update:cursor");
                bool again = LongActionTracker.TryBegin("cli_update:cursor", "更新中");
                longActionRelease = afterEnd && again && LongActionTracker.IsBusy("cli_update:cursor");
            }
            finally
            {
                LongActionTracker.ResetForTest();
            }
            AssertTrue(longActionMutex, "长操作互斥：cli_update:cursor 首次占用，重复 TryBegin 被拒绝，scan_local 互不阻塞");
            AssertTrue(longActionLabel, "进行中 FormatBusyLabel 含 ⏳ 与已耗时秒数");
            AssertTrue(longActionRelease, "End 后 IsBusy 为 false 且可再次 TryBegin");

            var cursorForBusy = reg.UserConfig.Agents.First(a => a.Id == "cursor");
            bool origCursorDetBusy = cursorForBusy.Detected;
            cursorForBusy.Detected = true;
            LongActionTracker.ResetForTest();
            try
            {
                CliInstallService.SetMockUpdateStatusForTest("cursor",
                    new CliInstallService.CliUpdateStatus("cursor", true, @"C:\mock\cursor-agent.cmd", "2026.01.01", "2026.11.18", true, DateTime.UtcNow));
                AssertTrue(LongActionTracker.TryBegin(LongActionTracker.CliUpdate("cursor"), "更新中"), "跨重绘测试前成功占用 cursor CLI 更新锁");
                fxHost.SwitchSettingsSubViewForTest(false);
                fxHost.SwitchToTabForTest(5);
                fxHost.UpdateLayout();
                var busyCliBtns = CollectLogicalAndVisual<System.Windows.Controls.Button>(fxHost.DetectedAgentsList);
                bool keptBusy = busyCliBtns.Any(b =>
                    !b.IsEnabled &&
                    b.Tag is LongActionTracker.LongActionTag busyTag &&
                    busyTag.Key == LongActionTracker.CliUpdate("cursor") &&
                    b.Content is System.Windows.Shapes.Path &&
                    b.ToolTip is string busyTip &&
                    busyTip.Contains('⏳') &&
                    busyTip.Contains("更新中", StringComparison.Ordinal) &&
                    Regex.IsMatch(busyTip, @"\d+s"));
                AssertTrue(keptBusy, "CLI 更新进行中跨重绘仍保持禁用态并显示耗时文案");
                LongActionTracker.End(LongActionTracker.CliUpdate("cursor"));
                fxHost.SwitchToTabForTest(5);
                fxHost.UpdateLayout();
                var restoredCliBtns = CollectLogicalAndVisual<System.Windows.Controls.Button>(fxHost.DetectedAgentsList);
                bool restoredUpdate = restoredCliBtns.Any(b =>
                    b.IsEnabled &&
                    b.Tag is LongActionTracker.LongActionTag restoredTag &&
                    restoredTag.Key == LongActionTracker.CliUpdate("cursor") &&
                    b.Content is System.Windows.Shapes.Path &&
                    (b.ToolTip as string)?.Contains("新版本可用", StringComparison.Ordinal) == true);
                AssertTrue(restoredUpdate, "CLI 更新锁释放后重绘恢复可点的更新按钮");
            }
            finally
            {
                LongActionTracker.ResetForTest();
                CliInstallService.SetMockUpdateStatusForTest("cursor", null);
                cursorForBusy.Detected = origCursorDetBusy;
                fxHost.SwitchSettingsSubViewForTest(false);
                fxHost.SwitchToTabForTest(5);
                fxHost.UpdateLayout();
            }

            // Workspace Trust Required 与无头 CLI 参数构建校验
            var cursorWriteArgs = AutoDispatchService.BuildCursorAgentHeadlessArgs(
                @"C:\Project\Godot\XX",
                new[] { @"C:\Project\Godot\配套工具" },
                autoTrust: true,
                canWriteFiles: true,
                approveMcps: true,
                mode: "fresh",
                resumeSessionId: null,
                modelSlug: "claude-4.5-sonnet");
            var cursorReadOnlyArgs = AutoDispatchService.BuildCursorAgentHeadlessArgs(
                @"C:\Project\Godot\XX",
                null,
                autoTrust: true,
                canWriteFiles: false,
                approveMcps: true,
                mode: "fresh",
                resumeSessionId: null,
                modelSlug: null);
            string sampleTrustErr = "⚠ Workspace Trust Required\nCursor Agent can execute code and access files in this directory.\nDo you trust the contents of this directory? C:\\Project\\Godot\\XX\nTo proceed: Run 'agent' interactively to decide, or pass --trust / -f";
            bool trustDetected = AutoDispatchService.IsInteractiveGateOrTrustError(sampleTrustErr);
            var trustFailure = NetworkResilienceService.ClassifyFailure(1, sampleTrustErr, "", null, false, true);
            AssertTrue(
                reg.UserConfig.AutoTrustManagedWorkspaces &&
                cursorWriteArgs.Contains("--print", StringComparison.Ordinal) &&
                cursorWriteArgs.Contains("--trust", StringComparison.Ordinal) &&
                cursorWriteArgs.Contains("-f", StringComparison.Ordinal) &&
                cursorWriteArgs.Contains("--approve-mcps", StringComparison.Ordinal) &&
                cursorWriteArgs.Contains("--workspace", StringComparison.Ordinal) &&
                cursorWriteArgs.Contains("--add-dir", StringComparison.Ordinal) &&
                !cursorWriteArgs.Contains("--mode plan", StringComparison.Ordinal) &&
                cursorReadOnlyArgs.Contains("--trust", StringComparison.Ordinal) &&
                cursorReadOnlyArgs.Contains("--mode plan", StringComparison.Ordinal) &&
                !cursorReadOnlyArgs.Contains(" -f", StringComparison.Ordinal) &&
                trustDetected &&
                trustFailure.FailureKind == "workspace_trust_required",
                $"Workspace Trust 与无头 CLI 参数自动放行校验通过 (WriteArgs={cursorWriteArgs}, PlanArgs={cursorReadOnlyArgs}, FailureKind={trustFailure.FailureKind})");

            // 全桌面 AI：禁止 --reuse-window 强开普通 IDE；缺分组时窗口内建组失败→无头 CLI 契约
            var cursorAgentsLaunchArgs = AutoDispatchService.BuildCursorAgentsWindowLaunchArgs(@"C:\Project\Godot\XX");
            bool cursorAgentsContractOk = AutoDispatchService.ValidateCursorAgentsNoIdeOpenContract(out string cursorAgentsContractDetail);
            bool allDesktopContractOk = AutoDispatchService.ValidateAllDesktopAgentsNoIdeOpenContract(out string allDesktopContractDetail);
            string autoDispatchSrcPath = Path.Combine(AppPaths.AppBaseDir, "..", "..", "..", "Services", "AutoDispatchService.cs");
            if (!File.Exists(autoDispatchSrcPath))
            {
                autoDispatchSrcPath = @"C:\Project\Godot\配套工具\tools\ai-collab-hub\src\Services\AutoDispatchService.cs";
            }
            string autoDispatchSrc = File.Exists(autoDispatchSrcPath) ? File.ReadAllText(autoDispatchSrcPath) : "";
            bool srcForbidsReuseWindowOpen = autoDispatchSrc.Contains("NO_PROJECT_GROUP_FALLBACK_CLI", StringComparison.Ordinal) &&
                autoDispatchSrc.Contains("Try-EnsureProjectGroupInDesktopApp", StringComparison.Ordinal) &&
                autoDispatchSrc.Contains("Try-CreateDialogInGroup", StringComparison.Ordinal) &&
                autoDispatchSrc.Contains("Try-EnsureCursorProjectGroupInAgents", StringComparison.Ordinal) &&
                autoDispatchSrc.Contains("BuildDesktopAgentWindowLaunchArgs", StringComparison.Ordinal) &&
                autoDispatchSrc.Contains("ResolveDesktopUiAutomationProfile", StringComparison.Ordinal) &&
                autoDispatchSrc.Contains("Close-NativeFolderPickers", StringComparison.Ordinal) &&
                !autoDispatchSrc.Contains("Start-Process -FilePath $cfg.CursorExePath", StringComparison.Ordinal) &&
                !Regex.IsMatch(autoDispatchSrc, @"extraArgs\s*=\s*\$""--reuse-window");

            var cursorProf = AutoDispatchService.ResolveDesktopUiAutomationProfile(new AgentRosterEntry { Id = "cursor", Name = "Cursor" });
            var grokProf = AutoDispatchService.ResolveDesktopUiAutomationProfile(new AgentRosterEntry { Id = "grok_bot", Name = "Grok Bot" });
            var codexProf = AutoDispatchService.ResolveDesktopUiAutomationProfile(new AgentRosterEntry { Id = "codex_cli", Name = "Codex" });
            var claudeProf = AutoDispatchService.ResolveDesktopUiAutomationProfile(new AgentRosterEntry { Id = "claude_code", Name = "Claude Code" });
            var windsurfProf = AutoDispatchService.ResolveDesktopUiAutomationProfile(new AgentRosterEntry { Id = "windsurf", Name = "Windsurf" });
            var antigravityProf = AutoDispatchService.ResolveDesktopUiAutomationProfile(new AgentRosterEntry { Id = "antigravity", Name = "Antigravity" });
            var customDeskProf = AutoDispatchService.ResolveDesktopUiAutomationProfile(new AgentRosterEntry
            {
                Id = "custom_desktop_ai",
                Name = "Custom Desktop AI",
                GuiExePath = @"C:\Tools\CustomAI\CustomAI.exe"
            });
            var myAiDeskProf = AutoDispatchService.ResolveDesktopUiAutomationProfile(new AgentRosterEntry { Id = "custom_desktop_ai", Name = "MyAI Desk" });
            string[] desktopLaunchSamples =
            {
                AutoDispatchService.BuildDesktopAgentWindowLaunchArgs(new AgentRosterEntry { Id = "cursor", Name = "Cursor" }),
                AutoDispatchService.BuildDesktopAgentWindowLaunchArgs(new AgentRosterEntry { Id = "grok_bot", Name = "Grok Bot" }),
                AutoDispatchService.BuildDesktopAgentWindowLaunchArgs(new AgentRosterEntry { Id = "codex_cli", Name = "Codex" }),
                AutoDispatchService.BuildDesktopAgentWindowLaunchArgs(new AgentRosterEntry { Id = "claude_code", Name = "Claude Code" }),
                AutoDispatchService.BuildDesktopAgentWindowLaunchArgs(new AgentRosterEntry { Id = "windsurf", Name = "Windsurf" }),
                AutoDispatchService.BuildDesktopAgentWindowLaunchArgs(new AgentRosterEntry { Id = "antigravity", Name = "Antigravity" }),
                AutoDispatchService.BuildDesktopAgentWindowLaunchArgs(new AgentRosterEntry { Id = "custom_desktop_ai", Name = "MyAI Desk" }),
                AutoDispatchService.BuildDesktopAgentWindowLaunchArgs(null),
                AutoDispatchService.BuildDesktopAgentWindowLaunchArgs(customDeskProf.AgentId == "custom_desktop_ai"
                    ? new AgentRosterEntry { Id = "custom_desktop_ai", Name = "Custom Desktop AI", GuiExePath = @"C:\Tools\CustomAI\CustomAI.exe" }
                    : new AgentRosterEntry { Id = "custom_desktop_ai", Name = "Custom Desktop AI" })
            };
            bool allLaunchForbidReuse = desktopLaunchSamples.All(a =>
                !AutoDispatchService.ContainsForbiddenCursorIdeEditorLaunchArgs(a) &&
                !a.Contains("--reuse-window", StringComparison.OrdinalIgnoreCase));
            bool profileMapOk =
                cursorProf.WinTitle.Equals("Cursor Agents", StringComparison.OrdinalIgnoreCase) &&
                grokProf.WinTitle.Equals("Grok Bot", StringComparison.OrdinalIgnoreCase) && grokProf.IsGrokBotGrouping &&
                codexProf.WinTitle.Equals("Codex", StringComparison.OrdinalIgnoreCase) && !codexProf.IsGrokBotGrouping &&
                claudeProf.WinTitle.IndexOf("Claude", StringComparison.OrdinalIgnoreCase) >= 0 &&
                !claudeProf.WinTitle.Equals("Cursor Agents", StringComparison.OrdinalIgnoreCase) &&
                windsurfProf.WinTitle.Equals("Windsurf", StringComparison.OrdinalIgnoreCase) &&
                !customDeskProf.WinTitle.Equals("Cursor Agents", StringComparison.OrdinalIgnoreCase) &&
                !customDeskProf.WinTitle.Equals("Grok Bot", StringComparison.OrdinalIgnoreCase) &&
                antigravityProf.WinTitle.Equals("Antigravity", StringComparison.OrdinalIgnoreCase) && !antigravityProf.IsGrokBotGrouping &&
                myAiDeskProf.WinTitle.Equals("MyAI Desk", StringComparison.Ordinal) && !myAiDeskProf.IsGrokBotGrouping &&
                cursorProf.GroupCreateMode == "directory_group" && cursorProf.ForbidIdeEditorLaunchArgs &&
                grokProf.GroupCreateMode == "group_or_bot" &&
                codexProf.GroupCreateMode == "directory_group" &&
                claudeProf.GroupCreateMode == "directory_group" && !claudeProf.IsGrokBotGrouping &&
                windsurfProf.GroupCreateMode == "directory_group" && windsurfProf.ForbidIdeEditorLaunchArgs && !windsurfProf.IsGrokBotGrouping &&
                !codexProf.WinTitle.Equals(grokProf.WinTitle, StringComparison.OrdinalIgnoreCase) &&
                !windsurfProf.WinTitle.Equals("Cursor Agents", StringComparison.OrdinalIgnoreCase) &&
                myAiDeskProf.ProcessNames.Count > 0;

            AssertTrue(
                cursorAgentsContractOk &&
                allDesktopContractOk &&
                profileMapOk &&
                allLaunchForbidReuse &&
                !AutoDispatchService.ContainsForbiddenCursorIdeEditorLaunchArgs(cursorAgentsLaunchArgs) &&
                cursorAgentsLaunchArgs.Contains("--suppress-popups-on-startup", StringComparison.OrdinalIgnoreCase) &&
                !cursorAgentsLaunchArgs.Contains(@"C:\Project\Godot\XX", StringComparison.OrdinalIgnoreCase) &&
                AutoDispatchService.CursorMissingProjectGroupFallbackCliToken == "NO_PROJECT_GROUP_FALLBACK_CLI" &&
                srcForbidsReuseWindowOpen,
                $"全桌面AI禁IDE强开与缺分组自建组/建对话回退CLI契约自检通过 ({cursorAgentsContractDetail}; {allDesktopContractDetail}; SrcOk={srcForbidsReuseWindowOpen}, ProfileOk={profileMapOk}, LaunchOk={allLaunchForbidReuse})");

            // 方案导入导出 JSON 与完整字段快照回归校验
            var exportJsonPath = Path.Combine(outDir, "team_profile_export_test.json");
            createdProf.WorkTypeRoutes["ui_shader_vfx"] = new WorkRouteRule
            {
                WorkTypeId = "ui_shader_vfx",
                TargetAgentId = "cursor",
                TargetModelName = "Claude Opus Test-A",
                TargetEffortTier = "high"
            };
            reg.ExportTeamProfileToJson(createdProf, exportJsonPath);
            var importedProf = reg.ImportTeamProfileFromJson(exportJsonPath);
            AssertTrue(
                importedProf != null &&
                importedProf.WorkTypeRoutes.ContainsKey("ui_shader_vfx") &&
                importedProf.WorkTypeRoutes["ui_shader_vfx"].TargetAgentId == "cursor",
                "配置方案 JSON 导出与导入完整保留成员快照与工作路由");

            // ========== v1.9.14 断言：自有/第三方模型识别 · 单模型1~5能力强度与规则路由 · 未启用AI零配置噪音 ==========
            var ownAgyGemini = LocalModelCatalogService.ClassifyModelOwnership("antigravity", "Gemini 4 Carbon");
            var ownAgyClaude = LocalModelCatalogService.ClassifyModelOwnership("antigravity", "Claude Opus 5.5");
            var ownCurComposer = LocalModelCatalogService.ClassifyModelOwnership("cursor", "Composer 2.5 (composer-2.5)");
            var ownCurAuto = LocalModelCatalogService.ClassifyModelOwnership("cursor", "Auto (default)");
            var ownCurClaude = LocalModelCatalogService.ClassifyModelOwnership("cursor", "Claude Opus 5.5 (claude-opus-5-5)");
            var ownCodexGpt = LocalModelCatalogService.ClassifyModelOwnership("codex_cli", "GPT-6-Luna (gpt-6-luna)");
            var ownGrokManaged = LocalModelCatalogService.ClassifyModelOwnership("grok_bot", LocalModelCatalogService.ManagedDefaultModelDisplay);
            AssertTrue(
                ownAgyGemini.IsNativeOwn && ownAgyGemini.VendorName == "Google" &&
                !ownAgyClaude.IsNativeOwn && ownAgyClaude.VendorName == "Anthropic" &&
                ownCurComposer.IsNativeOwn && ownCurAuto.IsNativeOwn &&
                !ownCurClaude.IsNativeOwn && ownCurClaude.VendorName == "Anthropic" &&
                ownCodexGpt.IsNativeOwn && ownCodexGpt.VendorName == "OpenAI" &&
                ownGrokManaged.IsNativeOwn && ownGrokManaged.VendorName == "xAI",
                $"客户端自有/第三方模型分类器精确通过 (AgyGem={ownAgyGemini.BadgeText}, AgyCla={ownAgyClaude.BadgeText}, CurComp={ownCurComposer.BadgeText}, CurCla={ownCurClaude.BadgeText})");

            // 单模型多能力 1~5 级强度与使用规则驱动真实路由选模
            var modelArt = new ModelWorkProfile
            {
                ModelName = "Art-Model-5",
                Enabled = true,
                RecommendedEffort = "medium",
                UsagePolicy = "strict_specialty_only"
            };
            modelArt.SetCapabilityScore("fast_execution", 3);
            modelArt.SetCapabilityScore("image_generation", 5);
            var modelCode = new ModelWorkProfile
            {
                ModelName = "Code-Model-5",
                Enabled = true,
                RecommendedEffort = "high",
                UsagePolicy = "primary_default"
            };
            modelCode.SetCapabilityScore("fast_execution", 5);
            var multiCapAgent = new AgentRosterEntry
            {
                Id = "custom_multi_cap",
                Name = "Custom MultiCap AI",
                Detected = true,
                Enabled = true,
                PreferredEffortTier = "auto",
                PrimaryWorkRoles = new List<string> { "fast_execution", "image_generation" },
                ModelProfiles = new List<ModelWorkProfile> { modelArt, modelCode },
                HighTierModel = "Code-Model-5",
                MediumTierModel = "Art-Model-5",
                LowTierModel = "Art-Model-5"
            };
            var codeChoice = RoutingService.ResolveAgentModelAndTier(multiCapAgent, "medium", false, "fast_execution", preferNativeWhenTied: false);
            var imgChoice = RoutingService.ResolveAgentModelAndTier(multiCapAgent, "medium", false, "image_generation", preferNativeWhenTied: false);

            // 同星级下测试「优先自有模型」与「轻量速办 vs 复杂攻坚重修」规则
            var nativeTestModel = new ModelWorkProfile
            {
                ModelName = "Composer 2.5 (composer-2.5)",
                Enabled = true,
                RecommendedEffort = "low",
                UsagePolicy = "light_fast_first",
                TaskDifficultyRule = "low_medium"
            };
            nativeTestModel.SetCapabilityScore("fast_execution", 4);
            var thirdHeavyModel = new ModelWorkProfile
            {
                ModelName = "Claude Opus 5.5 (claude-opus-5-5)",
                Enabled = true,
                RecommendedEffort = "high",
                UsagePolicy = "complex_or_rework_only",
                TaskDifficultyRule = "high_only"
            };
            thirdHeavyModel.SetCapabilityScore("fast_execution", 4);
            var ruleTestAgent = new AgentRosterEntry
            {
                Id = "cursor",
                Name = "Cursor",
                Detected = true,
                Enabled = true,
                PreferredEffortTier = "auto",
                PrimaryWorkRoles = new List<string> { "fast_execution" },
                ModelProfiles = new List<ModelWorkProfile> { thirdHeavyModel, nativeTestModel },
                HighTierModel = "Claude Opus 5.5 (claude-opus-5-5)",
                MediumTierModel = "Composer 2.5 (composer-2.5)",
                LowTierModel = "Composer 2.5 (composer-2.5)"
            };
            var lowDiffPick = RoutingService.ResolveAgentModelAndTier(ruleTestAgent, "low", false, "fast_execution", preferNativeWhenTied: true);
            var highDiffPick = RoutingService.ResolveAgentModelAndTier(ruleTestAgent, "high", false, "fast_execution", preferNativeWhenTied: true);
            AssertTrue(
                codeChoice.ModelName == "Code-Model-5" &&
                imgChoice.ModelName == "Art-Model-5" &&
                lowDiffPick.ModelName.Contains("Composer", StringComparison.OrdinalIgnoreCase) &&
                highDiffPick.ModelName.Contains("Opus", StringComparison.OrdinalIgnoreCase),
                $"单模型1~5能力强度与使用规则路由选模校验通过 (Code={codeChoice.ModelName}, Img={imgChoice.ModelName}, Low={lowDiffPick.ModelName}, High={highDiffPick.ModelName})");

            // 弹窗与主界面双端验证：未启用 AI 绝不展示配置，仅显示启用小入口；已启用 AI 展示多模型与1~5强度按钮
            var codexForHideTest = reg.UserConfig.Agents.FirstOrDefault(a => a.Id == "codex_cli");
            bool origCodexDetected = codexForHideTest?.Detected ?? false;
            bool origCodexEnabled = codexForHideTest?.Enabled ?? false;
            var cursorForWorkbenchTest = reg.UserConfig.Agents.FirstOrDefault(a => a.Id == "cursor");
            bool origCurDet = cursorForWorkbenchTest?.Detected ?? false;
            bool origCurEn = cursorForWorkbenchTest?.Enabled ?? false;
            var collapseTestAgents = new[] { "antigravity", "grok_bot" }
                .Select(id => reg.UserConfig.Agents.FirstOrDefault(a => a.Id == id))
                .Where(a => a != null)
                .Select(a => (Agent: a!, Det: a!.Detected, En: a.Enabled))
                .ToList();
            try
            {
                if (codexForHideTest != null) { codexForHideTest.Detected = true; codexForHideTest.Enabled = false; }
                if (cursorForWorkbenchTest != null) { cursorForWorkbenchTest.Detected = true; cursorForWorkbenchTest.Enabled = true; }
                foreach (var c in collapseTestAgents) { c.Agent.Detected = true; c.Agent.Enabled = true; }
                reg.SyncActiveTeamProfileWithCurrentState(saveToDisk: false);

                var (profMgrWin2, _) = Dialogs.BuildTeamProfileManagerDialogWindow(null, reg, new CloudRecommendationService());
                profMgrWin2.WindowStartupLocation = WindowStartupLocation.Manual;
                profMgrWin2.Left = -32000;
                profMgrWin2.Top = -32000;
                profMgrWin2.ShowActivated = false;
                profMgrWin2.Show();
                profMgrWin2.UpdateLayout();

                static bool IsStrengthCombo(System.Windows.Controls.ComboBox cb) =>
                    cb.Items.OfType<System.Windows.Controls.ComboBoxItem>().Any(ci => string.Equals(ci.Content as string, "5★王牌", StringComparison.Ordinal));
                List<System.Windows.Controls.Button> CapToggleBtns(string content) =>
                    CollectLogicalAndVisual<System.Windows.Controls.Button>(profMgrWin2)
                        .Where(b => string.Equals(b.Content as string, content, StringComparison.Ordinal))
                        .Distinct()
                        .ToList();
                string CheckAllCollapsed(string label)
                {
                    var expandBtns = CapToggleBtns("配置能力 ▼");
                    int collapseBtnCount = CapToggleBtns("收起能力 ▲").Count;
                    int strengthCombos = CollectLogicalAndVisual<System.Windows.Controls.ComboBox>(profMgrWin2).Count(IsStrengthCombo);
                    int summaryChips = CollectLogicalAndVisual<System.Windows.Controls.TextBlock>(profMgrWin2)
                        .Distinct()
                        .Count(t => t.IsVisible && (Regex.IsMatch(t.Text ?? "", @"\s[1-5]★$") || (t.Text ?? "").StartsWith("未开启任何能力", StringComparison.Ordinal)));
                    bool ok = expandBtns.Count > 0 && expandBtns.All(b => b.IsVisible) &&
                              collapseBtnCount == 0 && strengthCombos == 0 && summaryChips > 0;
                    AssertTrue(ok,
                        $"配置方案中心[{label}]模型能力面板默认全部收起 (配置能力▼={expandBtns.Count}, 全可见={expandBtns.All(b => b.IsVisible)}, 收起能力▲={collapseBtnCount}, 强度下拉={strengthCombos}, 摘要胶囊={summaryChips})");
                    return $"{label}:{expandBtns.Count}";
                }

                CheckAllCollapsed("首次打开");
                foreach (var agId in new[] { "antigravity", "cursor", "grok_bot" })
                {
                    if (reg.UserConfig.Agents.All(a => a.Id != agId)) continue;
                    (profMgrWin2.Tag as Action<int, string?>)?.Invoke(0, agId);
                    profMgrWin2.UpdateLayout();
                    CheckAllCollapsed(agId);
                }

                (profMgrWin2.Tag as Action<int, string?>)?.Invoke(0, "cursor");
                profMgrWin2.UpdateLayout();
                var firstExpandBtn = CapToggleBtns("配置能力 ▼").FirstOrDefault();
                firstExpandBtn?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                profMgrWin2.UpdateLayout();
                int expandedAfterClick = CapToggleBtns("收起能力 ▲").Count;

                var dlgBtns = CollectLogicalAndVisual<System.Windows.Controls.Button>(profMgrWin2);
                var dlgCombos = CollectLogicalAndVisual<System.Windows.Controls.ComboBox>(profMgrWin2);
                var dlgTexts = CollectLogicalAndVisual<System.Windows.Controls.TextBlock>(profMgrWin2);
                bool noDisabledInlineBtns = !dlgBtns.Any(b => (b.Content as string)?.Contains("启用 Codex", StringComparison.OrdinalIgnoreCase) == true);
                bool hasCompactIconTabs = dlgBtns.Any(b => (b.ToolTip as string)?.Contains("Cursor", StringComparison.OrdinalIgnoreCase) == true && b.Width == 36);
                bool hasCodexModelCard = dlgTexts.Any(t => t.Text.Contains("gpt-6-luna", StringComparison.OrdinalIgnoreCase) || t.Text.Contains("gpt-5.6", StringComparison.OrdinalIgnoreCase));
                bool hasStrengthCombo = dlgCombos.Any(cb => cb.Items.OfType<System.Windows.Controls.ComboBoxItem>().Any(ci => string.Equals(ci.Content as string, "5★王牌", StringComparison.Ordinal)));
                bool hasAddModelBtn = dlgBtns.Any(b => (b.Content as string)?.Contains("添加模型", StringComparison.Ordinal) == true);

                CapToggleBtns("收起能力 ▲").FirstOrDefault()?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                profMgrWin2.UpdateLayout();
                bool recollapsed = CapToggleBtns("收起能力 ▲").Count == 0 &&
                                   !CollectLogicalAndVisual<System.Windows.Controls.ComboBox>(profMgrWin2).Any(IsStrengthCombo);
                AssertTrue(
                    firstExpandBtn != null && expandedAfterClick == 1 && hasStrengthCombo && recollapsed,
                    $"点击 #1「配置能力 ▼」后展开强度下拉、再点「收起能力 ▲」恢复收起 (找到按钮={firstExpandBtn != null}, 展开数={expandedAfterClick}, StrengthCombo={hasStrengthCombo}, 再收起={recollapsed})");

                // 第 2 页：12 类路由实时命中预览（当前自动命中 / 已锁定优先）
                (profMgrWin2.Tag as Action<int, string?>)?.Invoke(1, null);
                profMgrWin2.UpdateLayout();
                int livePreviewCount = CollectLogicalAndVisual<System.Windows.Controls.TextBlock>(profMgrWin2)
                    .Count(t => t.Text.Contains("当前自动命中：", StringComparison.Ordinal) ||
                                t.Text.Contains("已锁定优先：", StringComparison.Ordinal));
                AssertTrue(
                    livePreviewCount >= 12,
                    $"配置方案中心第2页实时命中预览数量 >= 12 (实际={livePreviewCount})");
                profMgrWin2.Close();

                fxHost.SwitchToTabForTest(5);
                fxHost.SwitchSettingsSubViewForTest(false);
                fxHost.UpdateLayout();
                var mainCards = CollectLogicalAndVisual<System.Windows.Controls.Border>(fxHost.DetectedAgentsList)
                    .Where(bd => bd.Margin.Bottom is 0 or 6 or 8)
                    .ToList();
                var codexMainCard = mainCards.FirstOrDefault(bd =>
                    CollectLogicalAndVisual<System.Windows.Controls.TextBlock>(bd).Any(t => t.Text.Contains("Codex", StringComparison.OrdinalIgnoreCase)));
                bool codexMainZeroNoise = codexMainCard != null &&
                    !CollectLogicalAndVisual<System.Windows.Controls.TextBlock>(codexMainCard).Any(t =>
                        t.Text.Contains("🤖 模型矩阵：", StringComparison.Ordinal) ||
                        t.Text.Contains("🎯 职责与权限：", StringComparison.Ordinal));
                AssertTrue(
                    noDisabledInlineBtns && hasCompactIconTabs && !hasCodexModelCard && hasStrengthCombo && hasAddModelBtn && codexMainZeroNoise,
                    $"未启用AI零平铺+极简图标页签+多模型紧凑下拉强度工作台双端自检通过 (NoDisabledBtns={noDisabledInlineBtns}, IconTabs={hasCompactIconTabs}, CodexCard={hasCodexModelCard}, StrengthCombo={hasStrengthCombo}, AddModelBtn={hasAddModelBtn}, MainZeroNoise={codexMainZeroNoise})");
            }
            finally
            {
                if (codexForHideTest != null) { codexForHideTest.Detected = origCodexDetected; codexForHideTest.Enabled = origCodexEnabled; }
                if (cursorForWorkbenchTest != null) { cursorForWorkbenchTest.Detected = origCurDet; cursorForWorkbenchTest.Enabled = origCurEn; }
                foreach (var c in collapseTestAgents) { c.Agent.Detected = c.Det; c.Agent.Enabled = c.En; }
                reg.SyncActiveTeamProfileWithCurrentState(saveToDisk: false);
            }

            // ========== 协同工作台动态流程运行图自测 ==========
            {
                bool bakWfEngine = reg.UserConfig.WorkflowEngineEnabled;
                var bakEnabledMap = reg.UserConfig.Agents.ToDictionary(
                    a => a.Id, a => a.Enabled, StringComparer.OrdinalIgnoreCase);
                try
                {
                    reg.UserConfig.WorkflowEngineEnabled = true;
                    // 确保至少 2 个执行侧工具可用（含 grok_bot 优先）
                    foreach (var needId in new[] { "antigravity", "cursor", "grok_bot" })
                    {
                        var ag = reg.UserConfig.Agents.FirstOrDefault(a =>
                            string.Equals(a.Id, needId, StringComparison.OrdinalIgnoreCase));
                        if (ag != null && !ag.Enabled)
                            reg.SetAgentWorkflowEnabled(ag.Id, true);
                    }

                    fxHost.SwitchToTabForTest(1);
                    fxHost.RefreshWorkflowDiagram();
                    fxHost.UpdateLayout();

                    static List<string> ExpectedWorkerIds(RegistryService r)
                    {
                        var proj = r.GetActiveProject();
                        var master = RoutingService.ResolveMasterPlanner(r, proj);
                        var enabled = r.UserConfig.Agents.Where(a => a.Enabled).ToList();
                        var workers = enabled
                            .Where(a => master == null ||
                                        !string.Equals(a.Id, master.Id, StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(a => a.Detected)
                            .ThenBy(a => a.Name)
                            .Select(a => a.Id)
                            .ToList();
                        if (workers.Count == 0)
                            workers = enabled.OrderByDescending(a => a.Detected).ThenBy(a => a.Name).Select(a => a.Id).ToList();
                        return workers;
                    }

                    var summary0 = fxHost.GetWorkflowDiagramNodeSummaryForTest();
                    var expected0 = ExpectedWorkerIds(reg);
                    AssertTrue(
                        summary0.CanvasChildCount > 0 &&
                        !string.IsNullOrWhiteSpace(summary0.MasterId) &&
                        summary0.WorkerIds.SequenceEqual(expected0, StringComparer.OrdinalIgnoreCase),
                        $"流程运行图首屏节点完备 (Canvas={summary0.CanvasChildCount}, Master={summary0.MasterId}, Workers={summary0.WorkerIds.Count}/{expected0.Count})");

                    var toggleTarget = reg.UserConfig.Agents.FirstOrDefault(a =>
                                           string.Equals(a.Id, "grok_bot", StringComparison.OrdinalIgnoreCase) && a.Enabled)
                                       ?? reg.UserConfig.Agents.FirstOrDefault(a =>
                                           a.Enabled &&
                                           !string.Equals(a.Id, summary0.MasterId, StringComparison.OrdinalIgnoreCase));
                    AssertTrue(toggleTarget != null, "流程运行图自测找到可临时关闭的执行 AI");
                    int workersBefore = summary0.WorkerIds.Count;
                    bool wasOn = toggleTarget!.Enabled;
                    var offRes = reg.SetAgentWorkflowEnabled(toggleTarget.Id, false);
                    AssertTrue(offRes.Ok, $"临时关闭执行 AI 成功 ({toggleTarget.Id})");
                    fxHost.RefreshWorkflowDiagram();
                    fxHost.UpdateLayout();
                    var summaryOff = fxHost.GetWorkflowDiagramNodeSummaryForTest();
                    AssertTrue(
                        summaryOff.WorkerIds.Count == workersBefore - 1 &&
                        !summaryOff.WorkerIds.Contains(toggleTarget.Id, StringComparer.OrdinalIgnoreCase),
                        $"关闭执行 AI 后 WorkerIds 实时减少 (Before={workersBefore}, After={summaryOff.WorkerIds.Count})");
                    var onRes = reg.SetAgentWorkflowEnabled(toggleTarget.Id, true);
                    AssertTrue(onRes.Ok, $"重新开启执行 AI 成功 ({toggleTarget.Id})");
                    fxHost.RefreshWorkflowDiagram();
                    fxHost.UpdateLayout();
                    var summaryOn = fxHost.GetWorkflowDiagramNodeSummaryForTest();
                    AssertTrue(
                        summaryOn.WorkerIds.Count == workersBefore &&
                        summaryOn.WorkerIds.Contains(toggleTarget.Id, StringComparer.OrdinalIgnoreCase),
                        $"重新开启后 WorkerIds 实时恢复 (Count={summaryOn.WorkerIds.Count})");
                    if (!wasOn) reg.SetAgentWorkflowEnabled(toggleTarget.Id, false);

                    string? activeWorker = summaryOn.WorkerIds.FirstOrDefault() ?? "cursor";
                    fxHost.SetWorkflowDiagramPreviewPhaseForTest("master_analyzing", 1, 2, activeWorker);
                    AssertTrue(
                        fxHost.GetWorkflowDiagramNodeSummaryForTest().PhaseText.Contains("主脑深度分析", StringComparison.Ordinal),
                        "流程阶段 master_analyzing → PhaseText 正确");
                    fxHost.SetWorkflowDiagramPreviewPhaseForTest("executing", 1, 2, activeWorker);
                    AssertTrue(
                        fxHost.GetWorkflowDiagramNodeSummaryForTest().PhaseText.Contains("实操编码中", StringComparison.Ordinal),
                        "流程阶段 executing → PhaseText 正确");
                    fxHost.SetWorkflowDiagramPreviewPhaseForTest("master_verifying", 1, 2, activeWorker);
                    AssertTrue(
                        fxHost.GetWorkflowDiagramNodeSummaryForTest().PhaseText.Contains("主脑严格验收", StringComparison.Ordinal),
                        "流程阶段 master_verifying → PhaseText 正确");
                    fxHost.SetWorkflowDiagramPreviewPhaseForTest("reworking", 1, 2, activeWorker);
                    var reworkPhase = fxHost.GetWorkflowDiagramNodeSummaryForTest().PhaseText;
                    AssertTrue(
                        reworkPhase.Contains("打回精修", StringComparison.Ordinal) &&
                        reworkPhase.Contains("R1/2", StringComparison.Ordinal),
                        $"流程阶段 reworking → PhaseText 含 R1/2 (实际={reworkPhase})");
                    fxHost.SetWorkflowDiagramPreviewPhaseForTest("verified_delivered", 1, 2, activeWorker);
                    AssertTrue(
                        fxHost.GetWorkflowDiagramNodeSummaryForTest().PhaseText.Contains("达标交付", StringComparison.Ordinal),
                        "流程阶段 verified_delivered → PhaseText 正确");

                    bool demoOn = fxHost.ToggleWorkflowDiagramDemoForTest();
                    AssertTrue(demoOn && fxHost.GetWorkflowDiagramNodeSummaryForTest().DemoActive, "演示流转模式开启返回 true");
                    bool demoOff = fxHost.ToggleWorkflowDiagramDemoForTest();
                    AssertTrue(!demoOff && !fxHost.GetWorkflowDiagramNodeSummaryForTest().DemoActive, "演示流转模式关闭返回 false");
                    fxHost.RefreshWorkflowDiagram();
                    fxHost.UpdateLayout();

                    // —— Magpie 思维导图拓扑断言 ——
                    var canvas = fxHost.WorkflowDiagramCanvas;
                    AssertTrue(canvas != null && canvas.Children.Count > 0, "思维导图画布非空");
                    FrameworkElement? FindByTag(string tag) =>
                        canvas!.Children.OfType<FrameworkElement>()
                            .FirstOrDefault(fe => string.Equals(fe.Tag as string, tag, StringComparison.Ordinal));
                    var masterNode = FindByTag("MindMapNode:master");
                    var intakeNode = FindByTag("MindMapNode:intake");
                    var verifyNode = FindByTag("MindMapNode:verify");
                    var workerNode = canvas!.Children.OfType<FrameworkElement>()
                        .FirstOrDefault(fe => (fe.Tag as string)?.StartsWith("MindMapNode:worker", StringComparison.Ordinal) == true);
                    AssertTrue(masterNode != null, "存在 Tag=MindMapNode:master 主脑节点");
                    AssertTrue(intakeNode != null && verifyNode != null, "左侧存在 intake / verify 节点");
                    AssertTrue(workerNode != null, "右侧存在 MindMapNode:worker 节点");
                    double canvasMidX = canvas.ActualWidth > 1 ? canvas.ActualWidth * 0.5 : 0;
                    double masterLeft = Canvas.GetLeft(masterNode!);
                    double masterCx = masterLeft + masterNode!.ActualWidth / 2.0;
                    AssertTrue(Math.Abs(masterCx - canvasMidX) <= 36,
                        $"主脑水平中心落在画布中点±36px (masterCx={masterCx:F1}, mid={canvasMidX:F1})");
                    AssertTrue(Canvas.GetLeft(intakeNode!) < masterLeft && Canvas.GetLeft(verifyNode!) < masterLeft,
                        "intake/verify 位于主脑左侧");
                    AssertTrue(Canvas.GetLeft(workerNode!) > masterLeft + masterNode.ActualWidth * 0.4,
                        "worker 位于主脑右侧");
                    bool hasBezier = canvas.Children.OfType<System.Windows.Shapes.Path>()
                        .Select(p => p.Data)
                        .OfType<PathGeometry>()
                        .SelectMany(g => g.Figures)
                        .SelectMany(f => f.Segments)
                        .Any(s => s is BezierSegment);
                    AssertTrue(hasBezier, "画布 Path 含 BezierSegment 曲线段");

                    // —— UI 定尺防抖断言 ——
                    fxHost.UpdateLayout();
                    {
                        bool prev = reg.UserConfig.WorkflowEngineEnabled;
                        reg.UserConfig.WorkflowEngineEnabled = true;
                        fxHost.UpdateWorkflowSwitchBadges();
                        fxHost.UpdateLayout();
                        double wfWOn = fxHost.WorkflowSwitchBtn.ActualWidth;
                        reg.UserConfig.WorkflowEngineEnabled = false;
                        fxHost.UpdateWorkflowSwitchBadges();
                        fxHost.UpdateLayout();
                        double wfWOff = fxHost.WorkflowSwitchBtn.ActualWidth;
                        AssertTrue(Math.Abs(wfWOn - wfWOff) < 0.5,
                            $"WorkflowSwitchBtn 开/关 ActualWidth 相等 (on={wfWOn:F1}, off={wfWOff:F1})");
                        reg.UserConfig.WorkflowEngineEnabled = prev;
                        fxHost.UpdateWorkflowSwitchBadges();
                    }
                    fxHost.SwitchToTabForTest(6);
                    fxHost.UpdateLayout();
                    var segTabs = new RadioButton?[]
                    {
                        fxHost.TabSettingsGeneral,
                        fxHost.TabSettingsAppearance,
                        fxHost.TabSettingsResilience,
                        fxHost.TabSettingsAbout
                    }.Where(t => t != null).Cast<RadioButton>().ToList();
                    AssertTrue(segTabs.Count == 4, $"设置二级页 4 个 SettingsSegTabBtn 可见 (n={segTabs.Count})");
                    double segW0 = segTabs[0].ActualWidth;
                    AssertTrue(segTabs.All(t => Math.Abs(t.ActualWidth - segW0) < 0.5),
                        $"SettingsSegTabBtn ActualWidth 全部相等 (w={segW0:F1})");
                    fxHost.SwitchToTabForTest(1);
                    fxHost.UpdateLayout();
                    var overviewCards = fxHost.OverviewAgentsCards.Children.OfType<Border>().ToList();
                    AssertTrue(overviewCards.Count == 0 || overviewCards.All(c =>
                            Math.Abs(c.Height - 56) < 0.5 || Math.Abs(c.ActualHeight - 56) < 1.5),
                        $"OverviewAgentsCards 卡片 Height/ActualHeight 均为 56 (count={overviewCards.Count})");

                    double compactBakW = fxHost.Width;
                    double compactBakH = fxHost.Height;
                    fxHost.Width = 1220;
                    fxHost.Height = 760;
                    fxHost.SwitchToTabForTest(1);
                    fxHost.UpdateLayout();
                    AssertTrue(fxHost.PageOverview.ActualHeight > 400 && fxHost.PageOverview.ScrollableHeight <= 1.0,
                        $"工作台 1220x760 零垂直滚动 (ScrollableHeight={fxHost.PageOverview.ScrollableHeight:F2}, Viewport={fxHost.PageOverview.ActualHeight:F1})");
                    fxHost.SwitchToTabForTest(6);
                    foreach (var settingsKey in new[] { "general", "appearance", "resilience", "about" })
                    {
                        fxHost.SwitchSettingsTabForTest(settingsKey);
                        fxHost.UpdateLayout();
                        var settingsScroll = CollectLogicalAndVisual<ScrollViewer>(fxHost.PageAppSettings).FirstOrDefault();
                        AssertTrue(settingsScroll != null && settingsScroll.ActualHeight > 200 && settingsScroll.ScrollableHeight <= 1.0,
                            $"设置页 {settingsKey} 1220x760 零垂直滚动 (ScrollableHeight={settingsScroll?.ScrollableHeight:F2}, Viewport={settingsScroll?.ActualHeight:F1})");
                    }
                    fxHost.Width = compactBakW;
                    fxHost.Height = compactBakH;
                    fxHost.UpdateLayout();
                }
                finally
                {
                    // 确保演示态关闭并恢复真实预览 / 启停状态
                    if (fxHost.GetWorkflowDiagramNodeSummaryForTest().DemoActive)
                        fxHost.ToggleWorkflowDiagramDemoForTest();
                    foreach (var kv in bakEnabledMap.Where(kv => kv.Value))
                        reg.SetAgentWorkflowEnabled(kv.Key, true);
                    foreach (var kv in bakEnabledMap.Where(kv => !kv.Value))
                        reg.SetAgentWorkflowEnabled(kv.Key, false);
                    reg.UserConfig.WorkflowEngineEnabled = bakWfEngine;
                    fxHost.RefreshWorkflowDiagram();
                    fxHost.UpdateLayout();
                }
            }

            // ========== 阶段 1C-b2：悬浮窗音效摆位 / 展开防回弹 / CLI 契约 / OS 静音 ==========
            {
                // 1) 悬浮窗音效按钮摆位与弹出层清理
                var hdrSound = floatFx.HeaderSoundBtn;
                var hdrWf = floatFx.HeaderWorkflowBtn;
                var capSound = floatFx.CapsuleSoundBtn;
                var capOpacity = floatFx.CapsuleOpacityBtn;
                AssertTrue(
                    hdrSound != null && hdrWf != null && capSound != null && capOpacity != null,
                    "悬浮窗 Header/Capsule 音效与相邻按钮均已生成");
                var headerPanel = hdrSound!.Parent as System.Windows.Controls.Panel;
                var capsulePanel = capSound!.Parent as System.Windows.Controls.Panel;
                AssertTrue(
                    headerPanel != null &&
                    headerPanel.Children.IndexOf(hdrSound) == headerPanel.Children.IndexOf(hdrWf!) - 1,
                    "HeaderSoundBtn 紧邻 HeaderWorkflowBtn 左侧");
                AssertTrue(
                    capsulePanel != null &&
                    capsulePanel.Children.IndexOf(capSound) == capsulePanel.Children.IndexOf(capOpacity!) - 1,
                    "CapsuleSoundBtn 紧邻 CapsuleOpacityBtn 左侧");
                bool popupHasSoundBtn = floatFx.OpacityPopup != null &&
                    CollectLogicalAndVisual<FrameworkElement>(floatFx.OpacityPopup.Child as DependencyObject)
                        .Any(el => string.Equals(el.Name, "PopupSoundBtn", StringComparison.Ordinal));
                AssertTrue(!popupHasSoundBtn, "OpacityPopup 内不再存在 PopupSoundBtn");

                bool bakEnableSound = reg.UserConfig.EnableSoundEffects;
                bool bakSoundLite = reg.UserConfig.SoundLightweightMode;
                try
                {
                    // 先重置到全音效，再测三态循环与静音落盘
                    reg.UserConfig.EnableSoundEffects = true;
                    reg.UserConfig.SoundLightweightMode = false;
                    reg.SaveUserConfig();
                    SoundFxService.ApplySoundPreference(reg.UserConfig);
                    floatFx.SyncQuickSoundButtonUi();

                    var seenIcons = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < 3; i++)
                    {
                        hdrSound.RaiseEvent(
                            new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        floatFx.UpdateLayout();
                        string hIcon = hdrSound.Content as string ?? "";
                        string cIcon = capSound.Content as string ?? "";
                        AssertTrue(
                            hIcon == cIcon && (hIcon is "🔔" or "🎵" or "🔕"),
                            $"音效按钮第{i + 1}次点击后 Header/Capsule 同步为合法图标 (H={hIcon}, C={cIcon})");
                        seenIcons.Add(hIcon);

                        // 点到静音态：必须落盘 EnableSoundEffects=false 且硬闸门拦截播音
                        if (hIcon == "🔕")
                        {
                            AssertTrue(!reg.UserConfig.EnableSoundEffects,
                                "悬浮窗音效按钮切到静音后 UserConfig.EnableSoundEffects 须为 false");
                            AssertTrue(
                                File.Exists(AppPaths.UserConfigFile) &&
                                File.ReadAllText(AppPaths.UserConfigFile, Encoding.UTF8)
                                    .Contains("\"enable_sound_effects\": false", StringComparison.Ordinal),
                                "悬浮窗音效静音须持久化落盘 enable_sound_effects=false");
                            AssertTrue(
                                !SoundFxService.ShouldPlay(reg.UserConfig, SoundFxService.SoundKind.WorkStarted) &&
                                !SoundFxService.ShouldPlay(reg.UserConfig, SoundFxService.SoundKind.DispatchFlight),
                                "悬浮窗音效按钮静音后须硬闸门拦截全部 SoundKind");
                        }
                    }
                    AssertTrue(
                        seenIcons.SetEquals(new[] { "🔔", "🎵", "🔕" }),
                        $"连续3次点击覆盖静音/全音效/仅完成音三态 (实际={string.Join(",", seenIcons)})");
                }
                finally
                {
                    reg.UserConfig.EnableSoundEffects = bakEnableSound;
                    reg.UserConfig.SoundLightweightMode = bakSoundLite;
                    reg.SaveUserConfig();
                    SoundFxService.ApplySoundPreference(reg.UserConfig);
                    floatFx.SyncQuickSoundButtonUi();
                }

                // 2) 悬浮窗全形态绝对锁定回归：状态刷新不得改折叠/模式/可见性/宽高档位
                bool bakCollapsed = reg.UserConfig.FloatingWindowCollapsed;
                string bakLayoutMode = reg.UserConfig.FloatingLayoutMode;
                double? bakCustomW = reg.UserConfig.FloatingCustomWidth;
                double? bakCollapsedCustomW = reg.UserConfig.FloatingCollapsedCustomWidth;
                double? bakUltraW = reg.UserConfig.FloatingUltraCustomWidth;
                double? bakCustomH = reg.UserConfig.FloatingCustomHeight;
                bool bakFloatEnabled = reg.UserConfig.FloatingWindowEnabled;
                bool bakAutoExpand = reg.UserConfig.FloatingAutoExpandOnWorking;
                bool bakAutoCollapse = reg.UserConfig.FloatingAutoCollapseOnDone;
                bool bakWf = reg.UserConfig.WorkflowEngineEnabled;
                byte[]? bakLiveMorph = File.Exists(AutoDispatchService.LiveStateMapPath)
                    ? File.ReadAllBytes(AutoDispatchService.LiveStateMapPath) : null;
                try
                {
                    reg.UserConfig.WorkflowEngineEnabled = true;
                    // 即使旧配置仍勾选自动展开/收起，运行时也必须完全忽略
                    reg.UserConfig.FloatingAutoExpandOnWorking = true;
                    reg.UserConfig.FloatingAutoCollapseOnDone = true;
                    reg.UserConfig.FloatingWindowEnabled = true;
                    reg.SaveUserConfig();

                    void WriteMorphLive(string status, string phase)
                    {
                        var map = new Dictionary<string, LiveAgentWorkState>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["cursor"] = new LiveAgentWorkState
                            {
                                AgentId = "cursor",
                                AgentName = "Cursor",
                                Status = status,
                                WorkflowPhase = phase,
                                TaskSummary = "形态锁定自检任务",
                                LatestLogTail = status == "working" ? "Editing files" : "",
                                LastFailureBadge = status == "error" ? "超时" : "",
                                VerificationSummary = status == "done" ? "验收通过" : "",
                                UpdatedAt = DateTime.Now.ToString("HH:mm:ss"),
                                UpdatedUtcTicks = DateTime.UtcNow.Ticks,
                                DispatcherPid = status is "working" or "reworking" ? Environment.ProcessId : 0
                            }
                        };
                        RegistryService.WriteJsonAtomic(AutoDispatchService.LiveStateMapPath, map);
                    }

                    void CycleStatusRefreshPreserveMorph(
                        string label,
                        bool expectCollapsed,
                        FloatingOverlayWindow.FloatingCompactMode? expectMode,
                        bool expectVisible,
                        double expectWidth,
                        double? expectHeight)
                    {
                        string[][] chain =
                        {
                            new[] { "idle", "idle" },
                            new[] { "working", "executing" },
                            new[] { "error", "error" },
                            new[] { "working", "executing" },
                            new[] { "done", "verified_delivered" },
                            new[] { "idle", "idle" }
                        };
                        foreach (var step in chain)
                        {
                            WriteMorphLive(step[0], step[1]);
                            floatFx.RefreshOverlayData(force: true);
                            floatFx.UpdateLayout();
                            bool collapsedNow = floatFx.CollapsedMiniCapsule.Visibility == Visibility.Visible;
                            bool expandedNow = floatFx.ExpandedFullContainer.Visibility == Visibility.Visible;
                            AssertTrue(
                                collapsedNow == expectCollapsed &&
                                expandedNow == !expectCollapsed &&
                                reg.UserConfig.FloatingWindowCollapsed == expectCollapsed &&
                                floatFx.IsVisible == expectVisible &&
                                Math.Abs(floatFx.Width - expectWidth) < 2.0 &&
                                (expectMode == null || floatFx.CurrentCompactModeForTest == expectMode.Value) &&
                                (expectHeight == null || double.IsNaN(expectHeight.Value) ||
                                 Math.Abs((double.IsNaN(floatFx.Height) ? floatFx.ActualHeight : floatFx.Height) - expectHeight.Value) < 3.0),
                                $"形态锁定[{label}]@{step[0]}：Collapsed={collapsedNow}/{expectCollapsed}, Mode={floatFx.CurrentCompactModeForTest}/{expectMode}, Vis={floatFx.IsVisible}/{expectVisible}, W={floatFx.Width}/{expectWidth}, H={floatFx.Height}/{expectHeight}");
                        }
                    }

                    // 折叠胶囊态
                    reg.UserConfig.FloatingWindowCollapsed = true;
                    reg.UserConfig.FloatingLayoutMode = "full";
                    reg.UserConfig.FloatingCustomWidth = null;
                    reg.UserConfig.FloatingCollapsedCustomWidth = null;
                    reg.UserConfig.FloatingCustomHeight = null;
                    reg.SaveUserConfig();
                    floatFx.ApplyCollapsedVisual(true, preserveRightAnchor: false);
                    floatFx.RefreshOverlayData(force: true);
                    floatFx.UpdateLayout();
                    double capW = floatFx.Width;
                    CycleStatusRefreshPreserveMorph(
                        "折叠胶囊",
                        expectCollapsed: true,
                        expectMode: null,
                        expectVisible: true,
                        expectWidth: capW,
                        expectHeight: null);

                    // 极简单行态 Ultra
                    reg.UserConfig.FloatingWindowCollapsed = false;
                    reg.UserConfig.FloatingLayoutMode = "ultra";
                    reg.UserConfig.FloatingCustomHeight = null;
                    reg.SaveUserConfig();
                    floatFx.ApplyCollapsedVisual(false, preserveRightAnchor: false);
                    floatFx.EnsureDynamicMinHeight();
                    floatFx.RefreshOverlayData(force: true);
                    floatFx.UpdateLayout();
                    double ultraW = floatFx.Width;
                    AssertTrue(
                        floatFx.CurrentCompactModeForTest == FloatingOverlayWindow.FloatingCompactMode.Ultra,
                        $"极简基线 Mode=Ultra (实际={floatFx.CurrentCompactModeForTest})");
                    CycleStatusRefreshPreserveMorph(
                        "极简单行",
                        expectCollapsed: false,
                        expectMode: FloatingOverlayWindow.FloatingCompactMode.Ultra,
                        expectVisible: true,
                        expectWidth: ultraW,
                        expectHeight: null);

                    // 紧凑双行态 Compact
                    reg.UserConfig.FloatingLayoutMode = "compact";
                    reg.SaveUserConfig();
                    floatFx.EnsureDynamicMinHeight();
                    floatFx.RefreshOverlayData(force: true);
                    floatFx.UpdateLayout();
                    double compactW = floatFx.Width;
                    AssertTrue(
                        floatFx.CurrentCompactModeForTest == FloatingOverlayWindow.FloatingCompactMode.Compact,
                        $"紧凑基线 Mode=Compact (实际={floatFx.CurrentCompactModeForTest})");
                    CycleStatusRefreshPreserveMorph(
                        "紧凑双行",
                        expectCollapsed: false,
                        expectMode: FloatingOverlayWindow.FloatingCompactMode.Compact,
                        expectVisible: true,
                        expectWidth: compactW,
                        expectHeight: null);

                    // 完整展开态 Full
                    reg.UserConfig.FloatingLayoutMode = "full";
                    reg.SaveUserConfig();
                    floatFx.EnsureDynamicMinHeight();
                    floatFx.RefreshOverlayData(force: true);
                    floatFx.UpdateLayout();
                    double fullW = floatFx.Width;
                    AssertTrue(
                        floatFx.CurrentCompactModeForTest == FloatingOverlayWindow.FloatingCompactMode.Full,
                        $"完整基线 Mode=Full (实际={floatFx.CurrentCompactModeForTest})");
                    CycleStatusRefreshPreserveMorph(
                        "完整展开",
                        expectCollapsed: false,
                        expectMode: FloatingOverlayWindow.FloatingCompactMode.Full,
                        expectVisible: true,
                        expectWidth: fullW,
                        expectHeight: null);

                    // 隐藏态：Hide 后状态刷新不得 Show
                    floatFx.Hide();
                    reg.UserConfig.FloatingWindowEnabled = false;
                    reg.SaveUserConfig();
                    WriteMorphLive("working", "executing");
                    floatFx.RefreshOverlayData(force: true);
                    WriteMorphLive("done", "verified_delivered");
                    floatFx.RefreshOverlayData(force: true);
                    AssertTrue(
                        !floatFx.IsVisible && !reg.UserConfig.FloatingWindowEnabled,
                        $"隐藏态状态刷新不得自动 Show (IsVisible={floatFx.IsVisible}, Enabled={reg.UserConfig.FloatingWindowEnabled})");

                    // 用户手动切换形态 API 仍生效并持久化
                    reg.UserConfig.FloatingWindowEnabled = true;
                    reg.SaveUserConfig();
                    if (!floatFx.IsVisible) floatFx.Show();
                    floatFx.Left = -12000;
                    floatFx.Top = -12000;
                    floatFx.ApplyCollapsedVisual(false, preserveRightAnchor: false);
                    reg.UserConfig.FloatingWindowCollapsed = false;
                    reg.UserConfig.FloatingLayoutMode = "full";
                    reg.SaveUserConfig();
                    floatFx.ToggleCollapseState(); // 手动收起并落盘
                    AssertTrue(
                        reg.UserConfig.FloatingWindowCollapsed &&
                        floatFx.CollapsedMiniCapsule.Visibility == Visibility.Visible &&
                        File.Exists(AppPaths.UserConfigFile) &&
                        File.ReadAllText(AppPaths.UserConfigFile, Encoding.UTF8)
                            .Contains("\"floating_window_collapsed\": true", StringComparison.Ordinal),
                        "手动 ToggleCollapseState 须收起并持久化 floating_window_collapsed=true");
                    floatFx.ToggleCollapseState(); // 手动展开并落盘
                    reg.UserConfig.FloatingLayoutMode = "ultra";
                    reg.UserConfig.FloatingCustomHeight = null;
                    reg.SaveUserConfig();
                    floatFx.EnsureDynamicMinHeight();
                    floatFx.RefreshOverlayData(force: true);
                    AssertTrue(
                        !reg.UserConfig.FloatingWindowCollapsed &&
                        floatFx.CurrentCompactModeForTest == FloatingOverlayWindow.FloatingCompactMode.Ultra &&
                        File.ReadAllText(AppPaths.UserConfigFile, Encoding.UTF8)
                            .Contains("\"floating_layout_mode\": \"ultra\"", StringComparison.Ordinal),
                        "手动切到极简 Ultra 须持久化 floating_layout_mode=ultra");

                    // 主窗口最小化到托盘后，任务开始/完成刷新不得弹出
                    fxHost.MinimizeMainWindowToTray(ensureFloatingVisible: false);
                    fxHost.UpdateLayout();
                    AssertTrue(!fxHost.IsVisible && !fxHost.ShowInTaskbar,
                        "形态锁定托盘基线：主窗已隐藏");
                    WriteMorphLive("working", "executing");
                    fxHost.RefreshAllViews();
                    floatFx.RefreshOverlayData(force: true);
                    WriteMorphLive("done", "verified_delivered");
                    fxHost.RefreshAllViews();
                    floatFx.RefreshOverlayData(force: true);
                    AssertTrue(
                        !fxHost.IsVisible && !fxHost.ShowInTaskbar,
                        $"托盘隐藏后任务刷新不得 Show/Activate 主窗 (Vis={fxHost.IsVisible}, Taskbar={fxHost.ShowInTaskbar}, State={fxHost.WindowState})");
                }
                finally
                {
                    reg.UserConfig.FloatingWindowCollapsed = bakCollapsed;
                    reg.UserConfig.FloatingLayoutMode = bakLayoutMode;
                    reg.UserConfig.FloatingCustomWidth = bakCustomW;
                    reg.UserConfig.FloatingCollapsedCustomWidth = bakCollapsedCustomW;
                    reg.UserConfig.FloatingUltraCustomWidth = bakUltraW;
                    reg.UserConfig.FloatingCustomHeight = bakCustomH;
                    reg.UserConfig.FloatingWindowEnabled = bakFloatEnabled;
                    reg.UserConfig.FloatingAutoExpandOnWorking = false;
                    reg.UserConfig.FloatingAutoCollapseOnDone = false;
                    reg.UserConfig.WorkflowEngineEnabled = bakWf;
                    _ = bakAutoExpand;
                    _ = bakAutoCollapse;
                    reg.SaveUserConfig();
                    if (bakLiveMorph != null) File.WriteAllBytes(AutoDispatchService.LiveStateMapPath, bakLiveMorph);
                    else if (File.Exists(AutoDispatchService.LiveStateMapPath)) File.Delete(AutoDispatchService.LiveStateMapPath);
                    if (!floatFx.IsVisible && bakFloatEnabled) { floatFx.Show(); floatFx.Left = -12000; floatFx.Top = -12000; }
                    floatFx.ApplyUserPreferencesFromSettings();
                }

                // 3) CLI 90s 防误杀契约与桌面实时工作同步
                AssertTrue(
                    AutoDispatchService.CursorCliMinTimeoutSeconds >= 240,
                    $"CursorCliMinTimeoutSeconds >= 240 (实际={AutoDispatchService.CursorCliMinTimeoutSeconds})");
                string bufferedArgs = AutoDispatchService.BuildCursorAgentHeadlessArgs(
                    @"C:\Project\Godot\XX",
                    null,
                    autoTrust: true,
                    canWriteFiles: false,
                    approveMcps: false,
                    mode: "plan",
                    resumeSessionId: null,
                    modelSlug: "gpt-5");
                AssertTrue(
                    AutoDispatchService.IsBufferedPrintCliArgs(bufferedArgs),
                    "BuildCursorAgentHeadlessArgs(plan) 应识别为缓冲打印 CLI 参数");

                AutoDispatchService.SetDesktopBusyOverrideForTest(
                    "cursor",
                    new DesktopBusyProbeResult(true, "ai-collab-hub", "StopGenerationChat", DateTime.UtcNow));
                try
                {
                    var liveMap = AutoDispatchService.ReadMergedGlobalWorkStatesWithDesktopLive();
                    AssertTrue(
                        liveMap.TryGetValue("cursor", out var cursorLive) &&
                        cursorLive.Status == "working" &&
                        cursorLive.WorkflowPhase == "executing" &&
                        string.IsNullOrEmpty(cursorLive.LastFailureBadge),
                        $"桌面忙碌覆写强制 cursor working/executing (Status={cursorLive?.Status}, Phase={cursorLive?.WorkflowPhase}, Badge={cursorLive?.LastFailureBadge})");
                    fxHost.RefreshWorkflowDiagram();
                    fxHost.UpdateLayout();
                }
                finally
                {
                    AutoDispatchService.SetDesktopBusyOverrideForTest("cursor", null);
                }

                // 4) OS 级硬静音与 Cursor 提示音按钮
                SoundFxService.SyncProcessAudioMute(true);
                SoundFxService.SyncProcessAudioMute(false);
                AssertTrue(
                    fxHost.FindName("MuteCursorChimeBtn") is System.Windows.Controls.Button,
                    "主窗口存在 MuteCursorChimeBtn");
            }

            // 全部断言结束后：验证彻底退出清理并关闭悬浮窗
            floatFx.ShutdownForAppExit();
            AssertTrue(
                !floatFx.IsVisible &&
                (floatFx.OpacityPopup == null || !floatFx.OpacityPopup.IsOpen) &&
                (floatFx.VerifyCelebrationPopup == null || !floatFx.VerifyCelebrationPopup.IsOpen),
                "ShutdownForAppExit 安全停止轮询并关闭悬浮窗与 Popup");
            fxHost.Close();
        }

        // 5. 纯净绿色分享包导出与零隐私校验
        var share = new ShareExportService();
        var zipPath = Path.Combine(outDir, "share_verify.zip");
        share.ExportCleanPortableZip(zipPath);
        AssertTrue(File.Exists(zipPath), "已生成绿色分享压缩包");

        var inspectExtract = Path.Combine(outDir, "share_inspect");
        if (Directory.Exists(inspectExtract)) Directory.Delete(inspectExtract, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, inspectExtract);
        var shareFinalDet = Path.Combine(inspectExtract, "AI协同中心_绿色版", "registry", "detectors.json");
        AssertTrue(File.Exists(shareFinalDet), "分享包包含开放活口 registry 配置表");
        AssertTrue(!File.Exists(Path.Combine(inspectExtract, "AI协同中心_绿色版", "user_hub_config.json")), "分享包严格隔离不含任何个人项目数据");
        var shareFinalTxt = File.ReadAllText(shareFinalDet);
        string[] shareRequiredIds =
        {
            "trae", "kiro", "cline", "claude_desktop", "cherry_studio", "qwen_code", "ollama_local", "lm_studio"
        };
        bool shareFinalPrivacy =
            !shareFinalTxt.Contains("F:\\\\AIChat", StringComparison.OrdinalIgnoreCase) &&
            !shareFinalTxt.Contains("F:/AIChat", StringComparison.OrdinalIgnoreCase) &&
            !Regex.IsMatch(shareFinalTxt, @"[A-Za-z]:\\\\Users\\\\[^\\%""]+\\\\(?!AppData)");
        bool shareFinalHasAll = shareRequiredIds.All(id =>
            shareFinalTxt.Contains($"\"id\": \"{id}\"", StringComparison.OrdinalIgnoreCase) ||
            shareFinalTxt.Contains($"\"id\":\"{id}\"", StringComparison.OrdinalIgnoreCase));
        AssertTrue(
            shareFinalPrivacy && shareFinalHasAll,
            $"收口绿色版 detectors.json 隐私校验通过且含全部主流工具 (Privacy={shareFinalPrivacy}, HasAll={shareFinalHasAll})");

        // 进程名精确匹配回归：禁止 Name 首词兜底 / 子串 Contains 误报（如 UserOOBEBroker→roo、GeminiAppLauncher→gemini_cli）
        {
            var fakeIdx = new DiscoveryService.LocalEnvIndex();
            fakeIdx.ProcessNames.Add("UserOOBEBroker");
            fakeIdx.ProcessNames.Add("GeminiAppLauncher");

            var rooSpec = reg.Detectors.Detectors?.FirstOrDefault(d => d.Id == "roo_code")
                ?? new DetectorSpec { Id = "roo_code", Name = "Roo Code", Category = "ide_extension" };
            var geminiCliSpec = reg.Detectors.Detectors?.FirstOrDefault(d => d.Id == "gemini_cli")
                ?? new DetectorSpec { Id = "gemini_cli", Name = "Gemini CLI", Category = "local_cli" };
            var geminiDeskSpec = reg.Detectors.Detectors?.FirstOrDefault(d => d.Id == "gemini_desktop")
                ?? new DetectorSpec
                {
                    Id = "gemini_desktop",
                    Name = "Google Gemini (桌面端)",
                    Category = "local_agent",
                    ProbeProcessNames = new List<string> { "Gemini", "GeminiAppLauncher" }
                };

            AssertTrue(
                !DiscoveryService.MatchProcess(rooSpec, fakeIdx),
                "MatchProcess 回归：仅有 UserOOBEBroker/GeminiAppLauncher 时 roo_code 不得因进程名误命中");
            AssertTrue(
                !DiscoveryService.MatchProcess(geminiCliSpec, fakeIdx),
                "MatchProcess 回归：仅有 UserOOBEBroker/GeminiAppLauncher 时 gemini_cli 不得因进程名误命中");
            AssertTrue(
                DiscoveryService.MatchProcess(geminiDeskSpec, fakeIdx),
                "MatchProcess 回归：GeminiAppLauncher 应精确命中 gemini_desktop");
            AssertTrue(
                geminiCliSpec.ProbeProcessNames == null || geminiCliSpec.ProbeProcessNames.Count == 0,
                "registry：gemini_cli 的 probe_process_names 为空（不与桌面端进程混淆）");

            // MatchUninstall：Gemini 卸载项不得误命中 gemini_cli，应命中 gemini_desktop
            fakeIdx.UninstallEntries.Add(("Gemini", @"C:\Program Files\Google\Gemini", ""));
            AssertTrue(
                DiscoveryService.MatchUninstall(geminiCliSpec, fakeIdx) == null,
                "MatchUninstall 回归：Gemini 卸载项不得误命中 gemini_cli");
            AssertTrue(
                DiscoveryService.MatchUninstall(geminiDeskSpec, fakeIdx) != null,
                "MatchUninstall 回归：Gemini 卸载项应命中 gemini_desktop");

            var anythingSpec = reg.Detectors.Detectors?.FirstOrDefault(d => d.Id == "anythingllm");
            AssertTrue(
                anythingSpec != null && (anythingSpec.ProbePorts == null || anythingSpec.ProbePorts.Count == 0),
                "registry：anythingllm 的 ProbePorts 为空（不盲探通用开发端口）");

            var normalizedRoles = CloudRecommendationService.NormalizePrimaryWorkRoles(
                new[] { "deep_debug", "DEEP_DEBUG", "fast_execution", " " });
            AssertTrue(
                normalizedRoles.Count == 2,
                $"NormalizePrimaryWorkRoles 去重后数量为 2 (实际={normalizedRoles.Count})");

            var roleProbeAg = reg.UserConfig.Agents.FirstOrDefault(a => a.Enabled)
                              ?? reg.UserConfig.Agents.FirstOrDefault();
            AssertTrue(roleProbeAg != null, "PrimaryWorkRoles 去重自测找到可用模型");
            var bakRoleProbe = new List<string>(roleProbeAg!.PrimaryWorkRoles ?? new List<string>());
            TeamAgentSnapshot? roleProbeSnap = null;
            List<string>? bakSnapRoles = null;
            var activeTp = reg.UserConfig.TeamProfiles?.FirstOrDefault(p =>
                string.Equals(p.Id, reg.UserConfig.ActiveTeamProfileId, StringComparison.OrdinalIgnoreCase))
                ?? reg.UserConfig.TeamProfiles?.FirstOrDefault();
            if (activeTp != null)
            {
                roleProbeSnap = activeTp.AgentSnapshots?.FirstOrDefault(s =>
                    string.Equals(s.AgentId, roleProbeAg.Id, StringComparison.OrdinalIgnoreCase));
                if (roleProbeSnap != null)
                {
                    bakSnapRoles = new List<string>(roleProbeSnap.PrimaryWorkRoles ?? new List<string>());
                    roleProbeSnap.PrimaryWorkRoles = new List<string> { "deep_debug", "DEEP_DEBUG", "fast_execution", " " };
                }
            }
            roleProbeAg.PrimaryWorkRoles = new List<string> { "deep_debug", "DEEP_DEBUG", "fast_execution", " " };
            try
            {
                reg.SaveUserConfig();
                static bool HasCaseInsensitiveDupes(IEnumerable<string>? roles) =>
                    roles != null &&
                    roles.GroupBy(r => r.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);
                AssertTrue(
                    !HasCaseInsensitiveDupes(roleProbeAg.PrimaryWorkRoles) &&
                    roleProbeAg.PrimaryWorkRoles.Count == 2,
                    $"SaveUserConfig 后模型 PrimaryWorkRoles 无大小写重复 (Count={roleProbeAg.PrimaryWorkRoles.Count})");
                if (roleProbeSnap != null)
                {
                    AssertTrue(
                        !HasCaseInsensitiveDupes(roleProbeSnap.PrimaryWorkRoles),
                        $"SaveUserConfig 后快照 PrimaryWorkRoles 无大小写重复 (Count={roleProbeSnap.PrimaryWorkRoles?.Count})");
                }
            }
            finally
            {
                roleProbeAg.PrimaryWorkRoles = bakRoleProbe;
                if (roleProbeSnap != null && bakSnapRoles != null)
                    roleProbeSnap.PrimaryWorkRoles = bakSnapRoles;
                reg.SaveUserConfig();
            }
        }

        // 回归：桌面派单严禁抢前台（SW_SHOWNOACTIVATE / HWND_BOTTOM / 键鼠静默门控 / ValuePattern 优先）
        {
            var noStealWin32 = AutoDispatchService.BuildDesktopNoStealWin32Helpers();
            AssertTrue(
                noStealWin32.Contains("SW_SHOWNOACTIVATE", StringComparison.Ordinal) &&
                noStealWin32.Contains("HWND_BOTTOM", StringComparison.Ordinal) &&
                noStealWin32.Contains("SWP_NOACTIVATE", StringComparison.Ordinal) &&
                noStealWin32.Contains("PrepareWindowNoActivateKeepBelow", StringComparison.Ordinal) &&
                noStealWin32.Contains("WaitForUserIdleGap", StringComparison.Ordinal) &&
                noStealWin32.Contains("GetIdleMs", StringComparison.Ordinal) &&
                noStealWin32.Contains("DemoteAndRestoreForeground", StringComparison.Ordinal),
                "BuildDesktopNoStealWin32Helpers 含 SW_SHOWNOACTIVATE/HWND_BOTTOM/SWP_NOACTIVATE/静默门控/压回底层");
            var noStealPs = AutoDispatchService.BuildDesktopNoStealPsHelpers();
            AssertTrue(
                noStealPs.Contains("ValuePattern", StringComparison.Ordinal) &&
                noStealPs.Contains("Set-PromptTextRobust", StringComparison.Ordinal) &&
                noStealPs.Contains("WaitForUserIdleGap(600, 2500)", StringComparison.Ordinal) &&
                noStealPs.Contains("StrictNoForegroundStealOnDispatch", StringComparison.Ordinal) &&
                noStealPs.Contains("Set-Clipboard", StringComparison.Ordinal) &&
                noStealPs.Contains("DemoteAndRestoreForeground", StringComparison.Ordinal) &&
                noStealPs.Contains("0x0302", StringComparison.Ordinal) &&
                !noStealPs.Contains("SendKeys", StringComparison.OrdinalIgnoreCase) &&
                !noStealPs.Contains("SetCursorPos", StringComparison.OrdinalIgnoreCase) &&
                !noStealPs.Contains("mouse_event", StringComparison.OrdinalIgnoreCase) &&
                noStealPs.Contains("Invoke-DesktopControlNoFocus", StringComparison.Ordinal) &&
                noStealPs.Contains("Restore-DispatchForeground", StringComparison.Ordinal) &&
                noStealPs.Contains("SelectionItemPattern", StringComparison.Ordinal) &&
                noStealPs.Contains("ExpandCollapsePattern", StringComparison.Ordinal) &&
                noStealPs.Contains("$forbidBareFocus = $strictNoSteal -or [bool]$cfg.IsGrok", StringComparison.Ordinal) &&
                !noStealPs.Contains("SetForegroundWindow($hwnd)", StringComparison.Ordinal) &&
                !noStealPs.Contains("SW_RESTORE", StringComparison.Ordinal) &&
                !noStealPs.Contains("ShowWindow($hwnd", StringComparison.Ordinal),
                "BuildDesktopNoStealPsHelpers：ValuePattern 优先 + GetLastInputInfo 静默门控 + 剪贴板恢复 + 无键鼠污染 + Strict 禁抢前台");
            string adsPath = Path.Combine(AppPaths.AppBaseDir, "..", "..", "..", "Services", "AutoDispatchService.cs");
            if (!File.Exists(adsPath))
                adsPath = @"C:\Project\Godot\配套工具\tools\ai-collab-hub\src\Services\AutoDispatchService.cs";
            if (File.Exists(adsPath))
            {
                var ads = File.ReadAllText(adsPath);
                AssertTrue(
                    ads.Contains("PrepareWindowNoActivateKeepBelow", StringComparison.Ordinal) &&
                    ads.Contains("StrictNoForegroundStealOnDispatch", StringComparison.Ordinal) &&
                    ads.Contains("BuildDesktopNoStealWin32Helpers", StringComparison.Ordinal) &&
                    ads.Contains("activateIfRunning", StringComparison.Ordinal) &&
                    ads.Contains("Invoke-DesktopControlNoFocus", StringComparison.Ordinal) &&
                    ads.Contains("DemoteAndRestoreForeground", StringComparison.Ordinal) &&
                    ads.Contains("if ($strictNoSteal -and $prevForeground -ne $hwnd)", StringComparison.Ordinal) &&
                    !ads.Contains("SetForegroundWindow($hwnd)", StringComparison.Ordinal) &&
                    !ads.Contains("ShowWindow($hwnd, 9)", StringComparison.Ordinal) &&
                    !ads.Contains("ShowWindow($hwnd, SW_RESTORE)", StringComparison.Ordinal),
                    "桌面派单脚本 Strict 模式禁用未保护的 SetForegroundWindow($hwnd)/SW_RESTORE，统一走 PrepareWindowNoActivateKeepBelow 与 DemoteAndRestoreForeground");
            }
        }

        // 回归：桌面 UIA 防误触历史消息 & Revert 弹窗保护（5 层防护契约 + 跨软件防雷）
        var pickerPs = AutoDispatchService.BuildDesktopPromptPickerPsHelpers();
        AssertTrue(
            pickerPs.Contains("Steer without interrupting", StringComparison.Ordinal),
            "桌面输入框挑选：Steer without interrupting 生成态硬熔断（绝不向上回退匹配历史框）");
        AssertTrue(
            pickerPs.Contains("BoundingRectangle", StringComparison.Ordinal) &&
            pickerPs.Contains("Expression = 'Top'; Descending = $true", StringComparison.Ordinal) &&
            pickerPs.Contains("ClampedBottom", StringComparison.Ordinal) &&
            pickerPs.Contains("0.55", StringComparison.Ordinal),
            "桌面输入框挑选：Top 降序优先 + ClampedBottom 防滚动溢出 + 0.55 上半区历史气泡拒绝门");
        AssertTrue(
            pickerPs.Contains("Test-HasRevertFilesModal", StringComparison.Ordinal) &&
            pickerPs.Contains("hasKeepFilesBtn", StringComparison.Ordinal) &&
            pickerPs.Contains("Cancel-RevertFilesModalSafely", StringComparison.Ordinal) &&
            pickerPs.Contains("Revert Files", StringComparison.Ordinal) &&
            pickerPs.Contains("Keep Files", StringComparison.Ordinal) &&
            pickerPs.Contains("Restore Checkpoint", StringComparison.Ordinal) &&
            pickerPs.Contains("'取消'", StringComparison.Ordinal),
            "桌面发送：Revert/Checkpoint 弹窗识别、排除中英文危险按钮、仅点 Cancel/取消");
        AssertTrue(
            pickerPs.Contains("Invoke-SendButtonNearEdit", StringComparison.Ordinal) &&
            pickerPs.Contains("visBottom", StringComparison.Ordinal) &&
            pickerPs.Contains("dyToBox", StringComparison.Ordinal) &&
            pickerPs.Contains("-gt 140", StringComparison.Ordinal) &&
            pickerPs.Contains("if (-not $ok)", StringComparison.Ordinal) &&
            !pickerPs.Contains("$editCy", StringComparison.Ordinal),
            "桌面发送钮：Invoke-SendButtonNearEdit 按窗口裁剪视口区间距 dyToBox <= 140 且仅单击不双击");
        AssertTrue(
            AutoDispatchService.RevertModalAbortedToken == "REVERT_MODAL_ABORTED",
            "C# 侧 RevertModalAbortedToken == REVERT_MODAL_ABORTED");

        // 桌面观测完工判定（场景 A/B/C/D）与侧栏跨项目串扰隔离
        {
            var now = DateTime.UtcNow;
            // A：曾 Working + 文件静默 2.2s + 2 次空闲 → 立即完工
            AssertTrue(
                AutoDispatchService.EvaluateDesktopWatchCompletion(
                    everObservedWorking: true,
                    consecutiveIdleProbes: 2,
                    changeDetectedUtc: now.AddSeconds(-2.2),
                    lastObservedWorkingUtc: now.AddSeconds(-2.2),
                    watchElapsedSeconds: 8,
                    utcNow: now),
                "完工场景A：Working→Idle+文件静默2.2s+2次空闲应返回 true");

            // B：曾 Working + 只读无改 + 5s + 3 次空闲 → 完工（无需死等 32s）
            AssertTrue(
                AutoDispatchService.EvaluateDesktopWatchCompletion(
                    everObservedWorking: true,
                    consecutiveIdleProbes: 3,
                    changeDetectedUtc: null,
                    lastObservedWorkingUtc: now.AddSeconds(-3),
                    watchElapsedSeconds: 5.0,
                    utcNow: now),
                "完工场景B：Working→Idle+只读无改+5s+3次空闲应返回 true");

            // C：仅 1 次空闲时不得早退（防思考间隙）
            AssertTrue(
                !AutoDispatchService.EvaluateDesktopWatchCompletion(
                    everObservedWorking: true,
                    consecutiveIdleProbes: 1,
                    changeDetectedUtc: now.AddSeconds(-3),
                    lastObservedWorkingUtc: now.AddSeconds(-3),
                    watchElapsedSeconds: 10,
                    utcNow: now),
                "完工场景C：仅1次空闲应返回 false 防思考间隙早退");

            // D：未见 Working 且无改 — 5s 仍 false；11s+3 次空闲 true
            AssertTrue(
                !AutoDispatchService.EvaluateDesktopWatchCompletion(
                    everObservedWorking: false,
                    consecutiveIdleProbes: 3,
                    changeDetectedUtc: null,
                    lastObservedWorkingUtc: DateTime.MinValue,
                    watchElapsedSeconds: 5.0,
                    utcNow: now),
                "完工场景D：未见Working且无改时5s应返回 false");
            AssertTrue(
                AutoDispatchService.EvaluateDesktopWatchCompletion(
                    everObservedWorking: false,
                    consecutiveIdleProbes: 3,
                    changeDetectedUtc: null,
                    lastObservedWorkingUtc: DateTime.MinValue,
                    watchElapsedSeconds: 11.0,
                    utcNow: now),
                "完工场景D：未见Working且无改时11s+3次空闲应返回 true");
        }

        {
            var cands = new List<string> { "XX" };
            // 跨项目串扰：Stop 存在 + TAKT 有 Working，目标 XX 会话仅 Unread → 必须 false
            var crossTalk = new AutoDispatchService.SidebarBusyWindowSnapshot
            {
                HasSidebarGroupStructure = true,
                HasStopGeneration = true,
                HasSteerWithoutInterrupting = false,
                Groups = new List<AutoDispatchService.SidebarGroupSnapshot>
                {
                    new()
                    {
                        Name = "TAKT-UI-CC-main",
                        ChatButtonRawNames = new List<string> { "OtherTask, Working" }
                    },
                    new()
                    {
                        Name = "XX",
                        ChatButtonRawNames = new List<string> { "本单会话, Unread activity" }
                    }
                }
            };
            var (crossBusy, _) = AutoDispatchService.EvaluateSidebarBusyFromSnapshot(
                crossTalk, cands, "本单会话", "XX");
            AssertTrue(!crossBusy, "侧栏串扰隔离：他工程 Working + 本会话 Unread 时必须 Working=false");

            // 目标会话本身 Working → true
            var selfWorking = new AutoDispatchService.SidebarBusyWindowSnapshot
            {
                HasSidebarGroupStructure = true,
                HasStopGeneration = true,
                HasSteerWithoutInterrupting = false,
                Groups = new List<AutoDispatchService.SidebarGroupSnapshot>
                {
                    new()
                    {
                        Name = "TAKT-UI-CC-main",
                        ChatButtonRawNames = new List<string> { "OtherTask, Working" }
                    },
                    new()
                    {
                        Name = "XX",
                        ChatButtonRawNames = new List<string> { "本单会话, Working" }
                    }
                }
            };
            var (selfBusy, _) = AutoDispatchService.EvaluateSidebarBusyFromSnapshot(
                selfWorking, cands, "本单会话", "XX");
            AssertTrue(selfBusy, "侧栏判定：目标会话本身带 Working 应返回 true");

            // 无侧栏折叠态 + Stop generation → 回退 true
            var collapsed = new AutoDispatchService.SidebarBusyWindowSnapshot
            {
                HasSidebarGroupStructure = false,
                HasStopGeneration = true,
                HasSteerWithoutInterrupting = false,
                Groups = new List<AutoDispatchService.SidebarGroupSnapshot>()
            };
            var (collapsedBusy, _) = AutoDispatchService.EvaluateSidebarBusyFromSnapshot(
                collapsed, cands, "本单会话", "XX");
            AssertTrue(collapsedBusy, "无侧栏折叠态 + Stop generation 应回退 Working=true");

            // Cursor 右栏步骤折叠按钮不得识别为侧栏工程分组头
            string[] rightRailNames =
            {
                "Worked for 12s", "Thought for a few seconds", "Explored 3 files",
                "Edited FloatingOverlayWindow.xaml.cs", "Ran dotnet build",
                "Show more", "Context", "Add agents"
            };
            AssertTrue(
                rightRailNames.All(AutoDispatchService.IsCursorRightRailStepButtonName) &&
                !AutoDispatchService.IsCursorRightRailStepButtonName("XX") &&
                !AutoDispatchService.IsCursorRightRailStepButtonName("ai-collab-hub"),
                "Cursor 右栏步骤按钮前缀排除（Worked for/Thought/Explored/Edited/Ran/Show more/Context/Add agents）");
        }

        // 软件版本展示、SemVer、更新端点、Release JSON 与 AppUpdate 互斥
        {
            var current = AppUpdateService.GetCurrentVersion();
            var display = AppUpdateService.GetCurrentVersionDisplay();
            var parsed = AppUpdateService.ParseNormalizedVersion("v1.9.16");
            AssertTrue(
                current == "1.9.16" && current == AppPaths.AppVersion &&
                display == "v1.9.16" &&
                parsed.HasValue && parsed.Value.Major == 1 && parsed.Value.Minor == 9 && parsed.Value.Patch == 16 &&
                AppUpdateService.ParseNormalizedVersion("不是版本") == null,
                $"版本展示读取程序集规范版本 (current={current}, display={display})");

            AssertTrue(
                AppUpdateService.IsNewerVersion("1.9.17", "1.9.16") &&
                AppUpdateService.IsNewerVersion("v1.10.0", "1.9.99") &&
                AppUpdateService.IsNewerVersion("2.0.0", "v1.9.16") &&
                !AppUpdateService.IsNewerVersion("v1.9.16", "1.9.16") &&
                !AppUpdateService.IsNewerVersion("1.9.14", "1.9.16") &&
                !AppUpdateService.IsNewerVersion("v1.8.20", "1.9.0"),
                "SemVer 主次修订号比较通过");

            var gh = AppUpdateService.ResolveUpdateEndpoint("https://github.com/acme/ai-collab-hub.git");
            var ghSsh = AppUpdateService.ResolveUpdateEndpoint("git@github.com:acme/ai-collab-hub.git");
            var ghSshUrl = AppUpdateService.ResolveUpdateEndpoint("ssh://git@github.com/acme/ai-collab-hub.git");
            var ghShort = AppUpdateService.ResolveUpdateEndpoint("acme/ai-collab-hub");
            var ge = AppUpdateService.ResolveUpdateEndpoint("https://gitee.com/acme/ai-collab-hub");
            var geSsh = AppUpdateService.ResolveUpdateEndpoint("git@gitee.com:acme/ai-collab-hub.git");
            var custom = AppUpdateService.ResolveUpdateEndpoint("https://example.com/app/latest.json");
            AssertTrue(
                gh.Kind == "github" &&
                gh.ApiUrl == "https://api.github.com/repos/acme/ai-collab-hub/releases/latest" &&
                gh.TagsFallbackUrl == "https://api.github.com/repos/acme/ai-collab-hub/tags" &&
                ghSsh.Kind == "github" && ghSsh.ApiUrl == gh.ApiUrl && ghSsh.Repo == "ai-collab-hub" &&
                ghSshUrl.Kind == "github" && ghSshUrl.Owner == "acme" && ghSshUrl.Repo == "ai-collab-hub" &&
                ghShort.Kind == "github" && ghShort.ApiUrl == gh.ApiUrl &&
                ge.Kind == "gitee" &&
                ge.ApiUrl == "https://gitee.com/api/v5/repos/acme/ai-collab-hub/releases/latest" &&
                ge.TagsFallbackUrl.Contains("/repos/acme/ai-collab-hub/tags", StringComparison.Ordinal) &&
                geSsh.Kind == "gitee" && geSsh.Repo == "ai-collab-hub" &&
                custom.Kind == "custom" && custom.ApiUrl == "https://example.com/app/latest.json",
                $"更新端点解析通过 (gh={gh.Kind}/{gh.ApiUrl}, gitee={ge.Kind}, custom={custom.Kind})");

            const string githubJson = """
                {"tag_name":"v1.9.17","html_url":"https://github.com/acme/ai-collab-hub/releases/tag/v1.9.17","body":"修复更新","assets":[{"name":"notes.txt","browser_download_url":"https://example.com/notes.txt"},{"name":"AIHub.exe","browser_download_url":"https://example.com/hub.exe"}]}
                """;
            const string giteeJson = """
                {"tag_name":"v1.9.17","body":"码云说明","html_url":"https://gitee.com/acme/ai-collab-hub/releases/v1.9.17","assets":[{"name":"app.exe","browser_download_url":"https://gitee.com/acme/app.exe"}]}
                """;
            const string customJson = """
                {"version":"1.9.20","changelog":"自定义日志","html_url":"https://example.com/rel","download_url":"https://example.com/a.exe"}
                """;
            const string tagsJson = """
                [{"name":"v1.9.18","zipball_url":"https://api.github.com/repos/acme/ai-collab-hub/zipball/v1.9.18"}]
                """;
            var ghRel = AppUpdateService.ParseReleaseJson(githubJson);
            var geRel = AppUpdateService.ParseReleaseJson(giteeJson);
            var customRel = AppUpdateService.ParseReleaseJson(customJson);
            var tagRel = AppUpdateService.ParseReleaseJson(tagsJson);
            AssertTrue(
                ghRel != null && ghRel.Version == "1.9.17" &&
                ghRel.AssetDownloadUrl == "https://example.com/hub.exe" &&
                ghRel.Notes.Contains("修复更新", StringComparison.Ordinal) &&
                ghRel.HtmlUrl.Contains("/releases/tag/v1.9.17", StringComparison.Ordinal) &&
                geRel != null && geRel.Version == "1.9.17" && geRel.AssetName == "app.exe" &&
                geRel.Notes.Contains("码云说明", StringComparison.Ordinal) &&
                customRel != null && customRel.Version == "1.9.20" &&
                customRel.Notes.Contains("自定义日志", StringComparison.Ordinal) &&
                customRel.AssetDownloadUrl == "https://example.com/a.exe" &&
                tagRel != null && tagRel.Version == "1.9.18" && tagRel.TagName == "v1.9.18",
                "Release JSON（GitHub / Gitee / Custom / tags）解析通过");

            var gitRoot = Path.Combine(outDir, "fake_git_repo");
            Directory.CreateDirectory(Path.Combine(gitRoot, "src", "nested"));
            Directory.CreateDirectory(Path.Combine(gitRoot, ".git"));
            File.WriteAllText(
                Path.Combine(gitRoot, ".git", "config"),
                "[core]\n\trepositoryformatversion = 0\n[remote \"origin\"]\n\turl = git@github.com:acme/from-git.git\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n",
                new UTF8Encoding(false));
            var origin = AppUpdateService.TryDetectOriginRepoUrlFromGit(Path.Combine(gitRoot, "src", "nested"));
            AssertTrue(
                string.Equals(origin, "git@github.com:acme/from-git.git", StringComparison.Ordinal),
                $"只读识别 .git/config origin (url={origin})");

            var bare = Path.Combine(outDir, "no_git_leaf");
            Directory.CreateDirectory(bare);
            var needsRepo = AppUpdateService.CheckForUpdateAsync("", bare, probeGitOrigin: true, maxParentHops: 0)
                .GetAwaiter().GetResult();
            var isolated = AppUpdateService.CheckForUpdateAsync("https://github.com/acme/ai-collab-hub")
                .GetAwaiter().GetResult();
            var isolatedApply = AppUpdateService.DownloadAndApplyExeUpdateAsync("https://example.com/app.exe", "https://example.com/rel")
                .GetAwaiter().GetResult();
            AssertTrue(
                needsRepo.Status == AppUpdateStatus.NeedsRepoConfig &&
                needsRepo.Message.Contains("更新源", StringComparison.Ordinal) &&
                AppPaths.IsIsolatedTest &&
                isolated.Status == AppUpdateStatus.IsolatedSkipped &&
                isolated.Message.Contains("隔离测试", StringComparison.Ordinal) &&
                !isolatedApply.RestartScheduled &&
                isolatedApply.Message.Contains("隔离测试", StringComparison.Ordinal) &&
                AppUpdateService.HasMzHeader(new byte[] { 0x4D, 0x5A, 0x90, 0x00 }) &&
                !AppUpdateService.HasMzHeader(new byte[] { 0x3C, 0x68 }) &&
                !AppUpdateService.HasMzHeader(Array.Empty<byte>()),
                "未配置返回 NeedsRepoConfig，隔离测试不发外网且拒绝替换程序");
            AssertTrue(
                AppUpdateService.DefaultOfficialRepoUrl == "https://github.com/tianmenglele/ai-deck",
                "DefaultOfficialRepoUrl 等于官方仓库地址");
            AssertTrue(
                AppUpdateService.ResolveEffectiveRepoUrl("") == AppUpdateService.DefaultOfficialRepoUrl &&
                AppUpdateService.ResolveEffectiveRepoUrl("   ") == AppUpdateService.DefaultOfficialRepoUrl &&
                AppUpdateService.ResolveEffectiveRepoUrl("\t") == AppUpdateService.DefaultOfficialRepoUrl &&
                AppUpdateService.ResolveEffectiveRepoUrl("  https://github.com/acme/repo  ") == "https://github.com/acme/repo",
                "ResolveEffectiveRepoUrl 空白回落官方仓库，非空则 Trim");

            var freshCfg = new UserHubConfig();
            AssertTrue(
                freshCfg.AutoCheckAppUpdateOnStartup &&
                string.IsNullOrEmpty(freshCfg.AppUpdateRepoUrl),
                "默认启动自动检查更新，且更新源为空");

            bool firstLock = LongActionTracker.TryBegin(LongActionTracker.AppUpdate, "检查更新");
            bool secondLock = LongActionTracker.TryBegin(LongActionTracker.AppUpdate, "检查更新");
            AssertTrue(
                LongActionTracker.AppUpdate == "app_update" &&
                firstLock && !secondLock && LongActionTracker.IsBusy(LongActionTracker.AppUpdate),
                "AppUpdate 互斥：占用中重复开始必须失败");
            LongActionTracker.End(LongActionTracker.AppUpdate);
            bool thirdLock = LongActionTracker.TryBegin(LongActionTracker.AppUpdate, "检查更新");
            LongActionTracker.End(LongActionTracker.AppUpdate);
            AssertTrue(thirdLock && !LongActionTracker.IsBusy(LongActionTracker.AppUpdate),
                "AppUpdate 互斥：结束后可再次开始");
        }

        var freshOnboardLate = new UserHubConfig();
        AssertTrue(
            OnboardingWizardDialog.ShouldAutoShowOnboarding(freshOnboardLate, configFileExistedBeforeLoad: false, isIsolatedTest: false),
            "全新启动 ShouldAutoShowOnboarding 返回 true");
        freshOnboardLate.HasCompletedOnboarding = true;
        AssertTrue(
            !OnboardingWizardDialog.ShouldAutoShowOnboarding(freshOnboardLate, configFileExistedBeforeLoad: false, isIsolatedTest: false),
            "已完成引导后 ShouldAutoShowOnboarding 返回 false");
        var legacyOnboard = new UserHubConfig
        {
            Agents = new List<AgentRosterEntry>
            {
                new() { Id = "cursor", Name = "Cursor", Enabled = true, Detected = true }
            }
        };
        AssertTrue(
            !OnboardingWizardDialog.ShouldAutoShowOnboarding(legacyOnboard, configFileExistedBeforeLoad: true, isIsolatedTest: false),
            "已有老配置且含启用 AI 时 ShouldAutoShowOnboarding 返回 false");
        AssertTrue(
            !OnboardingWizardDialog.ShouldAutoShowOnboarding(new UserHubConfig(), configFileExistedBeforeLoad: false, isIsolatedTest: true),
            "隔离测试 ShouldAutoShowOnboarding 返回 false");

        string blankBundle = Path.Combine(Path.GetTempPath(), "aideck-blank-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bundle = RegistryService.ReleaseEmbeddedBundleToBlankDirectory(blankBundle);
            AssertTrue(
                bundle.ReleasedFileCount >= 6 && bundle.DetectorCount >= 40 && bundle.ProbeFieldsPresent,
                $"空白目录释放 5 份 registry JSON 与脚本并可加载 (files={bundle.ReleasedFileCount}, detectors={bundle.DetectorCount}, probes={bundle.ProbeFieldsPresent})");
        }
        finally
        {
            try { if (Directory.Exists(blankBundle)) Directory.Delete(blankBundle, true); } catch { }
        }

        string prevLauncherData = AppPaths.UserDataDir;
        string launcherScratch = Path.Combine(Path.GetTempPath(), "aideck-single-" + Guid.NewGuid().ToString("N"));
        AppPaths.SetDataDirOverride(launcherScratch);
        try
        {
            string launcher = SyncService.ResolveStableHubLauncherPath(standaloneSingleFile: true);
            string nativeLauncher = launcher.Replace('/', Path.DirectorySeparatorChar);
            AssertTrue(
                !launcher.Contains("Godot", StringComparison.OrdinalIgnoreCase) &&
                !launcher.Contains("配套工具", StringComparison.Ordinal) &&
                File.Exists(nativeLauncher),
                $"单文件独立模式启动脚本不指向开发仓库且真实存在 ({launcher})");
        }
        finally
        {
            AppPaths.SetDataDirOverride(prevLauncherData);
        }

        AssertTrue(AutoDispatchService.DesktopLiveProbeDisplayStaleMs >= 10000, $"桌面忙碌展示保鲜期不少于 10 秒 (ms={AutoDispatchService.DesktopLiveProbeDisplayStaleMs})");
        AutoDispatchService.SetDesktopBusyOverrideForTest(
            "cursor",
            new DesktopBusyProbeResult(true, "长任务", "XX", DateTime.UtcNow.AddMilliseconds(-2500), ActiveTaskCount: 2));
        try
        {
            var staleMap = AutoDispatchService.ReadMergedGlobalWorkStatesWithDesktopLive();
            AssertTrue(
                staleMap.TryGetValue("cursor", out var staleCursor) &&
                staleCursor.Status == "working" &&
                staleCursor.ActiveTaskCount >= 1,
                $"2500ms 忙碌缓存不掉回空闲 (status={ (staleMap.TryGetValue("cursor", out var s2) ? s2.Status : "missing") }, count={ (staleMap.TryGetValue("cursor", out var s3) ? s3.ActiveTaskCount : -1) })");
        }
        finally
        {
            AutoDispatchService.SetDesktopBusyOverrideForTest("cursor", null);
        }

        var wizReg = new RegistryService();
        wizReg.LoadAll();
        if ((wizReg.UserConfig.Agents?.Count ?? 0) == 0)
        {
            wizReg.UserConfig.Agents ??= new List<AgentRosterEntry>();
            wizReg.UserConfig.Agents.Add(new AgentRosterEntry { Id = "antigravity", Name = "Antigravity", Detected = true, Enabled = true, Category = "local_agent", Evidence = "自检种子" });
            wizReg.UserConfig.Agents.Add(new AgentRosterEntry { Id = "cursor", Name = "Cursor", Detected = true, Enabled = true, Category = "local_ide", Evidence = "自检种子" });
        }
        var wiz = new OnboardingWizardDialog(wizReg, new DiscoveryService())
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowActivated = false
        };
        wiz.Show();
        wiz.SwitchStepForTest(1);
        wiz.UpdateLayout();
        int step1Tiles = CountToolIconTiles(wiz);
        wiz.SwitchStepForTest(2);
        wiz.UpdateLayout();
        var wizFrame = wiz.FindName("WizardOuterFrame") as Border;
        int step2Tiles = CountToolIconTiles(wiz);
        int step2Tips = CountToolIconTilesWithTooltip(wiz);
        AssertTrue(
            wizFrame != null &&
            wizFrame.BorderThickness.Left >= 1.8 &&
            wizFrame.BorderThickness.Top >= 1.8 &&
            step1Tiles >= 1 &&
            step2Tiles >= 1 &&
            step2Tips == step2Tiles &&
            !string.IsNullOrWhiteSpace(wiz.SelectedMasterIdForTest),
            $"新手引导亮边框、图标瓷砖与主脑自动选中 (frame={wizFrame?.BorderThickness.Left}, step1={step1Tiles}, step2={step2Tiles}, tips={step2Tips}, master={wiz.SelectedMasterIdForTest})");
        wiz.SwitchStepForTest(3);
        AssertTrue(wiz.GetCurrentStepForTest() == 3, "新手向导可切换到第 3 步");
        wiz.CompleteForTest(skip: false);
        AssertTrue(wizReg.UserConfig.HasCompletedOnboarding, "CompleteForTest(skip: false) 后 HasCompletedOnboarding 为 true");
        wiz.Close();

        var poolReg = new RegistryService();
        poolReg.UserConfig.Agents.Add(new AgentRosterEntry { Id = "antigravity", Name = "Antigravity", Detected = true, Enabled = true, Category = "local_agent", Evidence = "种子甲" });
        poolReg.UserConfig.Agents.Add(new AgentRosterEntry { Id = "cursor", Name = "Cursor", Detected = true, Enabled = true, Category = "local_ide", Evidence = "种子乙" });
        poolReg.UserConfig.Agents.Add(new AgentRosterEntry { Id = "windsurf", Name = "Windsurf", Detected = true, Enabled = false, Category = "local_ide", Evidence = "种子丙" });
        poolReg.UserConfig.Agents.Add(new AgentRosterEntry { Id = "grok_bot", Name = "Grok Bot", Detected = true, Enabled = false, Category = "local_agent", Evidence = "种子丁" });
        var poolWiz = new OnboardingWizardDialog(poolReg, new DiscoveryService())
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowActivated = false
        };
        poolWiz.Show();
        poolWiz.UpdateLayout();
        var poolNext = FindButtonByContent(poolWiz, "下一步");
        AssertTrue(poolNext != null && poolWiz.GetCurrentStepForTest() == 1, "过滤探针从 Step 1 出发");
        poolNext!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        poolWiz.UpdateLayout();
        var poolFrame = poolWiz.FindName("WizardOuterFrame") as Border;
        AssertTrue(
            poolWiz.GetCurrentStepForTest() == 2 &&
            poolWiz.Step2MasterTileCountForTest == 2 &&
            poolWiz.Step2WorkerTileCountForTest == 2 &&
            poolFrame != null &&
            poolFrame.CornerRadius.TopLeft == 8 &&
            poolFrame.BorderThickness.Top >= 1.8 &&
            !ContainsSquareTopGlow(poolFrame),
            $"Step 2 只渲染已启用工具且外框无直角顶条 (step={poolWiz.GetCurrentStepForTest()}, master={poolWiz.Step2MasterTileCountForTest}, worker={poolWiz.Step2WorkerTileCountForTest}, radius={poolFrame?.CornerRadius.TopLeft}, thick={poolFrame?.BorderThickness.Top})");
        foreach (var agent in poolReg.UserConfig.Agents)
            agent.Enabled = false;
        poolWiz.SwitchStepForTest(1);
        poolWiz.UpdateLayout();
        var blockedNext = FindButtonByContent(poolWiz, "下一步");
        AssertTrue(blockedNext != null, "回到 Step 1 后仍能找到下一步");
        blockedNext!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        poolWiz.UpdateLayout();
        AssertTrue(
            poolWiz.GetCurrentStepForTest() == 1 &&
            VisualTreeContainsText(poolWiz, "请至少勾选 1 个要启用的 AI 工具"),
            $"Step 1 全不选时停留并提示 (step={poolWiz.GetCurrentStepForTest()})");
        poolWiz.Close();

        var tipAgent = new AgentRosterEntry
        {
            Id = "probe_tip",
            Name = "提示探针",
            Evidence = @"C:\VeryLong\Install\Path\That\Exceeds\FortyFour\Characters\tool.exe",
            TagScores = new Dictionary<string, int>
            {
                ["deep_bug_hunt"] = 5,
                ["cross_repo_impl"] = 4
            }
        };
        var detailTip = Dialogs.BuildToolDetailTooltip(tipAgent, false);
        string tipText = DumpTooltipText(detailTip);
        AssertTrue(
            detailTip.Width <= 310 &&
            tipText.Contains("深度排障", StringComparison.Ordinal) &&
            tipText.Contains("跨目录实装", StringComparison.Ordinal) &&
            !tipText.Contains("deep_bug_hunt", StringComparison.Ordinal) &&
            !tipText.Contains("cross_repo_impl", StringComparison.Ordinal),
            $"悬浮提示中文能力且宽度不超过 310 (width={detailTip.Width}, text={tipText.Replace('\n', '|')})");

        var navProbe = new MainWindow(reg, suppressAutoShowFloating: true)
        {
            Width = 960,
            Height = 640,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowActivated = false
        };
        navProbe.Show();
        navProbe.UpdateLayout();
        var navExp = navProbe.FindName("NavAdvancedExpander") as Expander;
        AssertTrue(navExp != null && navExp.IsExpanded == false, "新窗口初始化时 NavAdvancedExpander 收起");
        navProbe.SwitchToTabForTest(3);
        navProbe.UpdateLayout();
        AssertTrue(navExp!.IsExpanded, "SwitchToTabForTest(3) 后高级分组展开");
        navExp.IsExpanded = false;
        navProbe.SwitchToTabForTest(4);
        navProbe.UpdateLayout();
        AssertTrue(navExp.IsExpanded, "SwitchToTabForTest(4) 后高级分组展开");
        var probeNavSettings = navProbe.FindName("NavAppSettings") as RadioButton;
        AssertTrue(probeNavSettings != null && probeNavSettings.Visibility == Visibility.Collapsed, "窄窗探针左侧设置项保持折叠隐藏");
        navProbe.Width = 960;
        navProbe.UpdateLayout();
        var probeBrand = navProbe.FindName("HeaderBrandText") as TextBlock;
        var probeVer = navProbe.FindName("HeaderVersionText") as TextBlock;
        var probeOnboard = navProbe.FindName("HeaderOnboardingBtn") as Button;
        var probeFloat = navProbe.FindName("ToggleFloatingBtn") as Button;
        var probeSettingsBtn = navProbe.FindName("TopOpenSettingsBtn") as Button;
        var probeHeader = navProbe.FindName("HeaderBarGrid") as Grid;
        double probeUsed = 0;
        if (probeHeader != null)
        {
            foreach (UIElement child in probeHeader.Children)
            {
                if (child is FrameworkElement fe && fe.Visibility == Visibility.Visible)
                    probeUsed += fe.ActualWidth;
            }
        }
        AssertTrue(
            probeBrand != null && probeBrand.Text.Contains("AIDeck", StringComparison.Ordinal) && probeBrand.ActualWidth > 40 &&
            probeVer != null && probeVer.Text == "v1.9.16" && probeVer.ActualWidth > 20 &&
            probeOnboard != null && probeOnboard.IsVisible && probeOnboard.ActualWidth >= 24 &&
            ((System.Windows.Automation.AutomationProperties.GetName(probeOnboard) ?? "").Contains("新手向导")
             || (probeOnboard.ToolTip as string ?? "").Contains("新手向导")) &&
            probeFloat != null && probeFloat.IsVisible && probeFloat.ActualWidth >= 24 &&
            probeSettingsBtn != null && probeSettingsBtn.IsVisible && probeSettingsBtn.ActualWidth >= 24 &&
            probeHeader != null && probeUsed <= probeHeader.ActualWidth + 1.0,
            $"960 宽顶栏 AIDeck、v1.9.16、新手向导、悬浮窗、设置均可见且不截断 (Used={probeUsed:0}, HeaderW={probeHeader?.ActualWidth:0}, OnboardW={probeOnboard?.ActualWidth:0}, FloatW={probeFloat?.ActualWidth:0}, SettingsW={probeSettingsBtn?.ActualWidth:0})");

        AssertTrue(!navProbe.IsModalBackdropVisibleForTest, "灯箱默认关闭");
        navProbe.PushModalBackdrop();
        navProbe.PushModalBackdrop();
        AssertTrue(navProbe.IsModalBackdropVisibleForTest, "连续推入灯箱后暗幕可见");
        navProbe.PopModalBackdrop();
        AssertTrue(navProbe.IsModalBackdropVisibleForTest, "引用计数未归零时灯箱保持");
        navProbe.PopModalBackdrop();
        AssertTrue(!navProbe.IsModalBackdropVisibleForTest, "引用计数归零后灯箱关闭");

        var ringProbe = new FloatingOverlayWindow(reg, navProbe)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -14000,
            Top = -14000,
            ShowActivated = false
        };
        ringProbe.Show();
        ringProbe.ApplyCollapsedVisual(false, preserveRightAnchor: false);
        ringProbe.RefreshOverlayData(force: true, forceRebuildRows: true);
        var rowBefore = ringProbe.CaptureAgentRowIdentitiesForTest();
        ringProbe.RefreshOverlayData(force: true);
        var rowAfter = ringProbe.CaptureAgentRowIdentitiesForTest();
        bool rowsKept = rowBefore.Length > 0 && rowBefore.Length == rowAfter.Length;
        for (int i = 0; i < rowBefore.Length && rowsKept; i++)
            rowsKept = ReferenceEquals(rowBefore[i], rowAfter[i]);
        var codingAgent = new AgentRosterEntry { Id = "cursor", Name = "Cursor", Enabled = true };
        var codingRich = FloatingOverlayWindow.ResolveRichStatusForTest(
            codingAgent,
            new LiveAgentWorkState
            {
                AgentId = "cursor",
                Status = "working",
                WorkflowPhase = "executing",
                TaskSummary = "改码",
                LatestLogTail = "Editing FloatingOverlayWindow.StatusFx.cs"
            },
            true, true, false);
        var codingAvatar = ringProbe.BuildRichAvatarForTest(codingAgent, codingRich, 32, 1);
        var codingRing = FindTaggedEllipse(codingAvatar, "avatar_fx_ring");
        var codingRot = codingRing?.RenderTransform as RotateTransform;
        var badge1 = ringProbe.BuildRichAvatarForTest(codingAgent, codingRich, 32, 1);
        var badge3 = ringProbe.BuildRichAvatarForTest(codingAgent, codingRich, 32, 3);
        var badge12 = ringProbe.BuildRichAvatarForTest(codingAgent, codingRich, 32, 12);
        var mark1 = FindTaggedBorder(badge1, "task_count:1");
        var mark3 = FindTaggedBorder(badge3, "task_count:3");
        var mark12 = FindTaggedBorder(badge12, "task_count:12");
        AssertTrue(
            codingRich.Kind == FloatingRichFxKind.Coding &&
            codingRing != null &&
            codingRing.RenderTransformOrigin.X == 0.5 &&
            codingRing.RenderTransformOrigin.Y == 0.5 &&
            codingRot != null && codingRot.CenterX == 0 && codingRot.CenterY == 0 &&
            rowsKept &&
            mark1 != null && mark1.Visibility == Visibility.Collapsed &&
            mark3 != null && mark3.Visibility == Visibility.Visible && (mark3.Child as TextBlock)?.Text == "3" &&
            mark12 != null && mark12.Visibility == Visibility.Visible && (mark12.Child as TextBlock)?.Text == "9+",
            $"Coding 圆环原点、强制刷新不重建行、多任务角标 (kind={codingRich.Kind}, origin={codingRing?.RenderTransformOrigin}, center={codingRot?.CenterX},{codingRot?.CenterY}, rows={rowBefore.Length}/{rowsKept}, t1={mark1?.Visibility}, t3={(mark3?.Child as TextBlock)?.Text}, t12={(mark12?.Child as TextBlock)?.Text})");
        var masterBusy = FloatingOverlayWindow.ResolveRichStatusForTest(
            new AgentRosterEntry { Id = "antigravity", Name = "Antigravity", Enabled = true },
            new LiveAgentWorkState { AgentId = "antigravity", Status = "working", WorkflowPhase = "executing", TaskSummary = "拆解任务" },
            true, true, true, activeWorkerOrNull: null);
        AssertTrue(
            masterBusy.Kind == FloatingRichFxKind.MasterAnalyzing && masterBusy.IsActiveWork,
            $"主脑 working 且无子执行者时保持深度分析 (kind={masterBusy.Kind}, badge={masterBusy.BadgeText})");

        var inplaceAgent = reg.UserConfig.Agents.FirstOrDefault(a => a.Enabled && a.Id == "cursor")
                           ?? reg.UserConfig.Agents.FirstOrDefault(a => a.Enabled);
        byte[]? inplaceMapBackup = File.Exists(AutoDispatchService.LiveStateMapPath)
            ? File.ReadAllBytes(AutoDispatchService.LiveStateMapPath)
            : null;
        try
        {
            if (inplaceAgent != null)
            {
                var inplaceMap = new Dictionary<string, LiveAgentWorkState>(StringComparer.OrdinalIgnoreCase)
                {
                    [inplaceAgent.Id] = new LiveAgentWorkState
                    {
                        AgentId = inplaceAgent.Id,
                        AgentName = inplaceAgent.Name,
                        Status = "working",
                        WorkflowPhase = "executing",
                        TaskSummary = "原地增量刷新验收",
                        ProjectName = "XX",
                        UpdatedAt = "10:00:01",
                        UpdatedUtcTicks = 10_000,
                        DispatcherPid = Environment.ProcessId
                    }
                };
                RegistryService.WriteJsonAtomic(AutoDispatchService.LiveStateMapPath, inplaceMap);
                AutoDispatchService.SetDesktopBusyOverrideForTest(inplaceAgent.Id, null);
                ringProbe.RefreshOverlayData(force: true);
                ringProbe.UpdateLayout();
                var badgeHost = ringProbe.FindName("HeaderProjBadgeHost") as FrameworkElement;
                var badgeText = ringProbe.FindName("HeaderProjBadge") as TextBlock;
                AssertTrue(
                    badgeHost != null && badgeHost.Visibility == Visibility.Collapsed &&
                    (badgeText == null || string.IsNullOrEmpty(badgeText.Text)),
                    $"working 且 ProjectName=XX 时顶栏项目徽标保持隐藏 (vis={badgeHost?.Visibility}, text={badgeText?.Text})");

                navProbe.SwitchToTabForTest(1);
                navProbe.RefreshAllViews();
                navProbe.UpdateLayout();
                object? cardBefore = navProbe.OverviewAgentsCards.Children.Count > 0
                    ? navProbe.OverviewAgentsCards.Children[0]
                    : null;
                Border? nodeBefore = null;
                if (navProbe.WorkflowDiagramCanvas != null)
                {
                    foreach (UIElement child in navProbe.WorkflowDiagramCanvas.Children)
                    {
                        if (child is Border node && (node.Tag as string) == "MindMapNode:master")
                        {
                            nodeBefore = node;
                            break;
                        }
                    }
                }
                Border? rowBeforeCount = ringProbe.CaptureAgentRowIdentitiesForTest()
                    .OfType<Border>()
                    .FirstOrDefault(b => (b.Tag as string) == inplaceAgent.Id);

                inplaceMap[inplaceAgent.Id].UpdatedAt = "10:00:02";
                inplaceMap[inplaceAgent.Id].UpdatedUtcTicks = 20_000;
                RegistryService.WriteJsonAtomic(AutoDispatchService.LiveStateMapPath, inplaceMap);
                navProbe.RefreshAllViews();
                navProbe.UpdateLayout();
                object? cardAfter = navProbe.OverviewAgentsCards.Children.Count > 0
                    ? navProbe.OverviewAgentsCards.Children[0]
                    : null;
                Border? nodeAfter = null;
                if (navProbe.WorkflowDiagramCanvas != null)
                {
                    foreach (UIElement child in navProbe.WorkflowDiagramCanvas.Children)
                    {
                        if (child is Border node && (node.Tag as string) == "MindMapNode:master")
                        {
                            nodeAfter = node;
                            break;
                        }
                    }
                }
                AssertTrue(
                    cardBefore != null && ReferenceEquals(cardBefore, cardAfter) &&
                    nodeBefore != null && ReferenceEquals(nodeBefore, nodeAfter),
                    $"仅 UpdatedAt 变化时总览卡片与脑图节点引用保持不变 (card={cardBefore != null && ReferenceEquals(cardBefore, cardAfter)}, node={nodeBefore != null && ReferenceEquals(nodeBefore, nodeAfter)})");

                AutoDispatchService.SetDesktopBusyOverrideForTest(
                    inplaceAgent.Id,
                    new DesktopBusyProbeResult(
                        true,
                        "任务乙",
                        "XX",
                        DateTime.UtcNow,
                        ActiveTaskCount: 2,
                        ActiveTaskTitles: new[] { "任务甲", "任务乙" }));
                ringProbe.RefreshOverlayData(force: true);
                ringProbe.UpdateLayout();
                Border? rowAfterCount = ringProbe.CaptureAgentRowIdentitiesForTest()
                    .OfType<Border>()
                    .FirstOrDefault(b => (b.Tag as string) == inplaceAgent.Id);
                Border? countBadge = rowAfterCount == null ? null : FindNamedBorder(rowAfterCount, "AvatarTaskCountBadge");
                string tipDump = DumpTooltipText(rowAfterCount?.ToolTip);
                AssertTrue(
                    rowBeforeCount != null && ReferenceEquals(rowBeforeCount, rowAfterCount) &&
                    countBadge != null && countBadge.Visibility == Visibility.Visible &&
                    (countBadge.Child as TextBlock)?.Text == "2" &&
                    (countBadge.Tag as string) == "task_count:2" &&
                    tipDump.Contains("任务甲", StringComparison.Ordinal) &&
                    tipDump.Contains("任务乙", StringComparison.Ordinal) &&
                    tipDump.Contains("并行任务", StringComparison.Ordinal),
                    $"并发 1→2 原地刷新 (same={rowBeforeCount != null && ReferenceEquals(rowBeforeCount, rowAfterCount)}, badge={(countBadge?.Child as TextBlock)?.Text}/{countBadge?.Visibility}, tip={tipDump.Replace('\n', '|')})");
            }
        }
        finally
        {
            if (inplaceAgent != null)
                AutoDispatchService.SetDesktopBusyOverrideForTest(inplaceAgent.Id, null);
            if (inplaceMapBackup != null) File.WriteAllBytes(AutoDispatchService.LiveStateMapPath, inplaceMapBackup);
            else if (File.Exists(AutoDispatchService.LiveStateMapPath)) File.Delete(AutoDispatchService.LiveStateMapPath);
        }
        ringProbe.Close();
        navProbe.Close();

        var reportPath = Path.Combine(outDir, "self_test_report.txt");
        File.WriteAllLines(reportPath, log, new UTF8Encoding(false));
        foreach (var line in log) Console.WriteLine(line);
        Console.WriteLine("ALL_SELF_TESTS_PASSED");
        app.Shutdown();
        return 0;
    }

    private static int RunCaptureLive(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var app = CreateWpfApp();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var reg = new RegistryService();
        reg.LoadAll();

        var win = new MainWindow(reg, suppressAutoShowFloating: true)
        {
            Width = 1220,
            Height = 760,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowActivated = false
        };
        win.Show();
        win.PreloadCachesForLiveCapture();

        var tabNames = new[]
        {
            "live_01_overview",
            "live_02_dialogs",
            "live_03_sync",
            "live_04_ops",
            "live_05_settings",
            "live_06_app_settings"
        };
        for (int idx = 1; idx <= 6; idx++)
        {
            win.SwitchToTabForTest(idx);
            if (idx == 1)
            {
                // 协同工作台首屏：刷新动态流程运行图后再截，确保工作流图清晰可见
                win.RefreshWorkflowDiagram();
                win.UpdateLayout();
            }
            if (idx == 5)
            {
                win.SwitchSettingsSubViewForTest(false);
                win.UpdateLayout();
                // 确保 RosterFilterBar 处于 all，胶囊开关与顶部「工作流开关」清晰可见
                var filterBar = win.FindName("RosterFilterBar") as System.Windows.Controls.WrapPanel
                                ?? win.RosterFilterBar;
                if (filterBar != null)
                {
                    var allChip = filterBar.Children.OfType<System.Windows.Controls.Button>()
                        .FirstOrDefault(b => b.Tag is string t &&
                                             string.Equals(t, "RosterFilter:all", StringComparison.Ordinal));
                    allChip?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    win.UpdateLayout();
                }
            }
            if (idx == 6)
            {
                win.SwitchSettingsTabForTest("general");
                win.UpdateLayout();
            }
            win.UpdateLayout();
            CaptureElementPng(win, 1220, 760, Path.Combine(outDir, $"{tabNames[idx - 1]}.png"));
            if (idx == 6)
            {
                win.SwitchSettingsTabForTest("appearance");
                win.UpdateLayout();
                CaptureElementPng(win, 1220, 760, Path.Combine(outDir, "live_06c_settings_appearance.png"));
                win.SwitchSettingsTabForTest("about");
                win.UpdateLayout();
                CaptureElementPng(win, 1220, 760, Path.Combine(outDir, "live_06d_settings_about.png"));
                win.SwitchSettingsTabForTest("general");
            }
            if (idx == 1)
            {
                // 额外截一张「执行中」流光高亮运行态，便于目视验收动态流程图
                win.SetWorkflowDiagramPreviewPhaseForTest("executing", round: 1, maxRounds: 2, activeWorkerId: "cursor");
                win.UpdateLayout();
                CaptureElementPng(win, 1220, 760, Path.Combine(outDir, "live_01b_overview_active_flow.png"));
                win.ClearWorkflowDiagramPreviewPhaseForTest();
                win.UpdateLayout();
            }
            if (idx == 5)
            {
                win.SwitchSettingsSubViewForTest(true);
                win.UpdateLayout();
                CaptureElementPng(win, 1220, 760, Path.Combine(outDir, "live_05b_settings_routes.png"));
                CaptureElementPng(win, 1220, 760, Path.Combine(outDir, "live_06b_routing_work_categories.png"));
                win.SwitchSettingsSubViewForTest(false);
            }
        }

        // 截图桌面悬浮框（默认自适应 Full + Compact 紧凑 + Ultra 极简 + 多状态全貌预览 + 收起胶囊态）
        bool origCollapsed = reg.UserConfig.FloatingWindowCollapsed;
        string origDensity = reg.UserConfig.FloatingLayoutMode;
        double? origCustomW = reg.UserConfig.FloatingCustomWidth;
        double? origCustomH = reg.UserConfig.FloatingCustomHeight;
        double? origUltraCustomW = reg.UserConfig.FloatingUltraCustomWidth;
        double? origCollapsedCustomW = reg.UserConfig.FloatingCollapsedCustomWidth;
        var origAgentsSnapshot = reg.UserConfig.Agents.Select(a => a).ToList();
        byte[]? origLiveMapBytes = File.Exists(AutoDispatchService.LiveStateMapPath)
            ? File.ReadAllBytes(AutoDispatchService.LiveStateMapPath)
            : null;
        byte[]? origLiveSingleBytes = File.Exists(AutoDispatchService.LiveStateJsonPath)
            ? File.ReadAllBytes(AutoDispatchService.LiveStateJsonPath)
            : null;

        FloatingOverlayWindow? floatWin = null;
        try
        {
            static int MeasureExactOverlayHeight(Window w, int fixedW)
            {
                var root = VisualTreeHelper.GetChildrenCount(w) > 0 && VisualTreeHelper.GetChild(w, 0) is UIElement rc
                    ? rc
                    : (UIElement)w.Content;
                root.Measure(new Size(fixedW, double.PositiveInfinity));
                return Math.Max(46, (int)Math.Ceiling(root.DesiredSize.Height));
            }

            reg.UserConfig.FloatingWindowCollapsed = false;
            reg.UserConfig.FloatingLayoutMode = "full";
            floatWin = new FloatingOverlayWindow(reg, win)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowActivated = false
            };
            floatWin.Show();
            floatWin.SetCaptureQuietModeForTest(true);
            floatWin.ResetAdaptiveHeightForTest();
            floatWin.ApplyCustomSizeForTest(420, null, collapsed: false);
            floatWin.ApplyCollapsedVisual(false, preserveRightAnchor: false);
            floatWin.SetOpacityBarVisibleForPreview(false);
            floatWin.RefreshOverlayData(force: true);
            floatWin.UpdateLayout();
            int floatH = MeasureExactOverlayHeight(floatWin, 420);
            CaptureElementPng(floatWin, 420, floatH, Path.Combine(outDir, "live_07_floating_expanded.png"));

            // Compact 紧凑模式（真实 SizeToContent 自适应高度，上下边距对称 8px）
            reg.UserConfig.FloatingLayoutMode = "compact";
            reg.UserConfig.FloatingCustomHeight = null;
            reg.UserConfig.FloatingCustomWidth = null;
            floatWin.ApplyUserPreferencesFromSettings();
            floatWin.RefreshOverlayData(force: true);
            floatWin.UpdateLayout();
            int compactW = (int)Math.Round(floatWin.ActualWidth > 40 ? floatWin.ActualWidth : FloatingOverlayWindow.CompactWidthDefault);
            int compactH = MeasureExactOverlayHeight(floatWin, compactW);
            CaptureElementPng(floatWin, compactW, compactH, Path.Combine(outDir, "live_07b_floating_compact.png"));

            // Ultra 极简单行模式：模拟「收起胶囊 → 展开到极简行」真实路径，活跃长徽章 + NX 项目徽章下按真实 ActualWidth 实拍
            {
                var ultraTicks = DateTime.UtcNow.Ticks;
                var ultraNow = DateTime.Now.ToString("HH:mm:ss");
                var ultraMap = new Dictionary<string, LiveAgentWorkState>(StringComparer.OrdinalIgnoreCase)
                {
                    ["antigravity"] = new LiveAgentWorkState
                    {
                        AgentId = "antigravity", AgentName = "Antigravity", Status = "working",
                        WorkflowPhase = "dispatching", TaskSummary = "派单调度 Cursor 修复悬浮窗 Ultra 宽度",
                        ProjectName = "NX", UpdatedAt = ultraNow, UpdatedUtcTicks = ultraTicks, DispatcherPid = Environment.ProcessId
                    },
                    ["cursor"] = new LiveAgentWorkState
                    {
                        AgentId = "cursor", AgentName = "Cursor", Status = "working",
                        WorkflowPhase = "executing", LatestLogTail = "Editing Program.cs\nRunning build check",
                        TaskSummary = "编码实操修改 Program.cs 往返宽度自检",
                        ProjectName = "NX", UpdatedAt = ultraNow, UpdatedUtcTicks = ultraTicks, DispatcherPid = Environment.ProcessId
                    },
                    ["grok_bot"] = new LiveAgentWorkState
                    {
                        AgentId = "grok_bot", AgentName = "Grok Bot", Status = "done",
                        WorkflowPhase = "verified_delivered", TaskSummary = "已交付 Ultra 防裁切布局审查",
                        ProjectName = "NX", UpdatedAt = ultraNow, UpdatedUtcTicks = ultraTicks, DispatcherPid = Environment.ProcessId
                    }
                };
                string ultraOrigLayout = reg.UserConfig.FloatingLayoutMode;
                var ultraOrigAgents = reg.UserConfig.Agents;
                try
                {
                    RegistryService.WriteJsonAtomic(AutoDispatchService.LiveStateMapPath, ultraMap);
                    if (File.Exists(AutoDispatchService.LiveStateJsonPath)) File.Delete(AutoDispatchService.LiveStateJsonPath);
                    reg.UserConfig.Agents = new List<AgentRosterEntry>
                    {
                        new() { Id = "antigravity", Name = "Antigravity", Enabled = true, PreferredEffortTier = "auto" },
                        new() { Id = "cursor", Name = "Cursor", Enabled = true, PreferredEffortTier = "auto" },
                        new() { Id = "grok_bot", Name = "Grok Bot", Enabled = true, PreferredEffortTier = "auto" }
                    };
                    reg.UserConfig.FloatingCustomHeight = null;
                    reg.UserConfig.FloatingUltraCustomWidth = null;
                    reg.UserConfig.FloatingCollapsedCustomWidth = null;
                    floatWin.ResetAdaptiveHeightForTest();

                    reg.UserConfig.FloatingLayoutMode = "ultra";
                    reg.UserConfig.FloatingWindowCollapsed = true;
                    floatWin.ApplyCollapsedVisual(true, preserveRightAnchor: false);
                    floatWin.RefreshOverlayData(force: true);
                    floatWin.UpdateLayout();

                    reg.UserConfig.FloatingWindowCollapsed = false;
                    floatWin.ApplyCollapsedVisual(false, preserveRightAnchor: false);
                    floatWin.RefreshOverlayData(force: true);
                    floatWin.UpdateLayout();

                    int ultraW = (int)Math.Round(floatWin.ActualWidth);
                    if (Math.Abs(ultraW - FloatingOverlayWindow.UltraWidthDefault) > 2.0)
                        Console.WriteLine($"CAPTURE_WARN: live_07c Ultra ActualWidth={floatWin.ActualWidth:F1} 偏离目标 {FloatingOverlayWindow.UltraWidthDefault}");
                    int ultraH = MeasureExactOverlayHeight(floatWin, ultraW);
                    CaptureElementPng(floatWin, ultraW, ultraH, Path.Combine(outDir, "live_07c_floating_ultra.png"));
                }
                finally
                {
                    reg.UserConfig.Agents = ultraOrigAgents;
                    if (origLiveMapBytes != null) File.WriteAllBytes(AutoDispatchService.LiveStateMapPath, origLiveMapBytes);
                    else if (File.Exists(AutoDispatchService.LiveStateMapPath)) File.Delete(AutoDispatchService.LiveStateMapPath);
                    if (origLiveSingleBytes != null) File.WriteAllBytes(AutoDispatchService.LiveStateJsonPath, origLiveSingleBytes);
                    else if (File.Exists(AutoDispatchService.LiveStateJsonPath)) File.Delete(AutoDispatchService.LiveStateJsonPath);
                    reg.UserConfig.FloatingLayoutMode = ultraOrigLayout;
                }
            }

            reg.UserConfig.FloatingLayoutMode = "full";
            reg.UserConfig.FloatingUltraCustomWidth = null;
            floatWin.ApplyUserPreferencesFromSettings();

            // 多状态同框色板实拍（6 大关键状态清晰对比核验）
            var sampleAgents = new List<AgentRosterEntry>
            {
                new() { Id = "antigravity", Name = "Antigravity", Enabled = true, PreferredEffortTier = "auto" },
                new() { Id = "cursor", Name = "Cursor", Enabled = true, PreferredEffortTier = "auto" },
                new() { Id = "grok_bot", Name = "Grok Bot", Enabled = true, PreferredEffortTier = "auto" },
                new() { Id = "codex_cli", Name = "Codex CLI", Enabled = true, PreferredEffortTier = "high" },
                new() { Id = "claude_code", Name = "Claude Code", Enabled = true, PreferredEffortTier = "medium" },
                new() { Id = "kimi_demo", Name = "Kimi CLI", Enabled = true, PreferredEffortTier = "low" }
            };
            var nowTicks = DateTime.UtcNow.Ticks;
            var nowStr = DateTime.Now.ToString("HH:mm:ss");
            var projName = reg.GetActiveProject()?.Name ?? "XX";
            var sampleMap = new Dictionary<string, LiveAgentWorkState>(StringComparer.OrdinalIgnoreCase)
            {
                ["antigravity"] = new LiveAgentWorkState
                {
                    AgentId = "antigravity", AgentName = "Antigravity", Status = "working",
                    WorkflowPhase = "master_analyzing", TaskSummary = "统筹拆解 v1.9.11 悬浮窗配色与 CLI 安装方案",
                    ProjectName = projName, UpdatedAt = nowStr, UpdatedUtcTicks = nowTicks, DispatcherPid = Environment.ProcessId
                },
                ["cursor"] = new LiveAgentWorkState
                {
                    AgentId = "cursor", AgentName = "Cursor", Status = "working",
                    WorkflowPhase = "executing", LatestLogTail = "Editing FloatingOverlayWindow.StatusFx.cs\nRunning build check",
                    TaskSummary = "落地修改 StatusFx 高对比色板与增量动画刷新",
                    SelectedEffortTier = "high", SelectedModel = "Claude Opus 5.5",
                    ProjectName = projName, UpdatedAt = nowStr, UpdatedUtcTicks = nowTicks, DispatcherPid = Environment.ProcessId
                },
                ["grok_bot"] = new LiveAgentWorkState
                {
                    AgentId = "grok_bot", AgentName = "Grok Bot", Status = "working",
                    WorkflowPhase = "executing", LatestLogTail = "Thinking through WCAG contrast & layout bounds",
                    TaskSummary = "推演动态最低高度阈值与 Ultra 极简防裁切布局",
                    ProjectName = projName, UpdatedAt = nowStr, UpdatedUtcTicks = nowTicks, DispatcherPid = Environment.ProcessId
                },
                ["codex_cli"] = new LiveAgentWorkState
                {
                    AgentId = "codex_cli", AgentName = "Codex CLI", Status = "working",
                    WorkflowPhase = "master_verifying", TaskSummary = "严格核验 --self-test 与视觉截图对比度达标",
                    SelectedEffortTier = "medium", ProjectName = projName, UpdatedAt = nowStr, UpdatedUtcTicks = nowTicks, DispatcherPid = Environment.ProcessId
                },
                ["claude_code"] = new LiveAgentWorkState
                {
                    AgentId = "claude_code", AgentName = "Claude Code", Status = "working",
                    WorkflowPhase = "reworking", CurrentRound = 2,
                    TaskSummary = "第2轮精修 · 消除静态徽章闪烁并修正 #AARRGGBB 色彩串",
                    ProjectName = projName, UpdatedAt = nowStr, UpdatedUtcTicks = nowTicks, DispatcherPid = Environment.ProcessId
                },
                ["kimi_demo"] = new LiveAgentWorkState
                {
                    AgentId = "kimi_demo", AgentName = "Kimi CLI", Status = "done",
                    WorkflowPhase = "verified_delivered",
                    TaskSummary = "已完成官方 CLI 下载进度条与音效开关交付",
                    ProjectName = projName, UpdatedAt = nowStr, UpdatedUtcTicks = nowTicks, DispatcherPid = Environment.ProcessId
                }
            };
            RegistryService.WriteJsonAtomic(AutoDispatchService.LiveStateMapPath, sampleMap);
            reg.UserConfig.Agents = sampleAgents;
            floatWin.ResetAdaptiveHeightForTest();
            floatWin.ApplyCustomSizeForTest(450, 500, collapsed: false);
            floatWin.RefreshOverlayData(force: true);
            floatWin.UpdateLayout();
            int allStatesH = Math.Max(340, (int)Math.Ceiling(((UIElement)floatWin.Content).DesiredSize.Height) + 12);
            CaptureElementPng(floatWin, 450, allStatesH, Path.Combine(outDir, "live_07d_floating_all_states.png"));

            AutoDispatchService.SetDesktopBusyOverrideForTest(
                "cursor",
                new DesktopBusyProbeResult(true, "并行会话", "多任务", DateTime.UtcNow, ActiveTaskCount: 3,
                    ActiveTaskTitles: new[] { "并行会话", "增量刷新", "角标验收" }));
            AutoDispatchService.SetDesktopBusyOverrideForTest(
                "antigravity",
                new DesktopBusyProbeResult(true, "并行分析", "多任务", DateTime.UtcNow, ActiveTaskCount: 2,
                    ActiveTaskTitles: new[] { "主脑拆解", "并行分析" }));
            try
            {
                floatWin.RefreshOverlayData(force: true);
                floatWin.UpdateLayout();
                CaptureElementPng(floatWin, 450, allStatesH, Path.Combine(outDir, "live_07e_floating_multitask_badge.png"));
            }
            finally
            {
                AutoDispatchService.SetDesktopBusyOverrideForTest("cursor", null);
                AutoDispatchService.SetDesktopBusyOverrideForTest("antigravity", null);
            }

            // 还原真实 Agents 与状态后再拍透明度弹窗与折叠胶囊态
            reg.UserConfig.Agents = origAgentsSnapshot;
            if (origLiveMapBytes != null) File.WriteAllBytes(AutoDispatchService.LiveStateMapPath, origLiveMapBytes);
            else if (File.Exists(AutoDispatchService.LiveStateMapPath)) File.Delete(AutoDispatchService.LiveStateMapPath);
            if (origLiveSingleBytes != null) File.WriteAllBytes(AutoDispatchService.LiveStateJsonPath, origLiveSingleBytes);
            else if (File.Exists(AutoDispatchService.LiveStateJsonPath)) File.Delete(AutoDispatchService.LiveStateJsonPath);

            floatWin.ResetAdaptiveHeightForTest();
            floatWin.ApplyCustomSizeForTest(420, null, collapsed: false);
            floatWin.RefreshOverlayData(force: true);
            floatWin.SetOpacityBarVisibleForPreview(true);
            floatWin.UpdateLayout();
            CaptureElementPng(floatWin, 420, Math.Max(floatH, 230), Path.Combine(outDir, "live_09_floating_opacity_popup.png"));
            floatWin.SetOpacityBarVisibleForPreview(false);

            reg.UserConfig.FloatingWindowCollapsed = true;
            reg.UserConfig.FloatingCollapsedCustomWidth = null;
            floatWin.ApplyCollapsedVisual(true, preserveRightAnchor: false);
            floatWin.RefreshOverlayData(force: true);
            floatWin.UpdateLayout();
            int collapsedW = Math.Max((int)FloatingOverlayWindow.CollapsedWidthMin,
                (int)Math.Round(floatWin.ActualWidth > 40 ? floatWin.ActualWidth : floatWin.Width));
            int collapsedH = Math.Max(40, MeasureExactOverlayHeight(floatWin, collapsedW));
            CaptureElementPng(floatWin, collapsedW, collapsedH, Path.Combine(outDir, "live_08_floating_collapsed.png"));
            CaptureCollapsedWithBubblePng(
                floatWin,
                collapsedW,
                collapsedH,
                Path.Combine(outDir, "live_08b_floating_collapsed_bubble.png"));
        }
        finally
        {
            reg.UserConfig.Agents = origAgentsSnapshot;
            reg.UserConfig.FloatingWindowCollapsed = origCollapsed;
            reg.UserConfig.FloatingLayoutMode = origDensity;
            reg.UserConfig.FloatingCustomWidth = origCustomW;
            reg.UserConfig.FloatingCustomHeight = origCustomH;
            reg.UserConfig.FloatingUltraCustomWidth = origUltraCustomW;
            reg.UserConfig.FloatingCollapsedCustomWidth = origCollapsedCustomW;
            if (origLiveMapBytes != null) File.WriteAllBytes(AutoDispatchService.LiveStateMapPath, origLiveMapBytes);
            else if (File.Exists(AutoDispatchService.LiveStateMapPath)) File.Delete(AutoDispatchService.LiveStateMapPath);
            if (origLiveSingleBytes != null) File.WriteAllBytes(AutoDispatchService.LiveStateJsonPath, origLiveSingleBytes);
            else if (File.Exists(AutoDispatchService.LiveStateJsonPath)) File.Delete(AutoDispatchService.LiveStateJsonPath);
            reg.SaveUserConfig();
        }

        // 额外截图两个二级模态窗口（单个 AI 多方案与全参数窗口 + 云端多流派榜单窗口）
        var cloudSvc = new CloudRecommendationService();
        var sampleAgent = reg.UserConfig.Agents.FirstOrDefault(a => a.Id == "cursor") ?? reg.UserConfig.Agents.First();
        var (agentDlg, _) = Dialogs.BuildAgentConfigDialogWindow(null, reg, sampleAgent, cloudSvc);
        agentDlg.WindowStartupLocation = WindowStartupLocation.Manual;
        agentDlg.Left = -32000;
        agentDlg.Top = -32000;
        agentDlg.ShowActivated = false;
        agentDlg.Show();
        agentDlg.UpdateLayout();
        CaptureElementPng(agentDlg, 750, 696, Path.Combine(outDir, "live_10_dialog_agent_config.png"));
        (agentDlg.Tag as Action<int>)?.Invoke(2);
        agentDlg.UpdateLayout();
        CaptureElementPng(agentDlg, 750, 696, Path.Combine(outDir, "live_10e_dialog_runtime_params.png"));
        agentDlg.Close();

        var agyAgent = reg.UserConfig.Agents.FirstOrDefault(a => a.Id == "antigravity");
        if (agyAgent != null)
        {
            var (agyDlg, _) = Dialogs.BuildAgentConfigDialogWindow(null, reg, agyAgent, cloudSvc);
            agyDlg.WindowStartupLocation = WindowStartupLocation.Manual;
            agyDlg.Left = -32000;
            agyDlg.Top = -32000;
            agyDlg.ShowActivated = false;
            agyDlg.Show();
            agyDlg.UpdateLayout();
            CaptureElementPng(agyDlg, 750, 696, Path.Combine(outDir, "live_10b_dialog_antigravity_config.png"));
            agyDlg.Close();
        }

        var grokViewAgent = reg.UserConfig.Agents.FirstOrDefault(a => a.Id == "grok_bot");
        if (grokViewAgent != null)
        {
            var (grokDlg, _) = Dialogs.BuildAgentConfigDialogWindow(null, reg, grokViewAgent, cloudSvc);
            grokDlg.WindowStartupLocation = WindowStartupLocation.Manual;
            grokDlg.Left = -32000;
            grokDlg.Top = -32000;
            grokDlg.ShowActivated = false;
            grokDlg.Show();
            grokDlg.UpdateLayout();
            CaptureElementPng(grokDlg, 750, 696, Path.Combine(outDir, "live_10c_dialog_grok_config.png"));
            (grokDlg.Tag as Action<int>)?.Invoke(1);
            grokDlg.UpdateLayout();
            CaptureElementPng(grokDlg, 750, 696, Path.Combine(outDir, "live_10d_dialog_permissions_tags.png"));
            grokDlg.Close();
        }

        var (cloudDlg, _) = Dialogs.BuildCloudRecommendationDialogWindow(null, reg, cloudSvc);
        cloudDlg.WindowStartupLocation = WindowStartupLocation.Manual;
        cloudDlg.Left = -32000;
        cloudDlg.Top = -32000;
        cloudDlg.ShowActivated = false;
        cloudDlg.Show();
        cloudDlg.UpdateLayout();
        CaptureElementPng(cloudDlg, 750, 616, Path.Combine(outDir, "live_11_dialog_cloud_rec.png"));
        cloudDlg.Close();

        // 额外截图：模拟断网保护断点横幅呈现效果（拍完立即清理还原）
        var activeProj = reg.GetActiveProject();
        var existingCp = NetworkResilienceService.LoadInterruptedCheckpoint(activeProj);
        if (existingCp == null && activeProj != null)
        {
            NetworkResilienceService.SaveCheckpoint(activeProj, new TaskChainCheckpoint
            {
                Status = "interrupted",
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ProjectId = activeProj.Id,
                ProjectName = activeProj.Name,
                ProjectRoot = activeProj.PrimaryRoot,
                TaskInstruction = "落地修改战斗技能冷却配置与预制体特效挂载节点，并提交主脑核验 Diff",
                MasterPlannerName = "Antigravity",
                TargetAgentId = "cursor",
                TargetAgentName = "Cursor",
                CurrentRound = 2,
                MaxRounds = 3,
                NetworkRetriesUsed = 3,
                LastFailureKind = "remote_server_error",
                LastFailureLabel = "🌐 远端服务 503 波动 / 网络掉线"
            });
            win.SwitchToTabForTest(1);
            win.UpdateLayout();
            CaptureElementPng(win, 1220, 760, Path.Combine(outDir, "live_12_checkpoint_recovery_banner.png"));
            NetworkResilienceService.ClearInterruptedCheckpoint(activeProj);
        }

        // 额外截图：「任务执行卡住 / 通道受阻 · 选择解决方式」二级决策窗口
        var stuckDemoAgent = grokViewAgent ?? sampleAgent;
        var demoProj = activeProj ?? reg.UserConfig.Projects.First();
        var demoLive = new LiveAgentWorkState
        {
            AgentId = stuckDemoAgent.Id,
            AgentName = stuckDemoAgent.Name,
            Status = "error",
            WorkflowPhase = "awaiting_user_choice",
            LastFailureBadge = "🛠 进程/弹窗卡住·待选解决方式",
            TaskSummary = "优化废院石坪着色器高光粗糙度与网格初始化脚本并验证",
            LatestLogTail = "检测到 Create New Bot 模态框或后台通道未响应，已静默挂起等待您选择处理方案（零鼠标键盘占用）"
        };
        var demoCp = new TaskChainCheckpoint
        {
            Status = "interrupted",
            ProjectId = demoProj.Id,
            ProjectName = demoProj.Name,
            ProjectRoot = demoProj.PrimaryRoot,
            TargetAgentId = stuckDemoAgent.Id,
            TargetAgentName = stuckDemoAgent.Name,
            TaskInstruction = "优化废院石坪着色器高光粗糙度与网格初始化脚本并验证",
            LastFailureKind = "execution_stalled",
            LastFailureLabel = "🛠 进程/弹窗卡住·待选解决方式",
            LastErrorDetail = "后台首次自动重试后仍检测到客户端会话受阻，请从下方 5 种无感方案中选择处理方式"
        };
        var (stallDlg, _) = Dialogs.BuildStallResolutionDialogWindow(null, reg, demoProj, stuckDemoAgent, demoLive, demoCp);
        stallDlg.WindowStartupLocation = WindowStartupLocation.Manual;
        stallDlg.Left = -32000;
        stallDlg.Top = -32000;
        stallDlg.ShowActivated = false;
        stallDlg.Show();
        stallDlg.UpdateLayout();
        CaptureElementPng(stallDlg, 670, 611, Path.Combine(outDir, "live_13_dialog_stall_resolution.png"));
        stallDlg.Close();

        // 额外截图：悬浮窗深色结构化悬停卡片 ToolTip 预览
        var tipPreviewHost = new Window
        {
            Width = 408,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
            Content = new System.Windows.Controls.Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Child = FloatingOverlayWindow.BuildRichTooltipContent(
                    sampleAgent,
                    new LiveAgentWorkState
                    {
                        AgentId = sampleAgent.Id,
                        AgentName = sampleAgent.Name,
                        Status = "done",
                        SelectedModel = "Grok 4.7 (grok-4.7)",
                        SelectedTierBadge = "中档-Medium",
                        SessionTitle = "你最强模型诊断+执行代理",
                        DispatchReason = "Magpie 路由命中「代码开发实装」 ➔ 指派 [Cursor]",
                        VerificationSummary = "✓ 主脑 [Antigravity] 验收通过（首轮一次通过）",
                        TaskSummary = "协助盘点并安全迁移资源到 E:\\项目资源汇总（已有 Y3 编辑器资源/模型/游戏资源/音乐），遵守 junction 安全先复制校验再写入清单。",
                        LatestLogTail = "# 自动派单产出 - Cursor\n- 执行时间: 2026-11-17 19:24:14\n- **统筹与验收主脑**: `Antigravity`\n- 已完成盘点与复制校验并写入 E:\\项目资源汇总\\迁移清单.md"
                    },
                    (true, 4932, "窗口运行中 (PID 4932)"))
            }
        };
        tipPreviewHost.Show();
        tipPreviewHost.UpdateLayout();
        int tipH = Math.Max(180, (int)Math.Ceiling(((UIElement)tipPreviewHost.Content).DesiredSize.Height) + 6);
        CaptureElementPng(tipPreviewHost, 408, tipH, Path.Combine(outDir, "live_14_floating_tooltip_card.png"));
        tipPreviewHost.Close();

        // 额外截图：v1.9.14 统一「配置方案管理中心」模态窗口（Cursor 多模型与1~5强度 + Antigravity 自有/三方工作台 + 12类工作内容路由）
        // 抓拍前断言：切 Cursor / Antigravity / Grok 时所有模型「配置能力」面板默认全部收起
        static List<T> CollectCaptureUi<T>(DependencyObject? root) where T : DependencyObject
        {
            var found = new List<T>();
            if (root == null) return found;
            var visited = new HashSet<DependencyObject>();
            void Walk(DependencyObject node)
            {
                if (!visited.Add(node)) return;
                if (node is T matched) found.Add(matched);
                int vCount = 0;
                try { vCount = VisualTreeHelper.GetChildrenCount(node); } catch { }
                for (int i = 0; i < vCount; i++)
                    Walk(VisualTreeHelper.GetChild(node, i));
                foreach (var child in LogicalTreeHelper.GetChildren(node))
                {
                    if (child is DependencyObject d) Walk(d);
                }
            }
            Walk(root);
            return found;
        }
        void AssertCaptureCapsCollapsed(Window dlg, string label)
        {
            var expandBtns = CollectCaptureUi<System.Windows.Controls.Button>(dlg)
                .Where(b => string.Equals(b.Content as string, "配置能力 ▼", StringComparison.Ordinal)).Distinct().ToList();
            int collapseN = CollectCaptureUi<System.Windows.Controls.Button>(dlg)
                .Count(b => string.Equals(b.Content as string, "收起能力 ▲", StringComparison.Ordinal));
            int strengthN = CollectCaptureUi<System.Windows.Controls.ComboBox>(dlg)
                .Count(cb => cb.Items.OfType<System.Windows.Controls.ComboBoxItem>()
                    .Any(ci => string.Equals(ci.Content as string, "5★王牌", StringComparison.Ordinal)));
            int summaryN = CollectCaptureUi<System.Windows.Controls.TextBlock>(dlg)
                .Distinct()
                .Count(t => t.IsVisible && (Regex.IsMatch(t.Text ?? "", @"\s[1-5]★$") || (t.Text ?? "").StartsWith("未开启任何能力", StringComparison.Ordinal)));
            if (!(expandBtns.Count > 0 && collapseN == 0 && strengthN == 0 && summaryN > 0))
                throw new InvalidOperationException(
                    $"CAPTURE_FAIL: 配置方案中心[{label}]模型能力面板应默认全部收起 (配置能力▼={expandBtns.Count}, 收起能力▲={collapseN}, StrengthCombo={strengthN}, CapSummary={summaryN})");
            Console.WriteLine($"CAPTURE_OK: [{label}] 模型能力面板默认全部收起 (配置能力▼={expandBtns.Count}, CapSummary={summaryN})");
        }

        var (profMgrDlg, _) = Dialogs.BuildTeamProfileManagerDialogWindow(null, reg, cloudSvc);
        profMgrDlg.WindowStartupLocation = WindowStartupLocation.Manual;
        profMgrDlg.Left = -32000;
        profMgrDlg.Top = -32000;
        profMgrDlg.ShowActivated = false;
        profMgrDlg.Show();
        profMgrDlg.UpdateLayout();
        foreach (var focusId in new[] { "cursor", "antigravity", "grok_bot" })
        {
            (profMgrDlg.Tag as Action<int, string?>)?.Invoke(0, focusId);
            profMgrDlg.UpdateLayout();
            AssertCaptureCapsCollapsed(profMgrDlg, focusId);
            if (focusId == "cursor")
                CaptureElementPng(profMgrDlg, (int)profMgrDlg.Width, (int)profMgrDlg.Height, Path.Combine(outDir, "live_15_dialog_team_profile_manager.png"));
            else if (focusId == "antigravity")
                CaptureElementPng(profMgrDlg, (int)profMgrDlg.Width, (int)profMgrDlg.Height, Path.Combine(outDir, "live_15c_dialog_antigravity_models.png"));
        }
        (profMgrDlg.Tag as Action<int, string?>)?.Invoke(1, null);
        profMgrDlg.UpdateLayout();
        CaptureElementPng(profMgrDlg, (int)profMgrDlg.Width, (int)profMgrDlg.Height, Path.Combine(outDir, "live_15b_dialog_profile_routes.png"));
        profMgrDlg.Close();

        // 额外截图：v1.9.12 多颜色主题切换实拍（展示「黑曜流金 obsidian_gold」主题下的软件设置页，拍完立即恢复原主题）
        string origTheme = reg.UserConfig.FloatingThemeStyle;
        try
        {
            reg.UserConfig.FloatingThemeStyle = "obsidian_gold";
            ThemeService.ApplyGlobalTheme("obsidian_gold");
            win.SwitchToTabForTest(6);
            win.UpdateLayout();
            CaptureElementPng(win, 1220, 760, Path.Combine(outDir, "live_16_theme_obsidian_gold.png"));
        }
        finally
        {
            reg.UserConfig.FloatingThemeStyle = origTheme;
            ThemeService.ApplyGlobalTheme(origTheme);
            reg.SaveUserConfig();
        }

        // live_17：关闭主界面确认弹窗实机渲染
        {
            var (closeLiveDlg, _, _, _, _, _) = Dialogs.BuildCloseConfirmDialogWindow(null);
            closeLiveDlg.WindowStartupLocation = WindowStartupLocation.Manual;
            closeLiveDlg.Left = -32000;
            closeLiveDlg.Top = -32000;
            closeLiveDlg.ShowActivated = false;
            closeLiveDlg.Show();
            closeLiveDlg.UpdateLayout();
            CaptureElementPng(closeLiveDlg, (int)closeLiveDlg.Width, (int)closeLiveDlg.Height,
                Path.Combine(outDir, "live_17_dialog_close_confirm.png"));
            closeLiveDlg.Close();
        }

        var onboardLive = new OnboardingWizardDialog(reg, new DiscoveryService())
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowActivated = false
        };
        onboardLive.Show();
        onboardLive.SwitchStepForTest(1);
        onboardLive.UpdateLayout();
        CaptureElementPng(onboardLive, 760, 560, Path.Combine(outDir, "live_18_onboarding_step1.png"));
        onboardLive.SwitchStepForTest(2);
        onboardLive.UpdateLayout();
        CaptureElementPng(onboardLive, 760, 560, Path.Combine(outDir, "live_18b_onboarding_step2.png"));
        win.PushModalBackdrop();
        win.UpdateLayout();
        CaptureLightboxCompositePng(
            win, 1220, 760,
            onboardLive, 760, 560,
            Path.Combine(outDir, "live_18d_onboarding_lightbox_on_main.png"));
        win.PopModalBackdrop();
        onboardLive.SwitchStepForTest(3);
        onboardLive.UpdateLayout();
        CaptureElementPng(onboardLive, 760, 560, Path.Combine(outDir, "live_18c_onboarding_step3.png"));
        onboardLive.Close();

        floatWin.Close();
        win.Close();
        app.Shutdown();
        return 0;
    }

    /// <summary>解析标准 PCM RIFF/WAVE，供 --self-test 断言内嵌提示音。</summary>
    private static bool TryInspectEmbeddedPcmWav(
        byte[] wav,
        out int sampleRate,
        out int channels,
        out int bitsPerSample,
        out double durationSec,
        out double activeSec,
        out double peak)
    {
        sampleRate = 0;
        channels = 0;
        bitsPerSample = 0;
        durationSec = 0;
        activeSec = 0;
        peak = 0;
        if (wav.Length < 44) return false;
        if (wav[0] != (byte)'R' || wav[1] != (byte)'I' || wav[2] != (byte)'F' || wav[3] != (byte)'F') return false;
        if (wav[8] != (byte)'W' || wav[9] != (byte)'A' || wav[10] != (byte)'V' || wav[11] != (byte)'E') return false;

        int pos = 12;
        short audioFormat = 0;
        int dataPos = -1;
        int dataSize = 0;
        while (pos + 8 <= wav.Length)
        {
            string id = Encoding.ASCII.GetString(wav, pos, 4);
            int size = BitConverter.ToInt32(wav, pos + 4);
            int body = pos + 8;
            if (size < 0 || body > wav.Length || size > wav.Length - body) return false;
            if (id == "fmt ")
            {
                if (size < 16) return false;
                audioFormat = BitConverter.ToInt16(wav, body);
                channels = BitConverter.ToInt16(wav, body + 2);
                sampleRate = BitConverter.ToInt32(wav, body + 4);
                bitsPerSample = BitConverter.ToInt16(wav, body + 14);
            }
            else if (id == "data")
            {
                dataPos = body;
                dataSize = size;
            }
            int step = 8 + size + (size & 1);
            if (step <= 0) return false;
            pos += step;
        }

        if (audioFormat != 1 || dataPos < 0 || channels < 1 || bitsPerSample != 16 || sampleRate <= 0)
            return false;
        int bytesPerFrame = channels * (bitsPerSample / 8);
        if (bytesPerFrame <= 0 || dataSize < bytesPerFrame) return false;
        int frames = dataSize / bytesPerFrame;
        durationSec = frames / (double)sampleRate;
        int end = Math.Min(wav.Length, dataPos + frames * bytesPerFrame);
        for (int i = dataPos; i + 1 < end; i += 2)
        {
            double a = Math.Abs(BitConverter.ToInt16(wav, i)) / 32768.0;
            if (a > peak) peak = a;
        }
        double thr = Math.Max(0.01, peak * 0.05);
        int first = -1;
        int last = -1;
        for (int frame = 0; frame < frames; frame++)
        {
            int off = dataPos + frame * bytesPerFrame;
            bool hot = false;
            for (int c = 0; c < channels; c++)
            {
                if (Math.Abs(BitConverter.ToInt16(wav, off + c * 2)) / 32768.0 >= thr)
                {
                    hot = true;
                    break;
                }
            }
            if (!hot) continue;
            if (first < 0) first = frame;
            last = frame;
        }
        if (first >= 0 && last >= first)
            activeSec = (last - first + 1) / (double)sampleRate;
        return true;
    }

    private static int CountToolIconTiles(DependencyObject root) =>
        CollectTaggedElements(root, "tool_icon_tile").Count;

    private static int CountToolIconTilesWithTooltip(DependencyObject root) =>
        CollectTaggedElements(root, "tool_icon_tile").Count(fe => fe.ToolTip is ToolTip or Border);

    private static Button? FindButtonByContent(DependencyObject root, string content)
    {
        Button? found = null;
        var seen = new HashSet<DependencyObject>();
        void Walk(DependencyObject node)
        {
            if (found != null || !seen.Add(node)) return;
            if (node is Button btn && string.Equals(btn.Content as string, content, StringComparison.Ordinal))
            {
                found = btn;
                return;
            }
            int count = 0;
            try { count = VisualTreeHelper.GetChildrenCount(node); } catch { }
            for (int i = 0; i < count; i++)
                Walk(VisualTreeHelper.GetChild(node, i));
            foreach (var child in LogicalTreeHelper.GetChildren(node))
            {
                if (child is DependencyObject dep) Walk(dep);
            }
        }
        Walk(root);
        return found;
    }

    private static bool ContainsSquareTopGlow(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>();
        bool Walk(DependencyObject node)
        {
            if (!seen.Add(node)) return false;
            if (node is Border border &&
                !double.IsNaN(border.Height) &&
                border.Height > 0 &&
                border.Height <= 2.5 &&
                border.CornerRadius.TopLeft == 0 &&
                border.CornerRadius.TopRight == 0)
                return true;
            int count = 0;
            try { count = VisualTreeHelper.GetChildrenCount(node); } catch { }
            for (int i = 0; i < count; i++)
            {
                if (Walk(VisualTreeHelper.GetChild(node, i))) return true;
            }
            foreach (var child in LogicalTreeHelper.GetChildren(node))
            {
                if (child is DependencyObject dep && Walk(dep)) return true;
            }
            return false;
        }
        return Walk(root);
    }

    private static bool VisualTreeContainsText(DependencyObject root, string text)
    {
        var seen = new HashSet<DependencyObject>();
        bool Walk(DependencyObject node)
        {
            if (!seen.Add(node)) return false;
            if (node is TextBlock block && block.Text.Contains(text, StringComparison.Ordinal))
                return true;
            int count = 0;
            try { count = VisualTreeHelper.GetChildrenCount(node); } catch { }
            for (int i = 0; i < count; i++)
            {
                if (Walk(VisualTreeHelper.GetChild(node, i))) return true;
            }
            foreach (var child in LogicalTreeHelper.GetChildren(node))
            {
                if (child is DependencyObject dep && Walk(dep)) return true;
            }
            return false;
        }
        return Walk(root);
    }

    private static List<FrameworkElement> CollectTaggedElements(DependencyObject root, string tag)
    {
        var found = new List<FrameworkElement>();
        var seen = new HashSet<DependencyObject>();
        void Walk(DependencyObject node)
        {
            if (!seen.Add(node)) return;
            if (node is FrameworkElement fe && Equals(fe.Tag, tag))
                found.Add(fe);
            int count = 0;
            try { count = VisualTreeHelper.GetChildrenCount(node); } catch { }
            for (int i = 0; i < count; i++)
                Walk(VisualTreeHelper.GetChild(node, i));
            foreach (var child in LogicalTreeHelper.GetChildren(node))
            {
                if (child is DependencyObject dep) Walk(dep);
            }
        }
        Walk(root);
        return found;
    }

    private static System.Windows.Shapes.Ellipse? FindTaggedEllipse(DependencyObject root, string tag)
    {
        foreach (var fe in CollectTaggedElements(root, tag))
        {
            if (fe is System.Windows.Shapes.Ellipse ellipse) return ellipse;
        }
        return null;
    }

    private static Border? FindNamedBorder(DependencyObject root, string name)
    {
        if (root is Border border && string.Equals(border.Name, name, StringComparison.Ordinal))
            return border;
        if (root is Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                if (child is not DependencyObject dep) continue;
                var hit = FindNamedBorder(dep, name);
                if (hit != null) return hit;
            }
            return null;
        }
        if (root is Decorator decorator && decorator.Child is DependencyObject childDep)
            return FindNamedBorder(childDep, name);
        return null;
    }

    private static string DumpTooltipText(object? tip)
    {
        if (tip is string s) return s;
        if (tip is not ToolTip tool || tool.Content is not DependencyObject content) return "";
        var sb = new StringBuilder();
        void Walk(DependencyObject d)
        {
            if (d is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
                sb.AppendLine(tb.Text);
            if (d is Panel panel)
            {
                foreach (UIElement kid in panel.Children)
                {
                    if (kid is DependencyObject dep) Walk(dep);
                }
                return;
            }
            if (d is Decorator decorator && decorator.Child is DependencyObject child)
                Walk(child);
        }
        Walk(content);
        return sb.ToString();
    }

    private static Border? FindTaggedBorder(DependencyObject root, string tag)
    {
        foreach (var fe in CollectTaggedElements(root, tag))
        {
            if (fe is Border border) return border;
        }
        return null;
    }

    private static void CaptureLightboxCompositePng(
        Window main, int mainW, int mainH,
        Window dialog, int dialogW, int dialogH,
        string filePath)
    {
        var mainRoot = VisualTreeHelper.GetChildrenCount(main) > 0 && VisualTreeHelper.GetChild(main, 0) is UIElement mainChild
            ? mainChild
            : (UIElement)main.Content;
        var dialogRoot = VisualTreeHelper.GetChildrenCount(dialog) > 0 && VisualTreeHelper.GetChild(dialog, 0) is UIElement dialogChild
            ? dialogChild
            : (UIElement)dialog.Content;
        var mainBmp = RenderVisualBrush(mainRoot, mainW, mainH);
        var dialogBmp = RenderVisualBrush(dialogRoot, dialogW, dialogH);
        int left = Math.Max(0, (mainW - dialogW) / 2);
        int top = Math.Max(0, (mainH - dialogH) / 2);
        var rtb = new RenderTargetBitmap(mainW, mainH, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(mainBmp, new Rect(0, 0, mainW, mainH));
            dc.DrawImage(dialogBmp, new Rect(left, top, dialogW, dialogH));
        }
        rtb.Render(dv);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(filePath);
        encoder.Save(fs);
    }

    private static RenderTargetBitmap RenderVisualBrush(UIElement element, int width, int height)
    {
        element.Measure(new Size(width, Math.Max(height, 1)));
        element.Arrange(new Rect(0, 0, width, Math.Max(height, 1)));
        element.UpdateLayout();
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var vb = new VisualBrush(element)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top
            };
            dc.DrawRectangle(vb, null, new Rect(0, 0, width, height));
        }
        rtb.Render(dv);
        return rtb;
    }

    private static void CaptureCollapsedWithBubblePng(FloatingOverlayWindow floatWin, int collapsedW, int collapsedH, string filePath)
    {
        floatWin.ApplyCollapsedVisual(true, preserveRightAnchor: false);
        floatWin.RefreshOverlayData(force: true);
        floatWin.UpdateLayout();
        var root = VisualTreeHelper.GetChildrenCount(floatWin) > 0 && VisualTreeHelper.GetChild(floatWin, 0) is UIElement rc
            ? rc
            : (UIElement)floatWin.Content;
        var capBmp = RenderVisualBrush(root, collapsedW, collapsedH);

        floatWin.PreviewCompletionBubble();
        floatWin.SnapCompletionBubbleFullyVisibleForCapture();
        var banner = floatWin.VerifyCelebrationBanner;
        const int bubbleW = 356;
        banner.Measure(new Size(bubbleW, double.PositiveInfinity));
        double wantH = Math.Max(banner.DesiredSize.Height, banner.ActualHeight);
        int bubbleH = Math.Max(116, (int)Math.Ceiling(wantH + 8));
        var bubbleBmp = RenderVisualBrush(banner, bubbleW, bubbleH);

        int pad = 18;
        int gap = 2;
        int totalW = Math.Max(bubbleW, collapsedW) + pad * 2;
        int totalH = pad + collapsedH + gap + bubbleH + pad;
        var rtb = new RenderTargetBitmap(totalW, totalH, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0B, 0x12, 0x18)), null, new Rect(0, 0, totalW, totalH));
            dc.DrawImage(capBmp, new Rect((totalW - collapsedW) / 2.0, pad, collapsedW, collapsedH));
            dc.DrawImage(bubbleBmp, new Rect((totalW - bubbleW) / 2.0, pad + collapsedH + gap, bubbleW, bubbleH));
        }
        rtb.Render(dv);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(filePath);
        encoder.Save(fs);
    }

    private static void CaptureElementPng(Window window, int width, int height, string filePath)
    {
        var content = VisualTreeHelper.GetChildrenCount(window) > 0 && VisualTreeHelper.GetChild(window, 0) is UIElement rootChild
            ? rootChild
            : (UIElement)window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x11, 0x15, 0x1E)), null, new Rect(0, 0, width, height));
            var vb = new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            dc.DrawRectangle(vb, null, new Rect(0, 0, width, height));
        }
        rtb.Render(dv);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(filePath);
        encoder.Save(fs);
    }
}
