// ReplayClipperEngine - headless libobs recording helper for Replay Clipper.
// Copyright (C) 2026 the Replay Clipper authors
//
// This program is free software; you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation; either version 2 of the
// License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without
// even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
// General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with this program; if not,
// see <https://www.gnu.org/licenses/>.
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ReplayClipperEngine
{
    /// <summary>Log file in --log-dir: engine.log, rotated to engine.1.log .. engine.3.log at 4 MB.</summary>
    static class Log
    {
        const long MaxBytes = 4L * 1024 * 1024;
        const int Keep = 3;
        static readonly object Gate = new object();
        static string _dir;
        static StreamWriter _file;
        static long _written;
        public static int ObsErrors, ObsWarnings;

        public static void Open(string dir)
        {
            lock (Gate)
            {
                _dir = dir;
                Directory.CreateDirectory(dir);
                OpenFile();
            }
        }

        static void OpenFile()
        {
            string path = Path.Combine(_dir, "engine.log");
            FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _written = fs.Length;
            _file = new StreamWriter(fs, new UTF8Encoding(false));
            _file.AutoFlush = true;
        }

        static void Rotate()
        {
            try
            {
                _file.Dispose();
                for (int i = Keep; i >= 1; i--)
                {
                    string from = Path.Combine(_dir, i == 1 ? "engine.log" : "engine." + (i - 1) + ".log");
                    string to = Path.Combine(_dir, "engine." + i + ".log");
                    if (!File.Exists(from)) continue;
                    if (File.Exists(to)) File.Delete(to);
                    File.Move(from, to);
                }
            }
            catch { }
            try { OpenFile(); } catch { _file = null; }
        }

        public static void Line(string tag, string text)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + tag + " " + text;
            lock (Gate)
            {
                if (_file == null) return;
                try
                {
                    _file.WriteLine(line);
                    _written += line.Length + 2;
                    if (_written > MaxBytes) Rotate();
                }
                catch { }
            }
        }

        public static void Info(string fmt, params object[] args)
        {
            Line("[engine]", args.Length == 0 ? fmt : string.Format(CultureInfo.InvariantCulture, fmt, args));
        }

        // libobs log callback: (int lvl, const char *fmt, va_list args, void *param).
        // On x64 a va_list is a plain pointer, so it can be handed to the UCRT formatter as is.
        public static void FromObs(int lvl, IntPtr fmt, IntPtr vaList, IntPtr param)
        {
            try
            {
                if (lvl <= Obs.LOG_ERROR) Interlocked.Increment(ref ObsErrors);
                else if (lvl <= Obs.LOG_WARNING) Interlocked.Increment(ref ObsWarnings);
                if (lvl > Obs.LOG_INFO) return;
                byte[] buf = new byte[4096];
                // options = 2: _CRT_INTERNAL_PRINTF_STANDARD_SNPRINTF_BEHAVIOR
                int n = Win.__stdio_common_vsprintf(2UL, buf, (UIntPtr)(uint)buf.Length, fmt, IntPtr.Zero, vaList);
                if (n < 0) n = 0;
                if (n > buf.Length - 1) n = buf.Length - 1;
                string text = Encoding.UTF8.GetString(buf, 0, n);
                // the muxer announces every segment file; at one per two seconds that would drown the log
                if (lvl > Obs.LOG_WARNING && text.IndexOf("Changing output file to", StringComparison.Ordinal) >= 0) return;
                string tag = lvl <= Obs.LOG_ERROR ? "[obs:error]" : lvl <= Obs.LOG_WARNING ? "[obs:warn]" : "[obs]";
                Line(tag, text);
            }
            catch { }
        }
    }
}
