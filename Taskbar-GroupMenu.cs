using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    private static readonly string AppRoot = Application.StartupPath;
    private static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Taskbar-Groups");
    private static readonly string GroupsRoot = Path.Combine(DataRoot, "Groups");
    private static readonly string PinRoot = Path.Combine(DataRoot, "Pin to Taskbar");
    private static readonly string TemplatesRoot = Path.Combine(AppRoot, "_templates");
    private static readonly string IconRoot = Path.Combine(AppRoot, "Icon Library");
    private static readonly string ExePath = Path.Combine(AppRoot, "Taskbar-GroupMenu.exe");
    private static readonly string IconCacheRoot = Path.Combine(DataRoot, "Cache", "MenuIcons");
    private static readonly Dictionary<string, Bitmap> MemoryIconCache =
        new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
    private static readonly Stopwatch PerfClock = Stopwatch.StartNew();
    private static readonly bool PerfEnabled =
        String.Equals(Environment.GetEnvironmentVariable("TASKBAR_GROUPS_PERF"), "1", StringComparison.OrdinalIgnoreCase);
    private static readonly string SessionLogPath = Path.Combine(
        DataRoot,
        "TaskbarGroups_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
    private static readonly object SessionLogLock = new object();
    private static bool SessionLogActive;
    private static int PerfIconCacheHits;
    private static int PerfIconCacheMisses;
    private static bool PerfFirstMenuPaintLogged;
    private static readonly Queue<ToolStripItem> DeferredMenuDisposals =
        new Queue<ToolStripItem>();
    private static bool DeferredDisposalScheduled;
    private sealed class CachedGroupMenu
    {
        public string Signature;
        public ToolStripItem[] Items;
    }

    private static readonly Dictionary<string, CachedGroupMenu> GroupMenuCache =
        new Dictionary<string, CachedGroupMenu>(StringComparer.OrdinalIgnoreCase);
    private static string AttachedCachedGroup;
    private static bool PrewarmMode;
    private static bool PrewarmStarted;
    private static Stopwatch PerfMenuClock;

    private static string Group;
    private static string GroupFolder;
    private static ContextMenuStrip MainMenu;
    private static Form HostForm;
    private static Point MenuPoint;
    private static Point ExactRestorePoint;
    private static bool UseExactRestorePoint;
    private static Point LastMainMenuLocation;
    private static bool HaveLastMainMenuLocation;
    private static ToolStripItem DragCandidate;
    private static Point DragStartScreen;
    private static bool ReorderDragging;
    private static int DragInsertionIndex = -1;
    private static bool SuppressNextLaunch;
    private static Mutex HostMutex;
    private static Thread PipeServerThread;
    private static volatile bool HostShuttingDown;
    private static readonly string HostInstanceSuffix =
        Process.GetCurrentProcess().SessionId + "-" + GetCurrentUserSidForIpcName();
    private static readonly string HostMutexName =
        @"Local\Taskbar-Groups-MenuHost-" + HostInstanceSuffix;
    private static readonly string HostPipeName =
        "Taskbar-Groups-MenuHost-" + HostInstanceSuffix;

    private static IntPtr MouseHookHandle = IntPtr.Zero;
    private static LowLevelMouseProc MouseHookCallback;
    private static readonly object LastMouseClickLock = new object();
    private static Point LastMouseClickPoint = Point.Empty;
    private static long LastMouseClickTick;

    private static readonly string[] AllowedExtensions =
    {
        ".lnk", ".url", ".exe", ".bat", ".cmd", ".ps1", ".separator", ".heading"
    };

    private static void InitializeWinForms()
    {
        // SetCompatibleTextRenderingDefault must run before the first WinForms
        // window/handle is created in this process.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        PerfLog("WinForms initialized");
    }

    private static Point GetNativeCursorPosition()
    {
        POINT point;
        if (GetCursorPos(out point))
            return new Point(point.x, point.y);

        return Point.Empty;
    }

    private static string GetCurrentUserSidForIpcName()
    {
        try
        {
            SecurityIdentifier sid = WindowsIdentity.GetCurrent().User;
            return sid == null ? "unknown" : sid.Value.Replace("-", "_");
        }
        catch
        {
            return "unknown";
        }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        // Native Shell property sheets get their own process.
        if (args.Length >= 2 &&
            String.Equals(args[0], "--link-properties-worker", StringComparison.OrdinalIgnoreCase))
        {
            InitializeWinForms();
            RunLinkPropertiesWorker(args[1]);
            return;
        }

        bool hostOnly =
            args.Length == 1 &&
            String.Equals(args[0], "--resident-host", StringComparison.OrdinalIgnoreCase);

        if (!hostOnly && (args.Length < 1 || String.IsNullOrWhiteSpace(args[0])))
        {
            InitializeWinForms();
            MessageBox.Show("A group name is required.", "Taskbar Groups",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(GroupsRoot);
        Directory.CreateDirectory(PinRoot);
        Directory.CreateDirectory(IconCacheRoot);
        PerfLog("Data directories ready");
        PerfLog("IPC identity: session=" + Process.GetCurrentProcess().SessionId +
            "; mutex=" + HostMutexName + "; pipe=" + HostPipeName);

        string requestedGroup = hostOnly ? null : args[0];
        Point requestedMenuPoint = hostOnly ? Point.Empty : GetNativeCursorPosition();

        // IPC is the authoritative resident-host health check. A named mutex only
        // tells us that some process created/owns that kernel object; it does not
        // prove that the menu host is alive, initialized, or listening.
        if (hostOnly)
        {
            if (PingResidentHost())
            {
                PerfLog("Resident host already running and responsive; host-only invocation exiting");
                return;
            }
        }
        else
        {
            if (SendGroupToResidentHost(requestedGroup, requestedMenuPoint))
            {
                PerfLog("Forwarded group to resident host: " + requestedGroup);
                return;
            }
        }

        bool createdNew;
        HostMutex = new Mutex(true, HostMutexName, out createdNew);

        if (!createdNew)
        {
            // Another process may be in the short interval between taking the
            // mutex and opening its pipe. Retry the pipe, not the mutex.
            for (int attempt = 0; attempt < 40; attempt++)
            {
                Thread.Sleep(50);

                if (hostOnly)
                {
                    if (PingResidentHost())
                    {
                        PerfLog("Resident host became responsive after retry");
                        HostMutex.Dispose();
                        HostMutex = null;
                        return;
                    }
                }
                else if (SendGroupToResidentHost(requestedGroup, requestedMenuPoint))
                {
                    PerfLog("Forwarded group to resident host after retry: " + requestedGroup);
                    HostMutex.Dispose();
                    HostMutex = null;
                    return;
                }
            }

            PerfLog("Resident mutex exists but pipe remained unavailable for 2 seconds");
            InitializeWinForms();
            MessageBox.Show(
                "Taskbar Groups found an existing resident-host mutex, but no IPC server " +
                "became available within 2 seconds. Check the performance log for the " +
                "resident process and pipe name.",
                "Taskbar Groups",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            HostMutex.Dispose();
            HostMutex = null;
            return;
        }

        try
        {
            // This process won the resident-host election. It owns one general
            // session log until the process exits, regardless of PERF settings.
            StartSessionLog();
            InstallUnhandledExceptionLogging();
            LogInfo("Initializing WinForms.");
            InitializeWinForms();
            LogInfo("Initializing persistent menu host.");
            InitializePersistentHost();
            LogInfo("Starting taskbar mouse click tracker.");
            StartMouseClickTracker();
            LogInfo("Starting IPC pipe server.");
            StartPipeServer();

            HostForm.Shown += delegate
            {
                HostForm.BeginInvoke((MethodInvoker)delegate
                {
                    if (hostOnly)
                    {
                        PerfLog("Resident host started in prewarm mode");
                        PrewarmAllGroupMenus();
                    }
                    else
                    {
                        ShowRequestedGroup(requestedGroup, false, requestedMenuPoint);
                    }
                });
            };

            LogInfo("Resident host startup complete; entering application message loop.");
            PerfLog("Persistent host Application.Run starting");
            Application.Run(HostForm);
            PerfLog("Persistent host Application.Run returned");
            LogInfo("Application message loop returned; resident host is shutting down.");
        }
        catch (Exception ex)
        {
            LogError("Fatal resident-host exception.", ex);
            throw;
        }
        finally
        {
            LogInfo("Resident host cleanup starting.");
            HostShuttingDown = true;
            StopMouseClickTracker();

            foreach (KeyValuePair<string, CachedGroupMenu> pair in GroupMenuCache)
            {
                if (String.Equals(pair.Key, AttachedCachedGroup, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (pair.Value == null || pair.Value.Items == null)
                    continue;
                foreach (ToolStripItem item in pair.Value.Items)
                {
                    try { item.Dispose(); } catch { }
                }
            }
            GroupMenuCache.Clear();
            AttachedCachedGroup = null;

            while (DeferredMenuDisposals.Count > 0)
            {
                try { DeferredMenuDisposals.Dequeue().Dispose(); }
                catch { }
            }

            if (MainMenu != null)
            {
                MainMenu.Dispose();
                MainMenu = null;
            }

            if (HostForm != null)
            {
                HostForm.Dispose();
                HostForm = null;
            }

            if (HostMutex != null)
            {
                try { HostMutex.ReleaseMutex(); }
                catch { }
                HostMutex.Dispose();
                HostMutex = null;
            }
        }
    }

    private static void InitializePersistentHost()
    {
        HostForm = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Size = new Size(1, 1),
            Location = new Point(-32000, -32000),
            ShowIcon = false,
            TopMost = false
        };
        PerfLog("Persistent HostForm constructed");

        MainMenu = new ContextMenuStrip
        {
            ShowImageMargin = true,
            ShowCheckMargin = false
        };
        PerfLog("Persistent ContextMenuStrip constructed");

        ApplyTheme(MainMenu);
        MainMenu.Paint += MainMenuPaint;
        MainMenu.MouseMove += ReorderMouseMove;
        MainMenu.MouseUp += ReorderMouseUp;
        MainMenu.Opening += delegate { PerfLog("ContextMenuStrip Opening event"); };
        MainMenu.Opened += delegate { PerfLog("ContextMenuStrip Opened event"); };
        MainMenu.Paint += delegate { PerfLogOncePerMenuPaint(); };

        // Create the host HWND once. Subsequent menu invocations reuse it.
        HostForm.Show();
        PerfLog("Persistent HostForm shown");
    }

    private static void StartMouseClickTracker()
    {
        MouseHookCallback = MouseHookProc;

        using (Process process = Process.GetCurrentProcess())
        using (ProcessModule module = process.MainModule)
        {
            IntPtr moduleHandle = GetModuleHandle(module.ModuleName);
            MouseHookHandle = SetWindowsHookEx(
                WH_MOUSE_LL,
                MouseHookCallback,
                moduleHandle,
                0);
        }

        if (MouseHookHandle == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            PerfLog("Mouse click tracker failed; Win32=" + error);
        }
        else
        {
            PerfLog("Mouse click tracker started");
        }
    }

    private static void StopMouseClickTracker()
    {
        if (MouseHookHandle == IntPtr.Zero)
            return;

        UnhookWindowsHookEx(MouseHookHandle);
        MouseHookHandle = IntPtr.Zero;
        MouseHookCallback = null;
    }

    private static IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() == WM_LBUTTONDOWN)
        {
            MSLLHOOKSTRUCT data =
                (MSLLHOOKSTRUCT)Marshal.PtrToStructure(
                    lParam,
                    typeof(MSLLHOOKSTRUCT));

            lock (LastMouseClickLock)
            {
                LastMouseClickPoint = new Point(data.pt.x, data.pt.y);
                LastMouseClickTick = Environment.TickCount;
            }
        }

        return CallNextHookEx(MouseHookHandle, nCode, wParam, lParam);
    }

    private static bool TryGetRecentMouseClick(out Point point)
    {
        lock (LastMouseClickLock)
        {
            // IPC startup should normally arrive within a few hundred
            // milliseconds of the taskbar click. A two-second window allows
            // for a busy machine while preventing an unrelated old click from
            // positioning a later menu request.
            uint age = unchecked(
                (uint)(Environment.TickCount - LastMouseClickTick));

            if (LastMouseClickTick != 0 && age <= 2000)
            {
                point = LastMouseClickPoint;
                return true;
            }
        }

        point = Point.Empty;
        return false;
    }

    private static void StartPipeServer()
    {
        PipeServerThread = new Thread(delegate()
        {
            while (!HostShuttingDown)
            {
                try
                {
                    PipeSecurity pipeSecurity = new PipeSecurity();
                    SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User;
                    pipeSecurity.SetAccessRuleProtection(true, false);
                    pipeSecurity.AddAccessRule(new PipeAccessRule(
                        currentUser,
                        PipeAccessRights.FullControl,
                        AccessControlType.Allow));

                    using (var server = new NamedPipeServerStream(
                        HostPipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.None,
                        0,
                        0,
                        pipeSecurity))
                    {
                        server.WaitForConnection();

                        using (var reader = new StreamReader(server, Encoding.UTF8))
                        {
                            string requestedGroup = reader.ReadLine();
                            string xText = reader.ReadLine();
                            string yText = reader.ReadLine();

                            if (String.IsNullOrWhiteSpace(requestedGroup))
                                continue;

                            if (String.Equals(requestedGroup, "__PING__", StringComparison.Ordinal))
                            {
                                PerfLog("Resident host IPC ping received");
                                continue;
                            }

                            int requestedX;
                            int requestedY;
                            Point requestedPoint = Cursor.Position;
                            if (Int32.TryParse(xText, out requestedX) &&
                                Int32.TryParse(yText, out requestedY))
                            {
                                requestedPoint = new Point(requestedX, requestedY);
                            }

                            // The launcher does not start until after Windows has
                            // processed the taskbar click, so even its earliest
                            // Cursor.Position sample can be too late if the mouse
                            // is moved immediately. The resident process observes
                            // the actual global left-button-down instead.
                            Point actualClickPoint;
                            if (TryGetRecentMouseClick(out actualClickPoint))
                                requestedPoint = actualClickPoint;

                            Form host = HostForm;
                            if (host == null || host.IsDisposed)
                                continue;

                            PerfLog("IPC group request received: " + requestedGroup);
                            host.BeginInvoke((MethodInvoker)delegate
                            {
                                ShowRequestedGroup(requestedGroup, false, requestedPoint);
                            });
                        }
                    }
                }
                catch (IOException)
                {
                    if (!HostShuttingDown)
                        Thread.Sleep(25);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    PerfLog("Pipe server error: " + ex.Message);
                    if (!HostShuttingDown)
                        Thread.Sleep(100);
                }
            }
        });

        PipeServerThread.IsBackground = true;
        PipeServerThread.Name = "Taskbar Groups IPC";
        PipeServerThread.Start();
        PerfLog("Persistent host pipe server started: " + HostPipeName);
    }

    private static bool SendGroupToResidentHost(string requestedGroup, Point requestedPoint)
    {
        try
        {
            using (var client = new NamedPipeClientStream(
                ".",
                HostPipeName,
                PipeDirection.Out,
                PipeOptions.None))
            {
                client.Connect(150);

                using (var writer = new StreamWriter(client, Encoding.UTF8))
                {
                    writer.AutoFlush = true;
                    writer.WriteLine(requestedGroup);
                    writer.WriteLine(requestedPoint.X);
                    writer.WriteLine(requestedPoint.Y);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            PerfLog("IPC send failed on pipe '" + HostPipeName + "': " +
                ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    private static bool PingResidentHost()
    {
        try
        {
            using (var client = new NamedPipeClientStream(
                ".",
                HostPipeName,
                PipeDirection.Out,
                PipeOptions.None))
            {
                client.Connect(250);

                using (var writer = new StreamWriter(client, Encoding.UTF8))
                {
                    writer.AutoFlush = true;
                    writer.WriteLine("__PING__");
                    writer.WriteLine("0");
                    writer.WriteLine("0");
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            PerfLog("IPC ping failed on pipe '" + HostPipeName + "': " +
                ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    private static Queue<string> PrewarmQueue;
    private static Stopwatch PrewarmClock;

    private static void PrewarmAllGroupMenus()
    {
        if (PrewarmStarted)
            return;

        PrewarmStarted = true;
        PrewarmClock = Stopwatch.StartNew();
        PrewarmQueue = new Queue<string>(
            Directory.GetDirectories(GroupsRoot)
                .Select(path => Path.GetFileName(path))
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase));

        PerfLog("Menu prewarm queued; groups=" + PrewarmQueue.Count);
        QueueNextPrewarmGroup();
    }

    private static void QueueNextPrewarmGroup()
    {
        if (HostShuttingDown || HostForm == null || HostForm.IsDisposed)
            return;

        HostForm.BeginInvoke((MethodInvoker)delegate
        {
            if (HostShuttingDown)
                return;

            // A real user request always wins over background warmup.
            if (MainMenu != null && MainMenu.Visible)
            {
                HostForm.BeginInvoke((MethodInvoker)delegate { QueueNextPrewarmGroup(); });
                return;
            }

            if (PrewarmQueue == null || PrewarmQueue.Count == 0)
            {
                PrewarmMode = false;
                PerfLog("Menu prewarm completed; cached groups=" +
                    GroupMenuCache.Count + " in " +
                    (PrewarmClock == null ? "0.0" :
                        PrewarmClock.Elapsed.TotalMilliseconds.ToString("0.0")) + " ms");
                return;
            }

            string groupName = PrewarmQueue.Dequeue();
            PerfLog("Prewarming group: " + groupName);

            try
            {
                PrewarmMode = true;
                ShowRequestedGroup(groupName, false, Point.Empty);

                if (MainMenu.Items.Count > 0)
                    MainMenu.Items.Clear();

                AttachedCachedGroup = null;
            }
            catch (Exception ex)
            {
                PerfLog("Prewarm failed for '" + groupName + "': " +
                    ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                PrewarmMode = false;
            }

            // Yield back to the message pump before building the next group.
            QueueNextPrewarmGroup();
        });
    }

    private static void ShowRequestedGroup(
        string requestedGroup,
        bool preserveLocation,
        Point requestedPoint)
    {
        string requestedFolder = Path.Combine(GroupsRoot, requestedGroup);
        if (!Directory.Exists(requestedFolder))
        {
            MessageBox.Show("Taskbar group folder does not exist:\r\n" + requestedFolder,
                "Taskbar Groups", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (MainMenu != null && MainMenu.Visible)
        {
            string deferredGroup = requestedGroup;
            bool deferredPreserveLocation = preserveLocation;
            Point deferredRequestedPoint = requestedPoint;

            ToolStripDropDownClosedEventHandler closedHandler = null;
            closedHandler = delegate
            {
                MainMenu.Closed -= closedHandler;

                HostForm.BeginInvoke((MethodInvoker)delegate
                {
                    ShowRequestedGroup(
                        deferredGroup,
                        deferredPreserveLocation,
                        deferredRequestedPoint);
                });
            };

            MainMenu.Closed += closedHandler;
            MainMenu.Close(ToolStripDropDownCloseReason.AppClicked);
            return;
        }

        Group = requestedGroup;
        GroupFolder = requestedFolder;
        if (!preserveLocation)
        {
            UseExactRestorePoint = false;
            Point taskbarAnchor;
            if (TryGetTaskbarButtonAnchor(requestedGroup, out taskbarAnchor))
            {
                MenuPoint = taskbarAnchor;
                PerfLog("Taskbar button anchor found for '" + requestedGroup +
                    "': " + taskbarAnchor.X + "," + taskbarAnchor.Y);
            }
            else
            {
                // Fallback only. Normally the popup is positioned from the
                // actual Windows taskbar button rectangle, not the mouse.
                MenuPoint = requestedPoint;
                PerfLog("Taskbar button anchor not found for '" + requestedGroup +
                    "'; using launch point " + requestedPoint.X + "," + requestedPoint.Y);
            }
        }

        ShowGroupMenu();
    }

    private static bool TryGetTaskbarButtonAnchor(string requestedGroup, out Point anchor)
    {
        anchor = Point.Empty;

        Point clickPoint;
        if (!TryGetRecentMouseClick(out clickPoint))
            return false;

        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero)
            return false;

        RECT taskbarRect;
        if (!GetWindowRect(taskbar, out taskbarRect))
            return false;

        // Only accept the tracked mouse-down if it actually occurred inside
        // the taskbar. This is the click that launched the shortcut, captured
        // by the resident process before the launcher even starts.
        if (clickPoint.X < taskbarRect.Left || clickPoint.X >= taskbarRect.Right ||
            clickPoint.Y < taskbarRect.Top || clickPoint.Y >= taskbarRect.Bottom)
            return false;

        Rectangle workingArea = Screen.FromPoint(clickPoint).WorkingArea;

        if (taskbarRect.Top >= workingArea.Bottom)
        {
            // Bottom taskbar.
            anchor = new Point(clickPoint.X, taskbarRect.Top);
        }
        else if (taskbarRect.Bottom <= workingArea.Top)
        {
            // Top taskbar.
            anchor = new Point(clickPoint.X, taskbarRect.Bottom);
        }
        else if (taskbarRect.Left >= workingArea.Right)
        {
            // Right taskbar.
            anchor = new Point(taskbarRect.Left, clickPoint.Y);
        }
        else
        {
            // Left taskbar.
            anchor = new Point(taskbarRect.Right, clickPoint.Y);
        }

        PerfLog(
            "Taskbar launch anchor from resident mouse-down for '" +
            requestedGroup + "': " + anchor.X + "," + anchor.Y);

        return true;
    }

    private static string GetGroupMenuSignature(string folder)
    {
        try
        {
            var builder = new StringBuilder();
            foreach (string path in Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                FileInfo info = new FileInfo(path);
                builder.Append(info.Name).Append('|')
                    .Append(info.Length).Append('|')
                    .Append(info.LastWriteTimeUtc.Ticks).Append(';');
            }
            return builder.ToString();
        }
        catch
        {
            return Guid.NewGuid().ToString("N");
        }
    }

    private static void ShowGroupMenu()
    {
        PerfMenuClock = Stopwatch.StartNew();
        PerfIconCacheHits = 0;
        PerfIconCacheMisses = 0;
        PerfFirstMenuPaintLogged = false;
        PerfLog("Menu rebuild started for group: " + Group);

        string signature = GetGroupMenuSignature(GroupFolder);
        CachedGroupMenu cached;
        bool cacheHit =
            GroupMenuCache.TryGetValue(Group, out cached) &&
            cached != null &&
            cached.Items != null &&
            String.Equals(cached.Signature, signature, StringComparison.Ordinal);

        if (MainMenu.Items.Count > 0)
        {
            ToolStripItem[] oldItems = new ToolStripItem[MainMenu.Items.Count];
            MainMenu.Items.CopyTo(oldItems, 0);
            MainMenu.Items.Clear();

            if (String.IsNullOrEmpty(AttachedCachedGroup))
            {
                foreach (ToolStripItem oldItem in oldItems)
                    DeferredMenuDisposals.Enqueue(oldItem);
            }

            AttachedCachedGroup = null;
        }

        if (cacheHit)
        {
            MainMenu.Items.AddRange(cached.Items);
            AttachedCachedGroup = Group;
            PerfLog("Group menu cache hit; reattached " + cached.Items.Length + " top-level items");
            if (!PrewarmMode)
                ShowPreparedMainMenu();
            return;
        }

        if (cached != null)
        {
            GroupMenuCache.Remove(Group);
            if (cached.Items != null)
            {
                foreach (ToolStripItem item in cached.Items)
                    DeferredMenuDisposals.Enqueue(item);
            }
            PerfLog("Stale group menu cache invalidated");
        }

        PerfLog("Group menu cache miss; building hierarchy");
        PerfLog("GetGroupItems starting");
        Stopwatch enumerateClock = Stopwatch.StartNew();
        List<FileInfo> files = GetGroupItems();
        enumerateClock.Stop();
        PerfLog("Group enumerated (" + files.Count + " items) in " +
            enumerateClock.Elapsed.TotalMilliseconds.ToString("0.0") + " ms");

        if (files.Count == 0)
        {
            var empty = new ToolStripMenuItem("(No items yet)") { Enabled = false };
            MainMenu.Items.Add(empty);
        }
        else
        {
            foreach (FileInfo file in files)
                AddGroupItem(file);
        }

        PerfLog("Group items constructed; icon cache hits=" + PerfIconCacheHits + ", misses=" + PerfIconCacheMisses);
        PerfLog("Static menu items construction starting");

        if (MainMenu.Items.Count > 0 &&
            !(MainMenu.Items[MainMenu.Items.Count - 1] is ToolStripSeparator))
            MainMenu.Items.Add(new ToolStripSeparator());

        var openFolder = new ToolStripMenuItem("Open " + Group + " folder");
        openFolder.Click += delegate
        {
            string folderToOpen = GroupFolder;

            // Explorer needs to be launched only after our popup and hidden host
            // have actually finished closing. A foreground worker thread keeps
            // this tiny launcher process alive long enough to do that without
            // leaving our WinForms window present while Groupy attaches Explorer.
            System.Threading.Thread opener = new System.Threading.Thread(
                delegate()
                {
                    System.Threading.Thread.Sleep(200);

                    // Ask the Windows shell to open the folder rather than launching
                    // explorer.exe directly. Modern Explorer may create a transient
                    // process/window and hand the request to the existing shell, which
                    // produces a visible flash.
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = folderToOpen,
                        UseShellExecute = true
                    });
                });
            opener.IsBackground = false;
            opener.Start();

            CloseMenu(false);
        };
        MainMenu.Items.Add(openFolder);

        var options = new ToolStripMenuItem("Group Options");
        options.DropDownItems.Add(MakeItem("Add Link", delegate { AddLink(null, false); }));
        options.DropDownItems.Add(MakeItem("Add Header", delegate { InsertTemplate(null, true, false); }));
        options.DropDownItems.Add(MakeItem("Add Separator", delegate { InsertTemplate(null, false, false); }));
        options.DropDownItems.Add(new ToolStripSeparator());
        options.DropDownItems.Add(MakeItem("Rename Group", delegate { RenameGroup(); }));
        options.DropDownItems.Add(MakeItem("Change Group Icon", delegate { ChangeGroupIcon(); }));
        options.DropDownItems.Add(MakeItem("New Group", delegate { NewGroup(); }));
        options.DropDownItems.Add(MakeItem("Delete Group", delegate { DeleteGroup(); }));

        // This fires before WinForms closes the owning ContextMenuStrip.
        // Keep the hidden host alive while a command is running so modal
        // dialogs (Add Link, New Group, Rename, icon pickers, etc.) survive.
        options.DropDown.ItemClicked += delegate
        {
        };

        ApplyTheme(options.DropDown);
        if (IsDarkMode())
        {
            foreach (ToolStripItem child in options.DropDownItems)
                if (!(child is ToolStripSeparator))
                    child.ForeColor = Color.FromArgb(240, 240, 240);
        }

        MainMenu.Items.Add(options);
        PerfLog("Static menu items constructed");


        // Give the persistent popup a real owner window. Showing the
        // ContextMenuStrip by absolute screen coordinate leaves it effectively
        // ownerless; that worked when the old process died with the menu, but a
        // resident process can then leave the popup open after focus moves
        // elsewhere. Owning it with HostForm restores normal ContextMenuStrip
        // AutoClose behavior without destroying the persistent host.

        ToolStripItem[] builtItems = new ToolStripItem[MainMenu.Items.Count];
        MainMenu.Items.CopyTo(builtItems, 0);
        GroupMenuCache[Group] = new CachedGroupMenu
        {
            Signature = signature,
            Items = builtItems
        };
        AttachedCachedGroup = Group;
        PerfLog("Group menu cached; top-level items=" + builtItems.Length);

        if (!PrewarmMode)
            ShowPreparedMainMenu();
    }

    private static void ShowPreparedMainMenu()
    {
        Point showPoint = UseExactRestorePoint ? ExactRestorePoint : MenuPoint;
        HostForm.Location = showPoint;

        // Activate immediately before showing. ContextMenuStrip then owns the
        // popup-mode message loop and its built-in AutoClose semantics. Do not
        // separately close from HostForm.Deactivate: taskbar launcher clicks
        // legitimately move focus during IPC startup and that creates a race
        // where the newly requested menu is closed before it paints.
        HostForm.Activate();

        PerfLog("ContextMenuStrip.Show starting");
        MainMenu.Show(HostForm, new Point(0, 0));

        if (UseExactRestorePoint)
        {
            MainMenu.Location = ExactRestorePoint;
            UseExactRestorePoint = false;
        }

        PerfLog("ContextMenuStrip.Show returned");

        // Persist the actual top-left while the menu is definitely still open.
        // Submenu commands close MainMenu before their Click handlers run, so
        // RefreshMenu() cannot reliably query MainMenu.Location later.
        LastMainMenuLocation = MainMenu.Location;
        HaveLastMainMenuLocation = true;
    }

    private static void AddGroupItem(FileInfo file)
    {
        string ext = file.Extension.ToLowerInvariant();

        if (ext == ".separator")
        {
            var sep = new ToolStripSeparator
            {
                Tag = file.FullName,
                AutoSize = false,
                Height = 13
            };
            WireReorderHandlers(sep);
            MainMenu.Items.Add(sep);
            return;
        }

        var item = new ToolStripMenuItem
        {
            Text = StripNumericPrefix(Path.GetFileNameWithoutExtension(file.Name)),
            Tag = file.FullName
        };

        if (ext == ".heading")
        {
            item.Font = new Font(item.Font, FontStyle.Bold);
            item.ForeColor = IsDarkMode() ? Color.FromArgb(245, 245, 245) : Color.FromArgb(45, 45, 45);
            item.BackColor = IsDarkMode() ? Color.FromArgb(58, 58, 58) : Color.FromArgb(218, 218, 218);
        }

        WireReorderHandlers(item);

        if (ext != ".heading")
        {
            try
            {
                Image image = GetMenuImage(file.FullName);
                if (image != null) item.Image = image;
            }
            catch { }

            // ToolStripMenuItem.Click is not reliably left-button-only.
            // Launch explicitly from MouseUp so a right-click can only open
            // the management submenu and can never launch the item.
            item.MouseUp += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                if (SuppressNextLaunch)
                {
                    SuppressNextLaunch = false;
                    return;
                }

                try { Launch(file.FullName); }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not launch:\r\n" + file.FullName + "\r\n\r\n" + ex.Message,
                        "Taskbar Groups", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { CloseMenu(false); }
            };
        }

        MainMenu.Items.Add(item);
    }

    private static void WireReorderHandlers(ToolStripItem item)
    {
        item.MouseDown += ItemMouseDown;
        item.MouseMove += ReorderMouseMove;
        item.MouseUp += ReorderMouseUp;
    }

    private static void ReorderMouseMove(object sender, MouseEventArgs e)
    {
        if (DragCandidate == null || Control.MouseButtons != MouseButtons.Left) return;

        Point now = Control.MousePosition;
        if (!ReorderDragging)
        {
            Size threshold = SystemInformation.DragSize;
            if (Math.Abs(now.X - DragStartScreen.X) < threshold.Width / 2 &&
                Math.Abs(now.Y - DragStartScreen.Y) < threshold.Height / 2)
                return;

            ReorderDragging = true;
            MainMenu.Capture = true;
        }

        DragInsertionIndex = GetDropInsertionIndex(now);
        MainMenu.Invalidate();
    }

    private static void ReorderMouseUp(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || DragCandidate == null) return;

        ToolStripItem source = DragCandidate;
        bool wasDragging = ReorderDragging;
        int insertion = DragInsertionIndex;

        DragCandidate = null;
        ReorderDragging = false;
        DragInsertionIndex = -1;
        MainMenu.Capture = false;
        MainMenu.Invalidate();

        if (!wasDragging) return;

        SuppressNextLaunch = true;
        ReorderByDrop(source, insertion);
    }

    private static int GetDropInsertionIndex(Point screenPoint)
    {
        List<FileInfo> files = GetGroupItems();
        Point client = MainMenu.PointToClient(screenPoint);

        for (int i = 0; i < files.Count; i++)
        {
            ToolStripItem visual = FindMenuItemByPath(files[i].FullName);
            if (visual == null) continue;
            int midpoint = visual.Bounds.Top + visual.Bounds.Height / 2;
            if (client.Y < midpoint) return i;
        }

        return files.Count;
    }

    private static void ReorderByDrop(ToolStripItem source, int insertion)
    {
        if (source == null || source.Tag == null || insertion < 0) return;

        string sourcePath = source.Tag.ToString();
        List<FileInfo> ordered = GetGroupItems();
        int oldIndex = ordered.FindIndex(delegate(FileInfo f) { return SamePath(f.FullName, sourcePath); });
        if (oldIndex < 0) return;

        FileInfo moving = ordered[oldIndex];
        ordered.RemoveAt(oldIndex);
        if (insertion > oldIndex) insertion--;
        if (insertion < 0) insertion = 0;
        if (insertion > ordered.Count) insertion = ordered.Count;
        if (insertion == oldIndex) return;

        ordered.Insert(insertion, moving);

        try
        {
            RenumberInOrder(ordered);
            RefreshMenu();
        }
        catch (Exception ex)
        {
            Error("Could not reorder item.", ex);
        }
    }

    private static ToolStripItem FindMenuItemByPath(string path)
    {
        foreach (ToolStripItem item in MainMenu.Items)
            if (item.Tag != null && SamePath(item.Tag.ToString(), path))
                return item;
        return null;
    }

    private static void RenumberInOrder(List<FileInfo> ordered)
    {
        var temporary = new List<string>();

        for (int i = 0; i < ordered.Count; i++)
        {
            string tempPath = Path.Combine(GroupFolder,
                "__reorder_" + Guid.NewGuid().ToString("N") + ordered[i].Extension);
            File.Move(ordered[i].FullName, tempPath);
            temporary.Add(tempPath);
        }

        for (int i = 0; i < ordered.Count; i++)
        {
            string cleanName = StripNumericPrefix(Path.GetFileNameWithoutExtension(ordered[i].Name));
            string finalPath = Path.Combine(GroupFolder,
                PrefixName((i + 1) * 10, cleanName, ordered[i].Extension));
            File.Move(temporary[i], finalPath);
        }
    }

    private static void MainMenuPaint(object sender, PaintEventArgs e)
    {
        if (MainMenu == null) return;

        Color separatorBand = IsDarkMode() ? Color.FromArgb(48, 48, 48) : Color.FromArgb(232, 232, 232);
        Color separatorLine = IsDarkMode() ? Color.FromArgb(112, 112, 112) : Color.FromArgb(150, 150, 150);

        foreach (ToolStripItem item in MainMenu.Items)
        {
            if (item.Tag == null) continue;
            string path = item.Tag.ToString();
            if (!Path.GetExtension(path).Equals(".separator", StringComparison.OrdinalIgnoreCase)) continue;

            Rectangle band = new Rectangle(1, item.Bounds.Top, Math.Max(1, MainMenu.ClientSize.Width - 2), item.Bounds.Height);
            using (Brush b = new SolidBrush(separatorBand)) e.Graphics.FillRectangle(b, band);
            int y = item.Bounds.Top + item.Bounds.Height / 2;
            using (Pen p = new Pen(separatorLine))
                e.Graphics.DrawLine(p, 8, y, Math.Max(8, MainMenu.ClientSize.Width - 9), y);
        }

        if (ReorderDragging && DragInsertionIndex >= 0)
        {
            List<FileInfo> files = GetGroupItems();
            int y;
            if (DragInsertionIndex >= files.Count)
            {
                ToolStripItem last = files.Count == 0 ? null : FindMenuItemByPath(files[files.Count - 1].FullName);
                y = last == null ? 2 : last.Bounds.Bottom;
            }
            else
            {
                ToolStripItem target = FindMenuItemByPath(files[DragInsertionIndex].FullName);
                y = target == null ? 2 : target.Bounds.Top;
            }

            Color marker = IsDarkMode() ? Color.FromArgb(235, 235, 235) : Color.FromArgb(45, 45, 45);
            using (Pen p = new Pen(marker, 2))
                e.Graphics.DrawLine(p, 3, y, Math.Max(3, MainMenu.ClientSize.Width - 4), y);
        }
    }

    private static void ItemMouseDown(object sender, MouseEventArgs e)
    {
        ToolStripItem tsi = sender as ToolStripItem;
        if (tsi == null || tsi.Tag == null) return;

        if (e.Button == MouseButtons.Left)
        {
            DragCandidate = tsi;
            DragStartScreen = Control.MousePosition;
            ReorderDragging = false;
            DragInsertionIndex = -1;
            SuppressNextLaunch = false;
            return;
        }

        if (e.Button != MouseButtons.Right) return;

        string path = tsi.Tag.ToString();
        if (!File.Exists(path)) return;

        ToolStripMenuItem menuItem = tsi as ToolStripMenuItem;
        if (menuItem != null)
        {
            ShowItemContext(path, menuItem);
            return;
        }

        // ToolStripSeparator cannot own a submenu. For separators, use the
        // same management commands through Group Options instead of opening
        // a second top-level ContextMenuStrip, which would close MainMenu.
    }

    private static void ShowItemContext(string path, ToolStripMenuItem ownerItem)
    {
        if (MainMenu != null && MainMenu.Visible)
        {
            LastMainMenuLocation = MainMenu.Location;
            HaveLastMainMenuLocation = true;
        }

        ownerItem.DropDownItems.Clear();

        ownerItem.DropDown.ItemClicked += delegate
        {
        };

        ApplyTheme(ownerItem.DropDown);
        ToolStripItemCollection items = ownerItem.DropDownItems;

        items.Add(MakeItem("Move Up", delegate { MoveItem(path, -1); }));
        items.Add(MakeItem("Move Down", delegate { MoveItem(path, +1); }));
        items.Add(new ToolStripSeparator());

        items.Add(MakeItem("Insert Header Above", delegate { InsertTemplate(path, true, true); }));
        items.Add(MakeItem("Insert Separator Below", delegate { InsertTemplate(path, false, true); }));
        items.Add(new ToolStripSeparator());

        items.Add(MakeItem("Add Link Below", delegate { AddLink(path, true); }));
        items.Add(MakeItem("Rename", delegate { RenameItem(path); }));

        var changeIcon = MakeItem("Change Icon", delegate { ChangeItemIcon(path); });
        changeIcon.Enabled = Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);
        items.Add(changeIcon);

        items.Add(MakeItem("Remove", delegate { RemoveItem(path); }));
        items.Add(new ToolStripSeparator());

        var openLocation = MakeItem("Open File Location", delegate { OpenTargetLocation(path); });
        var linkProps = MakeItem("Link Properties", delegate { ShowProperties(path); });
        var fileProps = MakeItem("File Properties", delegate { ShowTargetProperties(path); });

        bool isLink = Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);
        bool isLayout = IsLayoutItem(path);

        string resolvedTarget;
        string resolvedArguments;
        string resolvedIconLocation;
        bool shortcutResolves =
            isLink &&
            TryResolveShortcut(
                path,
                out resolvedTarget,
                out resolvedArguments,
                out resolvedIconLocation
            );

        openLocation.Enabled = shortcutResolves;
        linkProps.Enabled = isLink;
        fileProps.Enabled = shortcutResolves;

        if (isLayout)
        {
            openLocation.Enabled = false;
            linkProps.Enabled = false;
            fileProps.Enabled = false;
            changeIcon.Enabled = false;
        }

        items.Add(openLocation);
        items.Add(linkProps);
        items.Add(fileProps);

        ownerItem.ShowDropDown();
    }

    private static ToolStripMenuItem MakeItem(string text, EventHandler click)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += delegate(object sender, EventArgs e)
        {
            try
            {
                click(sender, e);
            }
            finally
            {
            }
        };
        return item;
    }

    // ---------- Ordering ----------

    private sealed class OrderedItem
    {
        public string Path;
        public int Prefix;
        public string CleanName;
        public string Extension;
    }

    private static List<OrderedItem> GetOrderedItems()
    {
        return GetGroupItems()
            .Select(f => new OrderedItem
            {
                Path = f.FullName,
                Prefix = GetPrefix(f.Name),
                CleanName = StripNumericPrefix(Path.GetFileNameWithoutExtension(f.Name)),
                Extension = f.Extension
            })
            .OrderBy(x => x.Prefix)
            .ThenBy(x => x.CleanName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static List<FileInfo> GetGroupItems()
    {
        return new DirectoryInfo(GroupFolder)
            .GetFiles()
            .Where(f => AllowedExtensions.Contains(f.Extension.ToLowerInvariant()))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static int GetPrefix(string filename)
    {
        Match m = Regex.Match(Path.GetFileNameWithoutExtension(filename), @"^\s*(\d+)\s*[-._)]\s*");
        if (!m.Success) return Int32.MaxValue / 2;
        int n;
        return Int32.TryParse(m.Groups[1].Value, out n) ? n : Int32.MaxValue / 2;
    }

    private static string StripNumericPrefix(string name)
    {
        return Regex.Replace(name ?? "", @"^\s*\d+\s*[-._)]\s*", "");
    }

    private static string PrefixName(int prefix, string cleanName, string extension)
    {
        return prefix.ToString("D3") + " - " + cleanName + extension;
    }

    private static void MoveItem(string path, int direction)
    {
        try
        {
            var items = GetOrderedItems();
            int index = items.FindIndex(x => SamePath(x.Path, path));
            if (index < 0) return;

            if (direction < 0 && index == 0)
            {
                if (items[index].Prefix >= 10)
                {
                    RenameOne(items[index], items[index].Prefix - 10);
                }
                else
                {
                    // Shift every other item upward by 10, leaving the selected
                    // item at 000. Two-phase rename prevents collisions.
                    var map = new Dictionary<OrderedItem, int>();
                    foreach (var x in items)
                        map[x] = SamePath(x.Path, path) ? 0 : SafePrefix(x.Prefix) + 10;
                    RenameMany(map);
                }
            }
            else if (direction > 0 && index == items.Count - 1)
            {
                RenameOne(items[index], SafePrefix(items[index].Prefix) + 10);
            }
            else
            {
                int otherIndex = index + direction;
                OrderedItem a = items[index];
                OrderedItem b = items[otherIndex];

                // "Move" means changing order, so swap the two occupied prefix
                // values rather than creating duplicate prefixes.
                var map = new Dictionary<OrderedItem, int>
                {
                    { a, SafePrefix(b.Prefix) },
                    { b, SafePrefix(a.Prefix) }
                };
                RenameMany(map);
            }

            RefreshMenu();
        }
        catch (Exception ex) { Error("Could not move item.", ex); }
    }

    private static int SafePrefix(int prefix)
    {
        return prefix >= Int32.MaxValue / 4 ? 0 : prefix;
    }

    private static void RenameOne(OrderedItem item, int prefix)
    {
        var map = new Dictionary<OrderedItem, int> { { item, Math.Max(0, prefix) } };
        RenameMany(map);
    }

    private static void RenameMany(Dictionary<OrderedItem, int> map)
    {
        var temps = new Dictionary<OrderedItem, string>();

        foreach (var kv in map)
        {
            string temp = Path.Combine(GroupFolder, "__TBG_" + Guid.NewGuid().ToString("N") + kv.Key.Extension);
            File.Move(kv.Key.Path, temp);
            temps[kv.Key] = temp;
        }

        try
        {
            foreach (var kv in map)
            {
                string dest = Path.Combine(GroupFolder,
                    PrefixName(kv.Value, kv.Key.CleanName, kv.Key.Extension));
                if (File.Exists(dest))
                    throw new IOException("Destination already exists: " + dest);
                File.Move(temps[kv.Key], dest);
                temps[kv.Key] = null;
            }
        }
        finally
        {
            // If a later rename failed, restore anything still temporary.
            foreach (var kv in temps.Where(x => x.Value != null && File.Exists(x.Value)).ToList())
            {
                if (!File.Exists(kv.Key.Path))
                    File.Move(kv.Value, kv.Key.Path);
            }
        }
    }

    private static void RespaceAll()
    {
        var items = GetOrderedItems();
        var map = new Dictionary<OrderedItem, int>();
        for (int i = 0; i < items.Count; i++)
            map[items[i]] = (i + 1) * 10;
        RenameMany(map);
    }

    private static int GetInsertionPrefix(string anchorPath, bool above)
    {
        var items = GetOrderedItems();
        if (items.Count == 0) return 10;

        if (String.IsNullOrEmpty(anchorPath))
            return SafePrefix(items[items.Count - 1].Prefix) + 10;

        int index = items.FindIndex(x => SamePath(x.Path, anchorPath));
        if (index < 0) return SafePrefix(items[items.Count - 1].Prefix) + 10;

        int current = SafePrefix(items[index].Prefix);

        if (above)
        {
            if (index == 0)
            {
                if (current >= 10) return current - 10;

                // Need room before zero: shift all existing items by 10.
                var map = items.ToDictionary(x => x, x => SafePrefix(x.Prefix) + 10);
                RenameMany(map);
                return 0;
            }

            int previous = SafePrefix(items[index - 1].Prefix);
            if (current - previous <= 1)
            {
                string identity = GetIdentity(anchorPath);
                RespaceAll();
                string newAnchor = FindPathByIdentity(identity);
                return GetInsertionPrefix(newAnchor, true);
            }

            // Ceiling of midpoint.
            return previous + ((current - previous + 1) / 2);
        }
        else
        {
            if (index == items.Count - 1) return current + 10;

            int next = SafePrefix(items[index + 1].Prefix);
            if (next - current <= 1)
            {
                string identity = GetIdentity(anchorPath);
                RespaceAll();
                string newAnchor = FindPathByIdentity(identity);
                return GetInsertionPrefix(newAnchor, false);
            }

            return current + ((next - current + 1) / 2);
        }
    }

    private static string GetIdentity(string path)
    {
        return StripNumericPrefix(Path.GetFileNameWithoutExtension(path)) + "|" + Path.GetExtension(path);
    }

    private static string FindPathByCleanIdentity(string oldPath)
    {
        return FindPathByIdentity(GetIdentity(oldPath));
    }

    private static string FindPathByIdentity(string identity)
    {
        foreach (var f in GetGroupItems())
            if (String.Equals(GetIdentity(f.FullName), identity, StringComparison.OrdinalIgnoreCase))
                return f.FullName;
        return null;
    }

    // ---------- Insert / remove / rename ----------

    private static void InsertTemplate(string anchorPath, bool header, bool relative)
    {
        try
        {
            string template = Path.Combine(TemplatesRoot,
                header ? "Section Heading.heading" : "Spacer.separator");

            if (!File.Exists(template))
                throw new FileNotFoundException("Template not found.", template);

            int prefix = relative
                ? GetInsertionPrefix(anchorPath, header)
                : GetInsertionPrefix(null, false);

            string ext = header ? ".heading" : ".separator";
            string name;

            if (header)
            {
                string value = Prompt("Header text:", "Insert Header", "Section Heading");
                if (value == null) return;
                value = SanitizeFileName(value.Trim());
                if (value.Length == 0) return;
                name = PrefixName(prefix, value, ext);
            }
            else
            {
                // Filename text is irrelevant for separators, but keep it unique.
                name = PrefixName(prefix, "Spacer", ext);
            }

            string destination = UniquePath(Path.Combine(GroupFolder, name));
            File.Copy(template, destination);
            RefreshMenu();
        }
        catch (Exception ex) { Error("Could not insert item.", ex); }
    }

    private static void RemoveItem(string path)
    {
        string display = StripNumericPrefix(Path.GetFileNameWithoutExtension(path));
        DialogResult r = MessageBox.Show(
            "Remove \"" + display + "\" from " + Group + "?\r\n\r\nThis deletes the group item only.",
            "Taskbar Groups", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (r != DialogResult.Yes) return;

        try
        {
            File.Delete(path);
            RefreshMenu();
        }
        catch (Exception ex) { Error("Could not remove item.", ex); }
    }

    private static void RenameItem(string path)
    {
        try
        {
            string oldName = StripNumericPrefix(Path.GetFileNameWithoutExtension(path));
            string value = Prompt("Name:", "Rename Item", oldName);
            if (value == null) return;

            value = SanitizeFileName(value.Trim());
            if (value.Length == 0) return;

            int prefix = GetPrefix(Path.GetFileName(path));
            if (prefix >= Int32.MaxValue / 4) prefix = 10;

            string dest = Path.Combine(GroupFolder, PrefixName(prefix, value, Path.GetExtension(path)));
            if (SamePath(dest, path)) return;
            if (File.Exists(dest)) throw new IOException("An item with that filename already exists.");

            File.Move(path, dest);
            RefreshMenu();
        }
        catch (Exception ex) { Error("Could not rename item.", ex); }
    }

    // ---------- Add link ----------

    private static void AddLink(string anchorPath, bool relative)
    {
        using (var dialog = new AddLinkDialog())
        {
            if (dialog.ShowDialog() != DialogResult.OK) return;

            try
            {
                string target = Environment.ExpandEnvironmentVariables(dialog.TargetPath.Trim());
                string arguments = dialog.Arguments ?? "";

                if (!File.Exists(target) && !Directory.Exists(target))
                    throw new FileNotFoundException("Target does not exist.", target);

                // Keep the display name based on what the user selected before a
                // script target is rewritten to its interpreter executable.
                string name = SanitizeFileName(dialog.LinkName.Trim());
                if (name.Length == 0)
                    name = Path.GetFileNameWithoutExtension(target.TrimEnd('\\'));

                string selectedTarget = target;
                if (Path.GetExtension(selectedTarget).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
                {
                    target = FindPowerShellHost();
                    string scriptArguments = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(selectedTarget);
                    if (!String.IsNullOrWhiteSpace(arguments))
                        scriptArguments += " " + arguments.Trim();
                    arguments = scriptArguments;
                }

                int prefix = relative
                    ? GetInsertionPrefix(anchorPath, false)
                    : GetInsertionPrefix(null, false);

                string linkPath = UniquePath(Path.Combine(GroupFolder,
                    PrefixName(prefix, name, ".lnk")));

                string workingDirectory = File.Exists(selectedTarget)
                    ? Path.GetDirectoryName(selectedTarget)
                    : selectedTarget;

                // The dialog is the source of truth. When an existing .lnk is
                // selected, AddLinkDialog resolves its target and arguments into
                // the visible fields. Create the new shortcut from those values so
                // the imported arguments are preserved and remain editable.
                CreateShortcut(linkPath, target, arguments, workingDirectory,
                    dialog.IconPath, 0, name);

                RefreshMenu();
            }
            catch (Exception ex) { Error("Could not add link.", ex); }
        }
    }

    private static string BuildVisiblePowerShellArguments(string scriptPath, string existingArguments)
    {
        string existing = (existingArguments ?? "").Trim();
        string quotedScript = Quote(scriptPath);

        // With a blank field, expose the complete default command line. The user
        // can then remove -NoProfile / -ExecutionPolicy Bypass or append script
        // parameters before creating the link.
        if (existing.Length == 0)
            return "-NoProfile -ExecutionPolicy Bypass -File " + quotedScript;

        // Do not overwrite user-entered arguments. If they already reference the
        // selected script, leave them exactly as entered. Otherwise retain them and
        // add the required -File argument. Existing text is kept before -File so it
        // remains PowerShell-host arguments rather than accidentally becoming script
        // parameters. Once visible, the user can place script parameters after the
        // script path if desired.
        if (ArgumentsReferencePath(existing, scriptPath))
            return existing;

        return existing + " -File " + quotedScript;
    }

    private static bool ArgumentsReferencePath(string arguments, string path)
    {
        if (String.IsNullOrWhiteSpace(arguments) || String.IsNullOrWhiteSpace(path))
            return false;

        string expandedArguments = Environment.ExpandEnvironmentVariables(arguments);
        string expandedPath = Environment.ExpandEnvironmentVariables(path);

        return expandedArguments.IndexOf(expandedPath, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string FindPowerShellHost()
    {
        // Prefer PowerShell 7 when installed. Fall back to Windows PowerShell,
        // which is present on supported Windows versions.
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pwsh = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        if (File.Exists(pwsh)) return pwsh;

        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string entry in path.Split(Path.PathSeparator))
        {
            try
            {
                string directory = entry.Trim().Trim('"');
                if (directory.Length == 0) continue;
                string candidate = Path.Combine(directory, "pwsh.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    private sealed class AddLinkDialog : Form
    {
        private readonly TextBox target = new TextBox();
        private readonly TextBox arguments = new TextBox();
        private readonly TextBox name = new TextBox();
        private readonly TextBox icon = new TextBox();
        private string selectedShortcut = "";

        public string SelectedShortcut { get { return selectedShortcut; } }
        public string TargetPath { get { return target.Text; } }
        public string Arguments { get { return arguments.Text; } }
        public string LinkName { get { return name.Text; } }
        public string IconPath { get { return icon.Text; } }

        public AddLinkDialog()
        {
            Text = "Add Link";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(590, 204);

            AddLabel("Target:", 12, 18);
            target.SetBounds(82, 15, 405, 23);
            var browseTarget = new Button { Text = "Browse..." };
            browseTarget.SetBounds(495, 14, 82, 25);

            AddLabel("Arguments:", 12, 56);
            arguments.SetBounds(82, 53, 495, 23);

            AddLabel("Name:", 12, 94);
            name.SetBounds(82, 91, 495, 23);

            AddLabel("Icon:", 12, 132);
            icon.SetBounds(82, 129, 405, 23);
            icon.ReadOnly = true;
            icon.Text = "(Default)";
            var browseIcon = new Button { Text = "Browse..." };
            browseIcon.SetBounds(495, 128, 82, 25);

            var add = new Button { Text = "Add", DialogResult = DialogResult.OK };
            add.SetBounds(405, 167, 82, 27);
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            cancel.SetBounds(495, 167, 82, 27);

            Controls.AddRange(new Control[] { target, arguments, name, icon, browseTarget, browseIcon, add, cancel });
            AcceptButton = add;
            CancelButton = cancel;

            browseTarget.Click += delegate
            {
                using (var ofd = new OpenFileDialog
                {
                    Title = "Select target",
                    Filter = "All files (*.*)|*.*",
                    DereferenceLinks = false
                })
                {
                    if (ofd.ShowDialog(this) == DialogResult.OK)
                    {
                        string selected = ofd.FileName;

                        if (Path.GetExtension(selected).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                        {
                            string resolvedTarget, resolvedArguments, resolvedIcon;
                            if (!TryResolveShortcut(selected,
                                out resolvedTarget, out resolvedArguments, out resolvedIcon))
                            {
                                MessageBox.Show(this,
                                    "Could not read the selected shortcut.",
                                    "Add Link",
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Error);
                                return;
                            }

                            target.Text = resolvedTarget;
                            arguments.Text = resolvedArguments;

                            // Preserve the existing shortcut icon when it explicitly
                            // defines one. Otherwise leave "(Default)" so Windows
                            // uses the target application's normal icon.
                            string iconFile;
                            int iconIndex;
                            ParseIconLocation(resolvedIcon, out iconFile, out iconIndex);
                            icon.Text = String.IsNullOrWhiteSpace(iconFile)
                                ? "(Default)"
                                : iconFile;
                        }
                        else if (Path.GetExtension(selected).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
                        {
                            // A PowerShell script is an argument to the PowerShell host,
                            // not an executable target. Show that transformation in the UI
                            // so the user can see and edit exactly what the shortcut will run.
                            target.Text = FindPowerShellHost();
                            arguments.Text = BuildVisiblePowerShellArguments(selected, arguments.Text);
                        }
                        else
                        {
                            target.Text = selected;
                            arguments.Text = "";
                        }

                        if (String.IsNullOrWhiteSpace(name.Text))
                            name.Text = Path.GetFileNameWithoutExtension(selected);
                    }
                }
            };

            browseIcon.Click += delegate
            {
                using (var ofd = new OpenFileDialog
                {
                    Title = "Select icon source",
                    Filter = "Icon sources (*.ico;*.exe;*.dll)|*.ico;*.exe;*.dll|All files (*.*)|*.*"
                })
                {
                    if (ofd.ShowDialog(this) == DialogResult.OK)
                        icon.Text = ofd.FileName;
                }
            };

            add.Click += delegate
            {
                if (String.IsNullOrWhiteSpace(target.Text))
                {
                    MessageBox.Show(this, "Choose a target.", "Add Link",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    DialogResult = DialogResult.None;
                }
            };
        }

        private void AddLabel(string text, int x, int y)
        {
            var label = new Label { Text = text, AutoSize = true };
            label.Location = new Point(x, y);
            Controls.Add(label);
        }
    }

    // ---------- Shortcut editing / properties ----------

    private static void ChangeItemIcon(string path)
    {
        if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return;

        using (var ofd = new OpenFileDialog
        {
            Title = "Select icon source",
            Filter = "Icon sources (*.ico;*.exe;*.dll)|*.ico;*.exe;*.dll|All files (*.*)|*.*"
        })
        {
            if (ofd.ShowDialog() != DialogResult.OK) return;

            try
            {
                string target, args, oldIcon;
                if (!TryResolveShortcut(path, out target, out args, out oldIcon))
                    throw new InvalidOperationException("Could not read shortcut.");

                string working = File.Exists(target) ? Path.GetDirectoryName(target) : target;
                CreateShortcut(path, target, args, working, ofd.FileName, 0,
                    StripNumericPrefix(Path.GetFileNameWithoutExtension(path)));
                RefreshMenu();
            }
            catch (Exception ex) { Error("Could not change icon.", ex); }
        }
    }

    private static void OpenTargetLocation(string linkPath)
    {
        try
        {
            string target, args, icon;
            if (!TryResolveShortcut(linkPath, out target, out args, out icon))
                throw new InvalidOperationException("Could not resolve shortcut target.");

            string scriptPath;
            if (TryGetPowerShellScriptPath(target, args, out scriptPath) &&
                File.Exists(scriptPath))
            {
                Process.Start("explorer.exe", "/select," + Quote(scriptPath));
            }
            else if (File.Exists(target))
            {
                Process.Start("explorer.exe", "/select," + Quote(target));
            }
            else if (Directory.Exists(target))
            {
                Process.Start("explorer.exe", Quote(target));
            }
            else
            {
                throw new FileNotFoundException("Shortcut target no longer exists.", target);
            }

            CloseMenu(false);
        }
        catch (Exception ex) { Error("Could not open file location.", ex); }
    }

    private static bool TryGetPowerShellScriptPath(string target, string arguments,
        out string scriptPath)
    {
        scriptPath = "";

        string executableName = Path.GetFileName(target);
        bool isPowerShell =
            executableName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
            executableName.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase);

        if (!isPowerShell || String.IsNullOrWhiteSpace(arguments))
            return false;

        Match match = Regex.Match(
            arguments,
            @"(?:^|\s)-(?:File|F)\s+(?:""(?<quoted>[^""]+)""|(?<plain>\S+))",
            RegexOptions.IgnoreCase);

        if (!match.Success)
            return false;

        scriptPath = match.Groups["quoted"].Success
            ? match.Groups["quoted"].Value
            : match.Groups["plain"].Value;

        scriptPath = Environment.ExpandEnvironmentVariables(scriptPath);

        if (!Path.IsPathRooted(scriptPath))
        {
            string linkDirectory = Path.GetDirectoryName(target);
            if (!String.IsNullOrWhiteSpace(linkDirectory))
                scriptPath = Path.GetFullPath(Path.Combine(linkDirectory, scriptPath));
        }

        return scriptPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
    }

    private static void ShowTargetProperties(string linkPath)
    {
        try
        {
            string target, args, icon;
            if (!TryResolveShortcut(linkPath, out target, out args, out icon))
                throw new InvalidOperationException("Could not resolve shortcut target.");

            string scriptPath;
            if (TryGetPowerShellScriptPath(target, args, out scriptPath) &&
                File.Exists(scriptPath))
            {
                ShowProperties(scriptPath);
            }
            else
            {
                ShowProperties(target);
            }
        }
        catch (Exception ex) { Error("Could not open target properties.", ex); }
    }

    private static void ShowProperties(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException("The item no longer exists.", path);

        PerfLog("Starting Link Properties worker: " + path);

        Process.Start(new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = "--link-properties-worker " + Quote(path),
            WorkingDirectory = AppRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static void RunLinkPropertiesWorker(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return;

        using (var owner = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(1, 1),
            Opacity = 0
        })
        {
            bool propertyWindowSeen = false;
            System.Windows.Forms.Timer lifetimeTimer = new System.Windows.Forms.Timer { Interval = 100 };

            lifetimeTimer.Tick += delegate
            {
                bool propertyWindowExists = HasVisibleTopLevelWindowForCurrentProcess(owner.Handle);

                if (propertyWindowExists)
                {
                    propertyWindowSeen = true;
                    return;
                }

                // SHObjectProperties creates a modeless property sheet. Do not
                // close the hidden owner merely because SHObjectProperties()
                // returned; that destroys the property sheet before Windows has
                // a chance to paint it. Once we have actually observed the sheet
                // and it later disappears, the worker can exit.
                if (propertyWindowSeen)
                {
                    lifetimeTimer.Stop();
                    owner.Close();
                }
            };

            owner.Shown += delegate
            {
                owner.BeginInvoke((MethodInvoker)delegate
                {
                    bool opened = SHObjectProperties(owner.Handle, SHOP_FILEPATH, path, null);
                    if (!opened)
                    {
                        int error = Marshal.GetLastWin32Error();
                        MessageBox.Show(
                            "Windows could not open Link Properties.\r\n\r\nWin32 error: " + error,
                            "Taskbar Groups",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        owner.Close();
                        return;
                    }

                    // Keep this process/message loop alive while the native
                    // modeless Properties sheet exists.
                    lifetimeTimer.Start();
                });
            };

            Application.Run(owner);
            lifetimeTimer.Dispose();
        }
    }

    private static bool HasVisibleTopLevelWindowForCurrentProcess(IntPtr excludedHandle)
    {
        bool found = false;
        uint currentPid = (uint)Process.GetCurrentProcess().Id;

        EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
        {
            if (hwnd == excludedHandle || !IsWindowVisible(hwnd))
                return true;

            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == currentPid)
            {
                found = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static void PerfLogOncePerMenuPaint()
    {
        if (PerfFirstMenuPaintLogged)
            return;

        PerfFirstMenuPaintLogged = true;
        PerfLog("ContextMenuStrip first Paint event");

        ScheduleDeferredMenuDisposal();
    }

    private static void ScheduleDeferredMenuDisposal()
    {
        if (DeferredDisposalScheduled || DeferredMenuDisposals.Count == 0)
            return;

        DeferredDisposalScheduled = true;

        // BeginInvoke posts this work behind the current paint message, so the
        // newly requested menu becomes visible before old ToolStripItems are
        // torn down.
        HostForm.BeginInvoke((MethodInvoker)delegate
        {
            Stopwatch disposeClock = Stopwatch.StartNew();
            int disposed = 0;

            try
            {
                while (DeferredMenuDisposals.Count > 0)
                {
                    ToolStripItem item = DeferredMenuDisposals.Dequeue();
                    item.Dispose();
                    disposed++;
                }
            }
            finally
            {
                DeferredDisposalScheduled = false;
                PerfLog(
                    "Deferred previous-menu disposal completed: " +
                    disposed + " top-level items in " +
                    disposeClock.Elapsed.TotalMilliseconds.ToString("F1") +
                    " ms");
            }
        });
    }

    private static string FormatLogLine(string level, string category, string message)
    {
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
            "  PID=" + Process.GetCurrentProcess().Id.ToString().PadLeft(6) +
            "  " + level.PadRight(5) +
            "  " + category.PadRight(11) +
            "  " + message + Environment.NewLine;
    }

    private static void WriteSessionLog(string level, string category, string message)
    {
        if (!SessionLogActive) return;

        try
        {
            lock (SessionLogLock)
            {
                Directory.CreateDirectory(DataRoot);
                File.AppendAllText(SessionLogPath, FormatLogLine(level, category, message));
            }
        }
        catch
        {
            // Logging must never affect launcher behavior.
        }
    }

    private static void LogInfo(string message)
    {
        WriteSessionLog("INFO", "runtime", message);
    }

    private static void LogError(string message, Exception ex)
    {
        string detail = message;
        if (ex != null)
        {
            detail += Environment.NewLine +
                ex.GetType().FullName + ": " + ex.Message +
                Environment.NewLine + ex.StackTrace;

            if (ex.InnerException != null)
            {
                detail += Environment.NewLine + "Inner exception: " +
                    ex.InnerException.GetType().FullName + ": " +
                    ex.InnerException.Message +
                    Environment.NewLine + ex.InnerException.StackTrace;
            }
        }

        WriteSessionLog("ERROR", "runtime", detail);
    }

    private static void PerfLog(string message)
    {
        if (!PerfEnabled || !SessionLogActive) return;

        string menuElapsed = PerfMenuClock == null
            ? "       -"
            : PerfMenuClock.Elapsed.TotalMilliseconds.ToString("0.0").PadLeft(8);

        WriteSessionLog(
            "PERF",
            "performance",
            "process=" + PerfClock.Elapsed.TotalMilliseconds.ToString("0.0").PadLeft(8) + " ms" +
            "  menu=" + menuElapsed + " ms  " + message);
    }

    private static void StartSessionLog()
    {
        try
        {
            lock (SessionLogLock)
            {
                if (SessionLogActive) return;

                Directory.CreateDirectory(DataRoot);
                SessionLogActive = true;

                File.AppendAllText(
                    SessionLogPath,
                    FormatLogLine("INFO", "startup",
                        "Resident host logging session started") +
                    FormatLogLine("INFO", "startup",
                        "Executable: " + Process.GetCurrentProcess().MainModule.FileName) +
                    FormatLogLine("INFO", "startup",
                        "Session: " + Process.GetCurrentProcess().SessionId +
                        "; PID: " + Process.GetCurrentProcess().Id) +
                    FormatLogLine("INFO", "startup",
                        "Performance logging: " + (PerfEnabled ? "enabled" : "disabled")) +
                    FormatLogLine("INFO", "startup",
                        "IPC mutex: " + HostMutexName) +
                    FormatLogLine("INFO", "startup",
                        "IPC pipe: " + HostPipeName));
            }
        }
        catch
        {
            // Logging must never prevent the resident host from starting.
        }
    }

    private static void InstallUnhandledExceptionLogging()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
        {
            LogError("Unhandled WinForms UI-thread exception.", e.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception;
            if (ex != null)
                LogError("Unhandled AppDomain exception. IsTerminating=" + e.IsTerminating, ex);
            else
                WriteSessionLog("ERROR", "runtime",
                    "Unhandled AppDomain exception object: " +
                    Convert.ToString(e.ExceptionObject));
        };
    }

    // ---------- Group management ----------

    private static void NewGroup()
    {
        using (var dialog = new NewGroupDialog())
        {
            if (dialog.ShowDialog() != DialogResult.OK) return;

            try
            {
                string name = SanitizeFileName(dialog.GroupName.Trim());
                if (name.Length == 0) return;

                string folder = Path.Combine(GroupsRoot, name);
                if (Directory.Exists(folder))
                    throw new IOException("A group named \"" + name + "\" already exists.");

                Directory.CreateDirectory(folder);
                Directory.CreateDirectory(PinRoot);
        Directory.CreateDirectory(IconCacheRoot);

                string icon = dialog.IconPath;
                if (String.IsNullOrWhiteSpace(icon))
                {
                    string preferred = Path.Combine(IconRoot, "Folder.ico");
                    if (File.Exists(preferred)) icon = preferred;
                    else
                    {
                        string[] icons = Directory.Exists(IconRoot)
                            ? Directory.GetFiles(IconRoot, "*.ico")
                            : new string[0];
                        if (icons.Length > 0) icon = icons[0];
                    }
                }

                string shortcut = Path.Combine(PinRoot, name + ".lnk");
                CreateShortcut(shortcut, ExePath, Quote(name), AppRoot, icon, 0, name);

                Process.Start("explorer.exe", "/select," + Quote(shortcut));

                MessageBox.Show(
                    "Created group \"" + name + "\".\r\n\r\n" +
                    "Its shortcut is selected in \"Pin to Taskbar\". Pin it normally when you're ready.",
                    "Taskbar Groups", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { Error("Could not create group.", ex); }
        }
    }

    private sealed class NewGroupDialog : Form
    {
        private readonly TextBox name = new TextBox();
        private readonly TextBox icon = new TextBox();

        public string GroupName { get { return name.Text; } }
        public string IconPath { get { return icon.Text == "(Default)" ? "" : icon.Text; } }

        public NewGroupDialog()
        {
            Text = "New Group";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(520, 126);

            Controls.Add(new Label { Text = "Name:", AutoSize = true, Location = new Point(12, 20) });
            name.SetBounds(72, 17, 435, 23);

            Controls.Add(new Label { Text = "Icon:", AutoSize = true, Location = new Point(12, 58) });
            icon.SetBounds(72, 55, 337, 23);
            icon.ReadOnly = true;
            icon.Text = "(Default)";

            var browse = new Button { Text = "Browse..." };
            browse.SetBounds(417, 54, 90, 25);

            var create = new Button { Text = "Create", DialogResult = DialogResult.OK };
            create.SetBounds(327, 91, 82, 27);
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            cancel.SetBounds(417, 91, 90, 27);

            Controls.AddRange(new Control[] { name, icon, browse, create, cancel });
            AcceptButton = create;
            CancelButton = cancel;

            browse.Click += delegate
            {
                using (var ofd = new OpenFileDialog
                {
                    InitialDirectory = Directory.Exists(IconRoot) ? IconRoot : AppRoot,
                    Filter = "Icon sources (*.ico;*.exe;*.dll)|*.ico;*.exe;*.dll|All files (*.*)|*.*"
                })
                {
                    if (ofd.ShowDialog(this) == DialogResult.OK)
                        icon.Text = ofd.FileName;
                }
            };
        }
    }

    private static void RenameGroup()
    {
        string newName = Prompt("Group name:", "Rename Group", Group);
        if (newName == null) return;

        newName = SanitizeFileName(newName.Trim());
        if (newName.Length == 0 || String.Equals(newName, Group, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            string newFolder = Path.Combine(GroupsRoot, newName);
            if (Directory.Exists(newFolder))
                throw new IOException("A group named \"" + newName + "\" already exists.");

            string oldPin = Path.Combine(PinRoot, Group + ".lnk");
            string newPin = Path.Combine(PinRoot, newName + ".lnk");

            Directory.Move(GroupFolder, newFolder);

            if (File.Exists(oldPin))
            {
                string target, args, icon;
                TryResolveShortcut(oldPin, out target, out args, out icon);
                File.Move(oldPin, newPin);
                CreateShortcut(newPin, ExePath, Quote(newName), AppRoot, icon, 0, newName);
            }
            else
            {
                CreateShortcut(newPin, ExePath, Quote(newName), AppRoot, "", 0, newName);
            }

            MessageBox.Show(
                "Renamed \"" + Group + "\" to \"" + newName + "\".\r\n\r\n" +
                "If the old group is pinned, unpin it and pin the updated shortcut from:\r\n" + PinRoot,
                "Taskbar Groups", MessageBoxButtons.OK, MessageBoxIcon.Information);

            Group = newName;
            GroupFolder = newFolder;
            CloseMenu(false);
        }
        catch (Exception ex) { Error("Could not rename group.", ex); }
    }

    private static void ChangeGroupIcon()
    {
        using (var ofd = new OpenFileDialog
        {
            InitialDirectory = Directory.Exists(IconRoot) ? IconRoot : AppRoot,
            Title = "Select group icon",
            Filter = "Icon sources (*.ico;*.exe;*.dll)|*.ico;*.exe;*.dll|All files (*.*)|*.*"
        })
        {
            if (ofd.ShowDialog() != DialogResult.OK) return;

            try
            {
                string pin = Path.Combine(PinRoot, Group + ".lnk");
                if (!File.Exists(pin))
                    throw new FileNotFoundException("Group shortcut not found.", pin);

                string target, args, oldIcon;
                if (!TryResolveShortcut(pin, out target, out args, out oldIcon))
                    throw new InvalidOperationException("Could not read group shortcut.");

                CreateShortcut(pin, target, args, AppRoot, ofd.FileName, 0, Group);

                MessageBox.Show(
                    "Group shortcut icon updated.\r\n\r\n" +
                    "Windows may keep the old taskbar icon cached. If so, unpin and repin this group.",
                    "Taskbar Groups", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { Error("Could not change group icon.", ex); }
        }
    }

    private static void DeleteGroup()
    {
        DialogResult r = MessageBox.Show(
            "Delete group \"" + Group + "\"?\r\n\r\n" +
            "This deletes the group folder and its group items, and removes its shortcut from \"Pin to Taskbar\".\r\n\r\n" +
            "Programs and files targeted by the shortcuts are not deleted.",
            "Taskbar Groups", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (r != DialogResult.Yes) return;

        try
        {
            string pin = Path.Combine(PinRoot, Group + ".lnk");
            Directory.Delete(GroupFolder, true);
            if (File.Exists(pin)) File.Delete(pin);
            CloseMenu(false);
        }
        catch (Exception ex) { Error("Could not delete group.", ex); }
    }

    // ---------- Launch / shell / icons ----------

    private static void Launch(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext == ".lnk" || ext == ".url")
            Process.Start("explorer.exe", Quote(path));
        else if (ext == ".ps1")
            Process.Start("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -File " + Quote(path));
        else
            Process.Start(path);
    }

    private static Image GetMenuImage(string path)
    {
        // Menu startup must not depend on the shortcut target being responsive.
        // Cache validity is based only on the group item itself, which lives in
        // Taskbar-Groups' local data directory. In particular, do not stat the
        // resolved target or icon source here: either could be on a sleeping HDD,
        // disconnected USB device, or unavailable network share.
        string cacheKey = GetIconCacheKey(path);
        Image cached = TryGetCachedMenuImage(cacheKey);
        if (cached != null) return cached;

        Image image = LoadMenuImageUncached(path);
        if (image != null)
        {
            CacheMenuImage(cacheKey, image);
            return image;
        }

        return null;
    }

    private static Image LoadMenuImageUncached(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext == ".lnk")
        {
            string target, args, iconLocation;
            if (TryResolveShortcut(path, out target, out args, out iconLocation))
            {
                string iconFile;
                int iconIndex;
                ParseIconLocation(iconLocation, out iconFile, out iconIndex);

                Icon icon = ExtractIcon(iconFile, iconIndex);
                if (icon == null) icon = ExtractIcon(target, 0);
                if (icon != null)
                {
                    Bitmap bmp = icon.ToBitmap();
                    icon.Dispose();
                    return bmp;
                }
            }
        }

        try
        {
            Icon associated = Icon.ExtractAssociatedIcon(path);
            if (associated != null)
            {
                Bitmap bmp = associated.ToBitmap();
                associated.Dispose();
                return bmp;
            }
        }
        catch { }

        SHFILEINFO info = new SHFILEINFO();
        IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info),
            SHGFI_ICON | SHGFI_SMALLICON);
        if (result != IntPtr.Zero && info.hIcon != IntPtr.Zero)
        {
            Icon clone = (Icon)Icon.FromHandle(info.hIcon).Clone();
            DestroyIcon(info.hIcon);
            Bitmap bmp = clone.ToBitmap();
            clone.Dispose();
            return bmp;
        }

        return null;
    }

    private static string GetIconCacheKey(string path)
    {
        try
        {
            FileInfo file = new FileInfo(path);
            string identity = Path.GetFullPath(path).ToUpperInvariant() + "|" +
                file.Length + "|" + file.LastWriteTimeUtc.Ticks;

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(identity));
                var result = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) result.Append(b.ToString("x2"));
                return result.ToString();
            }
        }
        catch
        {
            // If metadata cannot be read, use the path alone. This still gives us
            // a stable cache entry without touching the shortcut's target.
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
                var result = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) result.Append(b.ToString("x2"));
                return result.ToString();
            }
        }
    }

    private static Image TryGetCachedMenuImage(string cacheKey)
    {
        Bitmap memoryImage;
        if (MemoryIconCache.TryGetValue(cacheKey, out memoryImage))
        {
            PerfIconCacheHits++;
            return new Bitmap(memoryImage);
        }

        string cachePath = Path.Combine(IconCacheRoot, cacheKey + ".png");
        if (!File.Exists(cachePath))
        {
            PerfIconCacheMisses++;
            return null;
        }

        try
        {
            // Image.FromFile keeps the file locked for the lifetime of the Image.
            // Clone it immediately so the cache remains freely replaceable.
            using (Image diskImage = Image.FromFile(cachePath))
            {
                Bitmap stored = new Bitmap(diskImage);
                MemoryIconCache[cacheKey] = stored;
                PerfIconCacheHits++;
                return new Bitmap(stored);
            }
        }
        catch
        {
            PerfIconCacheMisses++;
            try { File.Delete(cachePath); } catch { }
            return null;
        }
    }

    private static void CacheMenuImage(string cacheKey, Image image)
    {
        try
        {
            Bitmap stored = new Bitmap(image);
            MemoryIconCache[cacheKey] = stored;

            string cachePath = Path.Combine(IconCacheRoot, cacheKey + ".png");
            stored.Save(cachePath, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch
        {
            // Caching is an optimization only; icon loading must still succeed if
            // the cache directory is temporarily unavailable.
        }
    }

    private static Icon ExtractIcon(string file, int index)
    {
        if (String.IsNullOrWhiteSpace(file)) return null;
        file = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));
        if (!File.Exists(file)) return null;

        IntPtr[] large = new IntPtr[1];
        IntPtr[] small = new IntPtr[1];
        uint count = ExtractIconEx(file, index, large, small, 1);
        IntPtr h = small[0] != IntPtr.Zero ? small[0] : large[0];

        if (count == 0 || h == IntPtr.Zero) return null;

        Icon clone = (Icon)Icon.FromHandle(h).Clone();
        if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
        if (small[0] != IntPtr.Zero && small[0] != large[0]) DestroyIcon(small[0]);
        return clone;
    }

    private static void ParseIconLocation(string value, out string file, out int index)
    {
        file = value ?? "";
        index = 0;
        if (String.IsNullOrWhiteSpace(value)) return;

        int comma = value.LastIndexOf(',');
        if (comma > 0)
        {
            int parsed;
            if (Int32.TryParse(value.Substring(comma + 1).Trim(), out parsed))
            {
                index = parsed;
                file = value.Substring(0, comma).Trim().Trim('"');
            }
        }
    }

    // ---------- ShellLink COM ----------

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName,
            int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir,
            int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs,
            int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
            int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    private static void CreateShortcut(string linkPath, string target, string arguments,
        string workingDirectory, string iconPath, int iconIndex, string description)
    {
        object obj = new ShellLink();
        try
        {
            IShellLinkW link = (IShellLinkW)obj;
            link.SetPath(target);
            link.SetArguments(arguments ?? "");
            if (!String.IsNullOrWhiteSpace(workingDirectory))
                link.SetWorkingDirectory(workingDirectory);
            if (!String.IsNullOrWhiteSpace(description))
                link.SetDescription(description);

            if (!String.IsNullOrWhiteSpace(iconPath) && iconPath != "(Default)")
                link.SetIconLocation(iconPath, iconIndex);

            ((IPersistFile)obj).Save(linkPath, true);
            RemoveZoneIdentifier(linkPath);
        }
        finally
        {
            if (Marshal.IsComObject(obj)) Marshal.FinalReleaseComObject(obj);
        }
    }

    private static void SetShortcutIcon(string linkPath, string iconPath, int iconIndex)
    {
        object obj = new ShellLink();
        try
        {
            ((IPersistFile)obj).Load(linkPath, 0);
            ((IShellLinkW)obj).SetIconLocation(iconPath, iconIndex);
            ((IPersistFile)obj).Save(linkPath, true);
            RemoveZoneIdentifier(linkPath);
        }
        finally
        {
            if (Marshal.IsComObject(obj)) Marshal.FinalReleaseComObject(obj);
        }
    }

    private static void RemoveZoneIdentifier(string path)
    {
        try
        {
            File.Delete(path + ":Zone.Identifier");
        }
        catch
        {
        }
    }

    private static bool TryResolveShortcut(string linkPath, out string target,
        out string arguments, out string iconLocation)
    {
        target = arguments = iconLocation = "";
        object obj = null;

        try
        {
            obj = new ShellLink();
            ((IPersistFile)obj).Load(linkPath, 0);

            IShellLinkW link = (IShellLinkW)obj;

            var path = new StringBuilder(32768);
            link.GetPath(path, path.Capacity, IntPtr.Zero, 0);

            var args = new StringBuilder(32768);
            link.GetArguments(args, args.Capacity);

            var icon = new StringBuilder(32768);
            int iconIndex;
            link.GetIconLocation(icon, icon.Capacity, out iconIndex);

            target = Environment.ExpandEnvironmentVariables(path.ToString());

            string fileArguments;
            if (TryReadShortcutArgumentsFromFile(linkPath, out fileArguments))
                arguments = fileArguments;
            else
                arguments = args.ToString();

            iconLocation = icon.Length == 0 ? "" : icon + "," + iconIndex;
            return !String.IsNullOrWhiteSpace(target);
        }
        catch { return false; }
        finally
        {
            if (obj != null && Marshal.IsComObject(obj)) Marshal.FinalReleaseComObject(obj);
        }
    }

    private static bool TryReadShortcutArgumentsFromFile(string linkPath,
        out string arguments)
    {
        arguments = "";
        try
        {
            byte[] data = File.ReadAllBytes(linkPath);
            if (data.Length < 0x4C) return false;

            uint flags = BitConverter.ToUInt32(data, 0x14);
            int offset = 0x4C;

            if ((flags & 0x00000001) != 0)
            {
                if (offset + 2 > data.Length) return false;
                int size = BitConverter.ToUInt16(data, offset);
                offset += 2 + size;
                if (offset > data.Length) return false;
            }

            if ((flags & 0x00000002) != 0)
            {
                if (offset + 4 > data.Length) return false;
                uint size = BitConverter.ToUInt32(data, offset);
                if (size < 4 || offset + size > data.Length) return false;
                offset += (int)size;
            }

            bool unicode = (flags & 0x00000080) != 0;
            uint[] fields = { 0x04, 0x08, 0x10, 0x20, 0x40 };

            foreach (uint field in fields)
            {
                if ((flags & field) == 0) continue;
                if (offset + 2 > data.Length) return false;

                int chars = BitConverter.ToUInt16(data, offset);
                offset += 2;
                int bytes = unicode ? chars * 2 : chars;
                if (offset + bytes > data.Length) return false;

                if (field == 0x20)
                {
                    arguments = unicode
                        ? Encoding.Unicode.GetString(data, offset, bytes)
                        : Encoding.Default.GetString(data, offset, bytes);
                    return true;
                }
                offset += bytes;
            }
            return true;
        }
        catch { return false; }
    }

    // ---------- UI helpers ----------

    private static string Prompt(string label, string title, string initial)
    {
        using (var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(430, 112)
        })
        {
            var lbl = new Label { Text = label, AutoSize = true, Location = new Point(12, 15) };
            var box = new TextBox { Text = initial ?? "" };
            box.SetBounds(12, 36, 406, 23);

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
            ok.SetBounds(246, 73, 82, 27);
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            cancel.SetBounds(336, 73, 82, 27);

            form.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
            form.AcceptButton = ok;
            form.CancelButton = cancel;
            form.Shown += delegate { box.SelectAll(); box.Focus(); };

            return form.ShowDialog() == DialogResult.OK ? box.Text : null;
        }
    }

    private static void InvalidateGroupMenuCache(string groupName)
    {
        if (String.IsNullOrWhiteSpace(groupName))
            return;

        CachedGroupMenu cached;
        if (!GroupMenuCache.TryGetValue(groupName, out cached))
            return;

        GroupMenuCache.Remove(groupName);

        if (String.Equals(AttachedCachedGroup, groupName, StringComparison.OrdinalIgnoreCase))
        {
            AttachedCachedGroup = null;
            return;
        }

        if (cached != null && cached.Items != null)
        {
            foreach (ToolStripItem item in cached.Items)
                DeferredMenuDisposals.Enqueue(item);
            ScheduleDeferredMenuDisposal();
        }
    }

    private static void RefreshMenu()
    {
        // A submenu Click closes its owning ContextMenuStrip before this method
        // runs. Therefore MainMenu.Visible is normally false here. Restore from
        // the position captured while the main menu was still open.
        if (HaveLastMainMenuLocation)
        {
            ExactRestorePoint = LastMainMenuLocation;
            UseExactRestorePoint = true;
        }
        InvalidateGroupMenuCache(Group);

        if (MainMenu != null && MainMenu.Visible)
            MainMenu.Close();

        string groupToRefresh = Group;
        Form host = HostForm;
        if (host != null && !host.IsDisposed)
        {
            host.BeginInvoke((MethodInvoker)delegate
            {
                ShowRequestedGroup(groupToRefresh, true, ExactRestorePoint);
            });
        }
    }

    private static void CloseMenu(bool preserveRefresh)
    {
        if (MainMenu != null && MainMenu.Visible) MainMenu.Close();

        // HostForm intentionally remains alive. It owns the persistent WinForms
        // message loop and the reusable ContextMenuStrip.
    }

    private static void Error(string message, Exception ex)
    {
        LogError(message, ex);
        MessageBox.Show(message + "\r\n\r\n" + ex.Message, "Taskbar Groups",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static string SanitizeFileName(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            value = value.Replace(c.ToString(), "");
        return value.Trim();
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        string dir = Path.GetDirectoryName(path);
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        int n = 2;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, name + " (" + n++ + ")" + ext);
        } while (File.Exists(candidate));
        return candidate;
    }

    private static bool SamePath(string a, string b)
    {
        return String.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string s)
    {
        return "\"" + (s ?? "").Replace("\"", "\\\"") + "\"";
    }

    private static bool IsLayoutItem(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".heading" || ext == ".separator";
    }

    private static bool IsDarkMode()
    {
        try
        {
            object value = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return Convert.ToInt32(value) == 0;
        }
        catch { return false; }
    }

    private static void ApplyTheme(ToolStrip strip)
    {
        if (!IsDarkMode()) return;

        Color foreground = Color.FromArgb(240, 240, 240);
        strip.BackColor = Color.FromArgb(32, 32, 32);
        strip.ForeColor = foreground;
        strip.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());

        foreach (ToolStripItem item in strip.Items)
        {
            if (!(item is ToolStripSeparator))
                item.ForeColor = foreground;
        }
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        private readonly Color bg = Color.FromArgb(32, 32, 32);
        private readonly Color sel = Color.FromArgb(62, 62, 62);
        private readonly Color border = Color.FromArgb(80, 80, 80);

        public override Color ToolStripDropDownBackground { get { return bg; } }
        public override Color ImageMarginGradientBegin { get { return bg; } }
        public override Color ImageMarginGradientMiddle { get { return bg; } }
        public override Color ImageMarginGradientEnd { get { return bg; } }
        public override Color MenuBorder { get { return border; } }
        public override Color MenuItemBorder { get { return sel; } }
        public override Color MenuItemSelected { get { return sel; } }
        public override Color MenuItemSelectedGradientBegin { get { return sel; } }
        public override Color MenuItemSelectedGradientEnd { get { return sel; } }
        public override Color SeparatorDark { get { return border; } }
        public override Color SeparatorLight { get { return bg; } }
    }

    // ---------- Native shell ----------

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;

    private delegate IntPtr LowLevelMouseProc(
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelMouseProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const uint SHOP_FILEPATH = 0x00000002;
    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const int SW_SHOW = 5;
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpVerb;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpFile;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpParameters;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHObjectProperties(
        IntPtr hwnd,
        uint shopObjectType,
        string pszObjectName,
        string pszPropertyPage);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern uint ExtractIconEx(string szFileName, int nIconIndex,
        IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
