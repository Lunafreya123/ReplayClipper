// LegacyObs.cs —— 1.0 版留下的东西。
//
// 1.0 版靠 OBS 录制：在 OBS 的每个场景集合里登记了一个脚本（obs\save-recent-clip.lua），「配置 OBS」还会在
// OBS 里建一套名为 Replay Clipper 的配置文件和场景集合。现在程序自己录制，不再用 OBS。这里只剩下收尾：
//   · 程序启动时，悄悄把那条脚本登记从 OBS 的场景集合里撤掉（OBS 开着时改了也白改，留到以后某次启动再做）；
//   · 第一次从 1.0 升级上来时，沿用原来在 OBS 里设的保存位置，这样以前存下的片段照常看得到；
//   · 卸载时照旧可以把那套配置文件和场景集合删掉（见 Uninstall.cs）。
// 除此之外，程序不读、不写、不启动也不关闭 OBS。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ReplayClipper
{
    static partial class Program
    {
        const string ScriptFileName = "save-recent-clip.lua";
        const string OurProfileDir = "ReplayClipper";         // 「配置 OBS」建的配置文件所在的文件夹名
        const string OurCollectionFile = "ReplayClipper";     // 场景集合的文件名，不含 .json

        static string SettingObsPath;           // 1.0 版设置里的 OBS 安装位置（便携版 OBS 的配置在它自己的文件夹里）
        static bool SettingLegacyCleaned;       // OBS 里的脚本登记已经撤掉了

        // 1.0 版的脚本原来所在的位置（升级安装不会删掉旧文件，所以它可能还在）
        static string LegacyScript { get { return Path.Combine(AppDir, "obs", ScriptFileName); } }

        // 用另一个数据目录跑的测试实例不去碰真正的 OBS 配置，除非同时用 REPLAYCLIP_OBS_CONFIG 指定了一份假的
        static bool LegacyAllowed
        {
            get
            {
                return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("REPLAYCLIP_DATA"))
                    || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("REPLAYCLIP_OBS_CONFIG"));
            }
        }

        static bool ObsIsRunning()
        {
            Process[] ps = Process.GetProcessesByName("obs64");
            bool any = ps.Length > 0;
            foreach (Process p in ps) p.Dispose();
            return any;
        }

        // OBS 的配置在哪：普通安装在 %APPDATA%\obs-studio；便携版（安装目录里有 portable_mode.txt）在它自己的文件夹里
        static string ObsConfigRoot()
        {
            string o = Environment.GetEnvironmentVariable("REPLAYCLIP_OBS_CONFIG");      // 仅测试用
            if (!string.IsNullOrEmpty(o)) return o;
            try
            {
                string root = (SettingObsPath ?? "").Trim();
                if (root.Length > 0 && (File.Exists(Path.Combine(root, "portable_mode.txt")) || File.Exists(Path.Combine(root, "obs_portable_mode.txt"))))
                    return Path.Combine(root, "config", "obs-studio");
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio");
        }

        // OBS 当前配置文件里的录像路径：1.0 版把片段存在那里。读不到返回 null。
        static string LegacySaveFolder()
        {
            try
            {
                if (!LegacyAllowed) return null;
                string root = ObsConfigRoot(), profileDir = null;
                foreach (string f in new[] { "user.ini", "global.ini" })
                {
                    string p = Path.Combine(root, f);
                    if (!File.Exists(p)) continue;
                    profileDir = IniGet(ReadIni(p), "Basic", "ProfileDir");
                    if (!string.IsNullOrEmpty(profileDir)) break;
                }
                if (string.IsNullOrEmpty(profileDir)) return null;
                string iniPath = Path.Combine(root, "basic", "profiles", profileDir, "basic.ini");
                if (!File.Exists(iniPath)) return null;
                var prof = ReadIni(iniPath);
                string path = string.Equals(IniGet(prof, "Output", "Mode"), "Advanced", StringComparison.OrdinalIgnoreCase)
                    ? IniGet(prof, "AdvOut", "RecFilePath")
                    : IniGet(prof, "SimpleOutput", "FilePath");
                if (string.IsNullOrEmpty(path)) return null;
                return path.Replace("\\\\", "\\").Replace('/', '\\');
            }
            catch (Exception ex)
            {
                Log("LegacySaveFolder: " + ex.Message);
                return null;
            }
        }

        // 把 1.0 版登记在 OBS 里的脚本撤掉。做成了就记下来，以后不再做；OBS 正开着就留到下一次启动。
        static void LegacyCleanup()
        {
            try
            {
                if (SettingLegacyCleaned || !LegacyAllowed) return;
                bool realConfig = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("REPLAYCLIP_OBS_CONFIG"));
                if (realConfig && ObsIsRunning()) return;      // OBS 退出时会重写这些文件
                foreach (string f in ObsCollectionFiles())
                {
                    try { RemoveFromCollection(f); }
                    catch (Exception ex)
                    {
                        Log("legacy cleanup: " + Path.GetFileName(f) + ": " + ex.Message);
                        return;      // 下次启动再试
                    }
                }
                TryDeleteFile(LegacyScript);
                try
                {
                    string dir = Path.GetDirectoryName(LegacyScript);
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                }
                catch { }
                SettingLegacyCleaned = true;
                SaveSettings();
                Log("legacy cleanup done");
            }
            catch (Exception ex) { Log("legacy cleanup: " + ex.Message); }
        }

        // ---- 读 OBS 的配置文件

        static Dictionary<string, Dictionary<string, string>> ReadIni(string path)
        {
            var d = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> cur = null;
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line.EndsWith("]"))
                {
                    cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    d[line.Substring(1, line.Length - 2)] = cur;
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0 || cur == null) continue;
                cur[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return d;
        }

        static string IniGet(Dictionary<string, Dictionary<string, string>> ini, string section, string key)
        {
            Dictionary<string, string> s;
            string v;
            if (ini.TryGetValue(section, out s) && s.TryGetValue(key, out v)) return v;
            return null;
        }

        static List<string> ObsCollectionFiles()
        {
            string dir = Path.Combine(ObsConfigRoot(), "basic", "scenes");
            var list = new List<string>();
            if (!Directory.Exists(dir)) return list;
            foreach (string f in Directory.GetFiles(dir))
                if (f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) list.Add(f);
            return list;
        }

        // ---- 极简的 JSON 定位：只为了在 OBS 的场景集合文件里找到脚本列表那一小段，其余内容原样不动

        static int SkipWs(string s, int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return i;
        }

        static int SkipString(string s, int i)
        {
            i++;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\') i += 2;
                else if (c == '"') return i + 1;
                else i++;
            }
            throw new FormatException("Unterminated JSON string");
        }

        static int SkipValue(string s, int i)
        {
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[i];
            if (c == '"') return SkipString(s, i);
            if (c == '{' || c == '[')
            {
                int depth = 0;
                while (i < s.Length)
                {
                    c = s[i];
                    if (c == '"') { i = SkipString(s, i); continue; }
                    if (c == '{' || c == '[') depth++;
                    else if (c == '}' || c == ']')
                    {
                        depth--;
                        if (depth == 0) return i + 1;
                    }
                    i++;
                }
                throw new FormatException("Mismatched JSON brackets");
            }
            while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']' && !char.IsWhiteSpace(s[i])) i++;
            return i;
        }

        // 在 objStart（一个 '{'）开始的对象里找键 key，返回它的值所占的区间 [valStart, valEnd)
        static bool FindMember(string s, int objStart, string key, out int valStart, out int valEnd)
        {
            valStart = valEnd = -1;
            int i = SkipWs(s, objStart + 1);
            while (i < s.Length && s[i] != '}')
            {
                if (s[i] != '"') throw new FormatException("Malformed JSON");
                int keyEnd = SkipString(s, i);
                string k = s.Substring(i + 1, keyEnd - i - 2);
                i = SkipWs(s, keyEnd);
                if (i >= s.Length || s[i] != ':') throw new FormatException("Malformed JSON");
                i = SkipWs(s, i + 1);
                int ve = SkipValue(s, i);
                if (k == key) { valStart = i; valEnd = ve; return true; }
                i = SkipWs(s, ve);
                if (i < s.Length && s[i] == ',') i = SkipWs(s, i + 1);
            }
            return false;
        }

        static string ReadUtf8(string file, out bool bom)
        {
            byte[] raw = File.ReadAllBytes(file);
            bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            return new UTF8Encoding(false).GetString(raw, bom ? 3 : 0, raw.Length - (bom ? 3 : 0));
        }

        static bool IsOurScript(object pathValue)
        {
            string p = pathValue as string;
            if (string.IsNullOrEmpty(p)) return false;
            p = p.Replace('/', '\\');
            int slash = p.LastIndexOf('\\');
            return string.Equals(slash >= 0 ? p.Substring(slash + 1) : p, ScriptFileName, StringComparison.OrdinalIgnoreCase);
        }

        // 这个场景集合里登记的脚本列表（没有则为空）；同时给出列表在文本里的位置
        static List<object> ReadScriptEntries(string text, out int root, out int modules, out int arrStart, out int arrEnd)
        {
            modules = arrStart = arrEnd = -1;
            root = SkipWs(text, 0);
            if (root >= text.Length || text[root] != '{') throw new FormatException("Not a JSON object");
            var list = new List<object>();
            int ms, me;
            if (!FindMember(text, root, "modules", out ms, out me) || text[ms] != '{') return list;
            modules = ms;
            int a0, a1;
            if (!FindMember(text, ms, "scripts-tool", out a0, out a1) || text[a0] != '[') return list;
            arrStart = a0;
            arrEnd = a1;
            var js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            var parsed = js.DeserializeObject(text.Substring(a0, a1 - a0)) as IEnumerable;
            if (parsed != null) foreach (object o in parsed) list.Add(o);
            return list;
        }

        // ---- 读写 OBS 的 ini（保持原有的编码、换行和其余内容）

        class IniDoc
        {
            public List<string> Lines = new List<string>();
            public bool Bom = true;
            public string Nl = "\n";

            public static IniDoc Load(string path)
            {
                var doc = new IniDoc();
                if (!File.Exists(path)) return doc;
                bool bom;
                string text = ReadUtf8(path, out bom);
                doc.Bom = bom;
                doc.Nl = text.Contains("\r\n") ? "\r\n" : "\n";
                doc.Lines = text.Replace("\r\n", "\n").Split('\n').ToList();
                while (doc.Lines.Count > 0 && doc.Lines[doc.Lines.Count - 1].Length == 0) doc.Lines.RemoveAt(doc.Lines.Count - 1);
                return doc;
            }

            public void Set(string section, string key, string value)
            {
                int start = -1;
                for (int i = 0; i < Lines.Count; i++)
                    if (string.Equals(Lines[i].Trim(), "[" + section + "]", StringComparison.OrdinalIgnoreCase)) { start = i; break; }
                if (start < 0)
                {
                    if (Lines.Count > 0) Lines.Add("");
                    Lines.Add("[" + section + "]");
                    Lines.Add(key + "=" + value);
                    return;
                }
                int end = start + 1;
                while (end < Lines.Count && !Lines[end].TrimStart().StartsWith("[")) end++;
                for (int i = start + 1; i < end; i++)
                {
                    int eq = Lines[i].IndexOf('=');
                    if (eq > 0 && string.Equals(Lines[i].Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    {
                        Lines[i] = key + "=" + value;
                        return;
                    }
                }
                int insert = end;
                while (insert > start + 1 && Lines[insert - 1].Trim().Length == 0) insert--;
                Lines.Insert(insert, key + "=" + value);
            }

            public void Save(string path)
            {
                byte[] body = new UTF8Encoding(false).GetBytes(string.Join(Nl, Lines) + Nl);
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    if (Bom) fs.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                    fs.Write(body, 0, body.Length);
                }
            }
        }
    }
}
