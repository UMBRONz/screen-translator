using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace ScreenTranslator;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "Local\\ScreenTranslator.FirstVersion", out bool first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write(e.Exception);
        if (args.Contains("--smoke-test")) { Settings.Load(); return; }
        Application.Run(new TrayContext());
    }
}

internal sealed class TrayContext : ApplicationContext
{
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer poll = new() { Interval = 650 };
    string? lastHash;
    bool busy;
    Settings settings = Settings.Load();
    OverlayForm? overlay;

    public TrayContext()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Перезагрузить", null, (_, _) => Reload());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());
        tray = new NotifyIcon {
            Icon = SystemIcons.Information, Text = "Screen Translator — готов",
            Visible = true, ContextMenuStrip = menu
        };
        tray.DoubleClick += (_, _) => ShowHint();
        poll.Tick += async (_, _) => await CheckClipboard();
        poll.Start();
        tray.ShowBalloonTip(2500, "Screen Translator", "Нажмите Win+Shift+S и выделите область. Перевод появится автоматически.", ToolTipIcon.Info);
    }

    void ShowHint() => tray.ShowBalloonTip(2500, "Screen Translator", "Сделайте снимок области через Win+Shift+S. Настройки: ScreenTranslator.settings.json", ToolTipIcon.Info);
    void Reload() { settings = Settings.Load(); overlay?.Close(); overlay = null; lastHash = null; tray.ShowBalloonTip(1500, "Screen Translator", "Настройки перезагружены", ToolTipIcon.Info); }

    async Task CheckClipboard()
    {
        if (busy || !Clipboard.ContainsImage()) return;
        Bitmap? bmp = null;
        try
        {
            using var src = Clipboard.GetImage();
            if (src is null || src.Width < 8 || src.Height < 8) return;
            bmp = new Bitmap(src);
            string hash = Hash(bmp);
            if (hash == lastHash) return;
            lastHash = hash; busy = true; tray.Text = "Screen Translator — распознавание…";
            overlay?.Close();
            overlay = new OverlayForm([], bmp.Size, settings);
            overlay.FormClosed += (_, _) => overlay = null;
            overlay.Show();
            var blocks = await OcrService.RecognizeAsync(bmp, settings);
            if (blocks.Count == 0) { overlay?.Close(); Notify("Текст на снимке не найден", ToolTipIcon.Warning); return; }
            tray.Text = "Screen Translator — перевод…";
            blocks = await Translator.TranslateAsync(blocks, settings);
            overlay?.DisplayBlocks(blocks);
        }
        catch (ExternalException) { /* clipboard temporarily locked */ }
        catch (Exception ex) { overlay?.Close(); Log.Write(ex); Notify("Не удалось обработать снимок. Подробности в ScreenTranslator.log", ToolTipIcon.Error); }
        finally { bmp?.Dispose(); busy = false; tray.Text = "Screen Translator — готов"; }
    }

    static string Hash(Bitmap bmp) { using var ms = new MemoryStream(); bmp.Save(ms, ImageFormat.Png); return Convert.ToHexString(SHA256.HashData(ms.ToArray())); }
    void Notify(string text, ToolTipIcon icon) => tray.ShowBalloonTip(2500, "Screen Translator", text, icon);
    protected override void ExitThreadCore() { poll.Stop(); overlay?.Close(); tray.Visible = false; tray.Dispose(); base.ExitThreadCore(); }
}

internal enum BlockKind { Heading, Text, Bullet }
internal record TextBlock(string Text, BlockKind Kind);

internal static class OcrService
{
    sealed record RawLine(string Text, double Height, double Left, double Top);
    sealed record Candidate(string Language, List<RawLine> Lines, double Score);

    public static async Task<List<TextBlock>> RecognizeAsync(Bitmap bitmap, Settings settings)
    {
        var bytes = new MemoryStream(); bitmap.Save(bytes, ImageFormat.Png);
        using var ras = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(ras)) { writer.WriteBytes(bytes.ToArray()); await writer.StoreAsync(); await writer.FlushAsync(); writer.DetachStream(); }
        ras.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(ras);
        var softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var candidates = new List<Candidate>();
        foreach (var tag in settings.OcrLanguages)
        {
            try
            {
                var lang = new Language(tag);
                if (!OcrEngine.IsLanguageSupported(lang)) continue;
                var engine = OcrEngine.TryCreateFromLanguage(lang);
                if (engine is null) continue;
                var result = await engine.RecognizeAsync(softwareBitmap);
                var engineLines = result.Lines
                    .Select(l => ToRawLine(l))
                    .Where(x => x.Text.Length > 0).ToList();
                if (engineLines.Count > 0) candidates.Add(new Candidate(tag, engineLines, Quality(engineLines, tag)));
            }
            catch (Exception ex) { Log.Write(ex); }
        }
        if (candidates.Count == 0)
        {
            var fallback = OcrEngine.TryCreateFromUserProfileLanguages();
            if (fallback is null) throw new InvalidOperationException("Компонент Windows OCR недоступен. Установите языковые пакеты English OCR и Russian OCR в Параметрах Windows.");
            var result = await fallback.RecognizeAsync(softwareBitmap);
            var fallbackLines = result.Lines.Select(ToRawLine).Where(x => x.Text.Length > 0).ToList();
            candidates.Add(new Candidate("profile", fallbackLines, Quality(fallbackLines, "profile")));
        }
        var primary = candidates.OrderByDescending(x => x.Score).First();
        var lines = primary.Lines.Select(line =>
        {
            var alternatives = candidates.SelectMany(c => c.Lines.Select(l => (Line: l, Language: c.Language)))
                .Where(x => Math.Abs(x.Line.Top - line.Top) <= Math.Max(7, line.Height * .65));
            return alternatives.OrderByDescending(x => LineQuality(x.Line.Text, x.Language)).FirstOrDefault().Line ?? line;
        }).ToList();
        if (lines.Count == 0) return [];
        double median = lines.Select(x => x.Height).Order().ElementAt(lines.Count / 2);
        double leftEdge = lines.Select(x => x.Left).Order().ElementAt(Math.Max(0, lines.Count / 5));
        var indented = lines.Select(x => x.Left > leftEdge + Math.Max(16, median * .8)).ToArray();
        return lines.Select((x, i) => new TextBlock(Clean(x.Text), Kind(x.Text, x.Height, median, i,
            indented[i] && ((i > 0 && indented[i - 1]) || (i + 1 < indented.Length && indented[i + 1]))))).ToList();
    }

    static RawLine ToRawLine(OcrLine line)
    {
        if (line.Words.Count == 0) return new RawLine(line.Text.Trim(), 0, 0, 0);
        double left = line.Words.Min(w => w.BoundingRect.Left);
        double top = line.Words.Min(w => w.BoundingRect.Top);
        return new RawLine(line.Text.Trim(), line.Words.Average(w => w.BoundingRect.Height), left, top);
    }

    static double Quality(List<RawLine> lines, string language)
    {
        string text = string.Join(' ', lines.Select(x => x.Text));
        int latin = Regex.Matches(text, @"[A-Za-z]").Count;
        int cyrillic = Regex.Matches(text, @"[А-Яа-яЁё]").Count;
        int letters = latin + cyrillic;
        int mixedWords = Regex.Split(text, @"[^\p{L}]+")
            .Count(w => Regex.IsMatch(w, @"[A-Za-z]") && Regex.IsMatch(w, @"[А-Яа-яЁё]"));
        int suspicious = Regex.Matches(text, @"(?<=\p{L})\d|\d(?=\p{L})").Count;
        int isolated = lines.Count(x => x.Text.Length <= 2 && !Regex.IsMatch(x.Text, @"^[AIЯИВК]$"));
        double scriptBonus = language.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? cyrillic * .18
            : language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? latin * .18 : 0;
        return letters + scriptBonus - mixedWords * 18 - suspicious * 5 - isolated * 4;
    }

    static double LineQuality(string text, string language)
    {
        int latin = Regex.Matches(text, @"[A-Za-z]").Count;
        int cyrillic = Regex.Matches(text, @"[А-Яа-яЁё]").Count;
        int mixedWords = Regex.Split(text, @"[^\p{L}]+").Count(w => Regex.IsMatch(w, @"[A-Za-z]") && Regex.IsMatch(w, @"[А-Яа-яЁё]"));
        int suspicious = Regex.Matches(text, @"(?<=\p{L})\d|\d(?=\p{L})").Count;
        double scriptBonus = language.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? cyrillic * .22
            : language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? latin * .22 : 0;
        return latin + cyrillic + scriptBonus - mixedWords * 22 - suspicious * 7;
    }

    static string Clean(string s) => Regex.Replace(s, @"\s+", " ").Trim();
    static BlockKind Kind(string text, double h, double median, int index, bool looksIndentedList)
    {
        if (looksIndentedList || Regex.IsMatch(text, @"^([•●▪◦*-]|\d+[.)])\s*")) return BlockKind.Bullet;
        if ((h > median * 1.22 || index == 0 && text.Length < 80) && !Regex.IsMatch(text, @"[.!?]$")) return BlockKind.Heading;
        return BlockKind.Text;
    }
}

internal static class Translator
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    static readonly Regex Cyrillic = new(@"[А-Яа-яЁё]", RegexOptions.Compiled);
    static readonly Regex Latin = new(@"[A-Za-z]", RegexOptions.Compiled);

    public static async Task<List<TextBlock>> TranslateAsync(List<TextBlock> blocks, Settings s)
    {
        var result = new List<TextBlock>();
        foreach (var block in blocks)
        {
            if (!NeedsTranslation(block.Text)) { result.Add(block); continue; }
            try { result.Add(block with { Text = await TranslateText(block.Text, s) }); }
            catch (Exception ex) { Log.Write(ex); result.Add(block); }
        }
        return result;
    }

    static bool NeedsTranslation(string text)
    {
        int ru = Cyrillic.Matches(text).Count, latin = Latin.Matches(text).Count;
        return latin > 0 && ru < Math.Max(3, latin / 2);
    }

    static async Task<string> TranslateText(string text, Settings s)
    {
        string prefix = "";
        var m = Regex.Match(text, @"^([•●▪◦*-]|\d+[.)])\s*");
        if (m.Success) { prefix = m.Value; text = text[m.Length..]; }
        if (s.TranslationProvider.Equals("libretranslate", StringComparison.OrdinalIgnoreCase))
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, s.LibreTranslateUrl) { Content = JsonContent.Create(new { q = text, source = "auto", target = "ru", format = "text", api_key = s.LibreTranslateApiKey }) };
            using var resp = await Http.SendAsync(req); resp.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return prefix + json.RootElement.GetProperty("translatedText").GetString();
        }
        string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=ru&dt=t&q=" + Uri.EscapeDataString(text);
        using var response = await Http.GetAsync(url); response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var sb = new StringBuilder(); foreach (var part in doc.RootElement[0].EnumerateArray()) if (part[0].ValueKind == JsonValueKind.String) sb.Append(part[0].GetString());
        return prefix + (sb.Length > 0 ? sb.ToString() : text);
    }
}

internal sealed class OverlayForm : Form
{
    readonly List<TextBlock> blocks;
    readonly Settings settings;
    readonly System.Windows.Forms.Timer typing = new();
    readonly System.Windows.Forms.Timer loadingAnimation = new() { Interval = 16 };
    int blockIndex, charIndex;
    int scrollOffset;
    int typingChunk = 12;
    int loadingAngle;
    bool loading = true;
    readonly List<TextBlock> visible = [];

    public OverlayForm(List<TextBlock> blocks, Size sourceSize, Settings settings)
    {
        this.blocks = blocks; this.settings = settings;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        BackColor = Color.FromArgb(28, 34, 44); Opacity = 1;
        Padding = new Padding(26); DoubleBuffered = true; KeyPreview = true;
        int maxW = Math.Min(settings.MaxOverlayWidth, Screen.FromPoint(Cursor.Position).WorkingArea.Width - 24);
        Width = Math.Clamp(sourceSize.Width, 360, maxW);
        Height = Math.Clamp(sourceSize.Height, 170, Screen.FromPoint(Cursor.Position).WorkingArea.Height - 24);
        MinimumSize = new Size(320, 150);
        PositionNearCursor();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) Native.ReleaseCaptureAndMove(Handle); };
        MouseWheel += (_, e) => ScrollBy(-(e.Delta / 120) * 64);
        DoubleClick += (_, _) => Close();
        typing.Interval = Math.Clamp(settings.TypingDelayMs / 3, 1, 100);
        typing.Tick += (_, _) => NextCharacter();
        loadingAnimation.Tick += (_, _) => { loadingAngle = (loadingAngle + 7) % 360; Invalidate(); };
        Shown += (_, _) => { Native.EnableGlassBackdrop(Handle, settings.OverlayOpacity); Native.EnableRoundedCorners(Handle); loadingAnimation.Start(); };
    }

    public void DisplayBlocks(List<TextBlock> result)
    {
        blocks.Clear(); blocks.AddRange(result);
        visible.Clear(); blockIndex = 0; charIndex = 0; scrollOffset = 0;
        int totalCharacters = Math.Max(1, result.Sum(x => x.Text.Length));
        typingChunk = Math.Clamp((int)Math.Ceiling(totalCharacters / 32d), 12, 96);
        loading = false; loadingAnimation.Stop(); typing.Start(); Invalidate();
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000080; return cp; } }
    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084, HTCLIENT = 1;
        const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
        const int HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        base.WndProc(ref m);
        if (m.Msg != WM_NCHITTEST || m.Result != (IntPtr)HTCLIENT) return;

        long packed = m.LParam.ToInt64();
        var point = PointToClient(new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF)));
        const int grip = 9;
        bool left = point.X <= grip, right = point.X >= ClientSize.Width - grip;
        bool top = point.Y <= grip, bottom = point.Y >= ClientSize.Height - grip;
        if (left && top) m.Result = (IntPtr)HTTOPLEFT;
        else if (right && top) m.Result = (IntPtr)HTTOPRIGHT;
        else if (left && bottom) m.Result = (IntPtr)HTBOTTOMLEFT;
        else if (right && bottom) m.Result = (IntPtr)HTBOTTOMRIGHT;
        else if (left) m.Result = (IntPtr)HTLEFT;
        else if (right) m.Result = (IntPtr)HTRIGHT;
        else if (top) m.Result = (IntPtr)HTTOP;
        else if (bottom) m.Result = (IntPtr)HTBOTTOM;
    }
    void PositionNearCursor()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        int x = Cursor.Position.X - Width / 2, y = Cursor.Position.Y - 20;
        Location = new Point(Math.Clamp(x, area.Left + 12, area.Right - Width - 12), Math.Clamp(y, area.Top + 12, area.Bottom - Height - 12));
    }
    void NextCharacter()
    {
        for (int step = 0; step < typingChunk; step++)
        {
            if (blockIndex >= blocks.Count) { typing.Stop(); break; }
            var source = blocks[blockIndex];
            if (visible.Count <= blockIndex) visible.Add(source with { Text = "" });
            charIndex++;
            visible[blockIndex] = source with { Text = source.Text[..Math.Min(charIndex, source.Text.Length)] };
            if (charIndex >= source.Text.Length) { blockIndex++; charIndex = 0; }
        }
        Invalidate();
    }
    void ScrollBy(int amount)
    {
        scrollOffset = Math.Clamp(scrollOffset + amount, 0, Math.Max(0, MeasureContentHeight() - (Height - 48)));
        Invalidate();
    }
    int MeasureContentHeight()
    {
        int total = 0;
        using var g = CreateGraphics();
        foreach (var b in visible)
        {
            bool heading = b.Kind == BlockKind.Heading;
            using var font = new Font(settings.FontFamily, heading ? settings.FontSize + 4 : settings.FontSize, heading ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            string text = b.Kind == BlockKind.Bullet && !Regex.IsMatch(b.Text, @"^([•●▪◦*-]|\d+[.)])") ? "• " + b.Text : b.Text;
            total += TextRenderer.MeasureText(g, text, font, new Size(Width - 52, 10000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height + (heading ? 10 : 7);
        }
        return total;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var border = new Pen(Color.FromArgb(95, 255, 255, 255), 1); e.Graphics.DrawPath(border, RoundedRect(new Rectangle(1, 1, Width - 3, Height - 3), 21));
        if (loading)
        {
            DrawLoading(e.Graphics);
            return;
        }
        int y = 22 - scrollOffset;
        foreach (var b in visible)
        {
            bool heading = b.Kind == BlockKind.Heading;
            using var font = new Font(settings.FontFamily, heading ? settings.FontSize + 4 : settings.FontSize, heading ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            string text = b.Kind == BlockKind.Bullet && !Regex.IsMatch(b.Text, @"^([•●▪◦*-]|\d+[.)])") ? "• " + b.Text : b.Text;
            var rect = new Rectangle(26, y, Width - 52, Height - y - 18);
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
            var size = TextRenderer.MeasureText(e.Graphics, text, font, new Size(rect.Width, 1000), flags);
            TextRenderer.DrawText(e.Graphics, text, font, rect, Color.FromArgb(245, 248, 252), flags);
            y += size.Height + (heading ? 10 : 7); if (y > Height - 25) break;
        }
        int contentHeight = MeasureContentHeight(), viewport = Height - 48;
        if (contentHeight > viewport)
        {
            int trackHeight = Height - 58;
            int thumbHeight = Math.Max(28, trackHeight * viewport / contentHeight);
            int maxScroll = Math.Max(1, contentHeight - viewport);
            int thumbY = 24 + (trackHeight - thumbHeight) * scrollOffset / maxScroll;
            using var thumb = new SolidBrush(Color.FromArgb(105, 225, 235, 248));
            using var thumbPath = RoundedRect(new Rectangle(Width - 9, thumbY, 4, thumbHeight), 2);
            e.Graphics.FillPath(thumb, thumbPath);
        }
        using var hint = new Font("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
        TextRenderer.DrawText(e.Graphics, "двойной щелчок / Esc — закрыть", hint, new Point(27, Height - 18), Color.FromArgb(145, 220, 228, 240), TextFormatFlags.NoPadding);
    }
    void DrawLoading(Graphics g)
    {
        int diameter = Math.Clamp(Math.Min(Width, Height) / 3, 72, 132);
        var spinner = new Rectangle((Width - diameter) / 2, (Height - diameter) / 2 - 18, diameter, diameter);
        using var track = new Pen(Color.FromArgb(38, 235, 241, 250), 8) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var glow = new Pen(Color.FromArgb(55, 123, 211, 255), 15) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var active = new Pen(Color.FromArgb(240, 239, 248, 255), 8) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawEllipse(track, spinner);
        g.DrawArc(glow, spinner, loadingAngle, 92);
        g.DrawArc(active, spinner, loadingAngle, 92);
        using var font = new Font(settings.FontFamily, 15, FontStyle.Regular, GraphicsUnit.Pixel);
        const string text = "Распознаю и перевожу…";
        var size = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, text, font, new Point((Width - size.Width) / 2, spinner.Bottom + 18), Color.FromArgb(225, 244, 248, 255), TextFormatFlags.NoPadding);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { typing.Dispose(); loadingAnimation.Dispose(); }
        base.Dispose(disposing);
    }
    static GraphicsPath RoundedRect(Rectangle r, int radius) { int d = radius * 2; var p = new GraphicsPath(); p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p; }
}

internal sealed class Settings
{
    public string[] OcrLanguages { get; set; } = ["en-US", "ru-RU"];
    public string TranslationProvider { get; set; } = "google";
    public string LibreTranslateUrl { get; set; } = "https://libretranslate.com/translate";
    public string LibreTranslateApiKey { get; set; } = "";
    public double OverlayOpacity { get; set; } = .93;
    public int TypingDelayMs { get; set; } = 12;
    public int MaxOverlayWidth { get; set; } = 980;
    public string FontFamily { get; set; } = "Segoe UI Variable Text";
    public float FontSize { get; set; } = 18;
    public static string PathName => Path.Combine(AppContext.BaseDirectory, "ScreenTranslator.settings.json");
    public static Settings Load()
    {
        try { if (File.Exists(PathName)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathName), JsonOptions) ?? new(); } catch (Exception ex) { Log.Write(ex); }
        var s = new Settings(); try { File.WriteAllText(PathName, JsonSerializer.Serialize(s, JsonOptions)); } catch { } return s;
    }
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}

internal static class Log
{
    public static void Write(Exception ex) { try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ScreenTranslator.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n"); } catch { } }
}

internal static class Native
{
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    public static void ReleaseCaptureAndMove(IntPtr h) { ReleaseCapture(); SendMessage(h, 0xA1, (IntPtr)2, IntPtr.Zero); }
    public static void EnableGlassBackdrop(IntPtr h, double opacity)
    {
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_SYSTEMBACKDROP_TYPE = 38;
        int enabled = 1, transientWindow = 3;
        try
        {
            DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
            DwmSetWindowAttribute(h, DWMWA_SYSTEMBACKDROP_TYPE, ref transientWindow, sizeof(int));
            int alpha = Math.Clamp((int)(opacity * 105), 55, 110);
            var accent = new AccentPolicy { AccentState = 4, GradientColor = (alpha << 24) | (44 << 16) | (36 << 8) | 28 };
            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WindowCompositionAttributeData { Attribute = 19, Data = ptr, SizeOfData = size };
                SetWindowCompositionAttribute(h, ref data);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch { }
    }
    public static void EnableRoundedCorners(IntPtr h)
    {
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;
        int preference = DWMWCP_ROUND;
        try { DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int)); } catch { }
    }
    [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int AccentState, AccentFlags, GradientColor, AnimationId; }
    [StructLayout(LayoutKind.Sequential)] struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }
}
