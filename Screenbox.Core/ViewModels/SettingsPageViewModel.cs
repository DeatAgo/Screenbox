using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Contexts;
using Screenbox.Core.Coordinators;
using Screenbox.Core.Enums;
using Screenbox.Core.Helpers;
using Screenbox.Core.Messages;
using Screenbox.Core.Services;
using Windows.Devices.Enumeration;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.System;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace Screenbox.Core.ViewModels;

public sealed partial class SettingsPageViewModel : ObservableRecipient
{
    private const string PropertiesDockModeKey = "Player.PropertiesDockMode";

    public PlayerSettingsContext PlayerSettings { get; }
    public LibrarySettingsContext LibrarySettings { get; }
    public PlaybackSettingsContext PlaybackSettings { get; }
    public GeneralSettingsContext GeneralSettings { get; }

    [ObservableProperty] public partial string Language { get; set; }
    [ObservableProperty] public partial bool ShowRecent { get; set; }
    [ObservableProperty] public partial bool SearchRemovableStorage { get; set; }
    [ObservableProperty] public partial bool AdvancedMode { get; set; }
    [ObservableProperty] public partial int VideoUpscaling { get; set; }
    [ObservableProperty] public partial string GlobalArguments { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsRelaunchRequired { get; set; }
    [ObservableProperty] public partial StorageFolder? FrameCaptureFolder { get; set; }
    [ObservableProperty] public partial int PropertiesDockMode { get; set; }

    public ObservableCollection<StorageFolder> MusicLocations { get; }

    public ObservableCollection<StorageFolder> VideoLocations { get; }

    public ObservableCollection<StorageFolder> RemovableStorageFolders { get; }

    public IReadOnlyList<PlayerAutoResizeOption> PlayerAutoResizeOptions { get; } = Enum.GetValues<PlayerAutoResizeOption>();

    public IReadOnlyList<int> VolumeBoostOptions { get; } = _volumeOptions;

    public IReadOnlyList<int> PlayerSeekStepOptions { get; } = new[] { 5, 10, 15, 20, 30 };

    public IReadOnlyList<int> PlayerControlsHideDelayOptions { get; } = new[] { 1, 2, 3, 4, 5 };

    public IReadOnlyList<PlaybackActionKind> GestureOptions { get; } = Enum.GetValues<PlaybackActionKind>();

    public IReadOnlyList<string> SubtitleFontOptions { get; } = SystemFontHelper.GetInstalledFontFamilies();

    public IReadOnlyList<string> SubtitleLanguageOptions { get; } = new[]
    {
        "Default",
        "None",
        "English (en)",
        "Japanese (ja)",
        "Chinese (zh)",
        "Korean (ko)",
        "Spanish (es)",
        "French (fr)",
        "German (de)",
        "Portuguese (pt)",
        "Russian (ru)",
        "Arabic (ar)",
        "Hindi (hi)",
        "Vietnamese (vi)",
        "Thai (th)",
        "Indonesian (id)",
        "Custom..."
    };

    private static readonly string[] SubtitleLanguageModeValues =
    [
        "default",
        "none",
        "en",
        "ja",
        "zh",
        "ko",
        "es",
        "fr",
        "de",
        "pt",
        "ru",
        "ar",
        "hi",
        "vi",
        "th",
        "id",
        "custom"
    ];

    private static readonly int[] _volumeOptions = [100, 125, 150, 200];

    private readonly IFilesService _filesService;
    private readonly LibraryContext _libraryContext;
    private readonly RecentContext _recentContext;
    private readonly ILibraryCoordinator _libraryCoordinator;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _storageDeviceRefreshTimer;
    private readonly DeviceWatcher? _portableStorageDeviceWatcher;
    private readonly IPlaybackProgressTracker _playbackProgressTracker;
    private readonly ILogger<SettingsPageViewModel> _logger;
    private static InitialValues? _initialValues;
    private StorageLibrary? _videosLibrary;
    private StorageLibrary? _musicLibrary;

    private record InitialValues(string GlobalArguments, bool AdvancedMode, int VideoUpscaling, string Language)
    {
        public string GlobalArguments { get; } = GlobalArguments;
        public bool AdvancedMode { get; } = AdvancedMode;
        public int VideoUpscaling { get; } = VideoUpscaling;
        public string Language { get; } = Language;
    }

    public SettingsPageViewModel(
        PlayerSettingsContext playerSettings,
        LibrarySettingsContext librarySettings,
        PlaybackSettingsContext playbackSettings,
        GeneralSettingsContext generalSettings,
        IFilesService filesService,
        LibraryContext libraryContext,
        RecentContext recentContext,
        ILibraryCoordinator libraryCoordinator,
        IPlaybackProgressTracker playbackProgressTracker,
        ILogger<SettingsPageViewModel> logger)
    {
        PlayerSettings = playerSettings;
        LibrarySettings = librarySettings;
        PlaybackSettings = playbackSettings;
        GeneralSettings = generalSettings;
        _filesService = filesService;
        _libraryContext = libraryContext;
        _recentContext = recentContext;
        _libraryCoordinator = libraryCoordinator;
        _playbackProgressTracker = playbackProgressTracker;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _storageDeviceRefreshTimer = _dispatcherQueue.CreateTimer();
        MusicLocations = new ObservableCollection<StorageFolder>();
        VideoLocations = new ObservableCollection<StorageFolder>();
        RemovableStorageFolders = new ObservableCollection<StorageFolder>();

        if (SystemInformation.IsXbox)
        {
            _portableStorageDeviceWatcher = DeviceInformation.CreateWatcher(DeviceClass.PortableStorageDevice);
            _portableStorageDeviceWatcher.Updated += OnPortableStorageDeviceChanged;
            _portableStorageDeviceWatcher.Removed += OnPortableStorageDeviceChanged;
            _portableStorageDeviceWatcher.Start();
        }

        // Load values
        Language = GeneralSettings.Language;
        ShowRecent = GeneralSettings.ShowRecent;
        SearchRemovableStorage = LibrarySettings.SearchRemovableStorage;
        AdvancedMode = PlayerSettings.AdvancedMode;
        VideoUpscaling = (int)PlayerSettings.VideoUpscale;
        GlobalArguments = PlayerSettings.GlobalArguments;
        PropertiesDockMode = GetPropertiesDockMode() switch
        {
            Core.Enums.PropertiesDockMode.Left => 1,
            Core.Enums.PropertiesDockMode.Right => 2,
            _ => 0
        };

        // Setting initial values for relaunch check
        _initialValues ??= new InitialValues(GlobalArguments, AdvancedMode, VideoUpscaling, Language);
        CheckForRelaunch();
    }

    public static PropertiesDockMode GetPropertiesDockMode()
    {
        if (ApplicationData.Current.LocalSettings.Values.TryGetValue(PropertiesDockModeKey, out object? raw) &&
            raw is string modeName &&
            Enum.TryParse<PropertiesDockMode>(modeName, out var parsed))
        {
            return parsed;
        }

        return Core.Enums.PropertiesDockMode.Floating;
    }

    public static void SetPropertiesDockMode(PropertiesDockMode mode)
    {
        ApplicationData.Current.LocalSettings.Values[PropertiesDockModeKey] = mode.ToString();
    }

    partial void OnPropertiesDockModeChanged(int value)
    {
        PropertiesDockMode mode = value switch
        {
            1 => Core.Enums.PropertiesDockMode.Left,
            2 => Core.Enums.PropertiesDockMode.Right,
            _ => Core.Enums.PropertiesDockMode.Floating
        };
        SetPropertiesDockMode(mode);
    }

    public int SelectedSubtitleLanguageIndex
    {
        get
        {
            int idx = Array.IndexOf(SubtitleLanguageModeValues, PlayerSettings.SubtitlePreferredLanguageMode);
            return idx >= 0 ? idx : 0;
        }
        set
        {
            if (value < 0 || value >= SubtitleLanguageModeValues.Length) return;
            string mode = SubtitleLanguageModeValues[value];
            if (PlayerSettings.SubtitlePreferredLanguageMode == mode) return;
            PlayerSettings.SubtitlePreferredLanguageMode = mode;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustomSubtitleLanguageVisible));
        }
    }

    public Visibility IsCustomSubtitleLanguageVisible =>
        string.Equals(PlayerSettings.SubtitlePreferredLanguageMode, "custom", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string SubtitlePreferredLanguageCode
    {
        get => PlayerSettings.SubtitlePreferredLanguageCode;
        set
        {
            string trimmed = value?.Trim() ?? string.Empty;
            if (PlayerSettings.SubtitlePreferredLanguageCode == trimmed) return;
            PlayerSettings.SubtitlePreferredLanguageCode = trimmed;
            OnPropertyChanged();
        }
    }

    public bool SubtitleAllowEmbeddedFormatting
    {
        get => PlayerSettings.SubtitleAllowEmbeddedFormatting;
        set
        {
            if (PlayerSettings.SubtitleAllowEmbeddedFormatting == value) return;
            PlayerSettings.SubtitleAllowEmbeddedFormatting = value;
            OnPropertyChanged();
        }
    }

    public string SubtitleFontFamily
    {
        get => PlayerSettings.SubtitleFontFamily;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || PlayerSettings.SubtitleFontFamily == value) return;
            PlayerSettings.SubtitleFontFamily = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PreviewFontFamily));
        }
    }

    public double SubtitleTextScale
    {
        get => Math.Clamp(PlayerSettings.SubtitleTextScale, 50, 250);
        set
        {
            int scale = Math.Clamp((int)Math.Round(value), 50, 250);
            if (PlayerSettings.SubtitleTextScale == scale) return;
            PlayerSettings.SubtitleTextScale = scale;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubtitleTextScaleLabel));
            OnPropertyChanged(nameof(PreviewFontSize));
        }
    }

    public string SubtitleTextScaleLabel => $"{Math.Clamp(PlayerSettings.SubtitleTextScale, 50, 250)}%";

    public double SubtitleMargin
    {
        get => Math.Clamp(PlayerSettings.SubtitleMargin, 0, 200);
        set
        {
            int margin = Math.Clamp((int)Math.Round(value), 0, 200);
            if (PlayerSettings.SubtitleMargin == margin) return;
            PlayerSettings.SubtitleMargin = margin;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubtitleMarginLabel));
            OnPropertyChanged(nameof(PreviewSubtitleMargin));
        }
    }

    public string SubtitleMarginLabel => $"{Math.Clamp(PlayerSettings.SubtitleMargin, 0, 200)} px";

    public int SubtitleAlignment
    {
        get => Math.Clamp(PlayerSettings.SubtitleAlignment, 0, 2);
        set
        {
            int align = Math.Clamp(value, 0, 2);
            if (PlayerSettings.SubtitleAlignment == align) return;
            PlayerSettings.SubtitleAlignment = align;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PreviewHorizontalAlignment));
            OnPropertyChanged(nameof(PreviewTextAlignment));
        }
    }

    public HorizontalAlignment PreviewHorizontalAlignment => PlayerSettings.SubtitleAlignment switch
    {
        1 => HorizontalAlignment.Left,
        2 => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Center
    };

    public TextAlignment PreviewTextAlignment => PlayerSettings.SubtitleAlignment switch
    {
        1 => TextAlignment.Left,
        2 => TextAlignment.Right,
        _ => TextAlignment.Center
    };

    public Color SubtitleTextColor
    {
        get => RgbToColor(PlayerSettings.SubtitleColor, 255);
        set
        {
            int rgb = ColorToRgb(value);
            if (PlayerSettings.SubtitleColor == rgb) return;
            PlayerSettings.SubtitleColor = rgb;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubtitleTextSwatchBrush));
            OnPropertyChanged(nameof(PreviewForegroundBrush));
        }
    }

    public SolidColorBrush SubtitleTextSwatchBrush => new(RgbToColor(PlayerSettings.SubtitleColor, 255));

    public double SubtitleTextOpacityPercent
    {
        get => Math.Round(Math.Clamp(PlayerSettings.SubtitleOpacity, 0, 255) * 100.0 / 255.0);
        set
        {
            int alpha = Math.Clamp((int)Math.Round(value * 255.0 / 100.0), 0, 255);
            if (PlayerSettings.SubtitleOpacity == alpha) return;
            PlayerSettings.SubtitleOpacity = alpha;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubtitleTextOpacityLabel));
            OnPropertyChanged(nameof(PreviewForegroundBrush));
        }
    }

    public string SubtitleTextOpacityLabel => $"{(int)SubtitleTextOpacityPercent}%";

    public Color SubtitleBackgroundColor
    {
        get => RgbToColor(PlayerSettings.SubtitleBackgroundColor, 255);
        set
        {
            int rgb = ColorToRgb(value);
            if (PlayerSettings.SubtitleBackgroundColor == rgb) return;
            PlayerSettings.SubtitleBackgroundColor = rgb;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubtitleBackgroundSwatchBrush));
            OnPropertyChanged(nameof(PreviewBackgroundBrush));
        }
    }

    public SolidColorBrush SubtitleBackgroundSwatchBrush => new(RgbToColor(PlayerSettings.SubtitleBackgroundColor, 255));

    public double SubtitleBackgroundOpacityPercent
    {
        get => Math.Round(Math.Clamp(PlayerSettings.SubtitleBackgroundOpacity, 0, 255) * 100.0 / 255.0);
        set
        {
            int alpha = Math.Clamp((int)Math.Round(value * 255.0 / 100.0), 0, 255);
            if (PlayerSettings.SubtitleBackgroundOpacity == alpha) return;
            PlayerSettings.SubtitleBackgroundOpacity = alpha;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubtitleBackgroundOpacityLabel));
            OnPropertyChanged(nameof(PreviewBackgroundBrush));
        }
    }

    public string SubtitleBackgroundOpacityLabel => $"{(int)SubtitleBackgroundOpacityPercent}%";

    public double SubtitleOutlineThickness
    {
        get => Math.Clamp(PlayerSettings.SubtitleOutlineThickness, 0, 6);
        set
        {
            int thickness = Math.Clamp((int)Math.Round(value), 0, 6);
            if (PlayerSettings.SubtitleOutlineThickness == thickness) return;
            PlayerSettings.SubtitleOutlineThickness = thickness;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubtitleOutlineThicknessLabel));
            OnPropertyChanged(nameof(PreviewOutlineVisibility));
            OnPropertyChanged(nameof(PreviewOutlineMarginLeft));
            OnPropertyChanged(nameof(PreviewOutlineMarginRight));
            OnPropertyChanged(nameof(PreviewOutlineMarginTop));
            OnPropertyChanged(nameof(PreviewOutlineMarginBottom));
        }
    }

    public string SubtitleOutlineThicknessLabel => PlayerSettings.SubtitleOutlineThickness switch
    {
        0 => "None",
        <= 2 => $"Thin ({PlayerSettings.SubtitleOutlineThickness})",
        <= 4 => $"Medium ({PlayerSettings.SubtitleOutlineThickness})",
        _ => $"Thick ({PlayerSettings.SubtitleOutlineThickness})"
    };

    public Color SubtitleOutlineColor
    {
        get => RgbToColor(PlayerSettings.SubtitleOutlineColor, 255);
        set
        {
            int rgb = ColorToRgb(value);
            if (PlayerSettings.SubtitleOutlineColor == rgb) return;
            PlayerSettings.SubtitleOutlineColor = rgb;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubtitleOutlineSwatchBrush));
            OnPropertyChanged(nameof(PreviewOutlineBrush));
        }
    }

    public SolidColorBrush SubtitleOutlineSwatchBrush => new(RgbToColor(PlayerSettings.SubtitleOutlineColor, 255));

    public double SubtitleOutlineOpacityPercent
    {
        get => Math.Round(Math.Clamp(PlayerSettings.SubtitleOutlineOpacity, 0, 255) * 100.0 / 255.0);
        set
        {
            int alpha = Math.Clamp((int)Math.Round(value * 255.0 / 100.0), 0, 255);
            if (PlayerSettings.SubtitleOutlineOpacity == alpha) return;
            PlayerSettings.SubtitleOutlineOpacity = alpha;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubtitleOutlineOpacityLabel));
            OnPropertyChanged(nameof(PreviewOutlineBrush));
        }
    }

    public string SubtitleOutlineOpacityLabel => $"{(int)SubtitleOutlineOpacityPercent}%";

    public bool SubtitleShadow
    {
        get => PlayerSettings.SubtitleShadow;
        set
        {
            if (PlayerSettings.SubtitleShadow == value) return;
            PlayerSettings.SubtitleShadow = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PreviewShadowVisibility));
        }
    }

    public Color SubtitleShadowColor
    {
        get => RgbToColor(PlayerSettings.SubtitleShadowColor, 255);
        set
        {
            int rgb = ColorToRgb(value);
            if (PlayerSettings.SubtitleShadowColor == rgb) return;
            PlayerSettings.SubtitleShadowColor = rgb;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubtitleShadowSwatchBrush));
            OnPropertyChanged(nameof(PreviewShadowBrush));
        }
    }

    public SolidColorBrush SubtitleShadowSwatchBrush => new(RgbToColor(PlayerSettings.SubtitleShadowColor, 255));

    public double SubtitleShadowAngle
    {
        get => Math.Clamp(PlayerSettings.SubtitleShadowAngle, 0, 360);
        set
        {
            int angle = Math.Clamp((int)Math.Round(value), 0, 360);
            if (PlayerSettings.SubtitleShadowAngle == angle) return;
            PlayerSettings.SubtitleShadowAngle = angle;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubtitleShadowAngleLabel));
            OnPropertyChanged(nameof(PreviewShadowMargin));
        }
    }

    public string SubtitleShadowAngleLabel => $"{Math.Clamp(PlayerSettings.SubtitleShadowAngle, 0, 360)}°";

    public double SubtitleShadowDistance
    {
        get => Math.Clamp(PlayerSettings.SubtitleShadowDistance, 0, 20);
        set
        {
            int distance = Math.Clamp((int)Math.Round(value), 0, 20);
            if (PlayerSettings.SubtitleShadowDistance == distance) return;
            PlayerSettings.SubtitleShadowDistance = distance;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubtitleShadowDistanceLabel));
            OnPropertyChanged(nameof(PreviewShadowMargin));
        }
    }

    public string SubtitleShadowDistanceLabel => $"{Math.Clamp(PlayerSettings.SubtitleShadowDistance, 0, 20) / 100.0:0.00}";

    public bool SubtitleBold
    {
        get => PlayerSettings.SubtitleBold;
        set
        {
            if (PlayerSettings.SubtitleBold == value) return;
            PlayerSettings.SubtitleBold = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PreviewFontWeight));
        }
    }

    public Thickness PreviewSubtitleMargin => new(0, 0, 0, Math.Clamp(PlayerSettings.SubtitleMargin, 0, 200) * 0.25);

    public FontFamily PreviewFontFamily
    {
        get
        {
            try
            {
                return new FontFamily(PlayerSettings.SubtitleFontFamily);
            }
            catch
            {
                return new FontFamily("Segoe UI");
            }
        }
    }

    public double PreviewFontSize => 14.0 * (Math.Clamp(PlayerSettings.SubtitleTextScale, 50, 250) / 100.0);

    public SolidColorBrush PreviewForegroundBrush =>
        new(RgbToColor(PlayerSettings.SubtitleColor, (byte)Math.Clamp(PlayerSettings.SubtitleOpacity, 0, 255)));

    public SolidColorBrush PreviewBackgroundBrush =>
        new(RgbToColor(PlayerSettings.SubtitleBackgroundColor, (byte)Math.Clamp(PlayerSettings.SubtitleBackgroundOpacity, 0, 255)));

    public SolidColorBrush PreviewOutlineBrush =>
        new(RgbToColor(PlayerSettings.SubtitleOutlineColor, (byte)Math.Clamp(PlayerSettings.SubtitleOutlineOpacity, 0, 255)));

    public SolidColorBrush PreviewShadowBrush =>
        new(RgbToColor(PlayerSettings.SubtitleShadowColor, 180));

    public Visibility PreviewOutlineVisibility =>
        PlayerSettings.SubtitleOutlineThickness > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PreviewShadowVisibility =>
        PlayerSettings.SubtitleShadow ? Visibility.Visible : Visibility.Collapsed;

    public Thickness PreviewShadowMargin
    {
        get
        {
            double radians = Math.Clamp(PlayerSettings.SubtitleShadowAngle, 0, 360) * Math.PI / 180.0;
            double dist = Math.Clamp(PlayerSettings.SubtitleShadowDistance, 0, 20) * 0.35;
            double dx = Math.Cos(radians) * dist;
            double dy = -Math.Sin(radians) * dist;
            return new Thickness(dx, dy, -dx, -dy);
        }
    }

    private double PreviewOutlineStep => Math.Clamp(PlayerSettings.SubtitleOutlineThickness, 0, 6) * 0.45;

    public Thickness PreviewOutlineMarginLeft => new(-PreviewOutlineStep, 0, PreviewOutlineStep, 0);
    public Thickness PreviewOutlineMarginRight => new(PreviewOutlineStep, 0, -PreviewOutlineStep, 0);
    public Thickness PreviewOutlineMarginTop => new(0, -PreviewOutlineStep, 0, PreviewOutlineStep);
    public Thickness PreviewOutlineMarginBottom => new(0, PreviewOutlineStep, 0, -PreviewOutlineStep);

    public FontWeight PreviewFontWeight =>
        PlayerSettings.SubtitleBold ? FontWeights.Bold : FontWeights.Normal;

    [RelayCommand]
    private void ResetSubtitleAppearance()
    {
        PlayerSettings.SubtitlePreferredLanguageMode = "default";
        PlayerSettings.SubtitlePreferredLanguageCode = string.Empty;
        PlayerSettings.SubtitleAllowEmbeddedFormatting = true;
        PlayerSettings.SubtitleFontFamily = "Segoe UI";
        PlayerSettings.SubtitleTextScale = 100;
        PlayerSettings.SubtitleMargin = 0;
        PlayerSettings.SubtitleAlignment = 0;
        PlayerSettings.SubtitleColor = 16777215;
        PlayerSettings.SubtitleOpacity = 255;
        PlayerSettings.SubtitleBackgroundColor = 0;
        PlayerSettings.SubtitleBackgroundOpacity = 128;
        PlayerSettings.SubtitleOutlineThickness = 2;
        PlayerSettings.SubtitleOutlineColor = 0;
        PlayerSettings.SubtitleOutlineOpacity = 255;
        PlayerSettings.SubtitleShadow = false;
        PlayerSettings.SubtitleShadowColor = 0;
        PlayerSettings.SubtitleShadowAngle = 45;
        PlayerSettings.SubtitleShadowDistance = 4;
        PlayerSettings.SubtitleBold = false;

        OnPropertyChanged(nameof(SelectedSubtitleLanguageIndex));
        OnPropertyChanged(nameof(IsCustomSubtitleLanguageVisible));
        OnPropertyChanged(nameof(SubtitlePreferredLanguageCode));
        OnPropertyChanged(nameof(SubtitleAllowEmbeddedFormatting));
        OnPropertyChanged(nameof(SubtitleFontFamily));
        OnPropertyChanged(nameof(SubtitleTextScale));
        OnPropertyChanged(nameof(SubtitleTextScaleLabel));
        OnPropertyChanged(nameof(SubtitleMargin));
        OnPropertyChanged(nameof(SubtitleMarginLabel));
        OnPropertyChanged(nameof(SubtitleAlignment));
        OnPropertyChanged(nameof(SubtitleTextColor));
        OnPropertyChanged(nameof(SubtitleTextSwatchBrush));
        OnPropertyChanged(nameof(SubtitleTextOpacityPercent));
        OnPropertyChanged(nameof(SubtitleTextOpacityLabel));
        OnPropertyChanged(nameof(SubtitleBackgroundColor));
        OnPropertyChanged(nameof(SubtitleBackgroundSwatchBrush));
        OnPropertyChanged(nameof(SubtitleBackgroundOpacityPercent));
        OnPropertyChanged(nameof(SubtitleBackgroundOpacityLabel));
        OnPropertyChanged(nameof(SubtitleOutlineThickness));
        OnPropertyChanged(nameof(SubtitleOutlineThicknessLabel));
        OnPropertyChanged(nameof(SubtitleOutlineColor));
        OnPropertyChanged(nameof(SubtitleOutlineSwatchBrush));
        OnPropertyChanged(nameof(SubtitleOutlineOpacityPercent));
        OnPropertyChanged(nameof(SubtitleOutlineOpacityLabel));
        OnPropertyChanged(nameof(SubtitleShadow));
        OnPropertyChanged(nameof(SubtitleShadowColor));
        OnPropertyChanged(nameof(SubtitleShadowSwatchBrush));
        OnPropertyChanged(nameof(SubtitleShadowAngle));
        OnPropertyChanged(nameof(SubtitleShadowAngleLabel));
        OnPropertyChanged(nameof(SubtitleShadowDistance));
        OnPropertyChanged(nameof(SubtitleShadowDistanceLabel));
        OnPropertyChanged(nameof(SubtitleBold));

        OnPropertyChanged(nameof(PreviewSubtitleMargin));
        OnPropertyChanged(nameof(PreviewHorizontalAlignment));
        OnPropertyChanged(nameof(PreviewTextAlignment));
        OnPropertyChanged(nameof(PreviewFontFamily));
        OnPropertyChanged(nameof(PreviewFontSize));
        OnPropertyChanged(nameof(PreviewForegroundBrush));
        OnPropertyChanged(nameof(PreviewBackgroundBrush));
        OnPropertyChanged(nameof(PreviewOutlineBrush));
        OnPropertyChanged(nameof(PreviewShadowBrush));
        OnPropertyChanged(nameof(PreviewOutlineVisibility));
        OnPropertyChanged(nameof(PreviewOutlineMarginLeft));
        OnPropertyChanged(nameof(PreviewOutlineMarginRight));
        OnPropertyChanged(nameof(PreviewOutlineMarginTop));
        OnPropertyChanged(nameof(PreviewOutlineMarginBottom));
        OnPropertyChanged(nameof(PreviewShadowVisibility));
        OnPropertyChanged(nameof(PreviewShadowMargin));
        OnPropertyChanged(nameof(PreviewFontWeight));
    }

    private static Color RgbToColor(int rgb, byte alpha)
    {
        int masked = rgb & 0x00FFFFFF;
        return Color.FromArgb(
            alpha,
            (byte)((masked >> 16) & 0xFF),
            (byte)((masked >> 8) & 0xFF),
            (byte)(masked & 0xFF));
    }

    private static int ColorToRgb(Color color) => (color.R << 16) | (color.G << 8) | color.B;

    partial void OnLanguageChanged(string value)
    {
        GeneralSettings.Language = value;
        CheckForRelaunch();
    }

    partial void OnShowRecentChanged(bool value)
    {
        GeneralSettings.ShowRecent = value;
        if (!value)
        {
            ClearRecentContext();
        }
    }

    partial void OnSearchRemovableStorageChanged(bool value)
    {
        LibrarySettings.SearchRemovableStorage = value;
        if (SystemInformation.IsXbox && RemovableStorageFolders.Count > 0)
        {
            _dispatcherQueue.TryEnqueue(async () => await RefreshLibrariesAsync());
        }
    }

    partial void OnAdvancedModeChanged(bool value)
    {
        PlayerSettings.AdvancedMode = value;
        CheckForRelaunch();
    }

    partial void OnVideoUpscalingChanged(int value)
    {
        PlayerSettings.VideoUpscale = (VideoUpscaleOption)value;
        CheckForRelaunch();
    }

    partial void OnGlobalArgumentsChanged(string value)
    {
        string sanitized = VlcArgumentHelper.SanitizeArguments(value);
        if (sanitized != value)
        {
            GlobalArguments = sanitized;
            return;
        }

        PlayerSettings.GlobalArguments = sanitized;
        CheckForRelaunch();
    }

    [RelayCommand]
    private async Task RefreshLibrariesAsync()
    {
        await Task.WhenAll(RefreshMusicLibrary(), RefreshVideosLibrary());
    }

    [RelayCommand]
    private async Task AddVideosFolderAsync()
    {
        if (_videosLibrary == null) return;
        await _videosLibrary.RequestAddFolderAsync();
    }

    [RelayCommand]
    private async Task RemoveVideosFolderAsync(StorageFolder folder)
    {
        if (_videosLibrary == null) return;
        try
        {
            await _videosLibrary.RequestRemoveFolderAsync(folder);
        }
        catch (Exception)
        {
        }
    }

    [RelayCommand]
    private async Task AddMusicFolderAsync()
    {
        if (_musicLibrary == null) return;
        await _musicLibrary.RequestAddFolderAsync();
    }

    [RelayCommand]
    private async Task RemoveMusicFolderAsync(StorageFolder folder)
    {
        if (_musicLibrary == null) return;
        try
        {
            await _musicLibrary.RequestRemoveFolderAsync(folder);
        }
        catch (Exception)
        {
        }
    }

    [RelayCommand]
    private async Task ChangeFrameCaptureLocationAsync()
    {
        StorageFolder? folder = await _filesService.PickFolderAsync();
        if (folder is null) return;

        string oldToken = PlayerSettings.FrameCaptureFolderToken;
        if (!string.IsNullOrEmpty(oldToken) && StorageApplicationPermissions.FutureAccessList.ContainsItem(oldToken))
        {
            StorageApplicationPermissions.FutureAccessList.Remove(oldToken);
        }

        string newToken = StorageApplicationPermissions.FutureAccessList.Add(folder, "frame-capture");
        PlayerSettings.FrameCaptureFolderToken = newToken;
        FrameCaptureFolder = folder;
    }

    [RelayCommand]
    private async Task OpenFrameCaptureFolderAsync()
    {
        try
        {
            await LoadFrameCaptureFolderAsync();
            await Launcher.LaunchFolderAsync(FrameCaptureFolder);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to open the frame capture folder.");
        }
    }

    [RelayCommand]
    private void ClearRecentHistory()
    {
        ClearRecentContext();
        StorageApplicationPermissions.MostRecentlyUsedList.Clear();
    }

    private void ClearRecentContext()
    {
        _recentContext.Recent.Clear();
        _recentContext.PathToMruMappings.Clear();
        _recentContext.TokenToMediaMappings.Clear();
        _recentContext.IsLoaded = true;
    }

    [RelayCommand]
    private async Task ClearPlaybackPositionHistoryAsync()
    {
        try
        {
            _playbackProgressTracker.ClearAll();
            await _playbackProgressTracker.SaveToDiskAsync();
        }
        catch (Exception)
        {
        }
    }

    public void OnNavigatedFrom()
    {
        if (SystemInformation.IsXbox)
            _portableStorageDeviceWatcher?.Stop();
    }

    public async Task LoadLibraryLocations()
    {
        if (_videosLibrary == null)
        {
            _videosLibrary = _libraryContext.VideosStorageLibrary;
            if (_videosLibrary != null)
            {
                _videosLibrary.DefinitionChanged += LibraryOnDefinitionChanged;
            }
        }

        if (_musicLibrary == null)
        {
            _musicLibrary = _libraryContext.MusicStorageLibrary;
            if (_musicLibrary != null)
            {
                _musicLibrary.DefinitionChanged += LibraryOnDefinitionChanged;
            }
        }

        UpdateLibraryLocations();
        await UpdateRemovableStorageFoldersAsync();
    }

    public async Task LoadFrameCaptureFolderAsync()
    {
        try
        {
            FrameCaptureFolder = await _filesService.GetFrameCaptureFolderAsync(PlayerSettings.FrameCaptureFolderToken);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to load the frame capture folder.");
        }
    }

    private void LibraryOnDefinitionChanged(StorageLibrary sender, object args)
    {
        _dispatcherQueue.TryEnqueue(UpdateLibraryLocations);
    }

    private void OnPortableStorageDeviceChanged(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        async void RefreshAction() => await UpdateRemovableStorageFoldersAsync();
        _storageDeviceRefreshTimer.Debounce(RefreshAction, TimeSpan.FromMilliseconds(500));
    }

    private void UpdateLibraryLocations()
    {
        if (_videosLibrary != null)
        {
            VideoLocations.Clear();
            foreach (StorageFolder folder in _videosLibrary.Folders)
            {
                VideoLocations.Add(folder);
            }
        }

        if (_musicLibrary != null)
        {
            MusicLocations.Clear();

            foreach (StorageFolder folder in _musicLibrary.Folders)
            {
                MusicLocations.Add(folder);
            }
        }
    }

    private async Task UpdateRemovableStorageFoldersAsync()
    {
        if (SystemInformation.IsXbox)
        {
            RemovableStorageFolders.Clear();
            if (SystemInformation.IsKnownFolderRequestAccessSupported)
            {
                var accessStatus = await KnownFolders.RequestAccessAsync(KnownFolderId.RemovableDevices);
                if (accessStatus != KnownFoldersAccessStatus.Allowed)
                    return;
            }

            foreach (StorageFolder folder in await KnownFolders.RemovableDevices.GetFoldersAsync())
            {
                RemovableStorageFolders.Add(folder);
            }
        }
    }

    private async Task RefreshMusicLibrary()
    {
        try
        {
            await _libraryCoordinator.FetchMusicAsync(false);
        }
        catch (UnauthorizedAccessException)
        {
            Messenger.Send(new NotificationMessage(NotificationLevel.Error, NotificationKind.MusicLibraryAccessDenied));
        }
        catch (Exception e)
        {
            Messenger.Send(new NotificationMessage(NotificationLevel.Error, NotificationKind.None, message: e.Message));
            _logger.LogError(e, "Failed to refresh the music library from settings.");
        }
    }

    private async Task RefreshVideosLibrary()
    {
        try
        {
            await _libraryCoordinator.FetchVideosAsync(false);
        }
        catch (UnauthorizedAccessException)
        {
            Messenger.Send(new NotificationMessage(NotificationLevel.Error, NotificationKind.VideosLibraryAccessDenied));
        }
        catch (Exception e)
        {
            Messenger.Send(new NotificationMessage(NotificationLevel.Error, NotificationKind.None, message: e.Message));
            _logger.LogError(e, "Failed to refresh the videos library from settings.");
        }
    }

    private void CheckForRelaunch()
    {
        if (_initialValues == null) return;

        bool upscalingChanged = _initialValues.VideoUpscaling != VideoUpscaling;
        bool languageChanged = _initialValues.Language != Language;
        bool argsChanged = _initialValues.GlobalArguments != GlobalArguments;
        bool modeChanged = _initialValues.AdvancedMode != AdvancedMode;
        bool hasArgs = GlobalArguments.Length > 0;
        bool whenOn = modeChanged && AdvancedMode && hasArgs;
        bool whenOff = modeChanged && !AdvancedMode && ((!hasArgs && argsChanged) || hasArgs);
        bool whenOnAndChanged = AdvancedMode && argsChanged;

        IsRelaunchRequired = upscalingChanged || languageChanged || whenOn || whenOff || whenOnAndChanged;
    }
}
