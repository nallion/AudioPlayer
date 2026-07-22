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

        public void Start() => _graph?.Start();
        public void Stop() => _graph?.Stop();

        private void OnQuantumStarted(AudioGraph sender, object args)
        {
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
