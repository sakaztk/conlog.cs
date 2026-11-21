// conlog.exe : ConPTY wrapper that logs everything a console program prints
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ConLog;

static class Native
{
    public const int STD_INPUT_HANDLE = -10;
    public const int STD_OUTPUT_HANDLE = -11;
    public const uint ENABLE_PROCESSED_INPUT = 0x0001;
    public const uint ENABLE_LINE_INPUT = 0x0002;
    public const uint ENABLE_ECHO_INPUT = 0x0004;
    public const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;
    public const uint ENABLE_PROCESSED_OUTPUT = 0x0001;
    public const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint INFINITE = 0xFFFFFFFF;
    public static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    public struct COORD { public short X, Y; public COORD(short x, short y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1, PebBaseAddress, Reserved2a, Reserved2b, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleMode(IntPtr h, uint mode);
    [DllImport("kernel32.dll")] public static extern uint GetConsoleCP();
    [DllImport("kernel32.dll")] public static extern uint GetConsoleOutputCP();
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleCP(uint cp);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleOutputCP(uint cp);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attrs, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr hPC);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string? cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(IntPtr h, int infoClass, ref PROCESS_BASIC_INFORMATION info, int size, out int returned);
}

interface ILog : IDisposable
{
    /// <summary>Feeds child output. channel 0 = stdout / pseudo console, 1 = stderr (pipe mode).</summary>
    void Write(byte[] buf, int n, int channel = 0);
    void Resize(int cols, int rows);
    /// <summary>Writes a header/footer line directly to the log.</summary>
    void WriteMeta(string line);
}

/// <summary>Saves the terminal output as-is (escape sequences included).</summary>
sealed class RawLog : ILog
{
    readonly object sync = new object();
    readonly FileStream fs;
    readonly StreamWriter? w;       // used only when transcoding
    readonly Decoder[]? dec;
    readonly bool passthrough;

    public RawLog(string path, bool append, Encoding logEnc, Encoding srcEnc)
    {
        fs = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        passthrough = IsPlainUtf8(logEnc) && IsPlainUtf8(srcEnc);
        if (!passthrough)
        {
            w = new StreamWriter(fs, logEnc);
            dec = new[] { srcEnc.GetDecoder(), srcEnc.GetDecoder() };
        }
    }

    static bool IsPlainUtf8(Encoding e) => e.CodePage == 65001 && e.GetPreamble().Length == 0;

    public void Write(byte[] buf, int n, int channel = 0)
    {
        lock (sync)
        {
            if (passthrough) { fs.Write(buf, 0, n); fs.Flush(); return; }
            var d = dec![channel & 1];
            var chars = new char[n + 4];
            int c = d.GetChars(buf, 0, n, chars, 0, false);
            w!.Write(chars, 0, c);
            w.Flush();
        }
    }

    public void Resize(int cols, int rows) { }

    public void WriteMeta(string line)
    {
        lock (sync)
        {
            if (passthrough)
            {
                var b = new UTF8Encoding(false).GetBytes(line + "\r\n");
                fs.Write(b, 0, b.Length);
                fs.Flush();
            }
            else { w!.Write(line + "\r\n"); w.Flush(); }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (w != null) w.Dispose(); else fs.Dispose();
        }
    }
}

/// <summary>Sends everything to several logs at once (used by --raw-too).</summary>
sealed class TeeLog : ILog
{
    readonly ILog[] logs;
    public TeeLog(params ILog[] logs) { this.logs = logs; }
    public void Write(byte[] buf, int n, int channel = 0) { foreach (var l in logs) l.Write(buf, n, channel); }
    public void Resize(int cols, int rows) { foreach (var l in logs) l.Resize(cols, rows); }
    public void WriteMeta(string line) { foreach (var l in logs) l.WriteMeta(line); }
    public void Dispose() { foreach (var l in logs) l.Dispose(); }
}

/// <summary>One command-line option: short form, long form, aliases, help text and its effect.</summary>
sealed class OptSpec
{
    public readonly char Short;
    public readonly string Long;
    public readonly string[] Aliases;      // extra long names (compatibility)
    public readonly string? Arg;           // value placeholder, or null for a flag
    public readonly string Help;           // '\n' separates help lines
    public readonly Action<Options, string?> Apply;

    public OptSpec(char shortName, string longName, string[]? aliases, string? arg, string help, Action<Options, string?> apply)
    {
        Short = shortName; Long = longName; Aliases = aliases ?? new string[0];
        Arg = arg; Help = help; Apply = apply;
    }
}

sealed class Options
{
    public string? OutPath;
    public bool Append, Text, RawToo, Timestamps, NoHeader, NoFooter, ShowHelp;
    public Encoding LogEncoding = new UTF8Encoding(false);
    public Encoding SrcEncoding = new UTF8Encoding(false);
    public string[] Command = new string[0];
}

static class Program
{
    const int StartFailed = int.MinValue;

    static readonly OptSpec[] Specs =
    {
        new OptSpec('a', "--append", null, null,
            "append to the log file",
            (o, v) => o.Append = true),
        new OptSpec('e', "--encoding", null, "<name>",
            "log file encoding: utf8 (default), utf8bom, utf16, utf16be,\nsjis, or any .NET encoding name",
            (o, v) => o.LogEncoding = ParseEncoding(v!)),
        new OptSpec('h', "--help", null, null,
            "show this help",
            (o, v) => o.ShowHelp = true),
        new OptSpec('F', "--no-footer", null, null,
            "do not write the footer line",
            (o, v) => o.NoFooter = true),
        new OptSpec('H', "--no-header", null, null,
            "do not write the header lines",
            (o, v) => o.NoHeader = true),
        new OptSpec('o', "--output", null, "<file>",
            "log file (default: conlog_yyyyMMdd_HHmmss.log)",
            (o, v) => o.OutPath = v),
        new OptSpec('r', "--raw-too", null, null,
            "also write the raw log to <log file>.raw (use with --text)",
            (o, v) => o.RawToo = true),
        new OptSpec('E', "--src-encoding", null, "<name>",
            "encoding of the child's output in pipe mode: utf8 (default),\noem, ansi, sjis, ... (ignored in pseudo console mode)",
            (o, v) => o.SrcEncoding = ParseEncoding(v!)),
        new OptSpec('t', "--text", new[] { "--strip" }, null,
            "save the readable text format instead of raw",
            (o, v) => o.Text = true),
        new OptSpec('s', "--timestamp", new[] { "--ts" }, null,
            "prefix each line with a timestamp (implies --text)",
            (o, v) => o.Timestamps = true),
    };

    static void PrintUsage(TextWriter o)
    {
        o.WriteLine("Usage: conlog [options] [--] [command [args...]]");
        o.WriteLine();
        o.WriteLine("Runs the command in a pseudo console and logs everything it prints.");
        o.WriteLine("Without a command, a new shell (same kind as the parent shell) is started");
        o.WriteLine("in the current console and logged. If stdin or stdout is redirected, the");
        o.WriteLine("command runs in pipe mode (no pseudo console) and its output is logged.");
        o.WriteLine();
        o.WriteLine("Log formats:");
        o.WriteLine("  raw (default)  Saves the output exactly as the program emitted it, escape");
        o.WriteLine("                 sequences included. It is written immediately, so nothing is");
        o.WriteLine("                 lost even if conlog is killed, and it can be followed live");
        o.WriteLine("                 (e.g. Get-Content -Wait). Programs that redraw the screen");
        o.WriteLine("                 (PSReadLine, progress bars, ...) make it hard to read as text.");
        o.WriteLine("  text (--text)  Reproduces the screen with a built-in terminal emulator and");
        o.WriteLine("                 saves clean text without escape sequences. A row is written");
        o.WriteLine("                 only when it is final: when it scrolls off the screen, when");
        o.WriteLine("                 the screen is cleared, or when the session ends. Rows still");
        o.WriteLine("                 on screen are lost if conlog is killed.");
        o.WriteLine("  --raw-too      Use with --text to also keep the raw log as <log file>.raw,");
        o.WriteLine("                 a complete and immediate record next to the readable one.");
        o.WriteLine("                 (No effect without --text, since raw is already the default.)");
        o.WriteLine();
        o.WriteLine("Options (sorted by long name):");

        var sorted = new List<OptSpec>(Specs);
        sorted.Sort((x, y) => string.CompareOrdinal(x.Long, y.Long));

        var left = new List<string>();
        int width = 0;
        foreach (var s in sorted)
        {
            string l = "  -" + s.Short + ", " + s.Long + (s.Arg != null ? " " + s.Arg : "");
            left.Add(l);
            width = Math.Max(width, l.Length);
        }
        for (int i = 0; i < sorted.Count; i++)
        {
            var s = sorted[i];
            string help = s.Help;
            if (s.Aliases.Length > 0) help += " (alias: " + string.Join(", ", s.Aliases) + ")";
            string[] lines = help.Split('\n');
            o.WriteLine(left[i].PadRight(width + 2) + lines[0]);
            for (int k = 1; k < lines.Length; k++) o.WriteLine(new string(' ', width + 2) + lines[k]);
        }
        o.WriteLine();
        o.WriteLine("Short options can be bundled (-at) and take their value attached or separate");
        o.WriteLine("(-ofile.log, -o file). Long options also accept --name=value.");
    }

    static string NextValue(string[] args, ref int i, string opt)
    {
        if (i + 1 >= args.Length) throw new ArgumentException("Option " + opt + " requires a value.");
        return args[++i];
    }

    static OptSpec? FindLong(string name)
    {
        foreach (var s in Specs)
        {
            if (s.Long == name) return s;
            foreach (var alias in s.Aliases) if (alias == name) return s;
        }
        return null;
    }

    static OptSpec? FindShort(char c)
    {
        foreach (var s in Specs) if (s.Short == c) return s;
        return null;
    }

    static Encoding ParseEncoding(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "utf8": case "utf-8": return new UTF8Encoding(false);
            case "utf8bom": case "utf-8-bom": return new UTF8Encoding(true);
            case "utf16": case "utf-16": case "utf16le": return new UnicodeEncoding(false, true);
            case "utf16be": return new UnicodeEncoding(true, true);
            case "sjis": case "shift_jis": case "shift-jis": case "cp932": return Encoding.GetEncoding(932);
            case "oem": return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            case "ansi": return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            default:
                try { return Encoding.GetEncoding(name); }
                catch (ArgumentException) { throw new ArgumentException("Unknown encoding: " + name); }
        }
    }

    /// <summary>Returns null when help was requested.</summary>
    static Options? ParseArgs(string[] args)
    {
        var o = new Options();
        int i = 0;
        for (; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--") { i++; break; }
            if (a.Length < 2 || a[0] != '-') break;   // not an option: the command starts here

            if (a[1] == '-')
            {
                // Long option: --name or --name=value
                string name = a;
                string? value = null;
                int eq = a.IndexOf('=');
                if (eq > 0) { name = a.Substring(0, eq); value = a.Substring(eq + 1); }

                var spec = FindLong(name);
                if (spec == null) throw new ArgumentException("Unknown option: " + name);
                if (spec.Arg == null)
                {
                    if (value != null) throw new ArgumentException("Option " + name + " does not take a value.");
                    spec.Apply(o, null);
                }
                else
                {
                    if (value == null) value = NextValue(args, ref i, name);
                    spec.Apply(o, value);
                }
            }
            else
            {
                // Short options, possibly bundled: -a, -at, -ofile.log, -o file
                for (int k = 1; k < a.Length; k++)
                {
                    var spec = FindShort(a[k]);
                    if (spec == null) throw new ArgumentException("Unknown option: -" + a[k]);
                    if (spec.Arg == null) { spec.Apply(o, null); continue; }

                    string value = k + 1 < a.Length ? a.Substring(k + 1) : NextValue(args, ref i, "-" + a[k]);
                    spec.Apply(o, value);
                    break;   // the rest of the token was the value
                }
            }
        }
        if (o.ShowHelp) return null;

        o.Command = new string[args.Length - i];
        Array.Copy(args, i, o.Command, 0, o.Command.Length);
        return o;
    }

    static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        var sb = new StringBuilder("\"");
        int bs = 0;
        foreach (var c in a)
        {
            if (c == '\\') { bs++; continue; }
            if (c == '"') { sb.Append('\\', bs * 2 + 1).Append('"'); bs = 0; continue; }
            sb.Append('\\', bs).Append(c); bs = 0;
        }
        sb.Append('\\', bs * 2).Append('"');
        return sb.ToString();
    }

    static string JoinArgs(string[] a, int start)
    {
        var parts = new List<string>();
        for (int i = start; i < a.Length; i++) parts.Add(Quote(a[i]));
        return string.Join(" ", parts);
    }

    static Native.COORD GetSize()
    {
        try { return new Native.COORD((short)Console.WindowWidth, (short)Console.WindowHeight); }
        catch { return new Native.COORD(120, 30); }
    }

    // ---------------- Shell detection (no-command mode) ----------------

    /// <summary>Picks the same kind of shell as the parent process; falls back to %COMSPEC%.</summary>
    static string FindShell()
    {
        try
        {
            var pbi = new Native.PROCESS_BASIC_INFORMATION();
            int ret;
            int status = Native.NtQueryInformationProcess(Native.GetCurrentProcess(), 0, ref pbi,
                Marshal.SizeOf(typeof(Native.PROCESS_BASIC_INFORMATION)), out ret);
            if (status == 0)
            {
                int ppid = (int)pbi.InheritedFromUniqueProcessId.ToInt64();
                IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, ppid);
                if (h != IntPtr.Zero)
                {
                    try
                    {
                        var sb = new StringBuilder(1024);
                        int size = sb.Capacity;
                        if (Native.QueryFullProcessImageNameW(h, 0, sb, ref size))
                        {
                            string path = sb.ToString();
                            string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                            if (name == "powershell" || name == "pwsh" || name == "cmd") return path;
                        }
                    }
                    finally { Native.CloseHandle(h); }
                }
            }
        }
        catch (Exception) { }
        return Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
    }

    // ---------------- Main ----------------

    static int Main(string[] args)
    {
        Options? o;
        try { o = ParseArgs(args); }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine("conlog: " + e.Message);
            Console.Error.WriteLine("Try 'conlog --help' for more information.");
            return 2;
        }
        if (o == null) { PrintUsage(Console.Out); return 0; }

        IntPtr hIn = Native.GetStdHandle(Native.STD_INPUT_HANDLE);
        IntPtr hOut = Native.GetStdHandle(Native.STD_OUTPUT_HANDLE);
        uint inMode, outMode;
        bool inIsConsole = Native.GetConsoleMode(hIn, out inMode);
        bool outIsConsole = Native.GetConsoleMode(hOut, out outMode);
        bool interactive = inIsConsole && outIsConsole;   // otherwise: pipe mode

        string[] command = o.Command;
        bool shellMode = command.Length == 0;
        if (shellMode)
        {
            if (!interactive)
            {
                Console.Error.WriteLine("conlog: no command given, and stdin/stdout is not a console.");
                return 2;
            }
            if (Environment.GetEnvironmentVariable("CONLOG_SESSION") != null)
            {
                Console.Error.WriteLine("conlog: already inside a conlog session (CONLOG_SESSION is set).");
                return 1;
            }
            command = new[] { FindShell() };
        }

        string outPath = o.OutPath ?? $"conlog_{DateTime.Now:yyyyMMdd_HHmmss}.log";
        bool textMode = o.Text || o.Timestamps;
        var size = interactive ? GetSize() : new Native.COORD(512, 50);
        Encoding src = interactive ? new UTF8Encoding(false) : o.SrcEncoding;

        ILog log;
        string? rawPath = null;
        try
        {
            if (textMode)
            {
                var text = new ScreenLog(outPath, o.Append, o.Timestamps, size.X, size.Y, o.LogEncoding, src, !interactive);
                if (o.RawToo)
                {
                    rawPath = outPath + ".raw";
                    try { log = new TeeLog(text, new RawLog(rawPath, o.Append, o.LogEncoding, src)); }
                    catch { text.Dispose(); throw; }
                }
                else log = text;
            }
            else log = new RawLog(outPath, o.Append, o.LogEncoding, src);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            Console.Error.WriteLine("conlog: cannot open log file: " + e.Message);
            return 1;
        }

        var watch = Stopwatch.StartNew();
        string shown = shellMode ? command[0] + "  (interactive shell)" : JoinArgs(command, 0);
        if (!o.NoHeader)
        {
            log.WriteMeta("# conlog started: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            log.WriteMeta("# command: " + shown);
            log.WriteMeta("# host: " + Environment.MachineName + " | user: " + Environment.UserDomainName + "\\" + Environment.UserName);
            log.WriteMeta("# cwd: " + Environment.CurrentDirectory);
            log.WriteMeta("# mode: " + (interactive ? "pseudo console " + size.X + "x" + size.Y : "pipe"));
            log.WriteMeta("# format: " + (textMode ? "text (screen reproduced)" : "raw"));
        }

        int exitCode;
        try
        {
            exitCode = interactive
                ? RunConPty(command, log, inMode, outMode, size)
                : RunPipe(command, log, inIsConsole);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("conlog: " + e.Message);
            exitCode = StartFailed;
        }
        watch.Stop();

        if (!o.NoFooter)
        {
            var el = watch.Elapsed;
            string elapsed = string.Format("{0:00}:{1:00}:{2:00}", (int)el.TotalHours, el.Minutes, el.Seconds);
            log.WriteMeta("# conlog ended: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")
                + " | elapsed " + elapsed
                + " | " + (exitCode == StartFailed ? "failed to start" : "exit code " + exitCode));
        }
        log.Dispose();

        if (interactive)
            Console.Error.WriteLine("conlog: log saved to " + Path.GetFullPath(outPath)
                + (rawPath != null ? " (raw: " + Path.GetFullPath(rawPath) + ")" : ""));
        return exitCode == StartFailed ? 1 : exitCode;
    }

    // ---------------- Pseudo console mode ----------------

    static int RunConPty(string[] command, ILog log, uint inMode, uint outMode, Native.COORD size)
    {
        IntPtr hIn = Native.GetStdHandle(Native.STD_INPUT_HANDLE);
        IntPtr hOut = Native.GetStdHandle(Native.STD_OUTPUT_HANDLE);
        uint oldCP = Native.GetConsoleCP(), oldOutCP = Native.GetConsoleOutputCP();

        // Create the pseudo console
        SafeFileHandle inRead, inWrite, outRead, outWrite;
        Native.CreatePipe(out inRead, out inWrite, IntPtr.Zero, 0);
        Native.CreatePipe(out outRead, out outWrite, IntPtr.Zero, 0);
        IntPtr hPC;
        int hr = Native.CreatePseudoConsole(size, inRead, outWrite, 0, out hPC);
        if (hr != 0)
        {
            Console.Error.WriteLine($"conlog: CreatePseudoConsole failed (HRESULT 0x{hr:X8}). Windows 10 1809 or later is required.");
            return StartFailed;
        }
        inRead.Dispose();   // The pseudo console owns these ends now; close our copies
        outWrite.Dispose();

        // Start the child process
        var si = new Native.STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
        IntPtr attrSize = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
        si.lpAttributeList = Marshal.AllocHGlobal(attrSize);
        Native.InitializeProcThreadAttributeList(si.lpAttributeList, 1, 0, ref attrSize);
        Native.UpdateProcThreadAttribute(si.lpAttributeList, 0, Native.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
            hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero);

        string cmdLine = JoinArgs(command, 0);
        Environment.SetEnvironmentVariable("CONLOG_SESSION", "1");   // inherited by the child

        Native.PROCESS_INFORMATION pi;
        if (!Native.CreateProcessW(null, new StringBuilder(cmdLine), IntPtr.Zero, IntPtr.Zero, false,
                Native.EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, null, ref si, out pi))
        {
            Console.Error.WriteLine($"conlog: cannot start command: {cmdLine} (Win32 error {Marshal.GetLastWin32Error()})");
            Native.ClosePseudoConsole(hPC);
            inWrite.Dispose();
            outRead.Dispose();
            Native.DeleteProcThreadAttributeList(si.lpAttributeList);
            Marshal.FreeHGlobal(si.lpAttributeList);
            return StartFailed;
        }
        Native.CloseHandle(pi.hThread);

        // Switch our own console to raw VT mode
        Native.SetConsoleCP(65001);
        Native.SetConsoleOutputCP(65001);
        Native.SetConsoleMode(hIn, (inMode & ~(Native.ENABLE_ECHO_INPUT | Native.ENABLE_LINE_INPUT | Native.ENABLE_PROCESSED_INPUT))
                                   | Native.ENABLE_VIRTUAL_TERMINAL_INPUT);
        Native.SetConsoleMode(hOut, outMode | Native.ENABLE_PROCESSED_OUTPUT | Native.ENABLE_VIRTUAL_TERMINAL_PROCESSING);

        bool exited = false;
        var ptyIn = new FileStream(inWrite, FileAccess.Write, 4096, false);
        var ptyOut = new FileStream(outRead, FileAccess.Read, 4096, false);

        // Keyboard input -> child process
        var tIn = new Thread(() =>
        {
            try
            {
                using var stdin = Console.OpenStandardInput();
                var buf = new byte[1024];
                int n;
                while ((n = stdin.Read(buf, 0, buf.Length)) > 0)
                {
                    ptyIn.Write(buf, 0, n);
                    ptyIn.Flush();
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        })
        { IsBackground = true };
        tIn.Start();

        // Child process output -> screen + log
        var tOut = new Thread(() =>
        {
            try
            {
                using var stdout = Console.OpenStandardOutput();
                var buf = new byte[8192];
                int n;
                while ((n = ptyOut.Read(buf, 0, buf.Length)) > 0)
                {
                    stdout.Write(buf, 0, n);
                    stdout.Flush();
                    log.Write(buf, n);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        });
        tOut.Start();

        // Follow window size changes
        var tSize = new Thread(() =>
        {
            var last = GetSize();
            while (!exited)
            {
                Thread.Sleep(200);
                var cur = GetSize();
                if (cur.X != last.X || cur.Y != last.Y)
                {
                    Native.ResizePseudoConsole(hPC, cur);
                    log.Resize(cur.X, cur.Y);
                    last = cur;
                }
            }
        })
        { IsBackground = true };
        tSize.Start();

        // Wait for the child to exit
        Native.WaitForSingleObject(pi.hProcess, Native.INFINITE);
        uint exitCode;
        Native.GetExitCodeProcess(pi.hProcess, out exitCode);
        exited = true;

        // Close the pseudo console and drain remaining output
        Task.Run(() => Native.ClosePseudoConsole(hPC));
        tOut.Join(3000);

        Native.CloseHandle(pi.hProcess);
        Native.DeleteProcThreadAttributeList(si.lpAttributeList);
        Marshal.FreeHGlobal(si.lpAttributeList);

        // Restore console settings
        Native.SetConsoleMode(hIn, inMode);
        Native.SetConsoleMode(hOut, outMode);
        Native.SetConsoleCP(oldCP);
        Native.SetConsoleOutputCP(oldOutCP);

        return (int)exitCode;
    }

    // ---------------- Pipe mode (stdin or stdout redirected) ----------------

    static int RunPipe(string[] command, ILog log, bool stdinIsConsole)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command[0],
            Arguments = JoinArgs(command, 1),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = !stdinIsConsole,   // a console stdin is inherited by the child
        };
        psi.EnvironmentVariables["CONLOG_SESSION"] = "1";

        // Keep running on Ctrl+C so that the footer is written; the child gets Ctrl+C itself.
        try { Console.CancelKeyPress += (s, e) => { e.Cancel = true; }; } catch (Exception) { }

        Process p;
        try { p = Process.Start(psi)!; }
        catch (Exception e)
        {
            Console.Error.WriteLine($"conlog: cannot start command: {command[0]} ({e.Message})");
            return StartFailed;
        }

        var tOut = Pump(p.StandardOutput.BaseStream, Console.OpenStandardOutput(), log, 0);
        var tErr = Pump(p.StandardError.BaseStream, Console.OpenStandardError(), log, 1);

        if (!stdinIsConsole)
        {
            // Our redirected stdin -> child's stdin (EOF is propagated)
            new Thread(() =>
            {
                try
                {
                    using (var stdin = Console.OpenStandardInput())
                    {
                        var buf = new byte[8192];
                        int n;
                        var dst = p.StandardInput.BaseStream;
                        while ((n = stdin.Read(buf, 0, buf.Length)) > 0)
                        {
                            dst.Write(buf, 0, n);
                            dst.Flush();
                        }
                    }
                    p.StandardInput.Close();
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            })
            { IsBackground = true }.Start();
        }

        p.WaitForExit();
        tOut.Join();
        tErr.Join();
        return p.ExitCode;
    }

    /// <summary>Copies a child's output stream to our own stream and to the log.</summary>
    static Thread Pump(Stream from, Stream to, ILog log, int channel)
    {
        var t = new Thread(() =>
        {
            var buf = new byte[8192];
            bool forward = true;
            try
            {
                int n;
                while ((n = from.Read(buf, 0, buf.Length)) > 0)
                {
                    if (forward)
                    {
                        try { to.Write(buf, 0, n); to.Flush(); }
                        catch (IOException) { forward = false; }   // downstream closed; keep logging
                    }
                    log.Write(buf, n, channel);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        });
        t.Start();
        return t;
    }
}