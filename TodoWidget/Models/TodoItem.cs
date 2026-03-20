using System;
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

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            var handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
