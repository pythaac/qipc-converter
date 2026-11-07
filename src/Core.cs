using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace QipcConverter
{
    public sealed class InputFile
    {
        public string Path { get; private set; }
        public long Length { get; private set; }
        public DateTime ModifiedUtc { get; private set; }
        public DateTime ModifiedLocal { get { return ModifiedUtc.ToLocalTime(); } }
        public string OutputStem { get { return ModifiedLocal.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture); } }
        public InputFile(string path)
        {
            var info = new FileInfo(System.IO.Path.GetFullPath(path));
            if (!info.Exists) throw new FileNotFoundException("입력 파일을 찾을 수 없습니다.", path);
            Path = info.FullName;
            Length = info.Length;
            ModifiedUtc = info.LastWriteTimeUtc;
        }
        public void CheckUnchanged()
        {
            var info = new FileInfo(Path);
            if (!info.Exists || info.Length != Length || info.LastWriteTimeUtc != ModifiedUtc)
                throw new IOException("입력 파일이 변경되었습니다. 목록에서 제거한 뒤 다시 추가해 주세요.");
        }
    }

    public sealed class Discovery
    {
        public readonly List<InputFile> Files = new List<InputFile>();
        public readonly List<string> Warnings = new List<string>();
        public static Discovery Find(IEnumerable<string> paths)
        {
            var result = new Discovery();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(paths.Reverse());
            while (pending.Count > 0)
            {
                string path = pending.Pop();
                try
                {
                    path = System.IO.Path.GetFullPath(path);
                    if (!seen.Add(path)) continue;
                    if (Directory.Exists(path))
                    {
                        // Do not follow junctions/symlinks: avoid cycles and duplicate trees.
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        {
                            result.Warnings.Add("연결된 폴더는 제외했습니다: " + path);
                            continue;
                        }
                        foreach (string child in Directory.GetFiles(path)) pending.Push(child);
                        foreach (string child in Directory.GetDirectories(path)) pending.Push(child);
                    }
                    else if (File.Exists(path))
                    {
                        if (String.Equals(System.IO.Path.GetExtension(path), ".bin", StringComparison.OrdinalIgnoreCase))
                            result.Files.Add(new InputFile(path));
                    }
                    else result.Warnings.Add("경로를 찾을 수 없습니다: " + path);
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)) throw;
                    result.Warnings.Add(path + " — " + ex.Message);
                }
            }
            result.Files.Sort(delegate(InputFile a, InputFile b)
            {
                int date = a.ModifiedUtc.CompareTo(b.ModifiedUtc);
                return date != 0 ? date : StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path);
            });
            return result;
        }

        public static string OutputFolder(IEnumerable<string> dropped)
        {
            string[] paths = dropped.ToArray();
            if (paths.Length != 1) throw new ArgumentException("출력 위치에는 폴더 또는 파일 하나만 놓아 주세요.");
            string path = System.IO.Path.GetFullPath(paths[0]);
            if (Directory.Exists(path)) return path;
            if (File.Exists(path)) return System.IO.Path.GetDirectoryName(path);
            throw new IOException("출력 폴더를 찾을 수 없습니다.");
        }
    }

    public sealed class OwnedDirectory : IDisposable
    {
        public string Path { get; private set; }
        private readonly string parent;
        private readonly string name;
        public OwnedDirectory(string root)
        {
            parent = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            name = ".qipc-work-" + Guid.NewGuid().ToString("N");
            Path = System.IO.Path.Combine(parent, name);
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            // Only remove the unique directory this instance created, never an input/output root.
            string full = System.IO.Path.GetFullPath(Path);
            if (!String.Equals(System.IO.Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase)
                || System.IO.Path.GetFileName(full) != name) throw new IOException("임시 폴더 경로가 올바르지 않습니다.");
            try { if (Directory.Exists(full)) Directory.Delete(full, true); }
            catch (IOException) { /* A scanner may briefly hold a temporary file. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    public sealed class FrameSection
    {
        public long Offset;
        public long Length;
        public long Frames;
        public int Rate;
        public double Duration { get { return (double)Frames / Rate; } }
    }
    public sealed class ExtractionInfo
    {
        public long VideoFrames;
        public long AudioPackets;
        public int Chunks;
        public int Thumbnails;
        public int RecordingGaps;
        public readonly List<FrameSection> Sections = new List<FrameSection>();
        public int FramesPerSecond { get { return Sections.Count == 1 ? Sections[0].Rate : 0; } }
        public double Duration { get { return Sections.Sum(s => s.Duration); } }
    }

    public static class QipcReader
    {
        private static byte[] Read(BinaryReader reader, int count)
        {
            byte[] bytes = reader.ReadBytes(count);
            if (bytes.Length != count) throw new InvalidDataException("BIN 파일이 중간에서 잘렸습니다.");
            return bytes;
        }
        private static void Require(bool condition, long offset, string message)
        {
            if (!condition) throw new InvalidDataException(message + " (위치 " + offset.ToString(CultureInfo.InvariantCulture) + ")");
        }
        private static string VideoConfiguration(byte[] buffer, int count)
        {
            // QIPC keyframes begin with complete Annex B VPS/SPS/PPS NAL units.
            // Only use an exact, complete configuration match for a short GOP.
            int position = 0;
            for (int type = 32; type <= 34; type++)
            {
                if (position + 6 > count || buffer[position] != 0 || buffer[position + 1] != 0
                    || buffer[position + 2] != 0 || buffer[position + 3] != 1
                    || ((buffer[position + 4] >> 1) & 63) != type) return null;
                int next = position + 6;
                while (next + 4 <= count && !(buffer[next] == 0 && buffer[next + 1] == 0
                    && buffer[next + 2] == 0 && buffer[next + 3] == 1)) next++;
                if (next + 4 > count) return null;
                position = next;
            }
            return Convert.ToBase64String(buffer, 0, position);
        }
        public static ExtractionInfo Extract(string input, string video, string audio, Action<double> progress)
        {
            var info = new ExtractionInfo();
            using (var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
            using (var reader = new BinaryReader(source))
            using (var v = new FileStream(video, FileMode.CreateNew, FileAccess.Write))
            using (var a = new FileStream(audio, FileMode.CreateNew, FileAccess.Write))
            using (var index = new MemoryStream())
            {
                Require(source.Length >= 538, 0, "지원하는 QIPC 녹화 파일이 아닙니다.");
                source.Position = 512;
                var buffer = new byte[65536];
                var configurationRates = new Dictionary<string, int>(StringComparer.Ordinal);
                ulong lastVideo = 0;
                while (source.Position + 26 <= source.Length)
                {
                    long offset = source.Position;
                    byte[] header = Read(reader, 26);
                    if (header[0] == 1 && header[1] == 1)
                    {
                        source.Position = offset;
                        break;
                    }
                    Require((header[0] == 5 || header[0] == 7) && header[1] == 1 && header[2] == 1,
                        offset, "알 수 없는 녹화 블록입니다: " + BitConverter.ToString(header, 0, 3));
                    uint length = BitConverter.ToUInt32(header, 3);
                    uint mediaLength = BitConverter.ToUInt32(header, 22);
                    long end = offset + 26L + length;
                    long mediaEnd = offset + 26L + mediaLength;
                    Require(mediaLength > 0 && mediaLength <= length && end <= source.Length, offset, "녹화 블록 길이가 잘못되었습니다.");
                    Require(offset + 26 <= UInt32.MaxValue, offset, "지원 범위를 벗어난 파일 크기입니다.");
                    index.WriteByte(1); index.WriteByte(1);
                    byte[] start = BitConverter.GetBytes((uint)(offset + 26));
                    index.Write(start, 0, 4); index.Write(header, 3, 23);
                    long framesBefore = info.VideoFrames;
                    long videoOffset = v.Position;
                    var cadence = new List<ulong>();
                    ulong chunkPrevious = 0;
                    ulong chunkFirst = 0;
                    string configuration = null;
                    while (source.Position < mediaEnd)
                    {
                        long packetOffset = source.Position;
                        Require(mediaEnd - packetOffset >= 20, packetOffset, "프레임 헤더가 잘렸습니다.");
                        byte[] packet = Read(reader, 20);
                        ulong timestamp = BitConverter.ToUInt64(packet, 0);
                        ushort channel = BitConverter.ToUInt16(packet, 8);
                        int kind = packet[10];
                        int flags = packet[11];
                        uint size = BitConverter.ToUInt32(packet, 12);
                        Require(channel == 0 && (kind == 2 || kind == 4), packetOffset, "지원하지 않는 영상 또는 음성 형식입니다.");
                        Require((kind == 2 && (flags == 0 || flags == 1)) || (kind == 4 && flags == 130), packetOffset, "지원하지 않는 프레임 형식입니다.");
                        Require(size >= (kind == 2 ? 6 : 7) && source.Position + size <= mediaEnd, packetOffset, "프레임 길이가 잘못되었습니다.");
                        Stream destination = kind == 2 ? v : a;
                        long left = size;
                        bool first = true;
                        while (left > 0)
                        {
                            int count = reader.Read(buffer, 0, (int)Math.Min(left, buffer.Length));
                            Require(count > 0, packetOffset, "프레임 데이터가 잘렸습니다.");
                            if (first)
                            {
                                if (kind == 2)
                                {
                                    Require(count >= 6 && buffer[0] == 0 && buffer[1] == 0 && buffer[2] == 0 && buffer[3] == 1,
                                        packetOffset, "HEVC 영상 시작 코드를 찾을 수 없습니다.");
                                    if (info.VideoFrames == framesBefore) configuration = VideoConfiguration(buffer, count);
                                }
                                else
                                    Require(count >= 7 && buffer[0] == 255 && buffer[1] == 241,
                                        packetOffset, "AAC 음성 헤더를 찾을 수 없습니다.");
                                first = false;
                            }
                            destination.Write(buffer, 0, count);
                            left -= count;
                        }
                        if (kind == 2)
                        {
                            if (chunkFirst == 0) chunkFirst = timestamp;
                            if (chunkPrevious > 0 && timestamp > chunkPrevious) cadence.Add(timestamp - chunkPrevious);
                            chunkPrevious = timestamp;
                            if (lastVideo > 0 && timestamp > lastVideo && timestamp - lastVideo > 200) info.RecordingGaps++;
                            if (lastVideo > 0) Require(timestamp > lastVideo, packetOffset, "영상 시각 순서가 잘못되었습니다.");
                            lastVideo = timestamp;
                            info.VideoFrames++;
                        }
                        else info.AudioPackets++;
                    }
                    // Infer rate within each GOP; gaps between recordings must not affect it.
                    long blockFrames = info.VideoFrames - framesBefore;
                    Require(blockFrames > 0, offset, "영상 프레임이 없는 녹화 블록입니다.");
                    int rate = 0;
                    if (blockFrames < 3)
                    {
                        // A mode switch can finish the old GOP after just 1–2 frames.
                        // Reuse a measured rate only for identical VPS/SPS/PPS; never
                        // derive it from the time gap between separate recordings.
                        Require(configuration != null && configurationRates.TryGetValue(configuration, out rate) && rate > 0,
                            offset, "짧은 녹화 블록의 프레임 속도를 확인할 수 없습니다.");
                        Require(cadence.All(delta => Math.Abs(delta - 1000.0 / rate) < 1000.0 / rate * .25),
                            offset, "짧은 녹화 블록의 프레임 간격이 영상 설정과 맞지 않습니다.");
                    }
                    else
                    {
                        Require(chunkPrevious > chunkFirst, offset, "프레임 속도를 판별할 영상이 부족합니다.");
                        double measured = (blockFrames - 1) * 1000.0 / (chunkPrevious - chunkFirst);
                        rate = (int)Math.Round(measured);
                        cadence.Sort();
                        Require(rate >= 1 && rate <= 60 && Math.Abs(measured - rate) / rate < .05
                            && cadence.Count == blockFrames - 1 && Math.Abs(cadence[cadence.Count / 2] - 1000.0 / rate) < 1000.0 / rate * .25,
                            offset, "일정한 프레임 속도를 판별할 수 없습니다.");
                        if (configuration != null)
                        {
                            int previousRate;
                            configurationRates[configuration] = configurationRates.TryGetValue(configuration, out previousRate)
                                && previousRate != rate ? 0 : rate;
                        }
                    }
                    FrameSection section;
                    if (info.Sections.Count == 0 || info.Sections[info.Sections.Count - 1].Rate != rate)
                    {
                        section = new FrameSection { Offset = videoOffset, Rate = rate };
                        info.Sections.Add(section);
                    }
                    else section = info.Sections[info.Sections.Count - 1];
                    section.Frames += blockFrames;
                    section.Length = v.Position - section.Offset;
                    if (source.Position < end)
                    {
                        Require(end - source.Position >= 13, source.Position, "썸네일 헤더가 잘렸습니다.");
                        byte[] jpeg = Read(reader, 9);
                        uint jpegSize = BitConverter.ToUInt32(jpeg, 1);
                        Require(jpeg[0] == 1 && jpegSize >= 4 && jpegSize == BitConverter.ToUInt32(jpeg, 5)
                            && source.Position + jpegSize == end, source.Position, "썸네일 길이가 잘못되었습니다.");
                        byte[] magic = Read(reader, 2);
                        Require(magic[0] == 255 && magic[1] == 216, source.Position, "JPEG 시작 코드를 찾을 수 없습니다.");
                        source.Position = end - 2;
                        magic = Read(reader, 2);
                        Require(magic[0] == 255 && magic[1] == 217, source.Position, "JPEG 종료 코드를 찾을 수 없습니다.");
                        info.Thumbnails++;
                    }
                    info.Chunks++;
                    if (progress != null) progress((double)source.Position / source.Length);
                }
                Require(info.VideoFrames > 0, source.Position, "영상이 없는 파일입니다.");
                byte[] expected = index.ToArray();
                byte[] actual = Read(reader, expected.Length);
                Require(expected.SequenceEqual(actual), source.Position - expected.Length, "녹화 인덱스와 영상 블록이 일치하지 않습니다.");
                // QIPC preallocates 256 MiB; bytes after the current index can be stale.
                if (progress != null) progress(1);
            }
            return info;
        }
    }

    public sealed class ConversionProgress
    {
        public readonly double Fraction;
        public readonly string Stage;
        public ConversionProgress(double fraction, string stage) { Fraction = Math.Max(0, Math.Min(1, fraction)); Stage = stage; }
    }
    public sealed class ConversionResult
    {
        public string Output;
        public ExtractionInfo Info;
        public long ValidatedFrames;
    }

    public static class Ffmpeg
    {
        public const string ExpectedSha256 = "2ce797a0f88d7f067180338fb227f7b1928ea727bd9a4d7a1d022f7c52af71a3";
        public static string Hash(string file)
        {
            using (var input = File.OpenRead(file))
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }
        public static string ExtractBundled(string directory)
        {
            string file = System.IO.Path.Combine(directory, "ffmpeg.exe");
            using (Stream packed = Assembly.GetExecutingAssembly().GetManifestResourceStream("QipcConverter.ffmpeg.gz"))
            {
                if (packed == null) throw new FileNotFoundException("프로그램에 포함된 변환 도구를 찾을 수 없습니다.");
                using (var gzip = new GZipStream(packed, CompressionMode.Decompress))
                using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write)) gzip.CopyTo(output);
            }
            if (Hash(file) != ExpectedSha256) throw new InvalidDataException("포함된 변환 도구의 무결성 검사에 실패했습니다.");
            return file;
        }
        public static string Quote(string value)
        {
            // CommandLineToArgvW quoting: works with spaces, Korean paths and trailing backslashes.
            var output = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { output.Append('\\', slashes * 2 + 1); output.Append(c); }
                else { output.Append('\\', slashes); output.Append(c); }
                slashes = 0;
            }
            output.Append('\\', slashes * 2); output.Append('"');
            return output.ToString();
        }
        public static long Run(string executable, IEnumerable<string> args, Action<long> frames)
        {
            var errors = new Queue<string>();
            long lastFrame = 0;
            var start = new ProcessStartInfo(executable, String.Join(" ", args.Select(Quote)))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using (var process = new Process { StartInfo = start })
            {
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (String.IsNullOrEmpty(e.Data)) return;
                    lock (errors) { errors.Enqueue(e.Data); if (errors.Count > 30) errors.Dequeue(); }
                };
                if (!process.Start()) throw new IOException("영상 변환 도구를 시작할 수 없습니다.");
                process.BeginErrorReadLine();
                string line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    long value;
                    if (line.StartsWith("frame=", StringComparison.Ordinal) && Int64.TryParse(line.Substring(6).Trim(), out value))
                    {
                        lastFrame = value;
                        if (frames != null) frames(value);
                    }
                }
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    string details; lock (errors) details = String.Join(Environment.NewLine, errors);
                    throw new IOException("영상 처리에 실패했습니다. " + Environment.NewLine + details);
                }
            }
            return lastFrame;
        }
    }

    public static class Converter
    {
        private static void CopyRange(string source, string target, long offset, long length)
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
            {
                input.Position = offset;
                var buffer = new byte[65536];
                while (length > 0)
                {
                    int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
                    if (read == 0) throw new InvalidDataException("추출 영상이 중간에서 잘렸습니다.");
                    output.Write(buffer, 0, read); length -= read;
                }
            }
        }
        private static List<string> VideoArguments(string video, int rate)
        {
            return new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-n", "-fflags", "+genpts", "-r", rate.ToString(CultureInfo.InvariantCulture), "-f", "hevc", "-i", video };
        }
        private static void AddVideoOutput(List<string> args, int rate, string output)
        {
            string fps = rate.ToString(CultureInfo.InvariantCulture);
            args.AddRange(new[] { "-c", "copy", "-bsf:v", "filter_units=remove_types=39|40,setts=pts=N/(" + fps + "*TB):dts=N/(" + fps + "*TB):duration=1/(" + fps + "*TB)",
                "-tag:v", "hvc1", "-video_track_timescale", "90000", "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", output });
        }
        private static long Mux(string ffmpeg, string video, string audio, string movie, string work, ExtractionInfo info, Action<double, string> report)
        {
            if (info.Sections.Count == 1)
            {
                int rate = info.Sections[0].Rate;
                var args = VideoArguments(video, rate);
                if (info.AudioPackets > 0) args.AddRange(new[] { "-i", audio });
                args.AddRange(new[] { "-map", "0:v:0" });
                if (info.AudioPackets > 0) args.AddRange(new[] { "-map", "1:a:0" });
                AddVideoOutput(args, rate, movie);
                return Ffmpeg.Run(ffmpeg, args, delegate(long frame) { report(.20 + .10 * Math.Min(1, (double)frame / info.VideoFrames), "원본 해상도 MP4 생성"); });
            }
            // Day/night mode can change 20fps -> 15fps within a BIN. Mux sections
            // separately, then concatenate video timestamps while using one continuous
            // AAC stream, avoiding drift from forcing all frames to a single rate.
            long completed = 0;
            var concat = new StringBuilder("ffconcat version 1.0\n");
            for (int i = 0; i < info.Sections.Count; i++)
            {
                FrameSection section = info.Sections[i];
                string basename = "section-" + i.ToString("D4", CultureInfo.InvariantCulture);
                string raw = System.IO.Path.Combine(work, basename + ".hevc");
                string part = System.IO.Path.Combine(work, basename + ".mp4");
                CopyRange(video, raw, section.Offset, section.Length);
                var args = VideoArguments(raw, section.Rate); args.AddRange(new[] { "-map", "0:v:0" }); AddVideoOutput(args, section.Rate, part);
                long count = Ffmpeg.Run(ffmpeg, args, delegate(long frame)
                {
                    report(.20 + .08 * Math.Min(1, (double)(completed + frame) / info.VideoFrames), "프레임 속도별 영상 연결");
                });
                if (count != section.Frames) throw new InvalidDataException("영상 구간의 프레임 수가 일치하지 않습니다.");
                File.Delete(raw); completed += count;
                concat.Append("file '").Append(basename).Append(".mp4'\n");
                concat.Append("duration ").Append(section.Duration.ToString("0.000000000", CultureInfo.InvariantCulture)).Append('\n');
            }
            File.Delete(video);
            string manifest = System.IO.Path.Combine(work, "sections.ffconcat");
            File.WriteAllText(manifest, concat.ToString(), new UTF8Encoding(false));
            var merge = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-n", "-f", "concat", "-safe", "1", "-i", manifest };
            if (info.AudioPackets > 0) merge.AddRange(new[] { "-i", audio });
            merge.AddRange(new[] { "-map", "0:v:0" });
            if (info.AudioPackets > 0) merge.AddRange(new[] { "-map", "1:a:0" });
            merge.AddRange(new[] { "-c", "copy", "-tag:v", "hvc1", "-video_track_timescale", "90000", "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", movie });
            return Ffmpeg.Run(ffmpeg, merge, delegate(long frame) { report(.28 + .02 * Math.Min(1, (double)frame / info.VideoFrames), "원본 해상도 MP4 생성"); });
        }
        public static string Candidate(string directory, string stem, int number)
        {
            return System.IO.Path.Combine(directory, stem + (number == 1 ? "" : "_" + number.ToString(CultureInfo.InvariantCulture)) + ".mp4");
        }
        public static string Publish(string temporaryFile, string directory, string stem)
        {
            for (int suffix = 1; suffix < Int32.MaxValue; suffix++)
            {
                string candidate = Candidate(directory, stem, suffix);
                if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
                try { File.Move(temporaryFile, candidate); return candidate; }
                catch (IOException) { if (!File.Exists(candidate) && !Directory.Exists(candidate)) throw; }
            }
            throw new IOException("중복되지 않는 출력 이름을 만들 수 없습니다.");
        }
        public static ConversionResult Convert(InputFile input, string outputFolder, string ffmpeg, Action<ConversionProgress> progress)
        {
            input.CheckUnchanged();
            string output = System.IO.Path.GetFullPath(outputFolder);
            Directory.CreateDirectory(output);
            Action<double, string> report = delegate(double fraction, string stage)
            {
                if (progress != null) progress(new ConversionProgress(fraction, stage));
            };
            using (var work = new OwnedDirectory(output))
            {
                string video = System.IO.Path.Combine(work.Path, "video.hevc");
                string audio = System.IO.Path.Combine(work.Path, "audio.aac");
                string movie = System.IO.Path.Combine(work.Path, "movie.mp4");
                report(0, "영상·음성 추출");
                ExtractionInfo info = QipcReader.Extract(input.Path, video, audio, delegate(double value) { report(value * .20, "영상·음성 추출"); });
                report(.20, "원본 해상도 MP4 생성");
                long muxed = Mux(ffmpeg, video, audio, movie, work.Path, info, report);
                if (muxed != info.VideoFrames) throw new InvalidDataException("MP4 프레임 수가 원본과 일치하지 않습니다.");
                report(.30, "전체 영상·음성 검사");
                long verified = Ffmpeg.Run(ffmpeg, new[] { "-hide_banner", "-v", "error", "-nostdin", "-xerror", "-err_detect", "explode",
                    "-threads", "4", "-i", movie, "-map", "0:v:0", "-map", "0:a:0?", "-progress", "pipe:1", "-nostats", "-f", "null", "-" },
                    delegate(long frame) { report(.30 + .69 * Math.Min(1, (double)frame / info.VideoFrames), "전체 영상·음성 검사"); });
                if (verified != info.VideoFrames) throw new InvalidDataException("검사된 프레임 수가 원본과 일치하지 않습니다.");
                input.CheckUnchanged();
                string final = Publish(movie, output, input.OutputStem);
                report(1, "완료");
                return new ConversionResult { Output = final, Info = info, ValidatedFrames = verified };
            }
        }
    }
}
