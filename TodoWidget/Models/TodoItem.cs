using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.Serialization;

namespace TodoWidget.Models
{
    [DataContract]
    public class TodoItem : INotifyPropertyChanged
    {
        private string _title;
        private bool _isCompleted;
        private DateTime _taskDate;
        private string _taskTime;
        private int _sortOrder;
        private bool _isEditing;
        private bool _reminderEnabled = true;
        private bool _reminderTriggered;
        private bool _isRecurringInstance;
        private string _recurrenceMode = "none";
        private int _recurrenceWeekdayMask;
        private string _recurrenceRuleId;

        [DataMember(Order = 1)]
        public Guid Id { get; set; }

        [DataMember(Order = 2)]
        public string Title
        {
            get { return _title; }
            set
            {
                if (_title == value)
                {
                    return;
                }

                _title = value;
                OnPropertyChanged("Title");
            }
        }

        [DataMember(Order = 3)]
        public bool IsCompleted
        {
            get { return _isCompleted; }
            set
            {
                if (_isCompleted == value)
                {
                    return;
                }

                _isCompleted = value;
                OnPropertyChanged("IsCompleted");
            }
        }

        [DataMember(Order = 4)]
        public DateTime TaskDate
        {
            get { return _taskDate; }
            set
            {
                if (_taskDate == value)
                {
                    return;
                }

                _taskDate = value;
                OnPropertyChanged("TaskDate");
            }
        }

        [DataMember(Order = 5)]
        public string TaskTime
        {
            get { return _taskTime ?? string.Empty; }
            set
            {
                var nextValue = value ?? string.Empty;

                if (string.Equals(_taskTime, nextValue, StringComparison.Ordinal))
                {
                    return;
                }

                _taskTime = nextValue;
                OnPropertyChanged("TaskTime");
                OnPropertyChanged("ScheduleText");
            }
        }

        [DataMember(Order = 6)]
        public int SortOrder
        {
            get { return _sortOrder; }
            set
            {
                if (_sortOrder == value)
                {
                    return;
                }

                _sortOrder = value;
                OnPropertyChanged("SortOrder");
            }
        }

        [DataMember(Order = 7)]
        public bool ReminderEnabled
        {
            get { return _reminderEnabled; }
            set
            {
                if (_reminderEnabled == value)
                {
                    return;
                }

                _reminderEnabled = value;
                OnPropertyChanged("ReminderEnabled");
            }
        }

        [DataMember(Order = 8)]
        public bool ReminderTriggered
        {
            get { return _reminderTriggered; }
            set
            {
                if (_reminderTriggered == value)
                {
                    return;
                }

                _reminderTriggered = value;
                OnPropertyChanged("ReminderTriggered");
            }
        }

        [DataMember(Order = 9)]
        public bool IsRecurringInstance
        {
            get { return _isRecurringInstance; }
            set
            {
                if (_isRecurringInstance == value)
                {
                    return;
                }

                _isRecurringInstance = value;
                OnPropertyChanged("IsRecurringInstance");
                OnPropertyChanged("ScheduleText");
            }
        }

        [DataMember(Order = 10)]
        public string RecurrenceMode
        {
            get { return _recurrenceMode ?? "none"; }
            set
            {
                var nextValue = string.IsNullOrWhiteSpace(value)
                    ? "none"
                    : value.Trim().ToLowerInvariant();

                if (string.Equals(_recurrenceMode, nextValue, StringComparison.Ordinal))
                {
                    return;
                }

                _recurrenceMode = nextValue;
                OnPropertyChanged("RecurrenceMode");
                OnPropertyChanged("ScheduleText");
            }
        }

        [DataMember(Order = 11)]
        public int RecurrenceWeekdayMask
        {
            get { return _recurrenceWeekdayMask; }
            set
            {
                if (_recurrenceWeekdayMask == value)
                {
                    return;
                }

                _recurrenceWeekdayMask = value;
                OnPropertyChanged("RecurrenceWeekdayMask");
                OnPropertyChanged("ScheduleText");
            }
        }

        [DataMember(Order = 12)]
        public string RecurrenceRuleId
        {
            get { return _recurrenceRuleId ?? string.Empty; }
            set
            {
                var nextValue = value ?? string.Empty;
                if (string.Equals(_recurrenceRuleId, nextValue, StringComparison.Ordinal))
                {
                    return;
                }

                _recurrenceRuleId = nextValue;
                OnPropertyChanged("RecurrenceRuleId");
            }
        }

        public string ScheduleText
        {
            get
            {
                var timeText = TaskTime;
                if (!IsRecurringInstance || string.Equals(RecurrenceMode, "none", StringComparison.Ordinal))
                {
                    return timeText;
                }

                if (string.Equals(RecurrenceMode, "daily", StringComparison.Ordinal))
                {
                    return string.IsNullOrWhiteSpace(timeText) ? "매일" : "매일 " + timeText;
                }

                if (string.Equals(RecurrenceMode, "weekly", StringComparison.Ordinal))
                {
                    var weeklyLabel = BuildWeeklyLabel(RecurrenceWeekdayMask);
                    return string.IsNullOrWhiteSpace(timeText) ? weeklyLabel : weeklyLabel + " " + timeText;
                }

                return timeText;
            }
        }

        public bool IsEditing
        {
            get { return _isEditing; }
            set
            {
                if (_isEditing == value)
                {
                    return;
                }

                _isEditing = value;
                OnPropertyChanged("IsEditing");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            var handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        private static string BuildWeeklyLabel(int weekdayMask)
        {
            var labels = new List<string>();
            if ((weekdayMask & 1) != 0) labels.Add("월");
            if ((weekdayMask & 2) != 0) labels.Add("화");
            if ((weekdayMask & 4) != 0) labels.Add("수");
            if ((weekdayMask & 8) != 0) labels.Add("목");
            if ((weekdayMask & 16) != 0) labels.Add("금");
            if ((weekdayMask & 32) != 0) labels.Add("토");
            if ((weekdayMask & 64) != 0) labels.Add("일");

            if (labels.Count == 0)
            {
                return "매주";
            }

            return string.Join(",", labels);
        }
    }
}
