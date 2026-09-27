using System.Text.RegularExpressions;

namespace ArtFinder
{
    // Формат .txt-подписи для датасетов Civitai (civitai.com / civitai.red).
    //
    // Civitai разбивает подпись на теги ТОЛЬКО по запятым:
    //   классическая форма обучения — txt.split(',').map(trim().toLowerCase())
    //   (src/components/Training/Form/TrainingCommon.ts, getTextTagsAsList);
    //   Training Studio — text.split(/[,\n]/).map(trim) (trainingFlow.ts, splitTags).
    // Кроме того, классическая форма считает подпись «описанием» (caption),
    // а не тегами, если она длиннее 80 символов и запятых в ней меньше 5 %
    // длины — старый формат «тег\n\nтег» попадал именно туда и загружался
    // одним огромным тегом.
    //
    // Поэтому: одна строка "триггер, тег1, тег2", разделитель ", ",
    // внутри тегов запятых и переводов строк нет, повторы убраны
    // (Civitai сравнивает теги без учёта регистра), триггер — первым:
    // Civitai проверяет его регуляркой ^триггер(,|$) и иначе дописывает сам.
    internal static class Caption
    {
        private static readonly Regex Separators = new(@"[\s,]+", RegexOptions.Compiled);

        public static string Build(string mainTag, IEnumerable<string> tags)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parts = new List<string>();

            void Add(string raw)
            {
                string t = Separators.Replace(raw, " ").Trim();
                if (t.Length > 0 && seen.Add(t)) parts.Add(t);
            }

            // Главный тег может содержать несколько тегов через запятую
            foreach (var t in mainTag.Split(',')) Add(t);
            foreach (var t in tags) Add(t);

            return string.Join(", ", parts);
        }
    }
}
