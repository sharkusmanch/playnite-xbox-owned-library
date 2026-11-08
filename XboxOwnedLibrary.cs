using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using XboxOwnedLibrary.Services;

namespace XboxOwnedLibrary
{
    /// <summary>
    /// A library plugin only so Playnite treats its imports as new library games
    /// (metadata download, sorting names). The games themselves are imported under
    /// the built-in Xbox plugin, which installs and launches them.
    /// </summary>
    public class XboxOwnedLibrary : LibraryPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string NotificationId = "XboxOwnedLibrary_sync";
        private static readonly HttpClient _http;
        private readonly SemaphoreSlim _syncLock = new SemaphoreSlim(1, 1);

        internal XboxOwnedLibrarySettingsViewModel SettingsVm { get; }
        internal XboxAuth Auth { get; }

        public override Guid Id { get; } = Guid.Parse("2c8730ad-93a0-4a6d-9657-787afede3219");

        public override string Name => "Xbox Owned Library";

        private string ReportPath => Path.Combine(GetPluginUserDataPath(), "last-sync.txt");

        static XboxOwnedLibrary()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Playnite-XboxOwnedLibrary/1.0");
        }

        public XboxOwnedLibrary(IPlayniteAPI api) : base(api)
        {
            Auth = new XboxAuth(_http, GetPluginUserDataPath());
            SettingsVm = new XboxOwnedLibrarySettingsViewModel(this);
            Properties = new LibraryPluginProperties { HasSettings = true, HasCustomizedGameImport = true };
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            // Adding games goes through Playnite's library update (Update library >
            // Xbox Owned Library) so new games get metadata; only the preview is here.
            const string section = "@Xbox Owned Library";
            yield return new MainMenuItem
            {
                MenuSection = section,
                Description = "Preview owned Xbox PC games (no changes)",
                Action = _ => RunSyncWithProgress(true)
            };
            yield return new MainMenuItem
            {
                MenuSection = section,
                Description = "Open last sync report",
                Action = _ =>
                {
                    if (File.Exists(ReportPath))
                    {
                        Process.Start(ReportPath);
                    }
                    else
                    {
                        PlayniteApi.Dialogs.ShowMessage("No sync has run yet.", "Xbox Owned Library");
                    }
                }
            };
        }

        /// <summary>
        /// Runs inside Playnite's library update (on its worker thread). The returned
        /// games count as newly added, so Playnite downloads their metadata.
        /// </summary>
        public override IEnumerable<Game> ImportGames(LibraryImportGamesArgs args)
        {
            if (!SettingsVm.Settings.SyncOnLibraryUpdate)
            {
                return new List<Game>();
            }

            if (!Auth.HasSavedSignIn)
            {
                logger.Info("Not signed in; skipping owned Xbox games sync.");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    NotificationId,
                    "Xbox Owned Library: sign in from its extension settings to add owned Xbox PC games you haven't played.",
                    NotificationType.Info,
                    () => OpenSettingsView()));
                return new List<Game>();
            }

            try
            {
                var result = RunSyncAsync(false).GetAwaiter().GetResult();
                PlayniteApi.Notifications.Remove(NotificationId);
                return result?.ImportedGames ?? new List<Game>();
            }
            catch (Exception e)
            {
                logger.Error(e, "Owned Xbox games sync failed.");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    NotificationId,
                    "Xbox Owned Library sync failed: " + e.Message,
                    NotificationType.Error));
                return new List<Game>();
            }
        }

        private async Task<SyncResult> RunSyncAsync(bool dryRun)
        {
            if (!await _syncLock.WaitAsync(0))
            {
                logger.Info("Owned Xbox games sync already running; skipping.");
                return null;
            }

            try
            {
                var sync = new OwnedGamesSync(PlayniteApi, Auth, _http, SettingsVm.Settings);
                var result = await sync.RunAsync(dryRun, ReportPath);
                logger.Info(result.Summary(dryRun));
                if (!dryRun)
                {
                    SettingsVm.Settings.LastSync = $"{DateTime.Now:yyyy-MM-dd HH:mm}: {result.Summary(false)}";
                    SavePluginSettings(SettingsVm.Settings);
                }

                return result;
            }
            finally
            {
                _syncLock.Release();
            }
        }

        private void RunSyncWithProgress(bool dryRun)
        {
            if (!Auth.HasSavedSignIn)
            {
                PlayniteApi.Dialogs.ShowMessage("Sign in first: Add-ons > Extension settings > Libraries > Xbox Owned Library.", "Xbox Owned Library");
                return;
            }

            SyncResult result = null;
            Exception error = null;
            PlayniteApi.Dialogs.ActivateGlobalProgress(_ =>
            {
                try
                {
                    result = RunSyncAsync(dryRun).GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    error = e;
                }
            }, new GlobalProgressOptions(dryRun ? "Reading your owned Xbox games..." : "Adding your owned Xbox PC games...", false) { IsIndeterminate = true });

            if (error != null)
            {
                logger.Error(error, "Owned Xbox games sync failed.");
                PlayniteApi.Dialogs.ShowErrorMessage(error.Message, "Xbox Owned Library");
                return;
            }

            if (result == null)
            {
                PlayniteApi.Dialogs.ShowMessage("A sync is already running.", "Xbox Owned Library");
                return;
            }

            var open = PlayniteApi.Dialogs.ShowMessage(result.Summary(dryRun) + "\n\nOpen the full report?", "Xbox Owned Library", MessageBoxButton.YesNo);
            if (open == MessageBoxResult.Yes)
            {
                Process.Start(ReportPath);
            }
        }

        internal void SignIn()
        {
            logger.Info("Sign-in started.");
            XboxAuth.DeviceCode code;
            try
            {
                // Run off the UI thread: blocking the dispatcher on an HTTP await deadlocks.
                code = Task.Run(() => Auth.StartDeviceCodeAsync()).GetAwaiter().GetResult();
                logger.Info($"Device code issued; opening {code.verification_uri}.");
            }
            catch (Exception e)
            {
                logger.Error(e, "Device code request failed.");
                PlayniteApi.Dialogs.ShowErrorMessage(e.Message, "Xbox Owned Library");
                return;
            }

            try
            {
                Clipboard.SetText(code.user_code);
            }
            catch
            {
                // The clipboard can be locked by another app; the code is shown anyway.
            }

            Process.Start(code.LinkWithCode);

            Exception error = null;
            var progress = PlayniteApi.Dialogs.ActivateGlobalProgress(args =>
            {
                try
                {
                    Auth.CompleteDeviceCodeAsync(code, args.CancelToken).GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    error = e;
                }
            }, new GlobalProgressOptions(
                $"Approve the sign-in in your browser.\nIf it did not open, go to {code.verification_uri} and enter {code.user_code} (copied to the clipboard).",
                true)
            { IsIndeterminate = true });

            if (progress.Canceled || error is OperationCanceledException)
            {
                return;
            }

            if (error != null)
            {
                logger.Error(error, "Device code sign-in failed.");
                PlayniteApi.Dialogs.ShowErrorMessage(error.Message, "Xbox Owned Library");
                return;
            }

            logger.Info("Sign-in completed.");
            SettingsVm.RefreshSignInState();
            PlayniteApi.Dialogs.ShowMessage("Signed in. Preview from Main menu > Extensions > Xbox Owned Library, or add your owned games with Update library > Xbox Owned Library.", "Xbox Owned Library");
        }

        internal void SignOut()
        {
            Auth.SignOut();
            SettingsVm.RefreshSignInState();
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return SettingsVm;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new XboxOwnedLibrarySettingsView();
        }
    }
}
