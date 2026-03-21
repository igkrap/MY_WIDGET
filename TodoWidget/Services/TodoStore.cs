using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Data.SQLite;
using TodoWidget.Models;

namespace TodoWidget.Services
{
    public class TodoStore
    {
        private sealed class RecurrenceRule
        {
            public string Id { get; set; }
            public string TitleTemplate { get; set; }
            public string TaskTime { get; set; }
            public bool ReminderEnabled { get; set; }
            public string RecurrenceMode { get; set; }
            public int WeekdayMask { get; set; }
        }

        private readonly string _databasePath;
        private readonly string _legacyJsonPath;
        private readonly string _connectionString;

        public sealed class ExportResult
        {
            public string JsonPath { get; set; }
            public string CsvPath { get; set; }
        }

        public sealed class RecurrenceRuleEntry
        {
            public string Id { get; set; }
            public string TitleTemplate { get; set; }
            public string TaskTime { get; set; }
            public string RecurrenceMode { get; set; }
            public int WeekdayMask { get; set; }
            public bool IsActive { get; set; }
            public string RuleLabel { get; set; }
        }

        public TodoStore()
        {
            var appDataDirectory = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "TodoWidget");

            Directory.CreateDirectory(appDataDirectory);
            _databasePath = Path.Combine(appDataDirectory, "todos.db");
            _legacyJsonPath = Path.Combine(appDataDirectory, "todos.json");
            _connectionString = "Data Source=" + _databasePath + ";Version=3;";

            EnsureDatabase();
            MigrateFromLegacyJsonIfNeeded();
        }

        public string FilePath
        {
            get { return _databasePath; }
        }

        public string DirectoryPath
        {
            get { return Path.GetDirectoryName(_databasePath); }
        }

        public IList<TodoItem> Load()
        {
            var items = new List<TodoItem>();

            using (var connection = CreateConnection())
            {
                connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "SELECT id, title, is_completed, task_date, task_time, sort_order, reminder_enabled, reminder_triggered, is_recurring_instance, recurrence_mode, recurrence_weekday_mask, recurrence_rule_id " +
                        "FROM todos " +
                        "ORDER BY task_date ASC, sort_order ASC, title ASC;";

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            items.Add(new TodoItem
                            {
                                Id = Guid.Parse(reader.GetString(0)),
                                Title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                                IsCompleted = !reader.IsDBNull(2) && reader.GetInt32(2) == 1,
                                TaskDate = ParseIsoDate(reader.IsDBNull(3) ? null : reader.GetString(3)),
                                TaskTime = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                                SortOrder = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                                ReminderEnabled = reader.IsDBNull(6) || reader.GetInt32(6) == 1,
                                ReminderTriggered = !reader.IsDBNull(7) && reader.GetInt32(7) == 1,
                                IsRecurringInstance = !reader.IsDBNull(8) && reader.GetInt32(8) == 1,
                                RecurrenceMode = reader.IsDBNull(9) ? "none" : reader.GetString(9),
                                RecurrenceWeekdayMask = reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                                RecurrenceRuleId = reader.IsDBNull(11) ? string.Empty : reader.GetString(11)
                            });
                        }
                    }
                }
            }

            return items;
        }

        public void Save(IEnumerable<TodoItem> items)
        {
            var snapshot = (items ?? Enumerable.Empty<TodoItem>()).ToList();

            using (var connection = CreateConnection())
            {
                connection.Open();

                using (var transaction = connection.BeginTransaction())
                {
                    using (var deleteCommand = connection.CreateCommand())
                    {
                        deleteCommand.Transaction = transaction;
                        deleteCommand.CommandText = "DELETE FROM todos;";
                        deleteCommand.ExecuteNonQuery();
                    }

                    using (var insertCommand = connection.CreateCommand())
                    {
                        insertCommand.Transaction = transaction;
                        insertCommand.CommandText =
                            "INSERT INTO todos (id, title, is_completed, task_date, task_time, sort_order, reminder_enabled, reminder_triggered, is_recurring_instance, recurrence_mode, recurrence_weekday_mask, recurrence_rule_id, updated_at) " +
                            "VALUES (@id, @title, @is_completed, @task_date, @task_time, @sort_order, @reminder_enabled, @reminder_triggered, @is_recurring_instance, @recurrence_mode, @recurrence_weekday_mask, @recurrence_rule_id, @updated_at);";

                        var idParam = insertCommand.Parameters.Add("@id", System.Data.DbType.String);
                        var titleParam = insertCommand.Parameters.Add("@title", System.Data.DbType.String);
                        var completedParam = insertCommand.Parameters.Add("@is_completed", System.Data.DbType.Int32);
                        var dateParam = insertCommand.Parameters.Add("@task_date", System.Data.DbType.String);
                        var timeParam = insertCommand.Parameters.Add("@task_time", System.Data.DbType.String);
                        var orderParam = insertCommand.Parameters.Add("@sort_order", System.Data.DbType.Int32);
                        var reminderEnabledParam = insertCommand.Parameters.Add("@reminder_enabled", System.Data.DbType.Int32);
                        var reminderTriggeredParam = insertCommand.Parameters.Add("@reminder_triggered", System.Data.DbType.Int32);
                        var recurringParam = insertCommand.Parameters.Add("@is_recurring_instance", System.Data.DbType.Int32);
                        var recurrenceModeParam = insertCommand.Parameters.Add("@recurrence_mode", System.Data.DbType.String);
                        var recurrenceWeekdayMaskParam = insertCommand.Parameters.Add("@recurrence_weekday_mask", System.Data.DbType.Int32);
                        var recurrenceRuleIdParam = insertCommand.Parameters.Add("@recurrence_rule_id", System.Data.DbType.String);
                        var updatedAtParam = insertCommand.Parameters.Add("@updated_at", System.Data.DbType.String);

                        var updatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

                        foreach (var item in snapshot)
                        {
                            idParam.Value = item.Id.ToString("D");
                            titleParam.Value = item.Title ?? string.Empty;
                            completedParam.Value = item.IsCompleted ? 1 : 0;
                            dateParam.Value = item.TaskDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                            timeParam.Value = item.TaskTime ?? string.Empty;
                            orderParam.Value = item.SortOrder;
                            reminderEnabledParam.Value = item.ReminderEnabled ? 1 : 0;
                            reminderTriggeredParam.Value = item.ReminderTriggered ? 1 : 0;
                            recurringParam.Value = item.IsRecurringInstance ? 1 : 0;
                            recurrenceModeParam.Value = item.RecurrenceMode ?? "none";
                            recurrenceWeekdayMaskParam.Value = item.RecurrenceWeekdayMask;
                            recurrenceRuleIdParam.Value = string.IsNullOrWhiteSpace(item.RecurrenceRuleId) ? (object)DBNull.Value : item.RecurrenceRuleId;
                            updatedAtParam.Value = updatedAt;
                            insertCommand.ExecuteNonQuery();
                        }
                    }

                    transaction.Commit();
                }
            }
        }

        public ExportResult ExportReport(string outputDirectory)
        {
            var targetDirectory = string.IsNullOrWhiteSpace(outputDirectory) ? DirectoryPath : outputDirectory;
            Directory.CreateDirectory(targetDirectory);

            var rows = Load()
                .OrderBy(item => item.TaskDate)
                .ThenBy(item => item.SortOrder)
                .ThenBy(item => item.Title)
                .ToList();

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var jsonPath = Path.Combine(targetDirectory, "todo-report-" + stamp + ".json");
            var csvPath = Path.Combine(targetDirectory, "todo-report-" + stamp + ".csv");

            using (var stream = File.Create(jsonPath))
            {
                var serializer = new DataContractJsonSerializer(typeof(List<TodoItem>));
                serializer.WriteObject(stream, rows);
            }

            var csv = new StringBuilder();
            csv.AppendLine("Id,Title,IsCompleted,TaskDate,TaskTime,SortOrder,ReminderEnabled,ReminderTriggered,IsRecurringInstance,RecurrenceMode,RecurrenceWeekdayMask,RecurrenceRuleId");
            foreach (var row in rows)
            {
                csv.AppendLine(
                    EscapeCsv(row.Id.ToString("D")) + "," +
                    EscapeCsv(row.Title) + "," +
                    (row.IsCompleted ? "1" : "0") + "," +
                    row.TaskDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "," +
                    EscapeCsv(row.TaskTime) + "," +
                    row.SortOrder.ToString(CultureInfo.InvariantCulture) + "," +
                    (row.ReminderEnabled ? "1" : "0") + "," +
                    (row.ReminderTriggered ? "1" : "0") + "," +
                    (row.IsRecurringInstance ? "1" : "0") + "," +
                    EscapeCsv(row.RecurrenceMode) + "," +
                    row.RecurrenceWeekdayMask.ToString(CultureInfo.InvariantCulture) + "," +
                    EscapeCsv(row.RecurrenceRuleId));
            }

            File.WriteAllText(csvPath, csv.ToString(), Encoding.UTF8);

            return new ExportResult
            {
                JsonPath = jsonPath,
                CsvPath = csvPath
            };
        }

        public IList<TodoItem> EnsureRecurringInstancesForDate(DateTime date)
        {
            var targetDate = date.Date;
            var created = new List<TodoItem>();

            using (var connection = CreateConnection())
            {
                connection.Open();

                using (var transaction = connection.BeginTransaction())
                {
                    var rules = LoadActiveRules(connection, transaction);
                    if (rules.Count == 0)
                    {
                        transaction.Commit();
                        return created;
                    }

                    var existingRuleIds = LoadExistingRuleIdsForDate(connection, transaction, targetDate);
                    var skippedRuleIds = LoadSkippedRuleIdsForDate(connection, transaction, targetDate);
                    var sortOrder = GetNextSortOrderForDate(connection, transaction, targetDate);
                    var updatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

                    foreach (var rule in rules)
                    {
                        if (string.IsNullOrWhiteSpace(rule.Id))
                        {
                            continue;
                        }

                        if (existingRuleIds.Contains(rule.Id))
                        {
                            continue;
                        }

                        if (skippedRuleIds.Contains(rule.Id))
                        {
                            continue;
                        }

                        if (!DoesRuleApplyToDate(rule, targetDate))
                        {
                            continue;
                        }

                        var item = new TodoItem
                        {
                            Id = Guid.NewGuid(),
                            Title = rule.TitleTemplate ?? string.Empty,
                            IsCompleted = false,
                            TaskDate = targetDate,
                            TaskTime = rule.TaskTime ?? string.Empty,
                            SortOrder = sortOrder++,
                            ReminderEnabled = rule.ReminderEnabled && !string.IsNullOrWhiteSpace(rule.TaskTime),
                            ReminderTriggered = false,
                            IsRecurringInstance = true,
                            RecurrenceMode = string.IsNullOrWhiteSpace(rule.RecurrenceMode) ? "none" : rule.RecurrenceMode,
                            RecurrenceWeekdayMask = rule.WeekdayMask,
                            RecurrenceRuleId = rule.Id
                        };

                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText =
                                "INSERT INTO todos (id, title, is_completed, task_date, task_time, sort_order, reminder_enabled, reminder_triggered, is_recurring_instance, recurrence_mode, recurrence_weekday_mask, recurrence_rule_id, updated_at) " +
                                "VALUES (@id, @title, @is_completed, @task_date, @task_time, @sort_order, @reminder_enabled, @reminder_triggered, @is_recurring_instance, @recurrence_mode, @recurrence_weekday_mask, @recurrence_rule_id, @updated_at);";

                            command.Parameters.Add("@id", System.Data.DbType.String).Value = item.Id.ToString("D");
                            command.Parameters.Add("@title", System.Data.DbType.String).Value = item.Title;
                            command.Parameters.Add("@is_completed", System.Data.DbType.Int32).Value = 0;
                            command.Parameters.Add("@task_date", System.Data.DbType.String).Value = item.TaskDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                            command.Parameters.Add("@task_time", System.Data.DbType.String).Value = item.TaskTime;
                            command.Parameters.Add("@sort_order", System.Data.DbType.Int32).Value = item.SortOrder;
                            command.Parameters.Add("@reminder_enabled", System.Data.DbType.Int32).Value = item.ReminderEnabled ? 1 : 0;
                            command.Parameters.Add("@reminder_triggered", System.Data.DbType.Int32).Value = 0;
                            command.Parameters.Add("@is_recurring_instance", System.Data.DbType.Int32).Value = 1;
                            command.Parameters.Add("@recurrence_mode", System.Data.DbType.String).Value = item.RecurrenceMode;
                            command.Parameters.Add("@recurrence_weekday_mask", System.Data.DbType.Int32).Value = item.RecurrenceWeekdayMask;
                            command.Parameters.Add("@recurrence_rule_id", System.Data.DbType.String).Value = item.RecurrenceRuleId;
                            command.Parameters.Add("@updated_at", System.Data.DbType.String).Value = updatedAt;
                            command.ExecuteNonQuery();
                        }

                        created.Add(item);
                    }

                    transaction.Commit();
                }
            }

            return created;
        }

        public ISet<DateTime> GetActiveRecurrenceDatesInRange(DateTime startDate, DateTime endDate)
        {
            var from = startDate.Date;
            var to = endDate.Date;
            if (to < from)
            {
                var temp = from;
                from = to;
                to = temp;
            }

            var result = new HashSet<DateTime>();

            using (var connection = CreateConnection())
            {
                connection.Open();

                using (var transaction = connection.BeginTransaction())
                {
                    var rules = LoadActiveRules(connection, transaction);
                    if (rules.Count == 0)
                    {
                        transaction.Commit();
                        return result;
                    }

                    var skippedRuleIdsByDate = LoadSkippedRuleIdsByDateInRange(connection, transaction, from, to);

                    for (var date = from; date <= to; date = date.AddDays(1))
                    {
                        HashSet<string> skippedRuleIdsForDate;
                        skippedRuleIdsByDate.TryGetValue(date, out skippedRuleIdsForDate);

                        foreach (var rule in rules)
                        {
                            if (string.IsNullOrWhiteSpace(rule.Id))
                            {
                                continue;
                            }

                            if (skippedRuleIdsForDate != null && skippedRuleIdsForDate.Contains(rule.Id))
                            {
                                continue;
                            }

                            if (DoesRuleApplyToDate(rule, date))
                            {
                                result.Add(date);
                                break;
                            }
                        }
                    }

                    transaction.Commit();
                }
            }

            return result;
        }

        public IList<RecurrenceRuleEntry> LoadRecurrenceRules()
        {
            var rules = new List<RecurrenceRuleEntry>();

            using (var connection = CreateConnection())
            {
                connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "SELECT id, title_template, task_time, recurrence_mode, weekday_mask, is_active " +
                        "FROM recurrence_rules " +
                        "ORDER BY updated_at DESC;";

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            rules.Add(new RecurrenceRuleEntry
                            {
                                Id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                                TitleTemplate = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                                TaskTime = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                                RecurrenceMode = reader.IsDBNull(3) ? "none" : reader.GetString(3),
                                WeekdayMask = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                                IsActive = reader.IsDBNull(5) || reader.GetInt32(5) == 1
                            });
                            rules[rules.Count - 1].RuleLabel = BuildRuleLabel(rules[rules.Count - 1].RecurrenceMode, rules[rules.Count - 1].WeekdayMask, rules[rules.Count - 1].TaskTime);
                        }
                    }
                }
            }

            return rules;
        }

        public void AddRecurrenceSkip(string recurrenceRuleId, DateTime occurrenceDate)
        {
            if (string.IsNullOrWhiteSpace(recurrenceRuleId))
            {
                return;
            }

            using (var connection = CreateConnection())
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "INSERT OR REPLACE INTO recurrence_exceptions (id, recurrence_rule_id, occurrence_date, exception_type, created_at, updated_at) " +
                        "VALUES (@id, @recurrence_rule_id, @occurrence_date, 'skip', @created_at, @updated_at);";
                    command.Parameters.Add("@id", System.Data.DbType.String).Value = recurrenceRuleId + "|" + occurrenceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|skip";
                    command.Parameters.Add("@recurrence_rule_id", System.Data.DbType.String).Value = recurrenceRuleId;
                    command.Parameters.Add("@occurrence_date", System.Data.DbType.String).Value = occurrenceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    command.Parameters.Add("@created_at", System.Data.DbType.String).Value = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    command.Parameters.Add("@updated_at", System.Data.DbType.String).Value = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    command.ExecuteNonQuery();
                }
            }
        }

        public void DeleteRecurrenceRule(string recurrenceRuleId)
        {
            if (string.IsNullOrWhiteSpace(recurrenceRuleId))
            {
                return;
            }

            using (var connection = CreateConnection())
            {
                connection.Open();

                using (var transaction = connection.BeginTransaction())
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "DELETE FROM recurrence_rules WHERE id = @id;";
                        command.Parameters.Add("@id", System.Data.DbType.String).Value = recurrenceRuleId;
                        command.ExecuteNonQuery();
                    }

                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "DELETE FROM recurrence_exceptions WHERE recurrence_rule_id = @id;";
                        command.Parameters.Add("@id", System.Data.DbType.String).Value = recurrenceRuleId;
                        command.ExecuteNonQuery();
                    }

                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "DELETE FROM todos WHERE recurrence_rule_id = @id;";
                        command.Parameters.Add("@id", System.Data.DbType.String).Value = recurrenceRuleId;
                        command.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }
            }
        }

        public void SetRecurrenceRuleActive(string recurrenceRuleId, bool isActive)
        {
            if (string.IsNullOrWhiteSpace(recurrenceRuleId))
            {
                return;
            }

            using (var connection = CreateConnection())
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "UPDATE recurrence_rules " +
                        "SET is_active = @is_active, updated_at = @updated_at " +
                        "WHERE id = @id;";
                    command.Parameters.Add("@is_active", System.Data.DbType.Int32).Value = isActive ? 1 : 0;
                    command.Parameters.Add("@updated_at", System.Data.DbType.String).Value = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    command.Parameters.Add("@id", System.Data.DbType.String).Value = recurrenceRuleId;
                    command.ExecuteNonQuery();
                }
            }
        }

        private SQLiteConnection CreateConnection()
        {
            return new SQLiteConnection(_connectionString);
        }

        private void EnsureDatabase()
        {
            using (var connection = CreateConnection())
            {
                connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "CREATE TABLE IF NOT EXISTS todos (" +
                        "id TEXT PRIMARY KEY NOT NULL," +
                        "title TEXT NOT NULL," +
                        "is_completed INTEGER NOT NULL," +
                        "task_date TEXT NOT NULL," +
                        "task_time TEXT NOT NULL," +
                        "sort_order INTEGER NOT NULL DEFAULT 0," +
                        "reminder_enabled INTEGER NOT NULL DEFAULT 1," +
                        "reminder_triggered INTEGER NOT NULL DEFAULT 0," +
                        "is_recurring_instance INTEGER NOT NULL DEFAULT 0," +
                        "recurrence_mode TEXT NOT NULL DEFAULT 'none'," +
                        "recurrence_weekday_mask INTEGER NOT NULL DEFAULT 0," +
                        "recurrence_rule_id TEXT NULL," +
                        "updated_at TEXT NOT NULL" +
                        ");";
                    command.ExecuteNonQuery();
                }

                EnsureColumnExists(connection, "todos", "reminder_enabled", "INTEGER NOT NULL DEFAULT 1");
                EnsureColumnExists(connection, "todos", "reminder_triggered", "INTEGER NOT NULL DEFAULT 0");
                EnsureColumnExists(connection, "todos", "is_recurring_instance", "INTEGER NOT NULL DEFAULT 0");
                EnsureColumnExists(connection, "todos", "recurrence_mode", "TEXT NOT NULL DEFAULT 'none'");
                EnsureColumnExists(connection, "todos", "recurrence_weekday_mask", "INTEGER NOT NULL DEFAULT 0");
                EnsureColumnExists(connection, "todos", "recurrence_rule_id", "TEXT NULL");

                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "CREATE TABLE IF NOT EXISTS recurrence_rules (" +
                        "id TEXT PRIMARY KEY NOT NULL," +
                        "title_template TEXT NOT NULL," +
                        "task_time TEXT NOT NULL," +
                        "reminder_enabled INTEGER NOT NULL DEFAULT 1," +
                        "recurrence_mode TEXT NOT NULL," +
                        "weekday_mask INTEGER NOT NULL DEFAULT 0," +
                        "is_active INTEGER NOT NULL DEFAULT 1," +
                        "created_at TEXT NOT NULL," +
                        "updated_at TEXT NOT NULL" +
                        ");";
                    command.ExecuteNonQuery();
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "CREATE TABLE IF NOT EXISTS recurrence_exceptions (" +
                        "id TEXT PRIMARY KEY NOT NULL," +
                        "recurrence_rule_id TEXT NOT NULL," +
                        "occurrence_date TEXT NOT NULL," +
                        "exception_type TEXT NOT NULL," +
                        "created_at TEXT NOT NULL," +
                        "updated_at TEXT NOT NULL" +
                        ");";
                    command.ExecuteNonQuery();
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "CREATE INDEX IF NOT EXISTS idx_todos_date_sort ON todos(task_date, sort_order);";
                    command.ExecuteNonQuery();
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "CREATE INDEX IF NOT EXISTS idx_recurrence_rules_mode_active ON recurrence_rules(recurrence_mode, is_active);";
                    command.ExecuteNonQuery();
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "CREATE INDEX IF NOT EXISTS idx_recurrence_exceptions_rule_date ON recurrence_exceptions(recurrence_rule_id, occurrence_date, exception_type);";
                    command.ExecuteNonQuery();
                }
            }
        }

        public void SaveRecurrenceRule(TodoItem item)
        {
            if (item == null ||
                !item.IsRecurringInstance ||
                string.Equals(item.RecurrenceMode, "none", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(item.RecurrenceRuleId))
            {
                return;
            }

            var updatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            using (var connection = CreateConnection())
            {
                connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "INSERT OR REPLACE INTO recurrence_rules (id, title_template, task_time, reminder_enabled, recurrence_mode, weekday_mask, is_active, created_at, updated_at) " +
                        "VALUES (@id, @title_template, @task_time, @reminder_enabled, @recurrence_mode, @weekday_mask, 1, @created_at, @updated_at);";

                    command.Parameters.Add("@id", System.Data.DbType.String).Value = item.RecurrenceRuleId;
                    command.Parameters.Add("@title_template", System.Data.DbType.String).Value = item.Title ?? string.Empty;
                    command.Parameters.Add("@task_time", System.Data.DbType.String).Value = item.TaskTime ?? string.Empty;
                    command.Parameters.Add("@reminder_enabled", System.Data.DbType.Int32).Value =
                        (!string.IsNullOrWhiteSpace(item.TaskTime) && item.ReminderEnabled) ? 1 : 0;
                    command.Parameters.Add("@recurrence_mode", System.Data.DbType.String).Value = item.RecurrenceMode ?? "none";
                    command.Parameters.Add("@weekday_mask", System.Data.DbType.Int32).Value = item.RecurrenceWeekdayMask;
                    command.Parameters.Add("@created_at", System.Data.DbType.String).Value = updatedAt;
                    command.Parameters.Add("@updated_at", System.Data.DbType.String).Value = updatedAt;
                    command.ExecuteNonQuery();
                }
            }
        }

        private void MigrateFromLegacyJsonIfNeeded()
        {
            if (!File.Exists(_legacyJsonPath))
            {
                return;
            }

            if (GetRowCount() > 0)
            {
                return;
            }

            List<TodoItem> legacyItems;
            try
            {
                using (var stream = File.OpenRead(_legacyJsonPath))
                {
                    var serializer = new DataContractJsonSerializer(typeof(List<TodoItem>));
                    legacyItems = serializer.ReadObject(stream) as List<TodoItem>;
                }
            }
            catch
            {
                return;
            }

            if (legacyItems == null || legacyItems.Count == 0)
            {
                return;
            }

            Save(legacyItems);

            var backupPath = Path.Combine(DirectoryPath, "todos.legacy." + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".json");
            try
            {
                File.Copy(_legacyJsonPath, backupPath, true);
            }
            catch
            {
                // Keep migration non-fatal; existing JSON can stay if backup fails.
            }
        }

        private int GetRowCount()
        {
            using (var connection = CreateConnection())
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(1) FROM todos;";
                    return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
            }
        }

        private static DateTime ParseIsoDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return DateTime.Today;
            }

            DateTime parsed;
            if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            {
                return parsed;
            }

            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            {
                return parsed.Date;
            }

            return DateTime.Today;
        }

        private static string EscapeCsv(string value)
        {
            var text = value ?? string.Empty;
            if (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
            {
                return text;
            }

            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        private static void EnsureColumnExists(SQLiteConnection connection, string tableName, string columnName, string definition)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(" + tableName + ");";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var existingColumn = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                        if (string.Equals(existingColumn, columnName, StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }
                    }
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "ALTER TABLE " + tableName + " ADD COLUMN " + columnName + " " + definition + ";";
                command.ExecuteNonQuery();
            }
        }

        private static List<RecurrenceRule> LoadActiveRules(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            var rules = new List<RecurrenceRule>();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "SELECT id, title_template, task_time, reminder_enabled, recurrence_mode, weekday_mask " +
                    "FROM recurrence_rules " +
                    "WHERE is_active = 1;";

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        rules.Add(new RecurrenceRule
                        {
                            Id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                            TitleTemplate = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                            TaskTime = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                            ReminderEnabled = reader.IsDBNull(3) || reader.GetInt32(3) == 1,
                            RecurrenceMode = reader.IsDBNull(4) ? "none" : reader.GetString(4),
                            WeekdayMask = reader.IsDBNull(5) ? 0 : reader.GetInt32(5)
                        });
                    }
                }
            }

            return rules;
        }

        private static HashSet<string> LoadExistingRuleIdsForDate(SQLiteConnection connection, SQLiteTransaction transaction, DateTime targetDate)
        {
            var existingRuleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "SELECT recurrence_rule_id " +
                    "FROM todos " +
                    "WHERE task_date = @task_date " +
                    "AND recurrence_rule_id IS NOT NULL " +
                    "AND recurrence_rule_id <> '';";
                command.Parameters.Add("@task_date", System.Data.DbType.String).Value = targetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var ruleId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                        if (!string.IsNullOrWhiteSpace(ruleId))
                        {
                            existingRuleIds.Add(ruleId);
                        }
                    }
                }
            }

            return existingRuleIds;
        }

        private static int GetNextSortOrderForDate(SQLiteConnection connection, SQLiteTransaction transaction, DateTime targetDate)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT COALESCE(MAX(sort_order), -1) FROM todos WHERE task_date = @task_date;";
                command.Parameters.Add("@task_date", System.Data.DbType.String).Value = targetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var maxOrder = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                return maxOrder + 1;
            }
        }

        private static HashSet<string> LoadSkippedRuleIdsForDate(SQLiteConnection connection, SQLiteTransaction transaction, DateTime targetDate)
        {
            var skippedRuleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "SELECT recurrence_rule_id " +
                    "FROM recurrence_exceptions " +
                    "WHERE occurrence_date = @occurrence_date " +
                    "AND exception_type = 'skip';";
                command.Parameters.Add("@occurrence_date", System.Data.DbType.String).Value = targetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var ruleId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                        if (!string.IsNullOrWhiteSpace(ruleId))
                        {
                            skippedRuleIds.Add(ruleId);
                        }
                    }
                }
            }

            return skippedRuleIds;
        }

        private static Dictionary<DateTime, HashSet<string>> LoadSkippedRuleIdsByDateInRange(SQLiteConnection connection, SQLiteTransaction transaction, DateTime startDate, DateTime endDate)
        {
            var skippedRuleIdsByDate = new Dictionary<DateTime, HashSet<string>>();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "SELECT occurrence_date, recurrence_rule_id " +
                    "FROM recurrence_exceptions " +
                    "WHERE occurrence_date >= @start_date " +
                    "AND occurrence_date <= @end_date " +
                    "AND exception_type = 'skip';";
                command.Parameters.Add("@start_date", System.Data.DbType.String).Value = startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                command.Parameters.Add("@end_date", System.Data.DbType.String).Value = endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var occurrenceDate = ParseIsoDate(reader.IsDBNull(0) ? null : reader.GetString(0)).Date;
                        var ruleId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                        if (string.IsNullOrWhiteSpace(ruleId))
                        {
                            continue;
                        }

                        HashSet<string> skippedRuleIds;
                        if (!skippedRuleIdsByDate.TryGetValue(occurrenceDate, out skippedRuleIds))
                        {
                            skippedRuleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            skippedRuleIdsByDate[occurrenceDate] = skippedRuleIds;
                        }

                        skippedRuleIds.Add(ruleId);
                    }
                }
            }

            return skippedRuleIdsByDate;
        }

        private static bool DoesRuleApplyToDate(RecurrenceRule rule, DateTime targetDate)
        {
            if (rule == null)
            {
                return false;
            }

            if (string.Equals(rule.RecurrenceMode, "daily", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.Equals(rule.RecurrenceMode, "weekly", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var bit = GetWeekdayBit(targetDate.DayOfWeek);
            return bit != 0 && (rule.WeekdayMask & bit) != 0;
        }

        private static int GetWeekdayBit(DayOfWeek dayOfWeek)
        {
            switch (dayOfWeek)
            {
                case DayOfWeek.Monday:
                    return 1;
                case DayOfWeek.Tuesday:
                    return 2;
                case DayOfWeek.Wednesday:
                    return 4;
                case DayOfWeek.Thursday:
                    return 8;
                case DayOfWeek.Friday:
                    return 16;
                case DayOfWeek.Saturday:
                    return 32;
                default:
                    return 64;
            }
        }

        private static string BuildRuleLabel(string recurrenceMode, int weekdayMask, string taskTime)
        {
            var normalizedMode = string.IsNullOrWhiteSpace(recurrenceMode) ? "none" : recurrenceMode.Trim().ToLowerInvariant();
            var normalizedTime = taskTime ?? string.Empty;

            if (string.Equals(normalizedMode, "daily", StringComparison.Ordinal))
            {
                return string.IsNullOrWhiteSpace(normalizedTime) ? "매일" : "매일 " + normalizedTime;
            }

            if (string.Equals(normalizedMode, "weekly", StringComparison.Ordinal))
            {
                var labels = new List<string>();
                if ((weekdayMask & 1) != 0) labels.Add("월");
                if ((weekdayMask & 2) != 0) labels.Add("화");
                if ((weekdayMask & 4) != 0) labels.Add("수");
                if ((weekdayMask & 8) != 0) labels.Add("목");
                if ((weekdayMask & 16) != 0) labels.Add("금");
                if ((weekdayMask & 32) != 0) labels.Add("토");
                if ((weekdayMask & 64) != 0) labels.Add("일");

                var dayText = labels.Count == 0 ? "매주" : string.Join(",", labels);
                return string.IsNullOrWhiteSpace(normalizedTime) ? dayText : dayText + " " + normalizedTime;
            }

            return normalizedTime;
        }
    }
}
