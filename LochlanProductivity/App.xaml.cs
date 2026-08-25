using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using System;
using System.Threading;

namespace LochlanProductivity
{
    public partial class App : Application
    {
        private Window? m_window;

        // Prevents two instances from both enforcing blocking
        // (double process kills, conflicting timers, double saves).
        private Mutex? singleInstanceMutex;

        public static App? CurrentApp { get; private set; }

        public App()
        {
            InitializeComponent();

            CurrentApp = this;
        }

        protected override void OnLaunched(
            LaunchActivatedEventArgs args)
        {
            // Elevated hosts-file helper mode: swap the staged file
            // into place and exit. Runs before the single-instance
            // mutex so it works while the main instance is alive.
            string[] cliArguments =
                Environment.GetCommandLineArgs();

            for (int i = 0; i < cliArguments.Length - 1; i++)
            {
                if (cliArguments[i].Equals(
                    "--lp-hostsfile",
                    StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Services.HostsFileBlocker.PerformStagedSwap(
                            cliArguments[i + 1]);
                    }
                    catch
                    {
                    }

                    this.Exit();
                    return;
                }
            }

            singleInstanceMutex =
                new Mutex(
                    true,
                    @"Local\LochlanProductivity.SingleInstance",
                    out bool createdNew);

            if (!createdNew)
            {
                // Another instance is already running and
                // enforcing blocking. Relaunching the exe must
                // not create a second blocker.
                this.Exit();
                return;
            }

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

        public void ReleaseSingleInstanceLock()
        {
            try
            {
                singleInstanceMutex?.ReleaseMutex();
            }
            catch
            {
            }

            try
            {
                singleInstanceMutex?.Dispose();
            }
            catch
            {
            }

            singleInstanceMutex = null;
        }

        public Window? MainWindow =>
            m_window;
    }
}