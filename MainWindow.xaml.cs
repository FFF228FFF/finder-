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
using System.Windows.Threading;
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
        private double _lockedW = 512, _lockedH = 512; // в пикселях картинки

        // Учётные данные
        private string _e621Login = "";
        private string _e621ApiKey = "";

        // Кешированный Base64 для e621 Basic Auth (WebResourceRequested + HttpClient)
        private string _cachedBase64Auth = "";

        // Блокировщик рекламы (списки uBlock Origin + хардкод известных сетей)
        private readonly AdBlocker _adBlocker = new();

        // Хранилище для среды WebView2, чтобы вызывать CreateWebResourceResponse
        private CoreWebView2Environment? _webViewEnv;

        private string _currentImageFileName = "";
        private string _currentTags = "";
        // Исходный файл как скачан — «Сохранить оригинал» пишет его без перекодирования
        private byte[] _currentOriginalBytes = Array.Empty<byte>();
        private string _currentOriginalExt = "";

        // Защита от гонки: при быстром переходе между постами ответы приходят
        // в произвольном порядке. Применяется только результат последней
        // загрузки, предыдущая отменяется.
        private int _loadVersion;
        private CancellationTokenSource? _loadCts;

        // Кроп
        private bool _isDragging = false;
        private bool _isResizing = false;
        private Point _dragOffset;

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
            ApplyWindowPlacement();
            ApplyToolSettings();
            TxtMainTag.Text = _mainTag;
            UpdateSavePathLabel();
            SyncLockSizeButton();
            UpdateTagsPreview();

            SourceInitialized += (s, e) => WindowTheme.ApplyDark(this);

            // Главный тег сохраняется не на каждое нажатие, а через паузу после ввода
            _saveSettingsTimer.Tick += (s, e) => { _saveSettingsTimer.Stop(); SaveSettings(); };
            // При закрытии сохраняются и размер/положение окна, и ширина панелей
            Closing += (s, e) => { _jobCts?.Cancel(); _saveSettingsTimer.Stop(); SaveSettings(); };

            // Списки запрещённых тегов грузятся и компилируются в фоне заранее,
            // чтобы первое сохранение не ждало
            _ = Task.Run(TagFilter.Warmup);

            InitWebView();
        }

        private readonly DispatcherTimer _saveSettingsTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

        // ════════════════════════════════════════════════
        //  WEBVIEW2
        // ════════════════════════════════════════════════
        private async void InitWebView()
        {
            try
            {
                // Тёмный фон до первой отрисовки страницы — без белой вспышки
                WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 0x0B, 0x0B, 0x10);

                _webViewEnv = await CoreWebView2Environment.CreateAsync();
                await WebView.EnsureCoreWebView2Async(_webViewEnv);

                // Сайты с тёмной темой по prefers-color-scheme включат её сами
                WebView.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;

                WebView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                WebView.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
                WebView.SourceChanged += OnSourceChanged;
                WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                WebView.CoreWebView2.HistoryChanged += (_, _) => UpdateNavButtons();
                UpdateNavButtons();

                UpdateHttpAuth();
                NavigateTo("e621");

                // Загружаем фильтр-листы в фоне — не блокируем старт
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
                // Хардкод-блокировка уже активна с первого запроса (см. OnWebResourceRequested);
                // здесь лишь догружаются расширенные списки uBlock Origin.
                await _adBlocker.InitAsync();
                SetStatus($"✅ AdBlock: {_adBlocker.RuleCount:N0} правил загружено");
                ClearStatusLater(2500);
            }
            catch (Exception ex)
            {
                SetStatus($"⚠ Фильтры недоступны: {ex.Message} (хардкод активен)", error: true);
                ClearStatusLater(3000);
            }
        }

        // Перехват запросов WebView2: блокируем рекламу, добавляем Basic Auth для e621
        private void OnWebResourceRequested(object? s, CoreWebView2WebResourceRequestedEventArgs args)
        {
            try
            {
                string url = args.Request.Uri;

                if (_webViewEnv == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;

                // Защита: никогда не блокируем запросы к самому API сайтов.
                // Сравнивается именно хост, а не подстрока всего адреса —
                // иначе "https://ads.example/?ref=e621.net/posts" проходил бы мимо фильтра.
                bool isSiteApi = uri.Host.Equals("api.rule34.xxx", StringComparison.OrdinalIgnoreCase) ||
                                 (uri.Host.Equals("e621.net", StringComparison.OrdinalIgnoreCase) &&
                                  uri.AbsolutePath.StartsWith("/posts", StringComparison.OrdinalIgnoreCase));

                // ── Блокировка рекламы ───────────────────────────────────────────
                // ShouldBlock сам проверяет хардкод-список немедленно (не зависит
                // от IsLoaded) и дополнительно — загруженные фильтр-листы, когда
                // они готовы. Отдельный дублирующий список здесь не нужен.
                if (!isSiteApi && _adBlocker.ShouldBlock(url))
                {
                    args.Response = _webViewEnv.CreateWebResourceResponse(new MemoryStream(), 403, "Forbidden", "Content-Type: text/plain");
                    return;
                }

                // ── Авторизация e621 ────────────────────────────────────────────
                // Только сам e621.net по HTTPS: логин и ключ не должны уходить
                // на хосты вроде "e621.net.evil.com" или в чужие запросы,
                // где "e621.net" встречается лишь в параметрах.
                if (_currentSite == "e621" &&
                    uri.Scheme == Uri.UriSchemeHttps &&
                    uri.Host.Equals("e621.net", StringComparison.OrdinalIgnoreCase) &&
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
            if (!TxtAddress.IsKeyboardFocused) TxtAddress.Text = url;
            UpdateSaverSearch(url);

            // Сайт определяется по адресу, а не только по кнопкам: если перейти
            // на другой сайт по ссылке, режим (авторизация, разбор поста) следует за ним.
            string? site = SiteOf(url);
            if (site != null && site != _currentSite)
            {
                _currentSite = site;
                ClearArt();
                UpdateSiteButtons();
            }

            if (_currentSite == "e621")
            {
                var m = E621PostRegex.Match(url);
                if (m.Success) _ = LoadE621Async(m.Groups[1].Value);
            }
            // rule34 обрабатывается в OnNavigationCompleted — данные читаются
            // прямо из DOM страницы, а на момент SourceChanged он ещё не готов.
        }

        // Для rule34 арт и теги берутся напрямую со страницы поста, поэтому
        // нужен момент, когда DOM уже полностью загружен (в отличие от
        // SourceChanged, который срабатывает раньше).
        private void OnNavigationCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess || _currentSite != "rule34") return;

            string url = WebView.Source?.ToString() ?? "";
            if (!url.Contains("page=post") || !url.Contains("s=view")) return;

            var m = Rule34IdRegex.Match(url);
            if (m.Success) _ = LoadRule34Async(m.Groups[1].Value);
        }

        // ════════════════════════════════════════════════
        //  ПЕРЕКЛЮЧЕНИЕ САЙТОВ
        // ════════════════════════════════════════════════
        private void NavigateTo(string site)
        {
            _currentSite = site;
            ClearArt();
            UpdateSiteButtons();

            WebView.CoreWebView2?.Navigate(
                site == "e621" ? "https://e621.net" : "https://rule34.xxx");
        }

        private void UpdateSiteButtons()
        {
            BtnE621.Style = (Style)FindResource(_currentSite == "e621" ? "BlueBtn" : "BaseBtn");
            BtnRule34.Style = (Style)FindResource(_currentSite == "rule34" ? "BlueBtn" : "BaseBtn");
        }

        private static string? SiteOf(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
            static bool Is(string host, string domain) =>
                host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
            if (Is(u.Host, "e621.net")) return "e621";
            if (Is(u.Host, "rule34.xxx")) return "rule34";
            return null;
        }

        private void UpdateNavButtons()
        {
            bool ready = WebView.CoreWebView2 != null;
            BtnBack.IsEnabled = ready && WebView.CanGoBack;
            BtnForward.IsEnabled = ready && WebView.CanGoForward;
        }

        private void BtnE621_Click(object s, RoutedEventArgs e) => NavigateTo("e621");
        private void BtnRule34_Click(object s, RoutedEventArgs e) => NavigateTo("rule34");
        private void BtnBack_Click(object s, RoutedEventArgs e) { if (WebView.CanGoBack) WebView.GoBack(); }
        private void BtnForward_Click(object s, RoutedEventArgs e) { if (WebView.CanGoForward) WebView.GoForward(); }
        private void BtnRefresh_Click(object s, RoutedEventArgs e) => WebView.Reload();

        // ── Адресная строка ──
        // Адрес открывается как есть; всё остальное считается тегами и ищется
        // на текущем сайте.
        private static readonly Regex BareHostRegex =
            new(@"^[\w-]+(\.[\w-]+)*\.[a-z]{2,}(:\d+)?(/\S*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private void TxtAddress_KeyDown(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                TxtAddress.Text = WebView.Source?.ToString() ?? "";
                WebView.Focus();
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Enter || WebView.CoreWebView2 == null) return;
            e.Handled = true;

            string text = TxtAddress.Text.Trim();
            if (text.Length == 0) return;

            string target;
            if (text.Contains("://")) target = text;
            else if (!text.Contains(' ') && BareHostRegex.IsMatch(text)) target = "https://" + text;
            else
            {
                string tags = Uri.EscapeDataString(Regex.Replace(text, @"\s+", " "));
                target = _currentSite == "rule34"
                    ? $"https://rule34.xxx/index.php?page=post&s=list&tags={tags}"
                    : $"https://e621.net/posts?tags={tags}";
            }

            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                SetStatus("⚠ Некорректный адрес.", error: true);
                return;
            }
            WebView.CoreWebView2.Navigate(uri.AbsoluteUri);
            WebView.Focus();
        }

        private void TxtAddress_GotKeyboardFocus(object s, KeyboardFocusChangedEventArgs e) =>
            Dispatcher.BeginInvoke(new Action(TxtAddress.SelectAll), DispatcherPriority.Input);

        // ════════════════════════════════════════════════
        //  ЗАГРУЗКА АРТА — e621
        // ════════════════════════════════════════════════
        private (int version, CancellationToken token) BeginLoad()
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();
            return (++_loadVersion, _loadCts.Token);
        }

        private void CancelLoad()
        {
            _loadCts?.Cancel();
            _loadVersion++;
        }

        private async Task LoadE621Async(string postId)
        {
            var (version, token) = BeginLoad();
            try
            {
                SetStatus("⏳ Загрузка...");

                var (status, json) = await FetchE621JsonAsync($"https://e621.net/posts/{postId}.json", token,
                    onFallback: () => { if (version == _loadVersion) SetStatus("⏳ Загрузка через браузер..."); });
                if (version != _loadVersion) return;
                if (json == null)
                {
                    SetStatus($"❌ e621 API: {status}", error: true);
                    return;
                }

                using var doc = JsonDocument.Parse(json);
                var post = doc.RootElement.GetProperty("post");

                string? imgUrl = E621FileUrl(post);
                if (string.IsNullOrEmpty(imgUrl))
                {
                    SetStatus("❌ Файл недоступен (возможно, заблокирован без авторизации)", error: true);
                    return;
                }

                await LoadAndShowArtAsync(version, token, imgUrl, E621Tags(post), postId);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (version == _loadVersion) SetStatus($"❌ e621: {DescribeError(ex)}", error: true);
            }
        }

        // ── Общие помощники e621 (ручная загрузка и «Скачать всё») ──
        private HttpRequestMessage CreateE621Request(string url)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("User-Agent", $"ArtFinder/3.0 (by {(!string.IsNullOrEmpty(_e621Login) ? _e621Login : "Anonymous")} on e621)");
            if (!string.IsNullOrEmpty(_cachedBase64Auth))
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", _cachedBase64Auth);
            return req;
        }

        // JSON с API e621. Если прямое соединение не устанавливается (SSL/сеть —
        // типично при VPN/фильтрации провайдера), а встроенный браузер открыт
        // на e621, JSON берётся его сетевым стеком.
        // Возвращает (HTTP-статус или текст ошибки, тело или null при ошибке).
        private async Task<(string Status, string? Json)> FetchE621JsonAsync(
            string url, CancellationToken token, Action? onFallback = null)
        {
            try
            {
                using var resp = await SendWithRetryAsync(() => CreateE621Request(url), token);
                if (!resp.IsSuccessStatusCode) return ($"{(int)resp.StatusCode} {resp.ReasonPhrase}", null);
                return ("200", await resp.Content.ReadAsStringAsync(token));
            }
            catch (HttpRequestException) when (!token.IsCancellationRequested && CanFetchViaWebView(url))
            {
                onFallback?.Invoke();
                var (status, _, body) = await WebViewFetchAsync(url);
                token.ThrowIfCancellationRequested();
                return status == 200 ? ("200", Encoding.UTF8.GetString(body)) : (status.ToString(), null);
            }
        }

        private static string? E621FileUrl(JsonElement post) =>
            post.TryGetProperty("file", out var file) && file.TryGetProperty("url", out var u) &&
            u.ValueKind == JsonValueKind.String ? u.GetString() : null;

        // Теги всех категорий без повторов, в формате _currentTags ("\n\n")
        private static string E621Tags(JsonElement post)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var sb = new StringBuilder();
            if (post.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                foreach (var cat in tags.EnumerateObject())
                    foreach (var t in cat.Value.EnumerateArray())
                    {
                        string? tag = t.GetString();
                        if (tag != null && seen.Add(tag))
                        { if (sb.Length > 0) sb.Append("\n\n"); sb.Append(tag); }
                    }
            return sb.ToString();
        }

        // Скачивает картинку и применяет результат (картинка + имя + теги)
        // одним шагом — только если за время загрузки не начата новая.
        private async Task LoadAndShowArtAsync(int version, CancellationToken token,
                                               string imgUrl, string tags, string postId)
        {
            // Видео (webm/mp4 и т.п.) BitmapImage не декодирует — раньше файл
            // целиком качался в память и падал с невнятной ошибкой кодека.
            if (IsVideoUrl(imgUrl))
            {
                ClearArt();
                SetStatus($"⚠ Пост {postId} — видео ({Path.GetExtension(new Uri(NormalizeUrl(imgUrl)).AbsolutePath).TrimStart('.')}). Поддерживаются только изображения.", error: true);
                return;
            }

            var (bitmap, bytes) = await LoadImageAsync(imgUrl, token);
            if (version != _loadVersion) return;

            string path = new Uri(NormalizeUrl(imgUrl)).AbsolutePath;
            _currentImageFileName = Path.GetFileNameWithoutExtension(path);
            _currentOriginalExt = Path.GetExtension(path).ToLowerInvariant();
            _currentOriginalBytes = bytes;
            _currentTags = tags;
            ShowArt(bitmap, postId);
        }

        // ════════════════════════════════════════════════
        //  ЗАГРУЗКА АРТА — rule34
        // ════════════════════════════════════════════════
        // С 2025 года api.rule34.xxx требует авторизации (user_id + api_key)
        // для ЛЮБОГО запроса — без ключей сервер отвечает HTTP 200 с телом
        // "<error>Missing authentication...</error>" вместо JSON, из-за чего
        // раньше показывалось ложное "пост не найден" даже при верных ключах.
        // Сама же страница поста на обычном rule34.xxx открывается и содержит
        // картинку и теги БЕЗ какой-либо авторизации — поэтому данные теперь
        // читаются прямо из уже загруженной в WebView2 страницы (JS), а не
        // через API.
        private async Task LoadRule34Async(string postId)
        {
            if (WebView.CoreWebView2 == null) return;
            var (version, token) = BeginLoad();
            try
            {
                SetStatus("⏳ Загрузка...");

                string json = await WebView.CoreWebView2.ExecuteScriptAsync(Rule34ExtractScript);
                if (version != _loadVersion) return;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string imgUrl = root.TryGetProperty("img", out var imgProp) ? (imgProp.GetString() ?? "") : "";
                string tagStr = root.TryGetProperty("tags", out var tagsProp) ? (tagsProp.GetString() ?? "") : "";

                if (string.IsNullOrEmpty(imgUrl))
                {
                    SetStatus("❌ rule34: не удалось найти изображение на странице поста", error: true);
                    return;
                }

                await LoadAndShowArtAsync(version, token, imgUrl, Rule34Tags(tagStr), postId);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (version == _loadVersion) SetStatus($"❌ rule34: {DescribeError(ex)}", error: true);
            }
        }

        // Разбор страницы поста rule34 — функция принимает документ, поэтому
        // работает и для открытой страницы, и для страницы, скачанной fetch'ем
        // при «Скачать всё» (DOMParser). Ссылки разрешаются от base явно:
        // у документа из DOMParser свой базовый адрес не совпадает со страницей поста.
        private const string Rule34ExtractFn = """
        function r34Extract(doc, base) {
            function abs(h) { try { return h ? new URL(h, base).href : ''; } catch (e) { return ''; } }
            function pickImageUrl() {
                var anchors = doc.querySelectorAll('a');
                for (var i = 0; i < anchors.length; i++) {
                    var t = (anchors[i].textContent || '').trim();
                    var h = anchors[i].getAttribute('href');
                    if ((t === 'Original image' || t === 'Original video' || t.indexOf('Original') === 0) && h) return abs(h);
                }
                var img = doc.getElementById('image');
                if (img && img.tagName === 'IMG' && img.getAttribute('src')) return abs(img.getAttribute('src'));
                var vid = doc.querySelector('video');
                if (vid) {
                    var s = vid.currentSrc || vid.getAttribute('src');
                    if (!s) { var src = vid.querySelector('source'); if (src) s = src.getAttribute('src'); }
                    if (s) return abs(s);
                }
                return '';
            }
            function pickTags() {
                // Только textarea формы редактирования поста: первое
                // input[name="tags"] на странице — это поле ПОИСКА, в нём
                // лежит поисковый запрос, а не теги поста.
                var el = doc.querySelector('textarea#tags, textarea[name="tags"]');
                var v = el ? (el.value || el.textContent || '').trim() : '';
                if (v) return v;
                var out = [], seen = {};
                var anchors = doc.querySelectorAll('#tag-sidebar li a[href*="tags="]');
                for (var i = 0; i < anchors.length; i++) {
                    try {
                        var u = new URL(anchors[i].getAttribute('href'), base);
                        if (u.searchParams.get('s') !== 'list') continue;
                        var tg = u.searchParams.get('tags');
                        if (tg && tg !== 'all' && tg !== 'video' && !seen[tg]) { seen[tg] = 1; out.push(tg); }
                    } catch (e) { }
                }
                return out.join(' ');
            }
            return { img: pickImageUrl(), tags: pickTags() };
        }
        """;

        // JS выполняется в контексте уже открытой страницы поста и возвращает
        // { img, tags } — без сети и без ключей.
        private const string Rule34ExtractScript =
            "(function () {" + Rule34ExtractFn + " return r34Extract(document, location.href); })();";

        private static string Rule34Tags(string spaceSeparated) =>
            string.Join("\n\n", spaceSeparated.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        // ════════════════════════════════════════════════
        //  ЗАГРУЗКА ИЗОБРАЖЕНИЯ
        // ════════════════════════════════════════════════
        private static string NormalizeUrl(string url) =>
            url.StartsWith("//") ? "https:" + url : url;

        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".webm", ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".swf", ".flv" };

        private static bool IsVideoUrl(string url) =>
            Uri.TryCreate(NormalizeUrl(url), UriKind.Absolute, out var u) &&
            VideoExtensions.Contains(Path.GetExtension(u.AbsolutePath));

        // Файлы крупнее не скачиваем в память целиком
        private const long MaxImageBytes = 100L * 1024 * 1024;

        private async Task<(BitmapImage bitmap, byte[] bytes)> LoadImageAsync(string url, CancellationToken token)
        {
            byte[] bytes = await DownloadImageWithFallbackAsync(url, token);
            return (DecodeImage(bytes), bytes);
        }

        private async Task<byte[]> DownloadImageWithFallbackAsync(string url, CancellationToken token)
        {
            url = NormalizeUrl(url);
            try
            {
                return await DownloadImageAsync(url, token);
            }
            catch (HttpRequestException) when (!token.IsCancellationRequested && CanFetchViaWebView(url))
            {
                // Тот же запасной путь, что и для JSON e621: сетевой стек браузера
                var (status, mediaType, body) = await WebViewFetchAsync(url);
                token.ThrowIfCancellationRequested();
                if (status != 200) throw new HttpRequestException($"изображение: HTTP {status}");
                CheckImageResponse(mediaType, body.Length);
                return body;
            }
        }

        private static async Task<byte[]> DownloadImageAsync(string url, CancellationToken token)
        {
            using var resp = await SendWithRetryAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (url.Contains("e621.net"))
                    req.Headers.Referrer = new Uri("https://e621.net/");
                else if (url.Contains("rule34.xxx") || url.Contains("booru.org"))
                    req.Headers.Referrer = new Uri("https://rule34.xxx/");
                req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                return req;
            }, token);
            resp.EnsureSuccessStatusCode();
            CheckImageResponse(resp.Content.Headers.ContentType?.MediaType, resp.Content.Headers.ContentLength);

            long len = resp.Content.Headers.ContentLength ?? 512 * 1024;
            using var net = await resp.Content.ReadAsStreamAsync(token);
            using var mem = new MemoryStream((int)Math.Min(len, 30 * 1024 * 1024));
            await net.CopyToAsync(mem, token);
            return mem.ToArray();
        }

        private static void CheckImageResponse(string? mediaType, long? length)
        {
            if (!string.IsNullOrEmpty(mediaType) && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                                                 && mediaType != "application/octet-stream")
                throw new NotSupportedException($"сервер вернул {mediaType}, а не изображение");
            if (length > MaxImageBytes)
                throw new NotSupportedException($"файл слишком большой ({length / 1024 / 1024} МБ)");
        }

        private static BitmapImage DecodeImage(byte[] bytes)
        {
            using var mem = new MemoryStream(bytes, writable: false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = mem;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        // ════════════════════════════════════════════════
        //  СЕТЬ: повторы и запасной путь через WebView2
        // ════════════════════════════════════════════════
        // Обрыв TLS-рукопожатия ("The SSL connection could not be established")
        // и сброс соединения при VPN/фильтрации провайдера часто разовые —
        // запрос повторяется на новом соединении. HttpRequestMessage нельзя
        // отправить дважды, поэтому он создаётся фабрикой на каждую попытку.
        private const int MaxAttempts = 3;

        private static async Task<HttpResponseMessage> SendWithRetryAsync(
            Func<HttpRequestMessage> makeRequest, CancellationToken token)
        {
            for (int attempt = 1; ; attempt++)
            {
                using var req = makeRequest();
                try
                {
                    return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
                }
                catch (HttpRequestException) when (attempt < MaxAttempts && !token.IsCancellationRequested)
                {
                    await Task.Delay(400 * attempt, token);
                }
            }
        }

        // Запасной путь возможен, только если встроенный браузер сейчас на
        // e621.net: fetch выполняется в контексте страницы (для API это тот же
        // origin, static1.e621.net отдаёт CORS для https://e621.net).
        private bool CanFetchViaWebView(string url) =>
            WebView.CoreWebView2 != null &&
            Uri.TryCreate(url, UriKind.Absolute, out var target) &&
            (target.Host.Equals("e621.net", StringComparison.OrdinalIgnoreCase) ||
             target.Host.EndsWith(".e621.net", StringComparison.OrdinalIgnoreCase)) &&
            Uri.TryCreate(WebView.CoreWebView2.Source, UriKind.Absolute, out var page) &&
            page.Host.Equals("e621.net", StringComparison.OrdinalIgnoreCase);

        // Загрузка через сетевой стек Chromium (тот же, что показывает сайт).
        // Runtime.evaluate с awaitPromise дожидается результата fetch —
        // ExecuteScriptAsync промисы не ждёт.
        private async Task<(int status, string mediaType, byte[] body)> WebViewFetchAsync(string url)
        {
            string script = "(async () => {" +
                $"const r = await fetch({JsonSerializer.Serialize(url)});" +
                "const b = new Uint8Array(await r.arrayBuffer()); let s = '';" +
                "for (let i = 0; i < b.length; i += 0x8000) s += String.fromCharCode.apply(null, b.subarray(i, i + 0x8000));" +
                "return { status: r.status, type: r.headers.get('content-type') || '', data: btoa(s) };" +
                "})()";
            var value = await EvaluateInPageAsync(script);
            string mediaType = (value.GetProperty("type").GetString() ?? "").Split(';')[0].Trim();
            return (value.GetProperty("status").GetInt32(), mediaType,
                    Convert.FromBase64String(value.GetProperty("data").GetString() ?? ""));
        }

        // Выполняет JS в контексте открытой страницы и дожидается промиса.
        // Если страница как раз сменилась, контекст выполнения мог пропасть —
        // одна повторная попытка.
        private async Task<JsonElement> EvaluateInPageAsync(string expression)
        {
            if (WebView.CoreWebView2 == null) throw new HttpRequestException("браузер ещё не готов");
            string args = JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true });
            for (int attempt = 1; ; attempt++)
            {
                string result = await WebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args);
                using var doc = JsonDocument.Parse(result);
                var root = doc.RootElement;
                if (root.TryGetProperty("exceptionDetails", out var exc))
                {
                    if (attempt < 2) { await Task.Delay(500); continue; }
                    string text = exc.TryGetProperty("exception", out var e) && e.TryGetProperty("description", out var d)
                        ? d.GetString() ?? "" : exc.GetProperty("text").GetString() ?? "";
                    throw new HttpRequestException($"браузер: {text.Split('\n')[0]}");
                }
                return root.GetProperty("result").GetProperty("value").Clone();
            }
        }

        // Для строки статуса: вместо "see inner exception" — настоящая причина
        private static string DescribeError(Exception ex)
        {
            var msgs = new List<string>();
            for (var e = ex; e != null; e = e.InnerException)
            {
                string m = e.Message.Replace(", see inner exception.", "")
                                    .Replace("See the inner exception for details.", "").Trim();
                if (m.Length > 0 && !msgs.Contains(m)) msgs.Add(m);
            }
            return string.Join(" → ", msgs);
        }

        private void ShowArt(BitmapImage bitmap, string postId)
        {
            ArtImage.Source = bitmap;
            ArtImage.UpdateLayout();

            TxtCropHint.Visibility = Visibility.Collapsed;
            CropRect.Visibility = Visibility.Visible;
            UpdateResizeHandleVisibility();
            TxtImageSize.Text = $"Арт: {bitmap.PixelWidth} × {bitmap.PixelHeight} px";
            WorkArea.ToolTip = CropHelpText;
            SetArtControlsEnabled(true);
            UpdateTagsPreview();

            bool clamped = SetupCropAfterLoad();

            // Строка "<id> загружен" ждёт Art Saver — дополнения только после неё
            string msg = $"✅ {postId} загружен";
            if (AlreadySaved(_currentImageFileName, _currentOriginalExt)) msg += "  · уже есть в папке сохранения";
            if (clamped) msg += "  · арт меньше зафиксированной рамки, рамка уменьшена";
            SetStatus(msg);
        }

        private const string CropHelpText =
            "Перетаскивание или клик — переместить рамку, колесо мыши — размер.\n" +
            "Стрелки — сдвиг на 1 px (Shift — на 10 px), Ctrl+стрелки — размер.";

        private void ClearArt()
        {
            CancelLoad();
            EndCropDrag();
            ArtImage.Source = null;
            _cropPx = new Rect();
            _currentOriginalBytes = Array.Empty<byte>();
            _currentOriginalExt = "";
            _currentImageFileName = "";
            _currentTags = "";
            CropRect.Visibility = Visibility.Collapsed;
            ResizeHandle.Visibility = Visibility.Collapsed;
            TxtCropHint.Visibility = Visibility.Visible;
            TxtImageSize.Text = "";
            WorkArea.ToolTip = null;
            SetArtControlsEnabled(false);
            TxtCropW.Text = TxtCropH.Text = "";
            UpdateTagsPreview();
            SetStatus("");
            HideOverlays();
        }

        // Кнопки и поля, которые имеют смысл только при загруженном арте
        private void SetArtControlsEnabled(bool on)
        {
            BtnSaveCrop.IsEnabled = on;
            BtnSaveOriginal.IsEnabled = on;
            TxtCropW.IsEnabled = on;
            TxtCropH.IsEnabled = on;
        }

        private bool AlreadySaved(string baseName, string ext)
        {
            if (string.IsNullOrEmpty(_savePath) || string.IsNullOrEmpty(baseName)) return false;
            try
            {
                return (ext.Length > 0 && File.Exists(Path.Combine(_savePath, baseName + ext))) ||
                       File.Exists(Path.Combine(_savePath, baseName + ".png")) ||
                       File.Exists(Path.Combine(_savePath, baseName + "_crop.png"));
            }
            catch { return false; }
        }

        // Превью тегов показывает ровно то, что попадёт в .txt (главный тег,
        // фильтр Civitai, без повторов), а исключённые теги — отдельно.
        private void UpdateTagsPreview()
        {
            if (string.IsNullOrEmpty(_currentTags))
            {
                TxtTagsPreview.Text = ArtImage.Source == null ? "(теги не загружены)" : "(нет тегов)";
                TxtTagsRemoved.Visibility = Visibility.Collapsed;
                TagsExpander.Header = "Теги текущего арта";
                return;
            }

            var all = _currentTags.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
            var removed = all.Where(TagFilter.IsBlocked).ToList();
            string caption = Caption.Build(_mainTag, all.Where(t => !TagFilter.IsBlocked(t)));
            int count = caption.Length == 0 ? 0 : caption.Split(", ").Length;

            TxtTagsPreview.Text = caption.Length > 0 ? caption : "(нет тегов)";
            TagsExpander.Header = $"Теги текущего арта ({count})";
            if (removed.Count > 0)
            {
                TxtTagsRemoved.Text = $"Исключены фильтром Civitai ({removed.Count}): " + string.Join(", ", removed);
                TxtTagsRemoved.Visibility = Visibility.Visible;
            }
            else TxtTagsRemoved.Visibility = Visibility.Collapsed;
        }

        // ════════════════════════════════════════════════
        //  КРОП
        // ════════════════════════════════════════════════
        // Рамка хранится в ПИКСЕЛЯХ исходной картинки (_cropPx), а экранная
        // рамка лишь её проекция. Поэтому изменение размеров панели (окно,
        // GridSplitter, полоса прокрутки) не сдвигает выделение, а сохраняемый
        // кроп всегда совпадает с показанным и имеет указанный размер в px.
        private Rect _cropPx;

        // Минимальный размер рамки при работе мышью: ~24 точки на экране, чтобы
        // её можно было ухватить, но не больше 64 px арта — иначе на крупных артах
        // (масштаб 0.1) рамку нельзя было сделать меньше нескольких сотен px.
        private const double MinCropScreen = 24;
        private const double MinCropPx = 8;
        private const double MaxMinCropPx = 64;

        private static double MinCropSize(double k) =>
            Math.Max(MinCropPx, Math.Min(Math.Ceiling(MinCropScreen / k), MaxMinCropPx));

        // Картинка вписывается в поле целиком, в том числе с увеличением:
        // так мелкие арты и арты с DPI ≠ 96 в метаданных не выглядят крошечными.
        private Rect GetImageRect()
        {
            if (ArtImage.Source == null || WorkArea.ActualWidth == 0) return new Rect();
            var src = ArtImage.Source;
            double ratio = Math.Min(WorkArea.ActualWidth / src.Width,
                                    WorkArea.ActualHeight / src.Height);
            double w = src.Width * ratio;
            double h = src.Height * ratio;
            return new Rect((WorkArea.ActualWidth - w) / 2,
                            (WorkArea.ActualHeight - h) / 2, w, h);
        }

        private Size SourcePixels() =>
            ArtImage.Source is BitmapSource b ? new Size(b.PixelWidth, b.PixelHeight) : new Size();

        // экранных единиц на один пиксель картинки
        private double ViewScale(Rect img)
        {
            var px = SourcePixels();
            return px.Width > 0 ? img.Width / px.Width : 0;
        }

        private Rect CropViewRect(Rect img, double k) =>
            new(img.X + _cropPx.X * k, img.Y + _cropPx.Y * k, _cropPx.Width * k, _cropPx.Height * k);

        // Возвращает true, если зафиксированный размер пришлось уменьшить под арт
        private bool SetupCropAfterLoad()
        {
            var px = SourcePixels();
            if (px.Width == 0) return false;

            double w, h;
            if (_lockCropSize) { w = _lockedW; h = _lockedH; }
            else w = h = Math.Max(1, Math.Round(Math.Min(px.Width, px.Height) / 2)); // квадрат в половину арта
            bool clamped = w > px.Width || h > px.Height;
            w = Math.Min(w, px.Width);
            h = Math.Min(h, px.Height);
            _cropPx = new Rect(Math.Round((px.Width - w) / 2), Math.Round((px.Height - h) / 2), w, h);
            ApplyCropToView();
            return clamped;
        }

        // Поле кропа не сжимается меньше 140 точек: если окну не хватает высоты,
        // прокручивается блок управления, а не обрезается его низ.
        private void RightPanel_SizeChanged(object s, SizeChangedEventArgs e)
        {
            double header = RightPanel.RowDefinitions[0].ActualHeight;
            double cropMin = RightPanel.RowDefinitions[1].MinHeight;
            ControlsScroll.MaxHeight = Math.Max(80, RightPanel.ActualHeight - header - cropMin);
        }

        private void WorkArea_SizeChanged(object s, SizeChangedEventArgs e)
        {
            if (ArtImage.Source != null) ApplyCropToView();
        }

        // Проецирует _cropPx на экран: рамка, ручка, затемнение, поля размера
        private void ApplyCropToView()
        {
            Rect img = GetImageRect();
            double k = ViewScale(img);
            if (k == 0) return;

            Rect v = CropViewRect(img, k);
            Canvas.SetLeft(CropRect, v.X);
            Canvas.SetTop(CropRect, v.Y);
            CropRect.Width = v.Width;
            CropRect.Height = v.Height;
            UpdateHandlePosition();
            UpdateOverlays();
            ShowCropSizeFields(force: false);
        }

        // Поле, в котором пользователь сейчас печатает, не перезаписывается
        private void ShowCropSizeFields(bool force)
        {
            if (ArtImage.Source == null) { TxtCropW.Text = TxtCropH.Text = ""; return; }
            if (force || !TxtCropW.IsKeyboardFocused) TxtCropW.Text = _cropPx.Width.ToString("F0");
            if (force || !TxtCropH.IsKeyboardFocused) TxtCropH.Text = _cropPx.Height.ToString("F0");
        }

        // Перемещает рамку (координаты в px арта), не выпуская за края
        private void MoveCropTo(double x, double y)
        {
            var px = SourcePixels();
            if (px.Width == 0) return;
            _cropPx.X = Math.Clamp(Math.Round(x), 0, Math.Max(0, px.Width - _cropPx.Width));
            _cropPx.Y = Math.Clamp(Math.Round(y), 0, Math.Max(0, px.Height - _cropPx.Height));
            ApplyCropToView();
        }

        // Задаёт размер рамки (в px арта), сохраняя её центр
        private void SetCropSize(double w, double h)
        {
            var px = SourcePixels();
            if (px.Width == 0) return;
            w = Math.Clamp(Math.Round(w), 1, px.Width);
            h = Math.Clamp(Math.Round(h), 1, px.Height);
            double cx = _cropPx.X + _cropPx.Width / 2, cy = _cropPx.Y + _cropPx.Height / 2;
            _cropPx = new Rect(Math.Clamp(Math.Round(cx - w / 2), 0, px.Width - w),
                               Math.Clamp(Math.Round(cy - h / 2), 0, px.Height - h), w, h);
            ApplyCropToView();
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

        // При зафиксированном размере ручка скрыта: раньше она показывала
        // курсор ресайза, но ничего не делала.
        private void UpdateResizeHandleVisibility() =>
            ResizeHandle.Visibility = ArtImage.Source != null && !_lockCropSize
                ? Visibility.Visible : Visibility.Collapsed;

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

        // ── Мышь ──
        // Флаги перетаскивания сбрасываются и при потере захвата мыши
        // (Alt+Tab, отпускание кнопки за пределами окна) — иначе рамка
        // продолжала ездить за курсором без нажатой кнопки.
        private void EndCropDrag()
        {
            _isDragging = false;
            _isResizing = false;
            if (CropCanvas.IsMouseCaptured) CropCanvas.ReleaseMouseCapture();
            if (ResizeHandle.IsMouseCaptured) ResizeHandle.ReleaseMouseCapture();
        }

        private void CropCanvas_MouseDown(object s, MouseButtonEventArgs e)
        {
            if (ArtImage.Source == null) return;
            WorkArea.Focus();
            Rect img = GetImageRect();
            double k = ViewScale(img);
            if (k == 0) return;

            Point p = e.GetPosition(CropCanvas);
            Rect view = CropViewRect(img, k);
            Rect grab = view;
            grab.Inflate(6, 6); // маленькую рамку можно ухватить с запасом
            if (!grab.Contains(p))
            {
                // Клик мимо рамки переносит её центром в точку клика
                MoveCropTo((p.X - img.X) / k - _cropPx.Width / 2,
                           (p.Y - img.Y) / k - _cropPx.Height / 2);
                view = CropViewRect(img, k);
            }

            _dragOffset = new Point(p.X - view.X, p.Y - view.Y);
            _isDragging = CropCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void CropCanvas_MouseMove(object s, MouseEventArgs e)
        {
            if (!_isDragging) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndCropDrag(); return; }
            Rect img = GetImageRect();
            double k = ViewScale(img);
            if (k == 0) return;

            Point p = e.GetPosition(CropCanvas);
            MoveCropTo((p.X - _dragOffset.X - img.X) / k, (p.Y - _dragOffset.Y - img.Y) / k);
        }

        private void CropCanvas_MouseUp(object s, MouseButtonEventArgs e) => EndCropDrag();

        private void CropCanvas_LostMouseCapture(object s, MouseEventArgs e) => _isDragging = false;

        private void ResizeHandle_MouseDown(object s, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_lockCropSize || ArtImage.Source == null) return;
            WorkArea.Focus();
            _isResizing = ResizeHandle.CaptureMouse();
        }

        private void ResizeHandle_MouseMove(object s, MouseEventArgs e)
        {
            if (!_isResizing) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndCropDrag(); return; }
            Rect img = GetImageRect();
            double k = ViewScale(img);
            if (k == 0) return;
            var px = SourcePixels();

            Point p = e.GetPosition(CropCanvas);
            double minPx = MinCropSize(k);
            double w = Math.Round((p.X - (img.X + _cropPx.X * k)) / k);
            double h = Math.Round((p.Y - (img.Y + _cropPx.Y * k)) / k);
            _cropPx.Width = Math.Min(Math.Max(minPx, w), px.Width - _cropPx.X);
            _cropPx.Height = Math.Min(Math.Max(minPx, h), px.Height - _cropPx.Y);
            ApplyCropToView();
        }

        private void ResizeHandle_MouseUp(object s, MouseButtonEventArgs e)
        {
            EndCropDrag();
            e.Handled = true;
        }

        private void ResizeHandle_LostMouseCapture(object s, MouseEventArgs e) => _isResizing = false;

        // Колесо мыши — размер рамки (±10 %, пропорции сохраняются)
        private void WorkArea_MouseWheel(object s, MouseWheelEventArgs e)
        {
            if (ArtImage.Source == null || _lockCropSize || _isDragging || _isResizing) return;
            e.Handled = true;
            double k = ViewScale(GetImageRect());
            if (k == 0) return;
            var px = SourcePixels();

            double f = e.Delta > 0 ? 1.1 : 1 / 1.1;
            double min = Math.Min(MinCropSize(k), Math.Min(px.Width, px.Height));
            double w = _cropPx.Width * f, h = _cropPx.Height * f;
            double grow = Math.Max(1, Math.Max(min / w, min / h));        // не мельче минимума
            double fit = Math.Min(1, Math.Min(px.Width / (w * grow), px.Height / (h * grow))); // не больше арта
            SetCropSize(w * grow * fit, h * grow * fit);
        }

        // ── Клавиатура (поле кропа в фокусе после клика по нему) ──
        private void WorkArea_PreviewKeyDown(object s, KeyEventArgs e)
        {
            if (ArtImage.Source == null) return;
            int dx = 0, dy = 0;
            switch (e.Key)
            {
                case Key.Left: dx = -1; break;
                case Key.Right: dx = 1; break;
                case Key.Up: dy = -1; break;
                case Key.Down: dy = 1; break;
                default: return;
            }
            e.Handled = true;
            int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (_lockCropSize) return;
                // Размер меняется от правого нижнего угла, как ручкой
                var px = SourcePixels();
                _cropPx.Width = Math.Clamp(_cropPx.Width + dx * step, 1, px.Width - _cropPx.X);
                _cropPx.Height = Math.Clamp(_cropPx.Height + dy * step, 1, px.Height - _cropPx.Y);
                ApplyCropToView();
            }
            else MoveCropTo(_cropPx.X + dx * step, _cropPx.Y + dy * step);
        }

        // ── Точный размер рамки ──
        private void CropSizeBox_PreviewTextInput(object s, TextCompositionEventArgs e) =>
            e.Handled = !e.Text.All(char.IsAsciiDigit);

        private void CropSizeBox_KeyDown(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) ApplyCropSizeFields();
            else if (e.Key == Key.Escape) ShowCropSizeFields(force: true);
            else return;
            e.Handled = true;
            WorkArea.Focus();
        }

        private void CropSizeBox_LostKeyboardFocus(object s, KeyboardFocusChangedEventArgs e) => ApplyCropSizeFields();

        private void ApplyCropSizeFields()
        {
            if (ArtImage.Source == null) return;
            if (int.TryParse(TxtCropW.Text, out int w) && int.TryParse(TxtCropH.Text, out int h) &&
                w > 0 && h > 0 && (w != (int)_cropPx.Width || h != (int)_cropPx.Height))
            {
                SetCropSize(w, h);
                // При зафиксированном размере введённое значение становится новым фиксированным
                if (_lockCropSize)
                {
                    _lockedW = _cropPx.Width;
                    _lockedH = _cropPx.Height;
                    SyncLockSizeButton();
                    SaveSettings();
                }
            }
            ShowCropSizeFields(force: true);
        }

        // ════════════════════════════════════════════════
        //  КНОПКИ ПРАВОЙ ПАНЕЛИ
        // ════════════════════════════════════════════════
        private void BtnLockSize_Click(object s, RoutedEventArgs e)
        {
            _lockCropSize = !_lockCropSize;
            if (_lockCropSize && _cropPx.Width > 0)
            {
                _lockedW = _cropPx.Width;
                _lockedH = _cropPx.Height;
            }
            SyncLockSizeButton();
            SaveSettings();
        }

        // Приводит текст/стиль кнопки в соответствие с текущим _lockCropSize —
        // вызывается и при клике, и при старте приложения (после LoadSettings),
        // иначе после перезапуска с ранее включённой фиксацией кнопка визуально
        // показывала бы "выключено", хотя размер рамки уже зафиксирован.
        private void SyncLockSizeButton()
        {
            if (_lockCropSize)
            {
                BtnLockSize.Content = $"✔ Зафиксировано ({_lockedW:F0}×{_lockedH:F0} px)";
                BtnLockSize.Style = (Style)FindResource("YellowBtn");
            }
            else
            {
                BtnLockSize.Content = "Фиксировать размер рамки";
                BtnLockSize.Style = (Style)FindResource("BaseBtn");
            }
            UpdateResizeHandleVisibility();
        }

        private void TxtMainTag_TextChanged(object s, TextChangedEventArgs e)
        {
            _mainTag = TxtMainTag.Text;
            UpdateTagsPreview();
            _saveSettingsTimer.Stop();
            _saveSettingsTimer.Start();
        }

        private void TxtSavePath_MouseLeftButtonUp(object s, MouseButtonEventArgs e)
        {
            if (string.IsNullOrEmpty(_savePath) || !Directory.Exists(_savePath)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = _savePath, UseShellExecute = true });
            }
            catch (Exception ex) { SetStatus($"⚠ Не удалось открыть папку: {ex.Message}", error: true); }
        }

        // ── Горячие клавиши ──
        // Нажатия внутри WebView2 тоже приходят сюда (WPF-обёртка пересылает
        // сочетания с Ctrl), поэтому Ctrl+S не открывает «Сохранить страницу».
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        // Keyboard.Modifiers не видит Ctrl/Shift, нажатые, пока фокус был внутри
        // WebView2 (другое окно/процесс), поэтому дополнительно читается
        // физическое состояние клавиш.
        private static bool IsDown(ModifierKeys mod, int vk) =>
            Keyboard.Modifiers.HasFlag(mod) || (GetAsyncKeyState(vk) & 0x8000) != 0;
        private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

        private void Window_PreviewKeyDown(object s, KeyEventArgs e)
        {
            if (!IsDown(ModifierKeys.Control, VK_CONTROL) || IsDown(ModifierKeys.Alt, VK_MENU)) return;
            switch (e.Key)
            {
                case Key.S:
                    bool original = IsDown(ModifierKeys.Shift, VK_SHIFT);
                    if ((original ? BtnSaveOriginal : BtnSaveCrop).IsEnabled) SaveArt(crop: !original);
                    e.Handled = true;
                    break;
                case Key.L:
                    TxtAddress.Focus();
                    TxtAddress.SelectAll();
                    e.Handled = true;
                    break;
            }
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
                _e621Login, _e621ApiKey)
            { Owner = this };

            if (dlg.ShowDialog() == true)
            {
                _e621Login = dlg.E621Login;
                _e621ApiKey = dlg.E621ApiKey;
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

            // Снимок состояния до await: пока PNG кодируется, пользователь
            // может перейти к другому посту, и поля поменяются.
            // Кроп и оригинал — разные файлы, чтобы не перезаписывать друг друга;
            // у каждого свой .txt с тем же базовым именем.
            string baseName = crop ? _currentImageFileName + "_crop" : _currentImageFileName;
            string tags = _currentTags;
            byte[] originalBytes = _currentOriginalBytes;
            string originalExt = _currentOriginalExt;

            try
            {
                if (ArtImage.Source is not BitmapSource src) return;
                Directory.CreateDirectory(_savePath);

                string imgPath;
                bool overwritten;
                if (!crop && originalBytes.Length > 0 && originalExt.Length > 0)
                {
                    // Оригинал — байт в байт, как отдал сервер (без перекодирования в PNG)
                    imgPath = Path.Combine(_savePath, baseName + originalExt);
                    overwritten = File.Exists(imgPath);
                    await File.WriteAllBytesAsync(imgPath, originalBytes);
                }
                else
                {
                    BitmapSource imgToSave = src;
                    if (crop)
                    {
                        int x = Math.Clamp((int)_cropPx.X, 0, src.PixelWidth - 1);
                        int y = Math.Clamp((int)_cropPx.Y, 0, src.PixelHeight - 1);
                        int w = Math.Clamp((int)_cropPx.Width, 1, src.PixelWidth - x);
                        int h = Math.Clamp((int)_cropPx.Height, 1, src.PixelHeight - y);
                        imgToSave = new CroppedBitmap(src, new Int32Rect(x, y, w, h));
                    }
                    // Кодирование идёт в Task.Run: незамороженный CroppedBitmap
                    // принадлежит UI-потоку и там падал с InvalidOperationException.
                    if (imgToSave.CanFreeze) imgToSave.Freeze();

                    imgPath = Path.Combine(_savePath, baseName + ".png");
                    overwritten = File.Exists(imgPath);
                    await Task.Run(() =>
                    {
                        using var fs = File.Create(imgPath);
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(imgToSave));
                        enc.Save(fs);
                    });
                }

                int removed = await WriteCaptionAsync(Path.Combine(_savePath, baseName + ".txt"), tags);

                // Art Saver ищет в статусе "ОРИГИНАЛ СОХРАНЁН" — пояснения только в скобках после
                var notes = new List<string>();
                if (overwritten) notes.Add("файл перезаписан");
                if (removed > 0) notes.Add($"исключено запрещённых тегов: {removed}");
                string saved = crop ? "✔ КРОП СОХРАНЁН" : "✔ ОРИГИНАЛ СОХРАНЁН";
                SetStatus(notes.Count > 0 ? $"{saved} ({string.Join("; ", notes)})" : saved);
                ClearStatusLater(notes.Count > 0 ? 4000 : 2500);
            }
            catch (Exception ex) { SetStatus($"❌ {ex.Message}", error: true); }
        }

        // В .txt попадают только теги, разрешённые Civitai, в формате, который
        // Civitai сам разбивает на теги (см. Caption). Возвращает число исключённых.
        private async Task<int> WriteCaptionAsync(string path, string tags)
        {
            var (allowedTags, removed) = TagFilter.Filter(tags);
            string caption = Caption.Build(_mainTag,
                allowedTags.Split("\n\n", StringSplitOptions.RemoveEmptyEntries));
            // UTF-8 без BOM: BOM стал бы частью первого тега
            await File.WriteAllTextAsync(path, caption, new UTF8Encoding(false));
            return removed;
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
        private void UpdateSavePathLabel()
        {
            bool has = !string.IsNullOrEmpty(_savePath);
            TxtSavePath.Text = has ? _savePath : "(папка не выбрана)";
            TxtSavePath.ToolTip = has ? "Открыть папку в проводнике" : null;
            TxtSavePath.Cursor = has ? Cursors.Hand : null;
            // Папка датасета «Зеркала» — всегда папка сохранения
            TxtMirrorFolder.Text = has ? _savePath : "";
        }

        // Номер текущего сообщения: отложенная очистка стирает только «своё»
        // сообщение, а не то, что появилось позже (например, ошибку загрузки).
        private int _statusVersion;

        private void SetStatus(string msg, bool error = false)
        {
            _statusVersion++;
            TxtStatus.Foreground = (Brush)FindResource(
                error ? "StatusError" : msg.StartsWith("⏳") ? "TextMuted" : "StatusOk");
            TxtStatus.Text = msg;
            // Длинное сообщение обрезается многоточием — целиком видно в подсказке
            TxtStatus.ToolTip = string.IsNullOrEmpty(msg) ? null : msg;
        }

        private async void ClearStatusLater(int delayMs)
        {
            int version = _statusVersion;
            await Task.Delay(delayMs);
            if (version != _statusVersion) return;
            TxtStatus.Text = "";
            TxtStatus.ToolTip = null;
        }

        // ════════════════════════════════════════════════
        //  НАСТРОЙКИ
        // ════════════════════════════════════════════════
        private string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ArtFinder", "settings.json");

        private void SaveSettings()
        {
            // Вызывается из обработчиков UI: необработанное исключение (файл
            // занят антивирусом/синхронизацией, нет прав) роняло бы приложение.
            try
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
                    window = CaptureWindowPlacement(),
                    tools = CaptureToolSettings()
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                SetStatus($"⚠ Не удалось сохранить настройки: {ex.Message}", error: true);
            }
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
                UpdateHttpAuth();

                if (r.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Object)
                    _toolSettings = tools.Clone();

                if (r.TryGetProperty("window", out var w) && w.ValueKind == JsonValueKind.Object)
                {
                    double D(string k) => w.TryGetProperty(k, out var v) && v.TryGetDouble(out var d) ? d : double.NaN;
                    _placement = new WindowPlacement(D("left"), D("top"), D("width"), D("height"),
                        w.TryGetProperty("maximized", out var m) && m.ValueKind == JsonValueKind.True,
                        D("split"));
                }
            }
            catch { }
        }

        // ── Размер и положение окна, ширина панелей ──
        private sealed record WindowPlacement(double Left, double Top, double Width, double Height,
                                              bool Maximized, double Split);

        private WindowPlacement? _placement;

        private object CaptureWindowPlacement()
        {
            Rect b = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            double l = ColLeft.ActualWidth, rw = ColRight.ActualWidth;
            if (b.IsEmpty || double.IsNaN(b.Width) || b.Width <= 0)
                return _placement is null ? new { } : (object)new
                {
                    left = _placement.Left, top = _placement.Top, width = _placement.Width,
                    height = _placement.Height, maximized = _placement.Maximized, split = _placement.Split
                };
            return new
            {
                left = b.Left, top = b.Top, width = b.Width, height = b.Height,
                maximized = WindowState == WindowState.Maximized,
                split = l + rw > 0 ? Math.Round(l / (l + rw), 4) : 0.6875
            };
        }

        private void ApplyWindowPlacement()
        {
            if (_placement is not { } p) return;

            if (p.Split is > 0.1 and < 0.95)
            {
                ColLeft.Width = new GridLength(p.Split, GridUnitType.Star);
                ColRight.Width = new GridLength(1 - p.Split, GridUnitType.Star);
            }

            // Окно восстанавливается, только если оно хотя бы частично видно
            // на текущих мониторах (монитор могли отключить).
            if (double.IsNaN(p.Width) || double.IsNaN(p.Height) || double.IsNaN(p.Left) || double.IsNaN(p.Top)) return;
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                  SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var wnd = new Rect(p.Left, p.Top, Math.Max(MinWidth, p.Width), Math.Max(MinHeight, p.Height));
            var visible = Rect.Intersect(screen, wnd);
            if (visible.IsEmpty || visible.Width < 200 || visible.Height < 100) return;

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = wnd.Left; Top = wnd.Top; Width = wnd.Width; Height = wnd.Height;
            if (p.Maximized) WindowState = WindowState.Maximized;
        }
    }
}
