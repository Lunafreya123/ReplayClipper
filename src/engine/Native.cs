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

// P/Invoke surface. Every obs.dll entry below is an exported function of OBS Studio 33.0.1 (x64);
// struct layouts and enum values are transcribed from the 33.0.1 headers (libobs/obs.h,
// callback/calldata.h, media-io/video-io.h, media-io/audio-io.h, graphics/vec2.h).
// The helper refuses to run against any other OBS version (see Engine.PinnedObsVersion).
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ReplayClipperEngine
{
    static class Obs
    {
        const string L = "obs.dll";
        const CallingConvention CC = CallingConvention.Cdecl;

        public const int LOG_ERROR = 100, LOG_WARNING = 200, LOG_INFO = 300, LOG_DEBUG = 400;
        public const int VIDEO_FORMAT_NV12 = 2;
        public const int VIDEO_CS_709 = 2;
        public const int VIDEO_RANGE_PARTIAL = 1;
        public const int OBS_SCALE_BICUBIC = 2;
        public const int OBS_BOUNDS_SCALE_INNER = 2;
        public const uint OBS_ALIGN_CENTER = 0, OBS_ALIGN_LEFT = 1, OBS_ALIGN_TOP = 4;
        public const int SPEAKERS_STEREO = 2;
        public const int OBS_ENCODER_VIDEO = 1;
        public const uint OBS_ENCODER_CAP_DEPRECATED = 1, OBS_ENCODER_CAP_INTERNAL = 8;

        [UnmanagedFunctionPointer(CC)]
        public delegate void LogHandler(int lvl, IntPtr fmt, IntPtr vaList, IntPtr param);
        [UnmanagedFunctionPointer(CC)]
        public delegate void SignalCallback(IntPtr data, IntPtr calldata);
        [UnmanagedFunctionPointer(CC)]
        public delegate void TaskFn(IntPtr param);
        [UnmanagedFunctionPointer(CC)]
        public delegate void TaskHandler(IntPtr task, IntPtr param, [MarshalAs(UnmanagedType.I1)] bool wait);

        // struct obs_video_info - 56 bytes on x64
        [StructLayout(LayoutKind.Sequential)]
        public struct VideoInfo
        {
            public IntPtr graphics_module;
            public uint fps_num;
            public uint fps_den;
            public uint base_width;
            public uint base_height;
            public uint output_width;
            public uint output_height;
            public int output_format;
            public uint adapter;
            [MarshalAs(UnmanagedType.I1)]
            public bool gpu_conversion;
            public int colorspace;
            public int range;
            public int scale_type;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct AudioInfo
        {
            public uint samples_per_sec;
            public int speakers;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Vec2
        {
            public float x, y;
        }

        // ---- core ---------------------------------------------------------------
        [DllImport(L, CallingConvention = CC)]
        public static extern void base_set_log_handler(LogHandler handler, IntPtr param);
        [DllImport(L, CallingConvention = CC)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool obs_startup(byte[] locale, byte[] moduleConfigPath, IntPtr profilerStore);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_shutdown();
        [DllImport(L, CallingConvention = CC)]
        public static extern IntPtr obs_get_version_string();
        [DllImport(L, CallingConvention = CC)]
        public static extern uint obs_get_version();
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_add_data_path(byte[] path);
        [DllImport(L, CallingConvention = CC)]
        public static extern int obs_reset_video(ref VideoInfo ovi);
        [DllImport(L, CallingConvention = CC)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool obs_reset_audio(ref AudioInfo oai);
        [DllImport(L, CallingConvention = CC)]
        public static extern IntPtr obs_get_video();
        [DllImport(L, CallingConvention = CC)]
        public static extern IntPtr obs_get_audio();
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_set_ui_task_handler(TaskHandler handler);
        [DllImport(L, CallingConvention = CC)]
        public static extern void bfree(IntPtr ptr);

        // ---- modules ------------------------------------------------------------
        [DllImport(L, CallingConvention = CC)]
        public static extern int obs_open_module(out IntPtr module, byte[] path, byte[] dataPath);
        [DllImport(L, CallingConvention = CC)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool obs_init_module(IntPtr module);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_post_load_modules();
        [DllImport(L, CallingConvention = CC)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool obs_enum_encoder_types(UIntPtr idx, out IntPtr id);
        [DllImport(L, CallingConvention = CC)]
        public static extern IntPtr obs_get_encoder_codec(byte[] id);
        [DllImport(L, CallingConvention = CC)]
        public static extern int obs_get_encoder_type(byte[] id);
        [DllImport(L, CallingConvention = CC)]
        public static extern uint obs_get_encoder_caps(byte[] id);

        // ---- obs_data -----------------------------------------------------------
        [DllImport(L, CallingConvention = CC)]
        public static extern IntPtr obs_data_create();
        [DllImport(L, CallingConvention = CC)]
        public static extern IntPtr obs_data_create_from_json(byte[] json);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_data_release(IntPtr data);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_data_set_string(IntPtr data, byte[] name, byte[] val);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_data_set_int(IntPtr data, byte[] name, long val);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_data_set_bool(IntPtr data, byte[] name, [MarshalAs(UnmanagedType.I1)] bool val);
        [DllImport(L, CallingConvention = CC)]
        public static extern IntPtr obs_data_get_json(IntPtr data);

        // ---- sources / scene ----------------------------------------------------
        [DllImport(L, CallingConvention = CC)]
        public static extern IntPtr obs_source_create(byte[] id, byte[] name, IntPtr settings, IntPtr hotkeyData);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_source_release(IntPtr source);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_source_update(IntPtr source, IntPtr settings);
        [DllImport(L, CallingConvention = CC)]
        public static extern uint obs_source_get_width(IntPtr source);
        [DllImport(L, CallingConvention = CC)]
        public static extern uint obs_source_get_height(IntPtr source);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_set_output_source(uint channel, IntPtr source);
        [DllImport(L, CallingConvention = CC)]
        public static extern IntPtr obs_scene_create(byte[] name);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_scene_release(IntPtr scene);
        [DllImport(L, CallingConvention = CC)]
        public static extern IntPtr obs_scene_get_source(IntPtr scene);
        [DllImport(L, CallingConvention = CC)]
        public static extern IntPtr obs_scene_add(IntPtr scene, IntPtr source);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_sceneitem_set_pos(IntPtr item, ref Vec2 pos);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_sceneitem_set_alignment(IntPtr item, uint alignment);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_sceneitem_set_bounds_type(IntPtr item, int type);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_sceneitem_set_bounds_alignment(IntPtr item, uint alignment);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_sceneitem_set_bounds(IntPtr item, ref Vec2 bounds);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_sceneitem_set_scale_filter(IntPtr item, int filter);

        // ---- encoders -----------------------------------------------------------
        [DllImport(L, CallingConvention = CC)]
        public static extern IntPtr obs_video_encoder_create(byte[] id, byte[] name, IntPtr settings, IntPtr hotkeyData);
        [DllImport(L, CallingConvention = CC)]
        public static extern IntPtr obs_audio_encoder_create(byte[] id, byte[] name, IntPtr settings, UIntPtr mixerIdx, IntPtr hotkeyData);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_encoder_set_video(IntPtr encoder, IntPtr video);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_encoder_set_audio(IntPtr encoder, IntPtr audio);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_encoder_release(IntPtr encoder);
        [DllImport(L, CallingConvention = CC)]
        public static extern IntPtr obs_encoder_get_last_error(IntPtr encoder);
        [DllImport(L, CallingConvention = CC)]
        public static extern uint obs_encoder_get_priming_samples(IntPtr encoder);

        // ---- outputs ------------------------------------------------------------
        [DllImport(L, CallingConvention = CC)]
        public static extern IntPtr obs_output_create(byte[] id, byte[] name, IntPtr settings, IntPtr hotkeyData);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_output_release(IntPtr output);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_output_set_video_encoder(IntPtr output, IntPtr encoder);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_output_set_audio_encoder(IntPtr output, IntPtr encoder, UIntPtr idx);
        [DllImport(L, CallingConvention = CC)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool obs_output_start(IntPtr output);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_output_stop(IntPtr output);
        [DllImport(L, CallingConvention = CC)]
        public static extern void obs_output_force_stop(IntPtr output);
        [DllImport(L, CallingConvention = CC)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool obs_output_active(IntPtr output);
        [DllImport(L, CallingConvention = CC)]
        public static extern IntPtr obs_output_get_last_error(IntPtr output);
        [DllImport(L, CallingConvention = CC)]
        public static extern int obs_output_get_total_frames(IntPtr output);
        [DllImport(L, CallingConvention = CC)]
        public static extern IntPtr obs_output_get_signal_handler(IntPtr output);

        // ---- signals / calldata -------------------------------------------------
        [DllImport(L, CallingConvention = CC)]
        public static extern void signal_handler_connect(IntPtr handler, byte[] signal, SignalCallback cb, IntPtr data);
        [DllImport(L, CallingConvention = CC)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool calldata_get_string(IntPtr calldata, byte[] name, out IntPtr str);
        [DllImport(L, CallingConvention = CC)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool calldata_get_data(IntPtr calldata, byte[] name, out long val, UIntPtr size);

        // ---- stats --------------------------------------------------------------
        [DllImport(L, CallingConvention = CC)]
        public static extern uint obs_get_total_frames();
        [DllImport(L, CallingConvention = CC)]
        public static extern uint obs_get_lagged_frames();
        [DllImport(L, CallingConvention = CC)]
        public static extern uint video_output_get_skipped_frames(IntPtr video);

        // ---- helpers ------------------------------------------------------------
        /// <summary>NUL-terminated UTF-8; libobs strings are UTF-8.</summary>
        public static byte[] U(string s)
        {
            if (s == null) return null;
            byte[] raw = Encoding.UTF8.GetBytes(s);
            byte[] z = new byte[raw.Length + 1];
            Buffer.BlockCopy(raw, 0, z, 0, raw.Length);
            return z;
        }

        public static string S(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            int len = 0;
            while (Marshal.ReadByte(p, len) != 0) len++;
            byte[] buf = new byte[len];
            Marshal.Copy(p, buf, 0, len);
            return Encoding.UTF8.GetString(buf);
        }
    }

    static class Win
    {
        [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int __stdio_common_vsprintf(ulong options, byte[] buffer, UIntPtr count, IntPtr format, IntPtr locale, IntPtr vaList);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("kernel32.dll")]
        public static extern uint SetErrorMode(uint mode);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX mi);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplayDevicesW(string device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);
        public const uint EDD_GET_DEVICE_INTERFACE_NAME = 1;

        public const uint SYNCHRONIZE = 0x00100000, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll")]
        public static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        // PROCESS_MEMORY_COUNTERS_EX2 - 96 bytes on x64
        [StructLayout(LayoutKind.Sequential)]
        public struct PMC_EX2
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
            public ulong PrivateWorkingSetSize;
            public ulong SharedCommitUsage;
        }

        [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
        public static extern bool GetProcessMemoryInfo(IntPtr process, ref PMC_EX2 counters, uint cb);
    }
}
