using System.IO;
using System.Text;

namespace ArtFinder.Tools
{
    // Связь фоновой задачи с окном: журнал, прогресс, отмена.
    // Реализация потокобезопасна — вызывать можно из любого потока.
    internal interface IJobReporter
    {
        CancellationToken Token { get; }
        void Log(string line);
        void Report(int done, int total, string? text = null);
    }

    // Чтение текстовых описаний датасета в любой из встречающихся кодировок
    internal static class TextCodec
    {
        static TextCodec() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
        public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        // Возвращает (текст, название исходной кодировки)
        public static (string Text, string Encoding) Decode(byte[] raw)
        {
            if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
                return (Encoding.Unicode.GetString(raw, 2, raw.Length - 2), "UTF-16");
            if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
                return (Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2), "UTF-16");
            if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
                return (Encoding.UTF8.GetString(raw, 3, raw.Length - 3), "UTF-8 BOM");
            try { return (StrictUtf8.GetString(raw), "UTF-8"); }
            catch (DecoderFallbackException) { return (Encoding.GetEncoding(1251).GetString(raw), "CP1251"); }
        }

        // Запись через временный файл: при сбое исходный файл не портится
        public static void WriteAtomic(string path, byte[] data)
        {
            string tmp = path + ".part";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
        }
    }
}
