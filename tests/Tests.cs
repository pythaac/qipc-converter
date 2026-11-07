using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QipcConverter
{
    public static class Tests
    {
        private static int passed;
        private static void Check(bool success, string name)
        {
            if (!success) throw new Exception("FAIL: " + name);
            passed++; Console.WriteLine("PASS: " + name);
        }
        private static void Reject(Action action, string name)
        {
            try { action(); } catch (InvalidDataException) { Check(true, name); return; }
            catch (EndOfStreamException) { Check(true, name); return; }
            throw new Exception("FAIL: accepted invalid data: " + name);
        }
        private static byte[] Fixture(bool audio, bool thumbnail, int frameStep, byte marker = 7, int frames = 30, int configuration = 0)
        {
            byte[] media;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                for (int i = 0; i < frames; i++)
                {
                    writer.Write((ulong)(1743790050038L + i * frameStep)); writer.Write((ushort)0); writer.Write((byte)2); writer.Write((byte)(i == 0 ? 1 : 0));
                    byte[] payload = i == 0
                        ? new byte[] { 0, 0, 0, 1, 64, 1, 1, 1, 0, 0, 0, 1, 66, 1, 1, (byte)(configuration == 0 ? frameStep : configuration),
                            0, 0, 0, 1, 68, 1, 1, 1, 0, 0, 0, 1, 38, 1, 1, 1 }
                        : new byte[] { 0, 0, 0, 1, 2, 1, 1, 1 };
                    writer.Write((uint)payload.Length); writer.Write((uint)0); writer.Write(payload);
                    if (audio)
                    {
                        writer.Write((ulong)(1743790050040L + i * frameStep)); writer.Write((ushort)0); writer.Write((byte)4); writer.Write((byte)130);
                        writer.Write((uint)8); writer.Write((uint)0); writer.Write(new byte[] { 255, 241, 96, 64, 0, 0, 0, 0 });
                    }
                }
                media = stream.ToArray();
            }
            byte[] header = new byte[26]; header[0] = marker; header[1] = header[2] = 1;
            Buffer.BlockCopy(BitConverter.GetBytes((uint)(media.Length + (thumbnail ? 14 : 0))), 0, header, 3, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((uint)media.Length), 0, header, 22, 4);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new byte[512]); writer.Write(header); writer.Write(media);
                if (thumbnail) { writer.Write((byte)1); writer.Write((uint)5); writer.Write((uint)5); writer.Write(new byte[] { 255, 216, 0, 255, 217 }); }
                writer.Write((byte)1); writer.Write((byte)1); writer.Write((uint)538); writer.Write(header, 3, 23);
                writer.Write(new byte[] { 7, 0, 1, 1, 3, 4 }); // allowed stale tail
                return stream.ToArray();
            }
        }
        private static ExtractionInfo Parse(string root, byte[] bytes)
        {
            using (var work = new OwnedDirectory(root))
            {
                string input = Path.Combine(work.Path, "fixture.bin"); File.WriteAllBytes(input, bytes);
                return QipcReader.Extract(input, Path.Combine(work.Path, "video.hevc"), Path.Combine(work.Path, "audio.aac"), null);
            }
        }
        private static byte[] JoinFixtures(params byte[][] inputs)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            using (var index = new MemoryStream())
            using (var indexWriter = new BinaryWriter(index))
            {
                writer.Write(new byte[512]);
                for (int i = 0; i < inputs.Length; i++)
                {
                    byte[] bytes = inputs[i];
                    int length = (int)BitConverter.ToUInt32(bytes, 515);
                    for (int p = 538; p < 538 + length; p += 20 + (int)BitConverter.ToUInt32(bytes, p + 12))
                    {
                        byte[] time = BitConverter.GetBytes(BitConverter.ToUInt64(bytes, p) + (ulong)(i * 2000));
                        Buffer.BlockCopy(time, 0, bytes, p, 8);
                    }
                    indexWriter.Write((byte)1); indexWriter.Write((byte)1); indexWriter.Write((uint)(stream.Position + 26)); indexWriter.Write(bytes, 515, 23);
                    writer.Write(bytes, 512, 26 + length);
                }
                writer.Write(index.ToArray()); return stream.ToArray();
            }
        }
        private static void Drop(Control target, string[] paths)
        {
            var data = new DataObject(DataFormats.FileDrop, paths);
            var args = new DragEventArgs(data, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
            typeof(Control).GetMethod("OnDragEnter", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, new object[] { args });
            if (args.Effect != DragDropEffects.Copy) throw new Exception("DragEnter did not accept FileDrop.");
            typeof(Control).GetMethod("OnDragDrop", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, new object[] { args });
        }
        private static T Field<T>(object value, string field) { return (T)value.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value); }

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 3 && args[0] == "--integration") return Integration(args[1], args[2]);
                if (args.Length == 2 && args[0] == "--inspect")
                {
                    using (var work = new OwnedDirectory(Path.GetTempPath()))
                    {
                        var info = QipcReader.Extract(args[1], Path.Combine(work.Path, "v.hevc"), Path.Combine(work.Path, "a.aac"), null);
                        Console.WriteLine("Frames=" + info.VideoFrames + " Chunks=" + info.Chunks + " Audio=" + info.AudioPackets + " FPS=" + info.FramesPerSecond);
                    }
                    return 0;
                }
                string testRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-work");
                Directory.CreateDirectory(testRoot);
                using (var root = new OwnedDirectory(testRoot))
                {
                    var info = Parse(root.Path, Fixture(true, true, 67));
                    Check(info.VideoFrames == 30 && info.AudioPackets == 30 && info.Chunks == 1 && info.Thumbnails == 1, "packet boundaries, audio, thumbnail, index, stale trailer");
                    info = Parse(root.Path, Fixture(false, false, 67));
                    Check(info.VideoFrames == 30 && info.AudioPackets == 0, "video without audio");
                    Reject(() => Parse(root.Path, new byte[1024]), "unknown BIN signature");
                    byte[] corrupt = Fixture(true, true, 67); corrupt[538 + 10] = 9;
                    Reject(() => Parse(root.Path, corrupt), "unknown packet type");
                    corrupt = Fixture(true, true, 67); corrupt[538 + 12] = 255;
                    Reject(() => Parse(root.Path, corrupt), "invalid payload boundaries");
                    corrupt = Fixture(true, true, 67); corrupt[corrupt.Length - 10] ^= 1;
                    Reject(() => Parse(root.Path, corrupt), "index mismatch");
                    corrupt = Fixture(true, true, 67); Array.Resize(ref corrupt, corrupt.Length - 20);
                    Reject(() => Parse(root.Path, corrupt), "truncated index");
                    Check(Parse(root.Path, Fixture(false, false, 33)).FramesPerSecond == 30, "automatically infer 30fps");
                    Check(Parse(root.Path, Fixture(false, false, 50)).FramesPerSecond == 20, "automatically infer 20fps");
                    Check(Parse(root.Path, Fixture(false, false, 67)).FramesPerSecond == 15, "automatically infer 15fps");
                    var mixed = Parse(root.Path, JoinFixtures(Fixture(false, false, 67), Fixture(false, false, 50)));
                    Check(mixed.VideoFrames == 60 && mixed.Sections.Count == 2 && mixed.Sections[0].Rate == 15 && mixed.Sections[1].Rate == 20
                        && Math.Abs(mixed.Duration - 3.5) < .00001 && mixed.Sections[1].Offset == mixed.Sections[0].Length,
                        "mixed 15/20fps sections preserve timing and byte ranges");
                    Reject(() => Parse(root.Path, Fixture(false, false, 1)), "implausible frame rate rejected");
                    corrupt = Fixture(false, false, 67); corrupt[558] = 8;
                    Reject(() => Parse(root.Path, corrupt), "missing HEVC start code");

                    foreach (byte marker in new byte[] { 5, 7 })
                    {
                        info = Parse(root.Path, Fixture(true, true, 67, marker));
                        Check(info.VideoFrames == 30 && info.AudioPackets == 30 && info.Thumbnails == 1 && info.FramesPerSecond == 15,
                            "block marker " + marker + ": audio, thumbnail, index and video");
                        foreach (int shortFrames in new[] { 1, 2 })
                        {
                            info = Parse(root.Path, JoinFixtures(Fixture(false, false, 50, marker),
                                Fixture(false, false, 52, marker, shortFrames, 50), Fixture(false, false, 67, marker)));
                            Check(info.VideoFrames == 60 + shortFrames && info.Sections.Count == 2
                                && info.Sections[0].Frames == 30 + shortFrames && info.Sections[0].Rate == 20
                                && info.Sections[1].Rate == 15 && Math.Abs(info.Duration - ((30 + shortFrames) / 20.0 + 2)) < .00001,
                                "block marker " + marker + ": " + shortFrames + "-frame GOP at mode switch preserves every frame and timing");
                        }
                    }
                    corrupt = Fixture(true, true, 67, 5); corrupt[512] = 6;
                    Reject(() => Parse(root.Path, corrupt), "unrecognized block marker still rejected");
                    corrupt = Fixture(true, true, 67, 5); corrupt[corrupt.Length - 10] ^= 1;
                    Reject(() => Parse(root.Path, corrupt), "marker 5 index corruption still rejected");
                    Reject(() => Parse(root.Path, Fixture(false, false, 67, 5, 1)), "short GOP without a known rate rejected");
                    Reject(() => Parse(root.Path, JoinFixtures(Fixture(false, false, 50, 5), Fixture(false, false, 67, 5, 1))),
                        "short GOP with a different configuration rejected");
                    Reject(() => Parse(root.Path, JoinFixtures(Fixture(false, false, 50, 5), Fixture(false, false, 67, 5, 2, 50))),
                        "short GOP with inconsistent cadence rejected");
                    Reject(() => Parse(root.Path, JoinFixtures(Fixture(false, false, 50, 5, 30, 1),
                        Fixture(false, false, 67, 5, 30, 1), Fixture(false, false, 67, 5, 1, 1))),
                        "ambiguous configuration rate is never guessed");

                    string inputDir = Path.Combine(root.Path, "한글 입력 폴더"); string nested = Path.Combine(inputDir, "nested"); Directory.CreateDirectory(nested);
                    string first = Path.Combine(inputDir, "one.bin"); string second = Path.Combine(nested, "TWO.BIN");
                    File.WriteAllBytes(first, Fixture(false, false, 67)); File.WriteAllBytes(second, Fixture(false, false, 67));
                    File.WriteAllText(Path.Combine(nested, "ignore.txt"), "not a recording");
                    DateTime modified = new DateTime(2025, 4, 4, 19, 21, 26, DateTimeKind.Local); File.SetLastWriteTime(first, modified); File.SetLastWriteTime(second, modified);
                    Discovery found = Discovery.Find(new[] { inputDir, first, nested });
                    Check(found.Files.Count == 2, "recursive discovery, case-insensitive extension, path deduplication");
                    Check(new InputFile(first).OutputStem == "2025-04-04_19-21-26", "filename uses local modified time, not recording time");
                    Check(Discovery.OutputFolder(new[] { second }) == nested && Discovery.OutputFolder(new[] { inputDir }) == inputDir, "output file resolves to parent / folder resolves directly");
                    bool multipleRejected = false; try { Discovery.OutputFolder(new[] { first, second }); } catch (ArgumentException) { multipleRejected = true; }
                    Check(multipleRejected, "multiple output locations rejected");
                    var changed = new InputFile(first); File.AppendAllText(first, "changed");
                    bool changeRejected = false; try { changed.CheckUnchanged(); } catch (IOException) { changeRejected = true; }
                    Check(changeRejected, "changed input detected");

                    string publishDir = Path.Combine(root.Path, "outputs"); Directory.CreateDirectory(publishDir);
                    File.WriteAllText(Path.Combine(publishDir, "same.mp4"), "keep");
                    string temp1 = Path.Combine(root.Path, "a.tmp"); string temp2 = Path.Combine(root.Path, "b.tmp"); File.WriteAllText(temp1, "a"); File.WriteAllText(temp2, "b");
                    Task<string> a = Task.Run(() => Converter.Publish(temp1, publishDir, "same"));
                    Task<string> b = Task.Run(() => Converter.Publish(temp2, publishDir, "same")); Task.WaitAll(a, b);
                    Check(a.Result != b.Result && File.ReadAllText(Path.Combine(publishDir, "same.mp4")) == "keep", "concurrent publishing never overwrites existing output");
                    Check(File.Exists(Path.Combine(publishDir, "same_2.mp4")) && File.Exists(Path.Combine(publishDir, "same_3.mp4")), "numbered collisions");
                    Check(Ffmpeg.Quote("C:\\한글 폴더\\") == "\"C:\\한글 폴더\\\\\"", "Windows argument trailing-backslash quoting");

                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    using (var form = new MainForm(new string[0]))
                    {
                        IntPtr handle = form.Handle;
                        Control inputDrop = form.Controls.Find("InputDrop", true).Single();
                        Control outputDrop = form.Controls.Find("OutputDrop", true).Single();
                        // Exercise the real FileDrop handlers, including a child label's handler.
                        Drop(inputDrop.Controls[0].Controls[0], new[] { inputDir });
                        var timer = Stopwatch.StartNew();
                        while (Field<bool>(form, "scanning") && timer.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(10); }
                        Check(Field<List<InputFile>>(form, "inputs").Count == 2, "input drag-drop event recursively adds files");
                        Drop(outputDrop, new[] { second });
                        Check(Field<TextBox>(form, "outputPath").Text == nested, "output drag-drop event accepts a file's parent");
                        Drop(outputDrop, new[] { publishDir });
                        Check(Field<TextBox>(form, "outputPath").Text == publishDir, "output drag-drop event accepts a folder");
                        Check(Field<Button>(form, "start").Enabled, "convert button enabled when input and output are set");
                    }
                }
                Console.WriteLine("ALL PASSED: " + passed);
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
        }
        private static int Integration(string source, string output)
        {
            Directory.CreateDirectory(output);
            string before = Ffmpeg.Hash(source);
            using (var runtime = new OwnedDirectory(Path.GetTempPath()))
            {
                string ffmpeg = Ffmpeg.ExtractBundled(runtime.Path);
                int last = -1;
                var result = Converter.Convert(new InputFile(source), output, ffmpeg, delegate(ConversionProgress p)
                {
                    int percent = (int)(p.Fraction * 100);
                    if (percent / 10 != last / 10 || p.Fraction == 1) { Console.WriteLine(percent + "% " + p.Stage); last = percent; }
                });
                Check(before == Ffmpeg.Hash(source), "integration preserves input SHA-256");
                Check(result.Info.VideoFrames == result.ValidatedFrames, "integration validates every extracted video frame");
                Check(Directory.GetDirectories(output, ".qipc-work-*").Length == 0, "integration cleans temporary streams");
                Console.WriteLine("OUTPUT=" + result.Output);
                Console.WriteLine("FRAMES=" + result.ValidatedFrames + " SECONDS=" + result.Info.Duration);
            }
            return 0;
        }
    }
}
