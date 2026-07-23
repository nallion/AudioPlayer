using System;
using System.Numerics;
using AudioVisualizerPlayer.Helpers;
using Windows.Media.Audio;
using Windows.Media.Render;
using Windows.Storage;

namespace AudioVisualizerPlayer.Services
{
    /// <summary>
    /// Анализ спектра — снова через СОБСТВЕННЫЙ независимый AudioGraph,
    /// читающий тот же файл ОТДЕЛЬНО от звука (звук теперь идёт через
    /// MediaPlayer, см. PlaybackService — там же подробно о причине возврата
    /// к этой архитектуре). Небольшой дрейф между звуком и визуализацией
    /// теоретически возможен при частых паузах — гасим его
    /// Seek-ресинхронизацией на каждый Start(position) (см. MainPage,
    /// вызывается из OnPlaybackStateChanged при каждом возобновлении).
    ///
    /// Оптимизации, наработанные во время диагностики щелчков в звуке
    /// (когда всё это ещё было частью общего AudioGraph), сохранены:
    /// безаллокационный итеративный FFT (Helpers/FFT.cs), переиспользуемые
    /// буферы под сэмплы кадра и под "срез" для FFT, расчёт через раз
    /// (не на каждом кванте), флаг IsPaused для экономии CPU в фоне —
    /// экономия CPU по-прежнему полезна, даже если она и не была причиной
    /// щелчков в звуке (тот источник оказался в самой AudioGraph-архитектуре
    /// воспроизведения, а не в визуализаторе).
    /// </summary>
    public class VisualizerService : IDisposable
    {
        private AudioGraph _graph;
        private AudioFileInputNode _fileInput;
        private AudioFrameOutputNode _frameOutput;

        private const int FftSize = 4096;
        private const int BarCount = 40;

        private readonly FFT _fft = new FFT(FftSize);

        public event EventHandler<float[]> LevelsChanged;

        /// <summary>Экономия CPU при блокировке экрана/сворачивании — см.
        /// комментарий у одноимённого свойства в предыдущей версии этого
        /// файла. Топология графа не меняется, просто пропускаем работу.</summary>
        public bool IsPaused { get; set; }

        /// <summary>
        /// Открывает файл для анализа в собственном, независимом AudioGraph
        /// (без узла вывода на устройство — этот граф никогда не звучит,
        /// только анализирует). Вызывать один раз на трек, до первого Start().
        /// </summary>
        public async System.Threading.Tasks.Task InitializeAsync(StorageFile file)
        {
            Diag.Log($"VisualizerService.InitializeAsync начат для {file.Name}");
            Dispose(); // на случай повторного использования экземпляра
            Diag.Log("VisualizerService.InitializeAsync: Dispose() старого — готово");

            var settings = new AudioGraphSettings(AudioRenderCategory.Media);
            Diag.Log("VisualizerService.InitializeAsync: перед AudioGraph.CreateAsync");
            var graphResult = await AudioGraph.CreateAsync(settings);
            Diag.Log($"VisualizerService.InitializeAsync: AudioGraph.CreateAsync status={graphResult.Status}");
            if (graphResult.Status != AudioGraphCreationStatus.Success)
                throw new InvalidOperationException("Не удалось создать AudioGraph визуализатора: " + graphResult.Status);

            _graph = graphResult.Graph;

            Diag.Log("VisualizerService.InitializeAsync: перед CreateFileInputNodeAsync");
            var fileInputResult = await _graph.CreateFileInputNodeAsync(file);
            Diag.Log($"VisualizerService.InitializeAsync: CreateFileInputNodeAsync status={fileInputResult.Status}");
            if (fileInputResult.Status != AudioFileNodeCreationStatus.Success)
                throw new InvalidOperationException("Не удалось открыть файл для анализа: " + fileInputResult.Status);

            _fileInput = fileInputResult.FileInputNode;
            _frameOutput = _graph.CreateFrameOutputNode();
            _fileInput.AddOutgoingConnection(_frameOutput); // единственное соединение — этот граф ни во что больше не пишет

            _graph.QuantumStarted += OnQuantumStarted;
            Diag.Log("VisualizerService.InitializeAsync: завершён успешно");
        }

        /// <summary>
        /// Запускает/возобновляет анализ. syncPosition — текущая позиция
        /// РЕАЛЬНОГО плеера (PlaybackService.Position) на момент возобновления,
        /// пересинхронизирует наш независимый граф, чтобы минимизировать
        /// дрейф, накопившийся за время паузы.
        /// </summary>
        public void Start(TimeSpan? syncPosition = null)
        {
            if (_graph == null) return;

            if (syncPosition.HasValue)
            {
                _fileInput.Seek(syncPosition.Value);
                _sampleBuffer.Clear(); // старые накопленные сэмплы уже не актуальны после скачка позиции
            }

            _graph.Start();
        }

        /// <summary>
        /// Перемотка без обязательного запуска графа — вызывается при
        /// перетаскивании ProgressSlider независимо от того, играет плеер
        /// сейчас или на паузе. Если граф уже запущен (играет) — продолжает
        /// играть с новой позиции. Если на паузе — просто выравнивает
        /// позицию заранее, чтобы к моменту Play() всё уже было готово.
        /// </summary>
        public void Seek(TimeSpan position)
        {
            if (_fileInput == null) return;
            _fileInput.Seek(position);
            _sampleBuffer.Clear(); // старые накопленные сэмплы уже не актуальны после скачка позиции
        }

        public void Stop()
        {
            _graph?.Stop();
        }

        // Накопительный буфер: один quantum обычно приносит гораздо меньше
        // FftSize сэмплов, поэтому копим их между вызовами QuantumStarted.
        private readonly System.Collections.Generic.List<float> _sampleBuffer = new System.Collections.Generic.List<float>(FftSize * 2);

        // Переиспользуемые буферы — без единой аллокации на реальном пути
        // обработки кванта (см. историю диагностики щелчков в звуке).
        private float[] _extractScratch = new float[0];
        private readonly float[] _chunkBuffer = new float[FftSize];

        private float _agcMax = 0.01f;
        private volatile bool _isProcessingFft = false;
        private bool _skipThisQuantum = false;

        private void OnQuantumStarted(AudioGraph sender, object args)
        {
            try
            {
                if (IsPaused) return;

                Windows.Media.AudioFrame frame = _frameOutput.GetFrame();
                int sampleCount = ExtractSamplesInto(frame, ref _extractScratch);
                if (sampleCount == 0) return;

                for (int i = 0; i < sampleCount; i++)
                    _sampleBuffer.Add(_extractScratch[i]);

                if (_sampleBuffer.Count > FftSize * 4)
                {
                    _sampleBuffer.RemoveRange(0, _sampleBuffer.Count - FftSize * 2);
                }

                if (_sampleBuffer.Count < FftSize) return;
                if (_isProcessingFft) return;

                _skipThisQuantum = !_skipThisQuantum;
                if (_skipThisQuantum) return;

                _sampleBuffer.CopyTo(_sampleBuffer.Count - FftSize, _chunkBuffer, 0, FftSize);

                _isProcessingFft = true;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        Complex[] spectrum = _fft.Transform(_chunkBuffer);
                        float[] bars = FFT.ToBars(spectrum, BarCount);
                        NormalizeWithAgc(bars);
                        LevelsChanged?.Invoke(this, bars);
                    }
                    catch
                    {
                        // Ошибка в расчёте — просто пропускаем этот кадр визуализации.
                    }
                    finally
                    {
                        _isProcessingFft = false;
                    }
                });
            }
            catch
            {
                // Callback независимого графа-анализатора — необработанное
                // исключение здесь не может повлиять на реальный звук
                // (он идёт через MediaPlayer, полностью отдельно).
            }
        }

        private void NormalizeWithAgc(float[] bars)
        {
            float frameMax = 0f;
            for (int i = 0; i < bars.Length; i++)
                if (bars[i] > frameMax) frameMax = bars[i];

            _agcMax = Math.Max(frameMax, _agcMax * 0.98f);
            if (_agcMax < 0.0001f) _agcMax = 0.0001f;

            for (int i = 0; i < bars.Length; i++)
                bars[i] = Math.Min(1.0f, bars[i] / _agcMax);
        }

        private unsafe int ExtractSamplesInto(Windows.Media.AudioFrame frame, ref float[] scratch)
        {
            using (var buffer = frame.LockBuffer(Windows.Media.AudioBufferAccessMode.Read))
            using (var reference = buffer.CreateReference())
            {
                ((IMemoryBufferByteAccess)reference).GetBuffer(out byte* dataInBytes, out uint capacityInBytes);
                float* dataInFloat = (float*)dataInBytes;
                uint capacityInFloats = capacityInBytes / sizeof(float);

                if (scratch.Length < capacityInFloats)
                {
                    scratch = new float[capacityInFloats];
                }

                for (uint i = 0; i < capacityInFloats; i++)
                    scratch[i] = dataInFloat[i];

                return (int)capacityInFloats;
            }
        }

        public void Dispose()
        {
            Diag.Log($"VisualizerService.Dispose: начало, _graph == null: {_graph == null}");
            try
            {
                if (_graph != null)
                {
                    _graph.QuantumStarted -= OnQuantumStarted;
                    Diag.Log("VisualizerService.Dispose: после отписки QuantumStarted");

                    // ВАЖНО: граф нужно ОСТАНОВИТЬ до того, как начать
                    // освобождать его узлы — освобождение AudioFrameOutputNode
                    // на ещё активно работающем графе (реально гоняющем кванты)
                    // давало настоящий deadlock на этом устройстве. Раньше
                    // Stop() стоял почти в самом конце, после Dispose() узлов —
                    // именно поэтому и зависало.
                    _graph.Stop();
                    Diag.Log("VisualizerService.Dispose: после _graph.Stop()");
                }
                _frameOutput?.Dispose();
                Diag.Log("VisualizerService.Dispose: после _frameOutput.Dispose()");
                _fileInput?.Dispose();
                Diag.Log("VisualizerService.Dispose: после _fileInput.Dispose()");
                _graph?.Dispose();
                Diag.Log("VisualizerService.Dispose: после _graph.Dispose() — готово");
            }
            catch (ObjectDisposedException)
            {
                // Уже освобождено — ничего страшного.
            }
            finally
            {
                _frameOutput = null;
                _fileInput = null;
                _graph = null;
            }
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
