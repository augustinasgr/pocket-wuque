using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Input;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using Windows.Security.Cryptography;
using System.Windows.Threading;

namespace WuqueProbe;

public partial class MainWindow : System.Windows.Window
{
    private static readonly Guid ServiceId = Guid.Parse("1f40eaf8-aab4-14a3-f1ba-f61f35cddbaa");
    private static readonly Guid CommandId = Guid.Parse("1f400001-aab4-14a3-f1ba-f61f35cddbaa");
    private static readonly Guid NotifyId = Guid.Parse("1f400002-aab4-14a3-f1ba-f61f35cddbaa");
    private static readonly Guid DisplayId = Guid.Parse("1f400003-aab4-14a3-f1ba-f61f35cddbaa");
    private static readonly Guid NotifyAltId = Guid.Parse("1f400004-aab4-14a3-f1ba-f61f35cddbaa");
    private const int DialWidth = 320;
    private const int DialHeight = 172;
    private const int FirstPacketImageBytes = 218;
    private const int DialBlockBytes = 4096;
    private const int DialChunkBytes = 243;
    private const int FastPacketPacingDelayMs = 0;
    private const int FastBlockPacingDelayMs = 225;
    private const double GifPreviewPlaybackCalibration = 0.5;

    private readonly ObservableCollection<DeviceRow> _devices = new();
    private readonly List<LogEntry> _events = new();
    private readonly object _eventLock = new();
    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _commandCharacteristic;
    private GattCharacteristic? _notifyCharacteristic;
    private GattCharacteristic? _displayCharacteristic;
    private readonly List<GattCharacteristic> _notificationCharacteristics = new();
    private readonly object _waiterLock = new();
    private readonly List<(Func<byte[], bool> Match, TaskCompletionSource<byte[]> Source)> _notificationWaiters = new();
    private readonly List<byte> _dialAckStatuses = new();
    private readonly object _ackLock = new();
    private string? _imagePath;
    private DeviceRow? _selectedRow;
    private string? _maxPduSize;
    private string? _adapterInfo;
    private string? _lastWriteStatus;
    private bool _subscribed;
    private BitmapSource? _sourceBitmap;
    private Rect _sourceDisplayRect = Rect.Empty;
    private Rect _cropRect = Rect.Empty;
    private bool _draggingCrop;
    private bool _resizingCrop;
    private Point _dragStart;
    private Rect _cropStart;
    private double _sourceScale;
    private double _defaultCropWidth;
    private bool _fitEntireImage;
    private double _fitScale = 1.0;
    private int _previewRevision;
    private bool _isGif;
    private byte? _productGeneration;
    private int _gifSpeed = 5;
    private int _gifSourceFrameCount;
    private int _gifSourceDurationMs;
    private int _gifPreviewIndex;
    private List<BitmapSource> _gifFrames = new();
    private List<byte[]> _gifFrameRgb565 = new();
    private List<BitmapSource> _gifFramePreviews = new();
    private readonly DispatcherTimer _gifPreviewTimer = new();

    public MainWindow()
    {
        InitializeComponent();
        DevicesGrid.ItemsSource = _devices;
        ImageLayoutCombo.SelectedIndex = 0;
        ImageLayoutCombo.SelectionChanged += ImageLayoutCombo_SelectionChanged;
        FitScaleSlider.ValueChanged += FitScaleSlider_ValueChanged;
        _gifPreviewTimer.Tick += GifPreviewTimer_Tick;
        Loaded += async (_, _) => await CheckAdapterAsync();
        Closed += (_, _) => Cleanup();
    }

    private async Task CheckAdapterAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            _adapterInfo = adapter is null
                ? "No default Bluetooth adapter"
                : $"Adapter address={adapter.BluetoothAddress:X12}; central={adapter.IsCentralRoleSupported}; peripheral={adapter.IsPeripheralRoleSupported}; offload={adapter.IsAdvertisementOffloadSupported}";
            SetStatus(_adapterInfo);
            Log("adapter", _adapterInfo);
        }
        catch (Exception ex) { SetStatus("Bluetooth adapter check failed: " + ex.Message); Log("error", "Adapter check: " + ex); }
    }

    private void Scan_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            if (_watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started) return;
            _devices.Clear();
            _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            _watcher.Received += Watcher_Received;
            _watcher.Stopped += Watcher_Stopped;
            _watcher.Start();
            ScanButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            SetStatus("Scanning for BLE advertisements… Select the keyboard explicitly before connecting.");
            Log("scan", "Advertisement watcher started (active scanning).");
        }
        catch (Exception ex) { SetStatus("Could not start scan: " + ex.Message); Log("error", "Start scan: " + ex); }
    }

    private void Stop_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started) _watcher.Stop();
    }

    private void Watcher_Stopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        Dispatcher.Invoke(() =>
        {
            ScanButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            SetStatus($"Scan stopped: {args.Error}");
            Log("scan", $"Advertisement watcher stopped; error={args.Error}.");
        });
    }

    private void Watcher_Received(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var advertisement = args.Advertisement;
        var services = string.Join(",", advertisement.ServiceUuids.Select(x => x.ToString()));
        var manufacturers = string.Join(";", advertisement.ManufacturerData.Select(x =>
            $"company=0x{x.CompanyId:X4}:{BufferHex(x.Data)}"));
        var raw = string.Join(";", advertisement.DataSections.Select(x => $"type=0x{x.DataType:X2}:{BufferHex(x.Data)}"));
        var row = new DeviceRow
        {
            BluetoothAddressValue = args.BluetoothAddress,
            Name = string.IsNullOrWhiteSpace(advertisement.LocalName) ? "(no name)" : advertisement.LocalName,
            Rssi = args.RawSignalStrengthInDBm,
            ServiceUuidsText = services,
            ManufacturerDataText = manufacturers,
            RawAdvertisementHex = raw
        };
        Dispatcher.BeginInvoke(() =>
        {
            var existingIndex = -1;
            for (var i = 0; i < _devices.Count; i++)
                if (_devices[i].BluetoothAddressValue == row.BluetoothAddressValue) { existingIndex = i; break; }
            if (existingIndex >= 0)
            {
                // Keep the selected row instance stable while advertisements continue to arrive.
                if (_selectedRow?.BluetoothAddressValue != row.BluetoothAddressValue)
                    _devices[existingIndex] = row;
            }
            else _devices.Add(row);
            Log("advertisement", $"name={row.Name}; address={row.Address}; RSSI={row.Rssi}; services=[{services}]; manufacturer=[{manufacturers}]; raw=[{raw}]");
        });
    }

    private void DevicesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedRow = DevicesGrid.SelectedItem as DeviceRow;
        ConnectButton.IsEnabled = _selectedRow is not null && _device is null;
    }

    private async void Connect_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_selectedRow is null) return;
        ConnectButton.IsEnabled = false;
        _productGeneration = null;
        try
        {
            SetStatus($"Connecting to {_selectedRow.Name} ({_selectedRow.Address})…");
            Log("connect", $"User selected {_selectedRow.Name}, address={_selectedRow.Address}.");
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(_selectedRow.BluetoothAddressValue);
            if (_device is null) throw new InvalidOperationException("Windows did not return a BLE device object.");
            Log("connect", $"Connected device object: name={_device.Name}; id={_device.DeviceId}; connection={_device.ConnectionStatus}.");

            var services = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            Log("gatt", $"Service discovery status={services.Status}; protocolError={FormatProtocolError(services.ProtocolError)}; count={services.Services.Count}.");
            foreach (var found in services.Services) Log("gatt", $"Service {found.Uuid}; access={found.DeviceAccessInformation.CurrentStatus}.");
            _service = services.Services.FirstOrDefault(s => s.Uuid == ServiceId);
            if (_service is null) throw new InvalidOperationException($"Expected Wuque service {ServiceId} was not found.");

            try
            {
                var session = _service.Session;
                _maxPduSize = session is null ? "unavailable" : session.MaxPduSize.ToString(CultureInfo.InvariantCulture);
                Log("transport", $"GattSession MaxPduSize={_maxPduSize}.");
            }
            catch (Exception ex) { _maxPduSize = "unavailable: " + ex.Message; Log("transport", "Could not read MaxPduSize: " + ex.Message); }

            var result = await _service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            Log("gatt", $"Characteristic discovery status={result.Status}; protocolError={FormatProtocolError(result.ProtocolError)}; count={result.Characteristics.Count}.");
            foreach (var characteristic in result.Characteristics)
            {
                var properties = characteristic.CharacteristicProperties;
                Log("gatt", $"Characteristic {characteristic.Uuid}; properties={properties}; read={properties.HasFlag(GattCharacteristicProperties.Read)}; write={properties.HasFlag(GattCharacteristicProperties.Write)}; writeWithoutResponse={properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)}; notify={properties.HasFlag(GattCharacteristicProperties.Notify)}; indicate={properties.HasFlag(GattCharacteristicProperties.Indicate)}.");
            }
            _commandCharacteristic = result.Characteristics.FirstOrDefault(c => c.Uuid == CommandId);
            _notifyCharacteristic = result.Characteristics.FirstOrDefault(c => c.Uuid == NotifyId);
            _displayCharacteristic = result.Characteristics.FirstOrDefault(c => c.Uuid == DisplayId);
            var notifyAlt = result.Characteristics.FirstOrDefault(c => c.Uuid == NotifyAltId);
            if (_displayCharacteristic is not null) Log("gatt", $"Display-data characteristic {_displayCharacteristic.Uuid} found; properties={_displayCharacteristic.CharacteristicProperties}.");
            if (_commandCharacteristic is null || _notifyCharacteristic is null || _displayCharacteristic is null)
                throw new InvalidOperationException("Command-write, display-data write, or primary notification characteristic was not found.");
            if (!_displayCharacteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse))
                throw new InvalidOperationException("Display-data characteristic does not support Write Without Response, which this app requires for image uploads.");

            _notificationCharacteristics.Clear();
            foreach (var characteristic in new[] { _notifyCharacteristic, notifyAlt }.Where(c => c is not null).Cast<GattCharacteristic>())
            {
                if (!characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)) continue;
                characteristic.ValueChanged += Notify_ValueChanged;
                var cccd = await characteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                Log("notify", $"CCCD subscription characteristic={characteristic.Uuid}; status={cccd.Status}; protocolError={FormatProtocolError(cccd.ProtocolError)}.");
                if (cccd.Status != GattCommunicationStatus.Success)
                    throw new InvalidOperationException($"Notification subscription failed for {characteristic.Uuid}: {cccd.Status}");
                _notificationCharacteristics.Add(characteristic);
            }
            _subscribed = true;
            DisconnectButton.IsEnabled = true;
            QueryButton.IsEnabled = true;
            SetStatus($"Connected to {_device.Name}. Notifications enabled; MaxPduSize={_maxPduSize}.");
            UpdateSelectedMediaActionState();
        }
        catch (Exception ex)
        {
            Log("error", "Connect/discovery failed: " + ex);
            SetStatus("Connection or GATT discovery failed: " + ex.Message);
            Cleanup();
        }
        finally { ConnectButton.IsEnabled = _device is null && _selectedRow is not null; }
    }

    private void Notify_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var bytes = BufferBytes(args.CharacteristicValue);
        if (bytes.Length == 11 && bytes[0] == 0x88 && bytes[8] == 0x08 && bytes[9] == 0x06)
        {
            lock (_ackLock) _dialAckStatuses.Add(bytes[10]);
        }
        List<TaskCompletionSource<byte[]>> completed = new();
        lock (_waiterLock)
        {
            foreach (var waiter in _notificationWaiters.ToArray())
            {
                bool matches;
                try { matches = waiter.Match(bytes); } catch { matches = false; }
                if (!matches) continue;
                _notificationWaiters.Remove(waiter);
                completed.Add(waiter.Source);
            }
        }
        foreach (var waiter in completed) waiter.TrySetResult(bytes);
        Dispatcher.BeginInvoke(() =>
        {
            var hex = Convert.ToHexString(bytes);
            Log("notification", $"characteristic={sender.Uuid}; raw={hex}");
            if (bytes.Length > 19 && bytes[9] == 0x02)
            {
                _productGeneration = bytes[^1];
                Log("version", $"APK response guards passed; version={bytes[12]}.{bytes[13]}.{bytes[14]}; productGeneration={bytes[^1]}; raw={hex}");
                Dispatcher.BeginInvoke(() => UpdateSelectedMediaActionState());
            }
            else if (bytes.Length == 11 && bytes[0] == 0x88 && bytes[8] == 0x08 && bytes[9] == 0x06)
                Log("ack", $"Dial-data ACK observed; status=0x{bytes[10]:X2}; raw={hex}");
        });
    }

    private async void Query_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_commandCharacteristic is null || _notifyCharacteristic is null) return;
        QueryButton.IsEnabled = false;
        try
        {
            var payload = new byte[] { 0x00, 0x01, 0x00 };
            var frame = BuildCommandFrame(payload);
            Log("query", "Sending exactly one non-mutating version query to command characteristic using write-with-response.");
            Log("write", $"characteristic={CommandId}; option=WriteWithResponse; bytes={Convert.ToHexString(frame)}");
            var writer = new DataWriter();
            writer.WriteBytes(frame);
            var result = await _commandCharacteristic.WriteValueWithResultAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse);
            _lastWriteStatus = $"{result.Status}; protocolError={FormatProtocolError(result.ProtocolError)}";
            Log("write-result", $"OS GATT write completion: {_lastWriteStatus}.");
            if (result.Status != GattCommunicationStatus.Success) SetStatus("Query write failed: " + _lastWriteStatus);
            else
            {
                SetStatus("Query write completed at the GATT layer; listening for notifications for 8 seconds…");
                await Task.Delay(TimeSpan.FromSeconds(8));
                SetStatus("Version query window finished. Review raw notification and parsed response entries in the log.");
            }
        }
        catch (Exception ex) { Log("error", "Version query failed: " + ex); SetStatus("Version query failed: " + ex.Message); }
        finally { QueryButton.IsEnabled = _device is not null && _notifyCharacteristic is not null; }
    }

    private async void ChooseImage_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the dial image to transfer",
            Filter = "Dial images and animations|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Image files|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        _imagePath = dialog.FileName;
        ImagePathText.Text = _imagePath;
        FirstPacketButton.IsEnabled = false;
        FinalPreview.Source = null;
        try
        {
            _isGif = string.Equals(System.IO.Path.GetExtension(_imagePath), ".gif", StringComparison.OrdinalIgnoreCase);
            _gifFrames.Clear(); _gifFrameRgb565.Clear(); _gifFramePreviews.Clear();
            _gifPreviewTimer.Stop(); _gifPreviewIndex = 0;
            BitmapSource bitmap;
            if (_isGif)
            {
                GifAnimation animation = await Task.Run(() => DecodeGif(_imagePath));
                _gifSourceFrameCount = animation.SourceFrameCount;
                _gifSourceDurationMs = animation.DurationMs;
                _gifFrames = animation.Frames.ToList();
                if (_gifFrames.Count == 0) throw new InvalidDataException("The GIF contains no usable frames.");
                bitmap = _gifFrames[0];
                GifSpeedPanel.Visibility = Visibility.Visible;
                UpdateGifSpeedDisplay();
            }
            else
            {
                var still = new BitmapImage();
                still.BeginInit(); still.CacheOption = BitmapCacheOption.OnLoad;
                still.UriSource = new Uri(_imagePath, UriKind.Absolute); still.EndInit(); still.Freeze();
                bitmap = still;
                GifSpeedPanel.Visibility = Visibility.Collapsed;
            }
            _sourceBitmap = bitmap;
            FinalPreview.Source = null;
            SourceImage.Source = bitmap;
            _sourceScale = Math.Min(CropCanvas.Width / (double)bitmap.PixelWidth, CropCanvas.Height / (double)bitmap.PixelHeight);
            var displayWidth = bitmap.PixelWidth * _sourceScale;
            var displayHeight = bitmap.PixelHeight * _sourceScale;
            _sourceDisplayRect = new Rect((CropCanvas.Width - displayWidth) / 2, (CropCanvas.Height - displayHeight) / 2, displayWidth, displayHeight);
            var cropWidth = Math.Min(displayWidth, displayHeight * DialWidth / (double)DialHeight);
            var cropHeight = cropWidth * DialHeight / DialWidth;
            _cropRect = new Rect(_sourceDisplayRect.Left + (displayWidth - cropWidth) / 2,
                _sourceDisplayRect.Top + (displayHeight - cropHeight) / 2, cropWidth, cropHeight);
            _defaultCropWidth = cropWidth;
            UpdateSourceCanvasDisplay();
            UpdateCropPreview();
            await RefreshFinalPreviewAsync();
            UpdateSelectedMediaActionState();
            Log("image", _isGif
                ? $"Selected GIF {_imagePath}; source frames={_gifSourceFrameCount}; selected frames={_gifFrames.Count}; source duration={_gifSourceDurationMs}ms; frame crop={DialWidth}x{DialHeight}; uploaded GIF frames are directly converted to RGB565."
                : $"Selected {_imagePath}; source={bitmap.PixelWidth}x{bitmap.PixelHeight}; interactive crop is {DialWidth}x{DialHeight} aspect; preview uses JPEG quality 70 then RGB565 conversion.");
        }
        catch (Exception ex) { _imagePath = null; _gifFrames.Clear(); _gifFrameRgb565.Clear(); _gifFramePreviews.Clear(); _isGif = false; GifSpeedPanel.Visibility = Visibility.Collapsed; FirstPacketButton.IsEnabled = false; FinalPreview.Source = null; CropFrame.Visibility = Visibility.Collapsed; CropResizeHandle.Visibility = Visibility.Collapsed; SetStatus("Could not open image: " + ex.Message); Log("error", "Image load failed: " + ex); }
    }

    private void UpdateCropPreview()
    {
        if (_fitEntireImage)
        {
            var dialCanvasHeight = CropCanvas.Height;
            var dialCanvasWidth = dialCanvasHeight * DialWidth / (double)DialHeight;
            Canvas.SetLeft(CropFrame, (CropCanvas.Width - dialCanvasWidth) / 2);
            Canvas.SetTop(CropFrame, 0);
            CropFrame.Width = dialCanvasWidth;
            CropFrame.Height = dialCanvasHeight;
            CropFrame.Fill = Brushes.Transparent;
            CropFrame.Visibility = Visibility.Visible;
            CropResizeHandle.Visibility = Visibility.Collapsed;
            CropInfoText.Text = $"Fit entire {_sourceBitmap!.PixelWidth}×{_sourceBitmap.PixelHeight} source inside the dial; adjust size below. Nothing is cropped.";
            return;
        }
        CropFrame.Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        Canvas.SetLeft(CropFrame, _cropRect.Left); Canvas.SetTop(CropFrame, _cropRect.Top);
        CropFrame.Width = _cropRect.Width; CropFrame.Height = _cropRect.Height;
        CropFrame.Visibility = Visibility.Visible;
        Canvas.SetLeft(CropResizeHandle, _cropRect.Right - CropResizeHandle.Width / 2);
        Canvas.SetTop(CropResizeHandle, _cropRect.Bottom - CropResizeHandle.Height / 2);
        CropResizeHandle.Visibility = Visibility.Visible;
        CropInfoText.Text = $"Source {_sourceBitmap!.PixelWidth}×{_sourceBitmap.PixelHeight}; crop scale {_defaultCropWidth / _cropRect.Width:0.0}×. Drag the frame to reposition or the lower-right handle to scale.";
    }

    private Int32Rect GetCropPixels()
    {
        var x = (int)Math.Round((_cropRect.Left - _sourceDisplayRect.Left) / _sourceScale);
        var y = (int)Math.Round((_cropRect.Top - _sourceDisplayRect.Top) / _sourceScale);
        var width = (int)Math.Round(_cropRect.Width / _sourceScale);
        var height = (int)Math.Round(_cropRect.Height / _sourceScale);
        return new Int32Rect(Math.Max(0, x), Math.Max(0, y), Math.Min(width, _sourceBitmap!.PixelWidth - Math.Max(0, x)), Math.Min(height, _sourceBitmap.PixelHeight - Math.Max(0, y)));
    }

    private void CropCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_sourceBitmap is null || _fitEntireImage) return;
        var point = e.GetPosition(CropCanvas);
        var handle = new Rect(Canvas.GetLeft(CropResizeHandle), Canvas.GetTop(CropResizeHandle), CropResizeHandle.Width, CropResizeHandle.Height);
        if (handle.Contains(point)) _resizingCrop = true;
        else if (_cropRect.Contains(point)) _draggingCrop = true;
        else return;
        _dragStart = point; _cropStart = _cropRect;
        CropCanvas.CaptureMouse(); e.Handled = true;
    }

    private void CropCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingCrop && !_resizingCrop) return;
        var delta = e.GetPosition(CropCanvas) - _dragStart;
        if (_resizingCrop)
        {
            var aspect = DialWidth / (double)DialHeight;
            var maxWidth = Math.Min(_sourceDisplayRect.Right - _cropStart.Left, (_sourceDisplayRect.Bottom - _cropStart.Top) * aspect);
            var fromX = _cropStart.Width + delta.X;
            var fromY = (_cropStart.Height + delta.Y) * aspect;
            var width = Math.Clamp((fromX + fromY) / 2, Math.Min(48, maxWidth), maxWidth);
            _cropRect = new Rect(_cropStart.Left, _cropStart.Top, width, width / aspect);
        }
        else
        {
            var minX = _sourceDisplayRect.Left;
            var maxX = Math.Max(minX, _sourceDisplayRect.Right - _cropStart.Width);
            var minY = _sourceDisplayRect.Top;
            var maxY = Math.Max(minY, _sourceDisplayRect.Bottom - _cropStart.Height);
            var x = Math.Clamp(_cropStart.X + delta.X, minX, maxX);
            var y = Math.Clamp(_cropStart.Y + delta.Y, minY, maxY);
            _cropRect = new Rect(x, y, _cropStart.Width, _cropStart.Height);
        }
        UpdateCropPreview();
    }

    private async void CropCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingCrop && !_resizingCrop) return;
        _draggingCrop = false; _resizingCrop = false; CropCanvas.ReleaseMouseCapture();
        if (_imagePath is null || _sourceBitmap is null) return;
        await RefreshFinalPreviewAsync();
    }

    private async Task RefreshFinalPreviewAsync()
    {
        if (_imagePath is null || _sourceBitmap is null) return;
        FirstPacketButton.IsEnabled = false;
        var revision = ++_previewRevision;
        var path = _imagePath; var crop = GetCropPixels();
        try
        {
            if (_isGif)
            {
                var frames = _gifFrames.ToArray();
                var fit = _fitEntireImage; var fitScale = _fitScale;
                var prepared = await Task.Run(() => frames.Select(frame => ConvertBitmapToRgb565(frame, crop, false, fit, fitScale)).ToArray());
                if (revision != _previewRevision) return;
                _gifFrameRgb565 = prepared.Select(item => item.Bytes).ToList();
                _gifFramePreviews = prepared.Select(item => item.Preview).ToList();
                FinalPreview.Source = _gifFramePreviews[0];
                _gifPreviewIndex = 0;
                _gifPreviewTimer.Interval = GetGifFrameInterval();
                if (_gifFramePreviews.Count > 1) _gifPreviewTimer.Start();
                UpdateGifSpeedDisplay();
            }
            else
            {
                var fit = _fitEntireImage; var fitScale = _fitScale;
                var prepared = await Task.Run(() => LoadDialRgb565(path, crop, fit, fitScale));
                if (revision != _previewRevision) return;
                FinalPreview.Source = prepared.Preview;
            }
            UpdateSelectedMediaActionState();
            Log("image-layout", $"Preview refreshed; layout={(_fitEntireImage ? "fit entire image" : "crop to fill")}; fitScale={_fitScale:P0}; source crop={crop.X},{crop.Y} {crop.Width}x{crop.Height}.");
        }
        catch (Exception ex) { FirstPacketButton.IsEnabled = false; SetStatus("Preview update failed: " + ex.Message); Log("error", "Crop preview: " + ex); }
    }

    private void ImageLayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _fitEntireImage = ImageLayoutCombo.SelectedIndex == 1;
        FitScalePanel.Visibility = _fitEntireImage ? Visibility.Visible : Visibility.Collapsed;
        UpdateSourceCanvasDisplay();
        if (_sourceBitmap is not null)
        {
            UpdateCropPreview();
            _ = RefreshFinalPreviewAsync();
        }
    }

    private void FitScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _fitScale = e.NewValue / 100.0;
        FitScaleText.Text = $"{e.NewValue:0}%";
        if (_fitEntireImage && _sourceBitmap is not null)
        {
            UpdateSourceCanvasDisplay();
            UpdateCropPreview();
            _ = RefreshFinalPreviewAsync();
        }
    }

    private void UpdateSourceCanvasDisplay()
    {
        if (_sourceBitmap is null) return;
        if (!_fitEntireImage)
        {
            SourceImage.Width = CropCanvas.Width;
            SourceImage.Height = CropCanvas.Height;
            Canvas.SetLeft(SourceImage, 0);
            Canvas.SetTop(SourceImage, 0);
            return;
        }

        var dialCanvasHeight = CropCanvas.Height;
        var dialCanvasWidth = dialCanvasHeight * DialWidth / (double)DialHeight;
        var dialCanvasLeft = (CropCanvas.Width - dialCanvasWidth) / 2;
        var fit = Math.Min(dialCanvasWidth / _sourceBitmap.PixelWidth, dialCanvasHeight / _sourceBitmap.PixelHeight) * _fitScale;
        SourceImage.Width = _sourceBitmap.PixelWidth * fit;
        SourceImage.Height = _sourceBitmap.PixelHeight * fit;
        Canvas.SetLeft(SourceImage, dialCanvasLeft + (dialCanvasWidth - SourceImage.Width) / 2);
        Canvas.SetTop(SourceImage, (dialCanvasHeight - SourceImage.Height) / 2);
    }

    private void GifPreviewTimer_Tick(object? sender, EventArgs e)
    {
        if (_gifFramePreviews.Count < 2) return;
        _gifPreviewIndex = (_gifPreviewIndex + 1) % _gifFramePreviews.Count;
        FinalPreview.Source = _gifFramePreviews[_gifPreviewIndex];
        GifInfoText.Text = $"Frame {_gifPreviewIndex + 1}/{_gifFramePreviews.Count} · {_gifFramePreviews.Count} frames · playback duration {FormatEta(TimeSpan.FromMilliseconds(_gifFramePreviews.Count * GetGifFrameInterval().TotalMilliseconds))} at selected speed.";
    }

    private void GifSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _gifSpeed = (int)Math.Round(e.NewValue);
        if (_isGif)
        {
            _gifPreviewTimer.Interval = GetGifFrameInterval();
            UpdateGifSpeedDisplay();
        }
    }

    private TimeSpan GetGifFrameInterval() => TimeSpan.FromMilliseconds((11 - _gifSpeed) * 30 * GifPreviewPlaybackCalibration);

    private void UpdateGifSpeedDisplay()
    {
        if (!_isGif || _gifFramePreviews.Count == 0) return;
        GifSpeedText.Text = $"Speed {_gifSpeed}/10";
        var playbackMs = _gifFramePreviews.Count * GetGifFrameInterval().TotalMilliseconds;
        GifInfoText.Text = $"{_gifFramePreviews.Count} selected frames / {_gifSourceFrameCount} source frames · source duration {_gifSourceDurationMs} ms · estimated keyboard playback {playbackMs / 1000.0:0.0} s (uniform frame timing).";
    }

    private void UpdateSelectedMediaActionState(bool reportReason = true)
    {
        var canTransfer = _device is not null && _commandCharacteristic is not null && _displayCharacteristic is not null &&
            _notificationCharacteristics.Count > 0 && _imagePath is not null && FinalPreview.Source is not null &&
            (!_isGif || _productGeneration == 3);
        FirstPacketButton.IsEnabled = canTransfer;
        if (canTransfer)
        {
            FirstPacketButton.ToolTip = "Send the prepared dial to the connected keyboard.";
            return;
        }

        var reason = _device is null ? "Connect to a keyboard before transferring."
            : _commandCharacteristic is null || _displayCharacteristic is null || _notificationCharacteristics.Count == 0
                ? "The keyboard connection is not ready for dial transfer. Reconnect and try again."
            : _imagePath is null ? "Choose a picture or GIF first."
            : FinalPreview.Source is null ? "Preparing the exact dial preview…"
            : _isGif && _productGeneration is null ? "Query version to confirm generation 3 before sending a GIF."
            : _isGif ? $"GIF transfer requires generation 3; this keyboard reported generation {_productGeneration}."
            : "The dial is not ready to transfer.";
        FirstPacketButton.ToolTip = reason;
        if (reportReason) SetStatus(reason);
    }

    private async void FirstPacket_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_commandCharacteristic is null || _displayCharacteristic is null || _notificationCharacteristics.Count == 0 || _imagePath is null) return;
        FirstPacketButton.IsEnabled = false;
        QueryButton.IsEnabled = false;
        ChooseImageButton.IsEnabled = false;
        CropCanvas.IsEnabled = false;
        GifSpeedSlider.IsEnabled = false;
        TransferProgressBar.Visibility = Visibility.Visible;
        TransferProgressText.Visibility = Visibility.Visible;
        TransferProgressBar.Value = 0;
        TransferProgressText.Text = "Preparing image transfer…";
        try
        {
            byte[] imageBytes;
            if (_isGif)
            {
                if (_productGeneration != 3) throw new InvalidOperationException("GIF upload is enabled only for confirmed product generation 3 keyboards.");
                if (_gifFrameRgb565.Count == 0 || _gifFrameRgb565.Count != _gifFrames.Count)
                    throw new InvalidOperationException("GIF frames have not finished preparing. Re-select the GIF or adjust its crop first.");
                imageBytes = BuildGifPayload(_gifFrameRgb565, _gifSpeed);
            }
            else
            {
                var cropPixels = GetCropPixels();
                var fit = _fitEntireImage; var fitScale = _fitScale;
                var prepared = await Task.Run(() => LoadDialRgb565(_imagePath!, cropPixels, fit, fitScale));
                imageBytes = prepared.Bytes;
                FinalPreview.Source = prepared.Preview;
            }
            var dialBlocks = BuildDialBlocks(imageBytes, !_isGif);
            var totalPackets = dialBlocks.Sum(block => block.Count);
            var displayWriteOption = GattWriteOption.WriteWithoutResponse;
            var interPacketDelayMs = FastPacketPacingDelayMs;
            var interBlockDelayMs = FastBlockPacingDelayMs;
            Log("upload", $"Prepared RGB565 image bytes={imageBytes.Length}; blocks={dialBlocks.Count}; packets={totalPackets}; first-block packet lengths=[{string.Join(",", dialBlocks[0].Select(p => p.Length))}]; last-block packet lengths=[{string.Join(",", dialBlocks[^1].Select(p => p.Length))}].");
            Log("upload", $"Display packet write mode={displayWriteOption}; inter-packet delay={interPacketDelayMs}ms; minimum block interval={interBlockDelayMs}ms; each block waits for its keyboard ACK.");
            if (_service?.Session is { } session && session.MaxPduSize > 0)
            {
                var maxValueLength = session.MaxPduSize - 3;
                var oversized = dialBlocks.SelectMany(block => block).Max(p => p.Length);
                if (oversized > maxValueLength)
                    throw new InvalidOperationException(
                        $"Windows negotiated a GATT PDU of {session.MaxPduSize} bytes, allowing {maxValueLength}-byte values, " +
                        $"but this keyboard's dial protocol needs {oversized}-byte writes (PDU {oversized + 3}). " +
                        "No dial data was sent. Disconnect and reconnect, then check the log for GattSession MaxPduSize=247. " +
                        "If it remains 23, this BLE connection cannot carry the keyboard's required packet size.");
            }

            // Static and GIF dials use distinct 09 03 metadata bodies; start and readiness remain shared.
            var metadata = _isGif ? BuildGifMetadata(imageBytes.Length) : new byte[] { 0x09, 0x03, 0x01, 0x00, 0x0B, 0x00, 0x00, 0xFF, 0xFD,
                (byte)(imageBytes.Length >> 24), (byte)(imageBytes.Length >> 16), (byte)(imageBytes.Length >> 8), (byte)imageBytes.Length,
                0xFF, 0xFF, 0xFF };
            var metadataReply = await SendCommandAndWaitAsync(metadata,
                bytes => bytes.Length == 11 && bytes[8] == 0x09 && bytes[9] == 0x04,
                _isGif ? "GIF dial metadata" : "dial metadata", TimeSpan.FromSeconds(8));
            var metadataStatus = metadataReply[10];
            Log("upload-test", $"Dial metadata response status=0x{metadataStatus:X2}; raw={Convert.ToHexString(metadataReply)}.");
            if (metadataStatus is 0x01 or 0x05)
                throw new InvalidOperationException(metadataStatus == 0x01 ? "Keyboard rejected the dial metadata (status 1)." : "Keyboard reports busy (status 5); no image packet was sent.");

            var startBody = new byte[] { 0x08, 0x03, 0x00, 0xFF, 0xFF, 0xFF, 0x00, 0xFF, 0xFF, 0xFF };
            var startReply = await SendCommandAndWaitAsync(startBody,
                bytes => bytes.Length == 11 && bytes[0] == 0x88 && bytes[8] == 0x08 && bytes[9] == 0x04,
                "dial start", TimeSpan.FromSeconds(8));
            Log("upload-test", $"Dial-start response raw={Convert.ToHexString(startReply)}.");
            if (startReply[10] != 0x02)
                throw new InvalidOperationException($"Dial-start response status was 0x{startReply[10]:X2}, expected 02; no image packet was sent.");

            byte? deviceState = null;
            for (var attempt = 1; attempt <= 6; attempt++)
            {
                var statusReply = await SendCommandAndWaitAsync(new byte[] { 0x00, 0x13, 0x00 },
                    bytes => bytes.Length == 14 && bytes[6] == 0x06 && bytes[9] == 0x14,
                    $"keyboard status query {attempt}/6", TimeSpan.FromSeconds(5));
                deviceState = statusReply[13];
                Log("upload-test", $"Keyboard status attempt={attempt}; state={deviceState}; raw={Convert.ToHexString(statusReply)}.");
                if (deviceState == 3) break;
                if (attempt < 6) await Task.Delay(100);
            }
            if (deviceState != 3)
                throw new InvalidOperationException($"Keyboard did not report ready state 3 (last state {deviceState}); no image packet was sent.");

            lock (_ackLock) _dialAckStatuses.Clear();
            var sentPackets = 0;
            var transferClock = Stopwatch.StartNew();
            TransferProgressText.Text = $"Sending 0/{totalPackets} packets · estimating transfer rate…";
            for (var blockIndex = 0; blockIndex < dialBlocks.Count; blockIndex++)
            {
                var block = dialBlocks[blockIndex];
                var blockClock = Stopwatch.StartNew();
                int ackCountBeforeBlock;
                lock (_ackLock) ackCountBeforeBlock = _dialAckStatuses.Count;
                var isCheckpointBlock = blockIndex == 0 || (blockIndex + 1) % 64 == 0 || blockIndex + 1 == dialBlocks.Count;
                if (isCheckpointBlock)
                    Log("upload", $"Starting image block {blockIndex + 1}/{dialBlocks.Count}; packets={block.Count}; sent={sentPackets}/{totalPackets}.");
                for (var packetIndex = 0; packetIndex < block.Count; packetIndex++)
                {
                    var packet = block[packetIndex];
                    var packetWriter = new DataWriter();
                    packetWriter.WriteBytes(packet);
                    var packetResult = await _displayCharacteristic.WriteValueWithResultAsync(packetWriter.DetachBuffer(), displayWriteOption);
                    if (packetResult.Status != GattCommunicationStatus.Success)
                    {
                        Log("write-failure", $"Image block {blockIndex + 1}/{dialBlocks.Count} packet {packetIndex + 1}/{block.Count} ({sentPackets + 1}/{totalPackets}) failed; characteristic={DisplayId}; option={displayWriteOption}; length={packet.Length}; bytes={Convert.ToHexString(packet)}; OS GATT result={packetResult.Status}; protocolError={FormatProtocolError(packetResult.ProtocolError)}.");
                        throw new InvalidOperationException($"Image packet {sentPackets + 1}/{totalPackets} failed at the GATT layer. The transfer stopped; remaining packets were not sent.");
                    }
                    sentPackets++;
                    if (sentPackets == 1 || sentPackets % 50 == 0 || sentPackets == totalPackets)
                    {
                        TransferProgressBar.Value = sentPackets * 100.0 / totalPackets;
                        var averageSecondsPerPacket = transferClock.Elapsed.TotalSeconds / sentPackets;
                        var remaining = TimeSpan.FromSeconds(averageSecondsPerPacket * (totalPackets - sentPackets));
                        TransferProgressText.Text = sentPackets == totalPackets
                            ? $"Sent {sentPackets}/{totalPackets} packets · waiting for keyboard confirmation…"
                            : $"Sending {sentPackets}/{totalPackets} packets · block {blockIndex + 1}/{dialBlocks.Count} · about {FormatEta(remaining)} remaining";
                        SetStatus($"Image transfer: {sentPackets}/{totalPackets} packets sent ({blockIndex + 1}/{dialBlocks.Count} blocks)…");
                    }
                    if (sentPackets % 250 == 0 || sentPackets == totalPackets)
                        Log("upload-progress", $"Sent {sentPackets}/{totalPackets} packets across {blockIndex + 1}/{dialBlocks.Count} blocks; elapsed={FormatEta(transferClock.Elapsed)}.");
                    // Each OS write completes before the next packet is submitted.
                    if (interPacketDelayMs > 0 && packetIndex + 1 < block.Count)
                        await Task.Delay(interPacketDelayMs);
                }
                if (blockIndex + 1 < dialBlocks.Count)
                {
                    var blockAck = await WaitForDialBlockAckAsync(blockIndex + 1, ackCountBeforeBlock, TimeSpan.FromSeconds(5));
                    if (blockAck == 0x06)
                        throw new InvalidOperationException($"Keyboard reported error ACK 06 after image block {blockIndex + 1}/{dialBlocks.Count}; remaining blocks were not sent.");
                    var remainingPacing = TimeSpan.FromMilliseconds(interBlockDelayMs) - blockClock.Elapsed;
                    if (remainingPacing > TimeSpan.Zero)
                        await Task.Delay(remainingPacing);
                }
                if (blockIndex + 1 < dialBlocks.Count)
                {
                    if (isCheckpointBlock)
                    {
                        int acknowledgedBlocks;
                        lock (_ackLock) acknowledgedBlocks = _dialAckStatuses.Count;
                        Log("upload", $"Block {blockIndex + 1}/{dialBlocks.Count} acknowledged; {acknowledgedBlocks} ACKs received. Applying max rate of {1000.0 / interBlockDelayMs:0.0} blocks/sec.");
                    }
                }
            }
            var ackWaitTimeout = TimeSpan.FromSeconds(Math.Clamp(10 + dialBlocks.Count * 0.2, 15, 360));
            SetStatus($"All {sentPackets} image packets sent; waiting up to {FormatEta(ackWaitTimeout)} for keyboard confirmation…");
            byte[] ackStatuses;
            var ackWaitClock = Stopwatch.StartNew();
            var nextAckUiUpdate = TimeSpan.Zero;
            while (ackWaitClock.Elapsed < ackWaitTimeout)
            {
                lock (_ackLock) ackStatuses = _dialAckStatuses.ToArray();
                if (ackStatuses.Contains((byte)0x02) || ackStatuses.Contains((byte)0x06)) break;
                if (ackWaitClock.Elapsed >= nextAckUiUpdate)
                {
                    TransferProgressText.Text = $"All packets submitted · waiting for keyboard confirmation ({ackStatuses.Length}/{dialBlocks.Count} blocks acknowledged)…";
                    nextAckUiUpdate = ackWaitClock.Elapsed + TimeSpan.FromSeconds(1);
                }
                await Task.Delay(100);
            }
            lock (_ackLock) ackStatuses = _dialAckStatuses.ToArray();
            var ackSummary = ackStatuses.Length == 0
                ? "none received"
                : string.Join(", ", ackStatuses.GroupBy(status => status).OrderBy(group => group.Key)
                    .Select(group => $"0x{group.Key:X2}×{group.Count()}"));
            Log("upload", $"All image blocks submitted: packets sent={sentPackets}/{totalPackets}; protocol ACKs={ackStatuses.Length}/{dialBlocks.Count}; ACK wait={FormatEta(ackWaitClock.Elapsed)}; statuses=[{ackSummary}].");
            if (ackStatuses.Contains((byte)0x06))
                SetStatus($"All packets were sent, but the keyboard reported error ACK 06. Review the log; ACK statuses were [{ackSummary}].");
            else if (ackStatuses.Contains((byte)0x02))
                SetStatus($"All packets sent; keyboard reported success ACK 02. ACK statuses were [{ackSummary}].");
            else
                SetStatus($"Timed out after {FormatEta(ackWaitClock.Elapsed)} waiting for keyboard confirmation; ACKs received={ackStatuses.Length}/{dialBlocks.Count}.");
            TransferProgressText.Text = ackStatuses.Contains((byte)0x02) && !ackStatuses.Contains((byte)0x06)
                ? $"Complete · {sentPackets}/{totalPackets} packets · keyboard confirmed success"
                : ackStatuses.Contains((byte)0x06)
                    ? $"Keyboard reported error · {sentPackets}/{totalPackets} packets · ACKs: {ackSummary}"
                    : $"Confirmation timed out · {sentPackets}/{totalPackets} packets · ACKs: {ackStatuses.Length}/{dialBlocks.Count}";
        }
        catch (Exception ex)
        {
            TransferProgressText.Text = $"Stopped at {TransferProgressBar.Value / 100.0:P0} · {ex.Message}";
            Log("error", "Image transfer stopped: " + ex);
            SetStatus("Image transfer stopped: " + ex.Message);
        }
        finally
        {
            CropCanvas.IsEnabled = true;
            GifSpeedSlider.IsEnabled = true;
            UpdateSelectedMediaActionState(reportReason: false);
            QueryButton.IsEnabled = _device is not null;
            ChooseImageButton.IsEnabled = true;
        }
    }

    private static string FormatEta(TimeSpan duration)
    {
        var seconds = Math.Max(0, (int)Math.Ceiling(duration.TotalSeconds));
        return seconds >= 60 ? $"{seconds / 60}m {seconds % 60}s" : $"{seconds}s";
    }

    private async Task<byte> WaitForDialBlockAckAsync(int blockNumber, int previousAckCount, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            lock (_ackLock)
            {
                if (_dialAckStatuses.Count > previousAckCount)
                    return _dialAckStatuses[^1];
            }
            await Task.Delay(10);
        }

        throw new TimeoutException($"Keyboard did not acknowledge image block {blockNumber} within {FormatEta(timeout)}; stopped before sending the next block.");
    }

    private async Task<byte[]> SendCommandAndWaitAsync(byte[] body, Func<byte[], bool> match, string label, TimeSpan timeout)
    {
        if (_commandCharacteristic is null) throw new InvalidOperationException("Command characteristic is disconnected.");
        var response = WaitForNotificationAsync(match, timeout, label + " response");
        var frame = BuildCommandFrame(body);
        Log("write", $"{label}: characteristic={CommandId}; option=WriteWithResponse; bytes={Convert.ToHexString(frame)}");
        var writer = new DataWriter();
        writer.WriteBytes(frame);
        var result = await _commandCharacteristic.WriteValueWithResultAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse);
        Log("write-result", $"{label} OS GATT result={result.Status}; protocolError={FormatProtocolError(result.ProtocolError)}.");
        if (result.Status != GattCommunicationStatus.Success)
        {
            _ = response.ContinueWith(_ => { });
            throw new InvalidOperationException($"{label} failed at the GATT layer: {result.Status}.");
        }
        return await response;
    }

    private Task<byte[]> WaitForNotificationAsync(Func<byte[], bool> match, TimeSpan timeout, string label)
    {
        var source = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiterLock) _notificationWaiters.Add((match, source));
        return AwaitNotificationAsync(source, timeout, label);
    }

    private async Task<byte[]> AwaitNotificationAsync(TaskCompletionSource<byte[]> source, TimeSpan timeout, string label)
    {
        var delay = Task.Delay(timeout);
        if (await Task.WhenAny(source.Task, delay) != source.Task)
        {
            lock (_waiterLock) _notificationWaiters.RemoveAll(item => item.Source == source);
            throw new TimeoutException($"Timed out waiting for {label}.");
        }
        return await source.Task;
    }

    private static List<List<byte[]>> BuildDialBlocks(byte[] rgb565, bool verifyStaticLayout = true)
    {
        if (rgb565.Length < FirstPacketImageBytes) throw new InvalidOperationException("Dial payload is too short for the first packet.");
        var output = new List<List<byte[]>>();
        var blockCount = (int)Math.Ceiling((double)rgb565.Length / DialBlockBytes);
        for (var blockIndex = 0; blockIndex < blockCount; blockIndex++)
        {
            var blockOffset = blockIndex * DialBlockBytes;
            var blockLength = Math.Min(DialBlockBytes, rgb565.Length - blockOffset);
            var blockPackets = new List<byte[]>();
            var packetCount = (int)Math.Ceiling((double)blockLength / DialChunkBytes);
            var firstContinuation = 0;
            if (blockIndex == 0)
            {
                var header = new List<byte>(FirstPacketImageBytes + 26);
                header.AddRange(Convert.FromHexString("880000"));
                AddBigEndian(header, rgb565.Length + 17);
                header.AddRange(Convert.FromHexString("000805010009"));
                header.AddRange(new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0x00, 0xFF, 0xFF, 0xFF });
                header.AddRange(Convert.FromHexString("0202FFFF"));
                header.AddRange(rgb565.AsSpan(0, FirstPacketImageBytes).ToArray());
                header.Add(Xor(header));
                if (header.Count != 244) throw new InvalidOperationException($"Unexpected first packet length {header.Count}; expected 244.");
                blockPackets.Add(header.ToArray());
                firstContinuation = 1;
            }
            for (var packetIndex = firstContinuation; packetIndex < packetCount; packetIndex++)
            {
                var offset = packetIndex * DialChunkBytes - (blockIndex == 0 ? 25 : 0);
                var length = Math.Min(DialChunkBytes, blockLength - offset);
                if (offset < 0 || length <= 0) throw new InvalidOperationException($"Invalid image packet slice block={blockIndex}, packet={packetIndex}, offset={offset}, length={length}.");
                var data = rgb565.AsSpan(blockOffset + offset, length).ToArray();
                blockPackets.Add(data.Append(Xor(data)).ToArray());
            }
            output.Add(blockPackets);
        }
        var totalPackets = output.Sum(block => block.Count);
        if (verifyStaticLayout && (rgb565.Length != DialWidth * DialHeight * 2 || output.Count != 27 || totalPackets != 457 || output[0].Count != 17 || output[0][^1].Length != 234 || output[^1].Count != 15))
            throw new InvalidOperationException($"Unexpected image packet layout: blocks={output.Count}, packets={totalPackets}.");
        return output;
    }

    private static GifAnimation DecodeGif(string path)
    {
        var decoder = new GifBitmapDecoder(new Uri(path, UriKind.Absolute), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("The GIF contains no frames.");
        var lefts = decoder.Frames.Select(frame => ReadGifMetadataInt(frame.Metadata as BitmapMetadata, "/imgdesc/Left", 0)).ToArray();
        var tops = decoder.Frames.Select(frame => ReadGifMetadataInt(frame.Metadata as BitmapMetadata, "/imgdesc/Top", 0)).ToArray();
        var logicalWidth = ReadGifMetadataInt(decoder.Metadata as BitmapMetadata, "/logscrdesc/Width", 0);
        var logicalHeight = ReadGifMetadataInt(decoder.Metadata as BitmapMetadata, "/logscrdesc/Height", 0);
        logicalWidth = Math.Max(logicalWidth, decoder.Frames.Select((frame, i) => lefts[i] + frame.PixelWidth).Max());
        logicalHeight = Math.Max(logicalHeight, decoder.Frames.Select((frame, i) => tops[i] + frame.PixelHeight).Max());
        var canvas = new byte[logicalWidth * logicalHeight * 4];
        var output = new List<BitmapSource>(decoder.Frames.Count);
        var durationMs = decoder.Frames.Sum(frame => Math.Max(10, ReadGifMetadataInt(frame.Metadata as BitmapMetadata, "/grctlext/Delay", 10) * 10));
        byte[]? restorePrevious = null;
        Rect previousRect = Rect.Empty;
        var previousDisposal = 0;

        for (var frameIndex = 0; frameIndex < Math.Min(decoder.Frames.Count, 50); frameIndex++)
        {
            if (frameIndex > 0)
            {
                if (previousDisposal == 2 && !previousRect.IsEmpty)
                    ClearGifRect(canvas, logicalWidth, logicalHeight, previousRect);
                else if (previousDisposal == 3 && restorePrevious is not null)
                    canvas = (byte[])restorePrevious.Clone();
            }

            var frame = decoder.Frames[frameIndex];
            var disposal = ReadGifMetadataInt(frame.Metadata as BitmapMetadata, "/grctlext/Disposal", 0);
            restorePrevious = disposal == 3 ? (byte[])canvas.Clone() : null;
            var sourceRect = new Int32Rect(0, 0, frame.PixelWidth, frame.PixelHeight);
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
            var framePixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
            converted.CopyPixels(framePixels, frame.PixelWidth * 4, 0);
            AlphaCompositeGifFrame(canvas, logicalWidth, logicalHeight, framePixels, frame.PixelWidth, frame.PixelHeight, lefts[frameIndex], tops[frameIndex]);

            var snapshot = BitmapSource.Create(logicalWidth, logicalHeight, 96, 96, PixelFormats.Pbgra32, null, (byte[])canvas.Clone(), logicalWidth * 4);
            snapshot.Freeze(); output.Add(snapshot);
            previousDisposal = disposal;
            previousRect = new Rect(lefts[frameIndex], tops[frameIndex], sourceRect.Width, sourceRect.Height);
        }
        return new GifAnimation(output, decoder.Frames.Count, durationMs);
    }

    private static int ReadGifMetadataInt(BitmapMetadata? metadata, string query, int fallback)
    {
        try { return metadata is not null && metadata.ContainsQuery(query) ? Convert.ToInt32(metadata.GetQuery(query), CultureInfo.InvariantCulture) : fallback; }
        catch { return fallback; }
    }

    private static void ClearGifRect(byte[] canvas, int canvasWidth, int canvasHeight, Rect rect)
    {
        var left = Math.Clamp((int)rect.Left, 0, canvasWidth); var top = Math.Clamp((int)rect.Top, 0, canvasHeight);
        var right = Math.Clamp((int)rect.Right, 0, canvasWidth); var bottom = Math.Clamp((int)rect.Bottom, 0, canvasHeight);
        for (var y = top; y < bottom; y++) Array.Clear(canvas, (y * canvasWidth + left) * 4, (right - left) * 4);
    }

    private static void AlphaCompositeGifFrame(byte[] canvas, int canvasWidth, int canvasHeight, byte[] frame, int frameWidth, int frameHeight, int left, int top)
    {
        for (var y = 0; y < frameHeight; y++)
        for (var x = 0; x < frameWidth; x++)
        {
            var dx = left + x; var dy = top + y;
            if (dx < 0 || dy < 0 || dx >= canvasWidth || dy >= canvasHeight) continue;
            var si = (y * frameWidth + x) * 4; var di = (dy * canvasWidth + dx) * 4;
            var alpha = frame[si + 3]; var inverse = 255 - alpha;
            canvas[di] = (byte)Math.Min(255, frame[si] + (canvas[di] * inverse + 127) / 255);
            canvas[di + 1] = (byte)Math.Min(255, frame[si + 1] + (canvas[di + 1] * inverse + 127) / 255);
            canvas[di + 2] = (byte)Math.Min(255, frame[si + 2] + (canvas[di + 2] * inverse + 127) / 255);
            canvas[di + 3] = (byte)Math.Min(255, alpha + (canvas[di + 3] * inverse + 127) / 255);
        }
    }

    private static (byte[] Bytes, BitmapSource Preview) LoadDialRgb565(string path, Int32Rect cropRect, bool fitEntireImage = false, double fitScale = 1.0)
    {
        var source = new BitmapImage();
        source.BeginInit();
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.UriSource = new Uri(path, UriKind.Absolute);
        source.EndInit();
        source.Freeze();

        return ConvertBitmapToRgb565(source, cropRect, true, fitEntireImage, fitScale);
    }

    private static (byte[] Bytes, BitmapSource Preview) ConvertBitmapToRgb565(BitmapSource source, Int32Rect cropRect, bool jpegRoundTrip, bool fitEntireImage = false, double fitScale = 1.0)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            if (fitEntireImage)
            {
                context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, DialWidth, DialHeight));
                var scale = Math.Min(DialWidth / (double)source.PixelWidth, DialHeight / (double)source.PixelHeight) * Math.Clamp(fitScale, 0.25, 1.0);
                var width = source.PixelWidth * scale;
                var height = source.PixelHeight * scale;
                context.DrawImage(source, new Rect((DialWidth - width) / 2, (DialHeight - height) / 2, width, height));
            }
            else
            {
                var left = Math.Clamp(cropRect.X, 0, source.PixelWidth - 1);
                var top = Math.Clamp(cropRect.Y, 0, source.PixelHeight - 1);
                var widthPixels = Math.Clamp(cropRect.Width, 1, source.PixelWidth - left);
                var heightPixels = Math.Clamp(cropRect.Height, 1, source.PixelHeight - top);
                var crop = new CroppedBitmap(source, new Int32Rect(left, top, widthPixels, heightPixels));
                crop.Freeze();
                context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, DialWidth, DialHeight));
                context.DrawImage(crop, new Rect(0, 0, DialWidth, DialHeight));
            }
        }
        var cropped = new RenderTargetBitmap(DialWidth, DialHeight, 96, 96, PixelFormats.Pbgra32);
        cropped.Render(visual);

        BitmapSource convertedSource = cropped;
        if (jpegRoundTrip)
        {
            var jpeg = new JpegBitmapEncoder { QualityLevel = 70 };
            jpeg.Frames.Add(BitmapFrame.Create(cropped));
            using var jpegStream = new MemoryStream();
            jpeg.Save(jpegStream);
            if (jpegStream.Length / 1024 > 100)
            {
                for (var quality = 100; quality >= 0 && jpegStream.Length / 1024 > 100; quality -= 10)
                {
                    jpegStream.SetLength(0);
                    jpeg = new JpegBitmapEncoder { QualityLevel = quality };
                    jpeg.Frames.Add(BitmapFrame.Create(cropped));
                    jpeg.Save(jpegStream);
                }
            }
            jpegStream.Position = 0;
            var decoded = BitmapFrame.Create(jpegStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            decoded.Freeze();
            convertedSource = decoded;
        }
        var bgra = new FormatConvertedBitmap(convertedSource, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[DialWidth * DialHeight * 4];
        bgra.CopyPixels(pixels, DialWidth * 4, 0);
        var rgb565 = new byte[DialWidth * DialHeight * 2];
        for (var i = 0; i < DialWidth * DialHeight; i++)
        {
            var b = pixels[i * 4];
            var g = pixels[i * 4 + 1];
            var r = pixels[i * 4 + 2];
            var value = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
            rgb565[i * 2] = (byte)(value >> 8);
            rgb565[i * 2 + 1] = (byte)value;
        }
        var previewPixels = new byte[DialWidth * DialHeight * 4];
        for (var i = 0; i < DialWidth * DialHeight; i++)
        {
            var value = (ushort)((rgb565[i * 2] << 8) | rgb565[i * 2 + 1]);
            var r5 = (value >> 11) & 0x1F;
            var g6 = (value >> 5) & 0x3F;
            var b5 = value & 0x1F;
            previewPixels[i * 4] = (byte)((b5 << 3) | (b5 >> 2));
            previewPixels[i * 4 + 1] = (byte)((g6 << 2) | (g6 >> 4));
            previewPixels[i * 4 + 2] = (byte)((r5 << 3) | (r5 >> 2));
            previewPixels[i * 4 + 3] = 0xFF;
        }
        var exactPreview = BitmapSource.Create(DialWidth, DialHeight, 96, 96, PixelFormats.Bgra32, null, previewPixels, DialWidth * 4);
        exactPreview.Freeze();
        return (rgb565, exactPreview);
    }

    private static byte[] BuildGifMetadata(int binSize)
    {
        var body = new List<byte> { 0x09, 0x03, 0x04, 0x00, 0x08, 0x00, 0x00, 0xFF, 0xFC };
        AddBigEndian(body, binSize);
        body.AddRange(new byte[] { 0x05, 0x00, 0x14 });
        body.AddRange(Enumerable.Repeat((byte)0xFF, 20));
        return body.ToArray();
    }

    private static byte[] BuildGifPayload(IReadOnlyList<byte[]> frames, int speed)
    {
        if (frames.Count is < 1 or > 50) throw new InvalidOperationException("Generation 3 GIF dials support 1 to 50 frames.");
        if (frames.Any(frame => frame.Length != DialWidth * DialHeight * 2))
            throw new InvalidOperationException("Every GIF frame must contain exactly 320x172 RGB565 pixels.");
        if (speed is < 1 or > 10) throw new InvalidOperationException("GIF speed must be between 1 and 10.");

        var frameBytes = frames.Sum(frame => frame.Length);
        var frameOffsets = new byte[frames.Count * 4];
        var frameOffset = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            WriteLittleEndian(frameOffsets, i * 4, frameOffset);
            frameOffset += frames[i].Length;
        }

        var settings = new byte[84];
        var dimension = new byte[] { 0x40, 0x00, 0xAC };
        settings[0] = 21; settings[1] = 1; settings[7] = 1; settings[10] = 1;
        Array.Copy(dimension, 0, settings, 11, 3);
        settings[28] = 20; settings[29] = 3; settings[31] = 1;
        settings[32] = (byte)(11 - speed); settings[35] = (byte)frames.Count; settings[38] = 1;
        Array.Copy(dimension, 0, settings, 39, 3);
        settings[56] = 20; settings[57] = 4; settings[66] = 1;
        Array.Copy(dimension, 0, settings, 67, 3);

        var gifHeader = new byte[28];
        byte[] fixedHeader = { 0x00, 0x44, 0x4C, 0x58, 0xFC, 0xFF, 0x00, 0x00, 0x60, 0x01, 0x03, 0x00 };
        Array.Copy(fixedHeader, gifHeader, fixedHeader.Length);
        gifHeader[12] = (byte)frames.Count;
        gifHeader[15] = 1;
        var sectionOffsets = new byte[16];
        var dataOffset = 368;
        WriteLittleEndian(sectionOffsets, 0, dataOffset);
        dataOffset += settings.Length;
        WriteLittleEndian(sectionOffsets, 4, dataOffset);
        dataOffset += frameOffsets.Length;
        WriteLittleEndian(sectionOffsets, 8, dataOffset);
        dataOffset += frameBytes;
        WriteLittleEndian(sectionOffsets, 12, dataOffset);

        using var stream = new MemoryStream(368 + settings.Length + frameOffsets.Length + frameBytes + 4);
        stream.Write(gifHeader);
        stream.Write(Enumerable.Repeat((byte)0xFF, 324).ToArray());
        stream.Write(sectionOffsets);
        stream.Write(settings);
        stream.Write(frameOffsets);
        foreach (var frame in frames) stream.Write(frame);
        stream.Write(new byte[] { 0xFC, 0xFF, 0x00, 0x00 });
        return stream.ToArray();
    }

    private static void WriteLittleEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)value;
        target[offset + 1] = (byte)(value >> 8);
        target[offset + 2] = (byte)(value >> 16);
        target[offset + 3] = (byte)(value >> 24);
    }

    private static void AddBigEndian(List<byte> output, int value)
    {
        output.Add((byte)(value >> 24)); output.Add((byte)(value >> 16));
        output.Add((byte)(value >> 8)); output.Add((byte)value);
    }

    private static byte Xor(IEnumerable<byte> bytes)
    {
        byte result = 0;
        foreach (var value in bytes) result ^= value;
        return result;
    }

    private static byte[] BuildCommandFrame(byte[] payload)
    {
        var frame = new byte[8 + payload.Length];
        frame[0] = 0x88;
        frame[1] = 0x00;
        frame[2] = 0x00;
        var length = BitConverter.GetBytes(payload.Length);
        if (BitConverter.IsLittleEndian) Array.Reverse(length);
        System.Buffer.BlockCopy(length, 0, frame, 3, 4);
        byte xor = 0;
        foreach (var b in payload) xor ^= b;
        frame[7] = xor;
        System.Buffer.BlockCopy(payload, 0, frame, 8, payload.Length);
        return frame;
    }

    private async void Disconnect_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_subscribed)
        {
            foreach (var characteristic in _notificationCharacteristics)
            {
                try
                {
                    var result = await characteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None);
                    Log("notify", $"Notification disable characteristic={characteristic.Uuid}; status={result.Status}; protocolError={FormatProtocolError(result.ProtocolError)}.");
                }
                catch (Exception ex) { Log("error", $"Disable notifications for {characteristic.Uuid}: {ex.Message}"); }
            }
        }
        Cleanup();
        SetStatus("Disconnected.");
        Log("disconnect", "BLE connection released.");
    }

    private void Cleanup()
    {
        _productGeneration = null;
        foreach (var characteristic in _notificationCharacteristics) characteristic.ValueChanged -= Notify_ValueChanged;
        _notificationCharacteristics.Clear();
        _subscribed = false;
        _commandCharacteristic = null;
        _notifyCharacteristic = null;
        _displayCharacteristic = null;
        lock (_waiterLock)
        {
            foreach (var waiter in _notificationWaiters) waiter.Source.TrySetException(new InvalidOperationException("Disconnected while waiting for a device response."));
            _notificationWaiters.Clear();
        }
        _service?.Dispose();
        _service = null;
        _device?.Dispose();
        _device = null;
        if (Dispatcher.CheckAccess())
        {
            DisconnectButton.IsEnabled = false;
            QueryButton.IsEnabled = false;
            FirstPacketButton.IsEnabled = false;
            ConnectButton.IsEnabled = _selectedRow is not null;
        }
    }

    private async void Export_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Wuque probe support bundle",
            Filter = "JSON support bundle (*.json)|*.json",
            FileName = $"wuque-probe-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json",
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog(this) != true) return;
        var events = SnapshotEvents();
        var selected = _selectedRow;
        var bundle = new
        {
            exportedAt = DateTimeOffset.Now,
            app = "Pocket Wuque BLE Probe",
            os = Environment.OSVersion.VersionString,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            adapter = _adapterInfo,
            selectedDevice = selected is null ? null : new
            {
                selected.Name, selected.Address, selected.Rssi, selected.ServiceUuidsText,
                selected.ManufacturerDataText, selected.RawAdvertisementHex
            },
            transport = new { maxPduSize = _maxPduSize, lastWriteStatus = _lastWriteStatus },
            protocol = new { service = ServiceId, command = CommandId, notifications = NotifyId, displayData = DisplayId },
            imageTransfer = new
            {
                selectedImage = _imagePath,
                mediaType = _isGif ? "GIF" : "static image",
                layout = _fitEntireImage ? "fit-entire-image" : "crop-to-fill",
                fitScalePercent = _fitEntireImage ? (int?)Math.Round(_fitScale * 100) : null,
                sourceFrameCount = _isGif ? _gifSourceFrameCount : 1,
                uploadedFrameCount = _isGif ? _gifFrameRgb565.Count : 1,
                sourceDurationMs = _isGif ? _gifSourceDurationMs : (int?)null,
                gifSpeed = _isGif ? _gifSpeed : (int?)null,
                action = _isGif ? "complete custom GIF dial transfer" : "complete custom dial image transfer"
            },
            events
        };
        try
        {
            var json = JsonSerializer.Serialize(bundle, new JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(dialog.FileName, json, Encoding.UTF8);
            Log("export", $"Local support bundle saved to {dialog.FileName}.");
        }
        catch (Exception ex) { SetStatus("Export failed: " + ex.Message); Log("error", "Export: " + ex); }
    }

    private void Log(string category, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, category, message);
        lock (_eventLock) _events.Add(entry);
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AppendLog(entry)); return; }
        AppendLog(entry);
    }

    private void AppendLog(LogEntry entry)
    {
        LogBox.AppendText($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{entry.Category}] {entry.Message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private LogEntry[] SnapshotEvents() { lock (_eventLock) return _events.ToArray(); }
    private void SetStatus(string message) => StatusText.Text = message;

    private static string BufferHex(IBuffer buffer) => Convert.ToHexString(BufferBytes(buffer));
    private static byte[] BufferBytes(IBuffer buffer)
    {
        CryptographicBuffer.CopyToByteArray(buffer, out var bytes);
        return bytes;
    }
    private static string FormatProtocolError(byte? error) => error.HasValue ? $"0x{error.Value:X2}" : "none";

    private void Clear_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        LogBox.Clear();
        Log("log", "Visible log cleared; event history for export retained.");
    }

    private sealed record GifAnimation(List<BitmapSource> Frames, int SourceFrameCount, int DurationMs);
    private sealed record LogEntry(DateTimeOffset Timestamp, string Category, string Message);
}
