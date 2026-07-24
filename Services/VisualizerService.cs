using System;
using System.Numerics;
using AudioVisualizerPlayer.Helpers;
using Windows.Media.Audio;

namespace AudioVisualizerPlayer.Services
{
    /// <summary>
    /// Анализ спектра — подключается вторым исходящим соединением к ТОМУ ЖЕ
    /// AudioFileInputNode через Submix, что и реальный звук (см.
    /// PlaybackService.LoadAsync). Один и тот же поток данных для звука и
    /// визуализации — идеальная синхронизация по построению, дрейфа не
    /// может быть в принципе, никакой ручной пересинхронизации не нужно.
    ///
    /// Оптимизации, наработанные ранее (когда щёлкал звук), сохранены:
    /// безаллокационный итеративный FFT (Helpers/FFT.cs), переиспользуемые
    /// буферы, расчёт через раз, флаг IsPaused для экономии CPU в фоне.
    /// </summary>
    public class VisualizerService : IDisposable
    {
        private AudioGraph _graph;
        private AudioFrameOutputNode _frameOutput;

        private const int FftSize = 4096;
        private const int BarCount = 40;

        private readonly FFT _fft = new FFT(FftSize);

        public event EventHandler<float[]> LevelsChanged;

        public bool IsPaused { get; set; }

        private static int _attachToSuccessCount = 0;

        /// <summary>
        /// Подключается к уже загруженному треку в PlaybackService — второе
        /// исходящее соединение от Submix, что и реальный звук.
        /// </summary>
        public async System.Threading.Tasks.Task AttachToAsync(PlaybackService playback)
        {
            Diag.Log("VisualizerService.AttachToAsync вызван");
            Detach();

            _graph = playback.Graph;
            if (_graph == null || playback.Submix == null)
                throw new InvalidOperationException("PlaybackService ещё не загрузил трек — AttachTo нужно вызывать после LoadAsync.");

            try
            {
                _frameOutput = _graph.CreateFrameOutputNode(_graph.EncodingProperties);
                Diag.Log("  CreateFrameOutputNode(с форматом графа) — успех");
            }
            catch (Exception ex)
            {
                Diag.Log("  CreateFrameOutputNode — ОШИБКА: " + ex);
                throw new Exception($"AttachTo ШАГ A (CreateFrameOutputNode), успешных AttachTo до этого за сессию: {_attachToSuccessCount}: " + ex.Message, ex);
            }

            Exception lastError = null;
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                try
                {
                    playback.Submix.AddOutgoingConnection(_frameOutput);
                    Diag.Log($"  Submix -> FrameOutput подключено — успех (попытка {attempt})");
                    lastError = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Diag.Log($"  Submix -> FrameOutput — ОШИБКА (попытка {attempt}): {ex}");
                    if (attempt < 4)
                    {
                        await System.Threading.Tasks.Task.Delay(150);
                    }
                }
            }

            if (lastError != null)
            {
                throw new Exception($"AttachTo ШАГ B (AddOutgoingConnection) после 4 попыток, успешных AttachTo до этого за сессию: {_attachToSuccessCount}: " + lastError.Message, lastError);
            }

            _graph.QuantumStarted += OnQuantumStarted;
            _attachToSuccessCount++;
            Diag.Log($"AttachTo завершён успешно (успешных за сессию: {_attachToSuccessCount})");
        }

        private void Detach()
        {
            try
            {
                if (_graph != null)
                {
                    _graph.QuantumStarted -= OnQuantumStarted;
                }
                _frameOutput?.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Граф/узел уже уничтожены снаружи — нам тут больше нечего освобождать.
            }
            finally
            {
                _frameOutput = null;
                _graph = null;
            }
        }

        private readonly System.Collections.Generic.List<float> _sampleBuffer = new System.Collections.Generic.List<float>(FftSize * 2);
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
                    Diag.Log($"OnQuantumStarted: heartbeat, вызовов={_quantumCallCount}");
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
            Detach();
        }
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
