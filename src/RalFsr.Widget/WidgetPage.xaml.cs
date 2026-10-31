using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace RalFsr.Widget
{
    public sealed partial class WidgetPage : Page
    {
        private XboxGameBarWidget _widget;
        private XboxGameBarAppTargetTracker _tracker;
        /// <summary>Refreshes while OptiScaler downloads, and while an installed game runs so the status badge stays live.</summary>
        private readonly DispatcherTimer _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };

        private string _exeDir;
        private bool _gameInstalled;
        private bool _deviceSupported;
        private string _gameBarHint = "press Win + G to open Game Bar";
        private bool _setupReady;
        private bool _suppressOverlayEvent;
        private readonly List<string> _scaleKeys = new List<string>();
        private bool _busy;
        private int _installedCount;
        private bool _installedExpanded;

        public WidgetPage()
        {
            InitializeComponent();
            _poll.Tick += async (s, e) =>
            {
                if (Window.Current.Visible) await RefreshAsync(quiet: true);
            };
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            try
            {
                await StartAsync(e);
            }
            catch (Exception ex)
            {
                CrashLog.Write("page start", ex);
                CrashLog.Show("page start", ex);
            }
        }

        private async Task StartAsync(NavigationEventArgs e)
        {
            _widget = e.Parameter as XboxGameBarWidget;
            if (_widget != null)
            {
                try
                {
                    // Lets Game Bar tell us the current game's name. Optional: the helper finds the game either way.
                    _tracker = new XboxGameBarAppTargetTracker(_widget);
                    _tracker.TargetChanged += async (s, a) => await RunOnUi(() => RefreshAsync(quiet: true));
                }
                catch (Exception)
                {
                    _tracker = null;
                }
            }

            PageScroll.BringIntoViewRequested += PageScroll_BringIntoViewRequested;

            // While the helper is being retried (right after an update, for example), say so instead of looking stuck.
            HostClient.Starting += async (_, attempt) =>
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    StatusText.Text = $"Starting the helper (try {attempt} of 3)...");

            Window.Current.VisibilityChanged += async (s, a) =>
            {
                if (!a.Visible) return;
                await TellHelperWidgetOpenedAsync();
                await RefreshAsync(quiet: true);
            };

            await TellHelperWidgetOpenedAsync();
            await RefreshAsync(quiet: false);

            // Controller users land on the main action straight away.
            var first = PrimaryButton.Visibility == Visibility.Visible && GameActions.Visibility == Visibility.Visible
                ? (Control)PrimaryButton
                : RefreshButton;
            first.Focus(FocusState.Programmatic);
        }

        // ---------------------------------------------------------------- refresh and render

        private async Task RefreshAsync(bool quiet)
        {
            if (_busy) return;
            if (!quiet) StatusText.Text = "";

            string targetName = "", targetIsGame = "false";
            try
            {
                if (_tracker != null && _tracker.Setting == XboxGameBarAppTargetSetting.Enabled)
                {
                    var target = _tracker.GetTarget();
                    if (target != null)
                    {
                        targetName = target.DisplayName ?? "";
                        targetIsGame = target.IsGame ? "true" : "false";
                    }
                }
            }
            catch (Exception)
            {
                // Target info is a hint only.
            }

            var state = await HostClient.SendAsync("state", "targetName", targetName, "targetIsGame", targetIsGame);
            if (!Json.Bool(state, "ok"))
            {
                GameName.Text = HostClient.JustUpdated() ? "Restart to finish the update" : "Helper not running";
                GameMeta.Text = "";
                GameStatus.Text = Json.Str(state, "message");
                GameActions.Visibility = Visibility.Collapsed;
                return;
            }

            Render(state);
        }

        private void Render(JsonObject state)
        {
            // Device
            var device = Json.Obj(state, "device");
            DeviceText.Text = Json.Str(device, "summary");
            _deviceSupported = Json.Bool(device, "supported");
            _gameBarHint = Json.Str(device, "gameBarHint", "press Win + G to open Game Bar");
            // Shown for notes (official driver, experimental) as well as for unsupported devices.
            DeviceBanner.Visibility = Json.Bool(device, "showNote") ? Visibility.Visible : Visibility.Collapsed;
            DeviceBannerText.Text = Json.Str(device, "message");

            // Setup
            var opti = Json.Obj(state, "opti");
            var fsr4 = Json.Obj(state, "fsr4");
            var optiReady = Json.Bool(opti, "ready");
            var fsr4Ready = Json.Bool(fsr4, "ready");
            _setupReady = optiReady && fsr4Ready;

            OptiText.Text = (optiReady ? "Ready: " : "") + Json.Str(opti, "status", "Getting OptiScaler...");
            var fsr4Status = Json.Str(fsr4, "status");
            var fsr4Busy = Json.Bool(fsr4, "busy");
            if (fsr4Busy && !string.IsNullOrEmpty(fsr4Status))
                Fsr4Text.Text = fsr4Status; // download progress
            else if (fsr4Ready)
                Fsr4Text.Text = "Ready: FSR 4.1 Lite files " + Json.Str(fsr4, "version") +
                                (Json.Bool(fsr4, "verified") ? ", signed AMD driver files" : "");
            else
                Fsr4Text.Text = string.IsNullOrEmpty(fsr4Status) ? "Getting the FSR 4.1 Lite files..." : fsr4Status;

            // The manual import is a fallback: only offer it when the files aren't there and the download isn't running.
            ImportButton.Visibility = !fsr4Ready && !fsr4Busy && !string.IsNullOrEmpty(fsr4Status)
                ? Visibility.Visible
                : Visibility.Collapsed;

            _suppressOverlayEvent = true;
            OverlaySwitch.IsOn = Json.Bool(state, "overlay");
            var sizeIndex = Array.IndexOf(OverlaySizeKeys, Json.Str(state, "overlaySize", "medium"));
            OverlaySizeBox.SelectedIndex = sizeIndex >= 0 ? sizeIndex : OverlaySizeKeys.Length - 1;
            var positionIndex = Array.IndexOf(OverlayPositionKeys, Json.Str(state, "overlayPosition", "topleft"));
            OverlayPositionBox.SelectedIndex = positionIndex >= 0 ? positionIndex : 0;
            OverlayOptions.Visibility = OverlaySwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
            NotifySwitch.IsOn = Json.Bool(state, "notify");
            AutoUpdateSwitch.IsOn = Json.Bool(state, "autoUpdate");
            RenderWidgetVersion(Json.Obj(state, "widget"));
            _suppressOverlayEvent = false;

            var game = Json.Obj(state, "game");
            RenderGame(game);

            // Keep polling while downloading, or while an installed game isn't fully active yet / might change.
            var watchGame = game != null && Json.Bool(game, "installed");
            var widgetUpdating = Json.Bool(Json.Obj(state, "widget"), "updating"); // keep the progress bar moving
            if (Json.Bool(opti, "busy") || fsr4Busy || !fsr4Ready || watchGame || widgetUpdating) _poll.Start(); else _poll.Stop();
            RenderInstalled(Json.Arr(state, "installed"));
            RenderUpdates(Json.Num(state, "updatesAvailable"));
        }

        private void RenderGame(JsonObject game)
        {
            if (game == null)
            {
                _exeDir = null;
                GameName.Text = "No game detected";
                GameMeta.Text = "";
                GameStatus.Text = "Start a game, then " + _gameBarHint + " and open this widget over it.";
                GameActions.Visibility = Visibility.Collapsed;
                ActiveBadge.Visibility = Visibility.Collapsed;
                ScalePanel.Visibility = Visibility.Collapsed;
                ResolutionPanel.Visibility = Visibility.Collapsed;
                ResolutionText.Visibility = Visibility.Collapsed;
                AutoFixBanner.Visibility = Visibility.Collapsed;
                SwitchLoaderButton.Visibility = Visibility.Collapsed;
                CompatText.Visibility = Visibility.Collapsed;
                return;
            }

            RenderBadge(Json.Str(game, "activeStage"), Json.Str(game, "activeText"));

            SwitchLoaderButton.Visibility = Json.Bool(game, "canSwitchLoader") ? Visibility.Visible : Visibility.Collapsed;

            RenderResolution(game);

            var autoFix = Json.Str(game, "autoFix");
            if (!string.IsNullOrEmpty(autoFix))
            {
                AutoFixText.Text = autoFix;
                AutoFixBanner.Visibility = Visibility.Visible; // stays until the next game or widget session
            }

            _exeDir = Json.Str(game, "exeDir");
            _gameInstalled = Json.Bool(game, "installed");

            GameName.Text = Json.Str(game, "name");
            GameMeta.Text = Json.Str(game, "source") + "  |  " + Json.Str(game, "exeName");
            GameStatus.Text = Json.Str(game, "stateText");
            var overrideNote = Json.Str(game, "overrideNote"); // full-resolution note wins over the status text
            if (!string.IsNullOrEmpty(overrideNote)) GameStatus.Text = overrideNote;
            RenderCompat(Json.Obj(game, "compat"));

            // Which OptiScaler build this game has, so it's clear whether "Update" did its job.
            var optiBuild = Json.Str(game, "optiBuild");
            if (_gameInstalledFromState(game) && !string.IsNullOrEmpty(optiBuild) && string.IsNullOrEmpty(overrideNote))
                GameStatus.Text += " Using " + optiBuild + (Json.Bool(game, "optiOutdated") ? ", and a newer build is available." : ".");
            GameActions.Visibility = Visibility.Visible;

            RenderScaleOptions(game);

            if (_gameInstalled)
            {
                PrimaryText.Text = "Uninstall from this game";
                PrimaryIcon.Glyph = "\uE74D";
                PrimaryButton.IsEnabled = true;
            }
            else
            {
                PrimaryText.Text = "Install FSR 4.1 Lite";
                PrimaryIcon.Glyph = "\uE896";
                PrimaryButton.IsEnabled = Json.Bool(game, "canInstall") && _deviceSupported && _setupReady;
                if (Json.Bool(game, "canInstall") && !_setupReady)
                    GameStatus.Text += " Finish setup below to install.";
            }
        }

        /// <summary>Short chip labels for the render resolution keys; the helper's longer labels are the fallback.</summary>
        private static string ChipLabel(string key, string label)
        {
            switch (key)
            {
                case "game": return "Game";
                case "native": return "Native";
                case "quality": return "Quality";
                case "balanced": return "Balanced";
                case "performance": return "Perf.";
                default: return label;
            }
        }

        /// <summary>
        /// Fills the render resolution chips for an installed game and marks its current choice in red. The chips are
        /// built once; later refreshes only move the highlight, so controller focus stays on the chip just pressed.
        /// </summary>
        private void RenderScaleOptions(JsonObject game)
        {
            if (!Json.Bool(game, "installed"))
            {
                ScalePanel.Visibility = Visibility.Collapsed;
                return;
            }

            var options = Json.Arr(game, "renderScales");
            if (ScaleChips.Children.Count != options.Count)
            {
                ScaleChips.Children.Clear();
                ScaleChips.ColumnDefinitions.Clear();
                _scaleKeys.Clear();
                foreach (var item in options)
                {
                    if (item.ValueType != JsonValueType.Object) continue;
                    var o = item.GetObject();
                    var key = Json.Str(o, "key");
                    var chip = new Button
                    {
                        Style = (Style)Resources["ChipButton"],
                        Content = ChipLabel(key, Json.Str(o, "label")),
                        Tag = key,
                    };
                    ToolTipService.SetToolTip(chip, Json.Str(o, "label"));
                    chip.Click += ScaleChip_Click;
                    ScaleChips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    Grid.SetColumn(chip, _scaleKeys.Count);
                    ScaleChips.Children.Add(chip);
                    _scaleKeys.Add(key);
                }
            }

            var current = Json.Str(game, "renderScale", "game");
            foreach (var child in ScaleChips.Children)
            {
                var chip = child as Button;
                if (chip == null) continue;
                var on = (chip.Tag as string) == current;
                chip.Background = on ? (Brush)Application.Current.Resources["RalRedBrush"] : (Brush)Application.Current.Resources["ChipBrush"];
                chip.Foreground = new SolidColorBrush(on ? Colors.White : Color.FromArgb(255, 0xB9, 0xBF, 0xCA));
                chip.FontWeight = on ? Windows.UI.Text.FontWeights.SemiBold : Windows.UI.Text.FontWeights.Normal;
            }
            ScalePanel.Visibility = Visibility.Visible;
        }

        private async void ScaleChip_Click(object sender, RoutedEventArgs e)
        {
            var key = (sender as Button)?.Tag as string;
            if (string.IsNullOrEmpty(_exeDir) || string.IsNullOrEmpty(key)) return;
            await RunCommandAsync("renderScale", "exeDir", _exeDir, "value", key);
        }

        /// <summary>"1280 x 720" to "1920 x 1080" with a bar for the render share of the output width.</summary>
        private void RenderResolution(JsonObject game)
        {
            var rw = (int)Json.Num(game, "renderWidth");
            var rh = (int)Json.Num(game, "renderHeight");
            var dw = (int)Json.Num(game, "displayWidth");
            var dh = (int)Json.Num(game, "displayHeight");
            if (rw > 0 && dw > 0)
            {
                RenderResText.Text = rh > 0 ? rw + " \u00D7 " + rh : rw + " wide";
                OutputResText.Text = dh > 0 ? dw + " \u00D7 " + dh : dw + " wide";
                ResolutionBar.Value = Math.Min(100, Math.Round(100.0 * rw / dw));
                ResolutionPanel.Visibility = Visibility.Visible;
                ResolutionText.Visibility = Visibility.Collapsed;
                return;
            }
            ResolutionPanel.Visibility = Visibility.Collapsed;
            var text = Json.Str(game, "resolutionText");
            ResolutionText.Text = text;
            ResolutionText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void SwitchLoader_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_exeDir)) await RunCommandAsync("switchLoader", "exeDir", _exeDir);
        }

        private void RenderBadge(string stage, string text)
        {
            if (string.IsNullOrEmpty(text) || stage == "None")
            {
                ActiveBadge.Visibility = Visibility.Collapsed;
                return;
            }

            // Green when FSR 4.1 Lite is running, amber for "almost there" and "running, but not Lite".
            var color = stage == "Active"
                ? Color.FromArgb(255, 0x39, 0xC2, 0x7A)
                : Color.FromArgb(255, 0xE8, 0xA2, 0x3A);

            ActiveDot.Fill = new SolidColorBrush(color);
            ActiveBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, color.R, color.G, color.B));
            ActiveBadge.Background = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B));
            ActiveText.Text = text;
            ActiveText.Foreground = new SolidColorBrush(stage == "Active" ? Color.FromArgb(255, 0x9E, 0xE6, 0xBD) : Color.FromArgb(255, 0xF2, 0xC2, 0x7A));
            ActiveIcon.Glyph = stage == "Active" ? "\uE930" : "\uE7BA";
            ActiveIcon.Foreground = new SolidColorBrush(color);
            ActiveBadge.Visibility = Visibility.Visible;
        }

        private static bool _gameInstalledFromState(JsonObject game) => Json.Bool(game, "installed");

        private void RenderCompat(JsonObject compat)
        {
            var text = Json.Str(compat, "text");
            var warn = Json.Str(compat, "status") == "NotWorking" || Json.Str(compat, "status") == "Mixed";
            CompatText.Text = text;
            CompatText.Foreground = (Brush)Application.Current.Resources[warn ? "WarnBrush" : "MutedBrush"];
            CompatText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void RenderUpdates(double count)
        {
            var n = (int)count;
            UpdateAllButton.Content = n == 1 ? "Update 1 game to the new OptiScaler" : "Update " + n + " games to the new OptiScaler";
            UpdateAllButton.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void UpdateAll_Click(object sender, RoutedEventArgs e)
        {
            UpdateAllButton.IsEnabled = false;
            try { await RunCommandAsync("updateAll"); }
            finally { UpdateAllButton.IsEnabled = true; }
        }

        private void InstalledToggle_Click(object sender, RoutedEventArgs e)
        {
            _installedExpanded = !_installedExpanded;
            UpdateInstalledToggle();
        }

        private void UpdateInstalledToggle()
        {
            var games = _installedCount == 1 ? "1 game" : _installedCount + " games";
            InstalledToggleText.Text = "Installed in " + games + (_installedExpanded ? "  \u25BE" : "  \u25B8");
            InstalledListPanel.Visibility = _installedExpanded ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>"OptiScaler v10.0.0-pre1 (Oct 25, 2026) · update available", or that it's running.</summary>
        private static string InstalledNote(JsonObject o)
        {
            var build = Json.Str(o, "build");
            var note = string.IsNullOrEmpty(build) ? "FSR 4.1 Lite installed" : build;
            return note;
        }

        private void RenderInstalled(JsonArray installed)
        {
            var rows = new List<InstalledRow>();
            foreach (var item in installed)
            {
                if (item.ValueType != JsonValueType.Object) continue;
                var o = item.GetObject();
                var row = new InstalledRow
                {
                    Name = Json.Str(o, "name"),
                    ExeDir = Json.Str(o, "exeDir"),
                    Note = InstalledNote(o),
                };
                // Tags: running now (green) wins over a pending update (amber).
                if (Json.Bool(o, "running"))
                {
                    row.Status = "Running";
                    row.StatusForeground = (Brush)Application.Current.Resources["GoodTextBrush"];
                    row.StatusBackground = (Brush)Application.Current.Resources["GoodTintBrush"];
                }
                else if (Json.Bool(o, "outdated"))
                {
                    row.Status = "Update";
                    row.StatusForeground = (Brush)Application.Current.Resources["WarnTextBrush"];
                    row.StatusBackground = (Brush)Application.Current.Resources["WarnTintBrush"];
                }
                rows.Add(row);
            }
            InstalledList.ItemsSource = rows;
            _installedCount = rows.Count;
            UpdateInstalledToggle();
            InstalledPanel.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------------------------------------------------------------- actions

        private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(quiet: false);

        private async void Primary_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_exeDir)) return;
            await RunCommandAsync(_gameInstalled ? "remove" : "install", "exeDir", _exeDir);
        }

        private async void Remove_Click(object sender, RoutedEventArgs e)
        {
            var dir = (sender as Button)?.Tag as string;
            if (!string.IsNullOrEmpty(dir)) await RunCommandAsync("remove", "exeDir", dir);
        }

        /// <summary>Same order as the ComboBox items: Small, Medium, Large, Extra large.</summary>
        private static readonly string[] OverlaySizeKeys = { "small", "medium", "large", "xlarge", "huge", "max" };

        /// <summary>Same order as the position ComboBox items.</summary>
        private static readonly string[] OverlayPositionKeys = { "topleft", "topright", "bottomleft", "bottomright" };

        private async void OverlayPosition_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressOverlayEvent) return;
            var index = OverlayPositionBox.SelectedIndex;
            if (index < 0 || index >= OverlayPositionKeys.Length) return;
            await RunCommandAsync("overlayPosition", "value", OverlayPositionKeys[index]);
        }

        private async void OverlaySize_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressOverlayEvent) return;
            var index = OverlaySizeBox.SelectedIndex;
            if (index < 0 || index >= OverlaySizeKeys.Length) return;
            await RunCommandAsync("overlaySize", "value", OverlaySizeKeys[index]);
        }

        private async void Overlay_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressOverlayEvent) return;
            OverlayOptions.Visibility = OverlaySwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
            await RunCommandAsync("overlay", "on", OverlaySwitch.IsOn ? "true" : "false");
        }

        /// <summary>
        /// The widget's version line, and while updating: a progress bar (percentage while downloading, moving while
        /// installing) with the current stage, or the error and Try again if it failed.
        /// </summary>
        private void RenderWidgetVersion(JsonObject widget)
        {
            var version = Json.Str(widget, "version");
            var update = Json.Str(widget, "update");
            var updating = Json.Bool(widget, "updating");
            var failed = Json.Bool(widget, "failed");
            var message = Json.Str(widget, "message");
            var percent = (int)Json.Num(widget, "percent");
            var hasUpdate = !string.IsNullOrEmpty(update);

            AppVersionText.Text = "Version " + version;
            CheckUpdatesText.Text = hasUpdate ? "Version " + update + " is available" : "Up to date \u00B7 check for updates";

            WidgetUpdateBar.Visibility = updating ? Visibility.Visible : Visibility.Collapsed;
            WidgetUpdateBar.IsIndeterminate = updating && percent < 0;
            if (percent >= 0) WidgetUpdateBar.Value = percent;

            WidgetUpdateText.Text = message;
            WidgetUpdateText.Visibility = !string.IsNullOrEmpty(message) && (updating || failed || !hasUpdate)
                ? Visibility.Visible : Visibility.Collapsed;
            WidgetUpdateText.Foreground = (Brush)Application.Current.Resources[failed ? "WarnBrush" : "MutedBrush"];

            UpdateWidgetButton.Content = failed ? "Try again" : "Update to " + update;
            UpdateWidgetButton.Visibility = hasUpdate && !updating ? Visibility.Visible : Visibility.Collapsed;
            CheckUpdatesButton.IsEnabled = !updating;
            WidgetUpdatePanel.Visibility = WidgetUpdateBar.Visibility == Visibility.Visible ||
                                           WidgetUpdateText.Visibility == Visibility.Visible ||
                                           UpdateWidgetButton.Visibility == Visibility.Visible
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void SupportReport_Click(object sender, RoutedEventArgs e)
        {
            SupportReportText.Text = "Creating report...";
            try
            {
                await RunCommandAsync("supportReport", "exeDir", _exeDir ?? "", "gameName", GameName.Text ?? "",
                    "cardText", GameStatus.Text ?? "");
            }
            finally { SupportReportText.Text = "Create support report"; }
        }

        private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdatesText.Text = "Checking...";
            await RunCommandAsync("checkWidgetUpdate");
        }

        private async void UpdateWidget_Click(object sender, RoutedEventArgs e)
        {
            UpdateWidgetButton.IsEnabled = false;
            try { await RunCommandAsync("updateWidget"); }
            finally { UpdateWidgetButton.IsEnabled = true; }
        }

        private async void AutoUpdate_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressOverlayEvent) return;
            await RunCommandAsync("autoUpdate", "on", AutoUpdateSwitch.IsOn ? "true" : "false");
        }

        private async void Notify_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressOverlayEvent) return;
            var on = NotifySwitch.IsOn;

            // The watcher starts with Windows through the package's startup task.
            var note = "";
            try
            {
                var task = await StartupTask.GetAsync("FSR41LiteWatcher");
                if (on)
                {
                    var state = await task.RequestEnableAsync();
                    if (state == StartupTaskState.DisabledByUser || state == StartupTaskState.DisabledByPolicy)
                        note = " Windows has it turned off as a startup app: turn on \"FSR 4.1 Lite notifications\" " +
                               "in Settings > Apps > Startup so it also works after a restart.";
                }
                else
                {
                    task.Disable();
                }
            }
            catch (Exception)
            {
                // Notifications still work this session through the running helper.
            }

            await RunCommandAsync("notify", "on", on ? "true" : "false");
            if (note.Length > 0) StatusText.Text += note;
        }

        private async void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            var games = _installedCount == 1 ? "1 game" : _installedCount + " games";
            var dialog = new ContentDialog
            {
                Title = "Uninstall FSR 4.1 Lite?",
                Content = _installedCount > 0
                    ? "This removes FSR 4.1 Lite from " + games + " and puts their folders back the way they were. " +
                      "Then Windows Settings opens so you can uninstall the widget."
                    : "Windows Settings will open so you can uninstall the widget.",
                PrimaryButtonText = "Uninstall",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var result = await RunCommandAsync("removeAll");
            if (result == null || !Json.Bool(result, "readyToUninstall")) return; // a running game was skipped; message says which

            // Windows' own page for this app, with its Uninstall button.
            await Launcher.LaunchUriAsync(new Uri("ms-settings:appsfeatures-app?" + Package.Current.Id.FamilyName));
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add(".7z");
            picker.FileTypeFilter.Add(".zip");
            picker.FileTypeFilter.Add(".dll");

            var files = await picker.PickMultipleFilesAsync();
            if (files == null || files.Count == 0) return;

            // Copy into the package's temp folder, which the helper can read.
            var paths = new List<string>();
            foreach (var file in files)
            {
                var copy = await file.CopyAsync(ApplicationData.Current.TemporaryFolder, file.Name, NameCollisionOption.ReplaceExisting);
                paths.Add(copy.Path);
            }
            var joined = string.Join("\n", paths);

            var result = await RunCommandAsync("import", "paths", joined);
            if (result != null && Json.Bool(result, "needsConfirm"))
            {
                var dialog = new ContentDialog
                {
                    Title = "Import these files?",
                    Content = Json.Str(result, "message"),
                    PrimaryButtonText = "Import anyway",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    await RunCommandAsync("import", "paths", joined, "allowUnverified", "true");
            }
        }

        /// <summary>Sends a command, shows its message, and refreshes the view.</summary>
        /// <summary>
        /// Lets the helper know the widget was just opened, so it looks for a newer widget release (at most every 30
        /// minutes). Silent: no status message, and a failure doesn't matter.
        /// </summary>
        private static async Task TellHelperWidgetOpenedAsync()
        {
            try { await HostClient.SendAsync("widgetOpened"); }
            catch (Exception) { }
        }

        private async Task<JsonObject> RunCommandAsync(string cmd, params string[] pairs)
        {
            if (_busy) return null;
            _busy = true;

            // Disabling the buttons takes focus away from the one just pressed, and Windows scrolls to wherever focus
            // lands next (with a controller, often the bottom of the widget). Hold the scroll position and give focus
            // back afterwards.
            var focused = FocusManager.GetFocusedElement() as Control;
            var scroll = PageScroll.VerticalOffset;
            SetButtonsEnabled(false);
            StatusText.Text = "Working...";
            JsonObject result = null;
            try
            {
                result = await HostClient.SendAsync(cmd, pairs);
                StatusText.Text = Json.Str(result, "message");
            }
            finally
            {
                _busy = false;
                SetButtonsEnabled(true);
            }

            var message = StatusText.Text;
            await RefreshAsync(quiet: true);
            StatusText.Text = message;

            if (focused != null && focused.IsEnabled && focused.Visibility == Visibility.Visible)
                focused.Focus(FocusState.Programmatic);
            PageScroll.ChangeView(null, scroll, null, disableAnimation: true);
            return result;
        }

        /// <summary>While a command runs, ignore the scroll jumps that come from focus moving off disabled buttons.</summary>
        private void PageScroll_BringIntoViewRequested(UIElement sender, BringIntoViewRequestedEventArgs args)
        {
            if (_busy) args.Handled = true;
        }

        private void SetButtonsEnabled(bool on)
        {
            RefreshButton.IsEnabled = on;
            ImportButton.IsEnabled = on;
            OverlaySwitch.IsEnabled = on;
            OverlaySizeBox.IsEnabled = on;
            OverlayPositionBox.IsEnabled = on;
            NotifySwitch.IsEnabled = on;
            AutoUpdateSwitch.IsEnabled = on;
            UninstallButton.IsEnabled = on;
            ScaleChips.IsHitTestVisible = on;
            if (!on) PrimaryButton.IsEnabled = false;
        }

        private async Task RunOnUi(Func<Task> action)
        {
            var tcs = new TaskCompletionSource<bool>();
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                try { await action(); tcs.TrySetResult(true); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            await tcs.Task;
        }
    }
}
