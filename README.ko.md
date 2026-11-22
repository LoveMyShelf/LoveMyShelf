# Love My Shelf

<img src="docs/images/icon.png" width="64" align="right" alt="">

사진, GIF, 영상이 뒤섞여 쌓인 폴더를 한눈에 훑어보는 Windows 프로그램입니다. 나중에 쉽게 알아볼 수 있도록 원하는 장면을 9:16 세로 표지로 만들어 둘 수 있습니다.

[English](README.md) · [설명서](GUIDE.ko.md) · [변경 사항](CHANGELOG.md) · [다운로드](https://github.com/LoveMyShelf/LoveMyShelf/releases)

## 만든 이유

다운로드 폴더나 짤 저장 폴더에 파일이 수천 개 쌓이면 파일 탐색기로는 찾기가 힘듭니다. 썸네일은 작고, 영상은 엉뚱한 첫 장면이 보여서 결국 하나씩 열어 보게 됩니다.

Love My Shelf는 폴더 전체를 큰 썸네일로 보여 줍니다. 사진이나 영상의 한 장면을 세로로 잘라 표지로 저장해 두면, 그 파일은 다음부터 표지로 보입니다.

원본 파일은 건드리지 않습니다. 표지는 항상 별도 파일로 저장됩니다.

## 기능

- **큰 썸네일로 보기**: 사진, 움직이는 GIF·WebP, 영상을 모두 볼 수 있습니다. 이름 검색, 정렬, 표지 유무 필터를 지원합니다. 표지가 없는 파일은 Windows 썸네일을 그대로 써서, 파일이 많은 폴더도 빠르게 열립니다.
- **파일 탐색기처럼 쓰기**: 왼쪽 폴더 나무(즐겨찾기, OneDrive, 내 PC, 네트워크), 주소창, 뒤로·앞으로·위로, 탐색기와 같은 8가지 폴더 보기, 마우스로 끌어 범위 선택, Ctrl+C/X/V, 파일·폴더·빈 곳의 Windows 오른쪽 클릭 메뉴를 그대로 쓸 수 있습니다.
- **표지 만들기**: 9:16 틀 안에서 휠로 확대하고 끌어서 위치를 맞춘 뒤 JPG로 저장합니다. 파일 하나에 표지를 여러 장 만들 수 있습니다. 사진의 회전 정보(EXIF)도 반영됩니다.
- **영상에서 장면 고르기**: 영상을 멈추면 그 앞뒤 장면을 한 화면에 펼쳐서 고를 수 있습니다. 장면 간격은 바꿀 수 있습니다(기본 0.2초).
- **움직이는 표지**: 같은 9:16 구도로 영상의 짧은 구간을 GIF, WebP, MP4로 저장합니다. 구간은 손잡이 두 개로 고르고, 속도(0.5~2배)와 재생 방식(반복, 왕복, 거꾸로)을 정할 수 있습니다. 미리보기에서는 실제로 저장될 파일이 그대로 재생됩니다.
- **표지 모아 보기**: 여러 폴더에 흩어진 표지를 한 화면에서 볼 수 있습니다.
- **AI로 선명하게 키우기(4배)**: Swin2SR 모델을 ONNX Runtime으로 따로 실행해서, 키우는 동안에도 프로그램이 느려지지 않게 했습니다. 키운 결과는 원래 표지와 나란히 비교해 보고 고를 수 있습니다.
- **파일 관리**: 이름 바꾸기(F2), 휴지통으로 보내기(Delete), 다른 폴더로 옮기기를 할 수 있고, 표지도 원본을 따라 함께 옮겨집니다. 프로그램 밖에서 옮기거나 이름을 바꾼 파일도 Windows 파일 ID와 파일 내용으로 다시 찾아냅니다.
- **영상 재생**: libmpv로 재생합니다(하드웨어 가속). 키보드로 앞뒤 이동, 한 장면씩 넘기기, 속도·밝기 조절이 되고, 재생 바에 마우스를 올리면 미리보기가 나옵니다.
- **복사·공유·끌어다 놓기**: 표지나 원본을 다른 프로그램으로 넘길 수 있습니다. 파일을 Windows에 넘겨줄 뿐, 인터넷에 올리지는 않습니다.
- 밝은 테마와 어두운 테마, 한국어와 영어를 지원합니다.

지원하는 파일: JPG, PNG, BMP, TIFF, GIF, WebP, 그리고 FFmpeg·mpv로 열 수 있는 영상(MP4, MOV, MKV, WEBM, AVI 등)

## 시연 영상

**영상으로 표지 만들기**: 영상을 멈추고 앞뒤 장면 중 원하는 장면을 고른 뒤, 9:16 틀을 맞춰 저장합니다. 저장하면 목록에 표지가 보이고, 표지를 누르면 원본이 열립니다.

https://github.com/user-attachments/assets/c3bf3a02-9c82-4898-a5b9-30a3c739b787

**표지는 영상을 따라다닙니다**: 영상 하나에 표지를 여러 개 만들고 영상을 다른 폴더로 옮기면, 표지도 모두 같이 옮겨집니다. 필요 없는 표지는 한 번에 휴지통으로 보낼 수 있고, 원본 영상은 그대로 남습니다.

https://github.com/user-attachments/assets/080da44d-49cb-423e-b5c4-aaaef3ea5723

**움직이는 표지 만들기**: 구간을 고르고 속도와 재생 방식을 정해서 저장합니다. 만드는 동안에도 다른 작업을 계속할 수 있습니다. 다 만든 뒤 목록에서 표지에 마우스를 올리면 표지가 움직입니다.

https://github.com/user-attachments/assets/dd3a9104-e4df-4069-b19e-a975ee2fa7ce

**창 두 개로 파일 옮기기**: 새 창에 다른 폴더를 열고, 파일 여러 개를 골라 끌어다 놓으면 옮겨집니다. Ctrl+Z로 되돌릴 수 있습니다. 옮긴 영상의 움직이는 표지도 새 폴더에서 그대로 움직입니다.

https://github.com/user-attachments/assets/e10b8489-304c-4249-9db4-c6d26a9cdd7e

## 필요한 환경

- Windows 10 또는 11 (64비트)
- .NET Framework 4.8 (Windows 10 1903 이후에는 기본으로 설치되어 있습니다)

## 설치

[다운로드 페이지](https://github.com/LoveMyShelf/LoveMyShelf/releases)에서 `LoveMyShelf-Windows.zip`을 받아 압축을 모두 풀고 `LoveMyShelf.exe`를 실행하면 됩니다. 따로 설치할 필요는 없습니다.

프로그램에 코드 서명이 없어서 처음 실행할 때 Windows 보안 경고(SmartScreen)가 뜰 수 있습니다. 이때는 **추가 정보** → **실행**을 누르세요. 다운로드 페이지에 ZIP 파일의 SHA-256 값을 적어 두었습니다.

새 버전이 나오면 새 ZIP을 기존 폴더에 덮어쓰면 됩니다. 삭제하려면 프로그램 폴더와 `%LOCALAPPDATA%\LoveMyShelf` 폴더를 지우면 됩니다.

## 소스에서 빌드하기

Windows에 기본으로 들어 있는 .NET Framework 4.8 C# 컴파일러로 빌드합니다. Visual Studio, SDK, NuGet은 필요 없습니다.

```powershell
.\build.ps1
```

`src/` 폴더의 `*.cs` 파일을 모두 컴파일해서 `build.ps1`과 같은 폴더에 `LoveMyShelf.exe`(WPF)를 만듭니다. 프로그램이 켜져 있어도 빌드할 수 있고, 새로 만든 파일은 다음 실행부터 적용됩니다.

저장소에는 소스 코드만 있습니다. 모든 기능을 쓰려면 아래 파일을 `LoveMyShelf.exe`와 같은 폴더에 넣어 주세요. 다운로드한 ZIP에서 복사하는 게 가장 간단합니다.

| 경로 | 용도 | 출처 |
| --- | --- | --- |
| `libmpv-2.dll` | 영상 재생 | [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake) |
| `tools/ffmpeg.exe`, `tools/ffprobe.exe`, `tools/*.dll` | 장면 읽기, 움직이는 표지 저장 | [FFmpeg-Builds](https://github.com/FFmpeg-Builds/FFmpeg-Builds) (GPL shared) |
| `upscale/onnxruntime.dll`, `upscale/DirectML.dll`, `upscale/onnxruntime_providers_shared.dll` | AI로 키우기 | `onnxruntime-directml` 1.24.4 (PyPI) |
| `upscale/model.onnx` | AI로 키우기 모델 | [Xenova/swin2SR-realworld-sr-x4-64-bsrgan-psnr](https://huggingface.co/Xenova/swin2SR-realworld-sr-x4-64-bsrgan-psnr) |
| `fonts/` | 글꼴(Pretendard) | 이 저장소에 포함 |

이 파일들이 없어도 프로그램은 실행되고, 관련 기능만 쓸 수 없습니다. 정확한 버전은 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)에 있습니다.

## 코드 구성

소스 코드는 모두 `src/` 폴더에 있습니다. 파일별 설명은 [영어 README의 Code layout](README.md#code-layout)을 참고하세요.

## 데이터와 인터넷 사용

- 추가한 폴더, 표지 위치, 설정 같은 기록은 `%LOCALAPPDATA%\LoveMyShelf` 폴더에 텍스트·XML 파일로 저장됩니다.
- 표지는 처음 실행할 때 고른 표지 폴더에 JPG로 저장됩니다. 설정에서 원본 옆에 저장하도록 바꿀 수도 있습니다.
- 인터넷은 새 버전 확인에만 씁니다. GitHub에 최신 버전 번호를 한 번 물어보는 것이 전부이고, 「설명서 겸 옵션」에서 끌 수 있습니다. 사용 기록은 수집하지 않습니다.

## 현재 상태와 문의

현재 버전은 1.0.3입니다. Windows 11에서만 개발하고 테스트했습니다. 버그 제보, 질문, 제안은 [Issues](https://github.com/LoveMyShelf/LoveMyShelf/issues)에 남겨 주세요. 메일: lovemyshelf.app@outlook.com

## 라이선스

Love My Shelf는 [GPL-3.0](LICENSE) 라이선스를 따릅니다. 함께 들어 있는 libmpv, FFmpeg, ONNX Runtime, Swin2SR, Pretendard는 각자의 라이선스를 따르며, 자세한 내용은 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)에 있습니다.

시연 영상 속 강아지 영상은 Pexels의 [Nothing Ahead](https://www.pexels.com/video/11622180/) 님 영상이며, [Pexels 라이선스](https://www.pexels.com/license/)에 따라 사용했습니다.
