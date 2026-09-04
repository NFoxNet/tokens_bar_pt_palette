using System.Globalization;

namespace TokensLimitsExtension.Core.Services;

public interface ILocalizationService
{
    event EventHandler? LanguageChanged;

    CultureInfo Culture { get; }

    string GetString(string key, string? fallback = null);

    string Format(string key, params object?[] arguments);
}

public sealed class InvariantLocalizationService : ILocalizationService
{
    public static InvariantLocalizationService Instance { get; } = new();

    public event EventHandler? LanguageChanged
    {
        add { }
        remove { }
    }

    public CultureInfo Culture => CultureInfo.InvariantCulture;

    public string GetString(string key, string? fallback = null)
        => key switch
        {
            "details.limits" => "Лимиты",
            "details.loading" => "Загрузка…",
            "details.plan" => "План",
            "details.primary" => "Основное",
            "details.secondary" => "Дополнительное",
            "status.estimate" => "Оценка: ",
            "status.unavailable" => "Лимиты недоступны",
            "status.remaining" => "{0}% осталось",
            "status.reset" => "через {0}",
            "status.resetPassed" => "сброс уже прошёл",
            "overview.unavailable" => "данные недоступны",
            "time.hours" => "{0}ч",
            "time.days" => "{0}д",
            "time.minutes" => "{0}м",
            "window.weekly" => "Еженедельно",
            _ => fallback ?? key,
        };

    public string Format(string key, params object?[] arguments)
        => string.Format(CultureInfo.InvariantCulture, GetString(key), arguments);
}
