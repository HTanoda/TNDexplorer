using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using TNDexplorer.Models;

namespace TNDexplorer.Services
{
    public class SettingsService
    {
        private readonly string _settingsPath;

        public SettingsService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appFolder = Path.Combine(appData, "TNDexplorer");
            _settingsPath = Path.Combine(appFolder, "settings.json");
        }

        public AppSettings Load()
        {
            if (!File.Exists(_settingsPath))
                return new AppSettings();

            try
            {
                byte[] json = File.ReadAllBytes(_settingsPath);
                using (var stream = new MemoryStream(json))
                {
                    var serializer = new DataContractJsonSerializer(typeof(AppSettings));
                    return (AppSettings)serializer.ReadObject(stream);
                }
            }
            catch
            {
                QuarantineCorruptFile();
                return new AppSettings();
            }
        }

        private void QuarantineCorruptFile()
        {
            try
            {
                string corruptPath = _settingsPath + ".corrupt";
                if (File.Exists(corruptPath))
                    File.Delete(corruptPath);
                File.Move(_settingsPath, corruptPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to quarantine corrupt settings: {ex.Message}");
            }
        }

        public void Save(AppSettings settings)
        {
            try
            {
                string dir = Path.GetDirectoryName(_settingsPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                byte[] payload;
                using (var stream = new MemoryStream())
                {
                    var serializer = new DataContractJsonSerializer(typeof(AppSettings));
                    using (var writer = JsonReaderWriterFactory.CreateJsonWriter(
                        stream, Encoding.UTF8, true, true, "  "))
                    {
                        serializer.WriteObject(writer, settings);
                        writer.Flush();
                    }
                    payload = stream.ToArray();
                }

                string tempPath = _settingsPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                try
                {
                    using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        fs.Write(payload, 0, payload.Length);
                        fs.Flush(true);
                    }

                    if (File.Exists(_settingsPath))
                        File.Delete(_settingsPath);
                    File.Move(tempPath, _settingsPath);
                }
                finally
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }
    }
}
