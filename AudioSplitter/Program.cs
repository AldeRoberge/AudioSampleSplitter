using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AudioSplitter
{
    class Program
    {
        const double _500MB = 500 * 1024 * 1024;

        // Tolerance in seconds when matching silence endpoints
        const double ToleranceSeconds = 5.0;

        // Silence detection parameters (adjust as needed)
        const string SilenceNoise    = "-60dB";
        const double SilenceDuration = 1.0;

        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                // listen to the audio file
                Console.WriteLine("Usage: AudioSplitter <input_file>");
                var fileName = Console.ReadLine();
                args = new string[] { fileName };
            }

            string inputFile = args[0];
            if (!File.Exists(inputFile))
            {
                Console.WriteLine("Error: Input file not found.");
                return;
            }

            // Retrieve duration and bitrate using FFprobe.
            double duration = GetAudioDuration(inputFile);
            double bitrateKbps = GetAudioBitrate(inputFile);
            if (duration <= 0 || bitrateKbps <= 0)
            {
                Console.WriteLine("Error: Could not determine file duration or bitrate.");
                return;
            }

            double bytesPerSecond = (bitrateKbps * 1000) / 8.0;
            double maxSegmentDuration = _500MB / bytesPerSecond;
            Console.WriteLine($"Max segment duration (1GB limit): {maxSegmentDuration:F2} seconds");

            // Run FFmpeg silencedetect filter to get silence end timestamps.
            List<double> silencePoints = GetSilenceEndPoints(inputFile);
            if (silencePoints.Count == 0)
            {
                Console.WriteLine("No silence points detected. Using file end as the cut point.");
                silencePoints.Add(duration);
            }
            else
            {
                Console.WriteLine($"Detected {silencePoints.Count} silence endpoint(s).");
            }

            double currentTime = 0;
            int segmentIndex = 1;

            while (currentTime < duration)
            {
                double desiredTarget = currentTime + maxSegmentDuration;
                if (desiredTarget > duration)
                    desiredTarget = duration;

                // Look for a silence point near the desired target within tolerance.
                double nextCut = GetNextCutPoint(currentTime, desiredTarget, silencePoints, ToleranceSeconds);
                if (nextCut <= currentTime || nextCut > duration)
                    nextCut = duration;

                double segDuration = nextCut - currentTime;
                string outputFile = Path.Combine(
                    Path.GetDirectoryName(inputFile),
                    Path.GetFileNameWithoutExtension(inputFile) + $"_part{segmentIndex}" + Path.GetExtension(inputFile));

                // Try using -acodec instead of -c:a for compatibility with older FFmpeg versions
                // Also add -y to force overwrite if the output file exists
                string ffmpegArgs = $"-ss {currentTime.ToString("0.00", CultureInfo.InvariantCulture)} -i \"{inputFile}\" -t {segDuration.ToString("0.00", CultureInfo.InvariantCulture)} -acodec copy -y \"{outputFile}\"";
                Console.WriteLine($"Segment {segmentIndex}: {currentTime.ToString("0.00", CultureInfo.InvariantCulture)}s to {nextCut.ToString("0.00", CultureInfo.InvariantCulture)}s");
                Console.WriteLine($"Executing: ffmpeg {ffmpegArgs}");

                // Use the modified RunProcess method to capture and display errors
                int exitCode = RunProcessWithErrorOutput("ffmpeg", ffmpegArgs);

                if (exitCode != 0)
                {
                    Console.WriteLine($"Error: FFmpeg exited with code {exitCode}.");
                    Console.WriteLine("Trying alternative method with format conversion...");

                    // Try alternative method by re-encoding instead of copying
                    ffmpegArgs = $"-ss {currentTime.ToString("0.00", CultureInfo.InvariantCulture)} -i \"{inputFile}\" -t {segDuration.ToString("0.00", CultureInfo.InvariantCulture)} -acodec pcm_s16le -y \"{outputFile}\"";
                    Console.WriteLine($"Executing: ffmpeg {ffmpegArgs}");
                    exitCode = RunProcessWithErrorOutput("ffmpeg", ffmpegArgs);

                    if (exitCode != 0)
                    {
                        Console.WriteLine($"Error: Alternative method also failed with exit code {exitCode}.");
                        return;
                    }
                }

                // Verify the output file was created
                if (File.Exists(outputFile))
                {
                    Console.WriteLine($"Successfully created output file: {outputFile}");
                }
                else
                {
                    Console.WriteLine($"Warning: Output file was not created: {outputFile}");
                }

                currentTime = nextCut;
                segmentIndex++;
            }

            Console.WriteLine("Splitting completed.");
        }

        // Uses FFprobe to retrieve the audio duration in seconds.
        static double GetAudioDuration(string file)
        {
            string args = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{file}\"";
            string output = RunProcessWithOutput("ffprobe", args);
            if (double.TryParse(output, NumberStyles.Any, CultureInfo.InvariantCulture, out double duration))
                return duration;
            return 0;
        }

        // Uses FFprobe to retrieve the audio bitrate in kbps.
        static double GetAudioBitrate(string file)
        {
            string args = $"-v error -select_streams a:0 -show_entries stream=bit_rate -of default=noprint_wrappers=1:nokey=1 \"{file}\"";
            string output = RunProcessWithOutput("ffprobe", args);
            if (double.TryParse(output, NumberStyles.Any, CultureInfo.InvariantCulture, out double bitrate))
                return bitrate / 1000.0;
            return 128; // fallback bitrate
        }

        // Runs FFmpeg with the silencedetect filter to extract silence_end timestamps.
        static List<double> GetSilenceEndPoints(string file)
        {
            var silencePoints = new List<double>();
            string args = $"-i \"{file}\" -af silencedetect=noise={SilenceNoise}:d={SilenceDuration} -f null -";
            // We need stderr output because silencedetect logs go there.
            string output = RunProcessWithOutput("ffmpeg", args, readError: true);
            Regex regex = new Regex(@"silence_end:\s*(\d+(\.\d+)?)", RegexOptions.Compiled);
            MatchCollection matches = regex.Matches(output);
            foreach (Match match in matches)
            {
                if (match.Groups.Count > 1 &&
                    double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ts))
                {
                    silencePoints.Add(ts);
                }
            }

            silencePoints.Sort();
            return silencePoints;
        }

        // Finds the silence point nearest to the desired target within tolerance.
        static double GetNextCutPoint(double currentTime, double desiredTarget, List<double> silencePoints, double tolerance)
        {
            double bestCandidate = desiredTarget;
            double minDiff = double.MaxValue;
            foreach (double ts in silencePoints)
            {
                if (ts <= currentTime)
                    continue;
                double diff = Math.Abs(ts - desiredTarget);
                if (diff <= tolerance && diff < minDiff)
                {
                    bestCandidate = ts;
                    minDiff = diff;
                }

                if (ts > desiredTarget + tolerance)
                    break;
            }

            return bestCandidate;
        }

        // Modified method to run a process and display its error output
        static int RunProcessWithErrorOutput(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (Process proc = Process.Start(psi))
            {
                // Read output asynchronously to prevent deadlocks
                string output = proc.StandardOutput.ReadToEnd();
                string error = proc.StandardError.ReadToEnd();

                proc.WaitForExit();

                if (!string.IsNullOrEmpty(error))
                {
                    Console.WriteLine("FFmpeg error output:");
                    Console.WriteLine(error);
                }

                return proc.ExitCode;
            }
        }

        // Runs a process without capturing its output.
        static void RunProcess(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (Process proc = Process.Start(psi))
            {
                proc.WaitForExit();
            }
        }

        // Runs a process and returns its output as a string.
        static string RunProcessWithOutput(string exe, string args, bool readError = false)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = !readError,
                RedirectStandardError = readError,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (Process proc = Process.Start(psi))
            {
                string output = readError
                    ? proc.StandardError.ReadToEnd()
                    : proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                return output;
            }
        }
    }
}