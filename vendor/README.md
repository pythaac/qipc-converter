`build.ps1`에 검증된 FFmpeg 경로를 전달하거나 `tools/fetch-ffmpeg.ps1`로 가져옵니다.
실행 파일 및 압축 리소스는 `.gitignore`에 의해 Git에서 제외됩니다.
프로그램은 이 바이너리를 압축 리소스로 포함하고 실행 시 SHA-256을 검사합니다.
