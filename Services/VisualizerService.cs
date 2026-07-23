using System;
using System.Numerics;
using AudioVisualizerPlayer.Helpers;
using Windows.Media.Audio;

namespace AudioVisualizerPlayer.Services
{
    /// <summary>
    /// Анализ спектра. Раньше открывал файл в СВОЁМ отдельном AudioGraph,
    /// независимом от PlaybackService — это давало дрейф по времени со звуком.
    /// Теперь просто подключается вторым исходящим соединением к ТОМУ ЖЕ
    /// AudioFileInputNode, что и реальный звук (см. PlaybackService.LoadAsync).
    /// Ни своего графа, ни своего Start()/Stop() больше нет — Start/Stop всего
    /// потока (и звука, и анализа одновременно) управляется исключительно
    /// через PlaybackService.Play()/Pause(), это гарантирует идеальную
    /// синхронизацию без какой-либо ресинхронизации/Seek-костылей.
    /// </summary>
    public class VisualizerService : IDisposable
    {
        private AudioGraph _graph;
        private AudioFrameOutputNode _frameOutput;

        // 4096 вместо 1024: даёт 2048 полезных бинов вместо 512. При 1024
        // на самом низкочастотном конце логарифмической шкалы несколько
        // соседних полос из-за округления индекса попадали в один и тот же
        // bin — отсюда были "блоки" одинаковой высоты на низких частотах
        // (по 4-5 баров подряд буквально показывали одну и ту же частоту).
        // Компромисс: на типичной частоте дискретизации 44100Гц 4096 сэмплов
        // это ~93мс на одно окно FFT — чуть меньше временнóй чёткости, чем
        // было при ~23мс, но всё ещё вполне отзывчиво для визуализатора,
        // и разница в частотном разрешении того стоит.
        private const int FftSize = 4096; // степень двойки
        private const int BarCount = 40;   // столько же баров, сколько на макете

        // Переиспользуемый экземпляр FFT — все внутренние буферы (сам массив
        // расчёта, таблица bit-reversal, окно Ханна) выделяются один раз
        // здесь, а не заново на каждый вызов Transform(). См. Helpers/FFT.cs —
        // именно это убирает основной источник мелких аллокаций/GC-пауз,
        // которые могли провоцировать щелчки в звуке.
        private readonly FFT _fft = new FFT(FftSize);

        public event EventHandler<float[]> LevelsChanged;

        private static int _attachToSuccessCount = 0;

        /// <summary>
        /// Подключается к уже загруженному треку в PlaybackService — второе
        /// исходящее соединение от того же AudioFileInputNode, что играет звук.
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
                // Явно запрашиваем тот же формат, что согласовал весь граф
                // (_graph.EncodingProperties), а не дефолтный формат
                // CreateFrameOutputNode() без параметров.
                _frameOutput = _graph.CreateFrameOutputNode(_graph.EncodingProperties);
                Diag.Log("  CreateFrameOutputNode(с форматом графа) — успех");
            }
            catch (Exception ex)
            {
                Diag.Log("  CreateFrameOutputNode — ОШИБКА: " + ex);
                throw new Exception($"AttachTo ШАГ A (CreateFrameOutputNode), успешных AttachTo до этого за сессию: {_attachToSuccessCount}: " + ex.Message, ex);
            }

            // По логам видно: одно и то же соединение Submix -> FrameOutput
            // иногда падает, иногда сразу срабатывает для ОДНОГО И ТОГО ЖЕ
            // файла/условий — то есть это гонка по времени (драйверу нужен
            // короткий момент "устаканиться" после первого соединения
            // Submix -> DeviceOutput), а не жёсткая несовместимость форматов.
            // Лечим повтором с паузой, как раньше делали для DeviceOutput.
            Exception lastError = null;
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                try
                {
                    // От Submix, а не от FileInput напрямую — см. комментарий
                    // в PlaybackService про XAUDIO2_E_INVALID_CALL.
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
            // Защита от ObjectDisposedException: граф мог быть уже освобождён
            // снаружи (например, PlaybackService.LoadAsync сносит старый граф
            // при загрузке нового трека) — тогда отписка/Dispose здесь просто
            // не нужны, а не должны валить приложение с исключением.
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

        // Накопительный буфер: один quantum обычно приносит гораздо меньше
        // FftSize сэмплов (это ~10мс аудио), поэтому копим их между вызовами
        // QuantumStarted, а не ждём все 1024 разом за один callback.
        private readonly System.Collections.Generic.List<float> _sampleBuffer = new System.Collections.Generic.List<float>(FftSize * 2);

        // Переиспользуемый буфер под ExtractSamples — раньше там был
        // "new float[]" НА КАЖДОМ кванте без исключения (даже когда мы
        // пропускаем расчёт FFT через раз) — это постоянная, непрекращающаяся
        // аллокация прямо на real-time аудио-потоке. Здесь буфер выделяется
        // один раз и лишь изредка увеличивается (только если реальный размер
        // кванта вдруг окажется больше, чем был раньше — на практике это
        // происходит один раз, при самом первом кванте, дальше размер
        // стабилен).
        private float[] _extractScratch = new float[0];

        // Переиспользуемый буфер под "срез" для FFT — раньше "new float[FftSize]"
        // (16КБ) выделялся на real-time аудио-потоке при каждом расчёте.
        // Копируем сюда, а не создаём заново — безопасно, потому что
        // _isProcessingFft гарантирует: пока фоновая задача читает этот
        // буфер, аудио-поток сюда больше не пишет (следующая попытка просто
        // выходит раньше по этому флагу).
        private readonly float[] _chunkBuffer = new float[FftSize];

        // AGC: скользящий максимум амплитуды с медленным затуханием — вместо
        // жёсткого фиксированного коэффициента усиления, который заставлял
        // большинство басовых/средних частот упираться в потолок одновременно.
        private float _agcMax = 0.01f; // не 0 — избегаем деления на ноль в тихом начале трека

        // Пока идёт расчёт предыдущего кадра на фоновом потоке — новый не
        // запускаем, просто пропускаем кванты. Не даёт очереди из Task.Run
        // расти бесконечно, если FFT вдруг не успевает уложиться в темп
        // поступления квантов (лучше пропустить кадр визуализации, чем
        // копить фоновую работу).
        private volatile bool _isProcessingFft = false;

        // Дополнительно считаем FFT не на КАЖДОМ кванте, а через один —
        // глаз всё равно не различит разницу между обновлением каждые ~93мс
        // и каждые ~186мс, а частота аллокаций (Task-объект + всё, что
        // происходит внутри) и нагрузка на планировщик пула потоков падают
        // вдвое. Ещё один вклад в снижение GC-давления, помимо
        // безаллокационного FFT самого по себе.
        private bool _skipThisQuantum = false;

        private void OnQuantumStarted(AudioGraph sender, object args)
        {
            try
            {
                // ВАЖНО: этот метод вызывается на real-time аудио-потоке того же
                // самого AudioGraph, что рендерит настоящий звук в динамики
                // (раньше, при двух независимых графах, это было безопасно —
                // тяжёлые вычисления на графе-анализаторе не могли повлиять на
                // звук; после объединения в один граф это уже не так). Здесь
                // должны быть только ДЁШЕВЫЕ операции — копирование сэмплов,
                // БЕЗ единой аллокации. Сам FFT и вся математика уходят в
                // Task.Run ниже, на поток из пула потоков, чтобы не
                // задерживать рендеринг звука.
                Windows.Media.AudioFrame frame = _frameOutput.GetFrame();
                int sampleCount = ExtractSamplesInto(frame, ref _extractScratch);
                if (sampleCount == 0) return;

                for (int i = 0; i < sampleCount; i++)
                    _sampleBuffer.Add(_extractScratch[i]);

                // Не даём буферу расти бесконечно, если по какой-то причине
                // накопление опережает потребление.
                if (_sampleBuffer.Count > FftSize * 4)
                {
                    _sampleBuffer.RemoveRange(0, _sampleBuffer.Count - FftSize * 2);
                }

                if (_sampleBuffer.Count < FftSize) return;
                if (_isProcessingFft) return; // предыдущий расчёт ещё не закончился

                _skipThisQuantum = !_skipThisQuantum;
                if (_skipThisQuantum) return; // пропускаем через раз

                // Берём последние FftSize сэмплов из накопленного буфера — в
                // переиспользуемый _chunkBuffer, а не в новый массив. Безопасно:
                // пока _isProcessingFft == true, сюда больше никто не пишет
                // (см. комментарий у поля выше).
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
                // OnQuantumStarted вызывается из нативного audio-callback потока —
                // необработанное исключение здесь тихо проглатывается самим
                // AudioGraph. Пропускаем один кадр — на следующем обычно всё ок.
            }
        }

        private void NormalizeWithAgc(float[] bars)
        {
            float frameMax = 0f;
            for (int i = 0; i < bars.Length; i++)
                if (bars[i] > frameMax) frameMax = bars[i];

            // Затухание 0.98 за квант — максимум "плывёт" вниз на тихих участках,
            // но не мгновенно, чтобы не дёргаться между отдельными нотами.
            _agcMax = Math.Max(frameMax, _agcMax * 0.98f);
            if (_agcMax < 0.0001f) _agcMax = 0.0001f;

            for (int i = 0; i < bars.Length; i++)
                bars[i] = Math.Min(1.0f, bars[i] / _agcMax);
        }

        /// <summary>
        /// Копирует сэмплы кадра в scratch — переиспользуемый буфер вместо
        /// нового массива на каждый вызов. scratch увеличивается (пересоздаётся)
        /// только если реальный размер кванта оказался больше, чем помещается
        /// в текущий буфер — на практике это происходит один раз, на самом
        /// первом кванте, дальше размер стабилен и переаллокаций больше нет.
        /// Возвращает реальное количество сэмплов в этом кадре (может быть
        /// МЕНЬШЕ длины scratch, если буфер больше не пересоздавался после
        /// более крупного предыдущего кванта — вызывающий код должен читать
        /// ровно столько элементов, сколько вернула эта функция, не scratch.Length).
        /// </summary>
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
                    // Редкий путь — не на каждый квант, только когда реальный
                    // размер кванта вырос за пределы текущего буфера.
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

    // Нужно для доступа к сырым байтам AudioFrame — стандартный COM-интероп UWP
    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
