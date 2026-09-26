using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ArtFinder
{
    // Отсев тегов, запрещённых Civitai для обучения и генерации.
    // Списки взяты из открытого кода Civitai (src/utils/metadata/lists) и
    // встроены в сборку как Resources/BlockedTags.txt.
    //
    // Скорость: тег нормализуется один раз, точные слова/фразы ищутся в
    // HashSet по n-граммам слов, регулярки скомпилированы и объединены в
    // одну на секцию, а вердикт по каждому тегу кешируется — повторяющиеся
    // между постами теги проверяются одним поиском в словаре.
    internal static class TagFilter
    {
        private const RegexOptions Opts =
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        private static readonly HashSet<string> Phrases = new(StringComparer.Ordinal);
        private static readonly int MaxPhraseWords;
        private static readonly Regex? YoungNouns, YoungPartial, YoungAdjectives, Harmful;
        // "15 yo", "12 year old" и т.п. — возраст младше 18
        private static readonly Regex MinorAge = new(@"\b(\d{1,2}) ?(?:yo|y o|years? old|year old)\b", Opts);
        private static readonly ConcurrentDictionary<string, bool> Cache = new(StringComparer.Ordinal);

        static TagFilter()
        {
            var sections = new Dictionary<string, List<string>>();
            List<string>? cur = null;
            using var stream = typeof(TagFilter).Assembly
                .GetManifestResourceStream("ArtFinder.Resources.BlockedTags.txt");
            if (stream == null) return;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] == '[' && line[^1] == ']')
                {
                    cur = new List<string>();
                    sections[line[1..^1]] = cur;
                }
                else cur?.Add(line);
            }

            if (sections.TryGetValue("phrases", out var phrases))
                foreach (var p in phrases)
                {
                    string n = Normalize(p);
                    if (n.Length == 0) continue;
                    Phrases.Add(n);
                    MaxPhraseWords = Math.Max(MaxPhraseWords, n.Split(' ').Length);
                }

            YoungNouns = Build(sections, "young-nouns", wordBounded: true);
            YoungPartial = Build(sections, "young-partial", wordBounded: true);
            YoungAdjectives = Build(sections, "young-adjectives", wordBounded: true);
            Harmful = Build(sections, "harmful", wordBounded: false);
        }

        private static Regex? Build(Dictionary<string, List<string>> sections, string name, bool wordBounded)
        {
            if (!sections.TryGetValue(name, out var list) || list.Count == 0) return null;
            string alt = string.Join("|", list.Select(p => "(?:" + p + ")"));
            return new Regex(wordBounded ? @"\b(?:" + alt + @")\b" : alt, Opts);
        }

        // Вызывается при старте в фоне, чтобы загрузка списков и компиляция
        // регулярок не приходились на первое сохранение.
        public static void Warmup() => IsBlocked("warmup");

        // Теги разделены "\n\n" (формат _currentTags). Возвращает только
        // разрешённые теги в исходном порядке и число исключённых.
        public static (string Tags, int Removed) Filter(string tags)
        {
            if (string.IsNullOrEmpty(tags)) return (tags, 0);
            var sb = new StringBuilder(tags.Length);
            int removed = 0;
            foreach (var tag in tags.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                if (IsBlocked(tag)) { removed++; continue; }
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(tag);
            }
            return (sb.ToString(), removed);
        }

        public static bool IsBlocked(string tag) => Cache.GetOrAdd(tag, Check);

        private static bool Check(string tag)
        {
            string n = Normalize(tag);
            if (n.Length == 0) return false;

            if (Phrases.Contains(n)) return true;
            if (MaxPhraseWords > 0)
            {
                var words = n.Split(' ');
                for (int i = 0; i < words.Length; i++)
                    for (int len = 1; len <= MaxPhraseWords && i + len <= words.Length; len++)
                        if (Phrases.Contains(len == 1 ? words[i] : string.Join(' ', words, i, len)))
                            return true;
            }

            if (YoungNouns?.IsMatch(n) == true) return true;
            if (YoungAdjectives?.IsMatch(n) == true && YoungPartial?.IsMatch(n) == true) return true;
            if (Harmful?.IsMatch(n) == true) return true;

            var age = MinorAge.Match(n);
            return age.Success && int.Parse(age.Groups[1].Value) < 18;
        }

        // Нижний регистр; всё, кроме букв и цифр (включая "_" из тегов
        // e621/rule34), становится одиночным пробелом.
        private static string Normalize(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c))
                {
                    if (space && sb.Length > 0) sb.Append(' ');
                    space = false;
                    sb.Append(char.ToLowerInvariant(c));
                }
                else space = true;
            }
            return sb.ToString();
        }
    }
}
