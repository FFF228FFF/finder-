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
    /// Источники (в порядке приоритета):
    ///   1. uBlock Origin filters       — github.com/uBlockOrigin/uAssets (основной, включает adult)
    ///   2. uBlock Origin badware list  — дополнительный
    ///
    /// Жёсткий хардкод известных adult-рекламных сетей работает независимо от загрузки.
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
            // общие трекеры
            "googletagmanager.com", "google-analytics.com",
            "doubleclick.net", "googlesyndication.com",
        };

        private readonly List<Regex>     _regexRules  = new();
        private readonly HashSet<string> _domainRules = new(StringComparer.OrdinalIgnoreCase);

        public bool IsLoaded   { get; private set; }
        public int  RuleCount  => _regexRules.Count + _domainRules.Count;

        // ────────────────────────────────────────────────
        //  Публичный API
        // ────────────────────────────────────────────────
        public async Task InitAsync()
        {
            Directory.CreateDirectory(CacheDir);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            int downloaded = 0;
            foreach (var (localName, url) in FilterSources)
            {
                string localPath = Path.Combine(CacheDir, localName);
                bool ok = await EnsureCachedAsync(http, localPath, url);
                if (ok)
                {
                    ParseFile(localPath);
                    downloaded++;
                }
            }

            IsLoaded = true;

            if (downloaded == 0)
                throw new Exception("Ни один фильтр-лист не удалось загрузить");
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
            if (!IsLoaded) return false;

            // Домены из списков
            if (!string.IsNullOrEmpty(host) && IsDomainBlocked(host))
                return true;

            // Regex-правила
            foreach (var rx in _regexRules)
            {
                try { if (rx.IsMatch(url)) return true; }
                catch { /* таймаут — пропускаем */ }
            }

            return false;
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

        private bool IsDomainBlocked(string host)
        {
            if (_domainRules.Contains(host)) return true;
            int dot = host.IndexOf('.');
            while (dot >= 0 && dot < host.Length - 1)
            {
                if (_domainRules.Contains(host[(dot + 1)..])) return true;
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

        private void ParseFile(string path)
        {
            if (!File.Exists(path)) return;
            int linesParsed = 0;

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

                // Убираем опции ($script,$image,...) — нам важен только URL
                int dollar = line.IndexOf('$');
                string cleanLine = dollar > 0 ? line[..dollar] : line;

                // ||domain.com^ без пути — добавляем в быстрый HashSet
                if (cleanLine.StartsWith("||") && cleanLine.EndsWith("^"))
                {
                    string domain = cleanLine[2..^1];
                    if (IsValidDomain(domain))
                    { _domainRules.Add(domain); linesParsed++; }
                    continue;
                }

                // ||domain.com/path... — конвертируем в Regex
                if (cleanLine.StartsWith("||"))
                {
                    var rx = ToRegex(cleanLine);
                    if (rx != null) { _regexRules.Add(rx); linesParsed++; }
                    continue;
                }

                // |https://... — полный URL с начала
                if (cleanLine.StartsWith("|"))
                {
                    var rx = ToRegex(cleanLine);
                    if (rx != null) { _regexRules.Add(rx); linesParsed++; }
                }
            }
        }

        private static Regex? ToRegex(string pattern)
        {
            try
            {
                string p;
                if (pattern.StartsWith("||"))
                {
                    // ||domain.com/path → https?://([sub.]*)domain.com/path
                    string rest = Regex.Escape(pattern[2..])
                        .Replace(@"\*", ".*")
                        .Replace(@"\^", @"(?:[/?&]|$)");
                    p = @"https?://(?:[a-z0-9\-]+\.)*" + rest;
                }
                else
                {
                    string rest = Regex.Escape(pattern.TrimStart('|'))
                        .Replace(@"\*", ".*")
                        .Replace(@"\^", @"(?:[/?&]|$)");
                    p = pattern.StartsWith("|") ? "^" + rest : rest;
                }

                return new Regex(p,
                    RegexOptions.Compiled | RegexOptions.IgnoreCase,
                    TimeSpan.FromMilliseconds(15));
            }
            catch { return null; }
        }

        private static bool IsValidDomain(string s) =>
            s.Length > 3 && s.Contains('.') &&
            !s.Contains('/') && !s.Contains('*') && !s.Contains('?');

        private static string TryGetHost(string url)
        {
            try { return new Uri(url).Host.ToLowerInvariant(); }
            catch { return ""; }
        }
    }
}
