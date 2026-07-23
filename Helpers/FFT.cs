using System;
using System.Numerics;

namespace AudioVisualizerPlayer.Helpers
{
    /// <summary>
    /// Итеративная (не рекурсивная) Cooley-Tukey FFT радикс-2 — bit-reversal
    /// permutation + butterfly-проходы прямо в заранее выделенном буфере.
    ///
    /// Раньше рекурсивная реализация создавала новые массивы (new Complex[])
    /// на КАЖДОМ уровне рекурсии (12 уровней при FftSize=4096), да ещё и
    /// новый Complex[n] на каждый вызов Transform() — при вызове раз в ~93мс,
    /// пока играет музыка, это постоянный поток мелких аллокаций. На слабом
    /// ARM-процессоре это могло провоцировать паузы сборщика мусора (GC),
    /// которые в свою очередь — щелчки/пропуски звука (даже несмотря на то,
    /// что сам расчёт FFT уже вынесен с реального аудио-потока в фоновый
    /// поток — GC-пауза затрагивает процесс целиком, а не один поток).
    ///
    /// Эта версия — класс с состоянием: все буферы (сам массив под FFT,
    /// таблица bit-reversal, окно Ханна) выделяются ОДИН РАЗ в конструкторе
    /// и переиспользуются при каждом вызове Transform(). Внутри самого
    /// расчёта — ноль аллокаций.
    /// </summary>
    public class FFT
    {
        private readonly int _n;
        private readonly Complex[] _buffer;
        private readonly int[] _bitReverse;
        private readonly double[] _window;

        public FFT(int n)
        {
            if ((n & (n - 1)) != 0)
                throw new ArgumentException("Длина буфера должна быть степенью двойки");

            _n = n;
            _buffer = new Complex[n];
            _bitReverse = BuildBitReverseTable(n);
            _window = BuildWindow(n);
        }

        private static int[] BuildBitReverseTable(int n)
        {
            int bits = (int)Math.Log(n, 2);
            var table = new int[n];
            for (int i = 0; i < n; i++)
            {
                int rev = 0;
                int x = i;
                for (int b = 0; b < bits; b++)
                {
                    rev = (rev << 1) | (x & 1);
                    x >>= 1;
                }
                table[i] = rev;
            }
            return table;
        }

        // Оконная функция Ханна — уменьшает "растекание" спектра между бинами
        private static double[] BuildWindow(int n)
        {
            var w = new double[n];
            for (int i = 0; i < n; i++)
                w[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));
            return w;
        }

        /// <summary>
        /// Считает FFT для samples (длина должна совпадать с n из конструктора)
        /// прямо во внутреннем переиспользуемом буфере — ни одной аллокации
        /// внутри самого расчёта. ВАЖНО: возвращаемый Complex[] — это тот же
        /// самый переиспользуемый массив, не копия. Используйте значения
        /// сразу же (как и раньше делает VisualizerService — сразу передаёт
        /// в ToBars в том же вызове), не сохраняйте ссылку между вызовами
        /// Transform() — при следующем вызове содержимое перезапишется.
        /// </summary>
        public Complex[] Transform(float[] samples)
        {
            // Bit-reversal permutation + применение окна — сразу раскладываем
            // сэмплы в переставленном порядке, отдельного шага перестановки
            // после не нужно.
            for (int i = 0; i < _n; i++)
            {
                _buffer[_bitReverse[i]] = new Complex(samples[i] * _window[i], 0);
            }

            // Итеративные butterfly-проходы — снизу вверх, полностью in-place.
            for (int size = 2; size <= _n; size <<= 1)
            {
                int halfSize = size >> 1;
                double angleStep = -2 * Math.PI / size;

                for (int start = 0; start < _n; start += size)
                {
                    for (int k = 0; k < halfSize; k++)
                    {
                        Complex twiddle = Complex.FromPolarCoordinates(1.0, angleStep * k);
                        Complex even = _buffer[start + k];
                        Complex odd = _buffer[start + k + halfSize] * twiddle;
                        _buffer[start + k] = even + odd;
                        _buffer[start + k + halfSize] = even - odd;
                    }
                }
            }

            return _buffer;
        }

        /// <summary>
        /// Группирует спектр в barCount полос по логарифмической шкале
        /// и возвращает СЫРЫЕ (без AGC) амплитуды — адаптивную нормализацию
        /// делает VisualizerService, у которого есть состояние между кадрами.
        ///
        /// Внутри полосы берём MAX, а не среднее: высокочастотный контент
        /// в музыке чаще всего короткие всплески (тарелки, хай-хэт), а не
        /// постоянный гул — на широких высокочастотных полосах (там много
        /// бинов на полосу) среднее просто размывало яркий всплеск среди
        /// тишины рядом почти до нуля.
        ///
        /// Плюс частотная коррекция (compensationGain): у большинства
        /// реальной музыки энергия естественным образом спадает к высоким
        /// частотам — без компенсации верхние полосы визуально почти всегда
        /// оставались бы тусклыми по сравнению с басом, даже если MAX уже
        /// ловит транзиенты. Коэффициент растёт линейно от 1.0 (самый бас)
        /// до 4.0 (самые высокие частоты) — подобрано на глаз, крути смело,
        /// если захочется другой баланс.
        ///
        /// Возвращаемый float[] — НОВЫЙ массив на каждый вызов (не буфер
        /// класса): результат читает UI-поток асинхронно чуть позже (через
        /// Dispatcher.RunAsync), и если бы мы переиспользовали один и тот же
        /// буфер, следующий кадр мог бы перезаписать значения ДО того, как
        /// UI успеет их прочитать — гонка данных. barCount маленький (40),
        /// это дешёвая аллокация, в отличие от внутренних буферов FFT выше.
        /// </summary>
        public static float[] ToBars(Complex[] spectrum, int barCount)
        {
            // Обрезаем верхние ~10% спектра — зона рядом с частотой Найквиста
            // ненадёжна (утечка спектра, шум кодирования, погрешности нашей
            // простой FFT-реализации), и в сочетании с MAX + самым большим
            // коэффициентом усиления там раньше вылезал изолированный "взрыв"
            // на самом последнем баре, никак не связанный с остальной кривой.
            // Эти частоты и так на грани слышимости, ничего не теряем.
            int usableBins = (int)(spectrum.Length / 2 * 0.9);
            var bars = new float[barCount];

            double logMin = Math.Log(1);
            double logMax = Math.Log(usableBins);

            for (int b = 0; b < barCount; b++)
            {
                double loEdge = Math.Exp(logMin + (logMax - logMin) * b / barCount);
                double hiEdge = Math.Exp(logMin + (logMax - logMin) * (b + 1) / barCount);

                int lo = Math.Max(1, (int)loEdge);
                int hi = Math.Min(usableBins, Math.Max(lo + 1, (int)hiEdge));

                double max = 0;
                for (int i = lo; i < hi; i++)
                    if (spectrum[i].Magnitude > max) max = spectrum[i].Magnitude;

                double compensationGain = 1.0 + 3.0 * b / (barCount - 1);
                bars[b] = (float)(max * compensationGain);
            }

            return bars;
        }
    }
}
