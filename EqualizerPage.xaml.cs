using System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using AudioVisualizerPlayer.Services;

namespace AudioVisualizerPlayer
{
    /// <summary>
    /// 5 вертикальных слайдеров — по одному на полосу графического
    /// эквалайзера (см. PlaybackService.EqualizerFrequencies). Значения
    /// сразу применяются в реальном времени (PlaybackService.SetEqualizerGain,
    /// EqualizerBand.Gain можно менять на лету без пересборки графа) и
    /// сохраняются в LocalSettings, чтобы пережить перезапуск приложения.
    /// </summary>
    public sealed partial class EqualizerPage : Page
    {
        private const double MinGainDb = -15;
        private const double MaxGainDb = 15;

        public EqualizerPage()
        {
            InitializeComponent();
            BuildBandSliders();
        }

        private void BuildBandSliders()
        {
            var frequencies = PlaybackService.EqualizerFrequencies;

            for (int i = 0; i < frequencies.Length; i++)
            {
                BandsContainer.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });

                var column = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                var gainLabel = new TextBlock
                {
                    Text = $"{App.EqualizerGainsDb[i]:+0;-0;0} дБ",
                    Foreground = new SolidColorBrush(Colors.White),
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 4)
                };

                var slider = new Slider
                {
                    Orientation = Orientation.Vertical,
                    Minimum = MinGainDb,
                    Maximum = MaxGainDb,
                    Value = App.EqualizerGainsDb[i],
                    Height = 220,
                    Foreground = (Brush)Application.Current.Resources["AppAccentBrush"]
                };

                int bandIndex = i; // локальная копия для замыкания
                slider.ValueChanged += (s, e) =>
                {
                    App.EqualizerGainsDb[bandIndex] = e.NewValue;
                    App.Playback?.SetEqualizerGain(bandIndex, e.NewValue);
                    App.SaveEqualizerGains();
                    gainLabel.Text = $"{e.NewValue:+0;-0;0} дБ";
                };

                var freqLabel = new TextBlock
                {
                    Text = FormatFrequency(frequencies[i]),
                    Foreground = (Brush)Application.Current.Resources["AppDimBrush"],
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 0)
                };

                column.Children.Add(gainLabel);
                column.Children.Add(slider);
                column.Children.Add(freqLabel);

                Grid.SetColumn(column, i);
                BandsContainer.Children.Add(column);
            }
        }

        private static string FormatFrequency(double hz)
        {
            if (hz >= 1000)
            {
                double khz = hz / 1000.0;
                return (khz == Math.Floor(khz) ? khz.ToString("0") : khz.ToString("0.#")) + " кГц";
            }
            return hz.ToString("0") + " Гц";
        }
    }
}
