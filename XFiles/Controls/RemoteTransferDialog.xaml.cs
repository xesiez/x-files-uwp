using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;
using XFiles.FileSystem;
using XFiles.Services;
using ZXing;
using ZXing.Common;

namespace XFiles.Controls
{
    public sealed partial class RemoteTransferDialog : UserControl
    {
        private RemoteTransferServer _server;
        private string _targetDirectory;
        private CancellationTokenSource _downloadCts;

        public bool IsOpen => Visibility == Visibility.Visible;
        public Action OnClosed;
        public event Action<string> FileTransferCompleted;

        public RemoteTransferDialog()
        {
            InitializeComponent();
        }

        public async void Show(string targetDirectory)
        {
            _targetDirectory = targetDirectory;
            TargetFolderText.Text = targetDirectory;
            StatusLabelText.Text = "● Starting server...";
            ActivityProgressBar.Visibility = Visibility.Collapsed;
            ActivityDetailText.Visibility = Visibility.Collapsed;

            Visibility = Visibility.Visible;
            Overlay.Visibility = Visibility.Visible;

            if (_server == null)
            {
                _server = new RemoteTransferServer();
                _server.OnClientConnected += ip => RunOnUi(() =>
                {
                    StatusLabelText.Text = $"● Phone connected from {ip}";
                });
                _server.OnStatusMessage += msg => RunOnUi(() =>
                {
                    StatusLabelText.Text = msg;
                });
                _server.OnUploadProgress += (fn, rec, total) => RunOnUi(() =>
                {
                    ActivityProgressBar.Visibility = Visibility.Visible;
                    ActivityDetailText.Visibility = Visibility.Visible;
                    int pct = total > 0 ? (int)((rec * 100) / total) : 0;
                    ActivityProgressBar.Value = pct;
                    ActivityDetailText.Text = $"Uploading {fn} ({Formatting.FormatSize(rec)} / {Formatting.FormatSize(total)})";
                });
                _server.OnFileUploaded += (fn, path) => RunOnUi(() =>
                {
                    ActivityProgressBar.Visibility = Visibility.Collapsed;
                    ActivityDetailText.Visibility = Visibility.Visible;
                    ActivityDetailText.Text = $"✓ Uploaded {fn} to folder!";
                    StatusLabelText.Text = "● Ready for next transfer";
                    FileTransferCompleted?.Invoke(path);
                });
                _server.OnDownloadRequested += (url, dir) => RunOnUi(() =>
                {
                    StartRemoteDownload(url, dir);
                });
            }

            bool ok = await _server.StartAsync(targetDirectory);
            if (ok)
            {
                string serverUrl = _server.GetServerUrl();
                ServerUrlText.Text = serverUrl;
                StatusLabelText.Text = "● Server active — scan QR code or enter link on phone";
                GenerateQr(serverUrl);
            }
            else
            {
                StatusLabelText.Text = "❌ Could not start local server on port 8088";
            }
        }

        public void Close()
        {
            _downloadCts?.Cancel();
            _downloadCts?.Dispose();
            _downloadCts = null;

            _server?.Stop();
            Visibility = Visibility.Collapsed;
            Overlay.Visibility = Visibility.Collapsed;
            OnClosed?.Invoke();
        }

        private async void StartRemoteDownload(string url, string targetDir)
        {
            StatusLabelText.Text = "Downloading URL on Xbox...";
            ActivityProgressBar.Visibility = Visibility.Visible;
            ActivityDetailText.Visibility = Visibility.Visible;
            ActivityProgressBar.IsIndeterminate = true;
            ActivityDetailText.Text = $"Connecting: {url}";

            _downloadCts?.Cancel();
            _downloadCts = new CancellationTokenSource();

            Action<long, long> progress = (copied, total) =>
            {
                RunOnUi(() =>
                {
                    ActivityProgressBar.IsIndeterminate = false;
                    int pct = total > 0 ? (int)((copied * 100) / total) : 0;
                    ActivityProgressBar.Value = Math.Min(100, Math.Max(0, pct));
                    ActivityDetailText.Text = $"Downloading: {Formatting.FormatSize(copied)} / {(total > 0 ? Formatting.FormatSize(total) : "...")} ({pct}%)";
                });
            };

            try
            {
                var res = await DownloadService.TryDownloadAsync(url, targetDir, progress, _downloadCts.Token);
                if (res.Outcome == DownloadService.DownloadOutcome.Downloaded)
                {
                    ActivityProgressBar.Visibility = Visibility.Collapsed;
                    ActivityDetailText.Text = $"✓ Downloaded {Path.GetFileName(res.SavedPath)}!";
                    StatusLabelText.Text = "● Ready for next transfer";
                    FileTransferCompleted?.Invoke(res.SavedPath);
                }
                else if (res.Outcome == DownloadService.DownloadOutcome.NeedsBrowser)
                {
                    ActivityProgressBar.Visibility = Visibility.Collapsed;
                    ActivityDetailText.Text = "⚠️ Link requires interactive browser login / captcha";
                    StatusLabelText.Text = "● Ready for next transfer";
                }
                else
                {
                    ActivityProgressBar.Visibility = Visibility.Collapsed;
                    ActivityDetailText.Text = $"❌ Download failed: {res.Error ?? "Unknown error"}";
                    StatusLabelText.Text = "● Ready for next transfer";
                }
            }
            catch (Exception ex)
            {
                ActivityProgressBar.Visibility = Visibility.Collapsed;
                ActivityDetailText.Text = $"❌ Error: {ex.Message}";
                StatusLabelText.Text = "● Ready for next transfer";
            }
        }

        private void GenerateQr(string url)
        {
            try
            {
                var writer = new BarcodeWriterGeneric
                {
                    Format = BarcodeFormat.QR_CODE,
                    Options = new EncodingOptions
                    {
                        Width = 240,
                        Height = 240,
                        Margin = 1
                    }
                };

                var matrix = writer.Encode(url);
                if (matrix != null)
                {
                    int w = matrix.Width;
                    int h = matrix.Height;
                    var wb = new WriteableBitmap(w, h);
                    var stream = wb.PixelBuffer.AsStream();
                    var buffer = new byte[w * h * 4];
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            bool isBlack = matrix[x, y];
                            int offset = (y * w + x) * 4;
                            byte color = isBlack ? (byte)0 : (byte)255;
                            buffer[offset] = color;     // B
                            buffer[offset + 1] = color; // G
                            buffer[offset + 2] = color; // R
                            buffer[offset + 3] = 255;   // A
                        }
                    }
                    stream.Write(buffer, 0, buffer.Length);
                    wb.Invalidate();
                    QrImage.Source = wb;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("RemoteTransferDialog: QR generation failed: {Error}", ex.Message);
            }
        }

        private void OnOverlayTapped(object sender, TappedRoutedEventArgs e)
        {
            if (e.OriginalSource == Overlay)
            {
                Close();
            }
        }

        public void HandleKeyDown(VirtualKey key)
        {
            if (key == VirtualKey.GamepadB || key == VirtualKey.Escape)
            {
                Close();
            }
        }

        private async void RunOnUi(Action action)
        {
            if (Dispatcher.HasThreadAccess)
            {
                action();
            }
            else
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action());
            }
        }
    }
}
