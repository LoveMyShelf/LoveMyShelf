# Love My Shelf

<img src="docs/images/icon.png" width="64" align="right" alt="">

A Windows desktop app for browsing large, unsorted folders of photos, GIFs and videos as one big grid, and for marking items with **9:16 cover crops** so they are easy to recognize later.

[한국어](README.ko.md) · [User guide](GUIDE.md) · [Changelog](CHANGELOG.md) · [Releases](https://github.com/LoveMyShelf/LoveMyShelf/releases)

## Why

A downloads or meme folder with thousands of mixed files is hard to scan in File Explorer: the thumbnails are small, videos show an arbitrary first frame, and you end up opening files one by one. Love My Shelf shows the folder as a large grid. For any photo, or any moment in a video, you can save a vertical crop (a "cover"); the tile then shows that crop instead of the default thumbnail.

Originals are never modified. A cover is always written as a separate file.

## Features

- **Grid browser** for photos, animated GIF/WebP and video, with folder tabs, name search, sorting, and a filter for items with or without covers. Tiles without a cover use the Windows shell thumbnail, so large folders open without decoding every file.
- **Cover editor**: a fixed 9:16 frame over the image; zoom with the wheel, drag to position, save as JPG. Several covers per original are supported. Crop coordinates are stored relative to the upright original (EXIF orientation is applied).
- **Frame picker for video**: pause, open the editor, and pick from a grid of the frames around that point. The step between frames is configurable (default 0.2 s).
- **Animated covers**: export a short clip with the same 9:16 framing as GIF, WebP or MP4. Range is chosen with two handles on a frame strip; speed (0.5–2×) and loop mode (loop, ping-pong, reverse) are adjustable, and the preview plays the actual output file.
- **Cover collection**: all covers across folders in one view.
- **AI upscale (4×)** of a cover with Swin2SR through ONNX Runtime, run in a separate low-priority process; the result is compared side by side before you keep it.
- **File operations**: rename (F2), send to Recycle Bin (Delete), move to another folder. Covers and records follow the original. Originals that were moved or renamed outside the app can be found again by Windows file ID or a content signature.
- **Playback** through libmpv (hardware decoding) with keyboard seeking, frame stepping, speed and brightness keys, and seek-bar thumbnails.
- **Copy / Share / drag-out** of covers or originals to other apps. The app only hands files to Windows; it does not upload anything.
- Light and dark themes, Korean and English UI.

Supported files: JPG, PNG, BMP, TIFF, GIF, WebP, and video containers FFmpeg/mpv can read (MP4, MOV, MKV, WEBM, AVI, …).

## Demo

**Make a cover from a video.** Pause, pick the exact frame from the frame grid, drag and zoom the fixed 9:16 frame, and save. The grid tile then shows the cover; clicking it still opens the original.

https://github.com/user-attachments/assets/c3bf3a02-9c82-4898-a5b9-30a3c739b787

**Covers stay with their video.** Make several covers from one video, then move the video to another folder and all its covers go with it. Covers you no longer want go to the Recycle Bin with one click; the original video stays untouched.

https://github.com/user-attachments/assets/080da44d-49cb-423e-b5c4-aaaef3ea5723

**Make an animated cover.** Choose a range with the two handles on the frame strip, set speed and loop mode, and export. It is made in the background.

https://github.com/user-attachments/assets/cee0954e-f889-47cc-a41b-63cf8dcde45b

**Two windows side by side.** Open a second folder in a new window, select several files, and drag them across to move them. Ctrl+Z puts them back.

https://github.com/user-attachments/assets/c9d3386b-2012-4657-ad82-ad11f2bb0572

## Requirements

- Windows 10 or 11, 64-bit.
- .NET Framework 4.8 (included with Windows 10 1903 and later).

## Install

Download `LoveMyShelf-Windows.zip` from [Releases](https://github.com/LoveMyShelf/LoveMyShelf/releases), extract the whole archive, and run `LoveMyShelf.exe`. There is no installer. The executable is not code-signed, so Windows SmartScreen may show a warning (More info → Run anyway). Each release lists the ZIP's SHA-256.

To update, extract the new ZIP over the old folder. To uninstall, delete the folder and `%LOCALAPPDATA%\LoveMyShelf`.

## Building from source

The project builds with the C# compiler that ships with .NET Framework 4.8. No Visual Studio, SDK or NuGet is needed.

```powershell
.\build.ps1
```

This compiles every `*.cs` file in `src/` into `LoveMyShelf.exe` next to `build.ps1` (WPF, `/target:winexe`). If the app is running, the old executable is moved aside and the new one takes effect on the next start.

The repository contains only the source. To run a build with all features, place these next to `LoveMyShelf.exe` (the simplest way is to copy them from a release ZIP):

| Path | Used for | Source |
| --- | --- | --- |
| `libmpv-2.dll` | video playback | [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake) |
| `tools/ffmpeg.exe`, `tools/ffprobe.exe`, `tools/*.dll` | frame reading, animated cover export | [FFmpeg-Builds](https://github.com/FFmpeg-Builds/FFmpeg-Builds) (GPL shared) |
| `upscale/onnxruntime.dll`, `upscale/DirectML.dll`, `upscale/onnxruntime_providers_shared.dll` | AI upscale | `onnxruntime-directml` 1.24.4 (PyPI) |
| `upscale/model.onnx` | AI upscale model | [Xenova/swin2SR-realworld-sr-x4-64-bsrgan-psnr](https://huggingface.co/Xenova/swin2SR-realworld-sr-x4-64-bsrgan-psnr) |
| `fonts/` | UI font (Pretendard) | included in this repository |

Without them the app still starts; the features that need them are unavailable. Exact versions are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Code layout

All source files are in `src/`.

| File | Contents |
| --- | --- |
| `GalleryWindow.cs` | main window, folder tabs, virtualized tile grid |
| `TileImages.cs` | tile pictures (shell thumbnails, cover images) |
| `Viewer.cs`, `MpvPlayer.cs`, `SeekPreview.cs` | large view, libmpv playback in a child window, seek-bar thumbnails |
| `Crop.cs`, `CoverImages.cs` | cover editor surface, rendering a crop from the original |
| `FramePicker.cs`, `VideoFrames.cs` | video frame grid, FFmpeg frame reading |
| `Motion.cs`, `AnimationFrames.cs` | animated cover window and export, animated image decoding |
| `Store.cs` | per-folder records, cover metadata |
| `OriginalLocator.cs` | finding originals that moved (file ID, content signature) |
| `Rename.cs`, `Move.cs`, `RecycleBin.cs`, `SharedCoverFiles.cs` | file operations that keep covers attached |
| `Upscale.cs` | Swin2SR upscale through ONNX Runtime in a child process |
| `Sharing.cs` | copy, Windows share, drag-out |
| `Guide.cs`, `Options.cs`, `FirstRun.cs` | in-app guide and settings, first-run window |
| `Localization.cs` | Korean/English strings |
| `Themes.cs`, `Theme.xaml`, `UI.cs`, `Notice.cs`, `WindowTitleBar.cs`, `FullScreen.cs`, `DragSlider.cs`, `Explorer.cs` | shared UI |
| `Updates.cs` | version number and update check |

## Data and network

- Records (folder tabs, crop coordinates, settings) are plain text and XML files under `%LOCALAPPDATA%\LoveMyShelf`.
- Cover files are saved as JPG to a cover folder chosen on first run (or next to the originals, depending on settings).
- The only network access is an optional update check: one request to the GitHub Releases API for the latest tag. It can be turned off in Guide & Options. No telemetry.

## Status and feedback

Version 1.0.1. Developed and tested on Windows 11 only. Bug reports, questions and suggestions are welcome in [Issues](https://github.com/LoveMyShelf/LoveMyShelf/issues). Contact: lovemyshelf.app@outlook.com

## License

Love My Shelf is licensed under [GPL-3.0](LICENSE). Bundled components (libmpv, FFmpeg, ONNX Runtime, Swin2SR, Pretendard) keep their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

The dog video in the demos is from Pexels ([Nothing Ahead](https://www.pexels.com/video/11622180/)), used under the [Pexels License](https://www.pexels.com/license/).
