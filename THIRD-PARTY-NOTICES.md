# Third-party notices

Love My Shelf itself is licensed under the GNU General Public License v3.0 (see `LICENSE`). The Windows download also contains the following components, each under its own license.

## libmpv (`libmpv-2.dll`)

- What it does: built-in video playback.
- Source: mpv, <https://github.com/mpv-player/mpv>, commit `bcde6b93cc`.
- Binary: `mpv-dev-x86_64-20261111-git-bcde6b93cc.7z` from shinchiro's mpv-winbuild-cmake, <https://github.com/shinchiro/mpv-winbuild-cmake>. The build scripts there list every library compiled into this DLL (FFmpeg and others).
- License: GNU General Public License (this build enables GPL components). See <https://github.com/mpv-player/mpv/blob/master/Copyright>.
- Corresponding source: available from the links above. If you cannot obtain it there, open an issue at https://github.com/LoveMyShelf/LoveMyShelf/issues or email lovemyshelf.app@outlook.com and we will provide the complete corresponding source for this binary for at least three years after the release.

## ONNX Runtime (`upscale/onnxruntime.dll`, `upscale/onnxruntime_providers_shared.dll`, `upscale/DirectML.dll`)

- What it does: runs the AI upscaling model on the CPU.
- Version: 1.24.4 (onnxruntime-directml package from PyPI).
- License: MIT. Full text and third-party notices: `upscale/LICENSE-onnxruntime.txt` and `upscale/ThirdPartyNotices-onnxruntime.txt`. `DirectML.dll` is Microsoft's redistributable shipped in that package.

## Swin2SR model (`upscale/model.onnx`)

- What it does: the AI model used by **Upscale**.
- Model: Swin2SR real-world x4 (`caidas/swin2SR-realworld-sr-x4-64-bsrgan-psnr`), ONNX conversion by Xenova on Hugging Face.
- License: Apache License 2.0, <https://www.apache.org/licenses/LICENSE-2.0>.
- Paper: Conde et al., *Swin2SR: SwinV2 Transformer for Compressed Image Super-Resolution and Restoration*, ECCV 2022 Workshops.

## FFmpeg (`tools/ffmpeg.exe`, `tools/ffprobe.exe`, `tools/*.dll`)

- What it does: reads video frames for video covers.
- Version: `n8.1.3-39-g660b5aa7db-20261120`, 64-bit GPL shared build `ffmpeg-n8.1-latest-win64-gpl-shared-8.1.zip` (SHA-256 `6678d2025460773ee0e521502b268132cc0ab1bf62d31f482fd0e27577534390`) from FFmpeg-Builds, <https://github.com/FFmpeg-Builds/FFmpeg-Builds>, which is linked from <https://ffmpeg.org/download.html>. `ffplay.exe` and the development files were left out.
- Source: FFmpeg, <https://git.ffmpeg.org/ffmpeg.git> (commit `660b5aa7db` on the release/8.1 branch). The build scripts in the FFmpeg-Builds repository list every library compiled in.
- License: GNU General Public License v3.0 (this build enables GPL components). Full text: `tools/LICENSE-ffmpeg.txt`.
- Corresponding source: available from the links above. If you cannot obtain it there, open an issue at https://github.com/LoveMyShelf/LoveMyShelf/issues or email lovemyshelf.app@outlook.com and we will provide the complete corresponding source for this binary for at least three years after the release.

## Pretendard (`fonts/Pretendard-*.otf`)

- What it does: the typeface used for all text in Love My Shelf.
- Version: 1.3.9, the unmodified static OpenType files Regular, Medium, SemiBold, and Bold from <https://github.com/orioncactus/pretendard> (`packages/pretendard/dist/public/static`).
- Copyright (c) 2021, Kil Hyung-jin, with Reserved Font Name 'Pretendard'. Includes glyphs from Source Han Sans (Adobe), Inter (The Inter Project Authors), and M PLUS 1 (The M+ FONTS Project Authors).
- License: SIL Open Font License 1.1. Full text: `fonts/OFL.txt`.

## README images and demo videos

- `docs/images/icon.png` is Love My Shelf's own icon (the 256 px image from `gallery.ico`).
- The demo videos in the README are screen recordings of Love My Shelf, hosted as GitHub attachments. They are not part of the Windows download.
- The dog video shown in them is free stock media from Pexels, <https://www.pexels.com>, used under the Pexels License, <https://www.pexels.com/license/>. It is not covered by GPL-3.0. The creator is credited at the end of `README.md`.
