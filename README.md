# AudioVisualizerPlayer (UWP / Windows 10 Mobile)

Аудиоплеер с фоновым воспроизведением, лок-скрин контролами (SystemMediaTransportControls)
и живым спектральным визуализатором на переднем плане.

## Архитектура

```
PlaybackService (singleton, App-level)
   ├─ Windows.Media.Playback.MediaPlayer   → собственно воспроизведение, живёт в фоне
   ├─ SystemMediaTransportControls          → лок-скрин / Action Center (метаданные + кнопки)
   └─ VisualizerService (AudioGraph)        → параллельный граф для FFT-анализа
           └─ FFT.cs                        → расчёт полос спектра
MainPage.xaml                                → UI, подписан на VisualizerService.LevelsChanged
```

Ключевая идея: **один и тот же файл открывается дважды** —
1. через `MediaPlayer.Source` для собственно звука (то, что слышит пользователь, играет всегда, в т.ч. в фоне);
2. через `AudioGraph` с `AudioFileInputNode`, подключённый на `AudioFrameOutputNode`,
   чтобы вытаскивать сырые PCM-семплы и считать по ним FFT для визуализатора.

Визуализатор работает только пока `MainPage` на экране (это ограничение платформы,
не архитектуры) — при блокировке экрана пользователь видит системный UI лок-скрина,
а звук продолжает играть через `MediaPlayer` + SMTC.

## Настройка проекта в Visual Studio

1. Создать новый проект **Blank App (Universal Windows)**, Target/Min version — Windows 10, любая сборка ≥ 16299 (для WP10 подойдёт минимальная, доступная в твоём SDK).
2. Установить NuGet-пакет **Win2D.uwp** (`Microsoft.Graphics.Canvas.UI.Xaml`) — для `CanvasControl`, на котором рисуется визуализатор.
3. Скопировать файлы из этого архива в проект, сохранив структуру папок (`Services/`, `Helpers/`).
4. В `Package.appxmanifest`:
   - Добавить capability **Microphone** НЕ нужен (мы не пишем с микрофона, только читаем файл) — но нужен доступ к **Music Library**, если треки берутся из папки "Музыка": `<uap:Capability Name="musicLibrary"/>`.
   - Добавить декларацию **Background Tasks** → тип **Audio**, Entry point указывает на класс из App (см. `App.xaml.cs`, там же регистрация через `SystemMediaTransportControls`, что для UWP MediaPlayer — рекомендованный современный способ и явная `BackgroundTask` уже не обязательна, в отличие от старого `BackgroundMediaPlayer` WP8.1).
5. Собрать и запустить. Плей/пауза на лок-скрине заработает автоматически, т.к. SMTC регистрируется в `PlaybackService`.

## Файлы

| Файл | Назначение |
|---|---|
| `App.xaml.cs` | Инициализация `PlaybackService` как синглтона на уровне приложения |
| `MainPage.xaml` | UI в стиле макета (тёмная тема, акцент cyan, бары визуализатора) |
| `MainPage.xaml.cs` | Код-behind: подписка на события плеера и визуализатора, отрисовка баров |
| `Services/PlaybackService.cs` | Обёртка над `MediaPlayer` + `SystemMediaTransportControls` |
| `Services/VisualizerService.cs` | `AudioGraph` + разбор `AudioFrame` → передача сэмплов в FFT |
| `Helpers/FFT.cs` | Простая реализация Cooley-Tukey FFT + группировка в N полос |

## Известные ограничения

- Визуализатор не отображается на лок-скрине — так работает платформа, у SMTC нет API для кастомного контента.
- `AudioGraph` требует, чтобы декодер поддерживал формат файла (mp3/wav/aac — ок из коробки).
- Для очень длинных треков лучше не грузить оба графа (MediaPlayer + AudioGraph) сразу на старте — см. комментарий "ленивая инициализация" в `VisualizerService.cs`.
