# Love My Shelf

<img src="docs/images/icon.png" width="64" align="right" alt="">

정리 안 된 큰 폴더 속 사진·GIF·영상을 큰 바둑판으로 훑어보고, 알아보기 쉽게 **9:16 세로 표지**를 붙여 두는 Windows 프로그램입니다.

[English](README.md) · [설명서](GUIDE.ko.md) · [바뀐 점](CHANGELOG.md) · [Releases](https://github.com/LoveMyShelf/LoveMyShelf/releases)

## 만든 이유

파일이 수천 개 섞인 내려받기·밈 폴더는 파일 탐색기로 훑기 어렵습니다. 미리보기가 작고, 영상은 아무 첫 장면이나 보여서 결국 하나씩 열어 보게 됩니다. Love My Shelf는 폴더를 큰 바둑판으로 보여 줍니다. 사진이나 영상 속 한 장면에서 세로로 자른 그림(표지)을 저장하면, 그 칸에는 기본 미리보기 대신 표지가 보입니다.

원본은 바꾸지 않습니다. 표지는 언제나 별도 파일로 저장됩니다.

## 기능

- **바둑판 보기**: 사진, 움직이는 GIF·WebP, 영상. 폴더 탭, 이름 검색, 정렬, 표지 있음·없음 거르기. 표지가 없는 칸은 Windows 미리보기를 써서 큰 폴더도 파일을 하나하나 읽지 않고 열립니다.
- **표지 만들기**: 9:16 고정 틀 위에서 휠로 확대, 끌어서 위치 맞추기, JPG 저장. 원본 하나에 표지 여러 장 가능. 자른 위치는 EXIF 방향을 반영한 원본 기준 비율로 저장합니다.
- **영상 장면 고르기**: 멈춘 곳 앞뒤 장면을 바둑판으로 펼쳐 고릅니다. 장면 간격은 설정 가능(기본 0.2초).
- **움직이는 표지**: 같은 9:16 구도로 짧은 구간을 GIF·WebP·MP4로 저장. 장면 띠의 손잡이 두 개로 구간을 고르고, 속도(0.5~2배)와 재생 방식(반복·왕복·거꾸로)을 정하며, 미리보기는 실제로 만들어질 파일을 재생합니다.
- **표지 모아보기**: 여러 폴더의 표지를 한 화면에.
- **AI 4배 키우기**: ONNX Runtime으로 Swin2SR을 별도의 낮은 우선순위 프로세스에서 실행하고, 원래 것과 나란히 비교한 뒤 고릅니다.
- **파일 작업**: 이름 바꾸기(F2), 휴지통(Delete), 다른 폴더로 옮기기. 표지와 기록이 원본을 따라갑니다. 프로그램 밖에서 옮겨지거나 이름이 바뀐 원본은 Windows 파일 ID나 내용 지문으로 다시 찾습니다.
- **재생**: libmpv(하드웨어 디코딩), 키보드 건너뛰기·한 장면씩 이동·속도·밝기, 재생바 미리보기.
- **복사·공유·끌어다 놓기**: 파일을 Windows에 넘길 뿐 직접 올리지 않습니다.
- 밝은·어두운 화면, 한국어·영어.

열 수 있는 파일: JPG, PNG, BMP, TIFF, GIF, WebP, 그리고 FFmpeg·mpv가 읽는 영상(MP4, MOV, MKV, WEBM, AVI 등).

## 시연 영상

소리 없는 짧은 화면 녹화를 군더더기 없이 잘라 2배속으로 보여 줘요(화면 글자는 영어).

**영상으로 표지 만들기.** 영상을 멈추고 장면 바둑판에서 딱 맞는 장면을 고른 다음, 고정된 9:16 틀을 끌고 확대해 저장해요. 그러면 바둑판 칸에 표지가 보이고, 눌렀을 때는 원본이 열려요.

https://github.com/user-attachments/assets/c3bf3a02-9c82-4898-a5b9-30a3c739b787

**움직이는 표지 만들기.** 장면 띠의 손잡이 두 개로 구간을 고르고, 속도와 반복 방식을 정해 만들어요. 만드는 동안 다른 일을 해도 돼요.

https://github.com/user-attachments/assets/cee0954e-f889-47cc-a41b-63cf8dcde45b

**창 두 개 나란히 쓰기.** 새 창에 다른 폴더를 열고, 파일 여러 개를 골라 끌어 옮겨요. Ctrl+Z로 되돌릴 수 있어요.

https://github.com/user-attachments/assets/c9d3386b-2012-4657-ad82-ad11f2bb0572

## 필요한 환경

- Windows 10 또는 11, 64비트
- .NET Framework 4.8 (Windows 10 1903 이후에는 기본으로 들어 있음)

## 설치

[Releases](https://github.com/LoveMyShelf/LoveMyShelf/releases)에서 `LoveMyShelf-Windows.zip`을 받아 전체를 풀고 `LoveMyShelf.exe`를 실행합니다. 설치 프로그램은 없습니다. 코드 서명이 없어서 Windows SmartScreen 경고가 뜰 수 있습니다(추가 정보 → 실행). 각 릴리스에 ZIP의 SHA-256을 적어 둡니다.

업데이트는 새 ZIP을 원래 폴더에 덮어 풀면 됩니다. 지우려면 폴더와 `%LOCALAPPDATA%\LoveMyShelf`를 지웁니다.

## 소스에서 빌드

.NET Framework 4.8에 들어 있는 C# 컴파일러로 빌드합니다. Visual Studio, SDK, NuGet이 필요 없습니다.

```powershell
.\build.ps1
```

`src/` 폴더의 모든 `*.cs`를 `build.ps1` 옆의 `LoveMyShelf.exe`(WPF)로 컴파일합니다. 프로그램이 실행 중이면 옛 실행 파일을 옆으로 치우고, 다음에 켤 때 새 것이 적용됩니다.

저장소에는 소스만 있습니다. 모든 기능을 쓰려면 아래 파일을 `LoveMyShelf.exe` 옆에 두세요(릴리스 ZIP에서 복사하는 것이 가장 간단합니다).

| 경로 | 용도 | 출처 |
| --- | --- | --- |
| `libmpv-2.dll` | 영상 재생 | [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake) |
| `tools/ffmpeg.exe`, `tools/ffprobe.exe`, `tools/*.dll` | 장면 읽기, 움직이는 표지 저장 | [FFmpeg-Builds](https://github.com/FFmpeg-Builds/FFmpeg-Builds) (GPL shared) |
| `upscale/onnxruntime.dll`, `upscale/DirectML.dll`, `upscale/onnxruntime_providers_shared.dll` | AI 키우기 | `onnxruntime-directml` 1.24.4 (PyPI) |
| `upscale/model.onnx` | AI 키우기 모델 | [Xenova/swin2SR-realworld-sr-x4-64-bsrgan-psnr](https://huggingface.co/Xenova/swin2SR-realworld-sr-x4-64-bsrgan-psnr) |
| `fonts/` | 글꼴(Pretendard) | 이 저장소에 포함 |

이 파일이 없어도 프로그램은 켜지고, 해당 기능만 쓸 수 없습니다. 정확한 버전은 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)에 있습니다.

## 코드 구성

소스 파일은 모두 `src/` 폴더에 있습니다. 파일별 설명은 [영어 README의 Code layout](README.md#code-layout)을 참고하세요.

## 데이터와 네트워크

- 기록(폴더 탭, 자른 위치, 설정)은 `%LOCALAPPDATA%\LoveMyShelf` 아래의 텍스트·XML 파일입니다.
- 표지는 처음 켤 때 고른 표지 폴더(설정에 따라 원본 옆)에 JPG로 저장됩니다.
- 인터넷 접속은 새 버전 확인 하나뿐입니다. GitHub Releases API에 최신 태그를 한 번 묻고, 설명서 겸 옵션에서 끌 수 있습니다. 사용 통계는 보내지 않습니다.

## 상태와 의견

버전 1.0.1. Windows 11에서만 개발·시험했습니다. 버그, 질문, 제안은 [Issues](https://github.com/LoveMyShelf/LoveMyShelf/issues)에 남겨 주세요. 연락처: lovemyshelf.app@outlook.com

## 라이선스

Love My Shelf는 [GPL-3.0](LICENSE)입니다. 함께 배포하는 부품(libmpv, FFmpeg, ONNX Runtime, Swin2SR, Pretendard)은 각자의 라이선스를 따릅니다. [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)를 보세요.

시연 영상의 강아지 영상은 Pexels([Nothing Ahead](https://www.pexels.com/video/11622180/))의 것으로, [Pexels 라이선스](https://www.pexels.com/license/)에 따라 사용했습니다.
