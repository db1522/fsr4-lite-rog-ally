using System;
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
        }

        /// <summary>Game Bar opens the widget through this activation.</summary>
        protected override void OnActivated(IActivatedEventArgs args)
        {
            var widgetArgs = args as XboxGameBarWidgetActivatedEventArgs;
            if (widgetArgs == null || !widgetArgs.IsLaunchActivation) return;

            var frame = new Frame();
            Window.Current.Content = frame;
            _widget = new XboxGameBarWidget(widgetArgs, Window.Current.CoreWindow, frame);
            frame.Navigate(typeof(WidgetPage), _widget);
            Window.Current.Closed += (s, e) => { _widget = null; };
            Window.Current.Activate();
        }

        /// <summary>Opened from the Start menu instead of Game Bar: show the same page as a normal window.</summary>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            var frame = Window.Current.Content as Frame;
            if (frame == null)
            {
                frame = new Frame();
                Window.Current.Content = frame;
            }
            if (frame.Content == null) frame.Navigate(typeof(WidgetPage), null);
            Window.Current.Activate();
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
