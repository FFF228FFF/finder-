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
            TxtMainTag.Text = _mainTag;
            UpdateSavePathLabel();
            SyncLockSizeButton();

            // Главный тег сохраняется не на каждое нажатие, а через паузу после ввода
            _saveSettingsTimer.Tick += (s, e) => { _saveSettingsTimer.Stop(); SaveSettings(); };
            Closing += (s, e) => { if (_saveSettingsTimer.IsEnabled) { _saveSettingsTimer.Stop(); SaveSettings(); } };

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
                _webViewEnv = await CoreWebView2Environment.CreateAsync();
                await WebView.EnsureCoreWebView2Async(_webViewEnv);

                WebView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                WebView.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
                WebView.SourceChanged += OnSourceChanged;
                WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

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

                string apiUrl = $"https://e621.net/posts/{postId}.json";
                string json;
                try
                {
                    using var resp = await SendWithRetryAsync(() =>
                    {
                        var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                        req.Headers.Add("User-Agent", $"ArtFinder/2.2 (by {(!string.IsNullOrEmpty(_e621Login) ? _e621Login : "Anonymous")} on e621)");
                        if (!string.IsNullOrEmpty(_cachedBase64Auth))
                            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", _cachedBase64Auth);
                        return req;
                    }, token);
                    if (version != _loadVersion) return;

                    if (!resp.IsSuccessStatusCode)
                    {
                        SetStatus($"❌ e621 API: {(int)resp.StatusCode} {resp.ReasonPhrase}", error: true);
                        return;
                    }
                    json = await resp.Content.ReadAsStringAsync(token);
                }
                catch (HttpRequestException) when (CanFetchViaWebView(apiUrl))
                {
                    // Прямое соединение не установилось (SSL/сеть — типично при
                    // VPN/фильтрации провайдера), а встроенный браузер страницу
                    // открыл — берём JSON его сетевым стеком.
                    if (version != _loadVersion) return;
                    SetStatus("⏳ Загрузка через браузер...");
                    var (status, _, body) = await WebViewFetchAsync(apiUrl);
                    if (version != _loadVersion) return;
                    if (status != 200)
                    {
                        SetStatus($"❌ e621 API: {status}", error: true);
                        return;
                    }
                    json = Encoding.UTF8.GetString(body);
                }

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

                var seen = new HashSet<string>(StringComparer.Ordinal);
                var sb = new StringBuilder();
                foreach (var cat in post.GetProperty("tags").EnumerateObject())
                    foreach (var t in cat.Value.EnumerateArray())
                    {
                        string? tag = t.GetString();
                        if (tag != null && seen.Add(tag))
                        { if (sb.Length > 0) sb.Append("\n\n"); sb.Append(tag); }
                    }

                await LoadAndShowArtAsync(version, token, imgUrl, sb.ToString(), postId);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (version == _loadVersion) SetStatus($"❌ e621: {DescribeError(ex)}", error: true);
            }
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

                var sb = new StringBuilder();
                foreach (var tag in tagStr.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (sb.Length > 0) sb.Append("\n\n");
                    sb.Append(tag);
                }

                await LoadAndShowArtAsync(version, token, imgUrl, sb.ToString(), postId);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (version == _loadVersion) SetStatus($"❌ rule34: {DescribeError(ex)}", error: true);
            }
        }

        // JS выполняется в контексте уже открытой страницы поста и возвращает
        // { img, tags } — без сети и без ключей.
        // img: сначала ищем ссылку "Original image"/"Original video" (это то,
        //   что видит сам пользователь на странице), затем <img id="image">
        //   и <video> как запасные варианты.
        // tags: сначала берём готовое значение из textarea#tags (форма
        //   редактирования) — тот же формат, что раньше отдавало API;
        //   если его нет, собираем теги из ссылок #tag-sidebar (page=post&s=list&tags=...).
        private const string Rule34ExtractScript = """
        (function () {
            function pickImageUrl() {
                var anchors = document.querySelectorAll('a');
                for (var i = 0; i < anchors.length; i++) {
                    var t = (anchors[i].textContent || '').trim();
                    if ((t === 'Original image' || t === 'Original video' || t.indexOf('Original') === 0) && anchors[i].href) {
                        return anchors[i].href;
                    }
                }
                var img = document.getElementById('image');
                if (img && img.tagName === 'IMG' && img.src) return img.src;
                var vid = document.querySelector('video');
                if (vid) {
                    var s = vid.currentSrc || vid.src;
                    if (!s) { var src = vid.querySelector('source'); if (src) s = src.src; }
                    if (s) return s;
                }
                return '';
            }
            function pickTags() {
                // Только textarea формы редактирования поста: первое
                // input[name="tags"] на странице — это поле ПОИСКА, в нём
                // лежит поисковый запрос, а не теги поста.
                var el = document.querySelector('textarea#tags, textarea[name="tags"]');
                if (el && el.value.trim()) return el.value.trim();
                var out = [], seen = {};
                var anchors = document.querySelectorAll('#tag-sidebar li a[href*="tags="]');
                for (var i = 0; i < anchors.length; i++) {
                    try {
                        var u = new URL(anchors[i].href, location.href);
                        if (u.searchParams.get('s') !== 'list') continue;
                        var tg = u.searchParams.get('tags');
                        if (tg && tg !== 'all' && tg !== 'video' && !seen[tg]) { seen[tg] = 1; out.push(tg); }
                    } catch (e) { }
                }
                return out.join(' ');
            }
            return { img: pickImageUrl(), tags: pickTags() };
        })();
        """;

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
            url = NormalizeUrl(url);

            byte[] bytes;
            try
            {
                bytes = await DownloadImageAsync(url, token);
            }
            catch (HttpRequestException) when (!token.IsCancellationRequested && CanFetchViaWebView(url))
            {
                // Тот же запасной путь, что и для JSON e621: сетевой стек браузера
                var (status, mediaType, body) = await WebViewFetchAsync(url);
                token.ThrowIfCancellationRequested();
                if (status != 200) throw new HttpRequestException($"изображение: HTTP {status}");
                CheckImageResponse(mediaType, body.Length);
                bytes = body;
            }

            return (DecodeImage(bytes), bytes);
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
            string args = JsonSerializer.Serialize(new { expression = script, awaitPromise = true, returnByValue = true });

            // Если страница как раз сменилась, контекст выполнения мог
            // пропасть — одна повторная попытка.
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
                var value = root.GetProperty("result").GetProperty("value");
                string mediaType = (value.GetProperty("type").GetString() ?? "").Split(';')[0].Trim();
                return (value.GetProperty("status").GetInt32(), mediaType,
                        Convert.FromBase64String(value.GetProperty("data").GetString() ?? ""));
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

            // В превью теги через запятую (в .txt формат прежний — через пустую строку)
            TxtTagsPreview.Text = string.IsNullOrEmpty(_currentTags)
                ? "(нет тегов)"
                : string.Join(", ", _currentTags.Split("\n\n"));
            CropRect.Visibility = Visibility.Visible;
            ResizeHandle.Visibility = Visibility.Visible;

            SetupCropAfterLoad();
            SetStatus($"✅ {postId} загружен");
        }

        private void ClearArt()
        {
            CancelLoad();
            ArtImage.Source = null;
            _cropPx = new Rect();
            _currentOriginalBytes = Array.Empty<byte>();
            _currentOriginalExt = "";
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
        // Рамка хранится в ПИКСЕЛЯХ исходной картинки (_cropPx), а экранная
        // рамка лишь её проекция. Поэтому изменение размеров панели (окно,
        // GridSplitter, полоса прокрутки) не сдвигает выделение, а сохраняемый
        // кроп всегда совпадает с показанным и имеет указанный размер в px.
        private Rect _cropPx;

        // Минимальный размер рамки на экране, чтобы её можно было ухватить
        private const double MinCropScreen = 32;

        private Rect GetImageRect()
        {
            if (ArtImage.Source == null || WorkArea.ActualWidth == 0) return new Rect();
            var src = ArtImage.Source;
            double ratio = Math.Min(WorkArea.ActualWidth / src.Width,
                                    WorkArea.ActualHeight / src.Height);
            if (ratio > 1) ratio = 1;
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

        private void SetupCropAfterLoad()
        {
            var px = SourcePixels();
            if (px.Width == 0) return;

            double w, h;
            if (_lockCropSize) { w = _lockedW; h = _lockedH; }
            else
            {
                // по умолчанию — квадрат ~70 единиц на экране
                double scale = ViewScale(GetImageRect());
                w = h = scale > 0 ? Math.Round(70 / scale) : 256;
            }
            w = Math.Min(w, px.Width);
            h = Math.Min(h, px.Height);
            _cropPx = new Rect(Math.Round((px.Width - w) / 2), Math.Round((px.Height - h) / 2), w, h);
            ApplyCropToView();
        }

        private void WorkArea_SizeChanged(object s, SizeChangedEventArgs e)
        {
            if (ArtImage.Source != null) ApplyCropToView();
        }

        // Проецирует _cropPx на экран: рамка, ручка, затемнение, подпись
        private void ApplyCropToView()
        {
            Rect img = GetImageRect();
            double k = ViewScale(img);
            if (k == 0) return;

            Canvas.SetLeft(CropRect, img.X + _cropPx.X * k);
            Canvas.SetTop(CropRect, img.Y + _cropPx.Y * k);
            CropRect.Width = _cropPx.Width * k;
            CropRect.Height = _cropPx.Height * k;
            UpdateHandlePosition();
            UpdateOverlays();
            TxtCropSize.Text = $"Рамка: {_cropPx.Width:F0} × {_cropPx.Height:F0} px";
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
            double k = ViewScale(img);
            if (k == 0) return;
            var px = SourcePixels();

            double x = Math.Round((p.X - _dragOffset.X - img.X) / k);
            double y = Math.Round((p.Y - _dragOffset.Y - img.Y) / k);
            _cropPx.X = Math.Max(0, Math.Min(x, px.Width - _cropPx.Width));
            _cropPx.Y = Math.Max(0, Math.Min(y, px.Height - _cropPx.Height));
            ApplyCropToView();
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
            double k = ViewScale(img);
            if (k == 0) return;
            var px = SourcePixels();

            double minPx = Math.Ceiling(MinCropScreen / k);
            double cL = img.X + _cropPx.X * k;
            double cT = img.Y + _cropPx.Y * k;
            double w = Math.Round((p.X - cL) / k);
            double h = Math.Round((p.Y - cT) / k);
            _cropPx.Width = Math.Min(Math.Max(minPx, w), px.Width - _cropPx.X);
            _cropPx.Height = Math.Min(Math.Max(minPx, h), px.Height - _cropPx.Y);
            ApplyCropToView();
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
        }

        private void TxtMainTag_TextChanged(object s, TextChangedEventArgs e)
        {
            _mainTag = TxtMainTag.Text;
            _saveSettingsTimer.Stop();
            _saveSettingsTimer.Start();
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
                if (!crop && originalBytes.Length > 0 && originalExt.Length > 0)
                {
                    // Оригинал — байт в байт, как отдал сервер (без перекодирования в PNG)
                    imgPath = Path.Combine(_savePath, baseName + originalExt);
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
                    await Task.Run(() =>
                    {
                        using var fs = File.Create(imgPath);
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(imgToSave));
                        enc.Save(fs);
                    });
                }

                // В .txt попадают только теги, разрешённые Civitai, в формате,
                // который Civitai сам разбивает на теги (см. Caption)
                var (allowedTags, removed) = TagFilter.Filter(tags);
                string caption = Caption.Build(_mainTag,
                    allowedTags.Split("\n\n", StringSplitOptions.RemoveEmptyEntries));
                // UTF-8 без BOM: BOM стал бы частью первого тега
                await File.WriteAllTextAsync(Path.Combine(_savePath, baseName + ".txt"), caption,
                    new UTF8Encoding(false));

                string saved = crop ? "✔ КРОП СОХРАНЁН" : "✔ ОРИГИНАЛ СОХРАНЁН";
                SetStatus(removed > 0 ? $"{saved} (исключено запрещённых тегов: {removed})" : saved);
                ClearStatusLater(2000);
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

        // Номер текущего сообщения: отложенная очистка стирает только «своё»
        // сообщение, а не то, что появилось позже (например, ошибку загрузки).
        private int _statusVersion;

        private void SetStatus(string msg, bool error = false)
        {
            _statusVersion++;
            TxtStatus.Foreground = error
                ? new SolidColorBrush(Color.FromRgb(220, 80, 80))
                : (SolidColorBrush)FindResource("AccentGreen");
            TxtStatus.Text = msg;
        }

        private async void ClearStatusLater(int delayMs)
        {
            int version = _statusVersion;
            await Task.Delay(delayMs);
            if (version == _statusVersion) TxtStatus.Text = "";
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
                    e621ApiKey = _e621ApiKey
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
            }
            catch { }
        }
    }
}
