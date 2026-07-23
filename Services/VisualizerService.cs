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
            WriteDiagnostics("InitializeAsync начат. Файл: " + file.Name);

            var settings = new AudioGraphSettings(AudioRenderCategory.Media)
            {
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency
            };

            var graphResult = await AudioGraph.CreateAsync(settings);
            if (graphResult.Status != AudioGraphCreationStatus.Success)
            {
                WriteDiagnostics("AudioGraph.CreateAsync провалился: " + graphResult.Status);
                throw new InvalidOperationException("Не удалось создать AudioGraph: " + graphResult.Status);
            }
            WriteDiagnostics("AudioGraph создан успешно.");

            _graph = graphResult.Graph;

            var fileInputResult = await _graph.CreateFileInputNodeAsync(file);
            if (fileInputResult.Status != AudioFileNodeCreationStatus.Success)
            {
                WriteDiagnostics("CreateFileInputNodeAsync провалился: " + fileInputResult.Status);
                throw new InvalidOperationException("Не удалось открыть файл для анализа: " + fileInputResult.Status);
            }
            WriteDiagnostics("AudioFileInputNode создан успешно. Duration: " + fileInputResult.FileInputNode.Duration);

            _fileInput = fileInputResult.FileInputNode;

            // Важно: НЕ подключаем этот граф к звуковому устройству (DeviceOutputNode) —
            // звук уже играет через MediaPlayer. Этот граф существует только для анализа,
            // поэтому выход — FrameOutputNode, а не колонки.
            _frameOutput = _graph.CreateFrameOutputNode();
            _fileInput.AddOutgoingConnection(_frameOutput);

            _graph.QuantumStarted += OnQuantumStarted;
            WriteDiagnostics("InitializeAsync завершён успешно, QuantumStarted подписан.");
        }

        public void Start()
        {
            // Пишем синхронно, прямо здесь — раньше запись была через
            // System.Threading.Timer (колбэк на потоке из thread pool), и даже
            // диагностический файл не появился ни разу. Возможно, запись файлов
            // именно с такого потока в этом приложении ведёт себя иначе, чем
            // с UI-потока (где crash.log записывался нормально). Start() вызывается
            // из LoadDemoTrackAsync на UI-потоке — пишем прямо тут, синхронно,
            // без промежуточных потоков.
            WriteDiagnostics("Start() вызван. _graph == null: " + (_graph == null));

            try
            {
                _graph?.Start();
                WriteDiagnostics("_graph.Start() выполнен без исключений.");
            }
            catch (Exception ex)
            {
                WriteDiagnostics("_graph.Start() бросил исключение: " + ex);
            }
        }

        public void Stop() => _graph?.Stop();

        private static int _quantumCount = 0;
        private static bool _firstQuantumLogged = false;

        private static void WriteDiagnostics(string text)
        {
            try
            {
                var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var path = System.IO.Path.Combine(folder.Path, "visualizer_diagnostics.log");
                // Дописываем, а не перезаписываем — так видно всю последовательность событий.
                System.IO.File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text + "\n");
            }
            catch
            {
                // Если и это не работает — значит, дело не в потоке, а в чём-то ещё;
                // тогда единственный способ узнать причину — RDP/полный дамп.
            }
        }

        // Накопительный буфер: один quantum обычно приносит гораздо меньше
        // FftSize сэмплов (это ~10мс аудио), поэтому копим их между вызовами
        // QuantumStarted, а не ждём все 1024 разом за один callback — раньше
        // именно из-за этого метод всегда делал ранний return и до FFT/Invoke
        // дело не доходило вообще ни разу.
        private readonly System.Collections.Generic.List<float> _sampleBuffer = new System.Collections.Generic.List<float>(FftSize * 2);
        private static bool _sampleSizeLogged = false;

        private void OnQuantumStarted(AudioGraph sender, object args)
        {
            if (!_firstQuantumLogged)
            {
                _firstQuantumLogged = true;
                WriteDiagnostics("QuantumStarted сработал впервые.");
            }

            try
            {
                System.Threading.Interlocked.Increment(ref _quantumCount);

                Windows.Media.AudioFrame frame = _frameOutput.GetFrame();
                float[] samples = ExtractSamples(frame);

                if (!_sampleSizeLogged && samples != null)
                {
                    _sampleSizeLogged = true;
                    WriteDiagnostics($"Размер одного quantum: {samples.Length} сэмплов (FftSize нужен {FftSize}).");
                }

                if (samples == null || samples.Length == 0) return;

                _sampleBuffer.AddRange(samples);

                // Не даём буферу расти бесконечно, если по какой-то причине
                // накопление опережает потребление.
                if (_sampleBuffer.Count > FftSize * 4)
                {
                    _sampleBuffer.RemoveRange(0, _sampleBuffer.Count - FftSize * 2);
                }

                if (_sampleBuffer.Count < FftSize) return;

                // Берём последние FftSize сэмплов из накопленного буфера
                var chunk = new float[FftSize];
                _sampleBuffer.CopyTo(_sampleBuffer.Count - FftSize, chunk, 0, FftSize);

                Complex[] spectrum = FFT.Transform(chunk);
                float[] bars = FFT.ToBars(spectrum, BarCount);

                if (_quantumCount <= 5)
                {
                    var handler = LevelsChanged;
                    WriteDiagnostics($"Перед Invoke #{_quantumCount}: LevelsChanged == null: {handler == null}, " +
                        $"bars[0]={(bars.Length > 0 ? bars[0].ToString() : "N/A")}, bars.Length={bars.Length}");
                }

                LevelsChanged?.Invoke(this, bars);
            }
            catch (Exception ex)
            {
                // OnQuantumStarted вызывается из нативного audio-callback потока —
                // необработанное исключение здесь может тихо проглатываться самим
                // AudioGraph, никак не долетая ни до try/catch в UI-коде, ни до
                // Application.UnhandledException.
                if (_quantumCount <= 5) // логируем первые несколько, не заваливая лог
                {
                    WriteDiagnostics("OnQuantumStarted бросил исключение: " + ex);
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
