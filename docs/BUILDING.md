# 개발자용 안내 · For developers

[README로 돌아가기 · Back to README](../README.md)

프로그램을 쓰기만 할 분은 이 글을 읽지 않아도 돼요. [README](../README.md)의 다운로드만 누르면 돼요.
If you only want to use the program, you don't need this page. Just use the download in the [README](../README.md).

## 소스에서 빌드하기 · Building from source

.NET Framework 4.8에 들어 있는 C# 컴파일러로 빌드해요. Visual Studio, SDK, NuGet은 필요 없어요.
The project builds with the C# compiler that ships with .NET Framework 4.8. No Visual Studio, SDK or NuGet is needed.

```powershell
.\build.ps1
```

`src/` 폴더의 `*.cs` 파일을 모두 컴파일해서 `build.ps1`과 같은 폴더에 `LoveMyShelf.exe`(WPF)를 만들어요. 프로그램이 켜져 있어도 빌드할 수 있고, 새 파일은 다음 실행부터 적용돼요.
This compiles every `*.cs` file in `src/` into `LoveMyShelf.exe` next to `build.ps1` (WPF, `/target:winexe`). If the app is running, the old executable is moved aside and the new one takes effect on the next start.

## 함께 필요한 파일 · Files needed next to the exe

저장소에는 소스 코드만 있어요. 모든 기능을 쓰려면 아래 파일을 `LoveMyShelf.exe` 옆에 넣어 주세요. 다운로드한 ZIP에서 복사하는 게 가장 간단해요. 없어도 프로그램은 켜지고, 관련 기능만 쓸 수 없어요.
The repository contains only the source. To run a build with all features, place these next to `LoveMyShelf.exe` (the simplest way is to copy them from a release ZIP). Without them the app still starts; the features that need them are unavailable.

| 경로 · Path | 용도 · Used for | 출처 · Source |
| --- | --- | --- |
| `libmpv-2.dll` | 영상 재생 · video playback | [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake) |
| `tools/ffmpeg.exe`, `tools/ffprobe.exe`, `tools/*.dll` | 장면 읽기, 움직이는 표지 저장 · frame reading, animated cover export | [FFmpeg-Builds](https://github.com/FFmpeg-Builds/FFmpeg-Builds) (GPL shared) |
| `upscale/onnxruntime.dll`, `upscale/DirectML.dll`, `upscale/onnxruntime_providers_shared.dll` | AI 실행 엔진 · ONNX Runtime | `onnxruntime-directml` 1.24.4 (PyPI) |
| `upscale/model.onnx` | AI로 키우기 모델 · AI upscale model | [Xenova/swin2SR-realworld-sr-x4-64-bsrgan-psnr](https://huggingface.co/Xenova/swin2SR-realworld-sr-x4-64-bsrgan-psnr) |
| `faces/face_detection_yunet_2023mar.onnx`, `faces/face_recognition_sface_2021dec.onnx` | 얼굴로 사람 찾기 모델 · face detection/recognition models | [OpenCV Zoo](https://github.com/opencv/opencv_zoo) |
| `erase/lama_512_int8.onnx` | 필요 없는 것 AI로 지우기 모델 · LaMa inpainting model | [advimman/lama](https://github.com/advimman/lama), [g-ronimo/lama](https://huggingface.co/g-ronimo/lama) |
| `fonts/` | 글꼴(Pretendard 등) · UI and cover fonts | 이 저장소에 포함 · included in this repository |

정확한 버전은 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)에 있어요. .NET Framework 4.8은 Windows 10 1903 이후에 기본으로 들어 있어요.
Exact versions are in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md). .NET Framework 4.8 is included with Windows 10 1903 and later.

## 코드 구성 · Code layout

소스 파일은 모두 `src/`에 있어요. · All source files are in `src/`.

| File | Contents |
| --- | --- |
| `GalleryWindow.cs` | main window, virtualized tile grid |
| `FolderTree.cs`, `AddressBar.cs`, `FolderPane.cs`, `ShellMenu.cs` | folder tree, address bar, subfolder views, Windows right-click menu |
| `TileImages.cs` | tile pictures (shell thumbnails, cover images) |
| `Viewer.cs`, `MpvPlayer.cs`, `SeekPreview.cs`, `ViewFraming.cs`, `CoverStrip.cs` | large view, libmpv playback in a child window, seek-bar thumbnails, carrying the view's turn and zoom into thumbnails and covers, covers of one original |
| `SpeakerRouting.cs` | which speaker a video plays on, by the monitor its window is on |
| `Subtitles.cs`, `SubtitleExplorer.cs` | reading and saving SMI/SRT/ASS/VTT, subtitle explorer and sync |
| `Crop.cs`, `CoverImages.cs`, `Adjust.cs`, `Erase.cs` | cover editor surface, color/clarity adjustment, C# hybrid & LaMa ONNX erasing, rendering a crop from the original |
| `FramePicker.cs`, `VideoFrames.cs` | video frame grid, FFmpeg frame reading |
| `Motion.cs`, `AnimationFrames.cs` | animated cover window and export, animated image decoding |
| `Store.cs` | per-folder records, cover metadata |
| `OriginalLocator.cs`, `DriveLinks.cs` | finding originals that moved (file ID, content signature), following drives whose letter changed |
| `Rename.cs`, `Move.cs`, `RecycleBin.cs`, `SharedCoverFiles.cs` | file operations that keep covers attached |
| `Upscale.cs` | Swin2SR upscale through ONNX Runtime in a child process |
| `Sharing.cs` | copy, Windows share, drag-out |
| `Guide.cs`, `Options.cs`, `FirstRun.cs` | in-app guide and settings, first-run window |
| `Localization.cs` | Korean/English strings |
| `Themes.cs`, `Theme.xaml`, `UI.cs`, `Notice.cs`, `WindowTitleBar.cs`, `FullScreen.cs`, `DragSlider.cs`, `Explorer.cs` | shared UI |
| `Updates.cs`, `UpdateNews.cs`, `release-notes.xml` | version number, signed one-click update, what's new |

## 데이터와 인터넷 · Data and network

- 기록(추가한 폴더, 표지 위치, 설정)은 `%LOCALAPPDATA%\LoveMyShelf` 아래에 텍스트·XML 파일로 저장돼요.
  Records (saved folders, crop coordinates, settings) are plain text and XML files under `%LOCALAPPDATA%\LoveMyShelf`.
- 표지는 처음 실행할 때 고른 표지 폴더에 저장돼요. 설정에 따라 원본 옆에 저장할 수도 있어요.
  Cover files are saved to a cover folder chosen on first run (or next to the originals, depending on settings).
- 인터넷은 새 버전 확인(GitHub Releases API에 요청 한 번)과 업데이트 파일 다운로드에만 써요. 업데이트 목록은 서명을 확인한 뒤에만 믿어요. 「설명서 겸 옵션」에서 끌 수 있고, 사용 기록은 수집하지 않아요.
  The only network access is the optional update check (one request to the GitHub Releases API) and downloading update files. The update list is trusted only after its signature checks out. It can be turned off in Guide & Options. No telemetry.
- 실행 파일에는 코드 서명이 없어요. 각 릴리즈 글에 ZIP의 SHA-256을 적어 둬요.
  The executable is not code-signed. Each release lists the ZIP's SHA-256.

Windows 11에서만 개발하고 테스트했어요. · Developed and tested on Windows 11 only.
