using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DotaTikTok;

/// <summary>
/// Бесрамочное окно поверх Dota 2 с встроенным TikTok.
/// Ctrl+Shift+T — показать/скрыть (работает, даже когда окно скрыто).
/// </summary>
public sealed class OverlayForm : Form
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;
    private const int HOTKEY_ID = 1;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_T = 0x54;

    private const string StartUrl = "https://www.tiktok.com/";

    // TikTok отдаёт WAF-заглушку на стандартный UA WebView2/Edge, поэтому притворяемся обычным Chrome.
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DotaTikTok");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");
    private static readonly string WebViewDataDir = Path.Combine(SettingsDir, "webview");

    private readonly WebView2 _web = new()
    {
        Dock = DockStyle.Fill,
        DefaultBackgroundColor = Color.Black,
    };

    private readonly Panel _header = new()
    {
        Dock = DockStyle.Top,
        Height = 34,
        BackColor = Color.FromArgb(24, 24, 24),
    };

    private readonly Label _title = new()
    {
        Text = "TikTok  ·  Ctrl+Shift+T  ·  Esc — скрыть",
        AutoSize = false,
        Dock = DockStyle.Fill,
        ForeColor = Color.Gainsboro,
        Font = new Font("Segoe UI", 8.5f),
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8, 0, 0, 0),
    };

    private readonly Panel _grip = new()
    {
        Size = new Size(18, 18),
        BackColor = Color.FromArgb(24, 24, 24),
        Cursor = Cursors.SizeNWSE,
        Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
    };

    private readonly NotifyIcon _tray = new()
    {
        Icon = SystemIcons.Application,
        Text = "DotaTikTok (Ctrl+Shift+T)",
        Visible = true,
    };

    private bool _webViewReady;
    private bool _muted;
    private bool _hotkeyRegistered;

    private bool _gripDragging;
    private Point _gripStartCursor;
    private Size _gripStartSize;

    public OverlayForm()
    {
        // Полностью кастомное окно: без рамки, поверх всех, не в панели задач.
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(280, 380);
        BackColor = Color.Black;
        Text = "DotaTikTok";
        KeyPreview = true;

        BuildHeaderButtons();

        var available = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Size = new Size(600, 740);
        Location = new Point(available.Right - Width - 24, available.Top + 60);

        // Порядок добавления важен: Fill добавляется первым.
        Controls.Add(_web);
        Controls.Add(_header);
        Controls.Add(_grip);
        _grip.BringToFront();
        _grip.Location = new Point(ClientSize.Width - _grip.Width, ClientSize.Height - _grip.Height);

        _header.Controls.Add(_title);
        _header.MouseDown += DragWindow;
        _title.MouseDown += DragWindow;
        _grip.MouseDown += GripMouseDown;
        _grip.MouseMove += GripMouseMove;
        _grip.MouseUp += (_, _) => _gripDragging = false;
        _grip.Paint += DrawGrip;

        _tray.DoubleClick += (_, _) => ToggleVisible();
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
                _tray.ContextMenuStrip?.Show(Cursor.Position);
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Показать / скрыть", null, (_, _) => ToggleVisible());
        menu.Items.Add("Перезагрузить страницу", null, (_, _) => _web.Reload());
        menu.Items.Add("Сбросить вход", null, (_, _) => ResetLogin());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Close());
        _tray.ContextMenuStrip = menu;

        Load += async (_, _) =>
        {
            LoadSettings();
            await InitWebViewAsync();
        };

        Resize += (_, _) =>
        {
            _grip.Location = new Point(ClientSize.Width - _grip.Width, ClientSize.Height - _grip.Height);
            _grip.BringToFront();
        };

        FormClosing += (_, _) => SaveSettings();
        FormClosed += (_, _) =>
        {
            _tray.Visible = false;
            _tray.Dispose();
        };
    }

    private void BuildHeaderButtons()
    {
        Label Button(string text, string tooltip, Action onClick)
        {
            var b = new Label
            {
                Text = text,
                AutoSize = false,
                Size = new Size(26, 34),
                Dock = DockStyle.Right,
                ForeColor = Color.Gainsboro,
                Font = new Font("Segoe UI", 10f),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
            };
            var tip = new ToolTip();
            tip.SetToolTip(b, tooltip);
            b.Click += (_, _) => onClick();
            b.MouseEnter += (_, _) => b.ForeColor = Color.White;
            b.MouseLeave += (_, _) => b.ForeColor = Color.Gainsboro;
            return b;
        }

        var close = Button("✕", "Скрыть", () => Hide());
        var reload = Button("⟳", "Перезагрузить", () => _web.Reload());
        var mute = Button("🔇", "Выключить звук", () => ToggleSound());

        // DockStyles.Right добавляются в порядке: первый добавленный — самый правый.
        _header.Controls.Add(close);
        _header.Controls.Add(reload);
        _header.Controls.Add(mute);
    }

    private async Task InitWebViewAsync()
    {
        Directory.CreateDirectory(WebViewDataDir);

        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments =
                "--autoplay-policy=no-user-gesture-required --disable-blink-features=AutomationControlled",
        };
        var env = await CoreWebView2Environment.CreateAsync(null, WebViewDataDir, options);
        await _web.EnsureCoreWebView2Async(env);

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;

        core.Settings.UserAgent = UserAgent;

        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            core.Navigate(e.Uri);
        };

        // Esc внутри страницы прячет окно.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            "window.addEventListener('keydown', e => { if (e.key === 'Escape') " +
            "window.chrome.webview.postMessage('hide'); });");

        core.WebMessageReceived += (_, e) =>
        {
            if (e.TryGetWebMessageAsString() == "hide")
                Hide();
        };

        _webViewReady = true;
        core.Navigate(StartUrl);

        if (_muted)
            await ApplyMuteAsync(true);
    }

    private async Task ApplyMuteAsync(bool muted)
    {
        if (!_webViewReady)
            return;

        var script = muted
            ? """
              (() => {
                const mute = () => document.querySelectorAll('video').forEach(v => v.muted = true);
                mute();
                if (!window.__dotaTikTokMuteWatch) {
                  window.__dotaTikTokMuteWatch = setInterval(mute, 1500);
                  new MutationObserver(mute).observe(document.documentElement,
                      { childList: true, subtree: true });
                }
              })();
              """
            : """
              (() => {
                clearInterval(window.__dotaTikTokMuteWatch);
                window.__dotaTikTokMuteWatch = null;
                document.querySelectorAll('video').forEach(v => v.muted = false);
              })();
              """;

        try { await _web.ExecuteScriptAsync(script); } catch { /* страница ещё не готова */ }
    }

    private void ToggleSound()
    {
        _muted = !_muted;
        _ = ApplyMuteAsync(_muted);
        _title.Text = _muted ? "TikTok  ·  звук выключен  ·  Ctrl+Shift+T" : "TikTok  ·  Ctrl+Shift+T  ·  Esc — скрыть";
    }

    private void ResetLogin()
    {
        if (!_webViewReady)
            return;
        _web.CoreWebView2.CookieManager.DeleteAllCookies();
        _web.CoreWebView2.Navigate(StartUrl);
    }

    private void ToggleVisible()
    {
        if (Visible)
        {
            Hide();
            return;
        }
        Show();
        Activate();
        _web.Focus();
    }

    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
    }

    private void GripMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;
        _gripDragging = true;
        _gripStartCursor = Cursor.Position;
        _gripStartSize = Size;
    }

    private void GripMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_gripDragging)
            return;
        int dx = Cursor.Position.X - _gripStartCursor.X;
        int dy = Cursor.Position.Y - _gripStartCursor.Y;
        Size = new Size(
            Math.Max(MinimumSize.Width, _gripStartSize.Width + dx),
            Math.Max(MinimumSize.Height, _gripStartSize.Height + dy));
    }

    private void DrawGrip(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(110, 110, 110), 1.2f);
        for (var i = 1; i <= 3; i++)
        {
            var offset = i * 5;
            e.Graphics.DrawLine(pen, _grip.Width - offset, _grip.Height - 2,
                _grip.Width - 2, _grip.Height - offset);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hotkeyRegistered = RegisterHotKey(Handle, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_T);
        if (!_hotkeyRegistered)
            _title.Text = "Горячая клавиша занята — смотрите в трее";
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_hotkeyRegistered)
            UnregisterHotKey(Handle, HOTKEY_ID);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
        {
            ToggleVisible();
            return;
        }
        base.WndProc(ref m);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Hide();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return;
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            if (s is null)
                return;

            var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            Size = new Size(
                Math.Clamp(s.Width, MinimumSize.Width, screen.Width),
                Math.Clamp(s.Height, MinimumSize.Height, screen.Height));
            var loc = new Point(s.X, s.Y);
            if (!screen.Contains(loc))
                loc = new Point(screen.Right - Width - 24, screen.Top + 60);
            Location = loc;

            _muted = s.Muted;
        }
        catch { /* битый файл настроек — работаем на дефолтах */ }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var s = new Settings(Width, Height, Location.X, Location.Y, _muted);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private sealed record Settings(int Width, int Height, int X, int Y, bool Muted);
}
