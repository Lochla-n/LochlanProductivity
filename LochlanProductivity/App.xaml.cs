using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using System;

namespace LochlanProductivity
{
    public partial class App : Application
    {
        private Window? m_window;

        public static App? CurrentApp { get; private set; }

        public App()
        {
            InitializeComponent();

            CurrentApp = this;
        }

        protected override void OnLaunched(
            LaunchActivatedEventArgs args)
        {
            m_window = new MainWindow();

            m_window.AppWindow.Closing +=
                AppWindow_Closing;

            m_window.Activate();
        }

        private void AppWindow_Closing(
            AppWindow sender,
            AppWindowClosingEventArgs args)
        {
            // Don't allow the normal window close to terminate
            // the productivity blocker.
            //
            // The application stays alive in the background so
            // blocking enforcement continues.

            args.Cancel = true;

            try
            {
                sender.Hide();
            }
            catch
            {
            }
        }

        public void ExitApplication()
        {
            if (m_window == null)
                return;

            try
            {
                m_window.AppWindow.Closing -=
                    AppWindow_Closing;

                m_window.Close();
            }
            catch
            {
            }
        }

        public Window? MainWindow =>
            m_window;
    }
}