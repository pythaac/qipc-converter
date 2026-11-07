# QIPC Converter

QIPC 카메라의 `.bin` 녹화를 원본 해상도 MP4로 변환하는 Windows 데스크톱 프로그램입니다. C# / Windows Forms / .NET Framework 4.8로 작성했고, 테스트한 FFmpeg 7.1 실행 파일을 압축해 단일 EXE에 포함합니다. Python 설치, 서버, 계정, 인터넷 연결 없이 실행합니다.

## 사용

`QIPCConverter.exe`를 더블클릭 → BIN 파일/폴더 추가 → 출력 폴더 선택 → **변환하기**.

- 입력: 여러 파일, 여러 폴더를 드래그 앤 드롭. 폴더는 재귀적으로 탐색. 파일 찾기/폴더 찾기 제공.
- 출력: 폴더 드래그 앤 드롭, 폴더 찾기, 경로 직접 입력. 파일을 놓으면 상위 폴더 선택.
- 이름: 입력 파일의 `LastWriteTime`(현재 PC의 현지 시각, Windows 탐색기의 수정 날짜)으로 `yyyy-MM-dd_HH-mm-ss.mp4` 생성. 중복은 `_2`, `_3` 순서로 저장하며 기존 파일을 덮어쓰지 않음.
- 모든 출력은 선택한 폴더에 평면적으로 저장. 파일마다 MP4 하나.
- 원본 해상도 / HEVC / AAC 유지. 재인코딩 없음. 녹화 구간의 프레임 속도를 자동 판별하고 시간 공백 제외. 한 파일 안에서 15fps/20fps가 바뀌는 경우에도 구간별 재생 속도 유지.
- 전체/현재 파일 진행률, 단계, 파일별 결과와 오류 표시. 실패한 입력 다음 파일도 계속 처리.
- 작업은 백그라운드에서 실행. ‘현재 파일 후 중지’는 처리 중인 파일을 완료한 뒤 대기 작업을 중지하며 프로세스를 강제 종료하지 않음.
- 결과는 전체 디코딩 검사와 프레임 수 검사를 통과한 뒤에만 공개. 임시 디렉터리는 정리.

사용 대상은 Windows 10/11 x64와 .NET Framework 4.8입니다. HEVC 지원 재생기가 필요합니다. macOS 앱은 제공하지 않습니다. 이 EXE는 코드 서명하지 않은 로컬 빌드입니다.

## 소스와 빌드

```text
src/                      UI, 변환 엔진, 실행 진입점, manifest
tests/                    단위·통합 테스트 (외부 테스트 패키지 불필요)
tools/fetch-ffmpeg.ps1     고정 버전 FFmpeg 가져오기·해시 검사
build.ps1                 EXE 및 테스트 실행 파일 빌드
docs/                     배포용 사용 설명서
licenses/                 FFmpeg 라이선스
vendor/                   로컬 빌드 의존성 (바이너리는 Git 제외)
artifacts/                빌드 및 테스트 결과 (Git 제외)
```

Windows PowerShell에서 이 폴더로 이동해 실행합니다.

```powershell
.\build.ps1
# 또는 기존에 내려받은 검증된 바이너리 사용
.\build.ps1 -FfmpegPath 'C:\path\ffmpeg-win-x86_64-v7.1.exe'
.\artifacts\QipcConverter.Tests.exe
```

첫 빌드에서만 PyPI의 고정 버전 wheel을 내려받습니다. 최종 바이너리의 SHA-256은 코드와 빌드 스크립트에 고정되어 있습니다. 이후에는 기존 `vendor/ffmpeg.exe`를 재사용합니다. 빌드는 Windows에 포함된 .NET Framework C# 컴파일러를 사용하므로 별도 NuGet 패키지나 .NET SDK가 필요하지 않습니다. Visual Studio로 `QipcConverter.csproj`를 열려면 한 번 `build.ps1`을 실행해 리소스와 아이콘을 준비하고 .NET Framework 4.8 targeting pack을 설치하세요.

실제 파일 통합 검사(추출→MP4→전체 디코딩):

```powershell
.\artifacts\QipcConverter.Tests.exe --integration 'C:\recordings\qipc_00000.bin' 'C:\recordings\test-output'
```

실행 파일과 Git용 소스만 분리하여 압축하려면:

```powershell
.\tools\package.ps1
```

`artifacts/packages/`에 실행용 폴더, 실행용 ZIP, 소스 ZIP이 만들어집니다. 소스 ZIP에는 녹화 파일, 테스트 출력, 컴파일 결과, FFmpeg 바이너리를 넣지 않습니다. 구체적인 테스트 결과는 [docs/TESTING.md](docs/TESTING.md)에 기록했습니다.

빌드할 때 기존 EXE가 실행 중이면 창을 닫거나 `build.ps1 -BuildDirectory 'C:\path\new-build'`로 다른 빌드 폴더를 지정하세요. 패키징도 같은 `-BuildDirectory`를 받습니다. ZIP이 이미 있으면 덮어쓰지 않으므로 새로운 `-Destination`을 선택하세요.

배포 결과는 `artifacts/QIPCConverter-Windows/`입니다. Git에는 이 소스 폴더를 올리면 됩니다. BIN/MP4, 임시 데이터, FFmpeg 바이너리, 빌드 결과는 `.gitignore`로 제외했습니다. 저장소 생성, 커밋, 원격 업로드는 하지 않습니다. EXE 공개 배포 시 함께 제공할 FFmpeg 라이선스 및 해당 소스 안내는 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)를 확인하세요. 앱 자체 소스의 공개 라이선스는 저장소 소유자가 정하도록 미지정입니다.

## 파일 형식과 한계

테스트한 QIPC의 단일 채널, HEVC/AAC 블록 저장 형식을 지원합니다. 모든 BIN 형식의 범용 변환기는 아닙니다. 블록과 패킷 길이, 코덱 시작 코드, JPEG 썸네일 경계, 현재 녹화 인덱스를 검사하며 지원하지 않는 구조는 오류로 표시합니다. 미사용 영역의 오래된 인덱스는 변환하지 않습니다. 일부 부가정보(SEI type 39/40)가 엄격한 HEVC 디코딩에 오류를 일으켜 제거합니다. 영상 픽셀에 찍힌 촬영 날짜/워터마크와 오디오는 유지합니다. 각 GOP의 타임스탬프로 정수 fps를 판별하며, fps가 달라지는 구간은 같은 시간 기준의 MP4로 각각 작성한 뒤 영상만 이어 붙여 전체 AAC와 합칩니다. 판별할 수 없는 프레임 속도는 오류로 처리합니다.

파일은 스트리밍으로 읽으며 메모리에 전체 BIN을 적재하지 않습니다. 임시 원시 스트림과 MP4를 출력 볼륨의 고유 작업 폴더에 작성하므로 진행 중에는 입력 한 파일 크기의 약 2~3배 여유 공간이 추가로 필요합니다. 완성본은 같은 볼륨에서 원자적으로 이동하며, 다른 프로세스가 같은 이름을 먼저 만들면 다음 번호를 재시도합니다. 입력 수정 시각과 크기는 변환 전후 재확인합니다. 정션/심볼릭 링크 폴더는 순환 탐색 방지를 위해 건너뛰고 기록합니다.

UI 구현 참고: [Microsoft Windows Forms drag-and-drop](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/advanced/walkthrough-performing-a-drag-and-drop-operation-in-windows-forms). 변환 도구: [FFmpeg](https://ffmpeg.org/) / [Gyan Windows builds](https://www.gyan.dev/ffmpeg/builds/).
