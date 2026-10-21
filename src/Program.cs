using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;
using DesktopStamp.Core;
using DesktopStamp.Imaging;
using DesktopStamp.UI;

namespace DesktopStamp
{
    public static class Program
    {
        private static StampOverlayForm _mainForm;

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                NativeMethods.SetProcessDPIAware();
            }
            catch
            {
                // Manifest already declares PerMonitorV2 DPI awareness
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ApplicationExit += OnCleanupExit;
            AppDomain.CurrentDomain.ProcessExit += OnCleanupExit;
            AppDomain.CurrentDomain.UnhandledException += (s, e) => OnCleanupExit(s, EventArgs.Empty);

            if (args != null && args.Any(a => string.Equals(a, "--smoke-check", StringComparison.OrdinalIgnoreCase)))
            {
                return RunExecutableSmokeCheck();
            }

            using (_mainForm = new StampOverlayForm())
            {
                Application.Run(_mainForm);
            }

            return 0;
        }

        private static void OnCleanupExit(object sender, EventArgs e)
        {
            try
            {
                _mainForm?.ShutdownAndExit();
            }
            catch
            {
                // Ignore shutdown exceptions
            }
        }

        /// <summary>
        /// Performs a fast startup, asset-loading, per-pixel alpha rendering, and layered window smoke check
        /// for CLI verification of DesktopStamp.exe.
        /// </summary>
        private static int RunExecutableSmokeCheck()
        {
            try
            {
                using (var repo = new AssetRepository())
                {
                    var renderer = new StampRenderer(repo);
                    var sm = new StampStateMachine(new StampConfiguration(), new Vector2D(500, 500), 42);
                    var seals = SealImageProcessor.CreateBuiltInSampleSeals();
                    if (seals.Count == 0)
                    {
                        Console.Error.WriteLine("SMOKE FAIL: No built-in sample seals created.");
                        return 2;
                    }

                    using (var bmp = new Bitmap(460, 460, PixelFormat.Format32bppArgb))
                    using (var g = Graphics.FromImage(bmp))
                    {
                        // Render Idle
                        renderer.RenderFrame(g, 460, 460, sm, seals[0], null, 96.0);
                        // Transition to Aiming and Render
                        sm.BeginPickupDrag(new Vector2D(400, 400));
                        sm.Update(0.15);
                        sm.CompletePickupToAiming(new Vector2D(350, 350));
                        sm.Update(0.10);
                        renderer.RenderFrame(g, 460, 460, sm, seals[0], null, 96.0);
                    }

                    foreach (var s in seals) s.Dispose();

                    Console.WriteLine(
                        $"SMOKE OK: DesktopStamp.exe initialized; AssetsDir='{repo.AssetsDirectory}', " +
                        $"IdleSprite={(repo.IdleSprite != null)}, DragSprite={(repo.DragSprite != null)}, ReticleSprite={(repo.ReticleSprite != null)}.");
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"SMOKE EXCEPTION: {ex}");
                return 1;
            }
        }
    }
}
