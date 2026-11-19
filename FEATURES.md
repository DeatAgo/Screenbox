# Fork Enhancements & Feature Summary

This fork extends Screenbox with a dockable MediaInfo properties inspector, in-app subtitle setting reloads (no app restart required), custom playlist cover cropping and drag-and-drop reordering, audio-only playback mode, batch multi-selection, and Lively visualizer stability fixes.

---

## 1. Dockable & Floating MediaInfo Properties Panel
- **Docking Modes:** Dock the Properties panel to the **Left** or **Right** side of the player with resizable splitters, or pop it out into a non-modal **Floating** window that updates live as you select different media items.
- **Automatic Visibility Handling:** Automatically collapses the docked sidebar when entering Picture-in-Picture (`CompactOverlay`), Mini-Player, or Hidden player states, and restores it when returning to normal or fullscreen playback.
- **Deep Media Inspection (`MediaInfoLib`):** Uses `MediaInfoLib` to display container, video, and audio stream details (codec format, resolution, aspect ratio, frame rate, bit depth, sampling rate, and bitrate) with graceful fallback to WinRT/LibVLC metadata.
- **Optional FFmpeg Audio Bitrate Fallback:** If a container header omits audio bitrate metadata (e.g., certain Matroska/Opus or VBR tracks), placing `ffmpeg.exe` in the app's installation directory (`AppX` folder) enables automatic packet-level audio bitrate calculation. If `ffmpeg.exe` is not present—or if the file is over 500 MB and opened via the UWP sandbox fallback (which reads a 32 MB header chunk instead of copying the full file)—this step is skipped silently without errors.

## 2. Subtitle Customization & In-App Reload
- **Appearance & Language Settings:** Configure subtitle font family (enumerated from installed system fonts), font size, text color, background/outline styling, and preferred default subtitle language (supporting ISO-639 alias matching, `"default"`, `"none"`, or custom language codes).
- **Apply Without Restarting the App:** Subtitle appearance changes take effect as soon as the video is reloaded or replayed—automatically reinitializing the underlying LibVLC player and restoring your exact playback position and play/pause state—eliminating the need to restart Screenbox.

## 3. Custom Playlist Covers & Reordering
- **Cover Image Cropping:** Added `CropPlaylistCoverDialog` to crop and assign custom thumbnails to playlists.
- **Playlist & Item Reordering:** Drag and drop to reorder both your playlists on the Playlists page and the media items inside any playlist, with changes saved automatically.
- **Accurate Item Durations (`0:00` Fix):** Added a LibVLC metadata fallback when Windows Storage properties report `0:00` duration (common with certain container formats like MKV), so newly added playlist and play queue items display their actual duration immediately.

## 4. Audio-Only Playback Mode
- **Video Track Toggle:** Switch video files into **Audio-Only** mode (`:no-video`) to save resources or listen with the Lively audio visualizer, and toggle back seamlessly.

## 5. Multi-Selection & Batch Actions
- **Batch Operations:** Added multi-select support (`Ctrl+A` shortcut, selection checkboxes, and `SelectionBarControl`) across **All Videos**, **Folder View** (excluding folders from media batch actions), and **Playlist Details**.

## 6. Lively Visualizer Improvements
- **Smooth Transitions:** Prevented empty-thumbnail flashes and serialized texture transitions in the **Default** and **Music Tunnel** visualizers in audio-only mode.