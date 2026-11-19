// UninstallStub.cs —— 编译成程序文件夹里的 Uninstall.exe。
// 它本身什么都不删，只是启动 "ReplayClipper.exe --uninstall"，真正的卸载逻辑在 Uninstall.cs 里。

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

static class UninstallStub
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [STAThread]
    static int Main()
    {
        string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string exe = Path.Combine(dir, "ReplayClipper.exe");
        if (!File.Exists(exe))
        {
            // 这里读不到程序里选的语言，按系统的显示语言来
            bool zh = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            MessageBoxW(IntPtr.Zero,
                zh ? "未在同一文件夹中找到 ReplayClipper.exe，无法卸载。\n如果程序文件已被删除，直接删除此文件夹即可。"
                   : "ReplayClipper.exe wasn't found next to this file, so the uninstaller can't run.\nIf the program files are already gone, just delete this folder.",
                zh ? "卸载 Replay Clipper" : "Uninstall Replay Clipper", 0x30);
            return 1;
        }
        var psi = new ProcessStartInfo(exe, "--uninstall");
        psi.UseShellExecute = false;
        psi.WorkingDirectory = Path.GetTempPath();      // 不占着程序文件夹，免得它删不掉
        Process.Start(psi);
        return 0;
    }
}
