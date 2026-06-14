namespace ExileEye;

/// <summary>
/// Minimal UI localization. The interface language follows the chosen game language (a Russian
/// client gets a Russian UI). Keys map to (English, Russian); T() returns the active one.
/// </summary>
public static class Loc
{
    public static string Lang = "en";

    public static string T(string key) =>
        Strings.TryGetValue(key, out var v) ? (Lang == "ru" ? v.Ru : v.En) : key;

    private static readonly Dictionary<string, (string En, string Ru)> Strings = new()
    {
        ["subtitle"] = ("Live poe.ninja prices over your game panel", "Живые цены poe.ninja поверх игровой панели"),
        ["game_language"] = ("Game language", "Язык клиента"),
        ["game_language_tip"] = ("Language your PoE2 client runs in — item names are read in it",
            "Язык клиента PoE2 — на нём читаются названия предметов"),
        ["league"] = ("League", "Лига"),
        ["price_check"] = ("Price check", "Прайс-чек"),
        ["scan_panel"] = ("Scan panel", "Скан панели"),
        ["rebind"] = ("Rebind", "Изменить"),
        ["listings"] = ("Listings", "Рынок"),
        ["listings_tip"] = ("Trade market: Instant buyout = securable (buy now), Online = personal trade.",
            "Рынок: Мгновенный выкуп = securable (купить сразу), Онлайн = личный обмен."),
        ["listed_within"] = ("Listed within", "Выставлено не позже"),
        ["listed_tip"] = ("Only listings posted within this window — fresher ones are likelier buyable now.",
            "Только листинги не старше выбранного — свежие вероятнее купить сейчас."),
        ["account"] = ("Account", "Аккаунт"),
        ["session_placeholder"] = ("POESESSID (optional, for instant buyout)", "POESESSID (опц., для мгновенного выкупа)"),
        ["session_tip"] = ("Session cookie for the instant-buyout market — paste it from pathofexile.com cookies. Stored locally.",
            "Куки сессии для мгновенного выкупа — вставь из cookies pathofexile.com. Хранится локально."),

        ["loading"] = ("Loading", "Загрузка"),
        ["fetching_prices"] = ("Fetching prices from poe.ninja…", "Загружаю цены с poe.ninja…"),
        ["ready"] = ("Ready", "Готово"),
        ["ocr_missing"] = ("OCR model missing", "Нет данных OCR"),
        ["ocr_missing_msg"] = ("Couldn't download the Russian OCR data — check your connection and reselect the language.",
            "Не удалось скачать данные OCR — проверь соединение и переключи язык."),
        ["no_prices"] = ("No prices", "Нет цен"),
        ["no_prices_msg"] = ("poe.ninja returned nothing — check your connection or the selected league.",
            "poe.ninja ничего не вернул — проверь соединение или выбранную лигу."),
        ["logged_in"] = ("Logged in", "Вход выполнен"),
        ["logged_in_msg"] = ("Session captured — instant buyout is available.", "Сессия получена — мгновенный выкуп доступен."),
        ["items_priced"] = ("items priced", "предметов с ценой"),
        ["updated"] = ("updated", "обновлено"),
        ["scans_panel"] = ("scans a panel", "сканирует панель"),
        ["prices_item"] = ("prices the hovered item", "оценивает предмет под курсором"),

        // Price-check window
        ["window_title"] = ("Price check", "Прайс-чек"),
        ["searching"] = ("Searching…", "Поиск…"),
        ["no_data"] = ("No data (rate-limited or offline)", "Нет данных (лимит или офлайн)"),
        ["no_price"] = ("no price", "без цены"),
        ["range"] = ("range", "диапазон"),
        ["reliability"] = ("reliability", "надёжность"),
        ["rel_low"] = ("low", "низкая"),
        ["rel_medium"] = ("medium", "средняя"),
        ["rel_high"] = ("high", "высокая"),
        ["online"] = ("online", "онлайн"),
        ["mods"] = ("mods", "модов"),
        ["base_type"] = ("base type", "по базе"),
        ["scroll_more"] = ("scroll for more", "листай для ещё"),
        ["search"] = ("Search", "Поиск"),
        ["open_browser"] = ("Open in browser", "Открыть в браузере"),
        ["market"] = ("Market", "Рынок"),
        ["valuation_currency"] = ("Valuation currency", "Валюта оценки"),
        ["hdr_price"] = ("Price", "Цена"),
        ["hdr_level"] = ("Lvl", "Ур"),
        ["hdr_quality"] = ("Q", "Кач"),
        ["hdr_seller"] = ("Seller", "Продавец"),
        ["hdr_age"] = ("Age", "Когда"),

        // Tray
        ["tray_show"] = ("Show ExileEye", "Показать ExileEye"),
        ["tray_exit"] = ("Exit", "Выход"),
    };
}
