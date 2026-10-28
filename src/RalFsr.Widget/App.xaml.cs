using System;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace RalFsr.Widget
{
    sealed partial class App : Application
    {
        /// <summary>Connection to the full-trust helper, set when it connects back to our app service.</summary>
        public static AppServiceConnection Connection { get; private set; }

        public static event EventHandler ConnectionChanged;

        private static BackgroundTaskDeferral _serviceDeferral;
        private XboxGameBarWidget _widget;

        public App()
        {
            InitializeComponent();

            UnhandledException += (s, e) =>
            {
                e.Handled = true; // keep the widget alive and show the error instead
                CrashLog.Write("unhandled", e.Exception);
                CrashLog.Show("unhandled", e.Exception);
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                CrashLog.Write("background task", e.Exception);
                e.SetObserved();
            };
        }

        /// <summary>Game Bar opens the widget through this activation.</summary>
        protected override void OnActivated(IActivatedEventArgs args)
        {
            var widgetArgs = args as XboxGameBarWidgetActivatedEventArgs;
            if (widgetArgs == null || !widgetArgs.IsLaunchActivation) return;

            try
            {
                var frame = new Frame();
                Window.Current.Content = frame;
                _widget = new XboxGameBarWidget(widgetArgs, Window.Current.CoreWindow, frame);
                frame.Navigate(typeof(WidgetPage), _widget);
                Window.Current.Closed += (s, e) => { _widget = null; };
                Window.Current.Activate();
            }
            catch (Exception ex)
            {
                CrashLog.Write("widget start", ex);
                CrashLog.Show("widget start", ex);
            }
        }

        /// <summary>Opened from the Start menu instead of Game Bar: show the same page as a normal window.</summary>
        /// <summary>
        /// Launched as a normal app (Start menu, or Windows relaunching it after an update) rather than by Game Bar. The
        /// widget only makes sense in Game Bar, so never show a full-screen window: right after an update just close, and
        /// otherwise open Game Bar, where the widget lives.
        /// </summary>
        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            var justUpdated = false;
            try
            {
                justUpdated = DateTimeOffset.Now - Windows.ApplicationModel.Package.Current.InstalledDate < TimeSpan.FromMinutes(3);
            }
            catch (Exception)
            {
            }

            if (!justUpdated)
            {
                try { await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-gamebar:")); }
                catch (Exception) { }
            }
            Current.Exit();
        }

        /// <summary>The helper connecting to the in-process app service lands here.</summary>
        protected override void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            base.OnBackgroundActivated(args);
            var details = args.TaskInstance.TriggerDetails as AppServiceTriggerDetails;
            if (details == null) return;

            _serviceDeferral = args.TaskInstance.GetDeferral();
            args.TaskInstance.Canceled += OnServiceCanceled;
            Connection = details.AppServiceConnection;
            ConnectionChanged?.Invoke(null, EventArgs.Empty);
        }

        private void OnServiceCanceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
        {
            Connection = null;
            var deferral = _serviceDeferral;
            _serviceDeferral = null;
            deferral?.Complete();
            ConnectionChanged?.Invoke(null, EventArgs.Empty);
        }
    }
}
