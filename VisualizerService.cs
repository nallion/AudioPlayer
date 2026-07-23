using System;
using System.Numerics;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Render;
using Windows.Storage;
using AudioVisualizerPlayer.Helpers;

namespace AudioVisualizerPlayer.Services
{
    /// <summary>
    /// Отдельный AudioGraph, который открывает тот же файл, что играет MediaPlayer,
    /// и отдаёт наружу уровни полос для отрисовки. Работает только пока страница
    /// с визуализатором на экране — создавайте/останавливайте этот сервис
    /// в OnNavigatedTo / OnNavigatedFrom страницы, а не держите его в App,
    /// чтобы не тратить ресурсы, когда пользователь просто слушает музыку в фоне.
    /// </summary>
    public class VisualizerService : IDisposable
    {
        private AudioGraph _graph;
        private AudioFileInputNode _fileInput;
        private AudioFrameOutputNode _frameOutput;

        private const int FftSize = 1024; // степень двойки
        private const int BarCount = 40;   // столько же баров, сколько на макете

        public event EventHandler<float[]> LevelsChanged;

        public async Task InitializeAsync(StorageFile file)
        {
            var settings = new AudioGraphSettings(AudioRenderCategory.Media)
            {
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency
            };

            var graphResult = await AudioGraph.CreateAsync(settings);
            if (graphResult.Status != AudioGraphCreationStatus.Success)
                throw new InvalidOperationException("Не удалось создать AudioGraph: " + graphResult.Status);

            _graph = graphResult.Graph;

            var fileInputResult = await _graph.CreateFileInputNodeAsync(file);
            if (fileInputResult.Status != AudioFileNodeCreationStatus.Success)
                throw new InvalidOperationException("Не удалось открыть файл для анализа: " + fileInputResult.Status);

            _fileInput = fileInputResult.FileInputNode;

            // Важно: НЕ подключаем этот граф к звуковому устройству (DeviceOutputNode) —
            // звук уже играет через MediaPlayer. Этот граф существует только для анализа,
            // поэтому выход — FrameOutputNode, а не колонки.
            _frameOutput = _graph.CreateFrameOutputNode();
            _fileInput.AddOutgoingConnection(_frameOutput);

            _graph.QuantumStarted += OnQuantumStarted;
        }

        private System.Threading.Timer _diagnosticsTimer;

        public void Start()
        {
            _graph?.Start();

            // Диагностика: если QuantumStarted вообще ни разу не сработал (а не просто
            // тихо упал внутри), это значит граф не "качается" сам по себе без
            // подключённого DeviceOutputNode — тогда одного try/catch в
            // OnQuantumStarted недостаточно, там просто нечему кидать исключение.
            _diagnosticsTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                    var path = System.IO.Path.Combine(folder.Path, "visualizer_diagnostics.log");
                    System.IO.File.WriteAllText(path,
                        $"QuantumStarted count after 3s: {_quantumCount}\n" +
                        $"Graph state: {(_graph != null ? "created" : "null")}\n");
                }
                catch { }
            }, null, 3000, System.Threading.Timeout.Infinite);
        }

        public void Stop() => _graph?.Stop();

        private static int _quantumCount = 0;
        private static bool _firstErrorLogged = false;

        private void OnQuantumStarted(AudioGraph sender, object args)
        {
            try
            {
                System.Threading.Interlocked.Increment(ref _quantumCount);

                Windows.Media.AudioFrame frame = _frameOutput.GetFrame();
                float[] samples = ExtractSamples(frame);
                if (samples == null || samples.Length < FftSize) return;

                // Берём последний блок нужного размера
                var chunk = new float[FftSize];
                Array.Copy(samples, samples.Length - FftSize, chunk, 0, FftSize);

                Complex[] spectrum = FFT.Transform(chunk);
                float[] bars = FFT.ToBars(spectrum, BarCount);

                LevelsChanged?.Invoke(this, bars);
            }
            catch (Exception ex)
            {
                // OnQuantumStarted вызывается из нативного audio-callback потока —
                // необработанное исключение здесь может тихо проглатываться самим
                // AudioGraph, никак не долетая ни до try/catch в UI-коде, ни до
                // Application.UnhandledException. Раз "спектра нет вообще" без
                // единого сообщения об ошибке — это ровно такой случай. Пишем
                // причину в отдельный лог-файл при первом же случае, чтобы увидеть
                // её на следующем прогоне через Device Portal → File Explorer →
                // LocalAppData → AudioVisualizerPlayer → LocalState → visualizer_error.log
                if (!_firstErrorLogged)
                {
                    _firstErrorLogged = true;
                    try
                    {
                        var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                        var path = System.IO.Path.Combine(folder.Path, "visualizer_error.log");
                        System.IO.File.WriteAllText(path, ex.ToString());
                    }
                    catch { /* если и лог не записался — ничего не поделать */ }
                }
            }
        }

        private unsafe float[] ExtractSamples(Windows.Media.AudioFrame frame)
        {
            using (var buffer = frame.LockBuffer(Windows.Media.AudioBufferAccessMode.Read))
            using (var reference = buffer.CreateReference())
            {
                ((IMemoryBufferByteAccess)reference).GetBuffer(out byte* dataInBytes, out uint capacityInBytes);
                float* dataInFloat = (float*)dataInBytes;
                uint capacityInFloats = capacityInBytes / sizeof(float);

                var result = new float[capacityInFloats];
                for (uint i = 0; i < capacityInFloats; i++)
                    result[i] = dataInFloat[i];

                return result;
            }
        }

        public void Dispose()
        {
            _diagnosticsTimer?.Dispose();
            _graph?.Stop();
            _fileInput?.Dispose();
            _graph?.Dispose();
        }
    }

    // Нужно для доступа к сырым байтам AudioFrame — стандартный COM-интероп UWP
    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
