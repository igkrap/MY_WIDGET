using System.IO;
using System.Runtime.Serialization.Json;
using TodoWidget.Models;

namespace TodoWidget.Services
{
    public class SettingsStore
    {
        private readonly string _filePath;

        public SettingsStore()
        {
            var appDataDirectory = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "TodoWidget");

            Directory.CreateDirectory(appDataDirectory);
            _filePath = Path.Combine(appDataDirectory, "settings.json");
        }

        public WidgetSettings Load()
        {
            if (!File.Exists(_filePath))
            {
                return CreateDefault();
            }

            try
            {
                using (var stream = File.OpenRead(_filePath))
                {
                    var serializer = new DataContractJsonSerializer(typeof(WidgetSettings));
                    var result = serializer.ReadObject(stream) as WidgetSettings;
                    return result ?? CreateDefault();
                }
            }
            catch
            {
                return CreateDefault();
            }
        }

        public void Save(WidgetSettings settings)
        {
            using (var stream = File.Create(_filePath))
            {
                var serializer = new DataContractJsonSerializer(typeof(WidgetSettings));
                serializer.WriteObject(stream, settings);
            }
        }

        private static WidgetSettings CreateDefault()
        {
            return new WidgetSettings
            {
                Opacity = 0.96,
                IsTopmost = true
            };
        }
    }
}
