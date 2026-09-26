using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ArtFinder
{
    /// <summary>
    /// Загружает фильтр-листы и блокирует рекламные запросы в WebView2.
    ///
    /// Источники (github.com/uBlockOrigin/uAssets):
    ///   1. filters.txt              — основной список uBlock Origin
    ///   2. badware.txt              — вредоносные/навязчивые ресурсы
    ///   3. annoyances-others.txt    — прочий раздражающий контент
    ///
    /// Жёсткий хардкод известных adult-рекламных сетей работает независимо от загрузки
    /// (см. ShouldBlock: проверка хардкода не зависит от IsLoaded).
    /// </summary>
    internal class AdBlocker
    {
        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ArtFinder", "adblock");

        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

        // Рабочие URL (проверено: отдают 200 с github raw)
        private static readonly (string local, string url)[] FilterSources =
        {
            ("ublock_filters.txt",
             "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters.txt"),
            ("ublock_badware.txt",
             "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/badware.txt"),
            ("ublock_annoyances.txt",
             "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/annoyances-others.txt"),
        };

        // Хардкод — работает сразу, до загрузки списков
        // Рекламные сети, активно используемые на e621 и rule34
        private static readonly HashSet<string> HardcodedDomains = new(StringComparer.OrdinalIgnoreCase)
        {
            // Exoclick — основная рекламная сеть на rule34
            "exoclick.com", "syndication.exoclick.com", "ads.exoclick.com",
            "static.exoclick.com", "d1z76rl0rp8j4v.cloudfront.net",
            // TrafficJunky — pornhub/mindgeek сеть
            "trafficjunky.net", "trafficjunky.com",
            // JuicyAds
            "juicyads.com", "ads.juicyads.com",
            // e621 собственная реклама
            "rv.e621.net",
            // Ero-advertising
            "ero-advertising.com", "adspaces.ero-advertising.com",
            // AdSpyglass
            "adspyglass.com",
            // PlugRush
            "plugrush.com",
            // AdultForce
            "adultforce.com",
            // Fap Traffic
            "fap-traffic.com",
            // PopAds
            "popads.net", "popadvert.com",
            // PropellerAds
            "propellerads.com", "propellerclick.com",
            // TrafficStars
            "trafficstars.com",
            // AdXpansion
            "adxpansion.com",
            // FuckingFast (хостинг видео-рекламы)
            "fuckingfast.net",
            // AppNexus/Xandr
            "adnxs.com",
            // общие трекеры
            "googletagmanager.com", "google-analytics.com",
            "doubleclick.net", "googlesyndication.com",
        };

        // Набор правил строится целиком в фоне и подменяется одной ссылкой,
        // поэтому ShouldBlock (UI-поток) никогда не видит его наполовину.
        private sealed class RuleSet
        {
            public readonly HashSet<string> Domains = new(StringComparer.OrdinalIgnoreCase);
            // Правила с путём, сгруппированные по хосту правила: для URL
            // проверяются только правила его хоста и родительских доменов,
            // а не все сотни регулярок подряд.
            public readonly Dictionary<string, List<Regex>> PathRules = new(StringComparer.OrdinalIgnoreCase);
            public int PathRuleCount;
        }

        private RuleSet? _rules;

        public bool IsLoaded  => _rules != null;
        public int  RuleCount => _rules == null ? 0 : _rules.Domains.Count + _rules.PathRuleCount;

        // ────────────────────────────────────────────────
        //  Публичный API
        // ────────────────────────────────────────────────
        public async Task InitAsync()
        {
            Directory.CreateDirectory(CacheDir);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            var ready = new List<string>();
            foreach (var (localName, url) in FilterSources)
            {
                string localPath = Path.Combine(CacheDir, localName);
                if (await EnsureCachedAsync(http, localPath, url))
                    ready.Add(localPath);
            }

            if (ready.Count == 0)
                throw new Exception("Ни один фильтр-лист не удалось загрузить");

            // Разбор ~1 МБ текста и сборка регулярок — вне UI-потока
            var rules = await Task.Run(() =>
            {
                var set = new RuleSet();
                foreach (var path in ready) ParseFile(path, set);
                return set;
            });
            _rules = rules;
        }

        /// <summary>
        /// Проверяет, должен ли URL быть заблокирован.
        /// Хардкод работает всегда; динамические правила — после InitAsync.
        /// </summary>
        public bool ShouldBlock(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;

            string host = TryGetHost(url);

            // 1. Хардкод (мгновенно)
            if (!string.IsNullOrEmpty(host) && IsBlockedByHardcode(host))
                return true;

            // 2. Загруженные списки
            var rules = _rules;
            if (rules == null || string.IsNullOrEmpty(host)) return false;

            // host, затем родительские домены: a.b.example.com → b.example.com → example.com
            for (string h = host; ; )
            {
                if (rules.Domains.Contains(h)) return true;
                if (rules.PathRules.TryGetValue(h, out var list))
                    foreach (var rx in list)
                    {
                        try { if (rx.IsMatch(url)) return true; }
                        catch (RegexMatchTimeoutException) { /* патологический шаблон — пропускаем */ }
                    }
                int dot = h.IndexOf('.');
                if (dot < 0 || dot == h.Length - 1) return false;
                h = h[(dot + 1)..];
            }
        }

        // ────────────────────────────────────────────────
        //  Внутренние методы
        // ────────────────────────────────────────────────
        private static bool IsBlockedByHardcode(string host)
        {
            if (HardcodedDomains.Contains(host)) return true;
            // Проверяем суффиксы: sub.exoclick.com → exoclick.com
            int dot = host.IndexOf('.');
            while (dot >= 0 && dot < host.Length - 1)
            {
                if (HardcodedDomains.Contains(host[(dot + 1)..])) return true;
                dot = host.IndexOf('.', dot + 1);
            }
            return false;
        }

        private static async Task<bool> EnsureCachedAsync(HttpClient http, string localPath, string url)
        {
            // Свежий кеш — используем без скачивания
            if (File.Exists(localPath) &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(localPath) < CacheTtl)
                return true;

            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) return File.Exists(localPath); // вернём старый кеш

                using var net = await resp.Content.ReadAsStreamAsync();
                using var fs  = File.Create(localPath);
                await net.CopyToAsync(fs);
                return true;
            }
            catch
            {
                // Если старый кеш есть — продолжаем с ним
                return File.Exists(localPath);
            }
        }

        private static void ParseFile(string path, RuleSet set)
        {
            if (!File.Exists(path)) return;

            foreach (var rawLine in File.ReadLines(path))
            {
                string line = rawLine.Trim();

                // Пропускаем: комментарии, whitelist (@@), CSS-правила, пустые
                if (line.Length == 0
                    || line[0] == '!'
                    || line.StartsWith("@@")
                    || line.Contains("##")
                    || line.Contains("#@#")
                    || line.Contains("#?#")
                    || line.Contains("+js("))   // cosmetické skripty — не нужны
                    continue;

                // Опции ($script,$domain=...,$popup,...) нельзя просто отбросить:
                // многие из них сужают правило (только на сайте X, только для
                // попапов, только для IP Y). Без них правило вида
                // "||com/$doc,ipaddress=..." превращалось в блокировку всех .com.
                int dollar = line.IndexOf('$');
                if (dollar > 0 && !HasOnlySafeOptions(line[(dollar + 1)..]))
                    continue;
                string cleanLine = dollar > 0 ? line[..dollar] : line;

                // ||domain.com^ без пути — добавляем в быстрый HashSet
                if (cleanLine.StartsWith("||") && cleanLine.EndsWith("^"))
                {
                    string domain = cleanLine[2..^1];
                    if (IsValidDomain(domain)) set.Domains.Add(domain);
                    continue;
                }

                // ||domain.com/path... и |https://... — конвертируем в Regex
                if (cleanLine.StartsWith("|"))
                {
                    var rule = ToRegex(cleanLine);
                    if (rule == null) continue;
                    var (host, rx) = rule.Value;
                    if (!set.PathRules.TryGetValue(host, out var list))
                        set.PathRules[host] = list = new List<Regex>();
                    list.Add(rx);
                    set.PathRuleCount++;
                }
            }
        }

        // Опции, которые только ограничивают тип ресурса. Если их игнорировать,
        // правило блокирует чуть больше типов, но на том же адресе — безопасно.
        // Всё остальное (domain=, popup, redirect, csp, removeparam, ipaddress=,
        // badfilter, 1p, отрицания ~...) меняет смысл правила — такие пропускаем.
        private static readonly HashSet<string> SafeOptions = new(StringComparer.OrdinalIgnoreCase)
        {
            "script", "image", "stylesheet", "css", "xhr", "xmlhttprequest",
            "subdocument", "frame", "media", "font", "object", "ping", "beacon",
            "websocket", "other", "all", "important", "doc", "document",
            "third-party", "3p",
        };

        private static bool HasOnlySafeOptions(string options)
        {
            foreach (var opt in options.Split(','))
                if (!SafeOptions.Contains(opt.Trim())) return false;
            return true;
        }

        // Хост правила: "ads.example.com" — только буквы/цифры/дефисы и
        // минимум одна точка (отсекает "||com/", "||ad*/" и т.п.).
        private static readonly Regex HostRegex = new(
            @"^[a-z0-9-]+(\.[a-z0-9-]+)+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static string RuleHost(string rest)
        {
            int end = rest.IndexOfAny(new[] { '/', '^', '*', ':', '?', '|' });
            return end < 0 ? rest : rest[..end];
        }

        // Возвращает хост правила (ключ индекса) и регулярку для полного URL
        private static (string host, Regex rx)? ToRegex(string pattern)
        {
            try
            {
                // Завершающий "|" — якорь конца адреса
                bool endAnchor = pattern.Length > 2 && pattern.EndsWith('|') && !pattern.EndsWith("||");
                if (endAnchor) pattern = pattern[..^1];

                string p, host;
                if (pattern.StartsWith("||"))
                {
                    // ||domain.com/path → ^scheme://([sub.]*)domain.com/path
                    string rest = pattern[2..];
                    host = RuleHost(rest);
                    if (!HostRegex.IsMatch(host)) return null;
                    p = @"^[a-z][a-z0-9+.\-]*://(?:[a-z0-9\-]+\.)*" + EscapeBody(rest);
                }
                else
                {
                    // |https://domain.com/path → ^https://domain.com/path
                    string rest = pattern[1..];
                    int scheme = rest.IndexOf("://", StringComparison.Ordinal);
                    if (scheme <= 0) return null;
                    host = RuleHost(rest[(scheme + 3)..]);
                    if (!HostRegex.IsMatch(host)) return null;
                    p = "^" + EscapeBody(rest);
                }
                if (endAnchor) p += "$";

                // Без RegexOptions.Compiled: на один URL теперь проверяются
                // единицы правил, а компиляция сотен регулярок стоила ~1 с
                // на первых запросах и вызывала ложные таймауты.
                var rx = new Regex(p,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(50));
                return (host.ToLowerInvariant(), rx);
            }
            catch { return null; }
        }

        private static string EscapeBody(string s) =>
            Regex.Escape(s)
                .Replace(@"\*", ".*")
                .Replace(@"\^", @"(?:[/?&:=]|$)");

        private static bool IsValidDomain(string s) => HostRegex.IsMatch(s);

        private static string TryGetHost(string url)
        {
            try { return new Uri(url).Host.ToLowerInvariant(); }
            catch { return ""; }
        }
    }
}
