using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArtFinder.Tools
{
    // Расширяет датасет отзеркаленными по горизонтали копиями (бывший DatasetMirror.exe).
    //
    // Для каждой картинки name.ext создаётся name<суффикс>.ext, а для её
    // описаний (name.txt и др.) — name<суффикс>.txt. Всё — в ту же папку.
    // Новое по сравнению с Python-версией: в описаниях копий можно поменять
    // местами left/right ("facing_left" → "facing_right"), иначе теги
    // направления у отражённой картинки становятся неверными.
    internal static class DatasetMirror
    {
        public static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tif", ".tiff" };
        private static readonly HashSet<string> CaptionExts = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".caption", ".tags", ".json" };
        // В .json не лезем: это не теги, а произвольные метаданные
        private static readonly HashSet<string> SwappableExts = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".caption", ".tags" };

        public const string DefaultSuffix = "_flip";

        public sealed record Options(string Folder, string Suffix, bool Recursive, bool SwapLeftRight);

        public sealed class Result
        {
            public int Images, Captions, Swapped, Skipped, Errors, NoCaption;
        }

        public static bool IsValidSuffix(string s) =>
            s.Length > 0 && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

        public static Result Run(Options o, IJobReporter job)
        {
            if (!Directory.Exists(o.Folder)) throw new DirectoryNotFoundException($"Папка не найдена: {o.Folder}");
            if (!IsValidSuffix(o.Suffix)) throw new ArgumentException("Укажите корректный непустой суффикс (например _flip)");

            var images = Directory.EnumerateFiles(o.Folder, "*",
                    o.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(p => ImageExts.Contains(Path.GetExtension(p)))
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
            var r = new Result();
            job.Log($"Найдено изображений: {images.Count}");

            for (int i = 0; i < images.Count; i++)
            {
                job.Token.ThrowIfCancellationRequested();
                string img = images[i];
                string dir = Path.GetDirectoryName(img)!;
                string stem = Path.GetFileNameWithoutExtension(img);
                string name = Path.GetFileName(img);
                try
                {
                    // Уже отзеркаленные копии (повторный запуск) не трогаем
                    if (stem.EndsWith(o.Suffix, StringComparison.Ordinal))
                    {
                        string baseStem = stem[..^o.Suffix.Length];
                        if (baseStem.Length > 0 && ImageExts.Append(Path.GetExtension(img))
                                .Any(e => File.Exists(Path.Combine(dir, baseStem + e))))
                        {
                            r.Skipped++;
                            continue;
                        }
                    }

                    // WebP Windows умеет читать, но не записывать — копия сохраняется в PNG
                    string ext = Path.GetExtension(img);
                    string outExt = ext.Equals(".webp", StringComparison.OrdinalIgnoreCase) ? ".png" : ext;
                    string dst = Path.Combine(dir, stem + o.Suffix + outExt);
                    if (File.Exists(dst)) r.Skipped++;
                    else
                    {
                        MirrorImage(img, dst);
                        r.Images++;
                        job.Log($"[{i + 1}/{images.Count}] {name} -> {Path.GetFileName(dst)}" +
                                (outExt != ext ? "  (WebP сохранён как PNG)" : ""));
                    }

                    bool found = false;
                    foreach (var cap in Directory.EnumerateFiles(dir, stem + ".*"))
                    {
                        if (Path.GetFileNameWithoutExtension(cap) != stem || !CaptionExts.Contains(Path.GetExtension(cap)))
                            continue;
                        found = true;
                        string cdst = Path.Combine(dir, stem + o.Suffix + Path.GetExtension(cap));
                        if (File.Exists(cdst)) continue;
                        if (o.SwapLeftRight && SwappableExts.Contains(Path.GetExtension(cap)) && CopySwapped(cap, cdst))
                            r.Swapped++;
                        else
                            File.Copy(cap, cdst);
                        r.Captions++;
                    }
                    if (!found)
                    {
                        r.NoCaption++;
                        job.Log($"  ! нет файла-описания для {name}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Одна битая картинка не должна останавливать всё
                    r.Errors++;
                    string msg = ex is NotSupportedException or FileFormatException or System.Runtime.InteropServices.COMException
                        ? "не удалось прочитать изображение (файл повреждён или формат не поддерживается)"
                        : ex.Message;
                    job.Log($"  ОШИБКА {name}: {msg}");
                }
                finally
                {
                    job.Report(i + 1, images.Count, $"{i + 1} из {images.Count}");
                }
            }

            job.Log($"\nГотово. Создано изображений: {r.Images}, описаний: {r.Captions}" +
                    (o.SwapLeftRight ? $" (с заменой left↔right: {r.Swapped})" : "") +
                    $", пропущено (уже есть): {r.Skipped}, без описания: {r.NoCaption}, ошибок: {r.Errors}");
            return r;
        }

        // ── Отражение картинки ──
        private static void MirrorImage(string src, string dst)
        {
            BitmapFrame frame;
            using (var fs = File.OpenRead(src))
            {
                var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                frame = dec.Frames[0];
            }

            // Сначала применяется EXIF-поворот — зеркалится то, что видит человек
            var tg = new TransformGroup();
            AddOrientation(tg, ReadOrientation(frame));
            tg.Children.Add(new ScaleTransform(-1, 1));
            BitmapSource result = new TransformedBitmap(frame, tg);

            string ext = Path.GetExtension(dst).ToLowerInvariant();
            BitmapEncoder enc = ext switch
            {
                ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 95 },
                ".bmp" => new BmpBitmapEncoder(),
                ".tif" or ".tiff" => new TiffBitmapEncoder(),
                _ => new PngBitmapEncoder(),
            };
            if (enc is JpegBitmapEncoder &&
                result.Format != PixelFormats.Bgr24 && result.Format != PixelFormats.Gray8 &&
                result.Format != PixelFormats.Cmyk32 && result.Format != PixelFormats.Bgr32)
                result = new FormatConvertedBitmap(result, PixelFormats.Bgr24, null, 0);

            System.Collections.ObjectModel.ReadOnlyCollection<ColorContext>? icc = null;
            try { icc = frame.ColorContexts; } catch { /* у формата нет цветового профиля */ }
            enc.Frames.Add(BitmapFrame.Create(result, null, null, icc));

            string tmp = dst + ".part";
            try
            {
                using (var fs = File.Create(tmp)) enc.Save(fs);
                File.Move(tmp, dst, overwrite: false);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        private static int ReadOrientation(BitmapFrame frame)
        {
            if (frame.Metadata is not BitmapMetadata md) return 1;
            foreach (var q in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
            {
                try
                {
                    if (md.ContainsQuery(q) && md.GetQuery(q) is ushort v && v is >= 1 and <= 8) return v;
                }
                catch { /* запрос не поддерживается этим форматом */ }
            }
            return 1;
        }

        // EXIF Orientation → преобразование, приводящее картинку к виду "как показано"
        private static void AddOrientation(TransformGroup tg, int o)
        {
            switch (o)
            {
                case 2: tg.Children.Add(new ScaleTransform(-1, 1)); break;
                case 3: tg.Children.Add(new RotateTransform(180)); break;
                case 4: tg.Children.Add(new ScaleTransform(1, -1)); break;
                case 5: tg.Children.Add(new ScaleTransform(-1, 1)); tg.Children.Add(new RotateTransform(270)); break;
                case 6: tg.Children.Add(new RotateTransform(90)); break;
                case 7: tg.Children.Add(new ScaleTransform(-1, 1)); tg.Children.Add(new RotateTransform(90)); break;
                case 8: tg.Children.Add(new RotateTransform(270)); break;
            }
        }

        // ── left ↔ right в описании ──
        // Слово выделяется по буквам, а не по \b: "_" из тегов e621/rule34
        // ("facing_left") должно считаться разделителем, а "cleft" — нет.
        private static readonly Regex LeftRight =
            new(@"(?<![A-Za-z])(left|right)(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string SwapLeftRight(string text) => LeftRight.Replace(text, m =>
        {
            string w = m.Value;
            string swapped = w.Equals("left", StringComparison.OrdinalIgnoreCase) ? "right" : "left";
            if (w.All(char.IsUpper)) return swapped.ToUpperInvariant();
            if (char.IsUpper(w[0])) return char.ToUpperInvariant(swapped[0]) + swapped[1..];
            return swapped;
        });

        // true — если в тексте было что менять (иначе вызывающий просто копирует файл)
        private static bool CopySwapped(string src, string dst)
        {
            var (text, _) = TextCodec.Decode(File.ReadAllBytes(src));
            string swapped = SwapLeftRight(text);
            if (swapped == text) return false;
            File.WriteAllBytes(dst, TextCodec.Utf8NoBom.GetBytes(swapped));
            return true;
        }
    }
}
