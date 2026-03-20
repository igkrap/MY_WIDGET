using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using TodoWidget.Models;
using TodoWidget.Services;

namespace TodoWidget
{
    public partial class MainWindow : Window
    {
        private const double DefaultWindowOpacity = 0.96;
        private const double MinimumWindowOpacity = 0.55;

        private readonly ObservableCollection<TodoItem> _allTodos;
        private readonly ObservableCollection<TodoItem> _visibleTodos;
        private readonly TodoStore _todoStore;
        private readonly SettingsStore _settingsStore;
        private readonly StartupManager _startupManager;
        private readonly DispatcherTimer _clockTimer;
        private readonly CultureInfo _koreanCulture;

        private Forms.NotifyIcon _notifyIcon;
        private Forms.ToolStripMenuItem _trayTopmostMenuItem;
        private Forms.ToolStripMenuItem _trayStartupMenuItem;

        private DateTime _selectedDate;
        private DateTime _displayedMonth;
        private bool _isInitializing;
        private bool _hasShownTrayHint;

        public MainWindow()
        {
            _isInitializing = true;

            InitializeComponent();

            _koreanCulture = new CultureInfo("ko-KR");
            _todoStore = new TodoStore();
            _settingsStore = new SettingsStore();
            _startupManager = new StartupManager();
            _allTodos = new ObservableCollection<TodoItem>(_todoStore.Load());
            _visibleTodos = new ObservableCollection<TodoItem>();
            _clockTimer = new DispatcherTimer();

            TodoItemsControl.ItemsSource = _visibleTodos;
            OpenDataButton.ToolTip = _todoStore.FilePath;

            _selectedDate = DateTime.Today;
            _displayedMonth = new DateTime(_selectedDate.Year, _selectedDate.Month, 1);

            HookCollection();
            InitializeTrayIcon();
            ApplySavedSettings();
            StartClock();
            RefreshAll();
            UpdatePinButtonState();
            UpdateStartupState();
            UpdateOpacityState();
            UpdateWidgetClip();

            _isInitializing = false;
        }

        private void HookCollection()
        {
            _allTodos.CollectionChanged += AllTodosCollectionChanged;

            foreach (var item in _allTodos)
            {
                item.PropertyChanged += TodoItemPropertyChanged;
            }
        }

        private void AllTodosCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (TodoItem item in e.NewItems)
                {
                    item.PropertyChanged += TodoItemPropertyChanged;
                }
            }

            if (e.OldItems != null)
            {
                foreach (TodoItem item in e.OldItems)
                {
                    item.PropertyChanged -= TodoItemPropertyChanged;
                }
            }
        }

        private void TodoItemPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            SaveTodos();
            RefreshVisibleTodos();
            RenderCalendar();
        }

        private void InitializeTrayIcon()
        {
            _notifyIcon = new Forms.NotifyIcon();
            _notifyIcon.Text = "TodoWidget";
            _notifyIcon.Icon = Drawing.SystemIcons.Application;
            _notifyIcon.Visible = true;
            _notifyIcon.DoubleClick += NotifyIconDoubleClick;

            var openMenuItem = new Forms.ToolStripMenuItem("Open");
            openMenuItem.Click += OpenTrayMenuItemOnClick;

            _trayTopmostMenuItem = new Forms.ToolStripMenuItem("Always on top");
            _trayTopmostMenuItem.Click += TopmostTrayMenuItemOnClick;

            _trayStartupMenuItem = new Forms.ToolStripMenuItem("Run at startup");
            _trayStartupMenuItem.Click += StartupTrayMenuItemOnClick;

            var exitMenuItem = new Forms.ToolStripMenuItem("Exit");
            exitMenuItem.Click += ExitTrayMenuItemOnClick;

            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add(openMenuItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(_trayTopmostMenuItem);
            menu.Items.Add(_trayStartupMenuItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(exitMenuItem);

            _notifyIcon.ContextMenuStrip = menu;
        }

        private void ApplySavedSettings()
        {
            var settings = _settingsStore.Load();
            var safeOpacity = ClampOpacity(settings.Opacity);

            Topmost = settings.IsTopmost;
            Opacity = safeOpacity;
            OpacitySlider.Value = safeOpacity;
        }

        private void StartClock()
        {
            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += ClockTimerTick;
            _clockTimer.Start();
            UpdateClock();
        }

        private void ClockTimerTick(object sender, EventArgs e)
        {
            UpdateClock();
        }

        private void UpdateClock()
        {
            var now = DateTime.Now;
            TimeTextBlock.Text = now.ToString("HH:mm");
            SecondsTextBlock.Text = now.ToString("ss");
            DateTextBlock.Text = now.ToString("yyyy.MM.dd dddd", _koreanCulture);
        }

        private void RefreshAll()
        {
            UpdateInputPlaceholder();
            RefreshVisibleTodos();
            RenderCalendar();
        }

        private void UpdateHeader()
        {
            CalendarMonthTextBlock.Text = _displayedMonth.ToString("yyyy.MM", _koreanCulture);
            SelectedDateTitleTextBlock.Text = _selectedDate.ToString("MM.dd dddd", _koreanCulture);

            var selectedItems = _allTodos.Where(IsSelectedDateTodo).ToList();
            var completedCount = selectedItems.Count(item => item.IsCompleted);

            TaskSummaryTextBlock.Text = selectedItems.Count.ToString(_koreanCulture) + " tasks / " + completedCount.ToString(_koreanCulture) + " done";
        }

        private void RefreshVisibleTodos()
        {
            var orderedItems = _allTodos
                .Where(IsSelectedDateTodo)
                .OrderBy(item => item.IsCompleted)
                .ThenBy(item => item.Title)
                .ToList();

            _visibleTodos.Clear();

            foreach (var item in orderedItems)
            {
                _visibleTodos.Add(item);
            }

            EmptyStateTextBlock.Visibility = _visibleTodos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateHeader();
        }

        private bool IsSelectedDateTodo(TodoItem item)
        {
            return item.TaskDate.Date == _selectedDate.Date;
        }

        private void RenderCalendar()
        {
            CalendarMonthTextBlock.Text = _displayedMonth.ToString("yyyy.MM", _koreanCulture);
            CalendarDaysGrid.Children.Clear();

            var monthStart = new DateTime(_displayedMonth.Year, _displayedMonth.Month, 1);
            var gridStart = monthStart.AddDays(-(int)monthStart.DayOfWeek);

            for (var index = 0; index < 42; index++)
            {
                var day = gridStart.AddDays(index);
                CalendarDaysGrid.Children.Add(CreateDayButton(day));
            }
        }

        private Button CreateDayButton(DateTime day)
        {
            var button = new Button();
            button.Style = (Style)FindResource("CalendarDayButtonStyle");
            button.Tag = day;

            var isInDisplayedMonth = day.Month == _displayedMonth.Month;
            var isToday = day.Date == DateTime.Today;
            var isSelected = day.Date == _selectedDate.Date;
            var hasTodos = _allTodos.Any(item => item.TaskDate.Date == day.Date);

            var dayNumber = new TextBlock();
            dayNumber.Text = day.Day.ToString(_koreanCulture);
            dayNumber.HorizontalAlignment = HorizontalAlignment.Center;
            dayNumber.FontSize = 13;
            dayNumber.FontWeight = FontWeights.SemiBold;
            dayNumber.Foreground = isInDisplayedMonth
                ? new SolidColorBrush(Color.FromRgb(247, 251, 255))
                : new SolidColorBrush(Color.FromArgb(96, 247, 251, 255));

            var marker = new Ellipse();
            marker.Width = 6;
            marker.Height = 6;
            marker.HorizontalAlignment = HorizontalAlignment.Center;
            marker.Margin = new Thickness(0, 4, 0, 0);
            marker.Fill = hasTodos
                ? new SolidColorBrush(Color.FromRgb(99, 215, 176))
                : Brushes.Transparent;

            var stackPanel = new StackPanel();
            stackPanel.VerticalAlignment = VerticalAlignment.Center;
            stackPanel.HorizontalAlignment = HorizontalAlignment.Center;
            stackPanel.Margin = new Thickness(0, 4, 0, 4);
            stackPanel.Children.Add(dayNumber);
            stackPanel.Children.Add(marker);

            button.Content = stackPanel;

            if (isSelected)
            {
                button.Background = new SolidColorBrush(Color.FromRgb(99, 215, 176));
                button.BorderBrush = new SolidColorBrush(Color.FromArgb(84, 255, 255, 255));
                button.BorderThickness = new Thickness(1);
                dayNumber.Foreground = new SolidColorBrush(Color.FromRgb(8, 19, 15));
                marker.Fill = new SolidColorBrush(Color.FromArgb(100, 8, 19, 15));
            }
            else if (isToday)
            {
                button.Background = new SolidColorBrush(Color.FromArgb(42, 255, 168, 106));
                button.BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
                button.BorderThickness = new Thickness(1);
            }
            else if (!isInDisplayedMonth)
            {
                button.Background = new SolidColorBrush(Color.FromArgb(8, 255, 255, 255));
            }

            button.Click += CalendarDayButtonOnClick;
            return button;
        }

        private void CalendarDayButtonOnClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null)
            {
                return;
            }

            var date = (DateTime)button.Tag;
            _selectedDate = date.Date;
            _displayedMonth = new DateTime(_selectedDate.Year, _selectedDate.Month, 1);

            RefreshAll();
        }

        private void AddTodoButtonOnClick(object sender, RoutedEventArgs e)
        {
            AddTodo();
        }

        private void AddTodo()
        {
            var title = TodoInputTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(title))
            {
                TodoInputTextBox.Focus();
                return;
            }

            _allTodos.Add(new TodoItem
            {
                Id = Guid.NewGuid(),
                Title = title,
                IsCompleted = false,
                TaskDate = _selectedDate.Date
            });

            TodoInputTextBox.Clear();
            SaveTodos();
            RefreshAll();
            TodoInputTextBox.Focus();
        }

        private void DeleteTodoButtonOnClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null)
            {
                return;
            }

            var todoId = (Guid)button.Tag;
            var item = _allTodos.FirstOrDefault(todo => todo.Id == todoId);

            if (item == null)
            {
                return;
            }

            _allTodos.Remove(item);
            SaveTodos();
            RefreshAll();
        }

        private void ClearCompletedButtonOnClick(object sender, RoutedEventArgs e)
        {
            var completedItems = _allTodos
                .Where(item => item.TaskDate.Date == _selectedDate.Date && item.IsCompleted)
                .ToList();

            foreach (var item in completedItems)
            {
                _allTodos.Remove(item);
            }

            SaveTodos();
            RefreshAll();
        }

        private void TodoInputTextBoxOnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddTodo();
                e.Handled = true;
            }
        }

        private void TodoInputTextBoxOnTextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateInputPlaceholder();
        }

        private void UpdateInputPlaceholder()
        {
            InputPlaceholderTextBlock.Visibility = string.IsNullOrWhiteSpace(TodoInputTextBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void PreviousMonthButtonOnClick(object sender, RoutedEventArgs e)
        {
            ChangeMonth(-1);
        }

        private void NextMonthButtonOnClick(object sender, RoutedEventArgs e)
        {
            ChangeMonth(1);
        }

        private void TodayButtonOnClick(object sender, RoutedEventArgs e)
        {
            _selectedDate = DateTime.Today;
            _displayedMonth = new DateTime(_selectedDate.Year, _selectedDate.Month, 1);
            RefreshAll();
        }

        private void ChangeMonth(int monthOffset)
        {
            var nextMonth = _displayedMonth.AddMonths(monthOffset);
            var day = Math.Min(_selectedDate.Day, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month));

            _displayedMonth = new DateTime(nextMonth.Year, nextMonth.Month, 1);
            _selectedDate = new DateTime(nextMonth.Year, nextMonth.Month, day);

            RefreshAll();
        }

        private void SaveTodos()
        {
            _todoStore.Save(_allTodos);
        }

        private void SaveSettings()
        {
            var settings = new WidgetSettings();
            settings.Opacity = ClampOpacity(Opacity);
            settings.IsTopmost = Topmost;

            _settingsStore.Save(settings);
        }
        private void UpdatePinButtonState()
        {
            PinButton.Content = Topmost ? "Pinned" : "Pin";
            PinButton.Background = Topmost
                ? new SolidColorBrush(Color.FromArgb(54, 99, 215, 176))
                : new SolidColorBrush(Color.FromArgb(24, 255, 255, 255));

            if (_trayTopmostMenuItem != null)
            {
                _trayTopmostMenuItem.Checked = Topmost;
            }
        }

        private void UpdateStartupState()
        {
            var isStartupEnabled = _startupManager.IsEnabled();
            StartupButton.Content = isStartupEnabled ? "Auto On" : "Auto Off";
            StartupButton.Background = isStartupEnabled
                ? new SolidColorBrush(Color.FromArgb(38, 99, 215, 176))
                : new SolidColorBrush(Color.FromArgb(24, 255, 255, 255));

            if (_trayStartupMenuItem != null)
            {
                _trayStartupMenuItem.Checked = isStartupEnabled;
            }
        }

        private void UpdateOpacityState()
        {
            if (OpacityValueTextBlock == null)
            {
                return;
            }

            var culture = _koreanCulture ?? CultureInfo.InvariantCulture;
            OpacityValueTextBlock.Text = ((int)Math.Round(Opacity * 100.0, MidpointRounding.AwayFromZero)).ToString(culture) + "%";
        }

        private void SetTopmost(bool isTopmost)
        {
            Topmost = isTopmost;
            UpdatePinButtonState();
            SaveSettings();
        }

        private void ToggleStartup()
        {
            var nextValue = !_startupManager.IsEnabled();

            if (!_startupManager.SetEnabled(nextValue))
            {
                MessageBox.Show("Could not update the startup setting.", "TodoWidget", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            UpdateStartupState();
        }

        private void WindowLoaded(object sender, RoutedEventArgs e)
        {
            PositionWindow();
        }

        private void PositionWindow()
        {
            var workArea = SystemParameters.WorkArea;
            Left = Math.Max(workArea.Left + 14, workArea.Right - ActualWidth - 22);
            Top = Math.Max(workArea.Top + 14, workArea.Bottom - ActualHeight - 22);
        }

        private void DragAreaMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void PinButtonOnClick(object sender, RoutedEventArgs e)
        {
            SetTopmost(!Topmost);
        }

        private void HideButtonOnClick(object sender, RoutedEventArgs e)
        {
            HideToTray(true);
        }

        private void HideToTray(bool showBalloonTip)
        {
            Hide();

            if (_notifyIcon != null && showBalloonTip && !_hasShownTrayHint)
            {
                _notifyIcon.ShowBalloonTip(1500, "TodoWidget", "The widget is still running in the tray.", Forms.ToolTipIcon.Info);
                _hasShownTrayHint = true;
            }
        }

        private void RestoreFromTray()
        {
            if (!IsVisible)
            {
                Show();
            }

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
            Focus();
        }

        private void NotifyIconDoubleClick(object sender, EventArgs e)
        {
            RestoreFromTray();
        }

        private void OpenTrayMenuItemOnClick(object sender, EventArgs e)
        {
            RestoreFromTray();
        }

        private void TopmostTrayMenuItemOnClick(object sender, EventArgs e)
        {
            SetTopmost(!Topmost);
        }

        private void StartupTrayMenuItemOnClick(object sender, EventArgs e)
        {
            ToggleStartup();
        }

        private void ExitTrayMenuItemOnClick(object sender, EventArgs e)
        {
            Close();
        }

        private void StartupButtonOnClick(object sender, RoutedEventArgs e)
        {
            ToggleStartup();
        }

        private void OpenDataButtonOnClick(object sender, RoutedEventArgs e)
        {
            if (System.IO.File.Exists(_todoStore.FilePath))
            {
                Process.Start("explorer.exe", "/select,\"" + _todoStore.FilePath + "\"");
                return;
            }

            Process.Start("explorer.exe", "\"" + _todoStore.DirectoryPath + "\"");
        }

        private void OpacitySliderOnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var nextOpacity = ClampOpacity(e.NewValue);
            Opacity = nextOpacity;
            UpdateOpacityState();

            if (_isInitializing)
            {
                return;
            }

            SaveSettings();
        }

        private static double ClampOpacity(double value)
        {
            if (value < MinimumWindowOpacity)
            {
                return MinimumWindowOpacity;
            }

            if (value > 1.0)
            {
                return 1.0;
            }

            return value <= 0.0 ? DefaultWindowOpacity : value;
        }

        private void WindowStateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                HideToTray(false);
            }
        }

        private void WindowClosing(object sender, CancelEventArgs e)
        {
            SaveSettings();
            DisposeTrayIcon();
        }

        private void DisposeTrayIcon()
        {
            if (_notifyIcon == null)
            {
                return;
            }

            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        private void WidgetChromeSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateWidgetClip();
        }

        private void UpdateWidgetClip()
        {
            WidgetChrome.Clip = new RectangleGeometry(new Rect(0, 0, WidgetChrome.ActualWidth, WidgetChrome.ActualHeight), 28, 28);
        }
    }
}
