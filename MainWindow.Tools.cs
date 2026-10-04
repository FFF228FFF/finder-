using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ArtFinder.Tools;

namespace ArtFinder
{
    // Вкладки правой панели и бывшие Python-утилиты (Art Saver, Dataset Mirror),
    // встроенные в окно: всё работает в фоне, прогресс — в строке состояния.
    public partial class MainWindow
    {
        // ════════════════════════════════════════════════
        //  ВКЛАДКИ
        // ════════════════════════════════════════════════
        // При запуске всегда открыта вкладка «Кроп» (последняя не запоминается)
        private void Tab_Checked(object s, RoutedEventArgs e)
        {
            // Checked срабатывает ещё в InitializeComponent, до создания панелей
            if (RightPanel == null || PanelSaver == null || PanelMirror == null) return;
            RightPanel.Visibility = TabCrop.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelSaver.Visibility = TabSaver.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelMirror.Visibility = TabMirror.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void DigitsOnly_PreviewTextInput(object s, TextCompositionEventArgs e) =>
            e.Handled = !e.Text.All(char.IsAsciiDigit);

        // ════════════════════════════════════════════════
        //  ФОНОВЫЕ ЗАДАЧИ
        // ════════════════════════════════════════════════
        // Одновременно выполняется одна задача. Журнал и прогресс копятся
        // потокобезопасно и выводятся таймером — поток задачи не ждёт UI,
        // а тысячи строк журнала не забивают очередь окна.
        private sealed class JobContext : IJobReporter
        {
            private readonly ConcurrentQueue<string> _lines = new();
            private readonly object _lock = new();
            private int _done, _total;
            private string? _text;

            public JobContext(CancellationToken token) => Token = token;
            public CancellationToken Token { get; }
            public void Log(string line) => _lines.Enqueue(line);

            public void Report(int done, int total, string? text = null)
            {
                lock (_lock) { _done = done; _total = total; if (text != null) _text = text; }
            }

            public (int Done, int Total, string? Text) Snapshot()
            {
                lock (_lock) return (_done, _total, _text);
            }

            public string DrainLog()
            {
                var sb = new StringBuilder();
                while (_lines.TryDequeue(out var l)) sb.Append(l).Append(Environment.NewLine);
                return sb.ToString();
            }
        }

        private CancellationTokenSource? _jobCts;
        private JobContext? _job;
        private string _jobName = "";
        private TextBox? _jobLog;
        private bool _jobStopping;
        private DispatcherTimer? _jobTimer;

        private JobContext BeginJob(string name, TextBox log)
        {
            _jobCts = new CancellationTokenSource();
            _job = new JobContext(_jobCts.Token);
            _jobName = name;
            _jobLog = log;
            _jobStopping = false;
            log.Clear();
            SetStatus(""); // итог прошлой задачи больше не актуален

            if (_jobTimer == null)
            {
                _jobTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                _jobTimer.Tick += (_, _) => FlushJob();
            }
            _jobTimer.Start();

            TxtHotkeys.Visibility = Visibility.Collapsed;
            JobPanel.Visibility = Visibility.Visible;
            JobProgress.IsIndeterminate = true;
            TxtJob.Text = name + "…";
            UpdateToolButtons();
            return _job;
        }

        private void FlushJob()
        {
            if (_job == null) return;
            string text = _job.DrainLog();
            if (text.Length > 0 && _jobLog != null) AppendLog(_jobLog, text);

            var (done, total, info) = _job.Snapshot();
            if (total > 0)
            {
                JobProgress.IsIndeterminate = false;
                JobProgress.Maximum = total;
                JobProgress.Value = Math.Min(done, total);
            }
            TxtJob.Text = (info == null ? _jobName : $"{_jobName}: {info}") + (_jobStopping ? " — останавливаюсь…" : "");
        }

        private void EndJob()
        {
            FlushJob();
            _jobTimer?.Stop();
            _jobCts?.Dispose();
            _jobCts = null;
            _job = null;
            _jobLog = null;
            JobPanel.Visibility = Visibility.Collapsed;
            TxtHotkeys.Visibility = Visibility.Visible;
            UpdateToolButtons();
        }

        private static void AppendLog(TextBox box, string text)
        {
            box.AppendText(text);
            // Очень длинный журнал обрезается сначала, чтобы окно не тормозило
            if (box.Text.Length > 400_000) box.Text = box.Text[^300_000..];
            box.ScrollToEnd();
        }

        private void UpdateToolButtons()
        {
            bool busy = _jobCts != null;
            BtnSaverStart.IsEnabled = !busy;
            BtnSaverStop.IsEnabled = busy && _jobName == SaverJobName && !_jobStopping;
            BtnMirrorRun.IsEnabled = !busy;
            BtnJobStop.IsEnabled = busy && !_jobStopping;
        }

        private void BtnJobStop_Click(object s, RoutedEventArgs e)
        {
            if (_jobCts == null || _jobStopping) return;
            _jobStopping = true;
            _jobCts.Cancel();
            UpdateToolButtons();
            FlushJob();
        }

        // Инструменты работают с папкой сохранения с вкладки «Кроп»
        private string? ResolveToolFolder()
        {
            string path = _savePath;
            if (string.IsNullOrEmpty(path))
            {
                SetStatus("⚠ Выберите папку сохранения («Обзор…» или вкладка «Кроп»).", error: true);
                return null;
            }
            if (!Directory.Exists(path))
            {
                SetStatus($"⚠ Папка не найдена: {path}", error: true);
                return null;
            }
            return path;
        }

        // Работа с картинками WPF (BitmapDecoder/Encoder) — в отдельном STA-потоке
        private static Task<T> RunOnStaThread<T>(Func<T> work)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { tcs.SetResult(work()); }
                catch (OperationCanceledException) { tcs.SetCanceled(); }
                catch (Exception ex) { tcs.SetException(ex); }
            }) { IsBackground = true, Name = "ArtFinder job" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return tcs.Task;
        }

        // ════════════════════════════════════════════════
        //  ЗЕРКАЛО (бывший DatasetMirror.exe)
        // ════════════════════════════════════════════════
        private const string MirrorJobName = "Зеркало";

        private async void BtnMirrorRun_Click(object s, RoutedEventArgs e)
        {
            if (_jobCts != null) return;
            string? folder = ResolveToolFolder();
            if (folder == null) return;
            string suffix = TxtMirrorSuffix.Text.Trim();
            if (!DatasetMirror.IsValidSuffix(suffix))
            {
                SetStatus("⚠ Укажите корректный непустой суффикс имени (например _flip).", error: true);
                return;
            }

            var opt = new DatasetMirror.Options(folder, suffix,
                ChkMirrorRecursive.IsChecked == true, ChkMirrorSwap.IsChecked == true);
            SaveSettings();
            var job = BeginJob(MirrorJobName, LogMirror);
            job.Log($"Папка: {folder}");
            try
            {
                var r = await RunOnStaThread(() => DatasetMirror.Run(opt, job));
                string msg = $"Зеркало: создано изображений {r.Images}, описаний {r.Captions}, " +
                             $"пропущено {r.Skipped}, без описания {r.NoCaption}, ошибок {r.Errors}";
                SetStatus((r.Errors > 0 ? "⚠ " : "✔ ") + msg, error: r.Errors > 0);
            }
            catch (OperationCanceledException)
            {
                job.Log("\nОстановлено пользователем.");
                SetStatus("Зеркало остановлено.");
            }
            catch (Exception ex)
            {
                job.Log($"\nОШИБКА: {ex.Message}");
                SetStatus($"❌ Зеркало: {ex.Message}", error: true);
            }
            finally { EndJob(); }
        }

        // ════════════════════════════════════════════════
        //  СКАЧАТЬ ВСЁ (бывший ArtSaver.exe)
        // ════════════════════════════════════════════════
        // Python-версия нажимала кнопки ArtFinder через UI Automation и
        // блокировала окно. Здесь посты берутся напрямую: e621 — через API
        // (те же логин/ключ и запасной путь через браузер), rule34 — страницы
        // поиска и постов скачиваются fetch'ем в контексте открытой вкладки
        // rule34 и разбираются тем же r34Extract, что и при ручной загрузке.
        // Файлы и .txt пишутся точно так же, как кнопкой «Сохранить оригинал».
        private const string SaverJobName = "Скачать всё";
        private const int Rule34PageSize = 42;

        private sealed record SearchInfo(string Site, string Tags, int Start); // e621: № страницы, rule34: pid

        private sealed record SaverOptions(SearchInfo Search, bool FromCurrent, bool SkipExisting, int Limit, string Folder);

        private sealed class SaverStats
        {
            public int Saved, Skipped, Existing, Errors, ErrorsInRow;
        }

        private static SearchInfo? ParseSearch(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
            var q = HttpUtility.ParseQueryString(u.Query);
            switch (SiteOf(url))
            {
                case "e621":
                {
                    if (!u.AbsolutePath.StartsWith("/posts", StringComparison.OrdinalIgnoreCase)) return null;
                    bool postPage = Regex.IsMatch(u.AbsolutePath, @"^/posts/\d+");
                    string tags = ((postPage ? q["q"] : q["tags"]) ?? "").Trim();
                    int page = !postPage && int.TryParse(q["page"], out var p) && p > 0 ? p : 1;
                    return new SearchInfo("e621", tags, page);
                }
                case "rule34":
                {
                    if (!string.Equals(q["page"], "post", StringComparison.OrdinalIgnoreCase)) return null;
                    string tags = (q["tags"] ?? "").Trim();
                    if (tags == "all") tags = "";
                    int pid = q["s"] == "list" && int.TryParse(q["pid"], out var o) && o > 0 ? o : 0;
                    return new SearchInfo("rule34", tags, pid);
                }
            }
            return null;
        }

        private static int SearchPageNo(SearchInfo s) => s.Site == "e621" ? s.Start : s.Start / Rule34PageSize + 1;

        private void UpdateSaverSearch(string url)
        {
            var s = ParseSearch(url);
            if (s == null)
            {
                TxtSaverSearch.Text = "Откройте страницу поиска на e621 или rule34";
                return;
            }
            int page = SearchPageNo(s);
            TxtSaverSearch.Text = $"{s.Site}  ·  теги: {(s.Tags.Length > 0 ? s.Tags : "(все посты)")}" +
                                  (page > 1 ? $"  ·  страница {page}" : "");
        }

        private void UpdateSaverStats(SaverStats st) =>
            TxtSaverStats.Text = $"Сохранено: {st.Saved}  ·  Пропущено: {st.Skipped + st.Existing}  ·  Ошибок: {st.Errors}";

        private async void BtnSaverStart_Click(object s, RoutedEventArgs e)
        {
            if (_jobCts != null) return;
            var search = ParseSearch(WebView.Source?.ToString() ?? "");
            if (search == null)
            {
                SetStatus("⚠ Слева должна быть открыта страница поиска e621 или rule34.", error: true);
                return;
            }
            if (string.IsNullOrEmpty(_savePath))
            {
                SetStatus("⚠ Выберите папку сохранения на вкладке «Кроп».", error: true);
                return;
            }
            int limit = int.TryParse(TxtSaverLimit.Text, out var l) && l > 0 ? l : 0;
            if (search.Tags.Length == 0 && limit == 0)
            {
                SetStatus("⚠ Поиск без тегов — это все посты сайта. Задайте теги или ограничение «Не больше».", error: true);
                return;
            }

            var opt = new SaverOptions(search, ChkSaverFromCurrent.IsChecked == true,
                ChkSaverSkipExisting.IsChecked == true, limit, _savePath);
            SaveSettings();
            var job = BeginJob(SaverJobName, LogSaver);
            var st = new SaverStats();
            UpdateSaverStats(st);
            try
            {
                await RunSaverAsync(opt, job, st);
                SetStatus($"✔ Скачать всё: сохранено {st.Saved}, пропущено {st.Skipped + st.Existing}, ошибок {st.Errors}",
                          error: false);
            }
            catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
            {
                job.Log("\nОстановлено пользователем.");
                SetStatus($"Скачивание остановлено: сохранено {st.Saved}");
            }
            catch (Exception ex)
            {
                job.Log($"\nОШИБКА: {DescribeError(ex)}");
                SetStatus($"❌ Скачать всё: {DescribeError(ex)}", error: true);
            }
            finally
            {
                job.Log($"\nИтого: сохранено {st.Saved}, уже было {st.Existing}, пропущено (видео) {st.Skipped}, ошибок {st.Errors}");
                UpdateSaverStats(st);
                EndJob();
            }
        }

        private async Task RunSaverAsync(SaverOptions o, JobContext job, SaverStats st)
        {
            var ct = job.Token;
            job.Log($"Сайт: {o.Search.Site}, теги: «{(o.Search.Tags.Length > 0 ? o.Search.Tags : "все посты")}»" +
                    (o.Limit > 0 ? $", не больше {o.Limit}" : ""));
            job.Log($"Папка: {o.Folder}");
            job.Report(0, o.Limit, "получаю список…");

            // Обработка одного поста. false — достигнут лимит.
            async Task<bool> ProcessPostAsync(string id, int pageNo, Func<Task<(string? Url, string Tags)>> resolve)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var (fileUrl, tags) = await resolve();
                    if (string.IsNullOrEmpty(fileUrl))
                    {
                        st.Errors++;
                        job.Log($"  {id} — файл недоступен (удалён или скрыт без авторизации)");
                    }
                    else if (IsVideoUrl(fileUrl))
                    {
                        st.Skipped++;
                        job.Log($"  {id} — видео, пропуск");
                    }
                    else
                    {
                        string path = new Uri(NormalizeUrl(fileUrl)).AbsolutePath;
                        string baseName = Path.GetFileNameWithoutExtension(path);
                        string imgPath = Path.Combine(o.Folder, baseName + Path.GetExtension(path).ToLowerInvariant());
                        if (o.SkipExisting && File.Exists(imgPath))
                        {
                            st.Existing++;
                            job.Log($"  {id} — уже сохранён");
                        }
                        else
                        {
                            byte[] bytes = await DownloadImageWithFallbackAsync(fileUrl, ct);
                            // Запись не прерывается отменой: файл либо целый, либо его нет
                            Directory.CreateDirectory(o.Folder);
                            string tmp = imgPath + ".part";
                            await File.WriteAllBytesAsync(tmp, bytes, CancellationToken.None);
                            File.Move(tmp, imgPath, overwrite: true);
                            int removed = await WriteCaptionAsync(Path.Combine(o.Folder, baseName + ".txt"), tags);
                            st.Saved++;
                            job.Log($"  {id} — сохранён" + (removed > 0 ? $" (исключено запрещённых тегов: {removed})" : ""));
                        }
                    }
                    st.ErrorsInRow = 0;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    st.Errors++;
                    job.Log($"  {id} — ошибка: {DescribeError(ex)}");
                    if (++st.ErrorsInRow >= 10)
                        throw new InvalidOperationException("10 ошибок подряд — проверьте соединение с сайтом и папку сохранения");
                }

                UpdateSaverStats(st);
                job.Report(st.Saved, o.Limit, $"стр. {pageNo} · сохранено {st.Saved}" +
                                              (o.Limit > 0 ? $" из {o.Limit}" : ""));
                return o.Limit == 0 || st.Saved < o.Limit;
            }

            if (o.Search.Site == "e621") await RunE621SaverAsync(o, job, ProcessPostAsync);
            else await RunRule34SaverAsync(o, job, ProcessPostAsync);
        }

        private delegate Task<bool> PostHandler(string id, int pageNo, Func<Task<(string? Url, string Tags)>> resolve);

        // e621: страницы API. Обычный поиск листается по id ("b<id>", без
        // ограничения глубины), поиск с order:* — по номерам страниц
        // (по id он не листается; e621 отдаёт не дальше 750-й).
        private async Task RunE621SaverAsync(SaverOptions o, JobContext job, PostHandler handle)
        {
            var ct = job.Token;
            bool numeric = o.Search.Tags.Contains("order:", StringComparison.OrdinalIgnoreCase);
            int pageNo = o.FromCurrent ? o.Search.Start : 1;
            string pageParam = pageNo.ToString();
            int limit = 75; // как на сайте — чтобы «текущая страница» совпадала с открытой
            bool first = true;

            while (true)
            {
                if (!first) await Task.Delay(700, ct); // правила API e621: не чаще ~2 запросов в секунду
                first = false;
                string url = $"https://e621.net/posts.json?limit={limit}&page={pageParam}&tags={Uri.EscapeDataString(o.Search.Tags)}";
                job.Log($"Страница {pageNo}…");
                var (status, json) = await FetchE621JsonAsync(url, ct);
                if (json == null) throw new HttpRequestException($"e621 API: {status}");

                using var doc = JsonDocument.Parse(json);
                var posts = doc.RootElement.GetProperty("posts");
                if (posts.GetArrayLength() == 0) { job.Log("Больше постов нет — поиск пройден до конца."); return; }

                long minId = long.MaxValue;
                foreach (var p in posts.EnumerateArray())
                {
                    long id = p.GetProperty("id").GetInt64();
                    minId = Math.Min(minId, id);
                    var post = p.Clone();
                    if (!await handle(id.ToString(), pageNo, () => Task.FromResult((E621FileUrl(post), E621Tags(post)))))
                    {
                        job.Log("Достигнуто ограничение «Не больше».");
                        return;
                    }
                }

                pageNo++;
                if (numeric)
                {
                    if (pageNo > 750) { job.Log("e621 не отдаёт страницы дальше 750-й."); return; }
                    pageParam = pageNo.ToString();
                }
                else
                {
                    pageParam = "b" + minId;
                    limit = 320;
                }
            }
        }

        // rule34: API требует ключей, поэтому страницы поиска и постов берутся
        // так же, как их видит браузер. Вкладка браузера должна оставаться на rule34.
        private async Task RunRule34SaverAsync(SaverOptions o, JobContext job, PostHandler handle)
        {
            var ct = job.Token;
            int pid = o.FromCurrent ? o.Search.Start : 0;
            string tags = o.Search.Tags.Length > 0 ? o.Search.Tags : "all";

            while (true)
            {
                int pageNo = pid / Rule34PageSize + 1;
                job.Log($"Страница {pageNo}…");
                var ids = await Rule34ListAsync(
                    $"https://rule34.xxx/index.php?page=post&s=list&tags={Uri.EscapeDataString(tags)}&pid={pid}");
                ct.ThrowIfCancellationRequested();
                if (ids.Count == 0) { job.Log("Больше постов нет — поиск пройден до конца."); return; }

                foreach (var id in ids)
                {
                    bool more = await handle(id, pageNo, async () =>
                    {
                        await Task.Delay(250, ct); // не нагружаем сайт
                        var (img, tagStr) = await Rule34PostAsync($"https://rule34.xxx/index.php?page=post&s=view&id={id}");
                        return (img, Rule34Tags(tagStr));
                    });
                    if (!more) { job.Log("Достигнуто ограничение «Не больше»."); return; }
                }

                if (ids.Count < Rule34PageSize) { job.Log("Это была последняя страница."); return; }
                pid += Rule34PageSize;
                await Task.Delay(400, ct);
            }
        }

        private void EnsureRule34Page()
        {
            if (SiteOf(WebView.Source?.ToString() ?? "") != "rule34")
                throw new InvalidOperationException(
                    "браузер ушёл с rule34.xxx — для скачивания с rule34 вкладка слева должна оставаться на этом сайте");
        }

        private async Task<List<string>> Rule34ListAsync(string url)
        {
            EnsureRule34Page();
            string script = "(async () => {" +
                $"const url = {JsonSerializer.Serialize(url)};" +
                "const r = await fetch(url, { credentials: 'include' });" +
                "if (!r.ok) throw new Error('страница поиска: HTTP ' + r.status);" +
                "const doc = new DOMParser().parseFromString(await r.text(), 'text/html');" +
                "const ids = [], seen = {};" +
                "doc.querySelectorAll('.thumb a[href*=\"s=view\"], a[id^=\"p\"][href*=\"s=view\"]').forEach(a => {" +
                "  const m = /[?&]id=(\\d+)/.exec(a.getAttribute('href') || '');" +
                "  if (m && !seen[m[1]]) { seen[m[1]] = 1; ids.push(m[1]); }" +
                "});" +
                "return ids; })()";
            var value = await EvaluateInPageAsync(script);
            return value.EnumerateArray().Select(v => v.GetString() ?? "").Where(v => v.Length > 0).ToList();
        }

        private async Task<(string? Img, string Tags)> Rule34PostAsync(string url)
        {
            EnsureRule34Page();
            string script = "(async () => {" + Rule34ExtractFn +
                $"const url = {JsonSerializer.Serialize(url)};" +
                "const r = await fetch(url, { credentials: 'include' });" +
                "if (!r.ok) throw new Error('страница поста: HTTP ' + r.status);" +
                "const doc = new DOMParser().parseFromString(await r.text(), 'text/html');" +
                "return r34Extract(doc, url); })()";
            var v = await EvaluateInPageAsync(script);
            string img = v.TryGetProperty("img", out var i) ? i.GetString() ?? "" : "";
            string tags = v.TryGetProperty("tags", out var t) ? t.GetString() ?? "" : "";
            return (img.Length > 0 ? img : null, tags);
        }

        // ════════════════════════════════════════════════
        //  НАСТРОЙКИ ВКЛАДОК
        // ════════════════════════════════════════════════
        private JsonElement? _toolSettings;

        private object CaptureToolSettings() => new
        {
            saverFromCurrent = ChkSaverFromCurrent.IsChecked == true,
            saverSkipExisting = ChkSaverSkipExisting.IsChecked == true,
            saverLimit = TxtSaverLimit.Text.Trim(),
            mirrorSuffix = TxtMirrorSuffix.Text.Trim(),
            mirrorRecursive = ChkMirrorRecursive.IsChecked == true,
            mirrorSwap = ChkMirrorSwap.IsChecked == true,
        };

        private void ApplyToolSettings()
        {
            if (_toolSettings is not { } t) return;
            bool? B(string k) => t.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean() : null;
            string? S(string k) => t.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            ChkSaverFromCurrent.IsChecked = B("saverFromCurrent") ?? true;
            ChkSaverSkipExisting.IsChecked = B("saverSkipExisting") ?? true;
            TxtSaverLimit.Text = S("saverLimit") ?? "";
            string suffix = S("mirrorSuffix") ?? "";
            TxtMirrorSuffix.Text = DatasetMirror.IsValidSuffix(suffix) ? suffix : DatasetMirror.DefaultSuffix;
            ChkMirrorRecursive.IsChecked = B("mirrorRecursive") ?? false;
            ChkMirrorSwap.IsChecked = B("mirrorSwap") ?? false;
        }
    }
}
