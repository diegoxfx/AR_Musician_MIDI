using System;

public enum AppLanguage { English, Spanish }

/// <summary>Shared language for the Quest interface and its desktop simulation.</summary>
public static class AppText
{
    public static AppLanguage Language { get; private set; } = AppLanguage.English;
    public static event Action Changed;

    public static string Choose(string english, string spanish)
    {
        return Language == AppLanguage.Spanish ? spanish : english;
    }

    public static void SetLanguage(AppLanguage language)
    {
        if (language != AppLanguage.English && language != AppLanguage.Spanish)
            throw new ArgumentOutOfRangeException(nameof(language));
        if (Language == language) return;
        Language = language;
        Changed?.Invoke();
    }
}
