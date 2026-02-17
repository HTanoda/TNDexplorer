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
                return new AppSettings();
            }
        }

        public void Save(AppSettings settings)
        {
            try
            {
                string dir = Path.GetDirectoryName(_settingsPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using (var stream = new MemoryStream())
                {
                    var serializer = new DataContractJsonSerializer(typeof(AppSettings));
                    using (var writer = JsonReaderWriterFactory.CreateJsonWriter(
                        stream, Encoding.UTF8, true, true, "  "))
                    {
                        serializer.WriteObject(writer, settings);
                        writer.Flush();
                    }
                    File.WriteAllBytes(_settingsPath, stream.ToArray());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }
    }
}
