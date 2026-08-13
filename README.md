# ASF-RandomProfileAvatar

Плагин для **[ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm)**, который для каждого залогиненного бота через случайные, довольно длинные интервалы (недели-месяцы) меняет аватар на случайное изображение из пула — имитация того, что живой человек изредка меняет фото профиля, а не держит один и тот же аватар (или дефолтный) годами.

У каждого бота свой независимый цикл: раз в случайное число дней в диапазоне `[MinDelayDays; MaxDelayDays]` бот скачивает случайную картинку из пула кандидатов и выставляет её как аватар через тот же механизм, которым Steam сам обрабатывает загрузку аватара в браузере. Один и тот же URL подряд два раза не выбирается (если в пуле есть из чего выбрать).

## Источник изображений

По умолчанию используется пул из ~400 изображений, собранный с **[pfps.gg](https://pfps.gg)** (нейтральные категории — anime/gaming/meme/minimalist/cute и т.п., без NSFW и политики) и упакованный в `avatars.json` рядом с плагином.

> Важно: pfps.gg — сайт с пользовательским контентом, и его условия использования не дают явной лицензии на программное переиспользование изображений (только общая DMCA-оговорка на случай нарушения чьих-то прав). Для приватного использования на своих же ботах риск минимальный, но это не "чистая" по лицензии картинка — имейте это в виду. Если хотите — отключите `UseBundledAvatars` и задайте полностью свой список через `AvatarURLs`.

## Установка

1. Скачайте архив плагина из [Releases](../../releases) и распакуйте в папку `plugins` рядом с ASF (создайте подпапку с именем плагина).
2. Перезапустите ASF.

## Конфигурация

Настройки задаются **глобально**, в `ASF.json`, как дополнительные (нераспознанные ASF) свойства верхнего уровня:

```json
{
	"RandomProfileAvatarEnabled": true,
	"RandomProfileAvatarMinDelayDays": 14,
	"RandomProfileAvatarMaxDelayDays": 60,
	"RandomProfileAvatarUseBundledAvatars": true,
	"RandomProfileAvatarAvatarURLs": []
}
```

| Свойство | Тип | По умолчанию | Описание |
| --- | --- | --- | --- |
| `RandomProfileAvatarEnabled` | `bool` | `false` | Включает/выключает плагин. |
| `RandomProfileAvatarMinDelayDays` | `ushort`, дни | `14` | Нижняя граница случайной паузы между сменами аватара. |
| `RandomProfileAvatarMaxDelayDays` | `ushort`, дни | `60` | Верхняя граница случайной паузы между сменами аватара. |
| `RandomProfileAvatarUseBundledAvatars` | `bool` | `false` | Добавлять ли в пул кандидатов картинки из бандла `avatars.json` (см. выше). |
| `RandomProfileAvatarAvatarURLs` | `string[]` | `[]` | Свой список прямых URL картинок (jpg/jpeg/png/gif), добавляется к бандлу (если он включён) или используется как единственный источник (если бандл выключен). |

Если `MinDelayDays` больше `MaxDelayDays`, значения меняются местами автоматически. Если итоговый пул пуст (бандл выключен и свой список не задан), плагин один раз пишет предупреждение и ничего не делает.

## Сборка

Проект использует **[ASF-PluginTemplate](https://github.com/JustArchiNET/ASF-PluginTemplate)** и собирается вместе с исходниками ASF, подключёнными как git submodule:

```sh
git clone --recurse-submodules https://github.com/buddymurdock/ASF-RandomProfileAvatar.git
cd ASF-RandomProfileAvatar
dotnet build -c Release
```

Если репозиторий уже склонирован без `--recurse-submodules`, подтяните submodule отдельно:

```sh
git submodule update --init --recursive
```

## Лицензия

Apache-2.0, см. [LICENSE.txt](LICENSE.txt). Обратите внимание, что эта лицензия покрывает код плагина, а не изображения в `avatars.json` — см. предупреждение выше.
