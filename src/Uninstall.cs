// Uninstall.cs —— 卸载。
//
// 入口是程序文件夹里的 Uninstall.exe（它只是启动 "ReplayClipper.exe --uninstall"）。分两步：
//   第一步（在原位置运行）：询问确认，关掉正在运行的 Replay Clipper（后台录制随之停止）。
//   第二步：程序删不掉正在运行的自己，所以把自己复制一份到临时文件夹，由那份副本完成：
//           删开机启动项和桌面快捷方式 → 删程序文件、设置和回放缓存 → 最后把副本自己也删掉。
// 1.0 版靠 OBS 录制，在 OBS 里留有一个脚本登记和一套专用配置（见 LegacyObs.cs）：还在的话，这里一并清理。
//
// 不会动的东西：用户保存的片段。
// 只删除本程序自己的文件和文件夹（按名字逐个删），不会整个清空所在目录，
// 所以即使有人把别的文件放进了程序文件夹，那些文件也会留下。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ReplayClipper
{
    static partial class Program
    {
        static bool NoLog;      // 卸载过程中不再写日志（日志所在的文件夹正要被删掉）

        const uint MbOk = 0x0, MbOkCancel = 0x1, MbYesNo = 0x4, MbQuestion = 0x20, MbWarning = 0x30, MbInfo = 0x40,
                   MbDefault2 = 0x100, MbForeground = 0x10000, MbTopmost = 0x40000;
        const int IdOk = 1, IdYes = 6, IdNo = 7;

        // 仅测试用：设了这个环境变量就不弹对话框，按默认回答走；值为 "profile" 时连 OBS 里的专用配置也删
        static string UninstallAuto { get { return Environment.GetEnvironmentVariable("REPLAYCLIP_UNINSTALL_AUTO") ?? ""; } }

        static int Ask(string text, uint flags, int autoAnswer)
        {
            if (UninstallAuto.Length > 0) return autoAnswer;
            return MessageBoxW(IntPtr.Zero, text, Tr("卸载 " + AppName, "Uninstall " + AppName), flags | MbForeground | MbTopmost);
        }

        // ------------------------------------------------------------------ 第一步

        static int UninstallStage1()
        {
            NoLog = true;
            LoadSettings();
            string clips = ResolveFolder();

            // 1.0 版留在 OBS 里的东西还在不在
            bool realObs = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("REPLAYCLIP_OBS_CONFIG"));
            bool inObs = false, hasDedicated = false;
            if (LegacyAllowed)
            {
                try { inObs = AnyCollectionHasOurScript(); hasDedicated = DedicatedObsConfigExists(); } catch { }
            }
            bool legacy = inObs || hasDedicated;

            var sb = new StringBuilder();
            sb.AppendLine(Tr("确定要卸载 " + AppName + " 吗？", "Uninstall " + AppName + "?"));
            sb.AppendLine();
            sb.AppendLine(Tr("将执行以下操作：", "This will:"));
            sb.AppendLine(Tr("  · 关闭正在运行的 " + AppName + "，停止后台录制", "  · Close " + AppName + " if it's running and stop background recording"));
            if (legacy) sb.AppendLine(Tr("  · 从 OBS 中移除旧版本留下的脚本", "  · Remove the script an earlier version left in OBS"));
            sb.AppendLine(Tr("  · 删除桌面快捷方式和开机启动项，并从「已安装的应用」中移除", "  · Delete the desktop shortcut, the startup entry and the Installed apps entry"));
            sb.AppendLine(Tr("  · 删除程序文件、设置和回放缓存：", "  · Delete program files, settings and the replay buffer: ") + AppDir);
            sb.AppendLine();
            sb.AppendLine(Tr("以下内容将保留：", "These are kept:"));
            sb.Append(KeptClipsLine(clips));
            if (legacy)
            {
                sb.AppendLine();
                sb.Append(Tr("  · OBS 及其其他设置", "  · OBS and its other settings"));
            }
            if (Ask(sb.ToString(), MbOkCancel | MbQuestion | MbDefault2, IdOk) != IdOk) return 0;

            CloseOtherInstances();
            StopEngineHelpers(AppDir);

            // OBS 退出时会重写自己的配置文件，开着的时候改了也白改：请用户先把它退出，或者跳过这一项
            bool skipObs = !legacy;
            while (legacy && realObs && ObsIsRunning())
            {
                string msg = Tr("OBS 正在运行。要移除旧版本留在 OBS 中的脚本和配置，请先退出 OBS，然后选择「确定」。\n\n"
                              + "选择「取消」将跳过这一项，卸载照常进行。",
                                "OBS is running. To remove the script and configuration an earlier version left in OBS, close OBS, then select OK.\n\n"
                              + "Select Cancel to skip this step. The rest of the uninstall continues.");
                if (Ask(msg, MbOkCancel | MbWarning, 2) != IdOk) { skipObs = true; break; }
                Thread.Sleep(800);
            }

            bool removeDedicated = false;
            if (hasDedicated && !skipObs)
            {
                string msg = Tr("OBS 中有一套由旧版本创建的配置「" + AppName + "」（配置文件和场景集合）。\n\n"
                              + "是否一并删除？\n\n"
                              + "选择「否」将保留该配置，之后仍可在 OBS 中使用。",
                                "OBS has a profile and scene collection named \"" + AppName + "\" that an earlier version of this app created.\n\n"
                              + "Delete them as well?\n\n"
                              + "Select No to keep them. They will still work in OBS.");
                removeDedicated = Ask(msg, MbYesNo | MbQuestion | MbDefault2, UninstallAuto == "profile" ? IdYes : IdNo) == IdYes;
            }

            // 交给临时文件夹里的副本去做真正的删除
            string self = Assembly.GetExecutingAssembly().Location;
            string temp = Path.Combine(Path.GetTempPath(), "ReplayClipper-uninstall-" + RandomHex(4) + ".exe");
            File.Copy(self, temp, true);
            var psi = new ProcessStartInfo(temp,
                "--uninstall-stage2 \"" + AppDir.TrimEnd('\\') + "\" " + (removeDedicated ? "1" : "0") + " " + Process.GetCurrentProcess().Id + " " + (skipObs ? "1" : "0"));
            psi.UseShellExecute = false;
            psi.WorkingDirectory = Path.GetTempPath();
            Process.Start(psi);
            return 0;
        }

        // 录制组件是跟着程序一起结束的。万一还有从这个文件夹启动的留着（比如程序是被强行结束的），等一等，
        // 不行就强行结束：不留下一个没人管的进程接着录。
        static void StopEngineHelpers(string appDir)
        {
            string prefix = Path.Combine(appDir, "engine").TrimEnd('\\') + "\\";
            foreach (Process p in Process.GetProcessesByName("ReplayClipperEngine"))
            {
                try
                {
                    if (!p.MainModule.FileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!p.WaitForExit(3000))
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        static string KeptClipsLine(string clips)
        {
            return Tr("  · 已保存的片段", "  · Your saved clips") + (string.IsNullOrEmpty(clips) ? "" : Tr("（" + clips + "）", " (" + clips + ")"));
        }

        // 让从这个文件夹启动的其他 Replay Clipper 进程真正退出（别处的拷贝不碰）。
        // 关窗口不算数：那只会让它缩到托盘里。每一份运行着的程序都有一个不显示的控制窗口（见 Background.cs），
        // 向它发关闭消息才是"退出"。没有控制窗口的是旧版本，关掉它看得见的窗口即可。等不到就强行结束。
        static void CloseOtherInstances()
        {
            int me = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcessesByName("ReplayClipper"))
            {
                try
                {
                    if (p.Id == me) continue;
                    if (!SamePath(Path.GetDirectoryName(p.MainModule.FileName), AppDir)) continue;
                    uint pid = (uint)p.Id;
                    EnumWindows(delegate(IntPtr h, IntPtr l)
                    {
                        uint owner;
                        GetWindowThreadProcessId(h, out owner);
                        if (owner != pid) return true;
                        var title = new StringBuilder(64);
                        GetWindowTextW(h, title, 64);
                        if (title.ToString() == ControlWindowTitle || IsWindowVisible(h)) PostMessageW(h, 0x0010, IntPtr.Zero, IntPtr.Zero);
                        return true;
                    }, IntPtr.Zero);
                    if (!p.WaitForExit(8000))
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        static bool AnyCollectionHasOurScript()
        {
            foreach (string f in ObsCollectionFiles())
            {
                try
                {
                    bool bom;
                    int r, m, a0, a1;
                    foreach (object o in ReadScriptEntries(ReadUtf8(f, out bom), out r, out m, out a0, out a1))
                    {
                        var d = o as Dictionary<string, object>;
                        object p;
                        if (d != null && d.TryGetValue("path", out p) && IsOurScript(p) && SamePath((string)p, LegacyScript)) return true;
                    }
                }
                catch { }
            }
            return false;
        }

        // 「配置 OBS」建出来的那一套专用配置还在不在
        static bool DedicatedObsConfigExists()
        {
            string root = ObsConfigRoot();
            return Directory.Exists(Path.Combine(root, "basic", "profiles", OurProfileDir))
                || File.Exists(Path.Combine(root, "basic", "scenes", OurCollectionFile + ".json"));
        }

        // ------------------------------------------------------------------ 第二步（从临时文件夹里的副本运行）

        static int UninstallStage2(string[] args)
        {
            NoLog = true;
            if (args.Length < 2) return 1;
            string target = args[1].Trim().TrimEnd('\\');
            bool removeDedicated = args.Length > 2 && args[2] == "1";
            bool skipObs = args.Length > 4 && args[4] == "1";
            int parent;
            if (args.Length > 3 && int.TryParse(args[3], out parent))
            {
                try { using (Process p = Process.GetProcessById(parent)) p.WaitForExit(10000); } catch { }
            }

            // 先确认这里确实是本程序的文件夹，再动手
            // 下面那些"程序自带脚本在哪""设置在哪"的代码都按原来的文件夹来算
            AppDir = target;
            DataDir = Path.Combine(target, "data");
            LoadSettings();          // 界面用的是哪种语言、片段存在哪里

            // 程序不会装在盘符根目录（安装程序不允许）。万一是：不删任何东西，免得把同名的文件夹当成程序的删掉
            bool driveRoot = false;
            try { driveRoot = string.Equals(Path.GetPathRoot(target + "\\").TrimEnd('\\'), target, StringComparison.OrdinalIgnoreCase); } catch { driveRoot = true; }
            if (driveRoot || !File.Exists(Path.Combine(target, "ReplayClipper.exe")) || !File.Exists(Path.Combine(target, "ui", "index.html")))
            {
                Ask(Tr("未在此位置找到 " + AppName + " 的程序文件，卸载已取消：\n", AppName + "'s program files weren't found here. Uninstall was cancelled:\n") + target,
                    MbOk | MbWarning, IdOk);
                DeleteSelfLater();
                return 1;
            }
            string clips = ResolveFolder();
            bool cleanObs = !skipObs;

            var problems = new List<string>();
            if (cleanObs)
            {
                try { UninstallFromObs(removeDedicated, problems); }
                catch (Exception ex) { problems.Add(Tr("未能清理 OBS 中的设置：", "Couldn't clean up the OBS settings: ") + ex.Message); }
            }
            StopEngineHelpers(target);
            ClearBuffer();               // 回放缓存可能被用户放到了别处（设置里的"缓存位置"）：只删程序自己建的那个子文件夹
            RemoveAutostart(target);
            try { RemoveShortcuts(target); } catch { }
            try
            {
                // 早期版本放在系统盘的数据
                string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReplayClipper");
                if (Directory.Exists(legacy)) Directory.Delete(legacy, true);
            }
            catch { }
            DeleteProgramFiles(target, problems);
            UnregisterInstalledApp(target);      // 从系统的"已安装的应用"列表里去掉

            var sb = new StringBuilder();
            sb.AppendLine(problems.Count == 0
                ? Tr(AppName + " 已卸载。", AppName + " has been uninstalled.")
                : Tr(AppName + " 已卸载，但以下项目未能完成：", AppName + " has been uninstalled, but some items couldn't be completed:"));
            if (problems.Count > 0)
            {
                sb.AppendLine();
                foreach (string p in problems) sb.AppendLine("  · " + p);
            }
            sb.AppendLine();
            sb.AppendLine(Tr("已保留：", "Kept:"));
            sb.Append(KeptClipsLine(clips));
            if (cleanObs)
            {
                sb.AppendLine();
                sb.Append(Tr("  · OBS 及其其他设置", "  · OBS and its other settings"));
            }
            Ask(sb.ToString(), MbOk | (problems.Count == 0 ? MbInfo : MbWarning), IdOk);

            DeleteSelfLater();
            return problems.Count == 0 ? 0 : 2;
        }

        // 从 OBS 的配置里撤掉本程序留下的东西
        static void UninstallFromObs(bool removeDedicated, List<string> problems)
        {
            string root = ObsConfigRoot();
            string scenes = Path.Combine(root, "basic", "scenes");

            foreach (string f in ObsCollectionFiles())
            {
                try { RemoveFromCollection(f); }
                catch (Exception ex)
                {
                    string name = Path.GetFileName(f);
                    problems.Add(Tr("未能从 OBS 场景集合 " + name + " 中移除脚本：", "Couldn't remove the script from the OBS scene collection " + name + ": ") + ex.Message);
                }
            }

            string userIni = File.Exists(Path.Combine(root, "user.ini")) ? Path.Combine(root, "user.ini")
                           : File.Exists(Path.Combine(root, "global.ini")) ? Path.Combine(root, "global.ini") : null;
            if (removeDedicated && userIni != null) RemoveDedicatedObsConfig(root, userIni, problems);

            // 本程序改动 OBS 配置之前留下的备份
            if (Directory.Exists(scenes))
                foreach (string f in Directory.GetFiles(scenes))
                    if (f.EndsWith(".before-replayclipper", StringComparison.OrdinalIgnoreCase)) TryDeleteFile(f);
            foreach (string n in new[] { "user.ini", "global.ini" }) TryDeleteFile(Path.Combine(root, n + ".before-replayclipper"));
        }

        // 把本程序的脚本从一个场景集合的脚本列表里去掉；文件的其余内容原样不动
        static void RemoveFromCollection(string file)
        {
            bool bom;
            string text = ReadUtf8(file, out bom);
            int root, modules, a0, a1;
            List<object> existing = ReadScriptEntries(text, out root, out modules, out a0, out a1);
            if (a0 < 0) return;

            var keep = new List<object>();
            bool removed = false;
            foreach (object o in existing)
            {
                var d = o as Dictionary<string, object>;
                object p;
                if (d != null && d.TryGetValue("path", out p) && IsOurScript(p))
                {
                    string path = ((string)p).Replace('/', '\\');
                    // 指向本文件夹里那份脚本的，或者指向一个已经不存在的同名脚本的，都去掉
                    if (SamePath(path, LegacyScript) || !File.Exists(path)) { removed = true; continue; }
                }
                keep.Add(o);
            }
            if (!removed) return;

            var js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            string result = text.Substring(0, a0) + js.Serialize(keep) + text.Substring(a1);
            int r2, m2, b0, b1;
            ReadScriptEntries(result, out r2, out m2, out b0, out b1);      // 改完还能正常读出来才写回去

            byte[] body = new UTF8Encoding(false).GetBytes(result);
            using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write))
            {
                if (bom) fs.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                fs.Write(body, 0, body.Length);
            }
        }

        static string CollectionDisplayName(string file)
        {
            try
            {
                bool bom;
                string text = ReadUtf8(file, out bom);
                int a, b;
                if (FindMember(text, SkipWs(text, 0), "name", out a, out b) && text[a] == '"')
                    return new JavaScriptSerializer().Deserialize<string>(text.Substring(a, b - a));
            }
            catch { }
            return Path.GetFileNameWithoutExtension(file);
        }

        // 删除「配置 OBS」建出来的专用配置文件和场景集合。
        // 如果 OBS 当前正用着它们，先切回配置之前用的那一套（没有就换成任意一套别的）；一套别的都没有时就不删。
        static void RemoveDedicatedObsConfig(string root, string userIni, List<string> problems)
        {
            string profilesDir = Path.Combine(root, "basic", "profiles"), scenesDir = Path.Combine(root, "basic", "scenes");
            var cur = ReadIni(userIni);
            string curProfileDir = IniGet(cur, "Basic", "ProfileDir") ?? "";
            string curSceneFile = IniGet(cur, "Basic", "SceneCollectionFile") ?? "";
            bool withExt = curSceneFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            bool profileActive = string.Equals(curProfileDir, OurProfileDir, StringComparison.OrdinalIgnoreCase);
            bool sceneActive = string.Equals(withExt ? Path.GetFileNameWithoutExtension(curSceneFile) : curSceneFile, OurCollectionFile, StringComparison.OrdinalIgnoreCase);

            string backupPath = userIni + ".before-replayclipper";
            var backup = File.Exists(backupPath) ? ReadIni(backupPath) : null;

            string newProfileDir = null, newProfileName = null, newSceneStem = null, newSceneName = null;
            if (profileActive)
            {
                string dir = backup != null ? IniGet(backup, "Basic", "ProfileDir") : null;
                if (!string.IsNullOrEmpty(dir) && !string.Equals(dir, OurProfileDir, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(profilesDir, dir, "basic.ini")))
                {
                    newProfileDir = dir;
                    newProfileName = IniGet(backup, "Basic", "Profile");
                }
                else if (Directory.Exists(profilesDir))
                {
                    foreach (string d in Directory.GetDirectories(profilesDir))
                    {
                        string n = Path.GetFileName(d);
                        if (string.Equals(n, OurProfileDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(d, "basic.ini"))) continue;
                        newProfileDir = n;
                        break;
                    }
                }
                if (newProfileDir == null)
                {
                    problems.Add(Tr("OBS 中没有其他配置文件可供切换，因此保留了「" + AppName + "」配置。",
                                    "OBS has no other profile to switch to, so the \"" + AppName + "\" setup was kept."));
                    return;
                }
                if (string.IsNullOrEmpty(newProfileName))
                    newProfileName = IniGet(ReadIni(Path.Combine(profilesDir, newProfileDir, "basic.ini")), "General", "Name") ?? newProfileDir;
            }
            if (sceneActive)
            {
                string stem = backup != null ? IniGet(backup, "Basic", "SceneCollectionFile") : null;
                if (!string.IsNullOrEmpty(stem) && stem.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) stem = stem.Substring(0, stem.Length - 5);
                if (!string.IsNullOrEmpty(stem) && !string.Equals(stem, OurCollectionFile, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(scenesDir, stem + ".json")))
                {
                    newSceneStem = stem;
                }
                else
                {
                    foreach (string f in ObsCollectionFiles())
                    {
                        string n = Path.GetFileNameWithoutExtension(f);
                        if (string.Equals(n, OurCollectionFile, StringComparison.OrdinalIgnoreCase)) continue;
                        newSceneStem = n;
                        break;
                    }
                }
                if (newSceneStem == null)
                {
                    problems.Add(Tr("OBS 中没有其他场景集合可供切换，因此保留了「" + AppName + "」配置。",
                                    "OBS has no other scene collection to switch to, so the \"" + AppName + "\" setup was kept."));
                    return;
                }
                newSceneName = CollectionDisplayName(Path.Combine(scenesDir, newSceneStem + ".json"));
            }

            if (profileActive || sceneActive)
            {
                IniDoc doc = IniDoc.Load(userIni);
                if (profileActive)
                {
                    doc.Set("Basic", "Profile", newProfileName);
                    doc.Set("Basic", "ProfileDir", newProfileDir);
                }
                if (sceneActive)
                {
                    doc.Set("Basic", "SceneCollection", newSceneName);
                    doc.Set("Basic", "SceneCollectionFile", newSceneStem + (withExt ? ".json" : ""));
                }
                doc.Save(userIni);
            }

            try
            {
                string ourProfile = Path.Combine(profilesDir, OurProfileDir);
                if (Directory.Exists(ourProfile)) Directory.Delete(ourProfile, true);
            }
            catch (Exception ex) { problems.Add(Tr("未能删除 OBS 中的「" + AppName + "」配置文件：", "Couldn't delete the \"" + AppName + "\" profile in OBS: ") + ex.Message); }
            string ourScene = Path.Combine(scenesDir, OurCollectionFile + ".json");
            foreach (string suffix in new[] { "", ".bak", ".previous", ".before-replayclipper", ".v1" }) TryDeleteFile(ourScene + suffix);
        }

        static void TryDeleteFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            catch { }
        }

        // 删掉桌面和开始菜单里指向这个文件夹的快捷方式
        static void RemoveShortcuts(string target)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            object shell = Activator.CreateInstance(shellType);
            var folders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            };
            // 只删指向本程序自己的快捷方式。按"在这个文件夹下面"来比的话，程序装在别的软件旁边时会连它们的快捷方式一起删掉
            string[] ours = { Path.Combine(target, "ReplayClipper.exe"), Path.Combine(target, "Uninstall.exe") };
            foreach (string folder in folders)
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                string[] links;
                try { links = Directory.GetFiles(folder, "*.lnk"); } catch { continue; }
                foreach (string lnk in links)
                {
                    try
                    {
                        object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                        string tp = sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) as string;
                        if (!string.IsNullOrEmpty(tp) && ours.Any(o => string.Equals(tp, o, StringComparison.OrdinalIgnoreCase))) TryDeleteFile(lnk);
                    }
                    catch { }
                }
            }
        }

        // 只删本程序自己的文件和子文件夹；文件夹本身只有在删空之后才删
        static void DeleteProgramFiles(string target, List<string> problems)
        {
            string[] files =
            {
                "ReplayClipper.exe", "Uninstall.exe", "build.cmd", "README.txt", "README.zh-CN.txt",
                "使用说明.txt",      // 早期版本的说明文件
                "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll",
            };
            // 整个删掉的子文件夹：只有里面有本程序特有的文件时才算是程序自己的（同名的文件夹可能是用户的）。
            // obs 是 1.0 版的脚本所在的文件夹（升级安装后可能还留着）
            var dirs = new List<string>();
            foreach (string[] dm in new[]
            {
                new[] { "ui", "index.html" },
                new[] { "engine", "bin\\64bit\\ReplayClipperEngine.exe" },
                new[] { "ffmpeg", "README.txt" },
                new[] { "src", "engine\\PROTOCOL.md" },
                new[] { "obs", ScriptFileName },
            })
            {
                string marker = Path.Combine(target, dm[0], dm[1]);
                if (!File.Exists(marker)) continue;
                if (dm[0] == "ffmpeg")
                {
                    string text = "";
                    try { text = File.ReadAllText(marker); } catch { }
                    if (text.IndexOf("Replay Clipper", StringComparison.Ordinal) < 0) continue;
                }
                dirs.Add(dm[0]);
            }
            // data 里只删程序自己建的东西（设置、日志、回放缓存、录制组件和窗口组件的数据），删空了才删 data 本身
            string data = Path.Combine(target, "data");
            string[] dataFiles = { "settings.json", "settings.json.bak", "settings.json.tmp", "log.txt", "instance.txt" };
            string[] dataDirs = { "buffer", "engine", "webview", "browser" };

            // 刚关掉的窗口组件可能还要一两秒才松开文件，多试几次
            for (int attempt = 0; attempt < 14; attempt++)
            {
                bool allGone = true;
                foreach (string f in files.Concat(dataFiles.Select(n => "data\\" + n)))
                {
                    string p = Path.Combine(target, f);
                    TryDeleteFile(p);
                    if (File.Exists(p)) allGone = false;
                }
                foreach (string d in dirs.Concat(dataDirs.Select(n => "data\\" + n)))
                {
                    string p = Path.Combine(target, d);
                    try { if (Directory.Exists(p)) Directory.Delete(p, true); } catch { }
                    if (Directory.Exists(p)) allGone = false;
                }
                try { if (Directory.Exists(data) && !Directory.EnumerateFileSystemEntries(data).Any()) Directory.Delete(data); } catch { }
                if (allGone) break;
                Thread.Sleep(500);
            }

            var left = new List<string>();
            foreach (string f in files) if (File.Exists(Path.Combine(target, f))) left.Add(f);
            foreach (string d in dirs.Concat(dataDirs.Select(n => "data\\" + n))) if (Directory.Exists(Path.Combine(target, d))) left.Add(d + "\\");
            foreach (string f in dataFiles) if (File.Exists(Path.Combine(data, f))) left.Add("data\\" + f);
            if (left.Count > 0)
                problems.Add(Tr("以下文件正在使用中，未能删除，请稍后手动删除：" + target + "\\ 下的 " + string.Join("、", left.ToArray()),
                                "These items are in use and couldn't be deleted. Delete them manually later: " + string.Join(", ", left.ToArray()) + " in " + target));

            try
            {
                if (!Directory.Exists(target)) return;
                if (Directory.EnumerateFileSystemEntries(target).Any())
                {
                    if (left.Count == 0)
                        problems.Add(Tr("程序文件夹中还有其他文件，因此未删除该文件夹：", "The app folder contains other files, so the folder itself wasn't deleted: ") + target);
                }
                else
                {
                    Directory.Delete(target);
                }
            }
            catch { }
        }

        // 副本自己没法在运行时删除自己：留一条延迟几秒的删除命令
        static void DeleteSelfLater()
        {
            try
            {
                string self = Assembly.GetExecutingAssembly().Location;
                var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 4 127.0.0.1 >nul & del /f /q \"" + self + "\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Path.GetTempPath();
                Process.Start(psi);
            }
            catch { }
        }
    }
}
