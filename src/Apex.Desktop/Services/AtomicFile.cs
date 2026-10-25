using System;
using System.IO;
using System.Text;

namespace Apex.Desktop.Services
{
    /// <summary>Zapis do pliku obok celu; flush na dysk przed atomową zamianą nazwy.</summary>
    internal static class AtomicFile
    {
        public static void Write(string path, string content)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(content);
                    stream.Write(bytes);
                    stream.Flush(true);
                }
                File.Move(tmp, path, true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
    }
}
