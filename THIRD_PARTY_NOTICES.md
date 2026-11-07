# Third-party software

QIPC Converter starts FFmpeg as a separate process. The portable application contains an unmodified, gzip-compressed FFmpeg executable.

- FFmpeg: 7.1-essentials_build-www.gyan.dev, Windows x64, GCC 14.2.0.
- Copyright (c) 2000–2024 the FFmpeg developers and the respective bundled-library authors.
- Binary license: GNU GPL version 3 or (at your option) any later version. See `GPL-3.0.txt` in the distribution and `licenses/GPL-3.0.txt` in this source tree.
- Binary SHA-256: `2ce797a0f88d7f067180338fb227f7b1928ea727bd9a4d7a1d022f7c52af71a3`.
- Binary provenance: `imageio_ffmpeg-0.6.0-py3-none-win_amd64.whl`, https://pypi.org/project/imageio-ffmpeg/0.6.0/ . The imageio Python wrapper is not included or imported by this app.
- Upstream binary provider and build information: https://www.gyan.dev/ffmpeg/builds/ .
- FFmpeg 7.1 source: https://ffmpeg.org/releases/ffmpeg-7.1.tar.xz .
- FFmpeg source repository/tag: https://github.com/FFmpeg/FFmpeg/tree/n7.1 .
- Build tooling / dependency source references: https://github.com/rdp/ffmpeg-windows-build-helpers .

FFmpeg is free software, without any warranty; without even the implied warranty of merchantability or fitness for a particular purpose. The FFmpeg binary's license is separate from the application's source code. Keep this notice and the license with redistributed application builds; make corresponding source for FFmpeg and its enabled dependencies available as required by that license when publishing binaries. No license for the application's own source is assigned by this notice.

The full build configuration can be obtained by running the extracted executable with `-buildconf`. This app uses HEVC/AAC demuxing, MP4 muxing, bitstream filtering, and decoding; it does not re-encode video.
