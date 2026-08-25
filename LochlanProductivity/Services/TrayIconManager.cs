using System;
using System.Runtime.InteropServices;

namespace LochlanProductivity.Services
{
    // ============================================================
    // TRAY ICON
    //
    // Minimal Shell_NotifyIcon implementation (no NuGet packages).
    // Gives users a way back into the app after the window is
    // hidden by close-to-tray, plus a guarded Exit.
    //
    // The native message window lives on the UI thread; its posted
    // messages are dispatched by the XAML message pump, so event
    // handlers run on the UI thread. One instance per process.
    // ============================================================

    public sealed class TrayIconManager : IDisposable
    {
        private const uint WM_APP_TRAY_CALLBACK = 0x8000; // WM_APP

        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_MODIFY = 0x00000001;
        private const uint NIM_DELETE = 0x00000002;

        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;

        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONUP = 0x0205;

        private const int MENU_OPEN_ID = 1;
        private const int MENU_EXIT_ID = 2;

        private const uint MF_STRING = 0x00000000;

        private const int TPM_RIGHTBUTTON = 0x0002;
        private const int TPM_BOTTOMALIGN = 0x0020;
        private const int TPM_RETURNCMD = 0x0100;

        private delegate IntPtr WndProcDelegate(
            IntPtr hWnd,
            uint msg,
            IntPtr wParam,
            IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATAW
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;

            public uint dwState;
            public uint dwStateMask;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;

            public uint uVersion;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;

            public uint dwInfoFlags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string lpszMenuName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string lpszClassName;

            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Shell_NotifyIconW(
            uint dwMessage,
            ref NOTIFYICONDATAW lpData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern short UnregisterClassW(
            string lpClassName,
            IntPtr hInstance);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(
            IntPtr hWnd,
            uint msg,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll", EntryPoint = "PrivateExtractIconsW", CharSet = CharSet.Unicode)]
        private static extern uint PrivateExtractIconsW(
            string szFileName,
            int nIconIndex,
            int cxIcon,
            int cyIcon,
            IntPtr[]? phicon,
            uint[]? piconid,
            uint nIcons,
            uint flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenuW(
            IntPtr hMenu,
            uint uFlags,
            uint uIDNewItem,
            string lpNewItem);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenuEx(
            IntPtr hMenu,
            uint fuFlags,
            int x,
            int y,
            IntPtr hwnd,
            IntPtr lptpm);

        [DllImport("user32.dll")]
        private static extern bool PostMessageW(
            IntPtr hWnd,
            uint msg,
            IntPtr wParam,
            IntPtr lParam);

        // Single-instance routing for the static wndproc.
        private static TrayIconManager? activeInstance;

        private readonly string className =
            "LochlanProductivityTrayWnd_" + Guid.NewGuid().ToString("N");

        // Kept as a field so the delegate is never collected while
        // the native window class points at it.
        private readonly WndProcDelegate wndProcThunkDelegate;

        private IntPtr hwnd;
        private IntPtr hIcon = IntPtr.Zero;
        private bool ownsIcon;
        private bool iconAdded;
        private bool classRegistered;
        private bool disposed;

        private string tipText = "Lochlan Productivity";

        public Action? OpenRequested { get; set; }

        public Action? ExitRequested { get; set; }

        public TrayIconManager()
        {
            wndProcThunkDelegate = WndProcThunk;

            activeInstance = this;
        }

        // ============================================================
        // PUBLIC API
        // ============================================================

        public void Show(string tip)
        {
            if (disposed)
                return;

            if (!string.IsNullOrWhiteSpace(tip))
                tipText = tip!;

            EnsureWindow();

            if (hwnd == IntPtr.Zero)
                return;

            EnsureIcon();

            NOTIFYICONDATAW data = CreateNotifyData();

            if (!iconAdded)
            {
                iconAdded =
                    Shell_NotifyIconW(NIM_ADD, ref data);
            }
            else
            {
                Shell_NotifyIconW(NIM_MODIFY, ref data);
            }
        }

        public void Hide()
        {
            if (iconAdded && hwnd != IntPtr.Zero)
            {
                NOTIFYICONDATAW data = CreateNotifyData();

                Shell_NotifyIconW(NIM_DELETE, ref data);

                iconAdded = false;
            }
        }

        // ============================================================
        // NATIVE WINDOW
        // ============================================================

        private void EnsureWindow()
        {
            if (hwnd != IntPtr.Zero)
                return;

            IntPtr hInstance = GetModuleHandleW(null);

            WNDCLASSEXW windowClass = new()
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),

                style = 0,

                lpfnWndProc =
                    Marshal.GetFunctionPointerForDelegate(
                        wndProcThunkDelegate),

                hInstance = hInstance,

                lpszClassName = className
            };

            ushort atom = RegisterClassExW(ref windowClass);

            if (atom == 0)
            {
                int error = Marshal.GetLastWin32Error();

                // 1407 = ERROR_CLASS_ALREADY_EXISTS
                if (error != 1407)
                    return;
            }

            classRegistered = true;

            hwnd = CreateWindowExW(
                0,
                className,
                "",
                0,
                0, 0, 0, 0,
                IntPtr.Zero,
                IntPtr.Zero,
                hInstance,
                IntPtr.Zero);
        }

        private void EnsureIcon()
        {
            if (hIcon != IntPtr.Zero)
                return;

            try
            {
                string? executablePath =
                    Environment.ProcessPath;

                if (!string.IsNullOrWhiteSpace(executablePath))
                {
                    IntPtr[] icons = new IntPtr[1];

                    uint extracted =
                        PrivateExtractIconsW(
                            executablePath,
                            0,
                            32,
                            32,
                            icons,
                            null,
                            1,
                            0);

                    if (extracted > 0 && icons[0] != IntPtr.Zero)
                    {
                        hIcon = icons[0];
                        ownsIcon = true;
                        return;
                    }
                }
            }
            catch
            {
            }

            hIcon = LoadIconW(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
            ownsIcon = false;
        }

        private NOTIFYICONDATAW CreateNotifyData()
        {
            return new NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),

                hWnd = hwnd,

                uID = 1,

                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,

                uCallbackMessage = WM_APP_TRAY_CALLBACK,

                hIcon = hIcon,

                szTip = tipText ?? "",

                szInfo = "",

                szInfoTitle = ""
            };
        }

        // ============================================================
        // MESSAGE HANDLING
        // ============================================================

        private static IntPtr WndProcThunk(
            IntPtr hWnd,
            uint msg,
            IntPtr wParam,
            IntPtr lParam)
        {
            TrayIconManager? instance = activeInstance;

            if (instance != null &&
                msg == WM_APP_TRAY_CALLBACK)
            {
                instance.OnTrayCallback(
                    (int)lParam.ToInt64());

                return IntPtr.Zero;
            }

            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        private void OnTrayCallback(int mouseMessage)
        {
            switch (mouseMessage)
            {
                case WM_LBUTTONUP:

                    OpenRequested?.Invoke();
                    break;

                case WM_RBUTTONUP:

                    ShowContextMenu();
                    break;
            }
        }

        private void ShowContextMenu()
        {
            IntPtr menu = CreatePopupMenu();

            if (menu == IntPtr.Zero)
                return;

            AppendMenuW(menu, MF_STRING, MENU_OPEN_ID, "Open");
            AppendMenuW(menu, MF_STRING, MENU_EXIT_ID, "Exit");

            GetCursorPos(out POINT cursor);

            // Required so the menu closes when clicking elsewhere.
            SetForegroundWindow(hwnd);

            int selectedCommand =
                TrackPopupMenuEx(
                    menu,
                    TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN,
                    cursor.X,
                    cursor.Y,
                    hwnd,
                    IntPtr.Zero);

            PostMessageW(hwnd, 0x0000 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero);

            DestroyMenu(menu);

            if (selectedCommand == MENU_OPEN_ID)
            {
                OpenRequested?.Invoke();
            }
            else if (selectedCommand == MENU_EXIT_ID)
            {
                ExitRequested?.Invoke();
            }
        }

        // ============================================================
        // DISPOSE
        // ============================================================

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;

            Hide();

            if (hIcon != IntPtr.Zero && ownsIcon)
            {
                DestroyIcon(hIcon);
            }

            hIcon = IntPtr.Zero;

            if (hwnd != IntPtr.Zero)
            {
                DestroyWindow(hwnd);
                hwnd = IntPtr.Zero;
            }

            if (classRegistered)
            {
                UnregisterClassW(className, GetModuleHandleW(null));
                classRegistered = false;
            }

            if (activeInstance == this)
            {
                activeInstance = null;
            }
        }
    }
}
