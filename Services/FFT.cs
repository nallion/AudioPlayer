using System;
using System.Numerics;

namespace AudioVisualizerPlayer.Helpers
{
    /// <summary>
    /// Простая реализация Cooley-Tukey FFT (radix-2, вход должен быть степенью двойки)
    /// + группировка результата в N логарифмических полос — то, что нужно
    /// для баров визуализатора (низкие частоты уже, высокие — шире).
    /// </summary>
    public static class FFT
    {
        public static Complex[] Transform(float[] samples)
        {
            int n = samples.Length;
            if ((n & (n - 1)) != 0)
                throw new ArgumentException("Длина буфера должна быть степенью двойки");

            var buffer = new Complex[n];
            for (int i = 0; i < n; i++)
                buffer[i] = new Complex(samples[i] * Window(i, n), 0);

            Recurse(buffer);
            return buffer;
        }

        // Оконная функция Ханна — уменьшает "растекание" спектра между бинами
        private static double Window(int i, int n) =>
            0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));

        private static void Recurse(Complex[] a)
        {
            int n = a.Length;
            if (n <= 1) return;

            var even = new Complex[n / 2];
            var odd = new Complex[n / 2];
            for (int i = 0; i < n / 2; i++)
            {
                even[i] = a[2 * i];
                odd[i] = a[2 * i + 1];
            }

            Recurse(even);
            Recurse(odd);

            for (int k = 0; k < n / 2; k++)
            {
                var t = Complex.FromPolarCoordinates(1.0, -2 * Math.PI * k / n) * odd[k];
                a[k] = even[k] + t;
                a[k + n / 2] = even[k] - t;
            }
        }

        /// <summary>
        /// Группирует спектр в barCount полос по логарифмической шкале
        /// и возвращает СЫРЫЕ средние амплитуды (без нормализации к 0..1) —
        /// адаптивную нормализацию (AGC) делает VisualizerService, у которого
        /// есть состояние между кадрами.
        /// </summary>
        public static float[] ToBars(Complex[] spectrum, int barCount)
        {
            int usableBins = spectrum.Length / 2; // вторая половина — зеркало
            var bars = new float[barCount];

            double logMin = Math.Log(1);
            double logMax = Math.Log(usableBins);

            for (int b = 0; b < barCount; b++)
            {
                double loEdge = Math.Exp(logMin + (logMax - logMin) * b / barCount);
                double hiEdge = Math.Exp(logMin + (logMax - logMin) * (b + 1) / barCount);

                int lo = Math.Max(1, (int)loEdge);
                int hi = Math.Min(usableBins, Math.Max(lo + 1, (int)hiEdge));

                double sum = 0;
                for (int i = lo; i < hi; i++)
                    sum += spectrum[i].Magnitude;

                double avg = sum / (hi - lo);
                bars[b] = (float)avg;
            }

            return bars;
        }
    }
}
