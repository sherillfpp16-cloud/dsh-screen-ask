using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Starts ScreenSnap.exe with a hidden console (STARTF_USESHOWWINDOW + SW_HIDE),
// so the helper keeps its console (required for low-level input hooks) without
// ever showing a window.
class Bootstrap
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    private const uint CREATE_NEW_CONSOLE = 0x00000010;
    private const int STARTF_USESHOWWINDOW = 0x00000001;
    private const short SW_HIDE = 0;

    static void Main(string[] args)
    {
        string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        if (args.Length == 0)
        {
            try
            {
                string cfg = Path.Combine(dir, "bootstrap.args");
                if (File.Exists(cfg)) args = File.ReadAllText(cfg).Trim().Split(new[] { " " }, StringSplitOptions.RemoveEmptyEntries);
            }
            catch { }
        }
        string exe = Path.Combine(dir, "ScreenSnap.exe");
        try
        {
            try { File.AppendAllText(Path.Combine(dir, "bootstrap.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " args=" + string.Join(" ", args) + "\r\n"); } catch { }
            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            si.dwFlags = STARTF_USESHOWWINDOW;
            si.wShowWindow = SW_HIDE;
            var pi = new PROCESS_INFORMATION();
            string commandLine = "\"" + exe + "\" " + string.Join(" ", args);
            bool ok = CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                CREATE_NEW_CONSOLE, IntPtr.Zero, dir, ref si, out pi);
            if (ok)
            {
                try { CloseHandle(pi.hProcess); CloseHandle(pi.hThread); } catch { }
            }
        }
        catch { }
    }

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}


