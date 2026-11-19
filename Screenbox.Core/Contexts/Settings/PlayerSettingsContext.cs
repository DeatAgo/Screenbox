using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Screenbox.Core.Enums;
using Screenbox.Core.Helpers;
using Screenbox.Core.Services;

namespace Screenbox.Core.Contexts;

/// <summary>
/// Observable state holder for media player UI presentation, transport controls, seek intervals,
/// touch and pointer gestures, frame capture location, Lively audio visualizer, video upscaling,
/// and LibVLC engine configuration.
/// </summary>
public sealed partial class PlayerSettingsContext : SettingsContextBase<PlayerSettingsContext>
{
    private const string AutoResizeKey = "Player/AutoResize";
    private const string ShowControlsKey = "Player/ShowControls";
    private const string ControlsHideDelayKey = "Player/ControlsHideDelay";
    private const string ShowChaptersKey = "Player/ShowChapters";
    private const string PersistentShowRemainingTimeKey = "Values/ShowRemainingTime";
    private const string RewindStepKey = "Player/RewindStep";
    private const string FastForwardStepKey = "Player/FastForwardStep";
    private const string GeneralFrameCaptureFolderTokenKey = "General/FrameCaptureFolderToken";
    private const string GestureTapKey = "Player/Gesture/Tap";
    private const string GestureSwipeUpKey = "Player/Gesture/SwipeUp";
    private const string GestureSwipeDownKey = "Player/Gesture/SwipeDown";
    private const string GestureSwipeLeftKey = "Player/Gesture/SwipeLeft";
    private const string GestureSwipeRightKey = "Player/Gesture/SwipeRight";
    private const string GestureSlideVerticalKey = "Player/Gesture/SlideVertical";
    private const string GestureSlideHorizontalKey = "Player/Gesture/SlideHorizontal";
    private const string GesturePressAndHoldKey = "Player/Gesture/PressAndHold";
    private const string LivelyPathKey = "Player/Lively/Path";
    private const string AdvancedModeKey = "Advanced/IsEnabled";
    private const string AdvancedVideoUpscaleKey = "Advanced/VideoUpscale";
    private const string GlobalArgumentsKey = "Values/GlobalArguments";

    private const string SubtitlePreferredLanguageModeKey = "Player/Subtitle/PreferredLanguageMode";
    private const string SubtitlePreferredLanguageCodeKey = "Player/Subtitle/PreferredLanguageCode";
    private const string SubtitleAllowEmbeddedFormattingKey = "Player/Subtitle/AllowEmbeddedFormatting";
    private const string SubtitleFontFamilyKey = "Player/Subtitle/FontFamily";
    private const string SubtitleTextScaleKey = "Player/Subtitle/TextScale";
    private const string SubtitleMarginKey = "Player/Subtitle/Margin";
    private const string SubtitleAlignmentKey = "Player/Subtitle/Alignment";
    private const string SubtitleColorKey = "Player/Subtitle/Color";
    private const string SubtitleOpacityKey = "Player/Subtitle/Opacity";
    private const string SubtitleBackgroundColorKey = "Player/Subtitle/BackgroundColor";
    private const string SubtitleBackgroundOpacityKey = "Player/Subtitle/BackgroundOpacity";
    private const string SubtitleOutlineThicknessKey = "Player/Subtitle/OutlineThickness";
    private const string SubtitleOutlineColorKey = "Player/Subtitle/OutlineColor";
    private const string SubtitleOutlineOpacityKey = "Player/Subtitle/OutlineOpacity";
    private const string SubtitleShadowKey = "Player/Subtitle/Shadow";
    private const string SubtitleShadowColorKey = "Player/Subtitle/ShadowColor";
    private const string SubtitleShadowAngleKey = "Player/Subtitle/ShadowAngle";
    private const string SubtitleShadowDistanceKey = "Player/Subtitle/ShadowDistance";
    private const string SubtitleBoldKey = "Player/Subtitle/Bold";

    private static bool IsXbox => SystemInformation.IsXbox;

    [ObservableProperty]
    public partial bool IsAudioOnly { get; set; }

    public PlayerAutoResizeOption AutoResize
    {
        get => Get(AutoResizeKey, PlayerAutoResizeOption.Never);
        set => Set(AutoResizeKey, value);
    }

    /// <summary>
    /// Preferred subtitle track mode: "default", "none", or an ISO-639 language code (e.g. "en", "ja", "zh").
    /// </summary>
    public string SubtitlePreferredLanguageMode
    {
        get => Get(SubtitlePreferredLanguageModeKey, "default");
        set => Set(SubtitlePreferredLanguageModeKey, value);
    }

    /// <summary>
    /// Custom ISO-639 language code(s) when SubtitlePreferredLanguageMode is set to "custom".
    /// </summary>
    public string SubtitlePreferredLanguageCode
    {
        get => Get(SubtitlePreferredLanguageCodeKey, string.Empty);
        set => Set(SubtitlePreferredLanguageCodeKey, value);
    }

    /// <summary>
    /// Allows embedded subtitle formatting tags (subsdec-formatted) instead of overriding all inline styling.
    /// </summary>
    public bool SubtitleAllowEmbeddedFormatting
    {
        get => Get(SubtitleAllowEmbeddedFormattingKey, true);
        set => Set(SubtitleAllowEmbeddedFormattingKey, value);
    }

    public string SubtitleFontFamily
    {
        get => Get(SubtitleFontFamilyKey, "Segoe UI");
        set => Set(SubtitleFontFamilyKey, value);
    }

    /// <summary>
    /// LibVLC subtitle text scale percentage (50 to 250, default 100).
    /// </summary>
    public int SubtitleTextScale
    {
        get => Get(SubtitleTextScaleKey, 100);
        set => Set(SubtitleTextScaleKey, value);
    }

    /// <summary>
    /// LibVLC vertical subtitle margin in pixels (sub-margin, 0 to 200, default 0).
    /// </summary>
    public int SubtitleMargin
    {
        get => Get(SubtitleMarginKey, 0);
        set => Set(SubtitleMarginKey, value);
    }

    /// <summary>
    /// LibVLC horizontal subtitle alignment (subsdec-align): 0 = Center, 1 = Left, 2 = Right.
    /// </summary>
    public int SubtitleAlignment
    {
        get => Get(SubtitleAlignmentKey, 0);
        set => Set(SubtitleAlignmentKey, value);
    }

    /// <summary>
    /// Decimal RGB color value for subtitle text (default 16777215 = White).
    /// </summary>
    public int SubtitleColor
    {
        get => Get(SubtitleColorKey, 16777215);
        set => Set(SubtitleColorKey, value);
    }

    /// <summary>
    /// Text opacity from 0 (transparent) to 255 (solid). Default 255.
    /// </summary>
    public int SubtitleOpacity
    {
        get => Get(SubtitleOpacityKey, 255);
        set => Set(SubtitleOpacityKey, value);
    }

    /// <summary>
    /// Decimal RGB color value for subtitle background box (default 0 = Black).
    /// </summary>
    public int SubtitleBackgroundColor
    {
        get => Get(SubtitleBackgroundColorKey, 0);
        set => Set(SubtitleBackgroundColorKey, value);
    }

    /// <summary>
    /// Background box opacity from 0 (none) to 255 (solid). Default 128 (~50%).
    /// </summary>
    public int SubtitleBackgroundOpacity
    {
        get => Get(SubtitleBackgroundOpacityKey, 128);
        set => Set(SubtitleBackgroundOpacityKey, value);
    }

    /// <summary>
    /// FreeType outline thickness from 0 (none) to 6 (thick). Default 2 (thin).
    /// </summary>
    public int SubtitleOutlineThickness
    {
        get => Get(SubtitleOutlineThicknessKey, 2);
        set => Set(SubtitleOutlineThicknessKey, value);
    }

    /// <summary>
    /// Decimal RGB color value for subtitle outline (default 0 = Black).
    /// </summary>
    public int SubtitleOutlineColor
    {
        get => Get(SubtitleOutlineColorKey, 0);
        set => Set(SubtitleOutlineColorKey, value);
    }

    /// <summary>
    /// FreeType outline opacity from 0 to 255 (default 255).
    /// </summary>
    public int SubtitleOutlineOpacity
    {
        get => Get(SubtitleOutlineOpacityKey, 255);
        set => Set(SubtitleOutlineOpacityKey, value);
    }

    /// <summary>
    /// Enables FreeType drop shadow behind subtitle text.
    /// </summary>
    public bool SubtitleShadow
    {
        get => Get(SubtitleShadowKey, false);
        set => Set(SubtitleShadowKey, value);
    }

    /// <summary>
    /// Decimal RGB color value for FreeType drop shadow (default 0 = Black).
    /// </summary>
    public int SubtitleShadowColor
    {
        get => Get(SubtitleShadowColorKey, 0);
        set => Set(SubtitleShadowColorKey, value);
    }

    /// <summary>
    /// FreeType drop shadow angle in degrees (0 to 360, default 45).
    /// </summary>
    public int SubtitleShadowAngle
    {
        get => Get(SubtitleShadowAngleKey, 45);
        set => Set(SubtitleShadowAngleKey, value);
    }

    /// <summary>
    /// FreeType drop shadow offset distance in hundredths (0 to 20 -> 0.00 to 0.20, default 4 -> 0.04).
    /// </summary>
    public int SubtitleShadowDistance
    {
        get => Get(SubtitleShadowDistanceKey, 4);
        set => Set(SubtitleShadowDistanceKey, value);
    }

    public bool SubtitleBold
    {
        get => Get(SubtitleBoldKey, false);
        set => Set(SubtitleBoldKey, value);
    }

    public bool ShowControls
    {
        get => Get(ShowControlsKey, true);
        set => Set(ShowControlsKey, value);
    }

    public int ControlsHideDelay
    {
        get => Get(ControlsHideDelayKey, 3);
        set => Set(ControlsHideDelayKey, value);
    }

    public bool ShowChapters
    {
        get => Get(ShowChaptersKey, true);
        set => Set(ShowChaptersKey, value);
    }

    public bool ShowRemainingTime
    {
        get => Get(PersistentShowRemainingTimeKey, false);
        set => Set(PersistentShowRemainingTimeKey, value);
    }

    public int RewindStep
    {
        get => Get(RewindStepKey, 5);
        set => Set(RewindStepKey, value);
    }

    public int FastForwardStep
    {
        get => Get(FastForwardStepKey, 5);
        set => Set(FastForwardStepKey, value);
    }

    public string FrameCaptureFolderToken
    {
        get => Get(GeneralFrameCaptureFolderTokenKey, string.Empty);
        set => Set(GeneralFrameCaptureFolderTokenKey, value);
    }

    public PlaybackActionKind GestureTap
    {
        get => Get(GestureTapKey, IsXbox ? PlaybackActionKind.None : PlaybackActionKind.PlayPause);
        set => Set(GestureTapKey, value);
    }

    public PlaybackActionKind GestureSwipeUp
    {
        get => Get(GestureSwipeUpKey, IsXbox ? PlaybackActionKind.None : PlaybackActionKind.IncreaseVolume);
        set => Set(GestureSwipeUpKey, value);
    }

    public PlaybackActionKind GestureSwipeDown
    {
        get => Get(GestureSwipeDownKey, IsXbox ? PlaybackActionKind.None : PlaybackActionKind.DecreaseVolume);
        set => Set(GestureSwipeDownKey, value);
    }

    public PlaybackActionKind GestureSwipeLeft
    {
        get => Get(GestureSwipeLeftKey, IsXbox ? PlaybackActionKind.None : PlaybackActionKind.Rewind);
        set => Set(GestureSwipeLeftKey, value);
    }

    public PlaybackActionKind GestureSwipeRight
    {
        get => Get(GestureSwipeRightKey, IsXbox ? PlaybackActionKind.None : PlaybackActionKind.FastForward);
        set => Set(GestureSwipeRightKey, value);
    }

    public bool GestureSlideVertical
    {
        get => Get(GestureSlideVerticalKey, !IsXbox);
        set => Set(GestureSlideVerticalKey, value);
    }

    public bool GestureSlideHorizontal
    {
        get => Get(GestureSlideHorizontalKey, !IsXbox);
        set => Set(GestureSlideHorizontalKey, value);
    }

    public bool GesturePressAndHold
    {
        get => Get(GesturePressAndHoldKey, !IsXbox);
        set => Set(GesturePressAndHoldKey, value);
    }

    public string LivelyActivePath
    {
        get => Get(LivelyPathKey, string.Empty);
        set => Set(LivelyPathKey, value);
    }

    public bool AdvancedMode
    {
        get => Get(AdvancedModeKey, false);
        set => Set(AdvancedModeKey, value);
    }

    public VideoUpscaleOption VideoUpscale
    {
        get => Get(AdvancedVideoUpscaleKey, VideoUpscaleOption.Linear);
        set => Set(AdvancedVideoUpscaleKey, value);
    }

    public string GlobalArguments
    {
        get => Get(GlobalArgumentsKey, string.Empty);
        set => Set(GlobalArgumentsKey, value);
    }

    public PlayerSettingsContext(ISettingsStorage storage) : base(storage)
    {
    }
}
