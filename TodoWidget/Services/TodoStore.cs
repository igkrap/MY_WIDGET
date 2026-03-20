using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using TodoWidget.Models;

namespace TodoWidget.Services
{
    public class TodoStore
    {
        private readonly string _filePath;

        public TodoStore()
        {
            var appDataDirectory = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "TodoWidget");

            Directory.CreateDirectory(appDataDirectory);
            _filePath = Path.Combine(appDataDirectory, "todos.json");
        }

        public string FilePath
        {
            get { return _filePath; }
        }

        public string DirectoryPath
        {
            get { return Path.GetDirectoryName(_filePath); }
        }

        public IList<TodoItem> Load()
        {
            if (!File.Exists(_filePath))
            {
                return new List<TodoItem>();
            }

            try
            {
                using (var stream = File.OpenRead(_filePath))
                {
                    var serializer = new DataContractJsonSerializer(typeof(List<TodoItem>));
                    var result = serializer.ReadObject(stream) as List<TodoItem>;
                    return result ?? new List<TodoItem>();
                }
            }
            catch
            {
                return new List<TodoItem>();
            }
        }

        public void Save(IEnumerable<TodoItem> items)
        {
            var snapshot = items.ToList();

            using (var stream = File.Create(_filePath))
            {
                var serializer = new DataContractJsonSerializer(typeof(List<TodoItem>));
                serializer.WriteObject(stream, snapshot);
            }
        }
    }
}
