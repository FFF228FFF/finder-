using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace ArtFinder
{
    public partial class MainWindow : Window
    {
        // ──────── Состояние ────────
        private string _currentSite = "e621";
        private string _savePath = "";
        private string _mainTag = "";
        private bool _lockCropSize = false;
        private double _lockedW = 300, _lockedH = 300;

        // Учётные данные
        private string _e621Login = "";
        private string _e621ApiKey = "";
        private string _rule34Login = "";
        private string _rule34ApiKey = "";

        // Кешированный Base64 для e621 Basic Auth (WebResourceRequested + HttpClient)
        private string _cachedBase64Auth = "";

        // Числовой user_id для rule34 API
        private string _rule34NumericUserId = "";

        // Блокировщик рекламы (EasyList + EasyList Adult)
        private readonly AdBlocker _adBlocker = new();

        // Хранилище для среды WebView2, чтобы вызывать CreateWebResourceResponse
        private CoreWebView2Environment? _webViewEnv;

        private string _currentImageFileName = "";
        private string _currentTags = "";

        // Кроп
        private bool _isDragging = false;
        private bool _isResizing = false;
        private Point _dragOffset;
        private bool _isFirstLoad = true;

        // Regex
        private static readonly Regex E621PostRegex = new(@"e621\.net/posts/(\d+)", RegexOptions.Compiled);
        private static readonly Regex Rule34IdRegex = new(@"[?&]id=(\d+)", RegexOptions.Compiled);

        // HttpClient — один на всё приложение, без глобального Authorization
        private static readonly HttpClient _http;
        static MainWindow()
        {
            _http = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
                MaxConnectionsPerServer = 4
            })
            { Timeout = TimeSpan.FromSeconds(60) };
        }

        public MainWindow()
        {
            InitializeComponent();
            LoadSettings();
            TxtMainTag.Text = _mainTag;
            UpdateSavePathLabel();

            this.SizeChanged += (s, e) => { if (ArtImage.Source != null) CenterCropRect(); };

            InitWebView();
        }

        // ════════════════════════════════════════════════
        //  WEBVIEW2
        // ════════════════════════════════════════════════
        private async void InitWebView()
        {
            try
            {
                var opts = new CoreWebView2EnvironmentOptions("--ignore-certificate-errors");
                _webViewEnv = await CoreWebView2Environment.CreateAsync(null, null, opts);
                await WebView.EnsureCoreWebView2Async(_webViewEnv);

                WebView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                WebView.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
                WebView.SourceChanged += OnSourceChanged;

                UpdateHttpAuth();
                NavigateTo("e621");

                // Загружаем EasyList в фоне — не блокируем старт
                _ = InitAdBlockerAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка браузера: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task InitAdBlockerAsync()
        {
            try
            {
                SetStatus("⏳ Загрузка фильтров рекламы...");
                await _adBlocker.InitAsync();
                SetStatus($"✅ AdBlock: {_adBlocker.RuleCount:N0} правил загружено");
                await Task.Delay(2500);
                SetStatus("");
            }
            catch (Exception ex)
            {
                SetStatus($"⚠ Фильтры недоступны: {ex.Message} (хардкод активен)", error: true);
                await Task.Delay(3000);
                SetStatus("");
            }
        }

        // Перехват запросов WebView2: блокируем рекламу, добавляем Basic Auth для e621
        private void OnWebResourceRequested(object? s, CoreWebView2WebResourceRequestedEventArgs args)
        {
            try
            {
                string url = args.Request.Uri;

                if (_webViewEnv == null) return;

                // Защита: никогда не блокируем запросы к самому API сайтов
                if (url.Contains("api.rule34.xxx", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("e621.net/posts", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // ── Блокировка рекламы (Жесткий хардкод сетей) ──────────────────
                if (url.Contains("rv.e621.net", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("exoclick.com", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("trafficjunky.net", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("juicyads.com", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("fuckingfast.net", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("adspyglass.com", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("adnxs.com", StringComparison.OrdinalIgnoreCase))
                {
                    args.Response = _webViewEnv.CreateWebResourceResponse(new MemoryStream(), 403, "Forbidden", "Content-Type: text/plain");
                    return;
                }

                // EasyList проверка
                if (_adBlocker.IsLoaded && _adBlocker.ShouldBlock(url))
                {
                    args.Response = _webViewEnv.CreateWebResourceResponse(new MemoryStream(), 403, "Forbidden", "Content-Type: text/plain");
                    return;
                }

                // ── Авторизация e621 ────────────────────────────────────────────
                if (_currentSite == "e621" &&
                    url.Contains("e621.net", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(_cachedBase64Auth))
                {
                    args.Request.Headers.SetHeader("Authorization", $"Basic {_cachedBase64Auth}");
                }
            }
            catch { }
        }

        private void OnSourceChanged(object? s, CoreWebView2SourceChangedEventArgs e)
        {
            string url = WebView.Source?.ToString() ?? "";

            if (_currentSite == "e621")
            {
                var m = E621PostRegex.Match(url);
                if (m.Success) _ = LoadE621Async(m.Groups[1].Value);
            }
            else if (_currentSite == "rule34")
            {
                if (url.Contains("page=post") && url.Contains("s=view"))
                {
                    var m = Rule34IdRegex.Match(url);
                    if (m.Success) _ = LoadRule34Async(m.Groups[1].Value);
                }
            }
        }

        // ════════════════════════════════════════════════
        //  ПЕРЕКЛЮЧЕНИЕ САЙТОВ
        // ════════════════════════════════════════════════
        private void NavigateTo(string site)
        {
            _currentSite = site;
            ClearArt();

            BtnE621.Style = site == "e621" ? (Style)FindResource("BlueBtn") : (Style)FindResource("BaseBtn");
            BtnRule34.Style = site == "rule34" ? (Style)FindResource("BlueBtn") : (Style)FindResource("BaseBtn");

            WebView.CoreWebView2?.Navigate(
                site == "e621" ? "https://e621.net" : "https://rule34.xxx");
        }

        private void BtnE621_Click(object s, RoutedEventArgs e) => NavigateTo("e621");
        private void BtnRule34_Click(object s, RoutedEventArgs e) => NavigateTo("rule34");
        private void BtnBack_Click(object s, RoutedEventArgs e) { if (WebView.CanGoBack) WebView.GoBack(); }
        private void BtnForward_Click(object s, RoutedEventArgs e) { if (WebView.CanGoForward) WebView.GoForward(); }
        private void BtnRefresh_Click(object s, RoutedEventArgs e) => WebView.Reload();

        // ════════════════════════════════════════════════
        //  ЗАГРУЗКА АРТА — e621
        // ════════════════════════════════════════════════
        private async Task LoadE621Async(string postId)
        {
            try
            {
                SetStatus("⏳ Загрузка...");

                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://e621.net/posts/{postId}.json");

                req.Headers.Add("User-Agent", $"ArtFinder/1.0 (by {(!string.IsNullOrEmpty(_e621Login) ? _e621Login : "Anonymous")} on e621)");

                if (!string.IsNullOrEmpty(_cachedBase64Auth))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", _cachedBase64Auth);

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

                if (!resp.IsSuccessStatusCode)
                {
                    SetStatus($"❌ e621 API: {(int)resp.StatusCode} {resp.ReasonPhrase}", error: true);
                    return;
                }

                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var post = doc.RootElement.GetProperty("post");

                if (!post.GetProperty("file").TryGetProperty("url", out var urlProp) ||
                    urlProp.ValueKind == JsonValueKind.Null)
                {
                    SetStatus("❌ Файл недоступен (возможно, заблокирован без авторизации)", error: true);
                    return;
                }

                string? imgUrl = urlProp.GetString();
                if (string.IsNullOrEmpty(imgUrl)) { SetStatus("❌ Пустой URL файла", error: true); return; }

                _currentImageFileName = Path.GetFileNameWithoutExtension(imgUrl);

                var seen = new HashSet<string>(StringComparer.Ordinal);
                var sb = new StringBuilder();
                foreach (var cat in post.GetProperty("tags").EnumerateObject())
                    foreach (var t in cat.Value.EnumerateArray())
                    {
                        string? tag = t.GetString();
                        if (tag != null && seen.Add(tag))
                        { if (sb.Length > 0) sb.Append("\n\n"); sb.Append(tag); }
                    }
                _currentTags = sb.ToString();

                var bitmap = await LoadImageAsync(imgUrl);
                ShowArt(bitmap, postId);
            }
            catch (Exception ex)
            {
                SetStatus($"❌ e621: {ex.Message}", error: true);
            }
        }

        // ════════════════════════════════════════════════
        //  ЗАГРУЗКА АРТА — rule34
        // ════════════════════════════════════════════════
        private async Task LoadRule34Async(string postId)
        {
            try
            {
                SetStatus("⏳ Загрузка...");

                // Автоматически разрешаем ID пользователя
                await ResolveRule34UserIdAsync();

                string apiUrl = $"https://api.rule34.xxx/index.php?page=dapi&s=post&q=index&id={postId}&json=1";

                if (!string.IsNullOrEmpty(_rule34NumericUserId) && !string.IsNullOrEmpty(_rule34ApiKey))
                    apiUrl += $"&user_id={Uri.EscapeDataString(_rule34NumericUserId)}&api_key={Uri.EscapeDataString(_rule34ApiKey.Trim())}";

                using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                string cookies = await GetCookiesFromWebView("https://rule34.xxx");
                if (!string.IsNullOrEmpty(cookies))
                    req.Headers.Add("Cookie", cookies);

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

                if (!resp.IsSuccessStatusCode)
                {
                    SetStatus($"❌ rule34: ошибка сервера ({resp.StatusCode})", error: true);
                    return;
                }

                string json = await resp.Content.ReadAsStringAsync();

                if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null" || json.Trim() == "[]")
                {
                    SetStatus("❌ rule34: пост не найден (проверьте ID/авторизацию)", error: true);
                    return;
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                {
                    SetStatus("❌ rule34: пост не найден", error: true);
                    return;
                }

                var post = root[0];
                string? imgUrl = post.GetProperty("file_url").GetString();
                if (string.IsNullOrEmpty(imgUrl))
                {
                    SetStatus("❌ Файл недоступен", error: true);
                    return;
                }

                _currentImageFileName = Path.GetFileNameWithoutExtension(imgUrl);

                string tagStr = post.GetProperty("tags").GetString() ?? "";
                var sb = new StringBuilder();
                foreach (var tag in tagStr.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (sb.Length > 0) sb.Append("\n\n");
                    sb.Append(tag);
                }
                _currentTags = sb.ToString();

                var bitmap = await LoadImageAsync(imgUrl);
                ShowArt(bitmap, postId);
            }
            catch (Exception ex) { SetStatus($"❌ rule34: {ex.Message}", error: true); }
        }

        private async Task ResolveRule34UserIdAsync()
        {
            _rule34NumericUserId = "";

            // 1. Если пользователь в поле Логин сразу ввёл числовой ID, берем его напрямую
            if (!string.IsNullOrEmpty(_rule34Login) && long.TryParse(_rule34Login.Trim(), out _))
            {
                _rule34NumericUserId = _rule34Login.Trim();
                return;
            }

            // 2. Если там текст, пытаемся вытащить ID из кук авторизованного браузера WebView2
            if (WebView.CoreWebView2 != null)
            {
                try
                {
                    var cookieList = await WebView.CoreWebView2.CookieManager.GetCookiesAsync("https://rule34.xxx");
                    foreach (var c in cookieList)
                    {
                        if (c.Name == "user_id" && !string.IsNullOrEmpty(c.Value))
                        {
                            _rule34NumericUserId = c.Value;
                            return;
                        }
                    }
                }
                catch { }
            }

            // 3. Предупреждение, если ключ есть, а ID так и не смогли сопоставить
            if (!string.IsNullOrEmpty(_rule34ApiKey) && string.IsNullOrEmpty(_rule34NumericUserId))
            {
                Dispatcher.Invoke(() => SetStatus("⚠ rule34: введите числовой User ID в настройки или войдите на сайт", error: true));
                await Task.Delay(2000);
                Dispatcher.Invoke(() => SetStatus(""));
            }
        }

        private async Task<string> GetCookiesFromWebView(string uri)
        {
            if (WebView.CoreWebView2 == null) return "";
            try
            {
                var cookieList = await WebView.CoreWebView2.CookieManager.GetCookiesAsync(uri);
                var parts = new List<string>(cookieList.Count);
                foreach (var c in cookieList)
                    parts.Add($"{c.Name}={c.Value}");
                return string.Join("; ", parts);
            }
            catch { return ""; }
        }

        // ════════════════════════════════════════════════
        //  ЗАГРУЗКА ИЗОБРАЖЕНИЯ
        // ════════════════════════════════════════════════
        private static async Task<BitmapImage> LoadImageAsync(string url)
        {
            if (url.StartsWith("//")) url = "https:" + url;

            using var req = new HttpRequestMessage(HttpMethod.Get, url);

            if (url.Contains("e621.net"))
                req.Headers.Referrer = new Uri("https://e621.net/");
            else if (url.Contains("rule34.xxx") || url.Contains("booru.org"))
                req.Headers.Referrer = new Uri("https://rule34.xxx/");

            req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            long len = resp.Content.Headers.ContentLength ?? 512 * 1024;
            using var net = await resp.Content.ReadAsStreamAsync();
            using var mem = new MemoryStream((int)Math.Min(len, 30 * 1024 * 1024));
            await net.CopyToAsync(mem);
            mem.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = mem;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        private void ShowArt(BitmapImage bitmap, string postId)
        {
            Dispatcher.Invoke(() =>
            {
                ArtImage.Source = bitmap;
                ArtImage.UpdateLayout();

                TxtTagsPreview.Text = string.IsNullOrEmpty(_currentTags) ? "(нет тегов)" : _currentTags;
                CropRect.Visibility = Visibility.Visible;
                ResizeHandle.Visibility = Visibility.Visible;

                SetupCropAfterLoad();
                SetStatus($"✅ {postId} загружен");
            });
        }

        private void ClearArt()
        {
            ArtImage.Source = null;
            _currentImageFileName = "";
            _currentTags = "";
            CropRect.Visibility = Visibility.Collapsed;
            ResizeHandle.Visibility = Visibility.Collapsed;
            TxtTagsPreview.Text = "(теги не загружены)";
            TxtCropSize.Text = "Рамка: — × —";
            TxtStatus.Text = "";
            HideOverlays();
        }

        // ════════════════════════════════════════════════
        //  КРОП
        // ════════════════════════════════════════════════
        private Rect GetImageRect()
        {
            if (ArtImage.Source == null || ArtImage.ActualWidth == 0) return new Rect();
            var src = ArtImage.Source;
            double ratio = Math.Min(WorkArea.ActualWidth / src.Width,
                                    WorkArea.ActualHeight / src.Height);
            if (ratio > 1) ratio = 1;
            double w = src.Width * ratio;
            double h = src.Height * ratio;
            return new Rect((WorkArea.ActualWidth - w) / 2,
                            (WorkArea.ActualHeight - h) / 2, w, h);
        }

        private void SetupCropAfterLoad()
        {
            if (_isFirstLoad || !_lockCropSize)
            {
                CropRect.Width = _lockCropSize ? _lockedW : 70;
                CropRect.Height = _lockCropSize ? _lockedH : 70;
            }
            CenterCropRect();
            _isFirstLoad = false;
        }

        private void CenterCropRect()
        {
            Rect img = GetImageRect();
            if (img.Width == 0) return;

            double w = double.IsNaN(CropRect.Width) ? 300 : CropRect.Width;
            double h = double.IsNaN(CropRect.Height) ? 300 : CropRect.Height;
            w = Math.Min(w, img.Width);
            h = Math.Min(h, img.Height);
            CropRect.Width = w;
            CropRect.Height = h;

            Canvas.SetLeft(CropRect, img.X + (img.Width - w) / 2);
            Canvas.SetTop(CropRect, img.Y + (img.Height - h) / 2);
            UpdateHandlePosition();
            UpdateOverlays();
            TxtCropSize.Text = $"Рамка: {w:F0} × {h:F0}";
        }

        private void UpdateHandlePosition()
        {
            double w = double.IsNaN(CropRect.Width) ? 0 : CropRect.Width;
            double h = double.IsNaN(CropRect.Height) ? 0 : CropRect.Height;
            double l = Canvas.GetLeft(CropRect); if (double.IsNaN(l)) l = 0;
            double t = Canvas.GetTop(CropRect); if (double.IsNaN(t)) t = 0;
            Canvas.SetLeft(ResizeHandle, l + w - 7);
            Canvas.SetTop(ResizeHandle, t + h - 7);
        }

        private void UpdateOverlays()
        {
            Rect img = GetImageRect();
            if (img.Width == 0) { HideOverlays(); return; }

            double cL = Canvas.GetLeft(CropRect); if (double.IsNaN(cL)) cL = 0;
            double cT = Canvas.GetTop(CropRect); if (double.IsNaN(cT)) cT = 0;
            double cW = double.IsNaN(CropRect.Width) ? 0 : CropRect.Width;
            double cH = double.IsNaN(CropRect.Height) ? 0 : CropRect.Height;

            SetOverlay(OverlayTop, img.X, img.Y, img.Width, cT - img.Y);
            SetOverlay(OverlayBottom, img.X, cT + cH, img.Width, img.Bottom - (cT + cH));
            SetOverlay(OverlayLeft, img.X, cT, cL - img.X, cH);
            SetOverlay(OverlayRight, cL + cW, cT, img.Right - (cL + cW), cH);
        }

        private static void SetOverlay(System.Windows.Shapes.Rectangle r,
                                       double x, double y, double w, double h)
        {
            Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
            r.Width = Math.Max(0, w);
            r.Height = Math.Max(0, h);
        }

        private void HideOverlays()
        {
            foreach (var r in new[] { OverlayTop, OverlayBottom, OverlayLeft, OverlayRight })
            { r.Width = 0; r.Height = 0; }
        }

        private void CropCanvas_MouseDown(object s, MouseButtonEventArgs e)
        {
            if (ArtImage.Source == null) return;
            Point p = e.GetPosition(CropCanvas);
            double l = Canvas.GetLeft(CropRect); if (double.IsNaN(l)) l = 0;
            double t = Canvas.GetTop(CropRect); if (double.IsNaN(t)) t = 0;
            double w = double.IsNaN(CropRect.Width) ? 0 : CropRect.Width;
            double h = double.IsNaN(CropRect.Height) ? 0 : CropRect.Height;

            if (p.X >= l && p.X <= l + w && p.Y >= t && p.Y <= t + h)
            {
                _isDragging = true;
                _dragOffset = new Point(p.X - l, p.Y - t);
                CropCanvas.CaptureMouse();
            }
        }

        private void CropCanvas_MouseMove(object s, MouseEventArgs e)
        {
            if (!_isDragging) return;
            Point p = e.GetPosition(CropCanvas);
            Rect img = GetImageRect();
            double w = double.IsNaN(CropRect.Width) ? 0 : CropRect.Width;
            double h = double.IsNaN(CropRect.Height) ? 0 : CropRect.Height;

            double nL = Math.Max(img.X, Math.Min(p.X - _dragOffset.X, img.Right - w));
            double nT = Math.Max(img.Y, Math.Min(p.Y - _dragOffset.Y, img.Bottom - h));
            Canvas.SetLeft(CropRect, nL);
            Canvas.SetTop(CropRect, nT);
            UpdateHandlePosition();
            UpdateOverlays();
        }

        private void CropCanvas_MouseUp(object s, MouseButtonEventArgs e)
        {
            _isDragging = false;
            CropCanvas.ReleaseMouseCapture();
        }

        private void ResizeHandle_MouseDown(object s, MouseButtonEventArgs e)
        {
            if (_lockCropSize) return;
            _isResizing = true;
            ResizeHandle.CaptureMouse();
            e.Handled = true;
            ResizeHandle.MouseMove += ResizeHandle_MouseMove;
            ResizeHandle.MouseUp += ResizeHandle_MouseUp;
        }

        private void ResizeHandle_MouseMove(object s, MouseEventArgs e)
        {
            if (!_isResizing) return;
            Point p = e.GetPosition(CropCanvas);
            Rect img = GetImageRect();
            double cL = Canvas.GetLeft(CropRect); if (double.IsNaN(cL)) cL = 0; // Исправлено: cL вместо l
            double cT = Canvas.GetTop(CropRect); if (double.IsNaN(cT)) cT = 0;

            double newW = Math.Max(32, Math.Min(p.X - cL, img.Right - cL));
            double newH = Math.Max(32, Math.Min(p.Y - cT, img.Bottom - cT));
            CropRect.Width = newW;
            CropRect.Height = newH;
            UpdateHandlePosition();
            UpdateOverlays();
            TxtCropSize.Text = $"Рамка: {newW:F0} × {newH:F0}";
        }

        private void ResizeHandle_MouseUp(object s, MouseButtonEventArgs e)
        {
            _isResizing = false;
            ResizeHandle.ReleaseMouseCapture();
            ResizeHandle.MouseMove -= ResizeHandle_MouseMove;
            ResizeHandle.MouseUp -= ResizeHandle_MouseUp;
        }

        // ════════════════════════════════════════════════
        //  КНОПКИ ПРАВОЙ ПАНЕЛИ
        // ════════════════════════════════════════════════
        private void BtnLockSize_Click(object s, RoutedEventArgs e)
        {
            _lockCropSize = !_lockCropSize;
            if (_lockCropSize)
            {
                _lockedW = double.IsNaN(CropRect.Width) ? 300 : CropRect.Width;
                _lockedH = double.IsNaN(CropRect.Height) ? 300 : CropRect.Height;
                BtnLockSize.Content = $"✔ Зафиксировано ({_lockedW:F0}×{_lockedH:F0})";
                BtnLockSize.Style = (Style)FindResource("YellowBtn");
            }
            else
            {
                BtnLockSize.Content = "Фиксировать размер рамки";
                BtnLockSize.Style = (Style)FindResource("BaseBtn");
            }
            SaveSettings();
        }

        private void TxtMainTag_TextChanged(object s, TextChangedEventArgs e)
        {
            _mainTag = TxtMainTag.Text;
            SaveSettings();
        }

        private void BtnChoosePath_Click(object s, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Выберите папку сохранения" };
            if (dlg.ShowDialog() == true)
            {
                _savePath = dlg.FolderName;
                UpdateSavePathLabel();
                SaveSettings();
            }
        }

        private void BtnApiSettings_Click(object s, RoutedEventArgs e)
        {
            var dlg = new ApiSettingsWindow(
                _e621Login, _e621ApiKey, _rule34Login, _rule34ApiKey)
            { Owner = this };

            if (dlg.ShowDialog() == true)
            {
                _e621Login = dlg.E621Login;
                _e621ApiKey = dlg.E621ApiKey;
                _rule34Login = dlg.Rule34Login;
                _rule34ApiKey = dlg.Rule34ApiKey;
                _rule34NumericUserId = "";
                SaveSettings();
                UpdateHttpAuth();
                WebView.Reload();
            }
        }

        private void BtnSaveCrop_Click(object s, RoutedEventArgs e) => SaveArt(crop: true);
        private void BtnSaveOriginal_Click(object s, RoutedEventArgs e) => SaveArt(crop: false);

        // ════════════════════════════════════════════════
        //  СОХРАНЕНИЕ
        // ════════════════════════════════════════════════
        private async void SaveArt(bool crop)
        {
            if (ArtImage.Source == null)
            { SetStatus("⚠ Арт не загружен.", error: true); return; }
            if (string.IsNullOrEmpty(_savePath))
            { SetStatus("⚠ Выберите папку.", error: true); return; }
            if (string.IsNullOrEmpty(_currentImageFileName))
            { SetStatus("⚠ Имя файла неизвестно.", error: true); return; }

            try
            {
                if (ArtImage.Source is not BitmapSource src) return;
                Directory.CreateDirectory(_savePath);

                BitmapSource imgToSave;
                if (crop)
                {
                    Rect img = GetImageRect();
                    if (img.Width == 0) return;

                    double sX = src.PixelWidth / img.Width;
                    double sY = src.PixelHeight / img.Height;
                    double l = Canvas.GetLeft(CropRect); if (double.IsNaN(l)) l = 0;
                    double t = Canvas.GetTop(CropRect); if (double.IsNaN(t)) t = 0;
                    double w = double.IsNaN(CropRect.Width) ? 0 : CropRect.Width;
                    double h = double.IsNaN(CropRect.Height) ? 0 : CropRect.Height;

                    int x = (int)((l - img.X) * sX);
                    int y = (int)((t - img.Y) * sY);
                    int rW = (int)(w * sX);
                    int rH = (int)(h * sY);

                    imgToSave = new CroppedBitmap(src, new Int32Rect(
                        Math.Max(0, x), Math.Max(0, y),
                        Math.Min(rW, src.PixelWidth - Math.Max(0, x)),
                        Math.Min(rH, src.PixelHeight - Math.Max(0, y))));
                }
                else imgToSave = src;

                string imgPath = Path.Combine(_savePath, _currentImageFileName + ".png");
                await Task.Run(() =>
                {
                    using var fs = File.Create(imgPath);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(imgToSave));
                    enc.Save(fs);
                });

                string finalTags = string.IsNullOrWhiteSpace(_mainTag)
                    ? _currentTags
                    : _mainTag + (string.IsNullOrEmpty(_currentTags) ? "" : "\n\n" + _currentTags);
                File.WriteAllText(Path.Combine(_savePath, _currentImageFileName + ".txt"), finalTags);

                SetStatus(crop ? "✔ КРОП СОХРАНЁН" : "✔ ОРИГИНАЛ СОХРАНЁН");
                await Task.Delay(2000);
                SetStatus("");
            }
            catch (Exception ex) { SetStatus($"❌ {ex.Message}", error: true); }
        }

        // ════════════════════════════════════════════════
        //  АВТОРИЗАЦИЯ
        // ════════════════════════════════════════════════
        private void UpdateHttpAuth()
        {
            if (!string.IsNullOrEmpty(_e621Login) && !string.IsNullOrEmpty(_e621ApiKey))
            {
                _cachedBase64Auth = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{_e621Login}:{_e621ApiKey}"));
            }
            else
            {
                _cachedBase64Auth = "";
            }
        }

        // ════════════════════════════════════════════════
        //  ВСПОМОГАТЕЛЬНЫЕ
        // ════════════════════════════════════════════════
        private void UpdateSavePathLabel() =>
            TxtSavePath.Text = string.IsNullOrEmpty(_savePath) ? "(папка не выбрана)" : _savePath;

        private void SetStatus(string msg, bool error = false)
        {
            TxtStatus.Foreground = error
                ? new SolidColorBrush(Color.FromRgb(220, 80, 80))
                : (SolidColorBrush)FindResource("AccentGreen");
            TxtStatus.Text = msg;
        }

        // ════════════════════════════════════════════════
        //  НАСТРОЙКИ
        // ════════════════════════════════════════════════
        private string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ArtFinder", "settings.json");

        private void SaveSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new
            {
                savePath = _savePath,
                mainTag = _mainTag,
                lockCropSize = _lockCropSize,
                lockedW = _lockedW,
                lockedH = _lockedH,
                e621Login = _e621Login,
                e621ApiKey = _e621ApiKey,
                rule34Login = _rule34Login,
                rule34ApiKey = _rule34ApiKey
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                var r = doc.RootElement;
                string G(string k, string def = "") =>
                    r.TryGetProperty(k, out var v) ? (v.GetString() ?? def) : def;
                _savePath = G("savePath");
                _mainTag = G("mainTag");
                _lockCropSize = r.TryGetProperty("lockCropSize", out var v3) && v3.GetBoolean();
                _lockedW = r.TryGetProperty("lockedW", out var v4) ? v4.GetDouble() : 300;
                _lockedH = r.TryGetProperty("lockedH", out var v5) ? v5.GetDouble() : 300;
                _e621Login = G("e621Login");
                _e621ApiKey = G("e621ApiKey");
                _rule34Login = G("rule34Login");
                _rule34ApiKey = G("rule34ApiKey");
                UpdateHttpAuth();
            }
            catch { }
        }
    }
}