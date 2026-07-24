using System;
using System.Numerics;
using AudioVisualizerPlayer.Helpers;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.Render;

namespace AudioVisualizerPlayer.Services
{
    /// <summary>
    /// Анализ спектра через LOOPBACK-захват — слушаем то, что РЕАЛЬНО играет
    /// на выходе устройства (тот же самый сигнал, что слышит пользователь),
    /// вместо того чтобы читать файл заново отдельным независимым декодером.
    ///
    /// Это устраняет саму причину всех предыдущих проблем с рассинхронизацией
    /// (два независимых "часов" — звук и визуализация): паузы, перемотки,
    /// зацикливание, конец трека, тишина — всё автоматически отражается
    /// правильно, без единого явного сигнала синхронизации с нашей стороны,
    /// потому что это буквально один и тот же поток данных, а не два разных.
    ///
    /// ТЕХНИЧЕСКИЙ ПРИЁМ: CreateDeviceInputNodeAsync обычно создаёт узел
    /// записи С МИКРОФОНА. Но если передать в него DeviceInformation,
    /// полученный через MediaDevice.GetAudioRenderSelector() (обычно
    /// используется для устройств ВЫВОДА, не ввода), система интерпретирует
    /// это как запрос на loopback-захват — то, что уходит НА это устройство,
    /// а не то, что приходит С него. Задокументированный (хоть и не самый
    /// очевидный) приём — см. официальный пример Microsoft "Audio graphs" +
    /// независимое подтверждение на форуме разработчиков.
    ///
    /// БОЛЬШЕ НЕ ПРИВЯЗАН К КОНКРЕТНОМУ ФАЙЛУ/ТРЕКУ — создаётся ОДИН РАЗ за
    /// всё время работы приложения (см. MainPage.MainPage_Loaded), не
    /// пересоздаётся на каждый трек и не нуждается в пересинхронизации.
    /// Никакого Seek(), никакого "времени жизни декодирующей сессии" — этих
    /// проблем просто не существует в данной архитектуре, потому что мы
    /// больше не декодируем сжатый файл сами вообще.
    /// </summary>
    public class VisualizerService : IDisposable
    {
        private AudioGraph _graph;
        private AudioDeviceInputNode _loopbackInput;
        private AudioFrameOutputNode _frameOutput;

        private const int FftSize = 4096;
        private const int BarCount = 40;

        private readonly FFT _fft = new FFT(FftSize);

        public event EventHandler<float[]> LevelsChanged;

        /// <summary>Экономия CPU при блокировке экрана/сворачивании — топология
        /// графа не меняется, просто пропускаем обработку кадров.</summary>
        public bool IsPaused { get; set; }

        /// <summary>
        /// Создаёт loopback-граф. Вызывается ОДИН РАЗ за время жизни
        /// приложения (не на каждый трек) — сам граф не привязан к тому,
        /// что именно играет, просто слушает реальный звуковой выход.
        /// </summary>
        public async System.Threading.Tasks.Task InitializeLoopbackAsync()
        {
            Diag.Log("VisualizerService.InitializeLoopbackAsync: начало");
            Dispose(); // на случай повторного использования экземпляра

            var settings = new AudioGraphSettings(AudioRenderCategory.Media);
            Diag.Log("InitializeLoopbackAsync: перед AudioGraph.CreateAsync");
            var graphResult = await AudioGraph.CreateAsync(settings);
            Diag.Log($"InitializeLoopbackAsync: AudioGraph.CreateAsync status={graphResult.Status}");
            if (graphResult.Status != AudioGraphCreationStatus.Success)
                throw new InvalidOperationException("Не удалось создать AudioGraph для loopback: " + graphResult.Status);

            _graph = graphResult.Graph;

            // Ключевой момент: берём устройство из СЕЛЕКТОРА УСТРОЙСТВ
            // ВЫВОДА (не ввода!) — именно это заставляет систему выполнить
            // loopback-захват вместо записи с микрофона.
            Diag.Log("InitializeLoopbackAsync: перед DeviceInformation.FindAllAsync(GetAudioRenderSelector)");
            var renderDevices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
            Diag.Log($"InitializeLoopbackAsync: найдено устройств вывода = {renderDevices.Count}");
            foreach (var d in renderDevices)
            {
                Diag.Log($"  устройство: Name='{d.Name}', Id='{d.Id}'");
            }
            if (renderDevices.Count == 0)
                throw new InvalidOperationException("Не найдено ни одного устройства вывода для loopback-захвата.");

            var loopbackDevice = renderDevices[0]; // системное устройство по умолчанию — первое в списке
            Diag.Log($"InitializeLoopbackAsync: выбрано устройство '{loopbackDevice.Name}', перед CreateDeviceInputNodeAsync");

            var inputResult = await _graph.CreateDeviceInputNodeAsync(MediaCategory.Media, _graph.EncodingProperties, loopbackDevice);
            Diag.Log($"InitializeLoopbackAsync: CreateDeviceInputNodeAsync status={inputResult.Status}");
            if (inputResult.Status != AudioDeviceNodeCreationStatus.Success)
                throw new InvalidOperationException("Не удалось создать loopback-узел: " + inputResult.Status);

            _loopbackInput = inputResult.DeviceInputNode;
            _frameOutput = _graph.CreateFrameOutputNode();
            _loopbackInput.AddOutgoingConnection(_frameOutput);
            Diag.Log("InitializeLoopbackAsync: узлы соединены");

            _graph.QuantumStarted += OnQuantumStarted;

            // Loopback-граф просто всегда работает — не привязан к play/pause
            // конкретного трека (если реальный звук молчит, мы честно
            // услышим тишину — и AGC сама естественно опустит бары к нулю,
            // без необходимости явно останавливать/запускать граф на каждую
            // паузу). Управление Start()/Stop() ниже остаётся только ради
            // экономии CPU в фоне/на паузе, не ради корректности.
            _graph.Start();
            Diag.Log("InitializeLoopbackAsync: _graph.Start() вызван — завершено успешно");
        }

        public void Start()
        {
            _graph?.Start();
        }

        public void Stop()
        {
            _graph?.Stop();
        }

        // Накопительный буфер: один quantum обычно приносит гораздо меньше
        // FftSize сэмплов, поэтому копим их между вызовами QuantumStarted.
        private readonly System.Collections.Generic.List<float> _sampleBuffer = new System.Collections.Generic.List<float>(FftSize * 2);

        // Переиспользуемые буферы — без единой аллокации на реальном пути
        // обработки кванта.
        private float[] _extractScratch = new float[0];
        private readonly float[] _chunkBuffer = new float[FftSize];

        private float _agcMax = 0.01f;
        private volatile bool _isProcessingFft = false;
        private bool _skipThisQuantum = false;

        private int _quantumCallCount = 0;

        private void OnQuantumStarted(AudioGraph sender, object args)
        {
            try
            {
                if (IsPaused) return;

                _quantumCallCount++;
                if (_quantumCallCount % 50 == 0)
                {
                    Diag.Log($"VisualizerService.OnQuantumStarted: heartbeat, вызовов={_quantumCallCount}");
                }

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
                    catch (Exception exInner)
                    {
                        Diag.Log("OnQuantumStarted (фон, расчёт FFT): ОШИБКА: " + exInner);
                    }
                    finally
                    {
                        _isProcessingFft = false;
                    }
                });
            }
            catch (Exception ex)
            {
                Diag.Log("OnQuantumStarted: ОШИБКА: " + ex);
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
            try
            {
                if (_graph != null)
                {
                    _graph.QuantumStarted -= OnQuantumStarted;
                }
                _frameOutput?.Dispose();
                _loopbackInput?.Dispose();
                _graph?.Stop();
                _graph?.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Уже освобождено — ничего страшного.
            }
            finally
            {
                _frameOutput = null;
                _loopbackInput = null;
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
