using Playnite.SDK;
using Playnite.SDK.Data;
using System.Collections.Generic;

namespace XboxOwnedLibrary
{
    public class XboxOwnedLibrarySettings : ObservableObject
    {
        private bool _syncOnLibraryUpdate = true;
        private bool _importGamesWithGold = false;
        private bool _useStoreMetadata = false;
        private bool _addPlayAnywhereTag = false;
        private string _playAnywhereTagName = "Xbox Play Anywhere";
        private string _market = "US";
        private string _language = "en-US";
        private string _lastSync = string.Empty;

        /// <summary>Run after every Playnite library update.</summary>
        public bool SyncOnLibraryUpdate { get => _syncOnLibraryUpdate; set => SetValue(ref _syncOnLibraryUpdate, value); }

        /// <summary>Games with Gold grants only play while subscribed; tagged when imported.</summary>
        public bool ImportGamesWithGold { get => _importGamesWithGold; set => SetValue(ref _importGamesWithGold, value); }

        /// <summary>Fill details and artwork from the Microsoft Store instead of leaving them to Playnite's metadata download.</summary>
        public bool UseStoreMetadata { get => _useStoreMetadata; set => SetValue(ref _useStoreMetadata, value); }

        /// <summary>Tag owned Xbox Play Anywhere games, new and already in Playnite. Tags are only added, never removed.</summary>
        public bool AddPlayAnywhereTag { get => _addPlayAnywhereTag; set => SetValue(ref _addPlayAnywhereTag, value); }
        public string PlayAnywhereTagName { get => _playAnywhereTagName; set => SetValue(ref _playAnywhereTagName, value); }

        public string Market { get => _market; set => SetValue(ref _market, value); }
        public string Language { get => _language; set => SetValue(ref _language, value); }
        public string LastSync { get => _lastSync; set => SetValue(ref _lastSync, value); }
    }

    public class XboxOwnedLibrarySettingsViewModel : ObservableObject, ISettings
    {
        private readonly XboxOwnedLibrary _plugin;
        private XboxOwnedLibrarySettings _editingClone;
        private XboxOwnedLibrarySettings _settings;

        public XboxOwnedLibrarySettings Settings
        {
            get => _settings;
            set
            {
                _settings = value;
                OnPropertyChanged();
            }
        }

        public bool IsSignedIn => _plugin.Auth.HasSavedSignIn;
        public string SignInStatus => IsSignedIn ? "Signed in with xbox.com's app ID." : "Not signed in.";

        public RelayCommand SignInCommand { get; }
        public RelayCommand SignOutCommand { get; }

        public XboxOwnedLibrarySettingsViewModel(XboxOwnedLibrary plugin)
        {
            _plugin = plugin;
            Settings = _plugin.LoadPluginSettings<XboxOwnedLibrarySettings>() ?? new XboxOwnedLibrarySettings();
            SignInCommand = new RelayCommand(() => _plugin.SignIn());
            SignOutCommand = new RelayCommand(() => _plugin.SignOut());
        }

        public void RefreshSignInState()
        {
            OnPropertyChanged(nameof(IsSignedIn));
            OnPropertyChanged(nameof(SignInStatus));
        }

        public void BeginEdit()
        {
            _editingClone = Serialization.GetClone(Settings);
        }

        public void CancelEdit()
        {
            Settings = _editingClone;
        }

        public void EndEdit()
        {
            _plugin.SavePluginSettings(Settings);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            if (string.IsNullOrWhiteSpace(Settings.Market) || Settings.Market.Trim().Length != 2)
            {
                errors.Add("Market must be a two-letter country code, such as US.");
            }

            if (string.IsNullOrWhiteSpace(Settings.Language))
            {
                errors.Add("Language must be set, such as en-US.");
            }

            if (Settings.AddPlayAnywhereTag && string.IsNullOrWhiteSpace(Settings.PlayAnywhereTagName))
            {
                errors.Add("Enter a tag name for Play Anywhere games, or turn the option off.");
            }

            return errors.Count == 0;
        }
    }
}
