using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
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
        private enum RecurrenceMode
        {
            None,
            Daily,
            Weekly
        }

        private const double DefaultWindowOpacity = 0.96;
        private const double MinimumWindowOpacity = 0.55;
        private static readonly IntPtr HwndTopmost = new IntPtr(-1);
        private static readonly IntPtr HwndNotTopmost = new IntPtr(-2);
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private const uint SwpNoSendChanging = 0x0400;
        private const int WmHotKey = 0x0312;
        private const int HotKeyToggleVisibilityId = 0x7001;
        private const uint ModShift = 0x0004;
        private const uint ModWin = 0x0008;
        private const uint VkQ = 0x51;
        private const string DefaultTelegramBotToken = "";
        private const string DefaultTelegramBotUsername = "";
        private static readonly object TelegramLinkCodeLock = new object();
        private static readonly Dictionary<string, DateTime> RecentTelegramLinkCodes = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        private readonly ObservableCollection<TodoItem> _allTodos;
        private readonly ObservableCollection<TodoItem> _visibleTodos;
        private readonly ObservableCollection<TodoStore.RecurrenceRuleEntry> _recurrenceRules;
        private readonly TodoStore _todoStore;
        private readonly SettingsStore _settingsStore;
        private readonly StartupManager _startupManager;
        private readonly DispatcherTimer _clockTimer;
        private readonly CultureInfo _koreanCulture;
        private static readonly HttpClient TelegramHttpClient = new HttpClient();

        private Forms.NotifyIcon _notifyIcon;
        private Forms.ToolStripMenuItem _trayTopmostMenuItem;
        private Forms.ToolStripMenuItem _trayStartupMenuItem;

        private DateTime _selectedDate;
        private DateTime _displayedMonth;
        private DateTime _lastObservedDate;
        private bool _isInitializing;
        private bool _isTaskPanelExpanded;
        private bool _isBulkTodoUpdate;
        private bool _isReminderEnabledByDefault = true;
        private bool _isRepeatOptionsVisible;
        private DateTime _lastReminderCheckAt;
        private bool? _recurrencePopupWasOpenOnTogglePress;
        private bool _isTelegramEnabled;
        private string _telegramBotToken;
        private string _telegramChatId;
        private string _pendingTelegramLinkCode;
        private DateTime _pendingTelegramLinkIssuedAtUtc;
        private DateTime _lastTelegramCommandPollAtUtc;
        private bool _isTelegramCommandPollingInitialized;
        private long _telegramNextUpdateId;
        private RecurrenceMode _selectedRecurrenceMode = RecurrenceMode.None;
        private int _selectedWeekdayMask;
        private Point _dragStartPoint;
        private TodoItem _draggedTodoItem;
        private HwndSource _windowSource;

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
            _recurrenceRules = new ObservableCollection<TodoStore.RecurrenceRuleEntry>();
            _clockTimer = new DispatcherTimer();

            EnsureSortOrders();

            TodoItemsControl.ItemsSource = _visibleTodos;
            RecurrenceRulesItemsControl.ItemsSource = _recurrenceRules;
            OpenDataButton.ToolTip = _todoStore.FilePath;

            _selectedDate = DateTime.Today;
            _displayedMonth = new DateTime(_selectedDate.Year, _selectedDate.Month, 1);
            _lastObservedDate = DateTime.Today;
            _isTaskPanelExpanded = false;
            _selectedWeekdayMask = GetWeekdayBit(_selectedDate.DayOfWeek);

            HookCollection();
            InitializeTrayIcon();
            ApplySavedSettings();
            StartClock();
            RefreshAll();
            UpdatePinButtonState();
            UpdateStartupState();
            UpdateOpacityState();
            UpdateTaskPanelState();
            UpdateReminderToggleState();
            UpdateRepeatUiState();
            UpdateRecurrenceRulesPanelState();

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
            if (_isBulkTodoUpdate)
            {
                return;
            }

            SaveTodos();
            RefreshVisibleTodos();
            RenderCalendar();
        }

        private void InitializeTrayIcon()
        {
            _notifyIcon = new Forms.NotifyIcon();
            _notifyIcon.Text = "TodoWidget";
            _notifyIcon.Icon = GetApplicationIcon();
            _notifyIcon.Visible = true;
            _notifyIcon.DoubleClick += NotifyIconDoubleClick;

            var openMenuItem = new Forms.ToolStripMenuItem("Open");
            openMenuItem.Click += OpenTrayMenuItemOnClick;

            var exportMenuItem = new Forms.ToolStripMenuItem("Export Report");
            exportMenuItem.Click += ExportReportTrayMenuItemOnClick;

            var telegramSettingsMenuItem = new Forms.ToolStripMenuItem("Telegram Settings...");
            telegramSettingsMenuItem.Click += TelegramSettingsTrayMenuItemOnClick;

            _trayTopmostMenuItem = new Forms.ToolStripMenuItem("Always on top");
            _trayTopmostMenuItem.Click += TopmostTrayMenuItemOnClick;

            _trayStartupMenuItem = new Forms.ToolStripMenuItem("Run at startup");
            _trayStartupMenuItem.Click += StartupTrayMenuItemOnClick;

            var exitMenuItem = new Forms.ToolStripMenuItem("Exit");
            exitMenuItem.Click += ExitTrayMenuItemOnClick;

            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add(openMenuItem);
            menu.Items.Add(exportMenuItem);
            menu.Items.Add(telegramSettingsMenuItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(_trayTopmostMenuItem);
            menu.Items.Add(_trayStartupMenuItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(exitMenuItem);

            _notifyIcon.ContextMenuStrip = menu;
        }

        private static Drawing.Icon GetApplicationIcon()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule != null
                    ? Process.GetCurrentProcess().MainModule.FileName
                    : string.Empty;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    var icon = Drawing.Icon.ExtractAssociatedIcon(exePath);
                    if (icon != null)
                    {
                        return icon;
                    }
                }
            }
            catch
            {
                // Fallback below.
            }

            return Drawing.SystemIcons.Application;
        }

        private void ApplySavedSettings()
        {
            var settings = _settingsStore.Load();
            var safeOpacity = ClampOpacity(settings.Opacity);

            Topmost = settings.IsTopmost;
            Opacity = safeOpacity;
            OpacitySlider.Value = safeOpacity;
            _isTelegramEnabled = settings.TelegramEnabled;
            _telegramBotToken = !string.IsNullOrWhiteSpace(DefaultTelegramBotToken)
                ? DefaultTelegramBotToken
                : (settings.TelegramBotToken ?? string.Empty);
            _telegramChatId = settings.TelegramChatId ?? string.Empty;
            SyncTaskPanelVisualState();
        }

        private void StartClock()
        {
            _lastReminderCheckAt = DateTime.Now;
            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += ClockTimerTick;
            _clockTimer.Start();
            UpdateClock();
        }

        private void ClockTimerTick(object sender, EventArgs e)
        {
            UpdateClock();
            SyncCalendarSelectionWithCurrentDate();
            CheckDueReminders();
            CheckTelegramCommands();
        }

        private void UpdateClock()
        {
            var now = DateTime.Now;
            TimeTextBlock.Text = now.ToString("HH:mm");
            SecondsTextBlock.Text = now.ToString("ss");
            DateTextBlock.Text = now.ToString("yyyy.MM.dd dddd", _koreanCulture);
        }

        private void SyncCalendarSelectionWithCurrentDate()
        {
            var today = DateTime.Today;
            if (today == _lastObservedDate.Date)
            {
                return;
            }

            _lastObservedDate = today;
            _selectedDate = today;
            _displayedMonth = new DateTime(today.Year, today.Month, 1);
            _selectedWeekdayMask = GetWeekdayBit(today.DayOfWeek);
            RefreshAll();
        }

        private void RefreshAll()
        {
            NormalizeReminderStateForTimelessTodos();
            UpdateInputPlaceholder();
            RefreshRecurrenceRules();
            RefreshVisibleTodos();
            RenderCalendar();
        }

        private void NormalizeReminderStateForTimelessTodos()
        {
            var changed = false;
            RunBulkTodoUpdate(() =>
            {
                foreach (var item in _allTodos)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(item.TaskTime) || !item.ReminderEnabled)
                    {
                        continue;
                    }

                    item.ReminderEnabled = false;
                    changed = true;
                }
            });

            if (changed)
            {
                SaveTodos();
            }
        }

        private void UpdateHeader()
        {
            CalendarMonthTextBlock.Text = _displayedMonth.ToString("yyyy.MM", _koreanCulture);
            SelectedDateTitleTextBlock.Text = _selectedDate.ToString("MM.dd dddd", _koreanCulture);

            var selectedItems = _allTodos.Where(IsSelectedDateTodo).ToList();
            var completedCount = selectedItems.Count(item => item.IsCompleted);

            TaskSummaryTextBlock.Text = selectedItems.Count.ToString(_koreanCulture) + " tasks / " + completedCount.ToString(_koreanCulture) + " done";
        }

        private void UpdateTaskPanelState()
        {
            if (ToggleTasksButton == null || TasksPanelPopup == null)
            {
                return;
            }

            ToggleTasksButton.Content = _isTaskPanelExpanded ? ">" : "<";
            TasksPanelPopup.IsOpen = _isTaskPanelExpanded;
            SyncTaskPanelVisualState();

            if (_isTaskPanelExpanded)
            {
                Dispatcher.BeginInvoke(new Action(ApplyTaskPanelTopmostState), DispatcherPriority.Loaded);
            }
        }

        private void SyncTaskPanelVisualState()
        {
            if (TasksPanelBorder != null)
            {
                TasksPanelBorder.Opacity = Opacity;
            }
            ApplyRepeatOptionsPanelVisualState();
            ApplyRecurrenceRulesPanelVisualState();

            ApplyTaskPanelTopmostState();
        }

        private void ApplyRepeatOptionsPanelVisualState()
        {
            if (RepeatOptionsPanelBorder == null)
            {
                return;
            }

            var targetOpacity = TasksPanelBorder != null ? TasksPanelBorder.Opacity : Opacity;
            RepeatOptionsPanelBorder.Opacity = targetOpacity;
        }

        private void ApplyRecurrenceRulesPanelVisualState()
        {
            if (RecurrenceRulesPanelBorder == null)
            {
                return;
            }

            var targetOpacity = TasksPanelBorder != null ? TasksPanelBorder.Opacity : Opacity;
            RecurrenceRulesPanelBorder.Opacity = targetOpacity;
        }

        private void ApplyTaskPanelTopmostState()
        {
            if (TasksPanelPopup == null || !TasksPanelPopup.IsOpen || TasksPanelBorder == null)
            {
                return;
            }

            var source = PresentationSource.FromVisual(TasksPanelBorder) as HwndSource;
            if (source == null || source.Handle == IntPtr.Zero)
            {
                return;
            }

            SetWindowPos(
                source.Handle,
                Topmost ? HwndTopmost : HwndNotTopmost,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging);
        }

        private void TasksPanelPopupOnOpened(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ApplyTaskPanelTopmostState), DispatcherPriority.Loaded);
            Dispatcher.BeginInvoke(new Action(ApplyTaskPanelTopmostState), DispatcherPriority.ContextIdle);
        }

        private void EnsureSortOrders()
        {
            var dates = _allTodos
                .Select(item => item.TaskDate.Date)
                .Distinct()
                .ToList();

            var changed = false;
            foreach (var date in dates)
            {
                if (EnsureSortOrderForDate(date))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                SaveTodos();
            }
        }

        private bool EnsureSortOrderForDate(DateTime date)
        {
            var dateItems = _allTodos
                .Where(item => item.TaskDate.Date == date.Date)
                .ToList();

            if (dateItems.Count == 0)
            {
                return false;
            }

            var hasUniqueOrders = dateItems
                .GroupBy(item => item.IsRecurringInstance + "|" + item.SortOrder.ToString(CultureInfo.InvariantCulture))
                .All(group => group.Count() == 1);
            var needsInitialization = !hasUniqueOrders || dateItems.All(item => item.SortOrder == 0);

            if (!needsInitialization)
            {
                return false;
            }

            var ordered = dateItems
                .OrderBy(item => string.IsNullOrWhiteSpace(item.TaskTime) ? 1 : 0)
                .ThenBy(item => item.TaskTime)
                .ThenBy(item => item.Title)
                .ToList();

            RunBulkTodoUpdate(() =>
            {
                for (var index = 0; index < ordered.Count; index++)
                {
                    ordered[index].SortOrder = index;
                }
            });

            return true;
        }

        private void RunBulkTodoUpdate(Action action)
        {
            _isBulkTodoUpdate = true;
            try
            {
                action();
            }
            finally
            {
                _isBulkTodoUpdate = false;
            }
        }

        private void ResequenceDateTodos(DateTime date, System.Collections.Generic.IList<TodoItem> orderedItems)
        {
            RunBulkTodoUpdate(() =>
            {
                for (var index = 0; index < orderedItems.Count; index++)
                {
                    orderedItems[index].SortOrder = index;
                }
            });
        }

        private static int CompareByTimeThenTitle(TodoItem left, TodoItem right)
        {
            var leftHasTime = !string.IsNullOrWhiteSpace(left.TaskTime);
            var rightHasTime = !string.IsNullOrWhiteSpace(right.TaskTime);

            if (leftHasTime != rightHasTime)
            {
                return leftHasTime ? -1 : 1;
            }

            var timeCompare = string.Compare(left.TaskTime, right.TaskTime, StringComparison.Ordinal);
            if (timeCompare != 0)
            {
                return timeCompare;
            }

            return string.Compare(left.Title, right.Title, StringComparison.CurrentCultureIgnoreCase);
        }
        private void RefreshVisibleTodos()
        {
            EnsureRecurringInstancesLoadedForDate(_selectedDate.Date);

            var orderedItems = _allTodos
                .Where(IsSelectedDateTodo)
                .OrderBy(item => item.SortOrder)
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

        private void EnsureRecurringInstancesLoadedForDate(DateTime date)
        {
            var generatedItems = _todoStore.EnsureRecurringInstancesForDate(date.Date);
            if (generatedItems == null || generatedItems.Count == 0)
            {
                return;
            }

            foreach (var generatedItem in generatedItems)
            {
                if (_allTodos.All(existing => existing.Id != generatedItem.Id))
                {
                    _allTodos.Add(generatedItem);
                }
            }
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
            var gridEnd = gridStart.AddDays(41);
            var recurringDates = _todoStore.GetActiveRecurrenceDatesInRange(gridStart, gridEnd);

            for (var index = 0; index < 42; index++)
            {
                var day = gridStart.AddDays(index);
                CalendarDaysGrid.Children.Add(CreateDayButton(day, recurringDates.Contains(day.Date)));
            }
        }

        private Button CreateDayButton(DateTime day, bool hasRecurringRuleOnDay)
        {
            var button = new Button();
            button.Style = (Style)FindResource("CalendarDayButtonStyle");
            button.Tag = day;

            var isInDisplayedMonth = day.Month == _displayedMonth.Month;
            var isToday = day.Date == DateTime.Today;
            var isSelected = day.Date == _selectedDate.Date;
            var dayTodos = _allTodos.Where(item => item.TaskDate.Date == day.Date).ToList();
            var hasPersistedTodos = dayTodos.Count > 0;
            var hasTodos = hasPersistedTodos || hasRecurringRuleOnDay;
            var allCompleted = hasPersistedTodos && dayTodos.All(item => item.IsCompleted);

            var dayNumber = new TextBlock();
            dayNumber.Text = day.Day.ToString(_koreanCulture);
            dayNumber.HorizontalAlignment = HorizontalAlignment.Center;
            dayNumber.FontSize = 13;
            dayNumber.FontWeight = FontWeights.SemiBold;
            dayNumber.Foreground = isInDisplayedMonth
                ? new SolidColorBrush(Color.FromRgb(247, 251, 255))
                : new SolidColorBrush(Color.FromArgb(96, 247, 251, 255));

            var marker = new Ellipse();
            marker.Width = 5;
            marker.Height = 5;
            marker.HorizontalAlignment = HorizontalAlignment.Center;
            marker.Margin = new Thickness(0, 3, 0, 0);
            if (!hasTodos)
            {
                marker.Fill = Brushes.Transparent;
            }
            else if (allCompleted)
            {
                marker.Fill = new SolidColorBrush(Color.FromRgb(112, 163, 135));
            }
            else
            {
                marker.Fill = new SolidColorBrush(Color.FromRgb(255, 205, 87));
            }

            var stackPanel = new StackPanel();
            stackPanel.VerticalAlignment = VerticalAlignment.Center;
            stackPanel.HorizontalAlignment = HorizontalAlignment.Center;
            stackPanel.Margin = new Thickness(0, 2, 0, 2);
            stackPanel.Children.Add(dayNumber);
            stackPanel.Children.Add(marker);

            button.Content = stackPanel;

            if (isSelected)
            {
                button.Background = new SolidColorBrush(Color.FromRgb(112, 163, 135));
                button.BorderBrush = new SolidColorBrush(Color.FromArgb(84, 255, 255, 255));
                button.BorderThickness = new Thickness(1);
                dayNumber.Foreground = new SolidColorBrush(Color.FromRgb(243, 247, 252));
                marker.Fill = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
            }
            else if (isToday)
            {
                button.Background = new SolidColorBrush(Color.FromArgb(38, 112, 163, 135));
                button.BorderBrush = new SolidColorBrush(Color.FromArgb(78, 143, 191, 166));
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
            var timeText = TodoTimeTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(title))
            {
                TodoInputTextBox.Focus();
                return;
            }

            var normalizedTime = string.Empty;
            if (!string.IsNullOrWhiteSpace(timeText))
            {
                string parsedTime;
                if (TryNormalizeTimeInput(timeText, out parsedTime))
                {
                    normalizedTime = parsedTime;
                }
            }

            var newItem = new TodoItem
            {
                Id = Guid.NewGuid(),
                Title = title,
                IsCompleted = false,
                TaskDate = _selectedDate.Date,
                TaskTime = normalizedTime,
                ReminderEnabled = _isReminderEnabledByDefault && !string.IsNullOrWhiteSpace(normalizedTime),
                ReminderTriggered = false,
                IsRecurringInstance = _selectedRecurrenceMode != RecurrenceMode.None,
                RecurrenceMode = GetRecurrenceModeValue(_selectedRecurrenceMode),
                RecurrenceWeekdayMask = _selectedRecurrenceMode == RecurrenceMode.Weekly ? _selectedWeekdayMask : 0,
                RecurrenceRuleId = _selectedRecurrenceMode == RecurrenceMode.None ? string.Empty : Guid.NewGuid().ToString("D")
            };

            _allTodos.Add(newItem);

            var selectedDateItems = _allTodos
                .Where(item => item.TaskDate.Date == _selectedDate.Date && item.Id != newItem.Id)
                .OrderBy(item => item.SortOrder)
                .ToList();

            var insertIndex = selectedDateItems.FindIndex(item => CompareByTimeThenTitle(newItem, item) < 0);
            if (insertIndex < 0)
            {
                insertIndex = selectedDateItems.Count;
            }

            selectedDateItems.Insert(insertIndex, newItem);
            ResequenceDateTodos(_selectedDate.Date, selectedDateItems);

            if (newItem.IsRecurringInstance)
            {
                _todoStore.SaveRecurrenceRule(newItem);
            }

            TodoInputTextBox.Clear();
            TodoTimeTextBox.Clear();
            _isRepeatOptionsVisible = false;
            SaveTodos();
            RefreshAll();
            TodoInputTextBox.Focus();
        }

        private static string GetRecurrenceModeValue(RecurrenceMode mode)
        {
            switch (mode)
            {
                case RecurrenceMode.Daily:
                    return "daily";
                case RecurrenceMode.Weekly:
                    return "weekly";
                default:
                    return "none";
            }
        }

        private bool TryNormalizeTimeInput(string input, out string normalizedTime)
        {
            normalizedTime = string.Empty;
            if (string.IsNullOrWhiteSpace(input))
            {
                return true;
            }

            if (!IsValidCompleteTimeText(input))
            {
                return false;
            }

            var hours = int.Parse(input.Substring(0, 2), CultureInfo.InvariantCulture);
            var minutes = int.Parse(input.Substring(2, 2), CultureInfo.InvariantCulture);
            normalizedTime = new DateTime(1, 1, 1, hours, minutes, 0).ToString("HH:mm", _koreanCulture);
            return true;
        }

        private void ReminderToggleButtonOnClick(object sender, RoutedEventArgs e)
        {
            _isReminderEnabledByDefault = !_isReminderEnabledByDefault;
            UpdateReminderToggleState();
        }

        private void RepeatModeButtonOnClick(object sender, RoutedEventArgs e)
        {
            _isRepeatOptionsVisible = !_isRepeatOptionsVisible;
            UpdateRepeatUiState();
        }

        private void RepeatNoneButtonOnClick(object sender, RoutedEventArgs e)
        {
            _selectedRecurrenceMode = RecurrenceMode.None;
            UpdateRepeatUiState();
        }

        private void RepeatDailyButtonOnClick(object sender, RoutedEventArgs e)
        {
            _selectedRecurrenceMode = RecurrenceMode.Daily;
            UpdateRepeatUiState();
        }

        private void RepeatWeeklyButtonOnClick(object sender, RoutedEventArgs e)
        {
            _selectedRecurrenceMode = RecurrenceMode.Weekly;
            if (_selectedWeekdayMask == 0)
            {
                _selectedWeekdayMask = GetWeekdayBit(_selectedDate.DayOfWeek);
            }

            UpdateRepeatUiState();
        }

        private void RepeatCloseButtonOnClick(object sender, RoutedEventArgs e)
        {
            _isRepeatOptionsVisible = false;
            UpdateRepeatUiState();
        }

        private void WeekdayToggleButtonOnClick(object sender, RoutedEventArgs e)
        {
            if (_selectedRecurrenceMode != RecurrenceMode.Weekly)
            {
                return;
            }

            var button = sender as System.Windows.Controls.Primitives.ToggleButton;
            if (button == null)
            {
                return;
            }

            int bitValue;
            if (!int.TryParse(button.Tag as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out bitValue))
            {
                return;
            }

            if (button.IsChecked == true)
            {
                _selectedWeekdayMask |= bitValue;
            }
            else
            {
                _selectedWeekdayMask &= ~bitValue;
            }

            if (_selectedWeekdayMask == 0)
            {
                _selectedWeekdayMask = bitValue;
            }

            UpdateRepeatUiState();
        }

        private void UpdateReminderToggleState()
        {
            if (ReminderToggleButton == null)
            {
                return;
            }

            ReminderToggleButton.Content = _isReminderEnabledByDefault ? "\uC54C\uB9BC On" : "\uC54C\uB9BC Off";
            ReminderToggleButton.Background = _isReminderEnabledByDefault
                ? new SolidColorBrush(Color.FromArgb(46, 112, 163, 135))
                : new SolidColorBrush(Color.FromArgb(31, 50, 61, 75));
            ReminderToggleButton.BorderBrush = _isReminderEnabledByDefault
                ? new SolidColorBrush(Color.FromArgb(96, 143, 191, 166))
                : new SolidColorBrush(Color.FromArgb(75, 85, 101, 121));
        }

        private void UpdateRepeatUiState()
        {
            if (RepeatModeButton == null || RepeatOptionsPanel == null || WeekdayButtonsHost == null)
            {
                return;
            }

            if (_selectedRecurrenceMode == RecurrenceMode.Weekly && _selectedWeekdayMask == 0)
            {
                _selectedWeekdayMask = GetWeekdayBit(_selectedDate.DayOfWeek);
            }

            RepeatModeButton.Content = _selectedRecurrenceMode == RecurrenceMode.None
                ? "\uBC18\uBCF5 \uC5C6\uC74C"
                : _selectedRecurrenceMode == RecurrenceMode.Daily
                    ? "\uB9E4\uC77C"
                    : "\uB9E4\uC8FC";

            RepeatModeButton.Background = _selectedRecurrenceMode == RecurrenceMode.None
                ? new SolidColorBrush(Color.FromArgb(31, 50, 61, 75))
                : new SolidColorBrush(Color.FromArgb(46, 112, 163, 135));
            RepeatModeButton.BorderBrush = _selectedRecurrenceMode == RecurrenceMode.None
                ? new SolidColorBrush(Color.FromArgb(75, 85, 101, 121))
                : new SolidColorBrush(Color.FromArgb(96, 143, 191, 166));

            RepeatOptionsPanel.IsOpen = _isRepeatOptionsVisible;
            ApplyRepeatOptionsPanelVisualState();

            ApplyRepeatOptionButtonVisual(RepeatNoneButton, _selectedRecurrenceMode == RecurrenceMode.None);
            ApplyRepeatOptionButtonVisual(RepeatDailyButton, _selectedRecurrenceMode == RecurrenceMode.Daily);
            ApplyRepeatOptionButtonVisual(RepeatWeeklyButton, _selectedRecurrenceMode == RecurrenceMode.Weekly);

            var isWeekly = _selectedRecurrenceMode == RecurrenceMode.Weekly;
            WeekdayButtonsHost.IsEnabled = isWeekly;
            WeekdayButtonsHost.Opacity = isWeekly ? 1.0 : 0.5;

            SetWeekdayButtonState(WeekdayMonButton, 1);
            SetWeekdayButtonState(WeekdayTueButton, 2);
            SetWeekdayButtonState(WeekdayWedButton, 4);
            SetWeekdayButtonState(WeekdayThuButton, 8);
            SetWeekdayButtonState(WeekdayFriButton, 16);
            SetWeekdayButtonState(WeekdaySatButton, 32);
            SetWeekdayButtonState(WeekdaySunButton, 64);
        }

        private void RefreshRecurrenceRules()
        {
            var rules = _todoStore.LoadRecurrenceRules();
            _recurrenceRules.Clear();
            foreach (var rule in rules)
            {
                _recurrenceRules.Add(rule);
            }

            UpdateRecurrenceRulesPanelState();
        }

        private void UpdateRecurrenceRulesPanelState()
        {
            if (RecurrenceRulesPopup == null || RecurrenceRulesToggleButton == null || EmptyRecurrenceRulesTextBlock == null)
            {
                return;
            }

            RecurrenceRulesToggleButton.Content = RecurrenceRulesPopup.IsOpen ? "\uB8E8\uD2F4 \uB2EB\uAE30" : "\uB8E8\uD2F4";
            EmptyRecurrenceRulesTextBlock.Visibility = _recurrenceRules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RecurrenceRulesToggleButtonOnClick(object sender, RoutedEventArgs e)
        {
            if (RecurrenceRulesPopup == null)
            {
                return;
            }

            var wasOpenOnPress = _recurrencePopupWasOpenOnTogglePress ?? RecurrenceRulesPopup.IsOpen;
            _recurrencePopupWasOpenOnTogglePress = null;

            if (!wasOpenOnPress)
            {
                RefreshRecurrenceRules();
                RecurrenceRulesPopup.IsOpen = true;
            }
            else
            {
                RecurrenceRulesPopup.IsOpen = false;
            }

            UpdateRecurrenceRulesPanelState();
        }

        private void RecurrenceRulesToggleButtonOnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (RecurrenceRulesPopup == null)
            {
                return;
            }

            _recurrencePopupWasOpenOnTogglePress = RecurrenceRulesPopup.IsOpen;
        }

        private void RecurrenceRulesPopupOnClosed(object sender, EventArgs e)
        {
            UpdateRecurrenceRulesPanelState();
        }

        private void RecurrenceRulesPopupOnOpened(object sender, EventArgs e)
        {
            ApplyRecurrenceRulesPanelVisualState();
            UpdateRecurrenceRulesPanelState();
        }

        private void ToggleRecurrenceRuleActiveButtonOnClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null)
            {
                return;
            }

            var ruleId = button.Tag as string;
            if (string.IsNullOrWhiteSpace(ruleId))
            {
                return;
            }

            var rule = _recurrenceRules.FirstOrDefault(entry => string.Equals(entry.Id, ruleId, StringComparison.OrdinalIgnoreCase));
            if (rule == null)
            {
                return;
            }

            var nextActive = !rule.IsActive;
            _todoStore.SetRecurrenceRuleActive(ruleId, nextActive);
            RefreshRecurrenceRules();
            RefreshAll();
        }

        private void DeleteRecurrenceRuleButtonOnClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null)
            {
                return;
            }

            var ruleId = button.Tag as string;
            if (string.IsNullOrWhiteSpace(ruleId))
            {
                return;
            }

            _todoStore.DeleteRecurrenceRule(ruleId);

            var toRemove = _allTodos
                .Where(item => string.Equals(item.RecurrenceRuleId, ruleId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var item in toRemove)
            {
                _allTodos.Remove(item);
            }

            SaveTodos();
            RefreshAll();
        }

        private void RepeatOptionsPanelOnClosed(object sender, EventArgs e)
        {
            _isRepeatOptionsVisible = false;
        }

        private void RepeatOptionsPanelOnOpened(object sender, EventArgs e)
        {
            ApplyRepeatOptionsPanelVisualState();
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

        private static void ApplyRepeatOptionButtonVisual(Button button, bool isActive)
        {
            if (button == null)
            {
                return;
            }

            button.Background = isActive
                ? new SolidColorBrush(Color.FromArgb(46, 112, 163, 135))
                : new SolidColorBrush(Color.FromArgb(31, 50, 61, 75));
            button.BorderBrush = isActive
                ? new SolidColorBrush(Color.FromArgb(96, 143, 191, 166))
                : new SolidColorBrush(Color.FromArgb(75, 85, 101, 121));
            button.Foreground = new SolidColorBrush(Color.FromRgb(216, 228, 243));
        }

        private void SetWeekdayButtonState(System.Windows.Controls.Primitives.ToggleButton button, int bitValue)
        {
            if (button == null)
            {
                return;
            }

            button.IsChecked = (_selectedWeekdayMask & bitValue) != 0;
        }

        private void CheckDueReminders()
        {
            var now = DateTime.Now;
            EnsureRecurringInstancesLoadedForDate(now.Date);

            var checkStart = _lastReminderCheckAt;
            if (checkStart > now)
            {
                checkStart = now;
            }

            var dueItems = _allTodos
                .Where(item =>
                    item.TaskDate.Date == now.Date &&
                    !item.IsCompleted &&
                    item.ReminderEnabled &&
                    !item.ReminderTriggered &&
                    IsReminderDue(item, checkStart, now))
                .ToList();

            _lastReminderCheckAt = now;

            if (dueItems.Count == 0)
            {
                return;
            }

            RunBulkTodoUpdate(() =>
            {
                foreach (var item in dueItems)
                {
                    item.ReminderTriggered = true;
                }
            });

            SaveTodos();

            foreach (var item in dueItems)
            {
                ShowReminderNotification(item);
            }
        }

        private static bool IsReminderDue(TodoItem item, DateTime checkStart, DateTime now)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.TaskTime))
            {
                return false;
            }

            DateTime dueTime;
            if (!DateTime.TryParseExact(
                    item.TaskTime,
                    "HH:mm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out dueTime))
            {
                return false;
            }

            var dueAt = new DateTime(item.TaskDate.Year, item.TaskDate.Month, item.TaskDate.Day, dueTime.Hour, dueTime.Minute, 0);
            return dueAt > checkStart && dueAt <= now;
        }

        private void ShowReminderNotification(TodoItem item)
        {
            if (item == null)
            {
                return;
            }

            var timeLabel = string.IsNullOrWhiteSpace(item.TaskTime) ? string.Empty : "[" + item.TaskTime + "] ";
            var message = timeLabel + item.Title;

            try
            {
                if (_notifyIcon != null)
                {
                    _notifyIcon.BalloonTipTitle = "Todo Reminder";
                    _notifyIcon.BalloonTipText = message;
                    _notifyIcon.ShowBalloonTip(3500);
                }
            }
            catch
            {
                // Keep reminders non-fatal.
            }

            try
            {
                SystemSounds.Exclamation.Play();
            }
            catch
            {
                // Ignore sound errors.
            }

            SendTelegramReminder(message);
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

            DeleteTodoItem(item);
        }

        private void ClearCompletedButtonOnClick(object sender, RoutedEventArgs e)
        {
            var completedItems = _allTodos
                .Where(item => item.TaskDate.Date == _selectedDate.Date && item.IsCompleted)
                .ToList();

            foreach (var item in completedItems.Where(item => item.IsRecurringInstance && !string.IsNullOrWhiteSpace(item.RecurrenceRuleId)))
            {
                _todoStore.AddRecurrenceSkip(item.RecurrenceRuleId, item.TaskDate.Date);
            }

            foreach (var item in completedItems)
            {
                _allTodos.Remove(item);
            }

            var remaining = _allTodos
                .Where(item => item.TaskDate.Date == _selectedDate.Date)
                .OrderBy(item => item.SortOrder)
                .ToList();
            ResequenceDateTodos(_selectedDate.Date, remaining);

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

        private void TodoTimeTextBoxOnTextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateInputPlaceholder();
        }

        private void TodoTimeTextBoxOnPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox == null || string.IsNullOrEmpty(e.Text))
            {
                return;
            }

            if (e.Text.Any(ch => !char.IsDigit(ch)))
            {
                e.Handled = true;
                return;
            }

            var proposedText = BuildProposedText(textBox, e.Text);
            e.Handled = !IsPotentialValidTimeText(proposedText);
        }

        private void TodoTimeTextBoxOnPasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(DataFormats.Text))
            {
                e.CancelCommand();
                return;
            }

            var pastedText = e.DataObject.GetData(DataFormats.Text) as string;
            if (string.IsNullOrWhiteSpace(pastedText))
            {
                e.CancelCommand();
                return;
            }

            if (pastedText.Any(ch => !char.IsDigit(ch)))
            {
                e.CancelCommand();
                return;
            }

            var textBox = sender as TextBox;
            if (textBox == null)
            {
                return;
            }

            var remainingLength = textBox.MaxLength - (textBox.Text.Length - textBox.SelectionLength);
            if (remainingLength <= 0)
            {
                e.CancelCommand();
                return;
            }

            var insertText = pastedText;
            if (insertText.Length > remainingLength)
            {
                insertText = insertText.Substring(0, remainingLength);
            }

            var proposedText = BuildProposedText(textBox, insertText);
            if (!IsPotentialValidTimeText(proposedText))
            {
                e.CancelCommand();
                return;
            }

            if (insertText != pastedText)
            {
                textBox.SelectedText = insertText;
                e.CancelCommand();
            }
        }

        private static string BuildProposedText(TextBox textBox, string input)
        {
            var current = textBox.Text ?? string.Empty;
            var selectionStart = textBox.SelectionStart;
            var selectionLength = textBox.SelectionLength;

            if (selectionStart < 0)
            {
                selectionStart = 0;
            }

            if (selectionStart > current.Length)
            {
                selectionStart = current.Length;
            }

            if (selectionLength < 0)
            {
                selectionLength = 0;
            }

            if (selectionStart + selectionLength > current.Length)
            {
                selectionLength = current.Length - selectionStart;
            }

            return current.Remove(selectionStart, selectionLength).Insert(selectionStart, input);
        }

        private static bool IsPotentialValidTimeText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return true;
            }

            if (text.Length > 4 || text.Any(ch => !char.IsDigit(ch)))
            {
                return false;
            }

            if (text.Length >= 1)
            {
                var hourTens = text[0] - '0';
                if (hourTens > 2)
                {
                    return false;
                }
            }

            if (text.Length >= 2)
            {
                var hourTens = text[0] - '0';
                var hourOnes = text[1] - '0';
                if (hourTens == 2 && hourOnes > 3)
                {
                    return false;
                }
            }

            if (text.Length >= 3)
            {
                var minuteTens = text[2] - '0';
                if (minuteTens > 5)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidCompleteTimeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length != 4 || text.Any(ch => !char.IsDigit(ch)))
            {
                return false;
            }

            var hour = int.Parse(text.Substring(0, 2), CultureInfo.InvariantCulture);
            var minute = int.Parse(text.Substring(2, 2), CultureInfo.InvariantCulture);
            return hour >= 0 && hour <= 23 && minute >= 0 && minute <= 59;
        }

        private void InputTextBoxOnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox == null)
            {
                return;
            }

            FocusInputTextBox(textBox, true);
            e.Handled = !textBox.IsKeyboardFocusWithin;
        }

        private void TodoInputContainerOnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            FocusInputTextBox(TodoInputTextBox, true);
            e.Handled = true;
        }

        private void TodoTimeContainerOnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            FocusInputTextBox(TodoTimeTextBox, true);
            e.Handled = true;
        }

        private void FocusInputTextBox(TextBox textBox, bool moveCaretToEnd)
        {
            if (textBox == null)
            {
                return;
            }

            if (!IsActive)
            {
                Activate();
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                textBox.Focus();
                if (moveCaretToEnd)
                {
                    textBox.CaretIndex = textBox.Text.Length;
                }
            }), DispatcherPriority.Input);
        }

        private void UpdateInputPlaceholder()
        {
            InputPlaceholderTextBlock.Visibility = string.IsNullOrWhiteSpace(TodoInputTextBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;

            TimeInputPlaceholderTextBlock.Visibility = string.IsNullOrWhiteSpace(TodoTimeTextBox.Text)
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
            settings.TelegramEnabled = _isTelegramEnabled;
            settings.TelegramBotToken = _telegramBotToken ?? string.Empty;
            settings.TelegramChatId = _telegramChatId ?? string.Empty;

            _settingsStore.Save(settings);
        }
        private void UpdatePinButtonState()
        {
            PinButton.Content = Topmost ? "Pinned" : "Pin";
            PinButton.Background = Topmost
                ? new SolidColorBrush(Color.FromArgb(46, 112, 163, 135))
                : new SolidColorBrush(Color.FromArgb(31, 50, 61, 75));

            if (_trayTopmostMenuItem != null)
            {
                _trayTopmostMenuItem.Checked = Topmost;
            }

            SyncTaskPanelVisualState();
        }

        private void UpdateStartupState()
        {
            var isStartupEnabled = _startupManager.IsEnabled();
            StartupButton.Content = isStartupEnabled ? "Auto On" : "Auto Off";
            StartupButton.Background = isStartupEnabled
                ? new SolidColorBrush(Color.FromArgb(46, 112, 163, 135))
                : new SolidColorBrush(Color.FromArgb(31, 50, 61, 75));

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
            PlayShowAnimation();
        }

        private void WindowSourceInitialized(object sender, EventArgs e)
        {
            var helper = new WindowInteropHelper(this);
            _windowSource = HwndSource.FromHwnd(helper.Handle);
            if (_windowSource != null)
            {
                _windowSource.AddHook(WindowMessageHook);
                RegisterHotKey(helper.Handle, HotKeyToggleVisibilityId, ModWin | ModShift, VkQ);
            }
        }

        private void PositionWindow()
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - ActualWidth;
            Top = workArea.Bottom - ActualHeight + 6;
        }

        private void DragAreaMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        private void PinButtonOnClick(object sender, RoutedEventArgs e)
        {
            SetTopmost(!Topmost);
        }

        private void ToggleTasksButtonOnClick(object sender, RoutedEventArgs e)
        {
            _isTaskPanelExpanded = !_isTaskPanelExpanded;
            UpdateTaskPanelState();
        }

        private void HideButtonOnClick(object sender, RoutedEventArgs e)
        {
            HideToTray(true);
        }

        private void HideToTray(bool showBalloonTip)
        {
            if (TasksPanelPopup != null)
            {
                TasksPanelPopup.IsOpen = false;
            }

            Hide();
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

            PositionWindow();
            PlayShowAnimation();

            Activate();
            Focus();
        }

        private void PlayShowAnimation()
        {
            var targetTop = Top;
            var targetOpacity = ClampOpacity(OpacitySlider.Value);

            BeginAnimation(Window.TopProperty, null);
            BeginAnimation(Window.OpacityProperty, null);

            Top = targetTop + 28;
            Opacity = 0;

            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var slideAnimation = new DoubleAnimation(targetTop, TimeSpan.FromMilliseconds(220));
            slideAnimation.EasingFunction = easing;

            var fadeAnimation = new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(220));
            fadeAnimation.EasingFunction = easing;
            fadeAnimation.FillBehavior = FillBehavior.Stop;
            fadeAnimation.Completed += delegate
            {
                Opacity = targetOpacity;
                UpdateTaskPanelState();
            };

            BeginAnimation(Window.TopProperty, slideAnimation);
            BeginAnimation(Window.OpacityProperty, fadeAnimation);
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

        private void TelegramSettingsTrayMenuItemOnClick(object sender, EventArgs e)
        {
            ShowTelegramSettingsDialog();
        }

        private void ExportReportTrayMenuItemOnClick(object sender, EventArgs e)
        {
            try
            {
                var result = _todoStore.ExportReport(_todoStore.DirectoryPath);
                Process.Start("explorer.exe", "/select,\"" + result.CsvPath + "\"");
            }
            catch (Exception)
            {
                MessageBox.Show("Could not export the report files.", "TodoWidget", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OpacitySliderOnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var nextOpacity = ClampOpacity(e.NewValue);
            BeginAnimation(Window.OpacityProperty, null);
            Opacity = nextOpacity;
            SyncTaskPanelVisualState();
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
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                UnregisterHotKey(handle, HotKeyToggleVisibilityId);
            }

            if (_windowSource != null)
            {
                _windowSource.RemoveHook(WindowMessageHook);
                _windowSource = null;
            }

            SaveSettings();
            DisposeTrayIcon();
        }

        private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotKey && wParam.ToInt32() == HotKeyToggleVisibilityId)
            {
                ToggleWindowVisibilityFromHotKey();
                handled = true;
            }

            return IntPtr.Zero;
        }

        private void ToggleWindowVisibilityFromHotKey()
        {
            if (IsVisible && WindowState == WindowState.Normal)
            {
                HideToTray(false);
                return;
            }

            RestoreFromTray();
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

        private void ShowTelegramSettingsDialog()
        {
            using (var dialog = new Forms.Form())
            {
                dialog.Text = "Telegram Settings";
                dialog.FormBorderStyle = Forms.FormBorderStyle.FixedDialog;
                dialog.StartPosition = Forms.FormStartPosition.CenterScreen;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.Font = new Drawing.Font("Segoe UI", 10F, Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Point);
                dialog.AutoScaleMode = Forms.AutoScaleMode.Font;
                dialog.ClientSize = new Drawing.Size(900, 650);

                var enabledCheckBox = new Forms.CheckBox();
                enabledCheckBox.Text = "Enable Telegram notifications";
                enabledCheckBox.Checked = _isTelegramEnabled;
                enabledCheckBox.AutoSize = true;
                enabledCheckBox.Margin = new Forms.Padding(0, 0, 0, 8);

                var tokenLabel = new Forms.Label();
                tokenLabel.Text = "Bot Token";
                tokenLabel.AutoSize = true;
                tokenLabel.Margin = new Forms.Padding(0, 2, 0, 2);

                var tokenTextBox = new Forms.TextBox();
                tokenTextBox.Text = !string.IsNullOrWhiteSpace(DefaultTelegramBotToken)
                    ? DefaultTelegramBotToken
                    : (_telegramBotToken ?? string.Empty);
                tokenTextBox.ReadOnly = !string.IsNullOrWhiteSpace(DefaultTelegramBotToken);
                tokenTextBox.Dock = Forms.DockStyle.Fill;
                tokenTextBox.Margin = new Forms.Padding(0, 0, 0, 8);

                var chatIdLabel = new Forms.Label();
                chatIdLabel.Text = "Chat ID";
                chatIdLabel.AutoSize = true;
                chatIdLabel.Margin = new Forms.Padding(0, 2, 0, 2);

                var chatIdTextBox = new Forms.TextBox();
                chatIdTextBox.Text = _telegramChatId ?? string.Empty;
                chatIdTextBox.Dock = Forms.DockStyle.Fill;
                chatIdTextBox.Margin = new Forms.Padding(0, 0, 0, 8);

                var startLinkButton = new Forms.Button();
                startLinkButton.Text = "Start Link";
                startLinkButton.Width = 120;
                startLinkButton.Height = 32;
                startLinkButton.Margin = new Forms.Padding(0, 0, 8, 0);

                var verifyLinkButton = new Forms.Button();
                verifyLinkButton.Text = "Verify Link";
                verifyLinkButton.Width = 120;
                verifyLinkButton.Height = 32;
                verifyLinkButton.Margin = new Forms.Padding(0);

                var linkUrlLabel = new Forms.Label();
                linkUrlLabel.Text = "Link URL";
                linkUrlLabel.AutoSize = true;
                linkUrlLabel.TextAlign = Drawing.ContentAlignment.MiddleLeft;
                linkUrlLabel.Margin = new Forms.Padding(0, 0, 8, 0);

                var linkUrlTextBox = new Forms.TextBox();
                linkUrlTextBox.ReadOnly = true;
                linkUrlTextBox.Dock = Forms.DockStyle.Fill;
                linkUrlTextBox.Margin = new Forms.Padding(0, 0, 8, 0);

                var copyLinkButton = new Forms.Button();
                copyLinkButton.Text = "Copy URL";
                copyLinkButton.Width = 110;
                copyLinkButton.Height = 28;
                copyLinkButton.Margin = new Forms.Padding(0);

                var commandLabel = new Forms.Label();
                commandLabel.Text = "Command";
                commandLabel.AutoSize = true;
                commandLabel.TextAlign = Drawing.ContentAlignment.MiddleLeft;
                commandLabel.Margin = new Forms.Padding(0, 0, 8, 0);

                var commandTextBox = new Forms.TextBox();
                commandTextBox.ReadOnly = true;
                commandTextBox.Dock = Forms.DockStyle.Fill;
                commandTextBox.Margin = new Forms.Padding(0, 0, 8, 0);

                var copyCommandButton = new Forms.Button();
                copyCommandButton.Text = "Copy /link";
                copyCommandButton.Width = 110;
                copyCommandButton.Height = 28;
                copyCommandButton.Margin = new Forms.Padding(0);

                var guideTextBox = new Forms.TextBox();
                guideTextBox.Multiline = true;
                guideTextBox.ReadOnly = true;
                guideTextBox.TabStop = false;
                guideTextBox.ScrollBars = Forms.ScrollBars.None;
                guideTextBox.WordWrap = true;
                guideTextBox.Dock = Forms.DockStyle.Fill;
                guideTextBox.Margin = new Forms.Padding(0, 8, 0, 8);
                guideTextBox.Text =
                    "1. 현재 사용 PC 및 알림 원하는 기기에 텔레그램 설치\r\n" +
                    "2. Start Link 클릭하여 봇 채팅 Start\r\n" +
                    "3. 봇 채팅방에서 복사된 Command 붙여넣기\r\n" +
                    "4. Verify Link 클릭하여 Chat ID 로드\r\n" +
                    "5. Test 클릭하여 연동 체크\r\n" +
                    "6. Save를 클릭하여 연동 완료";

                var saveButton = new Forms.Button();
                saveButton.Text = "Save";
                saveButton.Width = 96;
                saveButton.Height = 32;
                saveButton.Margin = new Forms.Padding(8, 0, 0, 0);

                var testButton = new Forms.Button();
                testButton.Text = "Test";
                testButton.Width = 96;
                testButton.Height = 32;
                testButton.Margin = new Forms.Padding(8, 0, 0, 0);

                var cancelButton = new Forms.Button();
                cancelButton.Text = "Cancel";
                cancelButton.DialogResult = Forms.DialogResult.Cancel;
                cancelButton.Width = 98;
                cancelButton.Height = 32;
                cancelButton.Margin = new Forms.Padding(8, 0, 0, 0);

                saveButton.Click += delegate
                {
                    var enabled = enabledCheckBox.Checked;
                    var savedToken = (tokenTextBox.Text ?? string.Empty).Trim();
                    var chatId = (chatIdTextBox.Text ?? string.Empty).Trim();

                    if (enabled && (string.IsNullOrWhiteSpace(savedToken) || string.IsNullOrWhiteSpace(chatId)))
                    {
                        MessageBox.Show("Telegram 사용 시 Bot Token과 Chat ID를 모두 입력해 주세요.", "TodoWidget", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    _isTelegramEnabled = enabled;
                    _telegramBotToken = savedToken;
                    _telegramChatId = chatId;
                    SaveSettings();
                    MessageBox.Show("저장되었습니다.", "TodoWidget", MessageBoxButton.OK, MessageBoxImage.Information);
                };

                startLinkButton.Click += delegate
                {
                    var token = (tokenTextBox.Text ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        MessageBox.Show("Bot Token이 비어 있습니다.", "TodoWidget", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    var code = GenerateTelegramLinkCode();
                    _pendingTelegramLinkCode = code;
                    _pendingTelegramLinkIssuedAtUtc = DateTime.UtcNow;
                    var linkCommand = "/link " + code;
                    commandTextBox.Text = linkCommand;

                    try
                    {
                        Forms.Clipboard.SetText(linkCommand);
                    }
                    catch
                    {
                        // Ignore clipboard errors and continue.
                    }

                    var botUsername = string.Empty;
                    string usernameError;
                    if (!string.IsNullOrWhiteSpace(DefaultTelegramBotUsername))
                    {
                        botUsername = DefaultTelegramBotUsername.Trim().TrimStart('@');
                    }
                    else
                    {
                        TryGetTelegramBotUsername(token, out botUsername, out usernameError);
                    }

                    var startPayload = "link_" + code;
                    if (!string.IsNullOrWhiteSpace(botUsername))
                    {
                        var linkUrl = "https://t.me/" + botUsername + "?start=" + Uri.EscapeDataString(startPayload);
                        var webFallbackUrl = "https://web.telegram.org/k/#@" + botUsername;
                        linkUrlTextBox.Text = linkUrl;
                        try
                        {
                            Process.Start("explorer.exe", "\"" + linkUrl + "\"");
                        }
                        catch
                        {
                        }
                    }
                    else
                    {
                        linkUrlTextBox.Text = string.Empty;
                    }
                };

                copyLinkButton.Click += delegate
                {
                    var text = linkUrlTextBox.Text ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        MessageBox.Show("복사할 링크가 없습니다. Start Link를 먼저 누르세요.", "TodoWidget", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    try
                    {
                        Forms.Clipboard.SetText(text);
                        MessageBox.Show("링크를 복사했습니다.", "TodoWidget", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    catch
                    {
                        MessageBox.Show("클립보드 복사에 실패했습니다.", "TodoWidget", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                };

                copyCommandButton.Click += delegate
                {
                    var text = commandTextBox.Text ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        MessageBox.Show("복사할 명령이 없습니다. Start Link를 먼저 누르세요.", "TodoWidget", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    try
                    {
                        Forms.Clipboard.SetText(text);
                        MessageBox.Show("/link 명령을 복사했습니다.", "TodoWidget", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    catch
                    {
                        MessageBox.Show("클립보드 복사에 실패했습니다.", "TodoWidget", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                };

                verifyLinkButton.Click += delegate
                {
                    var token = (tokenTextBox.Text ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        MessageBox.Show("Bot Token이 비어 있습니다.", "TodoWidget", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(_pendingTelegramLinkCode))
                    {
                        MessageBox.Show("먼저 Start Link를 눌러 인증코드를 생성하세요.", "TodoWidget", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    string linkedChatId;
                    string error;
                    if (TryResolveTelegramChatIdByCode(token, _pendingTelegramLinkCode, _pendingTelegramLinkIssuedAtUtc, out linkedChatId, out error))
                    {
                        chatIdTextBox.Text = linkedChatId;
                        MessageBox.Show("연동 완료: Chat ID를 자동으로 가져왔습니다.", "TodoWidget", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show("연동 확인 실패\n" + error, "TodoWidget", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                };

                testButton.Click += delegate
                {
                    var testToken = (tokenTextBox.Text ?? string.Empty).Trim();
                    var testChatId = (chatIdTextBox.Text ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(testToken) || string.IsNullOrWhiteSpace(testChatId))
                    {
                        MessageBox.Show("Bot Token과 Chat ID를 입력한 뒤 테스트해 주세요.", "TodoWidget", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    string error;
                    if (TrySendTelegramMessage(testToken, testChatId, "[TodoWidget] Telegram test message", out error))
                    {
                        MessageBox.Show("테스트 메시지 전송 성공", "TodoWidget", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show("테스트 메시지 전송 실패\n" + error, "TodoWidget", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                };

                var actionPanel = new Forms.FlowLayoutPanel();
                actionPanel.AutoSize = true;
                actionPanel.WrapContents = false;
                actionPanel.FlowDirection = Forms.FlowDirection.LeftToRight;
                actionPanel.Dock = Forms.DockStyle.Fill;
                actionPanel.Margin = new Forms.Padding(0, 0, 0, 8);
                actionPanel.Controls.Add(startLinkButton);
                actionPanel.Controls.Add(verifyLinkButton);

                var linkRowPanel = new Forms.TableLayoutPanel();
                linkRowPanel.ColumnCount = 3;
                linkRowPanel.RowCount = 2;
                linkRowPanel.Dock = Forms.DockStyle.Fill;
                linkRowPanel.Margin = new Forms.Padding(0);
                linkRowPanel.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.AutoSize));
                linkRowPanel.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100F));
                linkRowPanel.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.AutoSize));
                linkRowPanel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
                linkRowPanel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));

                linkRowPanel.Controls.Add(linkUrlLabel, 0, 0);
                linkRowPanel.Controls.Add(linkUrlTextBox, 1, 0);
                linkRowPanel.Controls.Add(copyLinkButton, 2, 0);
                linkRowPanel.Controls.Add(commandLabel, 0, 1);
                linkRowPanel.Controls.Add(commandTextBox, 1, 1);
                linkRowPanel.Controls.Add(copyCommandButton, 2, 1);

                var bottomButtonPanel = new Forms.FlowLayoutPanel();
                bottomButtonPanel.AutoSize = true;
                bottomButtonPanel.WrapContents = false;
                bottomButtonPanel.FlowDirection = Forms.FlowDirection.RightToLeft;
                bottomButtonPanel.Dock = Forms.DockStyle.Fill;
                bottomButtonPanel.Margin = new Forms.Padding(0);
                bottomButtonPanel.Controls.Add(cancelButton);
                bottomButtonPanel.Controls.Add(saveButton);
                bottomButtonPanel.Controls.Add(testButton);

                var rootPanel = new Forms.TableLayoutPanel();
                rootPanel.Dock = Forms.DockStyle.Fill;
                rootPanel.Padding = new Forms.Padding(16, 16, 16, 12);
                rootPanel.ColumnCount = 1;
                rootPanel.RowCount = 9;
                rootPanel.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100F));
                rootPanel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
                rootPanel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
                rootPanel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
                rootPanel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
                rootPanel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
                rootPanel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
                rootPanel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
                rootPanel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100F));
                rootPanel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));

                rootPanel.Controls.Add(enabledCheckBox, 0, 0);
                rootPanel.Controls.Add(tokenLabel, 0, 1);
                rootPanel.Controls.Add(tokenTextBox, 0, 2);
                rootPanel.Controls.Add(chatIdLabel, 0, 3);
                rootPanel.Controls.Add(chatIdTextBox, 0, 4);
                rootPanel.Controls.Add(actionPanel, 0, 5);
                rootPanel.Controls.Add(linkRowPanel, 0, 6);
                rootPanel.Controls.Add(guideTextBox, 0, 7);
                rootPanel.Controls.Add(bottomButtonPanel, 0, 8);

                dialog.Controls.Add(rootPanel);
                dialog.AcceptButton = null;
                dialog.CancelButton = cancelButton;
                dialog.ShowDialog();
            }
        }

        private void SendTelegramReminder(string message)
        {
            var token = GetActiveTelegramBotToken();
            if (!_isTelegramEnabled ||
                string.IsNullOrWhiteSpace(token) ||
                string.IsNullOrWhiteSpace(_telegramChatId) ||
                string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            string _;
            TrySendTelegramMessage(token, _telegramChatId.Trim(), message, out _);
        }

        private void CheckTelegramCommands()
        {
            var token = GetActiveTelegramBotToken();
            var configuredChatId = (_telegramChatId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(configuredChatId))
            {
                return;
            }

            var nowUtc = DateTime.UtcNow;
            if ((nowUtc - _lastTelegramCommandPollAtUtc).TotalSeconds < 4)
            {
                return;
            }

            _lastTelegramCommandPollAtUtc = nowUtc;

            List<TelegramUpdateItem> updates;
            string pollError;
            if (!TryGetTelegramUpdates(token, _telegramNextUpdateId, out updates, out pollError) || updates == null)
            {
                return;
            }

            if (!_isTelegramCommandPollingInitialized)
            {
                _isTelegramCommandPollingInitialized = true;
                if (updates.Count > 0)
                {
                    _telegramNextUpdateId = updates.Max(item => item.UpdateId) + 1;
                }

                return;
            }

            if (updates.Count == 0)
            {
                return;
            }

            foreach (var update in updates.OrderBy(item => item.UpdateId))
            {
                if (update == null)
                {
                    continue;
                }

                if (update.UpdateId >= _telegramNextUpdateId)
                {
                    _telegramNextUpdateId = update.UpdateId + 1;
                }

                var message = update.Message;
                var chat = message != null ? message.Chat : null;
                var text = message != null ? message.Text : null;
                if (chat == null || string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var incomingChatId = chat.Id.ToString(CultureInfo.InvariantCulture);
                if (!string.Equals(incomingChatId, configuredChatId, StringComparison.Ordinal))
                {
                    continue;
                }

                string responseText;
                if (!TryHandleTelegramTodoCommand(text, out responseText))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(responseText))
                {
                    continue;
                }

                string sendError;
                TrySendTelegramMessage(token, configuredChatId, responseText, out sendError);
            }
        }

        private string GetActiveTelegramBotToken()
        {
            if (!string.IsNullOrWhiteSpace(DefaultTelegramBotToken))
            {
                return DefaultTelegramBotToken.Trim();
            }

            return (_telegramBotToken ?? string.Empty).Trim();
        }

        private bool TryHandleTelegramTodoCommand(string text, out string responseText)
        {
            responseText = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var trimmed = text.Trim();
            if (!trimmed.StartsWith("/", StringComparison.Ordinal))
            {
                return false;
            }

            var split = trimmed.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (split.Length == 0)
            {
                return false;
            }

            var commandToken = split[0].Trim();
            if (commandToken.Length <= 1)
            {
                return false;
            }

            var commandName = commandToken.Substring(1);
            var mentionIndex = commandName.IndexOf('@');
            if (mentionIndex >= 0)
            {
                commandName = commandName.Substring(0, mentionIndex);
            }

            var args = split.Length > 1 ? (split[1] ?? string.Empty).Trim() : string.Empty;
            switch (commandName.ToLowerInvariant())
            {
                case "help":
                case "?":
                    responseText = BuildTelegramHelpMessage();
                    return true;
                case "add":
                    responseText = HandleTelegramAddCommand(args);
                    return true;
                case "delete":
                    responseText = HandleTelegramDeleteCommand(args);
                    return true;
                case "list":
                    responseText = HandleTelegramListCommand(args);
                    return true;
                case "done":
                    responseText = HandleTelegramDoneCommand(args);
                    return true;
                default:
                    return false;
            }
        }

        private static string BuildTelegramHelpMessage()
        {
            return string.Join("\n", new[]
            {
                "TodoWidget Telegram Commands",
                "/help",
                "  커맨드 목록 보기",
                "/add [HHmm] 할일",
                "  오늘 할일 추가 (시간 생략 가능)",
                "/list [yyyymmdd]",
                "  할일 목록 보기 (기본: 오늘)",
                "/done id",
                "  할일 완료 처리",
                "/delete id",
                "  할일 삭제"
            });
        }

        private string HandleTelegramAddCommand(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                return "사용법: /add [HHmm] 할일";
            }

            var remaining = args.Trim();
            var normalizedTime = string.Empty;
            var firstToken = remaining.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (firstToken.Length == 4 && firstToken.All(char.IsDigit))
            {
                if (!TryNormalizeTimeInput(firstToken, out normalizedTime))
                {
                    return "시간 형식이 올바르지 않습니다. 예: /add 0930 회의";
                }

                remaining = remaining.Substring(firstToken.Length).Trim();
            }

            if (string.IsNullOrWhiteSpace(remaining))
            {
                return "할일 제목이 비어 있습니다. 예: /add 0930 회의";
            }

            var targetDate = DateTime.Today;
            var newItem = new TodoItem
            {
                Id = Guid.NewGuid(),
                Title = remaining,
                IsCompleted = false,
                TaskDate = targetDate,
                TaskTime = normalizedTime,
                ReminderEnabled = _isReminderEnabledByDefault && !string.IsNullOrWhiteSpace(normalizedTime),
                ReminderTriggered = false,
                IsRecurringInstance = false,
                RecurrenceMode = "none",
                RecurrenceWeekdayMask = 0,
                RecurrenceRuleId = string.Empty
            };

            _allTodos.Add(newItem);

            var dayItems = _allTodos
                .Where(item => item.TaskDate.Date == targetDate && item.Id != newItem.Id)
                .OrderBy(item => item.SortOrder)
                .ToList();

            var insertIndex = dayItems.FindIndex(item => CompareByTimeThenTitle(newItem, item) < 0);
            if (insertIndex < 0)
            {
                insertIndex = dayItems.Count;
            }

            dayItems.Insert(insertIndex, newItem);
            ResequenceDateTodos(targetDate, dayItems);
            SaveTodos();
            RefreshAll();

            return "[추가 완료] " +
                   (string.IsNullOrWhiteSpace(newItem.TaskTime) ? string.Empty : "[" + newItem.TaskTime + "] ") +
                   newItem.Title +
                   "\nID: " + newItem.Id.ToString("D");
        }

        private string HandleTelegramDeleteCommand(string args)
        {
            var idToken = (args ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(idToken))
            {
                return "사용법: /delete id";
            }

            TodoItem item;
            string error;
            if (!TryResolveTodoItemByIdToken(idToken, out item, out error))
            {
                return error;
            }

            DeleteTodoItem(item);
            return "[삭제 완료] " + item.Title + "\nID: " + item.Id.ToString("D");
        }

        private string HandleTelegramDoneCommand(string args)
        {
            var idToken = (args ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(idToken))
            {
                return "사용법: /done id";
            }

            TodoItem item;
            string error;
            if (!TryResolveTodoItemByIdToken(idToken, out item, out error))
            {
                return error;
            }

            if (item.IsCompleted)
            {
                return "이미 완료된 항목입니다.\nID: " + item.Id.ToString("D");
            }

            item.IsCompleted = true;
            SaveTodos();
            RefreshAll();
            return "[완료 처리] " + item.Title + "\nID: " + item.Id.ToString("D");
        }

        private string HandleTelegramListCommand(string args)
        {
            var dateArg = (args ?? string.Empty).Trim();
            var targetDate = DateTime.Today;
            if (!string.IsNullOrWhiteSpace(dateArg))
            {
                if (!DateTime.TryParseExact(dateArg, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out targetDate))
                {
                    return "날짜 형식이 올바르지 않습니다. 예: /list 20260321";
                }
            }

            EnsureRecurringInstancesLoadedForDate(targetDate);

            var items = _allTodos
                .Where(item => item.TaskDate.Date == targetDate.Date)
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Title)
                .ToList();

            if (items.Count == 0)
            {
                return targetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 할일이 없습니다.";
            }

            var lines = new List<string>
            {
                targetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 할일 " + items.Count.ToString(CultureInfo.InvariantCulture) + "개"
            };

            foreach (var item in items)
            {
                lines.Add((item.IsCompleted ? "[x] " : "[ ] ") +
                          (string.IsNullOrWhiteSpace(item.TaskTime) ? string.Empty : item.TaskTime + " ") +
                          item.Title);
                lines.Add("ID: " + item.Id.ToString("D"));
            }

            return string.Join("\n", lines);
        }

        private bool TryResolveTodoItemByIdToken(string idToken, out TodoItem item, out string error)
        {
            item = null;
            error = string.Empty;

            var normalized = (idToken ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                error = "ID를 입력해 주세요.";
                return false;
            }

            Guid parsedGuid;
            if (Guid.TryParse(normalized, out parsedGuid))
            {
                item = _allTodos.FirstOrDefault(todo => todo.Id == parsedGuid);
                if (item == null)
                {
                    error = "해당 ID의 할일을 찾지 못했습니다.";
                    return false;
                }

                return true;
            }

            var matches = _allTodos
                .Where(todo => todo.Id.ToString("D").StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                error = "해당 ID의 할일을 찾지 못했습니다.";
                return false;
            }

            if (matches.Count > 1)
            {
                error = "ID가 여러 항목과 일치합니다. /list에서 더 긴 ID를 사용해 주세요.";
                return false;
            }

            item = matches[0];
            return true;
        }

        private void DeleteTodoItem(TodoItem item)
        {
            if (item == null)
            {
                return;
            }

            if (item.IsRecurringInstance && !string.IsNullOrWhiteSpace(item.RecurrenceRuleId))
            {
                _todoStore.AddRecurrenceSkip(item.RecurrenceRuleId, item.TaskDate.Date);
            }

            var itemDate = item.TaskDate.Date;
            _allTodos.Remove(item);
            var remaining = _allTodos
                .Where(todo => todo.TaskDate.Date == itemDate)
                .OrderBy(todo => todo.SortOrder)
                .ToList();
            ResequenceDateTodos(itemDate, remaining);
            SaveTodos();
            RefreshAll();
        }

        private static bool TryGetTelegramUpdates(string token, long offset, out List<TelegramUpdateItem> updates, out string error)
        {
            updates = new List<TelegramUpdateItem>();
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(token))
            {
                error = "Missing token";
                return false;
            }

            try
            {
                var url = "https://api.telegram.org/bot" + token.Trim() + "/getUpdates?timeout=0";
                if (offset > 0)
                {
                    url += "&offset=" + offset.ToString(CultureInfo.InvariantCulture);
                }

                var response = TelegramHttpClient.GetAsync(url).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    error = "getUpdates failed: " + response.StatusCode;
                    return false;
                }

                var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var serializer = new DataContractJsonSerializer(typeof(TelegramUpdatesResponse));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? string.Empty)))
                {
                    var parsed = serializer.ReadObject(stream) as TelegramUpdatesResponse;
                    updates = parsed != null && parsed.Result != null ? parsed.Result : new List<TelegramUpdateItem>();
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TrySendTelegramMessage(string token, string chatId, string message, out string error)
        {
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(message))
            {
                error = "Missing token/chat_id/message";
                return false;
            }

            try
            {
                var requestUrl =
                    "https://api.telegram.org/bot" + token.Trim() +
                    "/sendMessage?chat_id=" + Uri.EscapeDataString(chatId.Trim()) +
                    "&text=" + Uri.EscapeDataString(message) +
                    "&disable_notification=false";

                var response = TelegramHttpClient.GetAsync(requestUrl).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    var body = response.Content != null
                        ? response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                        : string.Empty;
                    error = "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase + " " + body;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string GenerateTelegramLinkCode()
        {
            lock (TelegramLinkCodeLock)
            {
                var now = DateTime.UtcNow;
                var staleCodes = RecentTelegramLinkCodes
                    .Where(entry => (now - entry.Value).TotalHours >= 24)
                    .Select(entry => entry.Key)
                    .ToList();

                foreach (var staleCode in staleCodes)
                {
                    RecentTelegramLinkCodes.Remove(staleCode);
                }

                for (var attempt = 0; attempt < 30; attempt++)
                {
                    var code = GenerateSecureGroupedCode();
                    if (RecentTelegramLinkCodes.ContainsKey(code))
                    {
                        continue;
                    }

                    RecentTelegramLinkCodes[code] = now;
                    return code;
                }

                var fallback = Guid.NewGuid().ToString("N").Substring(0, 12).ToUpperInvariant();
                RecentTelegramLinkCodes[fallback] = now;
                return fallback;
            }
        }

        private static string GenerateSecureGroupedCode()
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            const int codeLength = 12;
            var buffer = new byte[codeLength];
            var chars = new char[codeLength];

            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(buffer);
                for (var i = 0; i < codeLength; i++)
                {
                    chars[i] = alphabet[buffer[i] % alphabet.Length];
                }
            }

            return new string(chars, 0, 4) + "-" +
                   new string(chars, 4, 4) + "-" +
                   new string(chars, 8, 4);
        }

        private static bool TryResolveTelegramChatIdByCode(string token, string code, DateTime issuedAfterUtc, out string chatId, out string error)
        {
            chatId = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(code))
            {
                error = "Missing token/code";
                return false;
            }

            try
            {
                var updatesUrl = "https://api.telegram.org/bot" + token.Trim() + "/getUpdates";
                var response = TelegramHttpClient.GetAsync(updatesUrl).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    error = "getUpdates failed: " + response.StatusCode;
                    return false;
                }

                var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var serializer = new DataContractJsonSerializer(typeof(TelegramUpdatesResponse));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? string.Empty)))
                {
                    var updates = serializer.ReadObject(stream) as TelegramUpdatesResponse;
                    if (updates == null || updates.Result == null || updates.Result.Count == 0)
                    {
                        error = "No updates yet. 봇 채팅에서 시작 또는 /link 코드를 먼저 보내주세요.";
                        return false;
                    }

                    var matched = updates.Result
                        .Where(item => item != null && item.Message != null && !string.IsNullOrWhiteSpace(item.Message.Text))
                        .Where(item => item.Message.Date <= 0 || DateTimeOffset.FromUnixTimeSeconds(item.Message.Date).UtcDateTime >= issuedAfterUtc.AddSeconds(-3))
                        .OrderByDescending(item => item.UpdateId)
                        .FirstOrDefault(item => item.Message.Text.IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (matched == null || matched.Message == null || matched.Message.Chat == null)
                    {
                        error = "코드가 포함된 메시지를 찾지 못했습니다.";
                        return false;
                    }

                    chatId = matched.Message.Chat.Id.ToString(CultureInfo.InvariantCulture);
                    return !string.IsNullOrWhiteSpace(chatId);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryGetTelegramBotUsername(string token, out string username, out string error)
        {
            username = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(token))
            {
                error = "Missing token";
                return false;
            }

            try
            {
                var url = "https://api.telegram.org/bot" + token.Trim() + "/getMe";
                var response = TelegramHttpClient.GetAsync(url).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    error = "getMe failed: " + response.StatusCode;
                    return false;
                }

                var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var serializer = new DataContractJsonSerializer(typeof(TelegramGetMeResponse));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? string.Empty)))
                {
                    var parsed = serializer.ReadObject(stream) as TelegramGetMeResponse;
                    if (parsed == null || parsed.Result == null || string.IsNullOrWhiteSpace(parsed.Result.Username))
                    {
                        error = "username not found";
                        return false;
                    }

                    username = parsed.Result.Username.Trim().TrimStart('@');
                    return !string.IsNullOrWhiteSpace(username);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        [DataContract]
        private sealed class TelegramUpdatesResponse
        {
            [DataMember(Name = "result")]
            public List<TelegramUpdateItem> Result { get; set; }
        }

        [DataContract]
        private sealed class TelegramUpdateItem
        {
            [DataMember(Name = "update_id")]
            public long UpdateId { get; set; }

            [DataMember(Name = "message")]
            public TelegramMessageItem Message { get; set; }
        }

        [DataContract]
        private sealed class TelegramMessageItem
        {
            [DataMember(Name = "text")]
            public string Text { get; set; }

            [DataMember(Name = "date")]
            public long Date { get; set; }

            [DataMember(Name = "chat")]
            public TelegramChatItem Chat { get; set; }
        }

        [DataContract]
        private sealed class TelegramChatItem
        {
            [DataMember(Name = "id")]
            public long Id { get; set; }
        }

        [DataContract]
        private sealed class TelegramGetMeResponse
        {
            [DataMember(Name = "result")]
            public TelegramBotUser Result { get; set; }
        }

        [DataContract]
        private sealed class TelegramBotUser
        {
            [DataMember(Name = "username")]
            public string Username { get; set; }
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T target)
                {
                    return target;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null)
            {
                yield break;
            }

            var childCount = VisualTreeHelper.GetChildrenCount(root);
            for (var index = 0; index < childCount; index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                if (child is T target)
                {
                    yield return target;
                }

                foreach (var descendant in FindVisualChildren<T>(child))
                {
                    yield return descendant;
                }
            }
        }

        private void TodoListScrollViewerOnLoaded(object sender, RoutedEventArgs e)
        {
            var scrollViewer = sender as ScrollViewer;
            if (scrollViewer == null)
            {
                return;
            }

            var scrollBarStyle = FindResource("TodoListScrollBarStyle") as Style;
            if (scrollBarStyle == null)
            {
                return;
            }

            foreach (var scrollBar in FindVisualChildren<System.Windows.Controls.Primitives.ScrollBar>(scrollViewer))
            {
                if (scrollBar.Orientation == Orientation.Vertical)
                {
                    scrollBar.Style = scrollBarStyle;
                }
            }
        }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags);


        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);


        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private void TodoItemBorderOnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var border = sender as Border;
            var item = border != null ? border.DataContext as TodoItem : null;
            if (item == null)
            {
                return;
            }

            _dragStartPoint = e.GetPosition(this);
            _draggedTodoItem = item;

            var origin = e.OriginalSource as DependencyObject;
            if (FindAncestor<Button>(origin) != null || FindAncestor<CheckBox>(origin) != null)
            {
                _draggedTodoItem = null;
                return;
            }
        }

        private void TodoItemBorderOnMouseMove(object sender, MouseEventArgs e)
        {
            if (_draggedTodoItem == null || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var currentPoint = e.GetPosition(this);
            var delta = currentPoint - _dragStartPoint;

            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            var sourceItem = _draggedTodoItem;
            _draggedTodoItem = null;
            DragDrop.DoDragDrop((DependencyObject)sender, sourceItem, DragDropEffects.Move);
        }

        private void TodoItemBorderOnDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(TodoItem)))
            {
                e.Effects = DragDropEffects.Move;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }

            e.Handled = true;
        }

        private void TodoItemBorderOnDrop(object sender, DragEventArgs e)
        {
            var targetBorder = sender as Border;
            var targetItem = targetBorder != null ? targetBorder.DataContext as TodoItem : null;
            var draggedItem = e.Data.GetData(typeof(TodoItem)) as TodoItem;

            if (targetItem == null || draggedItem == null || ReferenceEquals(targetItem, draggedItem))
            {
                return;
            }

            var dateItems = _allTodos
                .Where(item => item.TaskDate.Date == _selectedDate.Date)
                .OrderBy(item => item.SortOrder)
                .ToList();

            var sourceIndex = dateItems.IndexOf(draggedItem);
            var targetIndex = dateItems.IndexOf(targetItem);
            if (sourceIndex < 0 || targetIndex < 0)
            {
                return;
            }

            dateItems.Remove(draggedItem);

            // Dragging downward should place after the hovered item.
            var insertIndex = sourceIndex < targetIndex ? targetIndex : targetIndex;
            if (insertIndex < 0)
            {
                insertIndex = 0;
            }

            if (insertIndex > dateItems.Count)
            {
                insertIndex = dateItems.Count;
            }

            dateItems.Insert(insertIndex, draggedItem);
            ResequenceDateTodos(_selectedDate.Date, dateItems);
            SaveTodos();
            RefreshAll();
            e.Handled = true;
        }

    }
}





