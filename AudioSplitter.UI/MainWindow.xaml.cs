using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace AudioSplitter.UI
{
    public partial class MainWindow
    {
        private readonly ObservableCollection<AudioFile> _audioFiles = new();

        private bool _isFfmpegInstalled;

        // Define the local paths for ffmpeg and ffprobe (assumed to be in AudioSampleSplitter\Binaries)
        private readonly string _localFfmpegPath  = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Binaries", "ffmpeg.exe");
        private readonly string _localFfprobePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Binaries", "ffprobe.exe");

        // Silence detection parameters
        private const string SilenceNoise     = "-40dB";
        private const double SilenceDuration  = 1.0;
        private const double ToleranceSeconds = 5.0;

        public MainWindow()
        {
            InitializeComponent();
            FileListBox.ItemsSource = _audioFiles;

            // Check if FFMPEG is installed after UI is loaded
            Loaded += async (s, e) =>
            {
                LogToUI("Welcome to Audio Sample Splitter!");
                await CheckFfmpegStatus();
            };
        }

        private async Task CheckFfmpegStatus()
        {
            FfmpegStatusText.Text = "Checking...";
            FfmpegStatusIndicator.Fill = Brushes.Yellow;

            _isFfmpegInstalled = await Task.Run(IsFfmpegInstalled);

            if (_isFfmpegInstalled)
            {
                FfmpegStatusText.Text = "Installed";
                FfmpegStatusIndicator.Fill = Brushes.Green;

                LogToUI("FFmpeg is installed and ready to use.");
            }
            else
            {
                FfmpegStatusText.Text = "Not Installed";
                FfmpegStatusIndicator.Fill = Brushes.Red;
                GiveHelpOnHowToInstallFfmpeg();

                LogToUI("FFmpeg is not installed or cannot be found. Please install it to proceed.");
            }

            UpdateSplitButtonState();
        }

        private void GiveHelpOnHowToInstallFfmpeg()
        {
            // Show instructions to install FFMPEG
            MessageBoxResult result = MessageBox.Show(
                "FFmpeg is not installed or cannot be found in the local Binaries folder. Please install it to proceed with audio splitting.\n\n" +
                "Visit the following website for installation instructions:\n" +
                "https://ffmpeg.org/download.html\n\n" +
                "Do you want to open the installation page now?",
                "FFmpeg Not Installed",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://ffmpeg.org/download.html",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open installation page: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private bool IsFfmpegInstalled()
        {
            // Check if the local ffmpeg executable exists
            if (!File.Exists(_localFfmpegPath))
                return false;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = _localFfmpegPath,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null) return false;

                process.WaitForExit(3000); // Wait for 3 seconds max
                var output = process.StandardOutput.ReadToEnd();
                return output.Contains("ffmpeg version");
            }
            catch
            {
                return false;
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;

            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);

                foreach (var file in files)
                {
                    // Check if the file is an audio file
                    var extension = Path.GetExtension(file).ToLower();
                    if (IsAudioFile(extension))
                    {
                        AddAudioFile(file);
                    }
                }

                UpdateSplitButtonState();
            }
        }

        private bool IsAudioFile(string extension)
        {
            string[] supportedExtensions = { ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a" };
            return supportedExtensions.Contains(extension);
        }

        private void AddAudioFile(string filePath)
        {
            var fileInfo = new FileInfo(filePath);
            var formattedSize = FormatFileSize(fileInfo.Length);

            var audioFile = new AudioFile
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                FileSize = formattedSize,
                FileSizeBytes = fileInfo.Length
            };

            // Check if file already exists in the list
            if (_audioFiles.All(f => f.FilePath != filePath))
            {
                _audioFiles.Add(audioFile);
            }
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            var order = 0;
            double size = bytes;

            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }

            return $"{size:0.##} {sizes[order]}";
        }

        private void UpdateSplitButtonState()
        {
            SplitButton.IsEnabled = _isFfmpegInstalled && _audioFiles.Count > 0;
        }

        private async void SplitButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isFfmpegInstalled || _audioFiles.Count == 0)
                return;

            var splitSizeBytes = GetSelectedSplitSizeInBytes();

            // Show processing overlay
            ProcessingOverlay.Visibility = Visibility.Visible;
            ProcessingProgressBar.Value = 0;
            ProcessingProgressBar.IsIndeterminate = false;
            ProcessingProgressBar.Maximum = _audioFiles.Count;
            SplitButton.IsEnabled = false;

            try
            {
                await ProcessFilesAsync(splitSizeBytes);
                MessageBox.Show("All files have been processed successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error processing files: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // Hide processing overlay
                ProcessingOverlay.Visibility = Visibility.Collapsed;
                SplitButton.IsEnabled = true;
            }
        }

        private long GetSelectedSplitSizeInBytes()
        {
            switch (SplitSizeComboBox.SelectedIndex)
            {
                case 0: return 10L * 1024 * 1024; // 10 MB
                case 1: return 50L * 1024 * 1024; // 50 MB
                case 2: return 100L * 1024 * 1024; // 100 MB
                case 3: return 250L * 1024 * 1024; // 250 MB
                case 4: return 500L * 1024 * 1024; // 500 MB
                case 5: return 1024L * 1024 * 1024; // 1 GB
                default: return 100L * 1024 * 1024; // Default to 100 MB
            }
        }

        private async Task ProcessFilesAsync(long splitSizeBytes)
        {
            var processedCount = 0;

            foreach (var audioFile in _audioFiles)
            {
                await Task.Run(() => SplitAudioFile(audioFile.FilePath, splitSizeBytes));

                processedCount++;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingProgressBar.Value = processedCount;
                    StatusTextBlock.Text = $"Processed {processedCount} of {_audioFiles.Count} files";
                });
            }
        }

        private double GetAudioDuration(string file)
        {
            var args = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{file}\"";
            // Use the local ffprobe
            var output = RunProcessWithOutput(_localFfprobePath, args);
            if (double.TryParse(output, NumberStyles.Any, CultureInfo.InvariantCulture, out var duration))
                return duration;
            return 0;
        }

        private double GetAudioBitrate(string file)
        {
            var args = $"-v error -select_streams a:0 -show_entries stream=bit_rate -of default=noprint_wrappers=1:nokey=1 \"{file}\"";
            // Use the local ffprobe
            var output = RunProcessWithOutput(_localFfprobePath, args);
            if (double.TryParse(output, NumberStyles.Any, CultureInfo.InvariantCulture, out var bitrate))
                return bitrate / 1000.0;
            return 128; // fallback bitrate
        }

        private List<double> GetSilenceEndPoints(string file)
        {
            var silencePoints = new List<double>();
            var args = $"-i \"{file}\" -af silencedetect=noise={SilenceNoise}:d={SilenceDuration} -f null -";
            // Use the local ffmpeg and capture error output (where silence data is written)
            var output = RunProcessWithOutput(_localFfmpegPath, args, readError: true);

            var regex = new Regex(@"silence_end:\s*(\d+(\.\d+)?)", RegexOptions.Compiled);
            var matches = regex.Matches(output);

            foreach (Match match in matches)
            {
                if (match.Groups.Count > 1 &&
                    double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var ts))
                {
                    silencePoints.Add(ts);
                }
            }

            silencePoints.Sort();
            return silencePoints;
        }

        private double GetNextCutPoint(double currentTime, double desiredTarget, List<double> silencePoints, double tolerance)
        {
            var bestCandidate = desiredTarget;
            var minDiff = double.MaxValue;

            foreach (var ts in silencePoints)
            {
                if (ts <= currentTime)
                    continue;

                var diff = Math.Abs(ts - desiredTarget);
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

        private void SplitAudioFile(string filePath, long splitSizeBytes)
        {
            // Retrieve duration and bitrate using local ffprobe
            var duration = GetAudioDuration(filePath);
            var bitrateKbps = GetAudioBitrate(filePath);

            if (duration <= 0 || bitrateKbps <= 0)
            {
                throw new Exception("Could not determine file duration or bitrate.");
            }

            var bytesPerSecond = (bitrateKbps * 1000) / 8.0;
            var maxSegmentDuration = splitSizeBytes / bytesPerSecond;

            LogToUI($"Max segment duration ({FormatFileSize(splitSizeBytes)} limit): {maxSegmentDuration:F2} seconds");

            // Get silence points for intelligent splitting
            var silencePoints = GetSilenceEndPoints(filePath);
            if (silencePoints.Count == 0)
            {
                LogToUI("No silence points detected. Using file end as the cut point.");
                silencePoints.Add(duration);
            }
            else
            {
                LogToUI($"Detected {silencePoints.Count} silence endpoint(s).");
            }

            double currentTime = 0;
            var segmentIndex = 1;

            while (currentTime < duration)
            {
                var desiredTarget = currentTime + maxSegmentDuration;
                if (desiredTarget > duration)
                    desiredTarget = duration;

                // Look for a silence point near the desired target within tolerance
                var nextCut = GetNextCutPoint(currentTime, desiredTarget, silencePoints, ToleranceSeconds);
                if (nextCut <= currentTime || nextCut > duration)
                    nextCut = duration;

                var segDuration = nextCut - currentTime;
                var outputFile = Path.Combine(
                    Path.GetDirectoryName(filePath),
                    Path.GetFileNameWithoutExtension(filePath) + $"_part{segmentIndex}" + Path.GetExtension(filePath));

                // First try splitting with stream copy
                var ffmpegArgs = $"-ss {currentTime.ToString("0.00", CultureInfo.InvariantCulture)} -i \"{filePath}\" -t {segDuration.ToString("0.00", CultureInfo.InvariantCulture)} -acodec copy -y \"{outputFile}\"";
                LogToUI($"Segment {segmentIndex}: {currentTime:0.00}s to {nextCut:0.00}s");
                LogToUI($"Executing: {_localFfmpegPath} {ffmpegArgs}");

                var exitCode = RunProcessWithErrorOutput(_localFfmpegPath, ffmpegArgs);

                if (exitCode != 0)
                {
                    LogToUI($"Error: FFmpeg exited with code {exitCode}.");
                    LogToUI("Trying alternative method with format conversion...");

                    // Try alternative method by re-encoding instead of copying
                    ffmpegArgs = $"-ss {currentTime.ToString("0.00", CultureInfo.InvariantCulture)} -i \"{filePath}\" -t {segDuration.ToString("0.00", CultureInfo.InvariantCulture)} -acodec pcm_s16le -y \"{outputFile}\"";
                    LogToUI($"Executing: {_localFfmpegPath} {ffmpegArgs}");
                    exitCode = RunProcessWithErrorOutput(_localFfmpegPath, ffmpegArgs);

                    if (exitCode != 0)
                    {
                        throw new Exception($"Alternative method also failed with exit code {exitCode}.");
                    }
                }

                // Verify the output file was created
                if (File.Exists(outputFile))
                {
                    LogToUI($"Successfully created output file: {outputFile}");
                }
                else
                {
                    LogToUI($"Warning: Output file was not created: {outputFile}");
                }

                currentTime = nextCut;
                segmentIndex++;
            }

            LogToUI("Splitting completed.");
        }

        private void LogToUI(string message)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText(message + Environment.NewLine);
                LogTextBox.ScrollToEnd();
            });
        }

        private string RunProcessWithOutput(string exe, string args, bool readError = false)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = !readError,
                RedirectStandardError = readError,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            var output = readError
                ? proc.StandardError.ReadToEnd()
                : proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return output;
        }

        private int RunProcessWithErrorOutput(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            // Read output to prevent deadlocks
            var output = proc.StandardOutput.ReadToEnd();
            var error = proc.StandardError.ReadToEnd();

            proc.WaitForExit();

            if (string.IsNullOrEmpty(error)) return proc.ExitCode;

            LogToUI("FFmpeg error output:");
            LogToUI(error);

            return proc.ExitCode;
        }

        private void SourceCodeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // You can change this URL to your actual GitHub repository
                string githubUrl = "https://github.com/AldeRoberge/AudioSplitter";

                // This will open the URL in the default browser
                Process.Start(new ProcessStartInfo
                {
                    FileName = githubUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open GitHub: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void NeedHelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string email = "demersra@uqat.ca";
                string subject = Uri.EscapeDataString("Cherche Urgemment de L'aide"); // URL-encoded subject
                string body = Uri.EscapeDataString("Bonjour,\n\nJ'ai besoin d'aide avec mon Audio Sample Splitter.."); // URL-encoded body

                string mailto = $"mailto:{email}?subject={subject}&body={body}";

                Process.Start(new ProcessStartInfo(mailto) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open email client: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public class AudioFile
        {
            public string FilePath { get; set; }
            public string FileName { get; set; }
            public string FileSize { get; set; }
            public long FileSizeBytes { get; set; }
        }
    }
}