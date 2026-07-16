using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace QuickShot;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new QuickShotForm());
    }
}

internal sealed class QuickShotForm : Form
{
    private const int WhKeyboardLowLevel = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSystemKeyDown = 0x0104;
    private const int WmSystemKeyUp = 0x0105;
    private const int WmCaptureScreen = 0x8001;
    private const uint VkF11 = 0x7A;

    private readonly TextBox folderBox = new();
    private readonly Label statusLabel = new();
    private readonly NotifyIcon trayIcon = new();
    private readonly LowLevelKeyboardProc keyboardProc;
    private readonly string settingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuickShot",
        "settings.txt");

    private string screenshotFolder;
    private IntPtr keyboardHook;
    private Thread? hotkeyPollingThread;
    private volatile bool stopHotkeyPolling;
    private int captureQueuedForCurrentPress;
    private bool exiting;

    public QuickShotForm()
    {
        keyboardProc = KeyboardHookCallback;
        screenshotFolder = LoadFolder();

        Text = "QuickShot";
        Icon = SystemIcons.Application;
        ClientSize = new Size(540, 164);
        MinimumSize = new Size(480, 203);
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        var title = new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Location = new Point(16, 14),
            Text = "Press F11 anywhere to save a screenshot"
        };

        folderBox.Location = new Point(16, 48);
        folderBox.Size = new Size(398, 23);
        folderBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        folderBox.ReadOnly = true;
        folderBox.Text = screenshotFolder;

        var chooseButton = MakeButton("Choose Folder", new Point(422, 47), new Size(102, 25));
        chooseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        chooseButton.Click += (_, _) => ChooseFolder();

        var captureButton = MakeButton("Screenshot Now", new Point(16, 86), new Size(126, 31));
        captureButton.Click += (_, _) => CaptureScreen();

        var openButton = MakeButton("Open Folder", new Point(150, 86), new Size(108, 31));
        openButton.Click += (_, _) => OpenFolder();

        statusLabel.AutoEllipsis = true;
        statusLabel.Location = new Point(16, 130);
        statusLabel.Size = new Size(508, 22);
        statusLabel.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        statusLabel.ForeColor = Color.DimGray;
        statusLabel.Text = "Ready. Minimize to keep QuickShot in the system tray.";

        Controls.AddRange([title, folderBox, chooseButton, captureButton, openButton, statusLabel]);

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Screenshot now", null, (_, _) => CaptureScreen());
        trayMenu.Items.Add("Choose folder...", null, (_, _) => ChooseFolder());
        trayMenu.Items.Add("Open QuickShot", null, (_, _) => RestoreWindow());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, (_, _) => ExitApplication());

        trayIcon.Icon = Icon;
        trayIcon.Text = "QuickShot - F11 captures the screen";
        trayIcon.ContextMenuStrip = trayMenu;
        trayIcon.Visible = true;
        trayIcon.DoubleClick += (_, _) => RestoreWindow();

        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
                Hide();
        };
        FormClosing += OnFormClosing;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        keyboardHook = SetWindowsHookEx(
            WhKeyboardLowLevel,
            keyboardProc,
            GetModuleHandle(null),
            0);
        if (keyboardHook == IntPtr.Zero)
        {
            statusLabel.Text = "Ready. F11 game compatibility mode is active.";
        }

        stopHotkeyPolling = false;
        IntPtr windowHandle = Handle;
        hotkeyPollingThread = new Thread(() => PollF11(windowHandle))
        {
            IsBackground = true,
            Name = "QuickShot F11 monitor",
            Priority = ThreadPriority.AboveNormal
        };
        hotkeyPollingThread.Start();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        stopHotkeyPolling = true;
        hotkeyPollingThread?.Join(500);
        hotkeyPollingThread = null;

        if (keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(keyboardHook);
            keyboardHook = IntPtr.Zero;
        }

        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmCaptureScreen)
        {
            CaptureScreen();
            return;
        }

        base.WndProc(ref m);
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && unchecked((uint)Marshal.ReadInt32(lParam)) == VkF11)
        {
            int message = wParam.ToInt32();
            if (message is WmKeyDown or WmSystemKeyDown)
            {
                QueueCapture(Handle);
                return new IntPtr(1);
            }

            if (message is WmKeyUp or WmSystemKeyUp)
            {
                Interlocked.Exchange(ref captureQueuedForCurrentPress, 0);
                return new IntPtr(1);
            }
        }

        return CallNextHookEx(keyboardHook, code, wParam, lParam);
    }

    private void PollF11(IntPtr windowHandle)
    {
        while (!stopHotkeyPolling)
        {
            bool isDown = (GetAsyncKeyState(unchecked((int)VkF11)) & 0x8000) != 0;
            if (isDown)
                QueueCapture(windowHandle);
            else
                Interlocked.Exchange(ref captureQueuedForCurrentPress, 0);

            Thread.Sleep(8);
        }
    }

    private void QueueCapture(IntPtr windowHandle)
    {
        if (Interlocked.CompareExchange(ref captureQueuedForCurrentPress, 1, 0) != 0)
            return;

        if (!PostMessage(windowHandle, WmCaptureScreen, IntPtr.Zero, IntPtr.Zero))
            Interlocked.Exchange(ref captureQueuedForCurrentPress, 0);
    }

    private static Button MakeButton(string text, Point location, Size size) => new()
    {
        Text = text,
        Location = location,
        Size = size,
        UseVisualStyleBackColor = true
    };

    private void CaptureScreen()
    {
        try
        {
            Directory.CreateDirectory(screenshotFolder);
            Rectangle bounds = SystemInformation.VirtualScreen;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                throw new InvalidOperationException("Windows did not report a valid screen area.");

            string outputPath = NextScreenshotPath();
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    bounds.Left,
                    bounds.Top,
                    0,
                    0,
                    bounds.Size,
                    CopyPixelOperation.SourceCopy);
            }

            bitmap.Save(outputPath, ImageFormat.Png);
            statusLabel.ForeColor = Color.DarkGreen;
            statusLabel.Text = $"Saved: {Path.GetFileName(outputPath)}";
        }
        catch (Exception ex)
        {
            statusLabel.ForeColor = Color.Firebrick;
            statusLabel.Text = $"Screenshot failed: {ex.Message}";
            if (!Visible)
            {
                trayIcon.BalloonTipTitle = "QuickShot failed";
                trayIcon.BalloonTipText = ex.Message;
                trayIcon.ShowBalloonTip(3000);
            }
        }
    }

    private string NextScreenshotPath()
    {
        string timestamp = DateTime.Now.ToString(
            "yyyy-MM-dd_HH-mm-ss-fff",
            CultureInfo.InvariantCulture);
        string path = Path.Combine(screenshotFolder, $"Screenshot_{timestamp}.png");
        for (int suffix = 2; File.Exists(path); suffix++)
            path = Path.Combine(screenshotFolder, $"Screenshot_{timestamp}_{suffix}.png");
        return path;
    }

    private void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose where QuickShot saves screenshots",
            SelectedPath = Directory.Exists(screenshotFolder) ? screenshotFolder : string.Empty,
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
            return;

        screenshotFolder = Path.GetFullPath(dialog.SelectedPath);
        Directory.CreateDirectory(screenshotFolder);
        folderBox.Text = screenshotFolder;
        SaveFolder();
        statusLabel.ForeColor = Color.DimGray;
        statusLabel.Text = "Screenshot folder updated.";
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(screenshotFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", screenshotFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            statusLabel.ForeColor = Color.Firebrick;
            statusLabel.Text = $"Could not open folder: {ex.Message}";
        }
    }

    private string LoadFolder()
    {
        string fallback = Path.Combine(AppContext.BaseDirectory, "Screenshots");

        try
        {
            if (!File.Exists(settingsFile))
                return fallback;

            string saved = File.ReadAllText(settingsFile).Trim();
            return string.IsNullOrWhiteSpace(saved) ? fallback : Path.GetFullPath(saved);
        }
        catch
        {
            return fallback;
        }
    }

    private void SaveFolder()
    {
        try
        {
            string? parent = Path.GetDirectoryName(settingsFile);
            if (parent is not null)
                Directory.CreateDirectory(parent);
            File.WriteAllText(settingsFile, screenshotFolder);
        }
        catch (Exception ex)
        {
            statusLabel.ForeColor = Color.DarkOrange;
            statusLabel.Text = $"Folder selected, but the preference could not be saved: {ex.Message}";
        }
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        exiting = true;
        Close();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
            exiting = true;

        trayIcon.Visible = false;
        trayIcon.Dispose();
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        LowLevelKeyboardProc hookProc,
        IntPtr moduleHandle,
        uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hookHandle,
        int code,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
