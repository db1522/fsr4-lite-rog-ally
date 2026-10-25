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
        private bool _suppressScaleEvent;
        private readonly List<string> _scaleKeys = new List<string>();
        private bool _busy;
        private int _installedCount;

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

            Window.Current.VisibilityChanged += async (s, a) =>
            {
                if (a.Visible) await RefreshAsync(quiet: true);
            };

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
                GameName.Text = "Helper not running";
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

            _suppressOverlayEvent = true;
            OverlaySwitch.IsOn = Json.Bool(state, "overlay");
            WatermarkSwitch.IsOn = Json.Bool(state, "watermark");
            _suppressOverlayEvent = false;

            var game = Json.Obj(state, "game");
            RenderGame(game);

            // Keep polling while downloading, or while an installed game isn't fully active yet / might change.
            var watchGame = game != null && Json.Bool(game, "installed");
            if (Json.Bool(opti, "busy") || fsr4Busy || !fsr4Ready || watchGame) _poll.Start(); else _poll.Stop();
            RenderInstalled(Json.Arr(state, "installed"));
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
                RenderScaleBox.Visibility = Visibility.Collapsed;
                ResolutionText.Visibility = Visibility.Collapsed;
                AutoFixBanner.Visibility = Visibility.Collapsed;
                return;
            }

            RenderBadge(Json.Str(game, "activeStage"), Json.Str(game, "activeText"));

            var resolution = Json.Str(game, "resolutionText");
            ResolutionText.Text = resolution;
            ResolutionText.Visibility = string.IsNullOrEmpty(resolution) ? Visibility.Collapsed : Visibility.Visible;

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
            GameActions.Visibility = Visibility.Visible;

            RenderScaleOptions(game);

            if (_gameInstalled)
            {
                PrimaryButton.Content = "Uninstall from this game";
                PrimaryButton.IsEnabled = true;
            }
            else
            {
                PrimaryButton.Content = "Install FSR 4.1 Lite";
                PrimaryButton.IsEnabled = Json.Bool(game, "canInstall") && _deviceSupported && _setupReady;
                if (Json.Bool(game, "canInstall") && !_setupReady)
                    GameStatus.Text += " Finish setup below to install.";
            }
        }

        /// <summary>Fills the render resolution picker for an installed game and selects its current choice.</summary>
        private void RenderScaleOptions(JsonObject game)
        {
            if (!Json.Bool(game, "installed"))
            {
                RenderScaleBox.Visibility = Visibility.Collapsed;
                return;
            }

            _suppressScaleEvent = true;
            try
            {
                var options = Json.Arr(game, "renderScales");
                if (RenderScaleBox.Items.Count != options.Count)
                {
                    RenderScaleBox.Items.Clear();
                    _scaleKeys.Clear();
                    foreach (var item in options)
                    {
                        if (item.ValueType != JsonValueType.Object) continue;
                        var o = item.GetObject();
                        _scaleKeys.Add(Json.Str(o, "key"));
                        RenderScaleBox.Items.Add(Json.Str(o, "label"));
                    }
                }

                var index = _scaleKeys.IndexOf(Json.Str(game, "renderScale", "game"));
                RenderScaleBox.SelectedIndex = index >= 0 ? index : 0;
                RenderScaleBox.Visibility = Visibility.Visible;
            }
            finally
            {
                _suppressScaleEvent = false;
            }
        }

        private async void RenderScale_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressScaleEvent || string.IsNullOrEmpty(_exeDir)) return;
            var index = RenderScaleBox.SelectedIndex;
            if (index < 0 || index >= _scaleKeys.Count) return;
            await RunCommandAsync("renderScale", "exeDir", _exeDir, "value", _scaleKeys[index]);
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
            ActiveBadge.BorderBrush = new SolidColorBrush(color);
            ActiveBadge.Background = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B));
            ActiveText.Text = text;
            ActiveBadge.Visibility = Visibility.Visible;
        }

        private void RenderInstalled(JsonArray installed)
        {
            var rows = new List<InstalledRow>();
            foreach (var item in installed)
            {
                if (item.ValueType != JsonValueType.Object) continue;
                var o = item.GetObject();
                rows.Add(new InstalledRow
                {
                    Name = Json.Str(o, "name"),
                    ExeDir = Json.Str(o, "exeDir"),
                    Note = Json.Bool(o, "running") ? "Running now, close it to uninstall" : "FSR 4.1 Lite installed",
                });
            }
            InstalledList.ItemsSource = rows;
            _installedCount = rows.Count;
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

        private async void Overlay_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressOverlayEvent) return;
            await RunCommandAsync("overlay", "on", OverlaySwitch.IsOn ? "true" : "false");
        }

        private async void Watermark_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressOverlayEvent) return;
            await RunCommandAsync("watermark", "on", WatermarkSwitch.IsOn ? "true" : "false");
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
        private async Task<JsonObject> RunCommandAsync(string cmd, params string[] pairs)
        {
            if (_busy) return null;
            _busy = true;
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
            return result;
        }

        private void SetButtonsEnabled(bool on)
        {
            RefreshButton.IsEnabled = on;
            ImportButton.IsEnabled = on;
            OverlaySwitch.IsEnabled = on;
            WatermarkSwitch.IsEnabled = on;
            UninstallButton.IsEnabled = on;
            RenderScaleBox.IsEnabled = on;
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
