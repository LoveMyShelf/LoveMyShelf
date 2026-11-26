# Third-party notices

LoveMyShelf itself is licensed under the GNU General Public License v3.0 (see `LICENSE`). The Windows download also contains the following components, each under its own license.

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

## Face models (`faces/face_detection_yunet_2023mar.onnx`, `faces/face_recognition_sface_2021dec.onnx`)

- What they do: the two AI models used by **Find people by face**. They run on this computer only, through the ONNX Runtime above.
- YuNet (finds faces): from the OpenCV Model Zoo, <https://github.com/opencv/opencv_zoo/tree/main/models/face_detection_yunet>. License: MIT, full text in `faces/LICENSE-face_detection_yunet_2023mar.onnx.txt`.
- SFace (describes a face as 128 numbers): from the OpenCV Model Zoo, <https://github.com/opencv/opencv_zoo/tree/main/models/face_recognition_sface>. License: Apache License 2.0, full text in `faces/LICENSE-face_recognition_sface_2021dec.onnx.txt`.
- Papers: *YuNet: A Tiny Millisecond-level Face Detector*, <https://link.springer.com/article/10.1007/s11633-023-1423-y>; *SFace: Sigmoid-Constrained Hypersphere Loss for Robust Face Recognition*, <https://arxiv.org/abs/2205.12010>.

## FFmpeg (`tools/ffmpeg.exe`, `tools/ffprobe.exe`, `tools/*.dll`)

- What it does: reads video frames for video covers.
- Version: `n8.1.3-39-g660b5aa7db-20261120`, 64-bit GPL shared build `ffmpeg-n8.1-latest-win64-gpl-shared-8.1.zip` (SHA-256 `6678d2025460773ee0e521502b268132cc0ab1bf62d31f482fd0e27577534390`) from FFmpeg-Builds, <https://github.com/FFmpeg-Builds/FFmpeg-Builds>, which is linked from <https://ffmpeg.org/download.html>. `ffplay.exe` and the development files were left out.
- Source: FFmpeg, <https://git.ffmpeg.org/ffmpeg.git> (commit `660b5aa7db` on the release/8.1 branch). The build scripts in the FFmpeg-Builds repository list every library compiled in.
- License: GNU General Public License v3.0 (this build enables GPL components). Full text: `tools/LICENSE-ffmpeg.txt`.
- Corresponding source: available from the links above. If you cannot obtain it there, open an issue at https://github.com/LoveMyShelf/LoveMyShelf/issues or email lovemyshelf.app@outlook.com and we will provide the complete corresponding source for this binary for at least three years after the release.

## 7-Zip (`tools/7z.exe`, `tools/7z.dll`)

- What it does: makes and opens the **locked photo boxes** (7z archives locked with AES-256) of Backup and restore. The password is passed to 7-Zip through its input, never on its command line.
- Version: 7-Zip 25.01 (x64), 2025-08-03, the unmodified `7z.exe` and `7z.dll` from the official Windows installer, <https://www.7-zip.org/download.html>. SHA-256: `7z.exe` `4cd7d776c686427226a151789d2d61f0b2ed2c392148cc4e69c0238362fafecf`, `7z.dll` `5bd20fb38499d95c39594f41d4781b6181b3304b7f1f4d06b0182f514e7eaa74`.
- Copyright (C) 1999-2025 Igor Pavlov.
- License: GNU LGPL 2.1 or later for most of the code; parts of `7z.dll` are under the GNU LGPL with the unRAR license restriction (the RAR decompression code may not be used to re-create the RAR compression algorithm), the BSD 3-clause License, and the BSD 2-clause License. Full text: `tools/LICENSE-7zip.txt`.
- Source: <https://www.7-zip.org/download.html> (7-Zip source code) and <https://github.com/ip7z/7zip>. If you cannot obtain it there, open an issue at https://github.com/LoveMyShelf/LoveMyShelf/issues or email lovemyshelf.app@outlook.com and we will provide the corresponding source for at least three years after the release.

## Pretendard (`fonts/Pretendard-*.otf`)

- What it does: the typeface used for all text in LoveMyShelf.
- Version: 1.3.9, the unmodified static OpenType files Regular, Medium, SemiBold, and Bold from <https://github.com/orioncactus/pretendard> (`packages/pretendard/dist/public/static`).
- Copyright (c) 2021, Kil Hyung-jin, with Reserved Font Name 'Pretendard'. Includes glyphs from Source Han Sans (Adobe), Inter (The Inter Project Authors), and M PLUS 1 (The M+ FONTS Project Authors).
- License: SIL Open Font License 1.1. Full text: `fonts/OFL.txt`.

## README images and demo videos

- `docs/images/icon.png` is LoveMyShelf's own icon (the 256 px image from `src/gallery.ico`).
- The demo videos in the README are screen recordings of LoveMyShelf, hosted as GitHub attachments. They are not part of the Windows download.
