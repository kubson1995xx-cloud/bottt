using System;
using System.IO;
using System.Text;

namespace Apex.Desktop.Services
{
    /// <summary>Zapis do pliku obok celu; flush na dysk przed atomową zamianą nazwy.</summary>
    internal enum AtomicWriteBoundary { BeforeTempCreate, AfterTempWrite, AfterTempFlush, BeforeReplace, AfterReplace }
    internal static class AtomicFile
    {
        public static void Write(string path, string content) => Write(path, new UTF8Encoding(false).GetBytes(content));

        internal static void Write(string path, byte[] bytes) => Write(path, bytes, null);

        internal static void Write(string path, byte[] bytes, Action<AtomicWriteBoundary>? observer)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                observer?.Invoke(AtomicWriteBoundary.BeforeTempCreate);
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    observer?.Invoke(AtomicWriteBoundary.AfterTempWrite);
                    stream.Flush(true);
                    observer?.Invoke(AtomicWriteBoundary.AfterTempFlush);
                }
                observer?.Invoke(AtomicWriteBoundary.BeforeReplace);
                File.Move(tmp, path, true);
                observer?.Invoke(AtomicWriteBoundary.AfterReplace);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
    }
}
